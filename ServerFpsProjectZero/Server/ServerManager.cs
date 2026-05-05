using Newtonsoft.Json;
using ServerFpsProjectZero.Models;
using ServerFpsProjectZero.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ServerFpsProjectZero.Server
{
    public class ServerManager
    {
        private UdpClient udpServer;
        private bool isRunning;
        private Thread receiveThread;
        private int port;
        private Timer heartbeatCleanupTimer;

        // Client connections
        private ConcurrentDictionary<int, ClientConnection> clients;
        private ConcurrentDictionary<string, ClientConnection> tokenToClient;
        private ConcurrentDictionary<string, DateTime> heartbeatMap;

        // Events for different packet types
        public event Action<string, IPEndPoint> OnPacketReceived;
        public event Action<ClientConnection> OnClientConnected;
        public event Action<ClientConnection> OnClientDisconnected;

        // Packet-specific events
        public event Action<string, IPEndPoint> OnMovementPacket;
        public event Action<string, IPEndPoint> OnShootPacket;
        public event Action<string, IPEndPoint> OnAbilityPacket;
        public event Action<string, IPEndPoint> OnPlayerStatePacket;
        public event Action<string, IPEndPoint> OnPlayerDeathPacket;
        public event Action<string, IPEndPoint> OnPlayerRespawnPacket;
        public event Action<string, IPEndPoint> OnWeaponPickupPacket;
        public event Action<string, IPEndPoint> OnChatMessagePacket;
        public event Action<string, IPEndPoint> OnPingMarkerPacket;
        public event Action<string, IPEndPoint> OnLoginPacket;
        public event Action<string, IPEndPoint> OnRegisterPacket;
        public event Action<string, IPEndPoint> OnHeartbeatPacket;
        public event Action<string, IPEndPoint> OnLogoutPacket;
        public event Action<string, IPEndPoint> OnGetProfilePacket;
        public event Action<string, IPEndPoint> OnJoinQueuePacket;
        public event Action<string, IPEndPoint> OnLeaveQueuePacket;
        public event Action<string, IPEndPoint> OnGetQueueStatusPacket;
        public event Action<int, int, int, bool, int, int> OnPlayerGameStatsUpdate;


        public ServerManager(int port = 7777)
        {
            this.port = port;
            clients = new ConcurrentDictionary<int, ClientConnection>();
            tokenToClient = new ConcurrentDictionary<string, ClientConnection>();
            heartbeatMap = new ConcurrentDictionary<string, DateTime>();
        }

        public void Start()
        {
            try
            {
                udpServer = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                isRunning = true;

                receiveThread = new Thread(ReceivePackets);
                receiveThread.IsBackground = true;
                receiveThread.Start();

                heartbeatCleanupTimer = new Timer(CleanupHeartbeats, null, 30000, 30000);

                Console.WriteLine($"[ServerManager] Started on UDP port {port}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ServerManager] Failed to start: {ex.Message}");
                throw;
            }
        }

        public void Stop()
        {
            isRunning = false;

            heartbeatCleanupTimer?.Dispose();

            // Disconnect all clients
            foreach (var client in clients.Values)
            {
                client.IsConnected = false;
                OnClientDisconnected?.Invoke(client);
            }

            clients.Clear();
            tokenToClient.Clear();
            heartbeatMap.Clear();

            receiveThread?.Join(1000);
            udpServer?.Close();

            Console.WriteLine("[ServerManager] Stopped");
        }

        private void ReceivePackets()
        {
            while (isRunning)
            {
                try
                {
                    IPEndPoint remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = udpServer.Receive(ref remoteEndpoint);
                    string jsonData = Encoding.UTF8.GetString(data);

                    ThreadPool.QueueUserWorkItem(_ => ProcessPacket(jsonData, remoteEndpoint));
                }
                catch (Exception ex)
                {
                    if (isRunning)
                        Console.WriteLine($"[ServerManager] Receive error: {ex.Message}");
                }
            }
        }

        private void ProcessPacket(string jsonData, IPEndPoint remoteEndpoint)
        {
            try
            {
                OnPacketReceived?.Invoke(jsonData, remoteEndpoint);

                var packet = JsonConvert.DeserializeObject<Dictionary<string, object>>(jsonData);

                if (!packet.ContainsKey("type"))
                {
                    SendErrorResponse(remoteEndpoint, "Invalid packet format", 400);
                    return;
                }

                string type = packet["type"].ToString();

                switch (type)
                {
                    case "login":
                        OnLoginPacket?.Invoke(jsonData, remoteEndpoint);
                        break;
                    case "register":
                        OnRegisterPacket?.Invoke(jsonData, remoteEndpoint);
                        break;
                    case "heartbeat":
                        OnHeartbeatPacket?.Invoke(jsonData, remoteEndpoint);
                        break;
                    case "logout":
                        OnLogoutPacket?.Invoke(jsonData, remoteEndpoint);
                        break;
                    case "get_profile":
                        OnGetProfilePacket?.Invoke(jsonData, remoteEndpoint);
                        break;
                    case "join_queue":
                        OnJoinQueuePacket?.Invoke(jsonData, remoteEndpoint);
                        break;
                    case "leave_queue":
                        OnLeaveQueuePacket?.Invoke(jsonData, remoteEndpoint);
                        break;
                    case "get_queue_status":
                        OnGetQueueStatusPacket?.Invoke(jsonData, remoteEndpoint);
                        break;
                    case "movement":
                        OnMovementPacket?.Invoke(jsonData, remoteEndpoint);
                        break;
                    case "shoot":
                        OnShootPacket?.Invoke(jsonData, remoteEndpoint);
                        break;
                    case "use_ability":
                        OnAbilityPacket?.Invoke(jsonData, remoteEndpoint);
                        break;
                    case "player_state":
                        OnPlayerStatePacket?.Invoke(jsonData, remoteEndpoint);
                        break;
                    case "player_death":
                        OnPlayerDeathPacket?.Invoke(jsonData, remoteEndpoint);
                        break;
                    case "player_respawn":
                        OnPlayerRespawnPacket?.Invoke(jsonData, remoteEndpoint);
                        break;
                    case "weapon_pickup":
                        OnWeaponPickupPacket?.Invoke(jsonData, remoteEndpoint);
                        break;
                    case "chat_message":
                        OnChatMessagePacket?.Invoke(jsonData, remoteEndpoint);
                        break;
                    case "ping_marker":
                        OnPingMarkerPacket?.Invoke(jsonData, remoteEndpoint);
                        break;
                    default:
                        Console.WriteLine($"[ServerManager] Unknown packet type: {type}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ServerManager] Packet processing error: {ex.Message}");
            }
        }

        private void CleanupHeartbeats(object state)
        {
            var now = DateTime.UtcNow;
            var staleTokens = new List<string>();

            foreach (var kvp in heartbeatMap)
            {
                if ((now - kvp.Value).TotalSeconds > 30)
                {
                    staleTokens.Add(kvp.Key);
                }
            }

            foreach (var token in staleTokens)
            {
                if (tokenToClient.TryGetValue(token, out ClientConnection client))
                {
                    Console.WriteLine($"[ServerManager] Client {client.Username} disconnected due to heartbeat timeout");
                    DisconnectClient(client);
                }
            }
        }

        #region Client Management

        public ClientConnection CreateClient(int playerId, string username, IPEndPoint endpoint)
        {
            var client = new ClientConnection
            {
                PlayerId = playerId,
                Username = username,
                Endpoint = endpoint,
                SessionToken = Guid.NewGuid().ToString(),
                LastHeartbeat = DateTime.UtcNow,
                IsConnected = true
            };

            clients[playerId] = client;
            tokenToClient[client.SessionToken] = client;
            heartbeatMap[client.SessionToken] = DateTime.UtcNow;

            OnClientConnected?.Invoke(client);
            return client;
        }

        public void DisconnectClient(ClientConnection client)
        {
            if (client == null) return;

            tokenToClient.TryRemove(client.SessionToken, out _);
            clients.TryRemove(client.PlayerId, out _);
            heartbeatMap.TryRemove(client.SessionToken, out _);

            client.IsConnected = false;
            client.InQueue = false;
            client.InGame = false;

            OnClientDisconnected?.Invoke(client);
        }

        public void UpdateHeartbeat(ClientConnection client)
        {
            if (client == null) return;

            client.LastHeartbeat = DateTime.UtcNow;
            heartbeatMap[client.SessionToken] = DateTime.UtcNow;
        }
        public void UpdatePlayerGameStats(int playerId, int kills, int deaths, bool isWinner, int goldReward, int xpReward)
        {
            // This will be handled by LoginManager
            // You can either add an event or pass a reference to LoginManager
            OnPlayerGameStatsUpdate?.Invoke(playerId, kills, deaths, isWinner, goldReward, xpReward);
        }

        public ClientConnection GetClientByToken(string token)
        {
            tokenToClient.TryGetValue(token, out ClientConnection client);
            return client;
        }

        public ClientConnection GetClientById(int playerId)
        {
            clients.TryGetValue(playerId, out ClientConnection client);
            return client;
        }

        public ClientConnection GetClientByEndpoint(IPEndPoint endpoint)
        {
            return clients.Values.FirstOrDefault(c => c.Endpoint?.Equals(endpoint) == true);
        }

        public List<ClientConnection> GetAllClients()
        {
            return clients.Values.ToList();
        }

        #endregion

        #region Send Methods

        public void SendPacket(object packet, IPEndPoint endpoint)
        {
            try
            {
                string jsonData = JsonConvert.SerializeObject(packet);
                byte[] data = Encoding.UTF8.GetBytes(jsonData);
                udpServer.Send(data, data.Length, endpoint);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ServerManager] Send error: {ex.Message}");
            }
        }

        public void SendPacket(object packet, ClientConnection client)
        {
            if (client?.Endpoint != null)
            {
                SendPacket(packet, client.Endpoint);
            }
        }

        public void SendToAll(object packet, List<ClientConnection> clientList = null)
        {
            var targetClients = clientList ?? clients.Values.ToList();
            foreach (var client in targetClients)
            {
                if (client.IsConnected && client.Endpoint != null)
                {
                    SendPacket(packet, client);
                }
            }
        }

        public void SendErrorResponse(IPEndPoint endpoint, string message, int errorCode)
        {
            var response = new
            {
                type = "error",
                message = message,
                errorCode = errorCode,
                timestamp = DateTime.UtcNow
            };

            SendPacket(response, endpoint);
        }

        #endregion

        public void PrintActiveClients()
        {
            Console.WriteLine($"\n[ServerManager] Active Clients: {clients.Count}");
            foreach (var client in clients.Values)
            {
                string queueStatus = client.InQueue ? " (In Queue)" : "";
                string gameStatus = client.InGame ? " (In Game)" : "";
                Console.WriteLine($"  - {client.Username} (ID: {client.PlayerId}){queueStatus}{gameStatus}");
            }
        }
    }

    public class ClientConnection
    {
        public int PlayerId { get; set; }
        public string Username { get; set; }
        public IPEndPoint Endpoint { get; set; }
        public string SessionToken { get; set; }
        public DateTime LastHeartbeat { get; set; }
        public bool IsConnected { get; set; }
        public bool InQueue { get; set; }
        public DateTime JoinedQueueAt { get; set; }
        public bool InGame { get; set; }
        public int CurrentGameId { get; set; }
        public int TeamId { get; set; }
        public bool IsDead { get; set; } = false;
    }
}