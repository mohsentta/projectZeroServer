using ServerFpsProjectZero.Matchmaking;
using ServerFpsProjectZero.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ServerFpsProjectZero.Networking
{
    public class LoginServer
    {
        private UdpClient udpServer;
        private IPEndPoint serverEndpoint;
        private bool isRunning;
        private Thread receiveThread;

        // Store connected players
        private ConcurrentDictionary<int, Player> connectedPlayers;
        private ConcurrentDictionary<string, Player> tokenToPlayer;
        private ConcurrentDictionary<string, DateTime> rateLimitMap;
        private ConcurrentDictionary<string, DateTime> heartbeatMap;

        // Mock database storage
        private Dictionary<int, MockPlayerData> mockPlayerDatabase;
        private Dictionary<string, int> mockUsernameToId;
        private Dictionary<string, int> mockEmailToId;
        private int nextPlayerId;

        // Matchmaking
        private MatchmakingQueue matchmakingQueue;

        // Server configuration
        private int udpPort;
        private Timer heartbeatCleanupTimer;

        // Events
        public event Action<Player> OnPlayerLoggedIn;
        public event Action<Player> OnPlayerDisconnected;
        public event Action<string, IPEndPoint> OnFailedLogin;

        public LoginServer(int port = 7777)
        {
            udpPort = port;
            connectedPlayers = new ConcurrentDictionary<int, Player>();
            tokenToPlayer = new ConcurrentDictionary<string, Player>();
            rateLimitMap = new ConcurrentDictionary<string, DateTime>();
            heartbeatMap = new ConcurrentDictionary<string, DateTime>();

            // Initialize mock database
            mockPlayerDatabase = new Dictionary<int, MockPlayerData>();
            mockUsernameToId = new Dictionary<string, int>();
            mockEmailToId = new Dictionary<string, int>();
            nextPlayerId = 1000;

            // Initialize matchmaking
            matchmakingQueue = new MatchmakingQueue();

            // Subscribe to matchmaking events
            matchmakingQueue.OnGameMatched += HandleGameMatched;
            matchmakingQueue.OnPlayerEnteredQueue += HandlePlayerEnteredQueue;
            matchmakingQueue.OnPlayerLeftQueue += HandlePlayerLeftQueue;
            matchmakingQueue.OnPlayerTimedOut += HandlePlayerTimedOut;
            matchmakingQueue.OnQueueStatusChanged += HandleQueueStatusChanged;

            // Create test accounts
            InitializeMockData();
        }

        private void InitializeMockData()
        {
            // Create test players with varying MMR for balanced matchmaking
            CreateMockPlayer("player1", "player1@test.com", "password123", 1200, 500, 5);
            CreateMockPlayer("player2", "player2@test.com", "password123", 1350, 750, 8);
            CreateMockPlayer("player3", "player3@test.com", "password123", 1100, 300, 3);
            CreateMockPlayer("proplayer", "pro@test.com", "pro123", 1850, 2500, 15);
            CreateMockPlayer("newbie", "newbie@test.com", "test123", 1000, 100, 1);
            CreateMockPlayer("veteran", "vet@test.com", "vet123", 1500, 1200, 10);
            CreateMockPlayer("casual", "casual@test.com", "casual123", 1150, 400, 4);
            CreateMockPlayer("tryhard", "tryhard@test.com", "try123", 1700, 1800, 12);
            CreateMockPlayer("sniper", "sniper@test.com", "sniper123", 1400, 900, 7);
            CreateMockPlayer("shotgun", "shotgun@test.com", "shotgun123", 1250, 600, 6);

            Console.WriteLine("[Database] Mock database initialized with 10 test players");
        }

        private void CreateMockPlayer(string username, string email, string password, int mmr, int gold, int level)
        {
            string salt = GenerateSalt();
            string passwordHash = HashPassword(password, salt);

            var mockData = new MockPlayerData
            {
                PlayerId = nextPlayerId,
                Username = username,
                Email = email,
                PasswordHash = passwordHash,
                Salt = salt,
                Level = level,
                Experience = level * 100,
                Gold = gold,
                MMR = mmr,
                Rank = CalculateRankFromMMR(mmr),
                TotalMatches = level * 10,
                TotalWins = level * 6,
                TotalLosses = level * 4,
                TotalKills = level * 50,
                TotalDeaths = level * 30,
                IsBanned = false,
                BanExpiry = null,
                CreatedAt = DateTime.UtcNow.AddDays(-level * 7),
                LastLogin = DateTime.UtcNow.AddDays(-1),
                OwnedWeapons = new List<int> { 1, 2, 3, 4, 5, 6 },
                OwnedSkins = new List<int> { 1, 2, 3 },
                OwnedGrenades = new List<int> { 4, 5 },
                PrimaryWeaponId = 1,
                SecondaryWeaponId = 2,
                MeleeWeaponId = 3,
                GrenadeId = 4,
                PlayerSkinId = 1,
                WeaponSkinId = 1
            };

            mockPlayerDatabase[nextPlayerId] = mockData;
            mockUsernameToId[username.ToLower()] = nextPlayerId;
            mockEmailToId[email.ToLower()] = nextPlayerId;
            nextPlayerId++;
        }

        private int CalculateRankFromMMR(int mmr)
        {
            if (mmr < 1000) return 1;
            if (mmr < 1150) return 2;
            if (mmr < 1300) return 3;
            if (mmr < 1450) return 4;
            if (mmr < 1600) return 5;
            if (mmr < 1750) return 6;
            if (mmr < 1900) return 7;
            if (mmr < 2050) return 8;
            if (mmr < 2200) return 9;
            return 10;
        }

        private void HandleGameMatched(List<Player> players, GameSession gameSession)
        {
            Console.WriteLine($"\n[LoginServer] Game {gameSession.GameId} starting with {players.Count} players");

            // Notify each player about game start
            foreach (var player in players)
            {
                if (player.IsConnected && player.ClientEndpoint != null)
                {
                    var gameStartPacket = new
                    {
                        type = "game_start",
                        gameId = gameSession.GameId,
                        teamId = player.TeamId,
                        players = players.Select(p => new
                        {
                            p.PlayerId,
                            p.Username,
                            p.Level,
                            p.MMR,
                            p.Rank,
                            TeamId = p.TeamId,
                            Loadout = p.Loadout
                        }).ToList(),
                        timestamp = DateTime.UtcNow
                    };

                    SendPacket(player.ClientEndpoint, gameStartPacket);
                }
            }
        }

        private void HandlePlayerEnteredQueue(QueuedPlayer queuedPlayer)
        {
            var player = queuedPlayer.Player;
            if (player.IsConnected && player.ClientEndpoint != null)
            {
                SendQueueStatusUpdate(player);
                Console.WriteLine($"[Matchmaking] Player {player.Username} entered queue. Position: {GetQueuePosition(player)}");
            }
        }

        private void HandlePlayerLeftQueue(QueuedPlayer queuedPlayer)
        {
            var player = queuedPlayer.Player;
            if (player.IsConnected && player.ClientEndpoint != null)
            {
                SendQueueStatusUpdate(player);
                Console.WriteLine($"[Matchmaking] Player {player.Username} left queue");
            }
        }

        private void HandlePlayerTimedOut(QueuedPlayer queuedPlayer)
        {
            var player = queuedPlayer.Player;
            if (player.IsConnected && player.ClientEndpoint != null)
            {
                var timeoutPacket = new
                {
                    type = "queue_timeout",
                    message = "Matchmaking queue timed out. Please try again.",
                    timestamp = DateTime.UtcNow
                };
                SendPacket(player.ClientEndpoint, timeoutPacket);
                Console.WriteLine($"[Matchmaking] Player {player.Username} timed out of queue");
            }
        }

        private void HandleQueueStatusChanged(int queueSize)
        {
            // Broadcast queue status to all players in queue
            foreach (var player in GetPlayersInQueue())
            {
                if (player.IsConnected && player.ClientEndpoint != null)
                {
                    SendQueueStatusUpdate(player);
                }
            }
        }

        private List<Player> GetPlayersInQueue()
        {
            var queuePlayers = matchmakingQueue.GetQueueStatus();
            return queuePlayers.Select(qp => qp.Player).ToList();
        }

        private void SendQueueStatusUpdate(Player player)
        {
            if (player.IsConnected && player.ClientEndpoint != null)
            {
                var statusPacket = new
                {
                    type = "queue_status",
                    queueSize = matchmakingQueue.QueueSize,
                    position = GetQueuePosition(player),
                    estimatedWaitTime = EstimateWaitTime(),
                    playersNeeded = Math.Max(0, MatchmakingQueue.PLAYERS_PER_GAME - matchmakingQueue.QueueSize),
                    timestamp = DateTime.UtcNow
                };

                SendPacket(player.ClientEndpoint, statusPacket);
            }
        }

        private int GetQueuePosition(Player player)
        {
            var queue = matchmakingQueue.GetQueueStatus();
            for (int i = 0; i < queue.Count; i++)
            {
                if (queue[i].Player.PlayerId == player.PlayerId)
                    return i + 1;
            }
            return -1;
        }

        private int EstimateWaitTime()
        {
            int queueSize = matchmakingQueue.QueueSize;
            int playersNeeded = Math.Max(0, MatchmakingQueue.PLAYERS_PER_GAME - queueSize);
            // Rough estimate: 30 seconds per missing player (assuming 2 players join per minute)
            return (playersNeeded / 2) * 30;
        }

        public async Task Start()
        {
            try
            {
                udpServer = new UdpClient(udpPort);
                serverEndpoint = new IPEndPoint(IPAddress.Any, udpPort);
                isRunning = true;

                receiveThread = new Thread(ReceivePackets);
                receiveThread.IsBackground = true;
                receiveThread.Start();

                // Start heartbeat cleanup timer (runs every 30 seconds)
                heartbeatCleanupTimer = new Timer(CleanupHeartbeats, null, 30000, 30000);

                Console.WriteLine($"[LoginServer] Started on UDP port {udpPort}");
                Console.WriteLine($"[LoginServer] Mock database ready with {mockPlayerDatabase.Count} players");
                Console.WriteLine($"[LoginServer] Matchmaking queue active (requires {MatchmakingQueue.PLAYERS_PER_GAME} players per game)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LoginServer] Failed to start: {ex.Message}");
                throw;
            }
        }

        private void ReceivePackets()
        {
            while (isRunning)
            {
                try
                {
                    IPEndPoint clientEndpoint = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = udpServer.Receive(ref clientEndpoint);
                    string jsonData = Encoding.UTF8.GetString(data);

                    ThreadPool.QueueUserWorkItem(_ => ProcessPacket(jsonData, clientEndpoint));
                }
                catch (Exception ex)
                {
                    if (isRunning)
                        Console.WriteLine($"[LoginServer] Receive error: {ex.Message}");
                }
            }
        }

        private async void ProcessPacket(string jsonData, IPEndPoint clientEndpoint)
        {
            try
            {
                //if (!IsRateLimitValid(clientEndpoint))
                //{
                //    SendErrorResponse(clientEndpoint, "Rate limit exceeded. Please try again later.", 429);
                //    return;
                //}

                var packet = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonData);
                if (!packet.ContainsKey("type"))
                {
                    SendErrorResponse(clientEndpoint, "Invalid packet format", 400);
                    return;
                }

                string type = packet["type"].ToString();

                switch (type)
                {
                    case "login":
                        await HandleLogin(jsonData, clientEndpoint);
                        break;
                    case "register":
                        await HandleRegister(jsonData, clientEndpoint);
                        break;
                    case "heartbeat":
                        await HandleHeartbeat(jsonData, clientEndpoint);
                        break;
                    case "logout":
                        await HandleLogout(jsonData, clientEndpoint);
                        break;
                    case "get_profile":
                        await HandleGetProfile(jsonData, clientEndpoint);
                        break;
                    case "join_queue":
                        await HandleJoinQueue(jsonData, clientEndpoint);
                        break;
                    case "leave_queue":
                        await HandleLeaveQueue(jsonData, clientEndpoint);
                        break;
                    case "get_queue_status":
                        await HandleGetQueueStatus(jsonData, clientEndpoint);
                        break;
                    default:
                        SendErrorResponse(clientEndpoint, $"Unknown packet type: {type}", 400);
                        break;
                }
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"[LoginServer] JSON error: {ex.Message}");
                SendErrorResponse(clientEndpoint, "Invalid JSON format", 400);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LoginServer] Packet processing error: {ex.Message}");
                SendErrorResponse(clientEndpoint, "Internal server error", 500);
            }
        }

        private async Task HandleLogin(string jsonData, IPEndPoint clientEndpoint)
        {
            var loginRequest = JsonSerializer.Deserialize<LoginRequest>(jsonData);

            if (loginRequest == null || string.IsNullOrEmpty(loginRequest.username) || string.IsNullOrEmpty(loginRequest.password))
            {
                SendLoginResponse(clientEndpoint, false, 0, null, "Username and password required");
                OnFailedLogin?.Invoke("Missing credentials", clientEndpoint);
                return;
            }

            // Check if player is already logged in
            var existingPlayer = GetPlayerByUsername(loginRequest.username);
            if (existingPlayer != null && existingPlayer.IsConnected)
            {
                SendLoginResponse(clientEndpoint, false, 0, null, "Player already logged in");
                OnFailedLogin?.Invoke("Already logged in", clientEndpoint);
                return;
            }

            // Authenticate player from mock database
            var player = AuthenticatePlayer(loginRequest.username, loginRequest.password);

            if (player == null)
            {
                SendLoginResponse(clientEndpoint, false, 0, null, "Invalid username or password");
                OnFailedLogin?.Invoke("Invalid credentials", clientEndpoint);
                Console.WriteLine($"[LoginServer] Failed login attempt for '{loginRequest.username}' from {clientEndpoint.Address}");
                return;
            }

            // Check if player is banned
            if (player.IsBanned)
            {
                if (player.BanExpiry.HasValue && player.BanExpiry.Value > DateTime.UtcNow)
                {
                    string banMessage = $"Account is banned until {player.BanExpiry.Value:yyyy-MM-dd HH:mm:ss}";
                    SendLoginResponse(clientEndpoint, false, 0, null, banMessage);
                    OnFailedLogin?.Invoke(banMessage, clientEndpoint);
                    return;
                }
                else
                {
                    // Ban has expired
                    player.IsBanned = false;
                    player.BanExpiry = null;
                    await UpdateBanStatus(player.PlayerId, false, null);
                }
            }

            // Update player connection info
            player.ClientEndpoint = clientEndpoint;
            player.SessionToken = GenerateSessionToken();
            player.TokenExpiry = DateTime.UtcNow.AddDays(7);
            player.IsConnected = true;
            player.LastHeartbeat = DateTime.UtcNow;
            player.LastLogin = DateTime.UtcNow;

            // Update last login in mock database
            UpdateLastLogin(player.PlayerId);

            // Store player in memory
            connectedPlayers[player.PlayerId] = player;
            tokenToPlayer[player.SessionToken] = player;
            heartbeatMap[player.SessionToken] = DateTime.UtcNow;

            // Send success response
            SendLoginResponse(clientEndpoint, true, player.PlayerId, player.SessionToken, "Login successful");

            Console.WriteLine($"[LoginServer] ✓ Player '{player.Username}' (ID: {player.PlayerId}, MMR: {player.MMR}) logged in from {clientEndpoint.Address}");
            OnPlayerLoggedIn?.Invoke(player);
        }

        private Player AuthenticatePlayer(string username, string password)
        {
            // Find player by username
            if (!mockUsernameToId.TryGetValue(username.ToLower(), out int playerId))
            {
                return null;
            }

            if (!mockPlayerDatabase.TryGetValue(playerId, out MockPlayerData mockData))
            {
                return null;
            }

            // Verify password
            string computedHash = HashPassword(password, mockData.Salt);

            if (mockData.PasswordHash != computedHash)
            {
                return null;
            }

            // Check if banned
            if (mockData.IsBanned)
            {
                if (mockData.BanExpiry.HasValue && mockData.BanExpiry.Value > DateTime.UtcNow)
                {
                    var bannedPlayer = new Player(mockData.PlayerId, mockData.Username, mockData.Email, mockData.PasswordHash);
                    bannedPlayer.IsBanned = true;
                    bannedPlayer.BanExpiry = mockData.BanExpiry;
                    return bannedPlayer;
                }
            }

            // Create Player object from mock data
            var player = new Player(mockData.PlayerId, mockData.Username, mockData.Email, mockData.PasswordHash);

            // Set progression
            player.Level = mockData.Level;
            player.Experience = mockData.Experience;
            player.ExperienceToNextLevel = (int)(100 * Math.Pow(mockData.Level + 1, 1.5));
            player.Gold = mockData.Gold;
            player.MMR = mockData.MMR;
            player.Rank = mockData.Rank;

            // Set stats
            player.Stats.TotalMatches = mockData.TotalMatches;
            player.Stats.TotalWins = mockData.TotalWins;
            player.Stats.TotalLosses = mockData.TotalLosses;
            player.Stats.TotalKills = mockData.TotalKills;
            player.Stats.TotalDeaths = mockData.TotalDeaths;

            // Set inventory
            player.Inventory.OwnedWeapons = new List<int>(mockData.OwnedWeapons);
            player.Inventory.OwnedSkins = new List<int>(mockData.OwnedSkins);
            player.Inventory.OwnedGrenades = new List<int>(mockData.OwnedGrenades);

            // Set loadout
            player.Loadout.PrimaryWeaponId = mockData.PrimaryWeaponId;
            player.Loadout.SecondaryWeaponId = mockData.SecondaryWeaponId;
            player.Loadout.MeleeWeaponId = mockData.MeleeWeaponId;
            player.Loadout.GrenadeId = mockData.GrenadeId;
            player.Loadout.PlayerSkinId = mockData.PlayerSkinId;
            player.Loadout.WeaponSkinId = mockData.WeaponSkinId;

            // Set timestamps
            player.CreatedAt = mockData.CreatedAt;
            player.LastLogin = mockData.LastLogin;

            return player;
        }

        private async Task HandleRegister(string jsonData, IPEndPoint clientEndpoint)
        {
            var registerRequest = JsonSerializer.Deserialize<RegisterRequest>(jsonData);

            if (registerRequest == null)
            {
                SendRegisterResponse(clientEndpoint, false, "Invalid registration data");
                return;
            }

            // Validate input
            if (string.IsNullOrEmpty(registerRequest.username) ||
                string.IsNullOrEmpty(registerRequest.password) ||
                string.IsNullOrEmpty(registerRequest.email))
            {
                SendRegisterResponse(clientEndpoint, false, "All fields are required");
                return;
            }

            // Validate username length
            if (registerRequest.username.Length < 3 || registerRequest.username.Length > 20)
            {
                SendRegisterResponse(clientEndpoint, false, "Username must be between 3 and 20 characters");
                return;
            }

            // Validate email format
            if (!IsValidEmail(registerRequest.email))
            {
                SendRegisterResponse(clientEndpoint, false, "Invalid email format");
                return;
            }

            // Validate password strength
            if (registerRequest.password.Length < 6)
            {
                SendRegisterResponse(clientEndpoint, false, "Password must be at least 6 characters");
                return;
            }

            // Check if username or email already exists
            if (UsernameExists(registerRequest.username))
            {
                SendRegisterResponse(clientEndpoint, false, "Username already taken");
                return;
            }

            if (EmailExists(registerRequest.email))
            {
                SendRegisterResponse(clientEndpoint, false, "Email already registered");
                return;
            }

            // Create new player
            string salt = GenerateSalt();
            string passwordHash = HashPassword(registerRequest.password, salt);

            int playerId = CreateMockPlayerData(registerRequest.username, registerRequest.email, passwordHash, salt);

            if (playerId > 0)
            {
                SendRegisterResponse(clientEndpoint, true, "Registration successful! Please login.");
                Console.WriteLine($"[LoginServer] ✓ New player registered: '{registerRequest.username}' (ID: {playerId})");
            }
            else
            {
                SendRegisterResponse(clientEndpoint, false, "Registration failed. Please try again.");
            }
        }

        private int CreateMockPlayerData(string username, string email, string passwordHash, string salt)
        {
            var mockData = new MockPlayerData
            {
                PlayerId = nextPlayerId,
                Username = username,
                Email = email,
                PasswordHash = passwordHash,
                Salt = salt,
                Level = 1,
                Experience = 0,
                Gold = 500,
                MMR = 1000,
                Rank = 1,
                TotalMatches = 0,
                TotalWins = 0,
                TotalLosses = 0,
                TotalKills = 0,
                TotalDeaths = 0,
                IsBanned = false,
                BanExpiry = null,
                CreatedAt = DateTime.UtcNow,
                LastLogin = DateTime.UtcNow,
                OwnedWeapons = new List<int> { 1, 2, 3, 4 },
                OwnedSkins = new List<int> { 1 },
                OwnedGrenades = new List<int> { 4 },
                PrimaryWeaponId = 1,
                SecondaryWeaponId = 2,
                MeleeWeaponId = 3,
                GrenadeId = 4,
                PlayerSkinId = 1,
                WeaponSkinId = 1
            };

            mockPlayerDatabase[nextPlayerId] = mockData;
            mockUsernameToId[username.ToLower()] = nextPlayerId;
            mockEmailToId[email.ToLower()] = nextPlayerId;

            int newId = nextPlayerId;
            nextPlayerId++;

            return newId;
        }

        private async Task HandleHeartbeat(string jsonData, IPEndPoint clientEndpoint)
        {
            var heartbeat = JsonSerializer.Deserialize<HeartbeatPacket>(jsonData);

            if (heartbeat == null || string.IsNullOrEmpty(heartbeat.token))
            {
                return;
            }

            if (tokenToPlayer.TryGetValue(heartbeat.token, out Player player))
            {
                player.UpdateHeartbeat();
                heartbeatMap[heartbeat.token] = DateTime.UtcNow;

                // Send heartbeat response
                var response = new { type = "heartbeat_response", timestamp = DateTime.UtcNow };
                SendPacket(clientEndpoint, response);
            }
        }

        private void CleanupHeartbeats(object state)
        {
            var now = DateTime.UtcNow;
            var staleTokens = new List<string>();

            foreach (var kvp in heartbeatMap)
            {
                if ((now - kvp.Value).TotalSeconds > 30) // 30 seconds timeout
                {
                    staleTokens.Add(kvp.Key);
                }
            }

            foreach (var token in staleTokens)
            {
                if (tokenToPlayer.TryGetValue(token, out Player player))
                {
                    Console.WriteLine($"[LoginServer] Player {player.Username} disconnected due to heartbeat timeout");
                    heartbeatMap.TryRemove(token, out _);
                    DisconnectPlayer(player);
                }
            }
        }

        private async Task HandleLogout(string jsonData, IPEndPoint clientEndpoint)
        {
            var logoutRequest = JsonSerializer.Deserialize<LogoutRequest>(jsonData);

            if (logoutRequest == null || string.IsNullOrEmpty(logoutRequest.token))
            {
                return;
            }

            if (tokenToPlayer.TryGetValue(logoutRequest.token, out Player player))
            {
                DisconnectPlayer(player);

                var response = new { type = "logout_response", success = true, message = "Logged out successfully" };
                SendPacket(clientEndpoint, response);
            }
        }

        private void DisconnectPlayer(Player player)
        {
            // Remove from queue if in queue
            if (player.IsInQueue)
            {
                matchmakingQueue.RemoveFromQueue(player);
            }

            // Remove from collections
            tokenToPlayer.TryRemove(player.SessionToken, out _);
            connectedPlayers.TryRemove(player.PlayerId, out _);
            heartbeatMap.TryRemove(player.SessionToken, out _);

            player.IsConnected = false;
            player.IsInQueue = false;
            player.IsInGame = false;

            Console.WriteLine($"[LoginServer] Player '{player.Username}' disconnected");
            OnPlayerDisconnected?.Invoke(player);
        }

        private async Task HandleGetProfile(string jsonData, IPEndPoint clientEndpoint)
        {
            var profileRequest = JsonSerializer.Deserialize<ProfileRequest>(jsonData);

            if (profileRequest == null || string.IsNullOrEmpty(profileRequest.token))
            {
                SendErrorResponse(clientEndpoint, "Invalid token", 401);
                return;
            }

            if (tokenToPlayer.TryGetValue(profileRequest.token, out Player player))
            {
                var profile = player.GetFullProfile();

                var response = new { type = "profile_data", profile = profile, timestamp = DateTime.UtcNow };
                SendPacket(clientEndpoint, response);

                Console.WriteLine($"[LoginServer] Sent profile data for player '{player.Username}'");
            }
            else
            {
                SendErrorResponse(clientEndpoint, "Invalid or expired token", 401);
            }
        }

        private async Task HandleJoinQueue(string jsonData, IPEndPoint clientEndpoint)
        {
            var queueRequest = JsonSerializer.Deserialize<JoinQueueRequest>(jsonData);

            if (queueRequest == null || string.IsNullOrEmpty(queueRequest.token))
            {
                SendErrorResponse(clientEndpoint, "Invalid token", 401);
                return;
            }

            if (!tokenToPlayer.TryGetValue(queueRequest.token, out Player player))
            {
                SendErrorResponse(clientEndpoint, "Invalid or expired token", 401);
                return;
            }

            if (player.IsInGame)
            {
                var response = new { type = "queue_response", success = false, message = "Player is already in a game" };
                SendPacket(clientEndpoint, response);
                return;
            }

            bool added = matchmakingQueue.AddToQueue(player);

            var queueResponse = new
            {
                type = "queue_response",
                success = added,
                message = added ? "Added to matchmaking queue" : "Failed to add to queue",
                queueSize = matchmakingQueue.QueueSize,
                position = GetQueuePosition(player),
                estimatedWaitTime = EstimateWaitTime(),
                playersNeeded = Math.Max(0, MatchmakingQueue.PLAYERS_PER_GAME - matchmakingQueue.QueueSize)
            };

            SendPacket(clientEndpoint, queueResponse);
        }

        private async Task HandleLeaveQueue(string jsonData, IPEndPoint clientEndpoint)
        {
            var leaveRequest = JsonSerializer.Deserialize<LeaveQueueRequest>(jsonData);

            if (leaveRequest == null || string.IsNullOrEmpty(leaveRequest.token))
            {
                SendErrorResponse(clientEndpoint, "Invalid token", 401);
                return;
            }

            if (!tokenToPlayer.TryGetValue(leaveRequest.token, out Player player))
            {
                SendErrorResponse(clientEndpoint, "Invalid or expired token", 401);
                return;
            }

            bool removed = matchmakingQueue.RemoveFromQueue(player);

            var response = new
            {
                type = "queue_response",
                success = removed,
                message = removed ? "Removed from matchmaking queue" : "Not in queue",
                queueSize = matchmakingQueue.QueueSize
            };

            SendPacket(clientEndpoint, response);
        }

        private async Task HandleGetQueueStatus(string jsonData, IPEndPoint clientEndpoint)
        {
            var statusRequest = JsonSerializer.Deserialize<QueueStatusRequest>(jsonData);

            if (statusRequest == null || string.IsNullOrEmpty(statusRequest.token))
            {
                SendErrorResponse(clientEndpoint, "Invalid token", 401);
                return;
            }

            if (!tokenToPlayer.TryGetValue(statusRequest.token, out Player player))
            {
                SendErrorResponse(clientEndpoint, "Invalid or expired token", 401);
                return;
            }

            var response = new
            {
                type = "queue_status",
                queueSize = matchmakingQueue.QueueSize,
                position = GetQueuePosition(player),
                estimatedWaitTime = EstimateWaitTime(),
                playersNeeded = Math.Max(0, MatchmakingQueue.PLAYERS_PER_GAME - matchmakingQueue.QueueSize),
                inQueue = player.IsInQueue
            };

            SendPacket(clientEndpoint, response);
        }

        private async Task UpdateBanStatus(int playerId, bool isBanned, DateTime? banExpiry)
        {
            if (mockPlayerDatabase.TryGetValue(playerId, out MockPlayerData mockData))
            {
                mockData.IsBanned = isBanned;
                mockData.BanExpiry = banExpiry;
            }
            await Task.CompletedTask;
        }

        private bool IsRateLimitValid(IPEndPoint clientEndpoint)
        {
            string key = clientEndpoint.Address.ToString();

            if (rateLimitMap.TryGetValue(key, out DateTime lastRequest))
            {
                if ((DateTime.UtcNow - lastRequest).TotalMilliseconds < 1000) // 1 second cooldown
                {
                    return false;
                }
            }

            rateLimitMap[key] = DateTime.UtcNow;
            return true;
        }

        private void UpdateLastLogin(int playerId)
        {
            if (mockPlayerDatabase.TryGetValue(playerId, out MockPlayerData mockData))
            {
                mockData.LastLogin = DateTime.UtcNow;
            }
        }

        private bool UsernameExists(string username)
        {
            return mockUsernameToId.ContainsKey(username.ToLower());
        }

        private bool EmailExists(string email)
        {
            return mockEmailToId.ContainsKey(email.ToLower());
        }

        private Player GetPlayerByUsername(string username)
        {
            foreach (var player in connectedPlayers.Values)
            {
                if (player.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
                    return player;
            }
            return null;
        }

        // Helper Methods
        private string GenerateSalt()
        {
            byte[] saltBytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(saltBytes);
            }
            return Convert.ToBase64String(saltBytes);
        }

        private string HashPassword(string password, string salt)
        {
            using (var sha256 = SHA256.Create())
            {
                string combined = password + salt;
                byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(combined));
                return Convert.ToBase64String(hashBytes);
            }
        }

        private string GenerateSessionToken()
        {
            byte[] tokenBytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(tokenBytes);
            }
            return Convert.ToBase64String(tokenBytes);
        }

        private bool IsValidEmail(string email)
        {
            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }

        // Response Methods
        private void SendLoginResponse(IPEndPoint endpoint, bool success, int playerId, string token, string message)
        {
            var response = new
            {
                type = "login_response",
                success = success,
                playerId = playerId,
                token = token,
                message = message,
                timestamp = DateTime.UtcNow
            };

            SendPacket(endpoint, response);
        }

        private void SendRegisterResponse(IPEndPoint endpoint, bool success, string message)
        {
            var response = new
            {
                type = "register_response",
                success = success,
                message = message,
                timestamp = DateTime.UtcNow
            };

            SendPacket(endpoint, response);
        }

        private void SendErrorResponse(IPEndPoint endpoint, string message, int errorCode)
        {
            var response = new
            {
                type = "error",
                message = message,
                errorCode = errorCode,
                timestamp = DateTime.UtcNow
            };

            SendPacket(endpoint, response);
        }

        private void SendPacket(IPEndPoint endpoint, object packet)
        {
            try
            {
                string jsonData = JsonSerializer.Serialize(packet);
                byte[] data = Encoding.UTF8.GetBytes(jsonData);
                udpServer.Send(data, data.Length, endpoint);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LoginServer] Send error: {ex.Message}");
            }
        }

        public void Stop()
        {
            isRunning = false;

            // Stop matchmaking
            matchmakingQueue?.Stop();

            // Stop timers
            heartbeatCleanupTimer?.Dispose();

            // Disconnect all players
            foreach (var player in connectedPlayers.Values)
            {
                player.IsConnected = false;
                OnPlayerDisconnected?.Invoke(player);
            }

            connectedPlayers.Clear();
            tokenToPlayer.Clear();
            heartbeatMap.Clear();

            receiveThread?.Join(1000);
            udpServer?.Close();

            Console.WriteLine("[LoginServer] Server stopped");
        }

        public Player GetPlayerByToken(string token)
        {
            tokenToPlayer.TryGetValue(token, out Player player);
            return player;
        }

        public Player GetPlayerById(int playerId)
        {
            connectedPlayers.TryGetValue(playerId, out Player player);
            return player;
        }

        public void PrintActivePlayers()
        {
            Console.WriteLine($"\n[LoginServer] Active Players: {connectedPlayers.Count}");
            foreach (var player in connectedPlayers.Values)
            {
                string queueStatus = player.IsInQueue ? $" (In Queue - Pos: {GetQueuePosition(player)})" : "";
                string gameStatus = player.IsInGame ? " (In Game)" : "";
                Console.WriteLine($"  - {player.Username} (ID: {player.PlayerId}, MMR: {player.MMR}, Rank: {player.Rank}){queueStatus}{gameStatus}");
            }
            Console.WriteLine($"\n[Matchmaking] Queue Size: {matchmakingQueue.QueueSize}/{MatchmakingQueue.PLAYERS_PER_GAME}");
        }
    }

    // Mock Database Class
    public class MockPlayerData
    {
        public int PlayerId { get; set; }
        public string Username { get; set; }
        public string Email { get; set; }
        public string PasswordHash { get; set; }
        public string Salt { get; set; }

        // Progression
        public int Level { get; set; }
        public int Experience { get; set; }
        public int Gold { get; set; }
        public int MMR { get; set; }
        public int Rank { get; set; }

        // Stats
        public int TotalMatches { get; set; }
        public int TotalWins { get; set; }
        public int TotalLosses { get; set; }
        public int TotalKills { get; set; }
        public int TotalDeaths { get; set; }

        // Ban info
        public bool IsBanned { get; set; }
        public DateTime? BanExpiry { get; set; }

        // Timestamps
        public DateTime CreatedAt { get; set; }
        public DateTime LastLogin { get; set; }

        // Inventory
        public List<int> OwnedWeapons { get; set; }
        public List<int> OwnedSkins { get; set; }
        public List<int> OwnedGrenades { get; set; }

        // Loadout
        public int PrimaryWeaponId { get; set; }
        public int SecondaryWeaponId { get; set; }
        public int MeleeWeaponId { get; set; }
        public int GrenadeId { get; set; }
        public int PlayerSkinId { get; set; }
        public int WeaponSkinId { get; set; }
    }

    // Packet Classes
    public class LoginRequest
    {
        public string type { get; set; }
        public string username { get; set; }
        public string password { get; set; }
        public DateTime timestamp { get; set; }
    }

    public class RegisterRequest
    {
        public string type { get; set; }
        public string username { get; set; }
        public string password { get; set; }
        public string email { get; set; }
        public DateTime timestamp { get; set; }
    }

    public class HeartbeatPacket
    {
        public string type { get; set; }
        public string token { get; set; }
        public DateTime timestamp { get; set; }
    }

    public class LogoutRequest
    {
        public string type { get; set; }
        public string token { get; set; }
    }

    public class ProfileRequest
    {
        public string type { get; set; }
        public string token { get; set; }
        public int playerId { get; set; }
    }

    public class JoinQueueRequest
    {
        public string type { get; set; }
        public string token { get; set; }
    }

    public class LeaveQueueRequest
    {
        public string type { get; set; }
        public string token { get; set; }
    }

    public class QueueStatusRequest
    {
        public string type { get; set; }
        public string token { get; set; }
    }
}