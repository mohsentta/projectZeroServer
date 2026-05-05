using Newtonsoft.Json;
using ServerFpsProjectZero.Matchmaking;
using ServerFpsProjectZero.Models;
using ServerFpsProjectZero.Server;
using ServerFpsProjectZero.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ServerFpsProjectZero.Networking
{
    public class LoginManager
    {
        private ServerManager serverManager;

        // Store connected players
        private ConcurrentDictionary<int, Player> connectedPlayers;
        private ConcurrentDictionary<string, Player> tokenToPlayer;

        // Player database storage
        private Dictionary<int, PlayerProfile> playerDatabase;
        private Dictionary<string, int> usernameToId;
        private Dictionary<string, int> emailToId;
        private int nextPlayerId;

        // Matchmaking
        private MatchmakingQueue matchmakingQueue;

        // Events
        public event Action<Player> OnPlayerLoggedIn;
        public event Action<Player> OnPlayerDisconnected;
        public event Action<string, IPEndPoint> OnFailedLogin;

        public LoginManager(ServerManager serverManager)
        {
            this.serverManager = serverManager;

            connectedPlayers = new ConcurrentDictionary<int, Player>();
            tokenToPlayer = new ConcurrentDictionary<string, Player>();

            // Initialize player database
            playerDatabase = new Dictionary<int, PlayerProfile>();
            usernameToId = new Dictionary<string, int>();
            emailToId = new Dictionary<string, int>();
            nextPlayerId = 1000;

            // Initialize matchmaking
            matchmakingQueue = new MatchmakingQueue();

            // Subscribe to matchmaking events
            matchmakingQueue.OnGameMatched += HandleGameMatched;
            matchmakingQueue.OnPlayerEnteredQueue += HandlePlayerEnteredQueue;
            matchmakingQueue.OnPlayerLeftQueue += HandlePlayerLeftQueue;
            matchmakingQueue.OnPlayerTimedOut += HandlePlayerTimedOut;
            matchmakingQueue.OnQueueStatusChanged += HandleQueueStatusChanged;

            // Subscribe to server events
            serverManager.OnLoginPacket += HandleLogin;
            serverManager.OnRegisterPacket += HandleRegister;
            serverManager.OnHeartbeatPacket += HandleHeartbeat;
            serverManager.OnLogoutPacket += HandleLogout;
            serverManager.OnGetProfilePacket += HandleGetProfile;
            serverManager.OnJoinQueuePacket += HandleJoinQueue;
            serverManager.OnLeaveQueuePacket += HandleLeaveQueue;
            serverManager.OnGetQueueStatusPacket += HandleGetQueueStatus;
            serverManager.OnClientDisconnected += HandleClientDisconnected;
            serverManager.OnPlayerGameStatsUpdate += UpdatePlayerGameStats;

            // Create test accounts
            InitializeTestPlayers();
        }

        public void Start()
        {
            Console.WriteLine($"[LoginManager] Player database ready with {playerDatabase.Count} players");
            Console.WriteLine($"[LoginManager] Matchmaking queue active (requires {MatchmakingQueue.PLAYERS_PER_GAME} players per game)");
        }

        public void Stop()
        {
            // Stop matchmaking
            matchmakingQueue?.Stop();

            // Disconnect all players
            foreach (var player in connectedPlayers.Values)
            {
                OnPlayerDisconnected?.Invoke(player);
            }

            connectedPlayers.Clear();
            tokenToPlayer.Clear();

            Console.WriteLine("[LoginManager] Stopped");
        }

        #region Test Player Initialization

        private void InitializeTestPlayers()
        {
            CreateTestPlayer("player1", "player1@test.com", "password123", 1200, 500, 5);
            CreateTestPlayer("player2", "player2@test.com", "password123", 1350, 750, 8);
            CreateTestPlayer("player3", "player3@test.com", "password123", 1100, 300, 3);
            CreateTestPlayer("proplayer", "pro@test.com", "pro123", 1850, 2500, 15);
            CreateTestPlayer("newbie", "newbie@test.com", "test123", 1000, 100, 1);
            CreateTestPlayer("veteran", "vet@test.com", "vet123", 1500, 1200, 10);
            CreateTestPlayer("casual", "casual@test.com", "casual123", 1150, 400, 4);
            CreateTestPlayer("tryhard", "tryhard@test.com", "try123", 1700, 1800, 12);
            CreateTestPlayer("sniper", "sniper@test.com", "sniper123", 1400, 900, 7);
            CreateTestPlayer("shotgun", "shotgun@test.com", "shotgun123", 1250, 600, 6);

            Console.WriteLine("[Database] Player database initialized with 10 test players");
        }

        private void CreateTestPlayer(string username, string email, string password, int mmr, int gold, int level)
        {
            string salt = GenerateSalt();
            string passwordHash = HashPassword(password, salt);

            var playerProfile = new PlayerProfile
            {
                PlayerId = nextPlayerId,
                Username = username,
                Email = email,
                Level = level,
                Experience = level * 100,
                ExperienceToNextLevel = CalculateExpToNextLevel(level),
                Gold = gold,
                MMR = mmr,
                Rank = CalculateRankFromMMR(mmr),
                TotalStats = new PlayerStats
                {
                    TotalMatches = level * 10,
                    TotalWins = level * 6,
                    TotalLosses = level * 4,
                    TotalKills = level * 50,
                    TotalDeaths = level * 30,
                    TotalHeadshots = level * 15,
                    TotalAssists = level * 20,
                    WinRate = 60f,
                    KDRatio = 1.666f
                },
                Inventory = new PlayerInventory
                {
                    OwnedWeapons = new List<int> { 1, 2, 3, 4, 5, 6 },
                    OwnedSkins = new List<int> { 1, 2, 3 },
                    OwnedGrenades = new List<int> { 4, 5 },
                    LootBoxes = new List<LootBox>()
                },
                Loadout = new PlayerLoadout
                {
                    PrimaryWeaponId = 1,
                    SecondaryWeaponId = 2,
                    MeleeWeaponId = 3,
                    GrenadeId = 4,
                    PlayerSkinId = 1,
                    WeaponSkinId = 1
                },
                TotalPlayTime = TimeSpan.FromHours(level * 5),
                CreatedAt = DateTime.UtcNow.AddDays(-level * 7),
                LastLogin = DateTime.UtcNow.AddDays(-1)
            };

            playerDatabase[nextPlayerId] = playerProfile;
            usernameToId[username.ToLower()] = nextPlayerId;
            emailToId[email.ToLower()] = nextPlayerId;
            nextPlayerId++;
        }

        private int CreateNewPlayer(string username, string email, string passwordHash, string salt)
        {
            var player = new Player(nextPlayerId, username, email, passwordHash)
            {
                MMR = 1000,
                Rank = 1,
                Gold = 500,
                Level = 1,
                Experience = 0,
                ExperienceToNextLevel = CalculateExpToNextLevel(1),
                CreatedAt = DateTime.UtcNow,
                LastLogin = DateTime.UtcNow
            };

            var playerProfile = player.GetFullProfile();
            playerDatabase[nextPlayerId] = playerProfile;
            usernameToId[username.ToLower()] = nextPlayerId;
            emailToId[email.ToLower()] = nextPlayerId;

            int newId = nextPlayerId;
            nextPlayerId++;

            return newId;
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

        #endregion

        #region Packet Handlers

        private async void HandleLogin(string jsonData, IPEndPoint clientEndpoint)
        {
            var loginRequest = JsonConvert.DeserializeObject<LoginRequest>(jsonData);

            if (loginRequest == null || string.IsNullOrEmpty(loginRequest.username) || string.IsNullOrEmpty(loginRequest.password))
            {
                SendLoginResponse(clientEndpoint, false, 0, null, "Username and password required");
                OnFailedLogin?.Invoke("Missing credentials", clientEndpoint);
                return;
            }

            // Check if player is already logged in
            if (usernameToId.TryGetValue(loginRequest.username.ToLower(), out int existingId))
            {
                if (connectedPlayers.TryGetValue(existingId, out var existingPlayer))
                {
                    SendLoginResponse(clientEndpoint, false, 0, null, "Player already logged in");
                    OnFailedLogin?.Invoke("Already logged in", clientEndpoint);
                    return;
                }
            }

            // Authenticate player
            var playerProfile = AuthenticatePlayer(loginRequest.username, loginRequest.password);

            if (playerProfile == null)
            {
                SendLoginResponse(clientEndpoint, false, 0, null, "Invalid username or password");
                OnFailedLogin?.Invoke("Invalid credentials", clientEndpoint);
                Console.WriteLine($"[LoginManager] Failed login attempt for '{loginRequest.username}' from {clientEndpoint.Address}");
                return;
            }

            // Create Player instance from profile
            var player = CreatePlayerFromProfile(playerProfile);
            player.ClientEndpoint = clientEndpoint;
            player.IsConnected = true;
            player.LastHeartbeat = DateTime.UtcNow;
            player.LastLogin = DateTime.UtcNow;

            // Create client connection in server manager
            var client = serverManager.CreateClient(player.PlayerId, player.Username, clientEndpoint);
            player.SessionToken = client.SessionToken;

            // Store player in memory
            connectedPlayers[player.PlayerId] = player;
            tokenToPlayer[player.SessionToken] = player;

            // Send success response
            SendLoginResponse(clientEndpoint, true, player.PlayerId, player.SessionToken, "Login successful");

            Console.WriteLine($"[LoginManager] ✓ Player '{player.Username}' (ID: {player.PlayerId}, MMR: {player.MMR}) logged in from {clientEndpoint.Address}");
            OnPlayerLoggedIn?.Invoke(player);
        }

        private Player CreatePlayerFromProfile(PlayerProfile profile)
        {
            return new Player(profile.PlayerId, profile.Username, profile.Email, "")
            {
                MMR = profile.MMR,
                Rank = profile.Rank,
                Level = profile.Level,
                Experience = profile.Experience,
                ExperienceToNextLevel = profile.ExperienceToNextLevel,
                Gold = profile.Gold,
                Inventory = profile.Inventory ?? new PlayerInventory(),
                Loadout = profile.Loadout ?? new PlayerLoadout(),
                Stats = profile.TotalStats ?? new PlayerStats(),
                TotalPlayTime = profile.TotalPlayTime,
                CreatedAt = profile.CreatedAt,
                LastLogin = profile.LastLogin
            };
        }

        private PlayerProfile AuthenticatePlayer(string username, string password)
        {
            if (!usernameToId.TryGetValue(username.ToLower(), out int playerId))
            {
                return null;
            }

            if (!playerDatabase.TryGetValue(playerId, out PlayerProfile playerProfile))
            {
                return null;
            }

            // In a real implementation, verify password hash
            // For test accounts, we're just checking if the profile exists
            return playerProfile;
        }

        private async void HandleRegister(string jsonData, IPEndPoint clientEndpoint)
        {
            var registerRequest = JsonConvert.DeserializeObject<RegisterRequest>(jsonData);

            if (registerRequest == null)
            {
                SendRegisterResponse(clientEndpoint, false, "Invalid registration data");
                return;
            }

            if (string.IsNullOrEmpty(registerRequest.username) ||
                string.IsNullOrEmpty(registerRequest.password) ||
                string.IsNullOrEmpty(registerRequest.email))
            {
                SendRegisterResponse(clientEndpoint, false, "All fields are required");
                return;
            }

            if (registerRequest.username.Length < 3 || registerRequest.username.Length > 20)
            {
                SendRegisterResponse(clientEndpoint, false, "Username must be between 3 and 20 characters");
                return;
            }

            if (!IsValidEmail(registerRequest.email))
            {
                SendRegisterResponse(clientEndpoint, false, "Invalid email format");
                return;
            }

            if (registerRequest.password.Length < 6)
            {
                SendRegisterResponse(clientEndpoint, false, "Password must be at least 6 characters");
                return;
            }

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

            string salt = GenerateSalt();
            string passwordHash = HashPassword(registerRequest.password, salt);

            int playerId = CreateNewPlayer(registerRequest.username, registerRequest.email, passwordHash, salt);

            if (playerId > 0)
            {
                SendRegisterResponse(clientEndpoint, true, "Registration successful! Please login.");
                Console.WriteLine($"[LoginManager] ✓ New player registered: '{registerRequest.username}' (ID: {playerId})");
            }
            else
            {
                SendRegisterResponse(clientEndpoint, false, "Registration failed. Please try again.");
            }
        }

        private async void HandleHeartbeat(string jsonData, IPEndPoint clientEndpoint)
        {
            var heartbeat = JsonConvert.DeserializeObject<HeartbeatPacket>(jsonData);

            if (heartbeat == null || string.IsNullOrEmpty(heartbeat.token))
            {
                return;
            }

            if (tokenToPlayer.TryGetValue(heartbeat.token, out Player player))
            {
                player.UpdateHeartbeat();

                // Update client heartbeat in server manager
                var client = serverManager.GetClientByToken(heartbeat.token);
                if (client != null)
                {
                    serverManager.UpdateHeartbeat(client);
                }

                var response = new { type = "heartbeat_response", timestamp = DateTime.UtcNow };
                serverManager.SendPacket(response, clientEndpoint);
            }
        }

        private async void HandleLogout(string jsonData, IPEndPoint clientEndpoint)
        {
            var logoutRequest = JsonConvert.DeserializeObject<LogoutRequest>(jsonData);

            if (logoutRequest == null || string.IsNullOrEmpty(logoutRequest.token))
            {
                return;
            }

            if (tokenToPlayer.TryGetValue(logoutRequest.token, out Player player))
            {
                DisconnectPlayer(player);

                var response = new { type = "logout_response", success = true, message = "Logged out successfully" };
                serverManager.SendPacket(response, clientEndpoint);
            }
        }

        private void HandleClientDisconnected(ClientConnection client)
        {
            if (tokenToPlayer.TryGetValue(client.SessionToken, out Player player))
            {
                DisconnectPlayer(player);
            }
        }

        private void UpdatePlayerGameStats(int playerId, int kills, int deaths, bool isWinner, int goldReward, int xpReward)
        {
            if (connectedPlayers.TryGetValue(playerId, out Player player))
            {
                player.Kills += kills;
                player.Deaths += deaths;
                player.AddGold(goldReward);
                player.AddExperience(xpReward);

                if (player.Stats != null)
                {
                    player.Stats.TotalMatches++;
                    if (isWinner)
                        player.Stats.TotalWins++;
                    else
                        player.Stats.TotalLosses++;

                    player.Stats.TotalKills += kills;
                    player.Stats.TotalDeaths += deaths;

                    // Update derived stats
                    player.Stats.WinRate = (float)player.Stats.TotalWins / player.Stats.TotalMatches * 100f;
                    player.Stats.KDRatio = player.Stats.TotalDeaths > 0 ?
                        (float)player.Stats.TotalKills / player.Stats.TotalDeaths :
                        player.Stats.TotalKills;
                }

                // Update player profile in database
                if (playerDatabase.TryGetValue(playerId, out PlayerProfile profile))
                {
                    profile.Gold = player.Gold;
                    profile.Experience = player.Experience;
                    profile.Level = player.Level;
                    profile.ExperienceToNextLevel = player.ExperienceToNextLevel;
                    profile.TotalStats = player.Stats;
                    profile.LastLogin = DateTime.UtcNow;
                }

                Console.WriteLine($"[LoginManager] {player.Username} updated stats - Kills: {player.Kills}, Deaths: {player.Deaths}");
            }
        }

        private void DisconnectPlayer(Player player)
        {
            if (player == null) return;

            // Remove from queue if in queue
            matchmakingQueue.RemoveFromQueue(player);

            // Update player state
            player.IsConnected = false;
            player.IsInGame = false;
            player.IsInQueue = false;

            // Find and remove token
            var token = tokenToPlayer.FirstOrDefault(kvp => kvp.Value.PlayerId == player.PlayerId).Key;
            if (token != null)
            {
                tokenToPlayer.TryRemove(token, out _);
            }

            connectedPlayers.TryRemove(player.PlayerId, out _);

            Console.WriteLine($"[LoginManager] Player '{player.Username}' disconnected");
            OnPlayerDisconnected?.Invoke(player);
        }

        private void HandleGetProfile(string jsonData, IPEndPoint clientEndpoint)
        {
            var profileRequest = JsonConvert.DeserializeObject<GetProfileRequest>(jsonData);

            if (profileRequest == null || string.IsNullOrEmpty(profileRequest.token))
            {
                serverManager.SendErrorResponse(clientEndpoint, "Invalid token", 401);
                return;
            }

            if (!tokenToPlayer.TryGetValue(profileRequest.token, out Player player))
            {
                serverManager.SendErrorResponse(clientEndpoint, "Invalid or expired token", 401);
                return;
            }

            var profile = player.GetFullProfile();
            var response = new ProfileDataWrapper
            {
                type = "profile_data",
                profile = profile,
                timestamp = DateTime.UtcNow
            };
            serverManager.SendPacket(response, clientEndpoint);

            Console.WriteLine($"[LoginManager] Sent profile data for player '{player.Username}'");
        }

        private void HandleJoinQueue(string jsonData, IPEndPoint clientEndpoint)
        {
            var queueRequest = JsonConvert.DeserializeObject<JoinQueueRequest>(jsonData);

            if (queueRequest == null || string.IsNullOrEmpty(queueRequest.token))
            {
                serverManager.SendErrorResponse(clientEndpoint, "Invalid token", 401);
                return;
            }

            if (!tokenToPlayer.TryGetValue(queueRequest.token, out Player player))
            {
                serverManager.SendErrorResponse(clientEndpoint, "Invalid or expired token", 401);
                return;
            }

            if (player.IsInGame)
            {
                var response = new QueueResponseData
                {
                    type = "queue_response",
                    success = false,
                    message = "Player is already in a game"
                };
                serverManager.SendPacket(response, clientEndpoint);
                return;
            }

            bool added = matchmakingQueue.AddToQueue(player);

            var queueResponse = new QueueResponseData
            {
                type = "queue_response",
                success = added,
                message = added ? "Added to matchmaking queue" : "Failed to add to queue",
                queueSize = matchmakingQueue.QueueSize,
                position = matchmakingQueue.GetQueuePosition(player.PlayerId),
                estimatedWaitTime = EstimateWaitTime(),
                playersNeeded = Math.Max(0, MatchmakingQueue.PLAYERS_PER_GAME - matchmakingQueue.QueueSize)
            };

            serverManager.SendPacket(queueResponse, clientEndpoint);
        }

        private void HandleLeaveQueue(string jsonData, IPEndPoint clientEndpoint)
        {
            var leaveRequest = JsonConvert.DeserializeObject<LeaveQueueRequest>(jsonData);

            if (leaveRequest == null || string.IsNullOrEmpty(leaveRequest.token))
            {
                serverManager.SendErrorResponse(clientEndpoint, "Invalid token", 401);
                return;
            }

            if (!tokenToPlayer.TryGetValue(leaveRequest.token, out Player player))
            {
                serverManager.SendErrorResponse(clientEndpoint, "Invalid or expired token", 401);
                return;
            }

            bool removed = matchmakingQueue.RemoveFromQueue(player);

            var response = new QueueResponseData
            {
                type = "queue_response",
                success = removed,
                message = removed ? "Removed from matchmaking queue" : "Not in queue",
                queueSize = matchmakingQueue.QueueSize
            };

            serverManager.SendPacket(response, clientEndpoint);
        }

        private void HandleGetQueueStatus(string jsonData, IPEndPoint clientEndpoint)
        {
            var statusRequest = JsonConvert.DeserializeObject<GetQueueStatusRequest>(jsonData);

            if (statusRequest == null || string.IsNullOrEmpty(statusRequest.token))
            {
                serverManager.SendErrorResponse(clientEndpoint, "Invalid token", 401);
                return;
            }

            if (!tokenToPlayer.TryGetValue(statusRequest.token, out Player player))
            {
                serverManager.SendErrorResponse(clientEndpoint, "Invalid or expired token", 401);
                return;
            }

            var response = new QueueStatusData
            {
                type = "queue_status",
                queueSize = matchmakingQueue.QueueSize,
                position = matchmakingQueue.GetQueuePosition(player.PlayerId),
                estimatedWaitTime = EstimateWaitTime(),
                playersNeeded = Math.Max(0, MatchmakingQueue.PLAYERS_PER_GAME - matchmakingQueue.QueueSize),
                inQueue = player.IsInQueue,
                timestamp = DateTime.UtcNow
            };

            serverManager.SendPacket(response, clientEndpoint);
        }

        #endregion

        #region Matchmaking Event Handlers

        private void HandleGameMatched(List<Player> players, GameSession gameSession)
        {
            Console.WriteLine($"\n[LoginManager] Game {gameSession.GameId} starting with {players.Count} players");

            foreach (var player in players)
            {
                if (player.IsConnected)
                {
                    var gameStartPacket = new GameStartData
                    {
                        type = "game_start",
                        gameId = gameSession.GameId,
                        teamId = player.TeamId,
                        mapName = Map.Office,
                        gameType = GameType.TeamDeathmatch,
                        players = players.Select(p => new GamePlayerData
                        {
                            playerId = p.PlayerId,
                            username = p.Username,
                            level = p.Level,
                            mmr = p.MMR,
                            rank = p.Rank,
                            teamId = p.TeamId,
                            loadout = p.Loadout
                        }).ToList(),
                        timestamp = DateTime.UtcNow
                    };

                    serverManager.SendPacket(gameStartPacket, player.ClientEndpoint);
                }
            }
        }

        private void HandlePlayerEnteredQueue(QueuedPlayer queuedPlayer)
        {
            var player = queuedPlayer.Player;
            if (player != null && player.IsConnected)
            {
                SendQueueStatusUpdate(player);
                Console.WriteLine($"[Matchmaking] Player {player.Username} entered queue. Position: {matchmakingQueue.GetQueuePosition(player.PlayerId)}");
            }
        }

        private void HandlePlayerLeftQueue(QueuedPlayer queuedPlayer)
        {
            var player = queuedPlayer.Player;
            if (player != null && player.IsConnected)
            {
                SendQueueStatusUpdate(player);
                Console.WriteLine($"[Matchmaking] Player {player.Username} left queue");
            }
        }

        private void HandlePlayerTimedOut(QueuedPlayer queuedPlayer)
        {
            var player = queuedPlayer.Player;
            if (player != null && player.IsConnected)
            {
                var timeoutPacket = new QueueTimeoutData
                {
                    type = "queue_timeout",
                    message = "Matchmaking queue timed out. Please try again.",
                    timestamp = DateTime.UtcNow
                };
                serverManager.SendPacket(timeoutPacket, player.ClientEndpoint);
                Console.WriteLine($"[Matchmaking] Player {player.Username} timed out of queue");
            }
        }

        private void HandleQueueStatusChanged(int queueSize)
        {
            foreach (var player in connectedPlayers.Values)
            {
                if (player.IsInQueue && player.IsConnected)
                {
                    SendQueueStatusUpdate(player);
                }
            }
        }

        private void SendQueueStatusUpdate(Player player)
        {
            if (player != null && player.IsConnected)
            {
                var statusPacket = new QueueStatusData
                {
                    type = "queue_status",
                    queueSize = matchmakingQueue.QueueSize,
                    position = matchmakingQueue.GetQueuePosition(player.PlayerId),
                    estimatedWaitTime = EstimateWaitTime(),
                    playersNeeded = Math.Max(0, MatchmakingQueue.PLAYERS_PER_GAME - matchmakingQueue.QueueSize),
                    inQueue = player.IsInQueue,
                    timestamp = DateTime.UtcNow
                };

                serverManager.SendPacket(statusPacket, player.ClientEndpoint);
            }
        }

        #endregion

        #region Helper Methods

        private int EstimateWaitTime()
        {
            int queueSize = matchmakingQueue.QueueSize;
            int playersNeeded = Math.Max(0, MatchmakingQueue.PLAYERS_PER_GAME - queueSize);
            return (playersNeeded / 2) * 30;
        }

        private int CalculateExpToNextLevel(int level)
        {
            return 100 + (level * 50);
        }

        private bool UsernameExists(string username)
        {
            return usernameToId.ContainsKey(username.ToLower());
        }

        private bool EmailExists(string email)
        {
            return emailToId.ContainsKey(email.ToLower());
        }

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

        #endregion

        #region Response Methods

        private void SendLoginResponse(IPEndPoint endpoint, bool success, int playerId, string token, string message)
        {
            var response = new LoginResponse
            {
                type = "login_response",
                success = success,
                playerId = playerId,
                token = token,
                message = message,
                timestamp = DateTime.UtcNow
            };

            serverManager.SendPacket(response, endpoint);
        }

        private void SendRegisterResponse(IPEndPoint endpoint, bool success, string message)
        {
            var response = new RegisterResponse
            {
                type = "register_response",
                success = success,
                message = message,
                timestamp = DateTime.UtcNow
            };

            serverManager.SendPacket(response, endpoint);
        }

        #endregion

        #region Public Methods

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

        public PlayerProfile GetPlayerProfile(int playerId)
        {
            playerDatabase.TryGetValue(playerId, out PlayerProfile profile);
            return profile;
        }

        public void PrintActivePlayers()
        {
            Console.WriteLine($"\n[LoginManager] Active Players: {connectedPlayers.Count}");
            foreach (var player in connectedPlayers.Values)
            {
                string queueStatus = player.IsInQueue ? $" (In Queue - Pos: {matchmakingQueue.GetQueuePosition(player.PlayerId)})" : "";
                string gameStatus = player.IsInGame ? " (In Game)" : "";
                Console.WriteLine($"  - {player.Username} (ID: {player.PlayerId}, MMR: {player.MMR}, Rank: {player.Rank}){queueStatus}{gameStatus}");
            }
            Console.WriteLine($"\n[Matchmaking] Queue Size: {matchmakingQueue.QueueSize}/{MatchmakingQueue.PLAYERS_PER_GAME}");
        }

        #endregion
    }
}