﻿using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Multiplayer.Common
{
    public class MultiplayerServer
    {
        static MultiplayerServer()
        {
            MpConnectionState.SetImplementation(ConnectionStateEnum.ServerSteam, typeof(ServerSteamState));
            MpConnectionState.SetImplementation(ConnectionStateEnum.ServerJoining, typeof(ServerJoiningState));
            MpConnectionState.SetImplementation(ConnectionStateEnum.ServerPlaying, typeof(ServerPlayingState));
        }

        public static MultiplayerServer instance;

        public const int DefaultPort = 30502;

        public int coopFactionId;
        public byte[] savedGame; // Compressed game save
        public Dictionary<int, byte[]> mapData = new Dictionary<int, byte[]>(); // Map id to compressed map data

        public Dictionary<int, List<byte[]>> mapCmds = new Dictionary<int, List<byte[]>>(); // Map id to serialized cmds list
        public Dictionary<int, List<byte[]>> tmpMapCmds;

        public Dictionary<string, int> playerFactions = new Dictionary<string, int>(); // Username to faction id

        public List<ServerPlayer> players = new List<ServerPlayer>();
        public IEnumerable<ServerPlayer> PlayingPlayers => players.Where(p => p.IsPlaying);

        public string hostUsername;
        public int timer;
        public bool paused;
        public ActionQueue queue = new ActionQueue();
        public ServerSettings settings;

        public volatile bool running = true;

        private Dictionary<string, ChatCmdHandler> chatCmds = new Dictionary<string, ChatCmdHandler>();

        public int keepAliveId;
        public Stopwatch lastKeepAlive = Stopwatch.StartNew();

        private NetManager netManager;
        private NetManager arbiter;

        public int nextUniqueId;

        public int LocalPort => netManager.LocalPort;
        public int ArbiterPort => arbiter.LocalPort;
        public bool ArbiterPlaying => PlayingPlayers.Any(p => p.IsArbiter && p.status == PlayerStatus.Playing);

        public MultiplayerServer(ServerSettings settings)
        {
            this.settings = settings;

            RegisterChatCmd("autosave", new ChatCmdAutosave());
            RegisterChatCmd("kick", new ChatCmdKick());

            if (settings.direct || settings.lan)
                netManager = new NetManager(new MpNetListener(this, false));
        }

        public void StartListening()
        {
            netManager?.Start(settings.address, IPAddress.IPv6Any, settings.port);
        }

        public void SetupArbiterConnection()
        {
            arbiter = new NetManager(new MpNetListener(this, true));
            arbiter.Start(IPAddress.Loopback, IPAddress.IPv6Any, 0);
        }

        public void Run()
        {
            Stopwatch time = Stopwatch.StartNew();
            double lag = 0;
            double timePerTick = 1000.0 / 60.0;

            while (running)
            {
                double elapsed = time.ElapsedMillisDouble();
                time.Restart();
                lag += elapsed;

                while (lag >= timePerTick)
                {
                    TickNet();
                    if (!paused)
                        Tick();
                    lag -= timePerTick;
                }

                Thread.Sleep(10);
            }

            Stop();
        }

        private void Stop()
        {
            SendToAll(Packets.Server_DisconnectReason, new[] { "MpServerClosed" });

            if (netManager != null)
            {
                foreach (var peer in netManager.GetPeers(ConnectionState.Connected))
                    peer.Flush();
                netManager.Stop();
            }

            arbiter?.Stop();

            instance = null;
        }

        private int lastAutosave;

        public void TickNet()
        {
            netManager?.PollEvents();
            arbiter?.PollEvents();
            queue.RunQueue();
        }

        public void Tick()
        {
            if (timer % 3 == 0)
                SendToAll(Packets.Server_TimeControl, new object[] { timer });

            if (settings.lan && timer % 60 == 0)
                netManager.SendDiscoveryRequest(Encoding.UTF8.GetBytes("mp-server"), 5100);

            timer++;

            if (timer % 180 == 0)
            {
                SendLatencies();

                keepAliveId++;
                SendToAll(Packets.Server_KeepAlive, new object[] { keepAliveId });
                lastKeepAlive.Restart();
            }

            if (lastAutosave >= settings.autosaveInterval * 60 * 60)
            {
                DoAutosave();
                lastAutosave = 0;
            }

            lastAutosave++;
        }

        private void SendLatencies()
        {
            SendToAll(Packets.Server_PlayerList, new object[] { (byte)PlayerListAction.Latencies, PlayingPlayers.Select(p => p.Latency).ToArray() });
        }

        public bool DoAutosave()
        {
            if (tmpMapCmds != null)
                return false;

            SendCommand(CommandType.Autosave, ScheduledCommand.NoFaction, ScheduledCommand.Global, new byte[0]);
            tmpMapCmds = new Dictionary<int, List<byte[]>>();

            return true;
        }

        public void Enqueue(Action action)
        {
            queue.Enqueue(action);
        }

        private int nextPlayerId;

        public ServerPlayer OnConnected(IConnection conn)
        {
            if (conn.serverPlayer != null)
                MpLog.Error($"Connection {conn} already has a server player");

            conn.serverPlayer = new ServerPlayer(nextPlayerId++, conn);
            players.Add(conn.serverPlayer);
            MpLog.Log($"New connection: {conn}");

            return conn.serverPlayer;
        }

        public void OnDisconnected(IConnection conn)
        {
            if (conn.State == ConnectionStateEnum.Disconnected) return;

            ServerPlayer player = conn.serverPlayer;
            players.Remove(player);

            if (player.IsPlaying)
            {
                if (!players.Any(p => p.FactionId == player.FactionId))
                {
                    byte[] data = ByteWriter.GetBytes(player.FactionId);
                    SendCommand(CommandType.FactionOffline, ScheduledCommand.NoFaction, ScheduledCommand.Global, data);
                }

                SendNotification("MpPlayerDisconnected", conn.username);
                SendChat($"{conn.username} has left.");

                SendToAll(Packets.Server_PlayerList, new object[] { (byte)PlayerListAction.Remove, player.id });
            }

            conn.State = ConnectionStateEnum.Disconnected;

            MpLog.Log($"Disconnected: {conn}");
        }

        public void SendToAll(Packets id)
        {
            SendToAll(id, new byte[0]);
        }

        public void SendToAll(Packets id, object[] data)
        {
            SendToAll(id, ByteWriter.GetBytes(data));
        }

        public void SendToAll(Packets id, byte[] data, bool reliable = true)
        {
            foreach (ServerPlayer player in PlayingPlayers)
                player.conn.Send(id, data, reliable);
        }

        public ServerPlayer FindPlayer(Predicate<ServerPlayer> match)
        {
            return players.Find(match);
        }

        public ServerPlayer GetPlayer(string username)
        {
            return FindPlayer(player => player.Username == username);
        }

        public IdBlock NextIdBlock(int blockSize = 30000)
        {
            int blockStart = nextUniqueId;
            nextUniqueId = nextUniqueId + blockSize;
            MpLog.Log($"New id block {blockStart} of size {blockSize}");

            return new IdBlock(blockStart, blockSize);
        }

        public void SendCommand(CommandType cmd, int factionId, int mapId, byte[] data, string sourcePlayer = null)
        {
            byte[] toSave = new ScheduledCommand(cmd, timer, factionId, mapId, data).Serialize();

            // todo cull target players if not global
            mapCmds.GetOrAddNew(mapId).Add(toSave);
            tmpMapCmds?.GetOrAddNew(mapId).Add(toSave);

            byte[] toSend = toSave.Append(new byte[] { 0 });
            byte[] toSendSource = toSave.Append(new byte[] { 1 });

            foreach (var player in PlayingPlayers)
            {
                player.conn.Send(
                    Packets.Server_Command,
                    sourcePlayer == player.Username ? toSendSource : toSend
                );
            }
        }

        public void SendChat(string msg)
        {
            SendToAll(Packets.Server_Chat, new[] { msg });
        }

        public void SendNotification(string key, params string[] args)
        {
            SendToAll(Packets.Server_Notification, new object[] { key, args });
        }

        public void RegisterChatCmd(string cmdName, ChatCmdHandler handler)
        {
            chatCmds[cmdName] = handler;
        }

        public ChatCmdHandler GetCmdHandler(string cmdName)
        {
            chatCmds.TryGetValue(cmdName, out ChatCmdHandler handler);
            return handler;
        }
    }

    public class MpNetListener : INetEventListener
    {
        private MultiplayerServer server;
        private bool arbiter;

        public MpNetListener(MultiplayerServer server, bool arbiter)
        {
            this.server = server;
            this.arbiter = arbiter;
        }

        public void OnConnectionRequest(ConnectionRequest req)
        {
            if (!arbiter && server.settings.maxPlayers > 0 && server.players.Count(p => !p.IsArbiter) >= server.settings.maxPlayers)
            {
                var writer = new ByteWriter();
                writer.WriteString("Server is full");
                req.Reject(writer.GetArray());
                return;
            }

            req.Accept();
        }

        public void OnPeerConnected(NetPeer peer)
        {
            IConnection conn = new MpNetConnection(peer);
            conn.State = ConnectionStateEnum.ServerJoining;
            peer.Tag = conn;

            var player = server.OnConnected(conn);
            if (arbiter)
                player.type = PlayerType.Arbiter;
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            IConnection conn = peer.GetConnection();
            server.OnDisconnected(conn);
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
            peer.GetConnection().Latency = latency;
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, DeliveryMethod method)
        {
            byte[] data = reader.GetRemainingBytes();
            peer.GetConnection().serverPlayer.HandleReceive(data, method == DeliveryMethod.ReliableOrdered);
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
        }

        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
        }
    }

    public class ServerSettings
    {
        public string gameName;
        public IPAddress address;
        public int port;
        public int maxPlayers = 8;
        public int autosaveInterval = 8;
        public bool direct;
        public bool lan;
        public bool steam;
        public bool arbiter;
    }

    public class ServerPlayer
    {
        public int id;
        public IConnection conn;
        public PlayerType type;
        public PlayerStatus status;

        public string Username => conn.username;
        public int Latency => conn.Latency;
        public int FactionId => MultiplayerServer.instance.playerFactions[Username];
        public bool IsPlaying => conn.State == ConnectionStateEnum.ServerPlaying;
        public bool IsHost => MultiplayerServer.instance.hostUsername == Username;
        public bool IsArbiter => type == PlayerType.Arbiter;

        public MultiplayerServer Server => MultiplayerServer.instance;

        public ServerPlayer(int id, IConnection connection)
        {
            this.id = id;
            conn = connection;
        }

        public void HandleReceive(byte[] data, bool reliable)
        {
            try
            {
                conn.HandleReceive(data, reliable);
            }
            catch (Exception e)
            {
                MpLog.Error($"Error handling packet by {conn}: {e}");
                Disconnect($"Connection error: {e.GetType().Name}");
            }
        }

        public void Disconnect(string reasonKey)
        {
            conn.Send(Packets.Server_DisconnectReason, reasonKey);

            if (conn is MpNetConnection netConn)
                netConn.peer.Flush();

            conn.Close();
            Server.OnDisconnected(conn);
        }

        public void SendChat(string msg)
        {
            SendPacket(Packets.Server_Chat, new[] { msg });
        }

        public void SendPacket(Packets packet, byte[] data)
        {
            conn.Send(packet, data);
        }

        public void SendPacket(Packets packet, object[] data)
        {
            conn.Send(packet, data);
        }

        public void SendPlayerList()
        {
            var writer = new ByteWriter();

            writer.WriteByte((byte)PlayerListAction.List);
            writer.WriteInt32(Server.PlayingPlayers.Count());

            foreach (var player in Server.PlayingPlayers)
                writer.WriteRaw(player.SerializePlayerInfo());

            conn.Send(Packets.Server_PlayerList, writer.GetArray());
        }

        public byte[] SerializePlayerInfo()
        {
            var writer = new ByteWriter();

            writer.WriteInt32(id);
            writer.WriteString(Username);
            writer.WriteInt32(Latency);
            writer.WriteByte((byte)type);
            writer.WriteByte((byte)status);

            return writer.GetArray();
        }

        public void UpdateStatus(PlayerStatus status)
        {
            if (this.status == status) return;
            this.status = status;
            Server.SendToAll(Packets.Server_PlayerList, new object[] { (byte)PlayerListAction.Status, id, (byte)status });
        }
    }

    public enum PlayerStatus : byte
    {
        Simulating,
        Playing,
        Desynced
    }

    public enum PlayerType : byte
    {
        Normal,
        Steam,
        Arbiter
    }

    public class IdBlock
    {
        public int blockStart;
        public int blockSize;
        public int mapId = -1;

        public int current;
        public bool overflowHandled;

        public IdBlock(int blockStart, int blockSize, int mapId = -1)
        {
            this.blockStart = blockStart;
            this.blockSize = blockSize;
            this.mapId = mapId;
        }

        public int NextId()
        {
            // Overflows should be handled by the caller
            current++;
            return blockStart + current;
        }

        public byte[] Serialize()
        {
            ByteWriter writer = new ByteWriter();
            writer.WriteInt32(blockStart);
            writer.WriteInt32(blockSize);
            writer.WriteInt32(mapId);
            writer.WriteInt32(current);

            return writer.GetArray();
        }

        public static IdBlock Deserialize(ByteReader data)
        {
            IdBlock block = new IdBlock(data.ReadInt32(), data.ReadInt32(), data.ReadInt32());
            block.current = data.ReadInt32();
            return block;
        }
    }

    public class ActionQueue
    {
        private Queue<Action> queue = new Queue<Action>();
        private Queue<Action> tempQueue = new Queue<Action>();

        public void RunQueue()
        {
            lock (queue)
            {
                if (queue.Count > 0)
                {
                    foreach (Action a in queue)
                        tempQueue.Enqueue(a);
                    queue.Clear();
                }
            }

            try
            {
                while (tempQueue.Count > 0)
                    tempQueue.Dequeue().Invoke();
            }
            catch (Exception e)
            {
                MpLog.Log($"Exception while executing action queue: {e}");
            }
        }

        public void Enqueue(Action action)
        {
            lock (queue)
                queue.Enqueue(action);
        }
    }
}
