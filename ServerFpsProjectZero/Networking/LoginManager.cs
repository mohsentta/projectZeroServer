using ProjectZero.Core;
using ProjectZero.Services.BattlePass;
using ProjectZero.Services.Equipment;
using ProjectZero.Services.Matchmaking;
using ProjectZero.Services.Persistence;
using ProjectZero.Services.Rewards;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using ServerFpsProjectZero.Server;
using ServerFpsProjectZero.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
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
        private GameManager gameManager;
        private FriendsManager friendsManager;

        // Store connected players
        private ConcurrentDictionary<int, Player> connectedPlayers;
        private ConcurrentDictionary<string, Player> tokenToPlayer;

        // SQLite persistence and shared services
        private readonly PlayerStore store;
        private readonly BattlePassService battlePassService;
        private readonly DailyRewardService dailyRewardService;
        private readonly EquipService equipService;

        // Matchmaking
        private MatchmakingQueue matchmakingQueue;

        // Events
        public event Action<Player> OnPlayerLoggedIn;
        public event Action<Player> OnPlayerDisconnected;
        public event Action<string, IPEndPoint> OnFailedLogin;

        public LoginManager(ServerManager serverManager, GameManager _gameManager, string _connectionString = "players.db")
        {
            this.serverManager = serverManager;
            gameManager = _gameManager;

            connectedPlayers = new ConcurrentDictionary<int, Player>();
            tokenToPlayer = new ConcurrentDictionary<string, Player>();

            store = new PlayerStore(_connectionString);
            battlePassService = new BattlePassService(store);
            dailyRewardService = new DailyRewardService(store);
            equipService = new EquipService(store);

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

            // Battle pass / inventory
            serverManager.OnBattlePassGetPacket += HandleBattlePassGet;
            serverManager.OnBattlePassClaimPacket += HandleBattlePassClaim;
            serverManager.OnBattlePassBuyPremiumPacket += HandleBattlePassBuyPremium;
            serverManager.OnEquipItemPacket += HandleEquipItem;
            serverManager.OnGetInventoryPacket += HandleGetInventory;
        }

        public void SetFriendsManager(FriendsManager manager)
        {
            this.friendsManager = manager;
        }

        public void Start()
        {
            var playerCount = store.GetPlayerCount();
            Console.WriteLine($"[LoginManager] Player database ready with {playerCount} players");
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

        /// <summary>
        /// Wipes all persistent server data (drops every table in the database)
        /// and reinitializes the schema + test accounts. Bound to the 'restart'
        /// console command. Called on-demand, never automatically at startup.
        /// </summary>
        public void ResetDatabase()
        {
            Console.WriteLine("\n[LoginManager] Wiping server data...");

            // Disconnect any currently connected players and clear in-memory state.
            foreach (var player in connectedPlayers.Values)
            {
                if (player.IsConnected)
                    friendsManager?.UpdatePlayerStatus(player.PlayerId, PlayerStatus.Offline);
            }
            connectedPlayers.Clear();
            tokenToPlayer.Clear();

            store.Reset();

            Console.WriteLine("[LoginManager] Server data wiped and reinitialized.");
        }

        #region Database Initialization


        /// <summary>
        /// Adds a column to an existing table when it is missing (lightweight
        /// migration for databases created by an older schema). Preserves data.
        /// </summary>


        #endregion

        #region Test Player Initialization





        #endregion

        #region Database Operations



        /// <summary>
        /// Adds a column to an existing table, ignoring the error when it is already there.
        ///
        /// SQLite has no "ADD COLUMN IF NOT EXISTS", and CREATE TABLE IF NOT EXISTS does nothing
        /// for a table that already exists - so a database created before a column was
        /// introduced would never gain it.
        /// </summary>
        /// <returns>True when the column was actually added by this call.</returns>

        /// <summary>
        /// One-time migration: copies OwnedWeapons / OwnedSkins / OwnedGrenades into the single
        /// UnlockedItems list.
        ///
        /// The per-type columns were replaced because item ids are globally unique, so splitting
        /// them by type was redundant - and actively harmful, since a bare loadout id could not be
        /// checked against a list that needed to be told the type first.
        ///
        /// Entries that already exist in UnlockedItems are skipped, so running this twice is safe.
        /// </summary>

        /// <summary>
        /// One-time migration: the old single WeaponSkinId column applied to whichever gun was
        /// in the primary slot. Copies it into the per-gun assignment table as the skin for that
        /// player's primary weapon, so an existing assignment is not silently lost.
        /// </summary>

        /// <summary>
        /// Loads the battle pass row for a profile. A missing row is normal for an account
        /// created before the season existed (or for a brand new account), so it starts at
        /// tier 1 rather than failing the login.
        /// </summary>


        /// <summary>
        /// Writes the battle pass row. Upsert rather than update-only so the first save for an
        /// account that predates the season creates the row.
        /// </summary>

        /// <summary>
        /// Gives season XP for a completed match or co-op run, persists it, and pushes the new
        /// state to the player so the panel can update without a profile re-fetch.
        /// </summary>

        private void HandleBattlePassGet(string jsonData, IPEndPoint clientEndpoint)
        {
            var request = JsonConvert.DeserializeObject<BattlePassGetRequest>(jsonData);

            if (request == null || string.IsNullOrEmpty(request.token) ||
                !tokenToPlayer.TryGetValue(request.token, out Player player))
            {
                serverManager.SendErrorResponse(clientEndpoint, "Invalid or expired token", 401);
                return;
            }

            if (player.BattlePass == null)
                player.BattlePass = new BattlePassState();

            BattlePassCatalog.Normalize(player.BattlePass);
            serverManager.SendPacket(new BattlePassStateResponse
            {
                state = player.BattlePass,
                premiumPrice = BattlePassCatalog.PremiumPrice,
                gold = player.Gold
            }, clientEndpoint);
        }

        private void HandleBattlePassClaim(string jsonData, IPEndPoint clientEndpoint)
        {
            var request = JsonConvert.DeserializeObject<BattlePassClaimRequest>(jsonData);

            if (request == null || string.IsNullOrEmpty(request.token) ||
                !tokenToPlayer.TryGetValue(request.token, out Player player))
            {
                serverManager.SendErrorResponse(clientEndpoint, "Invalid or expired token", 401);
                return;
            }

            BattlePassClaimResult result = battlePassService.Claim(player, request.tier, request.premium);
            serverManager.SendPacket(result, clientEndpoint);

            if (result.success)
            {
                Console.WriteLine($"[BattlePass] {player.Username} claimed tier {request.tier} " +
                                  $"{(request.premium ? "paid" : "free")}: {result.reward?.Label} ({result.message})");
            }
        }

        private void HandleBattlePassBuyPremium(string jsonData, IPEndPoint clientEndpoint)
        {
            var request = JsonConvert.DeserializeObject<BattlePassBuyRequest>(jsonData);

            if (request == null || string.IsNullOrEmpty(request.token) ||
                !tokenToPlayer.TryGetValue(request.token, out Player player))
            {
                serverManager.SendErrorResponse(clientEndpoint, "Invalid or expired token", 401);
                return;
            }

            BattlePassPurchaseResult result = battlePassService.BuyPremium(player);
            serverManager.SendPacket(result, clientEndpoint);

            if (result.success)
                Console.WriteLine($"[BattlePass] {player.Username} unlocked the paid pass.");
        }

        private void HandleGetInventory(string jsonData, IPEndPoint clientEndpoint)
        {
            var request = JsonConvert.DeserializeObject<GetInventoryRequest>(jsonData);

            if (request == null || string.IsNullOrEmpty(request.token) ||
                !tokenToPlayer.TryGetValue(request.token, out Player player))
            {
                serverManager.SendErrorResponse(clientEndpoint, "Invalid or expired token", 401);
                return;
            }

            serverManager.SendPacket(new
            {
                type = "inventory_data",
                playerId = player.PlayerId,
                gold = player.Gold,
                inventory = player.Inventory,
                loadout = player.Loadout
            }, clientEndpoint);
        }

        private void HandleEquipItem(string jsonData, IPEndPoint clientEndpoint)
        {
            var request = JsonConvert.DeserializeObject<EquipRequest>(jsonData);

            if (request == null || string.IsNullOrEmpty(request.token) ||
                !tokenToPlayer.TryGetValue(request.token, out Player player))
            {
                serverManager.SendErrorResponse(clientEndpoint, "Invalid or expired token", 401);
                return;
            }

            EquipResponse response = equipService.Equip(player, request);
            serverManager.SendPacket(response, clientEndpoint);

            if (response.Success)
                Console.WriteLine($"[Equip] {player.Username}: {response.Message}");
        }

        #endregion

        private void ApplyDailyLogin(Player player)
        {
            DailyRewardResult reward = dailyRewardService.Apply(player, DateTime.UtcNow);
            if (reward.AlreadyClaimedToday)
            {
                Console.WriteLine($"[LoginManager] Player '{player.Username}' already claimed today's login reward.");
                return;
            }

            Console.WriteLine($"[LoginManager] Daily login reward for '{player.Username}': " +
                              $"streak={reward.Streak}, +{reward.Gold} gold, +{reward.Xp} XP");
        }

        private void AwardBattlePassXp(Player player, int xp, string source)
        {
            if (player == null || xp <= 0) return;

            BattlePassXpAward award = battlePassService.AwardXp(player, xp);
            var client = serverManager.GetClientById(player.PlayerId);
            if (client != null && client.IsConnected)
            {
                serverManager.SendPacket(new BattlePassXpUpdate
                {
                    state = award.State,
                    xpGained = award.XpGained,
                    tiersGained = award.TiersGained,
                    source = source
                }, client);
            }

            Console.WriteLine($"[BattlePass] {player.Username} +{xp} season XP ({source}) " +
                              $"- tier {award.State.Tier}, {award.State.Xp} total" +
                              (award.TiersGained > 0 ? $", +{award.TiersGained} tier(s)" : ""));
        }

        public void UpdatePlayerShards(Player player)
        {
            store.UpdatePlayerShards(player);
        }
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
            var playerProfile = store.GetPlayerProfileFromDb(loginRequest.username);

            if (playerProfile == null)
            {
                SendLoginResponse(clientEndpoint, false, 0, null, "Invalid username or password");
                OnFailedLogin?.Invoke("Invalid credentials", clientEndpoint);
                Console.WriteLine($"[LoginManager] Failed login attempt for '{loginRequest.username}' from {clientEndpoint.Address}");
                return;
            }

            // Reconnect support: if this account already has a session, tear down the
            // old one first instead of hard-rejecting. On a clean client quit the old
            // session is gone via logout/heartbeat; this covers the case where the
            // client crashed or the UDP socket died without a logout, which previously
            // locked the account ("Player already logged in") until a server restart.
            if (connectedPlayers.TryGetValue(playerProfile.PlayerId, out var existingPlayer))
            {
                Console.WriteLine($"[LoginManager] Player '{playerProfile.Username}' reconnecting - replacing existing session");
                DisconnectPlayer(existingPlayer);
            }

            // Create Player instance from profile
            var player = CreatePlayerFromProfile(playerProfile);
            player.ClientEndpoint = clientEndpoint;
            player.IsConnected = true;
            player.LastHeartbeat = DateTime.UtcNow;

            // Grant any due daily-login reward using the PREVIOUS login time
            // (player.LastLogin still holds the value loaded from the profile
            // here, because we have not overwritten it with "now" yet).
            ApplyDailyLogin(player);

            player.LastLogin = DateTime.UtcNow;

            // Create client connection in server manager
            var client = serverManager.CreateClient(player.PlayerId, player.Username, clientEndpoint);
            player.SessionToken = client.SessionToken;

            // Store player in memory
            connectedPlayers[player.PlayerId] = player;
            tokenToPlayer[player.SessionToken] = player;

            // Update the player profile (now includes the refreshed LastLogin,
            // LoginStreak and any daily-login reward that was just granted).
            store.UpdatePlayerProfile(player);

            // Send success response
            SendLoginResponse(clientEndpoint, true, player.PlayerId, player.SessionToken, "Login successful");

            Console.WriteLine($"[LoginManager] [OK] Player '{player.Username}' (ID: {player.PlayerId}, MMR: {player.MMR}) logged in from {clientEndpoint.Address}");

            OnPlayerLoggedIn?.Invoke(player);
            friendsManager?.UpdatePlayerStatus(player.PlayerId, PlayerStatus.Online);
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
                Money = profile.Money,
                // Must be carried over too: UpdatePlayerProfile writes player.CommonShards etc.
                // straight back to the database, so anything not copied here would be reset to
                // zero on the player's next save.
                CommonShards = profile.CommonShards,
                RareShards = profile.RareShards,
                EpicShards = profile.EpicShards,
                LegendaryShards = profile.LegendaryShards,
                Inventory = profile.Inventory ?? new PlayerInventory(),
                Loadout = profile.Loadout ?? new PlayerLoadout(),
                Stats = profile.TotalStats ?? new PlayerStats(),
                TotalPlayTime = profile.TotalPlayTime,
                CreatedAt = profile.CreatedAt,
                LastLogin = profile.LastLogin,
                LoginStreak = profile.LoginStreak,
                BattlePass = profile.BattlePass ?? new BattlePassState()
            };
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

            if (store.UsernameExists(registerRequest.username))
            {
                SendRegisterResponse(clientEndpoint, false, "Username already taken");
                return;
            }

            if (store.EmailExists(registerRequest.email))
            {
                SendRegisterResponse(clientEndpoint, false, "Email already registered");
                return;
            }

            string salt = GenerateSalt();
            string passwordHash = HashPassword(registerRequest.password, salt);

            int playerId = store.CreateNewPlayer(registerRequest.username, registerRequest.email, passwordHash, salt);

            if (playerId > 0)
            {
                SendRegisterResponse(clientEndpoint, true, "Registration successful! Please login.");
                Console.WriteLine($"[LoginManager] [OK] New player registered: '{registerRequest.username}' (ID: {playerId})");
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

                // Season progress uses the same XP figure the match awarded. This runs before
                // UpdatePlayerProfile, which persists the battle pass row.
                AwardBattlePassXp(player, Math.Max(1, xpReward), "match");

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

                // Save to database
                store.UpdatePlayerProfile(player);
                store.UpdatePlayerInventory(player);
                store.UpdatePlayerLoadout(player);

                Console.WriteLine($"[LoginManager] {player.Username} updated stats - Kills: {player.Kills}, Deaths: {player.Deaths}");
            }
        }

        private void DisconnectPlayer(Player player)
        {
            if (player == null) return;

            friendsManager?.OnPlayerDisconnected(player.PlayerId);

            // Remove the player from any active game first so the remaining
            // clients receive a player_despawn and stop rendering them.
            if (player.IsInGame)
                gameManager?.RemovePlayerFromGame(player.PlayerId);

            // Remove from queue if in queue
            matchmakingQueue.RemoveFromQueue(player);

            // Remove the matching ClientConnection from the server manager's
            // lookups too. Without this, a reconnect (login that replaces an old
            // session) leaves a stale ClientConnection in heartbeatMap, which later
            // timeouts out and removes the NEW connection for the same PlayerId.
            // Use the event-free variant to avoid recursing back into this method.
            var client = serverManager.GetClientById(player.PlayerId);
            if (client != null)
                serverManager.RemoveClientConnection(client);

            // Save player data to database before disconnecting
            store.UpdatePlayerProfile(player);
            store.UpdatePlayerInventory(player);
            store.UpdatePlayerLoadout(player);

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

        /// <summary>
        /// Clears the in-game flags on the Player model for a player who is no
        /// longer in a game (used when the game ends or is forfeited). The
        /// ClientConnection flags are owned by GameManager; this clears the
        /// parallel Player-model flags that block matchmaking re-entry.
        /// </summary>
        public void ResetPlayerGameState(int playerId)
        {
            if (connectedPlayers.TryGetValue(playerId, out Player player))
            {
                player.IsInGame = false;
                player.IsInQueue = false;
                player.CurrentGameId = -1;
                player.TeamId = -1;
                player.Kills = 0;
                player.Deaths = 0;
                player.Score = 0;
            }
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

            if (added)
            {
                friendsManager?.UpdatePlayerStatus(player.PlayerId, PlayerStatus.InQueue);
            }

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

            if (removed)
            {
                friendsManager?.UpdatePlayerStatus(player.PlayerId, PlayerStatus.Online);
            }

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
            Console.WriteLine($"\n[LoginManager] Matchmaking complete with {players.Count} players; creating game.");

            // Mark matched players as InGame for their friends.
            foreach (var player in players)
            {
                if (player.IsConnected)
                    friendsManager?.UpdatePlayerStatus(player.PlayerId, PlayerStatus.InGame);
            }

            // GameManager.CreateGame is the single authority here: it assigns a
            // stable game id and balanced teams and sends one authoritative
            // game_start (+ player_spawn) packet to each participant. Calling it
            // once avoids the previous bug of creating N GameRooms per match and
            // sending conflicting game_start packets with mismatched ids/teams.
            gameManager.CreateGame(players, GameType.TeamDeathmatch, Map.Office);
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

        /// <summary>
        /// Persists just the four shard columns for one account.
        ///
        /// Separate from UpdatePlayerProfile on purpose: shards are granted mid-run, by a drop,
        /// and a full profile write from the game-loop thread would race the packet handlers that
        /// also write the profile. This touches only the four columns it owns.
        /// </summary>

        public Player GetPlayerByToken(string token)
        {
            tokenToPlayer.TryGetValue(token, out Player player);
            return player;
        }

        /// <summary>
        /// Console/debug helper: grants season XP to a connected player by username.
        /// Used to tune the track and to exercise claims without playing a full match.
        /// </summary>
        public bool GrantBattlePassXp(string username, int amount)
        {
            Player player = connectedPlayers.Values.FirstOrDefault(
                p => string.Equals(p.Username, username, StringComparison.OrdinalIgnoreCase));

            if (player == null) return false;

            AwardBattlePassXp(player, amount, "debug");
            return true;
        }

        public Player GetPlayerById(int playerId)
        {
            connectedPlayers.TryGetValue(playerId, out Player player);
            return player;
        }

        public PlayerProfile GetPlayerProfile(int playerId)
        {
            // First check connected players
            var player = GetPlayerById(playerId);
            if (player != null)
            {
                return player.GetFullProfile();
            }

            // Fall back to database
            return store.GetPlayerProfileFromDbById(playerId);
        }

        public void UpdatePlayerStatusForFriends(int playerId, PlayerStatus status)
        {
            friendsManager?.UpdatePlayerStatus(playerId, status);
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