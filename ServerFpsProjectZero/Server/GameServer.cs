using Newtonsoft.Json;
using ServerFpsProjectZero.Models;
using ServerFpsProjectZero.Server;
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
    public class GameServer
    {
        private UdpClient udpServer;
        private ConcurrentDictionary<int, ClientConnection> clients = new ConcurrentDictionary<int, ClientConnection>();
        private ConcurrentDictionary<int, GameRoom> activeGames = new ConcurrentDictionary<int, GameRoom>();
        private ConcurrentQueue<PlayerMatchmaking> matchmakingQueue = new ConcurrentQueue<PlayerMatchmaking>();
        private int nextPlayerId = 1000;
        private int nextGameId = 1;
        private bool isRunning = false;
        private Thread receiveThread;
        private Thread matchmakingThread;

        // Configuration
        private const int TICK_RATE = 20; // 20 ticks per second
        private const float TICK_TIME = 1f / TICK_RATE;
        private const int MATCHMAKING_INTERVAL = 2000; // 2 seconds
        private const int PLAYERS_PER_GAME = 4; // 2v2
        private const float GAME_DURATION = 600f; // 10 minutes in seconds

        public GameServer()
        {
            LoadPlayerData();
        }

        public void Start(int port = 7777)
        {
            try
            {
                udpServer = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                isRunning = true;

                receiveThread = new Thread(ReceivePackets);
                receiveThread.IsBackground = true;
                receiveThread.Start();

                matchmakingThread = new Thread(MatchmakingLoop);
                matchmakingThread.IsBackground = true;
                matchmakingThread.Start();

                // Start game update loop
                Task.Run(GameUpdateLoop);

                Console.WriteLine($"[Server] Game server started on port {port}");
                Console.WriteLine($"[Server] Players per game: {PLAYERS_PER_GAME}");
                Console.WriteLine($"[Server] Game duration: {GAME_DURATION} seconds");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server] Failed to start: {ex.Message}");
            }
        }

        public void Stop()
        {
            isRunning = false;
            receiveThread?.Join(1000);
            matchmakingThread?.Join(1000);
            udpServer?.Close();
            Console.WriteLine("[Server] Game server stopped");
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

                    Task.Run(() => ProcessPacket(jsonData, remoteEndpoint));
                }
                catch (Exception ex)
                {
                    if (isRunning)
                        Console.WriteLine($"[Server] Receive error: {ex.Message}");
                }
            }
        }

        private void ProcessPacket(string jsonData, IPEndPoint remoteEndpoint)
        {
            try
            {
                var packet = JsonConvert.DeserializeObject<Dictionary<string, object>>(jsonData);
                string type = packet["type"].ToString();

                // Find or create client connection
                var client = clients.Values.FirstOrDefault(c => c.Endpoint.Equals(remoteEndpoint));

                switch (type)
                {
                    case "login":
                        HandleLogin(jsonData, remoteEndpoint);
                        break;
                    case "register":
                        HandleRegister(jsonData, remoteEndpoint);
                        break;
                    case "get_profile":
                        HandleGetProfile(jsonData, remoteEndpoint);
                        break;
                    case "join_queue":
                        HandleJoinQueue(jsonData, remoteEndpoint);
                        break;
                    case "leave_queue":
                        HandleLeaveQueue(jsonData, remoteEndpoint);
                        break;
                    case "get_queue_status":
                        HandleGetQueueStatus(jsonData, remoteEndpoint);
                        break;
                    case "logout":
                        HandleLogout(jsonData, remoteEndpoint);
                        break;
                    case "heartbeat":
                        HandleHeartbeat(jsonData, remoteEndpoint);
                        break;
                    case "movement_input":
                        HandleMovementInput(jsonData, remoteEndpoint);
                        break;
                    case "shoot_input":
                        HandleShootInput(jsonData, remoteEndpoint);
                        break;
                    case "ability_input":
                        HandleAbilityInput(jsonData, remoteEndpoint);
                        break;
                    case "player_state":
                        HandlePlayerState(jsonData, remoteEndpoint);
                        break;
                    case "player_respawn":
                        HandlePlayerRespawn(jsonData, remoteEndpoint);
                        break;
                    case "weapon_pickup":
                        HandleWeaponPickup(jsonData, remoteEndpoint);
                        break;
                    case "chat_message":
                        HandleChatMessage(jsonData, remoteEndpoint);
                        break;
                    case "ping":
                        HandlePing(jsonData, remoteEndpoint);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server] Packet processing error: {ex.Message}");
            }
        }

        #region Authentication Handlers

        private void HandleLogin(string jsonData, IPEndPoint endpoint)
        {
            var request = JsonConvert.DeserializeObject<LoginRequest>(jsonData);

            // Simulate database check (replace with actual DB)
            var player = GetPlayerByUsername(request.username);

            LoginResponse response = new LoginResponse();

            if (player != null && player.Password == request.password) // In production, use proper password hashing
            {
                // Create or update client connection
                var client = clients.GetOrAdd(player.PlayerId, new ClientConnection
                {
                    PlayerId = player.PlayerId,
                    Username = player.Username,
                    Endpoint = endpoint,
                    SessionToken = Guid.NewGuid().ToString(),
                    LastHeartbeat = DateTime.UtcNow
                });

                response.type = "login_response";
                response.success = true;
                response.playerId = player.PlayerId;
                response.token = client.SessionToken;
                response.message = "Login successful";
                response.timestamp = DateTime.UtcNow;

                Console.WriteLine($"[Server] Player {player.Username} (ID: {player.PlayerId}) logged in");
            }
            else
            {
                response.type = "login_response";
                response.success = false;
                response.message = "Invalid username or password";
                response.timestamp = DateTime.UtcNow;
            }

            SendPacket(response, endpoint);
        }

        private void HandleRegister(string jsonData, IPEndPoint endpoint)
        {
            var request = JsonConvert.DeserializeObject<RegisterRequest>(jsonData);

            // Check if username exists
            var existingPlayer = GetPlayerByUsername(request.username);

            RegisterResponse response = new RegisterResponse();

            if (existingPlayer != null)
            {
                response.type = "register_response";
                response.success = false;
                response.message = "Username already exists";
                response.timestamp = DateTime.UtcNow;
            }
            else
            {
                // Create new player (simulate database insert)
                var newPlayer = new PlayerData
                {
                    PlayerId = Interlocked.Increment(ref nextPlayerId),
                    Username = request.username,
                    Password = request.password, // In production, hash this!
                    Email = request.email,
                    Level = 1,
                    Experience = 0,
                    Gold = 500,
                    MMR = 1200,
                    Rank = 3,
                    CreatedAt = DateTime.UtcNow,
                    LastLogin = DateTime.UtcNow
                };

                SavePlayer(newPlayer);

                response.type = "register_response";
                response.success = true;
                response.message = "Registration successful";
                response.timestamp = DateTime.UtcNow;

                Console.WriteLine($"[Server] New player registered: {request.username} (ID: {newPlayer.PlayerId})");
            }

            SendPacket(response, endpoint);
        }

        private void HandleGetProfile(string jsonData, IPEndPoint endpoint)
        {
            var request = JsonConvert.DeserializeObject<GetProfileRequest>(jsonData);
            var client = GetClientByToken(request.token);

            if (client == null)
            {
                SendError(endpoint, "Invalid session", 401);
                return;
            }

            var player = GetPlayerById(request.playerId);

            if (player == null)
            {
                SendError(endpoint, "Player not found", 404);
                return;
            }

            var profileResponse = new ProfileDataPacket
            {
                type = "profile_data",
                profile = new PlayerProfile
                {
                    PlayerId = player.PlayerId,
                    Username = player.Username,
                    Email = player.Email,
                    Level = player.Level,
                    Experience = player.Experience,
                    ExperienceToNextLevel = CalculateExpToNextLevel(player.Level),
                    Gold = player.Gold,
                    MMR = player.MMR,
                    Rank = player.Rank,
                    TotalStats = player.TotalStats,
                    Inventory = player.Inventory,
                    Loadout = player.Loadout,
                    TotalPlayTime = player.TotalPlayTime,
                    CreatedAt = player.CreatedAt,
                    LastLogin = player.LastLogin
                },
                timestamp = DateTime.UtcNow
            };

            SendPacket(profileResponse, endpoint);
        }

        #endregion

        #region Matchmaking Handlers

        private void HandleJoinQueue(string jsonData, IPEndPoint endpoint)
        {
            var request = JsonConvert.DeserializeObject<JoinQueueRequest>(jsonData);
            var client = GetClientByToken(request.token);

            if (client == null)
            {
                SendError(endpoint, "Invalid session", 401);
                return;
            }

            if (client.InQueue)
            {
                SendQueueResponse(endpoint, false, "Already in queue", 0, 0, 0, 0);
                return;
            }

            var player = GetPlayerById(client.PlayerId);
            var matchmakingPlayer = new PlayerMatchmaking
            {
                PlayerId = client.PlayerId,
                Username = client.Username,
                MMR = player.MMR,
                JoinedAt = DateTime.UtcNow,
                Endpoint = endpoint
            };

            matchmakingQueue.Enqueue(matchmakingPlayer);
            client.InQueue = true;
            client.JoinedQueueAt = DateTime.UtcNow;

            Console.WriteLine($"[Server] {client.Username} joined matchmaking queue. Queue size: {matchmakingQueue.Count}");
            SendQueueResponse(endpoint, true, "Joined queue", 0, GetQueuePosition(client.PlayerId), matchmakingQueue.Count, CalculateEstimatedWaitTime());
        }

        private void HandleLeaveQueue(string jsonData, IPEndPoint endpoint)
        {
            var request = JsonConvert.DeserializeObject<LeaveQueueRequest>(jsonData);
            var client = GetClientByToken(request.token);

            if (client == null)
            {
                SendError(endpoint, "Invalid session", 401);
                return;
            }

            if (!client.InQueue)
            {
                return;
            }

            // Remove from queue
            var tempList = new List<PlayerMatchmaking>();
            while (matchmakingQueue.TryDequeue(out var queuedPlayer))
            {
                if (queuedPlayer.PlayerId != client.PlayerId)
                {
                    tempList.Add(queuedPlayer);
                }
            }

            foreach (var player in tempList)
            {
                matchmakingQueue.Enqueue(player);
            }

            client.InQueue = false;
            Console.WriteLine($"[Server] {client.Username} left matchmaking queue");
            SendQueueResponse(endpoint, true, "Left queue", 0, 0, matchmakingQueue.Count, 0);
        }

        private void HandleGetQueueStatus(string jsonData, IPEndPoint endpoint)
        {
            var request = JsonConvert.DeserializeObject<GetQueueStatusRequest>(jsonData);
            var client = GetClientByToken(request.token);

            if (client == null)
            {
                SendError(endpoint, "Invalid session", 401);
                return;
            }

            var queueStatus = new QueueStatusData
            {
                type = "queue_status",
                queueSize = matchmakingQueue.Count,
                position = GetQueuePosition(client.PlayerId),
                estimatedWaitTime = CalculateEstimatedWaitTime(),
                playersNeeded = PLAYERS_PER_GAME - (matchmakingQueue.Count % PLAYERS_PER_GAME),
                inQueue = client.InQueue,
                timestamp = DateTime.UtcNow
            };

            SendPacket(queueStatus, endpoint);
        }

        private void MatchmakingLoop()
        {
            while (isRunning)
            {
                Thread.Sleep(MATCHMAKING_INTERVAL);

                if (matchmakingQueue.Count >= PLAYERS_PER_GAME)
                {
                    var players = new List<PlayerMatchmaking>();
                    int taken = 0;

                    while (taken < PLAYERS_PER_GAME && matchmakingQueue.TryDequeue(out var player))
                    {
                        players.Add(player);
                        taken++;
                    }

                    if (players.Count == PLAYERS_PER_GAME)
                    {
                        CreateGame(players);
                    }
                    else
                    {
                        // If not enough players, put them back
                        foreach (var player in players)
                        {
                            matchmakingQueue.Enqueue(player);
                        }
                    }
                }
            }
        }

        private void CreateGame(List<PlayerMatchmaking> players)
        {
            int gameId = Interlocked.Increment(ref nextGameId);

            // Sort by MMR for balanced teams
            players = players.OrderBy(p => p.MMR).ToList();

            var redTeam = new List<ClientConnection>();
            var blueTeam = new List<ClientConnection>();

            // Snake draft for balanced teams
            for (int i = 0; i < players.Count; i++)
            {
                var client = GetClientById(players[i].PlayerId);
                if (i % 2 == 0)
                    redTeam.Add(client);
                else
                    blueTeam.Add(client);
            }

            var gameRoom = new GameRoom
            {
                GameId = gameId,
                RedTeam = redTeam,
                BlueTeam = blueTeam,
                StartTime = DateTime.UtcNow,
                TimeRemaining = GAME_DURATION,
                RedScore = 0,
                BlueScore = 0,
                IsActive = true
            };

            // Initialize player stats for this game
            foreach (var player in redTeam.Concat(blueTeam))
            {
                gameRoom.PlayerStats[player.PlayerId] = new InGameStats
                {
                    Kills = 0,
                    Deaths = 0,
                    Score = 0,
                    TeamId = redTeam.Contains(player) ? 0 : 1
                };
            }

            activeGames.TryAdd(gameId, gameRoom);

            // Notify all players
            foreach (var player in redTeam.Concat(blueTeam))
            {
                player.InQueue = false;
                player.InGame = true;
                player.CurrentGameId = gameId;
                player.TeamId = redTeam.Contains(player) ? 0 : 1;

                var gameStartData = new GameStartData
                {
                    type = "game_start",
                    gameId = gameId,
                    teamId = player.TeamId,
                    players = GetGamePlayerDataList(redTeam, blueTeam),
                    timestamp = DateTime.UtcNow
                };

                SendPacket(gameStartData, player.Endpoint);
                Console.WriteLine($"[Server] Game {gameId} started for {player.Username} on team {(player.TeamId == 0 ? "Red" : "Blue")}");
            }

            Console.WriteLine($"[Server] Game {gameId} created! Red Team: {redTeam.Count}, Blue Team: {blueTeam.Count}");
        }

        #endregion

        #region Game Logic Handlers

        private async Task GameUpdateLoop()
        {
            var lastTick = DateTime.UtcNow;

            while (isRunning)
            {
                var now = DateTime.UtcNow;
                var deltaTime = (now - lastTick).TotalSeconds;

                if (deltaTime >= TICK_TIME)
                {
                    lastTick = now;
                    await UpdateGames();
                }

                await Task.Delay(16); // ~60 FPS
            }
        }

        private async Task UpdateGames()
        {
            foreach (var game in activeGames.Values)
            {
                if (!game.IsActive)
                    continue;

                // Update game timer
                var elapsed = (DateTime.UtcNow - game.StartTime).TotalSeconds;
                game.TimeRemaining = GAME_DURATION - elapsed;

                // Check for game end
                if (game.TimeRemaining <= 0 || game.RedScore >= 10 || game.BlueScore >= 10)
                {
                    await EndGame(game);
                    continue;
                }

                // Send game state to all players
                await SendGameState(game);
            }
        }

        private async Task SendGameState(GameRoom game)
        {
            foreach (var player in game.RedTeam.Concat(game.BlueTeam))
            {
                if (!player.InGame)
                    continue;

                var playerStats = game.PlayerStats[player.PlayerId];

                var gameState = new GameStateData
                {
                    type = "game_state",
                    gameId = game.GameId,
                    timeRemaining = (int)Math.Max(0, game.TimeRemaining),
                    redScore = game.RedScore,
                    blueScore = game.BlueScore,
                    playerStats = new PlayerStatsData
                    {
                        kills = playerStats.Kills,
                        deaths = playerStats.Deaths,
                        score = playerStats.Score
                    },
                    otherPlayers = GetOtherPlayersData(game, player.PlayerId),
                    timestamp = DateTime.UtcNow
                };

                SendPacket(gameState, player.Endpoint);
            }
        }

        private async Task EndGame(GameRoom game)
        {
            game.IsActive = false;

            // Calculate results for each player
            foreach (var player in game.RedTeam.Concat(game.BlueTeam))
            {
                var stats = game.PlayerStats[player.PlayerId];
                bool isWinner = (player.TeamId == 0 && game.RedScore > game.BlueScore) ||
                               (player.TeamId == 1 && game.BlueScore > game.RedScore);

                int goldReward = 100 + (stats.Kills * 10) + (isWinner ? 50 : 0);
                int xpReward = 50 + (stats.Kills * 5) + (isWinner ? 25 : 0);

                // Update player stats (in real implementation, save to database)
                var playerData = GetPlayerById(player.PlayerId);
                if (playerData != null)
                {
                    playerData.Gold += goldReward;
                    playerData.Experience += xpReward;

                    // Check level up
                    while (playerData.Experience >= CalculateExpToNextLevel(playerData.Level))
                    {
                        playerData.Experience -= CalculateExpToNextLevel(playerData.Level);
                        playerData.Level++;
                        Console.WriteLine($"[Server] {player.Username} leveled up to {playerData.Level}!");
                    }

                    // Update total stats
                    if (playerData.TotalStats == null)
                        playerData.TotalStats = new PlayerStats();

                    playerData.TotalStats.TotalMatches++;
                    if (isWinner) playerData.TotalStats.TotalWins++;
                    else playerData.TotalStats.TotalLosses++;
                    playerData.TotalStats.TotalKills += stats.Kills;
                    playerData.TotalStats.TotalDeaths += stats.Deaths;

                    if (playerData.TotalStats.TotalMatches > 0)
                    {
                        playerData.TotalStats.WinRate = (float)playerData.TotalStats.TotalWins / playerData.TotalStats.TotalMatches * 100f;
                        playerData.TotalStats.KDRatio = playerData.TotalStats.TotalDeaths > 0 ?
                            (float)playerData.TotalStats.TotalKills / playerData.TotalStats.TotalDeaths :
                            playerData.TotalStats.TotalKills;
                    }

                    SavePlayer(playerData);
                }

                var result = new MatchResult
                {
                    IsWin = isWinner,
                    GoldReward = goldReward,
                    ExperienceReward = xpReward,
                    Kills = stats.Kills,
                    Deaths = stats.Deaths
                };

                var gameEndData = new
                {
                    type = "game_end",
                    result = result,
                    timestamp = DateTime.UtcNow
                };

                SendPacket(gameEndData, player.Endpoint);

                // Reset player state
                player.InGame = false;
                player.CurrentGameId = -1;
                player.TeamId = -1;

                Console.WriteLine($"[Server] Game {game.GameId} ended for {player.Username}: {(isWinner ? "WINNER" : "LOSER")} - K/D: {stats.Kills}/{stats.Deaths}, Gold: +{goldReward}, XP: +{xpReward}");
            }

            // Remove game
            activeGames.TryRemove(game.GameId, out _);
        }

        private void HandleMovementInput(string jsonData, IPEndPoint endpoint)
        {
            var input = JsonConvert.DeserializeObject<MovementInput>(jsonData);
            var client = GetClientByToken(input.token);

            if (client == null || !client.InGame)
                return;

            // Update player position in game
            if (activeGames.TryGetValue(client.CurrentGameId, out var game))
            {
                // Store position for other players
                game.PlayerPositions[client.PlayerId] = new Vector3Data
                {
                    x = input.positionX,
                    y = input.positionY,
                    z = input.positionZ
                };
            }
        }

        private void HandleShootInput(string jsonData, IPEndPoint endpoint)
        {
            var input = JsonConvert.DeserializeObject<ShootInput>(jsonData);
            var client = GetClientByToken(input.token);

            if (client == null || !client.InGame)
                return;

            if (!activeGames.TryGetValue(client.CurrentGameId, out var game))
                return;

            // Get shooter position
            var shooterPos = game.PlayerPositions.ContainsKey(client.PlayerId)
                ? game.PlayerPositions[client.PlayerId]
                : new Vector3Data();

            // Simple hit detection (replace with proper raycasting in production)
            foreach (var target in game.RedTeam.Concat(game.BlueTeam))
            {
                if (target.PlayerId == client.PlayerId || target.TeamId == client.TeamId)
                    continue;

                if (!game.PlayerPositions.ContainsKey(target.PlayerId))
                    continue;

                var targetPos = game.PlayerPositions[target.PlayerId];
                var targetPoint = new Vector3Data { x = input.targetX, y = input.targetY, z = input.targetZ };

                float distanceToTarget = CalculateDistance(shooterPos, targetPos);
                float distanceToShot = CalculateDistance(shooterPos, targetPoint);

                // Check if shot is close to target (simplified hit detection)
                if (distanceToTarget < 15f && distanceToShot < 15f)
                {
                    // Calculate damage
                    int damage = CalculateDamage(input.weaponId, distanceToTarget, false);
                    bool isHeadshot = UnityEngine.Random.value < 0.1f; // 10% headshot chance (simplified)
                    if (isHeadshot) damage *= 2;

                    // Create hit data
                    var hitData = new HitData
                    {
                        type = "player_hit",
                        attackerId = client.PlayerId,
                        attackerName = client.Username,
                        targetId = target.PlayerId,
                        targetName = target.Username,
                        damage = damage,
                        weaponId = input.weaponId,
                        hitPoint = targetPos,
                        hitNormal = new Vector3Data { x = 0, y = 1, z = 0 },
                        isHeadshot = isHeadshot,
                        isKill = false,
                        timestamp = DateTime.UtcNow
                    };

                    // Apply damage
                    var targetStats = game.PlayerStats[target.PlayerId];
                    var shooterStats = game.PlayerStats[client.PlayerId];

                    // Check if target has health state
                    int targetHealth = 100;
                    if (game.PlayerStates.ContainsKey(target.PlayerId))
                    {
                        targetHealth = game.PlayerStates[target.PlayerId].health;
                        targetHealth -= damage;
                        game.PlayerStates[target.PlayerId].health = targetHealth;
                    }
                    else
                    {
                        targetHealth -= damage;
                        game.PlayerStates[target.PlayerId] = new PlayerStateInfo
                        {
                            health = targetHealth,
                            isAlive = targetHealth > 0
                        };
                    }

                    // Check for kill
                    if (targetHealth <= 0)
                    {
                        hitData.isKill = true;

                        // Update stats
                        targetStats.Deaths++;
                        shooterStats.Kills++;
                        shooterStats.Score += 100;

                        if (isHeadshot)
                        {
                            shooterStats.Score += 50; // Bonus for headshot
                        }

                        // Update team score
                        if (client.TeamId == 0)
                            game.RedScore++;
                        else
                            game.BlueScore++;

                        // Create kill data
                        var killData = new KillData
                        {
                            type = "player_killed",
                            killerId = client.PlayerId,
                            killerName = client.Username,
                            victimId = target.PlayerId,
                            victimName = target.Username,
                            weaponId = input.weaponId,
                            isHeadshot = isHeadshot,
                            killerTeamId = client.TeamId,
                            victimTeamId = target.TeamId,
                            killerScore = shooterStats.Score,
                            victimScore = targetStats.Score,
                            timestamp = DateTime.UtcNow
                        };

                        // Broadcast kill to all players in game
                        foreach (var player in game.RedTeam.Concat(game.BlueTeam))
                        {
                            if (!player.InGame) continue;
                            SendPacket(killData, player.Endpoint);
                        }

                        Console.WriteLine($"[Server] {client.Username} killed {target.Username}! Score: {game.RedScore}-{game.BlueScore}");

                        // Handle player death
                        HandlePlayerDeath(target, game);
                    }

                    // Broadcast hit to all players in game
                    foreach (var player in game.RedTeam.Concat(game.BlueTeam))
                    {
                        if (!player.InGame) continue;
                        SendPacket(hitData, player.Endpoint);
                    }

                    break; // Only hit one target per shot
                }
            }
        }


        private void HandleAbilityInput(string jsonData, IPEndPoint endpoint)
        {
            var input = JsonConvert.DeserializeObject<AbilityInput>(jsonData);
            var client = GetClientByToken(input.token);

            if (client == null || !client.InGame)
                return;

            // Process ability logic
            Console.WriteLine($"[Server] {client.Username} used ability: {input.abilityName}");
        }
        private void HandlePlayerState(string jsonData, IPEndPoint endpoint)
        {
            var state = JsonConvert.DeserializeObject<PlayerStateData>(jsonData);
            var client = GetClientByToken(state.token);

            if (client == null || !client.InGame)
                return;

            // Update player state in game
            if (activeGames.TryGetValue(client.CurrentGameId, out var game))
            {
                // Update position
                game.PlayerPositions[client.PlayerId] = new Vector3Data
                {
                    x = state.positionX,
                    y = state.positionY,
                    z = state.positionZ
                };

                // Update player state (health, ammo, etc.)
                if (game.PlayerStates.ContainsKey(client.PlayerId))
                {
                    var playerState = game.PlayerStates[client.PlayerId];
                    playerState.health = state.health;
                    playerState.currentAmmo = state.currentAmmo;
                    playerState.isReloading = state.isReloading;
                    playerState.isCrouching = state.isCrouching;
                    playerState.isSprinting = state.isSprinting;
                    playerState.isAiming = state.isAiming;
                }

                // Broadcast state to other players
                foreach (var player in game.RedTeam.Concat(game.BlueTeam))
                {
                    if (player.PlayerId == client.PlayerId || !player.InGame)
                        continue;

                    var otherPlayerData = new OtherPlayerData
                    {
                        playerId = client.PlayerId,
                        username = client.Username,
                        teamId = client.TeamId,
                        position = game.PlayerPositions[client.PlayerId],
                        health = state.health,
                        isAlive = state.health > 0
                    };

                    var stateUpdate = new
                    {
                        type = "player_state_update",
                        player = otherPlayerData,
                        timestamp = DateTime.UtcNow
                    };

                    SendPacket(stateUpdate, player.Endpoint);
                }
            }
        }

        private void HandlePlayerRespawn(string jsonData, IPEndPoint endpoint)
        {
            var respawn = JsonConvert.DeserializeObject<PlayerRespawnData>(jsonData);
            var client = GetClientByToken(respawn.token);

            if (client == null || !client.InGame)
                return;

            if (activeGames.TryGetValue(client.CurrentGameId, out var game))
            {
                // Get spawn position based on team
                var spawnPos = GetSpawnPosition(client.TeamId, game);
                respawn.spawnPosition = spawnPos;

                // Reset player stats for respawn
                if (game.PlayerStates.ContainsKey(client.PlayerId))
                {
                    game.PlayerStates[client.PlayerId].health = 100;
                    game.PlayerStates[client.PlayerId].isAlive = true;
                }

                // Notify all players about respawn
                var spawnData = new PlayerSpawnData
                {
                    type = "player_spawned",
                    playerId = client.PlayerId,
                    username = client.Username,
                    teamId = client.TeamId,
                    position = spawnPos,
                    health = 100,
                    loadout = GetPlayerById(client.PlayerId)?.Loadout ?? new PlayerLoadout(),
                    timestamp = DateTime.UtcNow
                };

                foreach (var player in game.RedTeam.Concat(game.BlueTeam))
                {
                    if (!player.InGame) continue;
                    SendPacket(spawnData, player.Endpoint);
                }

                Console.WriteLine($"[Server] {client.Username} respawned in game {game.GameId}");
            }
        }

        private void HandleWeaponPickup(string jsonData, IPEndPoint endpoint)
        {
            var pickup = JsonConvert.DeserializeObject<WeaponPickupData>(jsonData);
            var client = GetClientByToken(pickup.token);

            if (client == null || !client.InGame)
                return;

            // Validate pickup (in production, check if weapon exists at position, etc.)
            var playerData = GetPlayerById(client.PlayerId);
            if (playerData != null)
            {
                // Add weapon to inventory if not already owned
                if (!playerData.Inventory.OwnedWeapons.Contains(pickup.weaponId))
                {
                    playerData.Inventory.OwnedWeapons.Add(pickup.weaponId);
                    SavePlayer(playerData);
                }

                Console.WriteLine($"[Server] {client.Username} picked up weapon {pickup.weaponId} with {pickup.ammoAmount} ammo");
            }

            // Confirm pickup to client
            var confirmPickup = new
            {
                type = "weapon_pickup_confirm",
                weaponId = pickup.weaponId,
                ammoAmount = pickup.ammoAmount,
                timestamp = DateTime.UtcNow
            };

            SendPacket(confirmPickup, endpoint);
        }

        private void HandleChatMessage(string jsonData, IPEndPoint endpoint)
        {
            var chat = JsonConvert.DeserializeObject<ChatMessage>(jsonData);
            var client = GetClientByToken(chat.token);

            if (client == null)
                return;

            chat.playerId = client.PlayerId;
            chat.username = client.Username;
            chat.timestamp = DateTime.UtcNow;

            // Broadcast message
            if (chat.teamId == -1)
            {
                // All chat - send to all connected players
                foreach (var c in clients.Values)
                {
                    SendPacket(chat, c.Endpoint);
                }
            }
            else
            {
                // Team chat - only send to teammates in same game
                if (client.InGame && activeGames.TryGetValue(client.CurrentGameId, out var game))
                {
                    var team = chat.teamId == 0 ? game.RedTeam : game.BlueTeam;
                    foreach (var teammate in team)
                    {
                        if (teammate.InGame)
                        {
                            SendPacket(chat, teammate.Endpoint);
                        }
                    }
                }
            }
        }

        private void HandlePing(string jsonData, IPEndPoint endpoint)
        {
            var ping = JsonConvert.DeserializeObject<PingData>(jsonData);
            var client = GetClientByToken(ping.token);

            if (client == null || !client.InGame)
                return;

            ping.playerId = client.PlayerId;

            // Broadcast ping to team
            if (activeGames.TryGetValue(client.CurrentGameId, out var game))
            {
                var team = client.TeamId == 0 ? game.RedTeam : game.BlueTeam;
                foreach (var teammate in team)
                {
                    if (teammate.InGame)
                    {
                        SendPacket(ping, teammate.Endpoint);
                    }
                }
            }
        }
        private void HandlePlayerDeath(ClientConnection victim, GameRoom game)
        {
            // Mark player as dead
            victim.IsDead = true;
            game.PlayerStates[victim.PlayerId].isAlive = false;

            // Notify victim
            var deathData = new
            {
                type = "player_death",
                respawnTime = 5f,
                timestamp = DateTime.UtcNow
            };
            SendPacket(deathData, victim.Endpoint);
        }

        private int CalculateDamage(int weaponId, float distance, bool isHeadshot)
        {
            // Base damage by weapon type (simplified)
            int baseDamage = weaponId switch
            {
                1 => 25, // Assault Rifle
                2 => 35, // Pistol
                3 => 50, // Shotgun (close range)
                4 => 70, // Sniper
                5 => 20, // SMG
                _ => 15
            };

            // Distance falloff
            float falloff = Mathf.Clamp01(1f - (distance / 100f));
            int finalDamage = Mathf.RoundToInt(baseDamage * (0.5f + falloff * 0.5f));

            // Headshot multiplier
            if (isHeadshot)
                finalDamage *= 2;

            return finalDamage;
        }

        private Vector3Data GetSpawnPosition(int teamId, GameRoom game)
        {
            // Return predetermined spawn positions (in production, use map-specific spawn points)
            if (teamId == 0)
            {
                return new Vector3Data { x = -10f, y = 1f, z = 0f };
            }
            else
            {
                return new Vector3Data { x = 10f, y = 1f, z = 0f };
            }
        }

        #endregion

        #region Helper Methods

        private void HandleHeartbeat(string jsonData, IPEndPoint endpoint)
        {
            var heartbeat = JsonConvert.DeserializeObject<HeartbeatPacket>(jsonData);
            var client = GetClientByToken(heartbeat.token);

            if (client != null)
            {
                client.LastHeartbeat = DateTime.UtcNow;
            }
        }

        private void HandleLogout(string jsonData, IPEndPoint endpoint)
        {
            var request = JsonConvert.DeserializeObject<LogoutRequest>(jsonData);
            var client = GetClientByToken(request.token);

            if (client != null)
            {
                // Remove from queue if in queue
                if (client.InQueue)
                {
                    HandleLeaveQueue(jsonData, endpoint);
                }

                clients.TryRemove(client.PlayerId, out _);
                Console.WriteLine($"[Server] {client.Username} logged out");
            }
        }

        private ClientConnection GetClientByToken(string token)
        {
            return clients.Values.FirstOrDefault(c => c.SessionToken == token);
        }

        private ClientConnection GetClientById(int playerId)
        {
            clients.TryGetValue(playerId, out var client);
            return client;
        }

        private int GetQueuePosition(int playerId)
        {
            int position = 1;
            foreach (var player in matchmakingQueue)
            {
                if (player.PlayerId == playerId)
                    return position;
                position++;
            }
            return 0;
        }

        private int CalculateEstimatedWaitTime()
        {
            int playersNeeded = PLAYERS_PER_GAME - (matchmakingQueue.Count % PLAYERS_PER_GAME);
            return playersNeeded * 30; // Rough estimate: 30 seconds per player needed
        }

        private List<GamePlayerData> GetGamePlayerDataList(List<ClientConnection> redTeam, List<ClientConnection> blueTeam)
        {
            var players = new List<GamePlayerData>();

            foreach (var player in redTeam)
            {
                var data = GetPlayerById(player.PlayerId);
                players.Add(new GamePlayerData
                {
                    playerId = player.PlayerId,
                    username = player.Username,
                    level = data.Level,
                    mmr = data.MMR,
                    rank = data.Rank,
                    teamId = 0,
                    loadout = data.Loadout ?? new PlayerLoadout()
                });
            }

            foreach (var player in blueTeam)
            {
                var data = GetPlayerById(player.PlayerId);
                players.Add(new GamePlayerData
                {
                    playerId = player.PlayerId,
                    username = player.Username,
                    level = data.Level,
                    mmr = data.MMR,
                    rank = data.Rank,
                    teamId = 1,
                    loadout = data.Loadout ?? new PlayerLoadout()
                });
            }

            return players;
        }

        private List<OtherPlayerData> GetOtherPlayersData(GameRoom game, int currentPlayerId)
        {
            var others = new List<OtherPlayerData>();

            foreach (var player in game.RedTeam.Concat(game.BlueTeam))
            {
                if (player.PlayerId == currentPlayerId)
                    continue;

                others.Add(new OtherPlayerData
                {
                    playerId = player.PlayerId,
                    username = player.Username,
                    teamId = player.TeamId,
                    position = game.PlayerPositions.ContainsKey(player.PlayerId) ?
                               game.PlayerPositions[player.PlayerId] : new Vector3Data(),
                    health = 100, // Simplified
                    isAlive = true
                });
            }

            return others;
        }

        private float CalculateDistance(Vector3Data pos1, Vector3Data pos2)
        {
            float dx = pos1.x - pos2.x;
            float dy = pos1.y - pos2.y;
            float dz = pos1.z - pos2.z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private int CalculateExpToNextLevel(int level)
        {
            return 100 + (level * 50); // Example formula
        }

        private void SendPacket(object packet, IPEndPoint endpoint)
        {
            try
            {
                string jsonData = JsonConvert.SerializeObject(packet);
                byte[] data = Encoding.UTF8.GetBytes(jsonData);
                udpServer.Send(data, data.Length, endpoint);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server] Send error: {ex.Message}");
            }
        }

        private void SendError(IPEndPoint endpoint, string message, int errorCode)
        {
            var error = new ErrorData
            {
                type = "error",
                message = message,
                errorCode = errorCode,
                timestamp = DateTime.UtcNow
            };
            SendPacket(error, endpoint);
        }

        private void SendQueueResponse(IPEndPoint endpoint, bool success, string message, int gameId, int position, int queueSize, int estimatedWait)
        {
            var response = new QueueResponseData
            {
                type = "queue_response",
                success = success,
                message = message,
                queueSize = queueSize,
                position = position,
                estimatedWaitTime = estimatedWait,
                playersNeeded = PLAYERS_PER_GAME - (queueSize % PLAYERS_PER_GAME)
            };
            SendPacket(response, endpoint);
        }

        #endregion

        #region Database Simulation (Replace with actual database)

        private ConcurrentDictionary<int, PlayerData> playerDatabase = new ConcurrentDictionary<int, PlayerData>();

        private void LoadPlayerData()
        {
            // Add some test players
            var testPlayer = new PlayerData
            {
                PlayerId = 1000,
                Username = "player1",
                Password = "password123",
                Email = "player1@test.com",
                Level = 5,
                Experience = 500,
                Gold = 500,
                MMR = 1200,
                Rank = 3,
                TotalStats = new PlayerStats
                {
                    TotalMatches = 50,
                    TotalWins = 30,
                    TotalLosses = 20,
                    TotalKills = 250,
                    TotalDeaths = 150,
                    //WinRate = 60f,
                    //KDRatio = 1.666f
                },
                Inventory = new PlayerInventory
                {
                    OwnedWeapons = new List<int> { 1, 2, 3, 4, 5, 6 },
                    OwnedSkins = new List<int> { 1, 2, 3 },
                    OwnedGrenades = new List<int> { 4, 5 },
                    //LootBoxes = new List<object>()
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
                CreatedAt = DateTime.UtcNow.AddDays(-30),
                LastLogin = DateTime.UtcNow
            };

            playerDatabase.TryAdd(1000, testPlayer);
        }

        private PlayerData GetPlayerByUsername(string username)
        {
            return playerDatabase.Values.FirstOrDefault(p => p.Username == username);
        }

        private PlayerData GetPlayerById(int playerId)
        {
            playerDatabase.TryGetValue(playerId, out var player);
            return player;
        }

        private void SavePlayer(PlayerData player)
        {
            playerDatabase.AddOrUpdate(player.PlayerId, player, (id, old) => player);
        }

        #endregion
    }

    #region Data Models

    public class ClientConnection
    {
        public int PlayerId { get; set; }
        public string Username { get; set; }
        public IPEndPoint Endpoint { get; set; }
        public string SessionToken { get; set; }
        public DateTime LastHeartbeat { get; set; }
        public bool InQueue { get; set; }
        public DateTime JoinedQueueAt { get; set; }
        public bool InGame { get; set; }
        public int CurrentGameId { get; set; }
        public int TeamId { get; set; }
        public bool IsDead { get; set; } = false;
    }

    public class PlayerMatchmaking
    {
        public int PlayerId { get; set; }
        public string Username { get; set; }
        public int MMR { get; set; }
        public DateTime JoinedAt { get; set; }
        public IPEndPoint Endpoint { get; set; }
    }

    public class GameRoom
    {
        public int GameId { get; set; }
        public List<ClientConnection> RedTeam { get; set; }
        public List<ClientConnection> BlueTeam { get; set; }
        public DateTime StartTime { get; set; }
        public double TimeRemaining { get; set; }
        public int RedScore { get; set; }
        public int BlueScore { get; set; }
        public bool IsActive { get; set; }
        public Dictionary<int, InGameStats> PlayerStats { get; set; } = new Dictionary<int, InGameStats>();
        public Dictionary<int, Vector3Data> PlayerPositions { get; set; } = new Dictionary<int, Vector3Data>();
        public Dictionary<int, PlayerStateInfo> PlayerStates { get; set; } = new Dictionary<int, PlayerStateInfo>();
    }

    public class InGameStats
    {
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public int Score { get; set; }
        public int TeamId { get; set; }
    }

    public class PlayerData
    {
        public int PlayerId { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string Email { get; set; }
        public int Level { get; set; }
        public int Experience { get; set; }
        public int Gold { get; set; }
        public int MMR { get; set; }
        public int Rank { get; set; }
        public PlayerStats TotalStats { get; set; }
        public PlayerInventory Inventory { get; set; }
        public PlayerLoadout Loadout { get; set; }
        public TimeSpan TotalPlayTime { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastLogin { get; set; }
    }

    // Request/Response classes
    public class LoginRequest
    {
        public string type;
        public string username;
        public string password;
        public DateTime timestamp;
    }

    public class RegisterRequest
    {
        public string type;
        public string username;
        public string password;
        public string email;
        public DateTime timestamp;
    }

    public class GetProfileRequest
    {
        public string type;
        public string token;
        public int playerId;
    }

    public class JoinQueueRequest
    {
        public string type;
        public string token;
    }

    public class LeaveQueueRequest
    {
        public string type;
        public string token;
    }

    public class GetQueueStatusRequest
    {
        public string type;
        public string token;
    }

    public class LogoutRequest
    {
        public string type;
        public string token;
    }

    public class HeartbeatPacket
    {
        public string type;
        public string token;
        public DateTime timestamp;
    }

    public class MovementInput
    {
        public string type;
        public string token;
        public float positionX;
        public float positionY;
        public float positionZ;
        public float rotation;
    }

    public class ShootInput
    {
        public string type;
        public string token;
        public float targetX;
        public float targetY;
        public float targetZ;
    }

    public class AbilityInput
    {
        public string type;
        public string token;
        public string abilityName;
    }
    // Add this class inside GameServer or in a separate file
    public class PlayerStateInfo
    {
        public int health = 100;
        public bool isAlive = true;
        public int currentAmmo = 30;
        public bool isReloading = false;
        public bool isCrouching = false;
        public bool isSprinting = false;
        public bool isAiming = false;
    }
    #endregion
}

