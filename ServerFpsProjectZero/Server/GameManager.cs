using Newtonsoft.Json;
using ServerFpsProjectZero.Models;
using ServerFpsProjectZero.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ServerFpsProjectZero.Server
{
    public class GameManager
    {
        private ServerManager serverManager;
        private ConcurrentDictionary<int, GameRoom> activeGames;
        private ConcurrentQueue<PlayerMatchmaking> matchmakingQueue;
        private int nextGameId = 1;
        private bool isRunning = false;
        private Thread matchmakingThread;
        private Thread gameUpdateThread;

        // Configuration
        private const int TICK_RATE = 20;
        private const float TICK_TIME = 1f / TICK_RATE;
        private const int MATCHMAKING_INTERVAL = 2000;
        private const int PLAYERS_PER_GAME = 2;
        private const float GAME_DURATION = 600f;

        public GameManager(ServerManager serverManager)
        {
            this.serverManager = serverManager;
            activeGames = new ConcurrentDictionary<int, GameRoom>();
            matchmakingQueue = new ConcurrentQueue<PlayerMatchmaking>();

            // Subscribe to game-related events only
            SubscribeToEvents();
        }

        public void Start()
        {
            isRunning = true;

            matchmakingThread = new Thread(MatchmakingLoop);
            matchmakingThread.IsBackground = true;
            matchmakingThread.Start();

            gameUpdateThread = new Thread(GameUpdateLoop);
            gameUpdateThread.IsBackground = true;
            gameUpdateThread.Start();

            Console.WriteLine($"[GameManager] Started");
            Console.WriteLine($"[GameManager] Players per game: {PLAYERS_PER_GAME}");
            Console.WriteLine($"[GameManager] Game duration: {GAME_DURATION} seconds");
        }

        public void Stop()
        {
            isRunning = false;
            matchmakingThread?.Join(1000);
            gameUpdateThread?.Join(1000);

            // End all active games
            foreach (var game in activeGames.Values)
            {
                game.IsActive = false;
            }
            activeGames.Clear();

            Console.WriteLine("[GameManager] Stopped");
        }

        #region Event Subscriptions

        private void SubscribeToEvents()
        {
            serverManager.OnJoinQueuePacket += HandleJoinQueue;
            serverManager.OnLeaveQueuePacket += HandleLeaveQueue;
            serverManager.OnGetQueueStatusPacket += HandleGetQueueStatus;
            serverManager.OnMovementPacket += HandleMovementInput;
            serverManager.OnShootPacket += HandleShootInput;
            serverManager.OnAbilityPacket += HandleAbilityInput;
            serverManager.OnPlayerStatePacket += HandlePlayerState;
            serverManager.OnPlayerDeathPacket += HandlePlayerDeathPacket;
            serverManager.OnPlayerRespawnPacket += HandlePlayerRespawnPacket;
            serverManager.OnWeaponPickupPacket += HandleWeaponPickup;
            serverManager.OnChatMessagePacket += HandleChatMessage;
            serverManager.OnPingMarkerPacket += HandlePingMarker;
        }

        #endregion

        #region Matchmaking Handlers

        private void HandleJoinQueue(string jsonData, System.Net.IPEndPoint endpoint)
        {
            var request = JsonConvert.DeserializeObject<JoinQueueRequest>(jsonData);
            var client = serverManager.GetClientByToken(request.token);

            if (client == null)
            {
                serverManager.SendErrorResponse(endpoint, "Invalid session", 401);
                return;
            }

            if (client.InQueue)
            {
                SendQueueResponse(client, false, "Already in queue");
                return;
            }

            if (client.InGame)
            {
                SendQueueResponse(client, false, "Already in a game");
                return;
            }

            var matchmakingPlayer = new PlayerMatchmaking
            {
                PlayerId = client.PlayerId,
                Username = client.Username,
                JoinedAt = DateTime.UtcNow,
                Client = client
            };

            matchmakingQueue.Enqueue(matchmakingPlayer);
            client.InQueue = true;
            client.JoinedQueueAt = DateTime.UtcNow;

            Console.WriteLine($"[GameManager] {client.Username} joined matchmaking queue. Queue size: {matchmakingQueue.Count}");
            SendQueueResponse(client, true, "Joined queue");
        }

        private void HandleLeaveQueue(string jsonData, System.Net.IPEndPoint endpoint)
        {
            var request = JsonConvert.DeserializeObject<LeaveQueueRequest>(jsonData);
            var client = serverManager.GetClientByToken(request.token);

            if (client == null)
            {
                serverManager.SendErrorResponse(endpoint, "Invalid session", 401);
                return;
            }

            if (!client.InQueue)
            {
                return;
            }

            RemoveFromQueue(client);
            Console.WriteLine($"[GameManager] {client.Username} left matchmaking queue");
            SendQueueResponse(client, true, "Left queue");
        }

        private void HandleGetQueueStatus(string jsonData, System.Net.IPEndPoint endpoint)
        {
            var request = JsonConvert.DeserializeObject<GetQueueStatusRequest>(jsonData);
            var client = serverManager.GetClientByToken(request.token);

            if (client == null)
            {
                serverManager.SendErrorResponse(endpoint, "Invalid session", 401);
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

            serverManager.SendPacket(queueStatus, endpoint);
        }

        private void RemoveFromQueue(ClientConnection client)
        {
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
        }

        private void SendQueueResponse(ClientConnection client, bool success, string message)
        {
            var response = new QueueResponseData
            {
                type = "queue_response",
                success = success,
                message = message,
                queueSize = matchmakingQueue.Count,
                position = GetQueuePosition(client.PlayerId),
                estimatedWaitTime = CalculateEstimatedWaitTime(),
                playersNeeded = PLAYERS_PER_GAME - (matchmakingQueue.Count % PLAYERS_PER_GAME)
            };
            serverManager.SendPacket(response, client);
        }

        #endregion

        #region Matchmaking Loop

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

            players = players.OrderBy(p => p.MMR).ToList();

            var redTeam = new List<ClientConnection>();
            var blueTeam = new List<ClientConnection>();

            for (int i = 0; i < players.Count; i++)
            {
                var client = players[i].Client;
                if (client != null)
                {
                    if (i % 2 == 0)
                        redTeam.Add(client);
                    else
                        blueTeam.Add(client);
                }
            }

            var gameRoom = new GameRoom
            {
                GameId = gameId,
                GameType = GameType.TeamDeathmatch,
                MapName = Map.Office,
                RedTeam = redTeam,
                BlueTeam = blueTeam,
                StartTime = DateTime.UtcNow,
                TimeRemaining = GAME_DURATION,
                RedScore = 0,
                BlueScore = 0,
                IsActive = true
            };

            foreach (var player in redTeam.Concat(blueTeam))
            {
                gameRoom.PlayerStats[player.PlayerId] = new InGameStats
                {
                    Kills = 0,
                    Deaths = 0,
                    Score = 0,
                    TeamId = redTeam.Contains(player) ? 0 : 1
                };

                gameRoom.PlayerPositions[player.PlayerId] = GetSpawnPosition(redTeam.Contains(player) ? 0 : 1);
                gameRoom.PlayerStates[player.PlayerId] = new PlayerStateInfo
                {
                    health = 100,
                    isAlive = true,
                    currentAmmo = 30,
                    isReloading = false,
                    isCrouching = false,
                    isSprinting = false,
                    isAiming = false
                };
            }

            activeGames.TryAdd(gameId, gameRoom);

            foreach (var player in redTeam.Concat(blueTeam))
            {
                player.InQueue = false;
                player.InGame = true;
                player.CurrentGameId = gameId;
                player.TeamId = redTeam.Contains(player) ? 0 : 1;
                player.IsDead = false;

                var gameStartData = new GameStartData
                {
                    type = "game_start",
                    mapName = Map.Office,
                    gameType = GameType.TeamDeathmatch,
                    gameId = gameId,
                    teamId = player.TeamId,
                    players = GetGamePlayerDataList(redTeam, blueTeam),
                    timestamp = DateTime.UtcNow
                };

                var spawnData = new PlayerSpawnData
                {
                    type = "player_spawn",
                    playerId = player.PlayerId,
                    username = player.Username,
                    teamId = player.TeamId,
                    position = GetSpawnPosition(player.TeamId),
                    health = 100,
                    timestamp = DateTime.UtcNow
                };

                serverManager.SendPacket(gameStartData, player);
                serverManager.SendPacket(spawnData, player);

                Console.WriteLine($"[GameManager] Game {gameId} started for {player.Username} on team {(player.TeamId == 0 ? "Red" : "Blue")}");
            }

            Console.WriteLine($"[GameManager] Game {gameId} created! Red Team: {redTeam.Count}, Blue Team: {blueTeam.Count}");
        }

        #endregion

        #region Game Update Loop

        private void GameUpdateLoop()
        {
            while (isRunning)
            {
                var startTime = DateTime.UtcNow;

                UpdateGames();

                var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
                var sleepTime = (int)(TICK_TIME * 1000 - elapsed);
                if (sleepTime > 0)
                {
                    Thread.Sleep(sleepTime);
                }
            }
        }

        private void UpdateGames()
        {
            foreach (var game in activeGames.Values)
            {
                if (!game.IsActive)
                    continue;

                var elapsed = (DateTime.UtcNow - game.StartTime).TotalSeconds;
                game.TimeRemaining = GAME_DURATION - elapsed;

                if (game.TimeRemaining <= 0 || game.RedScore >= 10 || game.BlueScore >= 10)
                {
                    EndGame(game);
                    continue;
                }

                SendGameState(game);
            }
        }

        private void SendGameState(GameRoom game)
        {
            foreach (var player in game.RedTeam.Concat(game.BlueTeam))
            {
                if (!player.InGame || !player.IsConnected)
                    continue;

                var playerStats = game.PlayerStats.ContainsKey(player.PlayerId) ?
                    game.PlayerStats[player.PlayerId] : new InGameStats();

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

                serverManager.SendPacket(gameState, player);
            }
        }

        private void EndGame(GameRoom game)
        {
            game.IsActive = false;

            foreach (var player in game.RedTeam.Concat(game.BlueTeam))
            {
                var stats = game.PlayerStats.ContainsKey(player.PlayerId) ?
                    game.PlayerStats[player.PlayerId] : new InGameStats();

                bool isWinner = (player.TeamId == 0 && game.RedScore > game.BlueScore) ||
                               (player.TeamId == 1 && game.BlueScore > game.RedScore);

                int goldReward = 100 + (stats.Kills * 10) + (isWinner ? 50 : 0);
                int xpReward = 50 + (stats.Kills * 5) + (isWinner ? 25 : 0);

                // Update player stats through LoginManager
                //UpdatePlayerStats(player.PlayerId, stats, isWinner, goldReward, xpReward);

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

                serverManager.SendPacket(gameEndData, player);

                player.InGame = false;
                player.CurrentGameId = -1;
                player.TeamId = -1;
                player.IsDead = false;

                Console.WriteLine($"[GameManager] Game {game.GameId} ended for {player.Username}: {(isWinner ? "WINNER" : "LOSER")} - K/D: {stats.Kills}/{stats.Deaths}, Gold: +{goldReward}, XP: +{xpReward}");
            }

            activeGames.TryRemove(game.GameId, out _);
        }

        //private void UpdatePlayerStats(int playerId, InGameStats stats, bool isWinner, int goldReward, int xpReward)
        //{
        //    // Delegate player stat updates to LoginManager
        //    serverManager.UpdatePlayerGameStats(playerId, stats.Kills, stats.Deaths, isWinner, goldReward, xpReward);
        //}

        #endregion

        #region Game Input Handlers

        private void HandleMovementInput(string jsonData, System.Net.IPEndPoint endpoint)
        {
            var input = JsonConvert.DeserializeObject<MovementInput>(jsonData);
            var client = serverManager.GetClientByToken(input.token);

            if (client == null || !client.InGame)
                return;

            if (activeGames.TryGetValue(client.CurrentGameId, out var game))
            {
                game.PlayerPositions[client.PlayerId] = new Vector3Data
                {
                    x = input.position.x,
                    y = input.position.y,
                    z = input.position.z
                };
                game.PlayerRotations[client.PlayerId] = new Vector2Data
                {
                    x = input.rotation.x,
                    y = input.rotation.y
                };
            }
        }

        private void HandleShootInput(string jsonData, System.Net.IPEndPoint endpoint)
        {
            var input = JsonConvert.DeserializeObject<ShootInput>(jsonData);
            var client = serverManager.GetClientByToken(input.token);

            if (client == null || !client.InGame)
                return;

            if (!activeGames.TryGetValue(client.CurrentGameId, out var game))
                return;

            var shooterPos = game.PlayerPositions.ContainsKey(client.PlayerId)
                ? game.PlayerPositions[client.PlayerId]
                : new Vector3Data();

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

                if (distanceToTarget < 15f && distanceToShot < 15f)
                {
                    int damage = CalculateDamage(input.weaponId, distanceToTarget, false);
                    bool isHeadshot = new Random().NextDouble() < 0.1f;
                    if (isHeadshot) damage *= 2;

                    var targetState = game.PlayerStates.ContainsKey(target.PlayerId) ?
                        game.PlayerStates[target.PlayerId] : new PlayerStateInfo();

                    targetState.health -= damage;
                    game.PlayerStates[target.PlayerId] = targetState;

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
                        isKill = targetState.health <= 0,
                        timestamp = DateTime.UtcNow
                    };

                    if (targetState.health <= 0)
                    {
                        var targetStats = game.PlayerStats[target.PlayerId];
                        var shooterStats = game.PlayerStats[client.PlayerId];

                        targetStats.Deaths++;
                        shooterStats.Kills++;
                        shooterStats.Score += 100;

                        if (isHeadshot)
                        {
                            shooterStats.Score += 50;
                        }

                        if (client.TeamId == 0)
                            game.RedScore++;
                        else
                            game.BlueScore++;

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

                        BroadcastToGame(game, killData);

                        Console.WriteLine($"[GameManager] {client.Username} killed {target.Username}! Score: {game.RedScore}-{game.BlueScore}");

                        HandlePlayerDeath(target, game);
                    }

                    BroadcastToGame(game, hitData);
                    break;
                }
            }
        }

        private void HandleAbilityInput(string jsonData, System.Net.IPEndPoint endpoint)
        {
            var input = JsonConvert.DeserializeObject<AbilityInput>(jsonData);
            var client = serverManager.GetClientByToken(input.token);

            if (client == null || !client.InGame)
                return;

            Console.WriteLine($"[GameManager] {client.Username} used ability: {input.abilityName}");

            if (activeGames.TryGetValue(client.CurrentGameId, out var game))
            {
                var abilityData = new
                {
                    type = "ability_used",
                    playerId = client.PlayerId,
                    playerName = client.Username,
                    abilityName = input.abilityName,
                    targetX = input.targetX,
                    targetY = input.targetY,
                    targetZ = input.targetZ,
                    timestamp = DateTime.UtcNow
                };

                BroadcastToGameExcept(game, client.PlayerId, abilityData);
            }
        }

        private void HandlePlayerState(string jsonData, System.Net.IPEndPoint endpoint)
        {
            var state = JsonConvert.DeserializeObject<PlayerStateData>(jsonData);
            var client = serverManager.GetClientByToken(state.token);

            if (client == null || !client.InGame)
                return;

            if (activeGames.TryGetValue(client.CurrentGameId, out var game))
            {
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
            }
        }

        private void HandlePlayerDeathPacket(string jsonData, System.Net.IPEndPoint endpoint)
        {
            var deathPacket = JsonConvert.DeserializeObject<dynamic>(jsonData);
            string token = deathPacket.token;
            int gameId = deathPacket.gameId;

            var client = serverManager.GetClientByToken(token);

            if (client == null || !client.InGame)
                return;

            if (activeGames.TryGetValue(gameId, out var game))
            {
                HandlePlayerDeath(client, game);
            }
        }

        private void HandlePlayerRespawnPacket(string jsonData, System.Net.IPEndPoint endpoint)
        {
            var respawnPacket = JsonConvert.DeserializeObject<dynamic>(jsonData);
            string token = respawnPacket.token;
            int gameId = respawnPacket.gameId;

            var client = serverManager.GetClientByToken(token);

            if (client == null || !client.InGame)
                return;

            if (activeGames.TryGetValue(gameId, out var game))
            {
                var spawnPos = GetSpawnPosition(client.TeamId);

                if (game.PlayerStates.ContainsKey(client.PlayerId))
                {
                    game.PlayerStates[client.PlayerId].health = 100;
                    game.PlayerStates[client.PlayerId].isAlive = true;
                }

                client.IsDead = false;

                var spawnData = new PlayerSpawnData
                {
                    type = "player_spawn",
                    playerId = client.PlayerId,
                    username = client.Username,
                    teamId = client.TeamId,
                    position = spawnPos,
                    health = 100,
                    timestamp = DateTime.UtcNow
                };

                BroadcastToGame(game, spawnData);

                Console.WriteLine($"[GameManager] {client.Username} respawned in game {game.GameId}");
            }
        }

        private void HandleWeaponPickup(string jsonData, System.Net.IPEndPoint endpoint)
        {
            var pickup = JsonConvert.DeserializeObject<WeaponPickupData>(jsonData);
            var client = serverManager.GetClientByToken(pickup.token);

            if (client == null || !client.InGame)
                return;

            Console.WriteLine($"[GameManager] {client.Username} picked up weapon {pickup.weaponId}");

            var confirmPickup = new
            {
                type = "weapon_pickup_confirm",
                weaponId = pickup.weaponId,
                ammoAmount = pickup.ammoAmount,
                timestamp = DateTime.UtcNow
            };

            serverManager.SendPacket(confirmPickup, client);
        }

        private void HandleChatMessage(string jsonData, System.Net.IPEndPoint endpoint)
        {
            var chat = JsonConvert.DeserializeObject<ChatMessage>(jsonData);
            var client = serverManager.GetClientByToken(chat.token);

            if (client == null)
                return;

            chat.playerId = client.PlayerId;
            chat.username = client.Username;
            chat.timestamp = DateTime.UtcNow;

            if (chat.teamId == -1)
            {
                serverManager.SendToAll(chat);
            }
            else
            {
                if (client.InGame && activeGames.TryGetValue(client.CurrentGameId, out var game))
                {
                    var team = chat.teamId == 0 ? game.RedTeam : game.BlueTeam;
                    serverManager.SendToAll(chat, team.Where(t => t.InGame).ToList());
                }
            }
        }

        private void HandlePingMarker(string jsonData, System.Net.IPEndPoint endpoint)
        {
            var ping = JsonConvert.DeserializeObject<PingData>(jsonData);
            var client = serverManager.GetClientByToken(ping.token);

            if (client == null || !client.InGame)
                return;

            ping.playerId = client.PlayerId;

            if (activeGames.TryGetValue(client.CurrentGameId, out var game))
            {
                var team = client.TeamId == 0 ? game.RedTeam : game.BlueTeam;
                BroadcastToGameExcept(game, client.PlayerId, ping, team);
            }
        }

        private void HandlePlayerDeath(ClientConnection victim, GameRoom game)
        {
            victim.IsDead = true;
            if (game.PlayerStates.ContainsKey(victim.PlayerId))
            {
                game.PlayerStates[victim.PlayerId].isAlive = false;
            }

            var deathData = new
            {
                type = "player_death_notification",
                respawnTime = 5f,
                timestamp = DateTime.UtcNow
            };
            serverManager.SendPacket(deathData, victim);
        }

        private void HandlePlayerDisconnectFromGame(ClientConnection client, GameRoom game)
        {
            var disconnectData = new PlayerDespawnData
            {
                type = "player_despawn",
                playerId = client.PlayerId,
                reason = "disconnect",
                timestamp = DateTime.UtcNow
            };

            BroadcastToGameExcept(game, client.PlayerId, disconnectData);
        }

        #endregion

        #region Broadcast Helpers

        private void BroadcastToGame(GameRoom game, object packet)
        {
            var allPlayers = game.RedTeam.Concat(game.BlueTeam).Where(p => p.InGame && p.IsConnected);
            serverManager.SendToAll(packet, allPlayers.ToList());
        }

        private void BroadcastToGameExcept(GameRoom game, int excludePlayerId, object packet, IEnumerable<ClientConnection> specificPlayers = null)
        {
            var players = specificPlayers ?? game.RedTeam.Concat(game.BlueTeam);
            var targetPlayers = players.Where(p => p.PlayerId != excludePlayerId && p.InGame && p.IsConnected);
            serverManager.SendToAll(packet, targetPlayers.ToList());
        }

        #endregion

        #region Helper Methods

        private int CalculateDamage(int weaponId, float distance, bool isHeadshot)
        {
            int baseDamage = weaponId switch
            {
                1 => 25,
                2 => 35,
                3 => 20,
                4 => 70,
                5 => 15,
                _ => 15
            };

            float falloff = Math.Clamp(1f - (distance / 100f), 0.3f, 1f);
            int finalDamage = (int)(baseDamage * falloff);

            if (isHeadshot)
                finalDamage *= 2;

            return Math.Max(1, finalDamage);
        }

        private Vector3Data GetSpawnPosition(int teamId)
        {
            if (teamId == 0)
            {
                return new Vector3Data { x = -15f, y = 1f, z = 0f };
            }
            else
            {
                return new Vector3Data { x = 15f, y = 1f, z = 0f };
            }
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
            return playersNeeded * 30;
        }

        private List<GamePlayerData> GetGamePlayerDataList(List<ClientConnection> redTeam, List<ClientConnection> blueTeam)
        {
            var players = new List<GamePlayerData>();

            foreach (var player in redTeam)
            {
                players.Add(new GamePlayerData
                {
                    playerId = player.PlayerId,
                    username = player.Username,
                    teamId = 0
                });
            }

            foreach (var player in blueTeam)
            {
                players.Add(new GamePlayerData
                {
                    playerId = player.PlayerId,
                    username = player.Username,
                    teamId = 1
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

                var playerState = game.PlayerStates.ContainsKey(player.PlayerId) ?
                    game.PlayerStates[player.PlayerId] : new PlayerStateInfo();

                others.Add(new OtherPlayerData
                {
                    playerId = player.PlayerId,
                    username = player.Username,
                    teamId = player.TeamId,
                    position = game.PlayerPositions.ContainsKey(player.PlayerId) ?
                               game.PlayerPositions[player.PlayerId] : new Vector3Data(),

                    rotation = game.PlayerRotations.ContainsKey(player.PlayerId) ?
                               game.PlayerRotations[player.PlayerId] : new Vector2Data(),
                    health = playerState.health,
                    isAlive = playerState.isAlive
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

        #endregion

        // Add to GameManager's public methods region
        public int GetActiveGameCount()
        {
            return activeGames.Count;
        }

        public void PrintActiveGames()
        {
            Console.WriteLine($"\n[GameManager] Active Games: {activeGames.Count}");
            foreach (var game in activeGames.Values)
            {
                Console.WriteLine($"  Game {game.GameId}: {game.MapName} ({game.GameType})");
                Console.WriteLine($"    Scores: Red {game.RedScore} - {game.BlueScore} Blue");
                Console.WriteLine($"    Time: {Math.Max(0, (int)game.TimeRemaining)}s remaining");
                Console.WriteLine($"    Players: {game.RedTeam.Count + game.BlueTeam.Count} total");
                Console.WriteLine($"    Red Team: {string.Join(", ", game.RedTeam.Select(p => p.Username))}");
                Console.WriteLine($"    Blue Team: {string.Join(", ", game.BlueTeam.Select(p => p.Username))}");
                Console.WriteLine();
            }
        }
    }

    #region Game Data Models

    public class PlayerMatchmaking
    {
        public int PlayerId { get; set; }
        public string Username { get; set; }
        public int MMR { get; set; }
        public DateTime JoinedAt { get; set; }
        public ClientConnection Client { get; set; }
    }

    public class GameRoom
    {
        public int GameId { get; set; }
        public Map MapName { get; set; }
        public GameType GameType { get; set; }
        public List<ClientConnection> RedTeam { get; set; }
        public List<ClientConnection> BlueTeam { get; set; }
        public DateTime StartTime { get; set; }
        public double TimeRemaining { get; set; }
        public int RedScore { get; set; }
        public int BlueScore { get; set; }
        public bool IsActive { get; set; }
        public Dictionary<int, InGameStats> PlayerStats { get; set; } = new Dictionary<int, InGameStats>();
        public Dictionary<int, Vector3Data> PlayerPositions { get; set; } = new Dictionary<int, Vector3Data>();
        public Dictionary<int, Vector2Data> PlayerRotations { get; set; } = new Dictionary<int, Vector2Data>();
        public Dictionary<int, PlayerStateInfo> PlayerStates { get; set; } = new Dictionary<int, PlayerStateInfo>();
    }

    public class InGameStats
    {
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public int Score { get; set; }
        public int TeamId { get; set; }
    }

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