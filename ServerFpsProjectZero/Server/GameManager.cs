using Newtonsoft.Json;
using ServerFpsProjectZero.Models;
using ServerFpsProjectZero.Networking;
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
        private LoginManager loginManager;
        private ConcurrentDictionary<int, GameRoom> activeGames;
        private int nextGameId = 1;
        private bool isRunning = false;
        private Thread gameUpdateThread;

        // Configuration
        private const int TICK_RATE = 20;
        private const float TICK_TIME = 1f / TICK_RATE;
        private const float GAME_DURATION = 600f;
        private const int MinPlayersPerGame = 2;

        public GameManager(ServerManager serverManager)
        {
            this.serverManager = serverManager;
            activeGames = new ConcurrentDictionary<int, GameRoom>();

            // Subscribe to game-related events only
            SubscribeToEvents();
        }

        public void SetLoginManager(LoginManager loginManager)
        {
            this.loginManager = loginManager;
        }

        public void Start()
        {
            isRunning = true;

            gameUpdateThread = new Thread(GameUpdateLoop);
            gameUpdateThread.IsBackground = true;
            gameUpdateThread.Start();

            Console.WriteLine($"[GameManager] Started");
            Console.WriteLine($"[GameManager] Game duration: {GAME_DURATION} seconds");
        }

        public void Stop()
        {
            isRunning = false;
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

        #region Public Game Creation Method

        public GameRoom CreateGame(List<Player> players, GameType gameType = GameType.TeamDeathmatch, Map mapName = Map.Office)
        {
            int gameId = Interlocked.Increment(ref nextGameId);

            // Sort players by MMR for balanced teams
            players = players.OrderBy(p => p.MMR).ToList();

            var redTeam = new List<ClientConnection>();
            var blueTeam = new List<ClientConnection>();
            var playerLookup = new Dictionary<int, Player>();

            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];
                playerLookup[player.PlayerId] = player;

                // Get or create client connection for each player
                var client = serverManager.GetClientByToken(player.SessionToken);
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
                GameType = gameType,
                MapName = mapName,
                RedTeam = redTeam,
                BlueTeam = blueTeam,
                StartTime = DateTime.UtcNow,
                TimeRemaining = GAME_DURATION,
                RedScore = 0,
                BlueScore = 0,
                IsActive = true
            };

            // Initialize player stats and states
            foreach (var player in redTeam.Concat(blueTeam))
            {
                gameRoom.PlayerStats[player.PlayerId] = new InGameStats
                {
                    Kills = 0,
                    Deaths = 0,
                    Score = 0,
                    TeamId = redTeam.Contains(player) ? 0 : 1
                };

                //gameRoom.PlayerPositions[player.PlayerId] = GetSpawnPosition(redTeam.Contains(player) ? 0 : 1);
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

            // Notify all players about game start
            foreach (var player in redTeam.Concat(blueTeam))
            {
                player.InGame = true;
                player.CurrentGameId = gameId;
                player.TeamId = redTeam.Contains(player) ? 0 : 1;
                player.IsDead = false;

                var gameStartData = new GameStartData
                {
                    type = "game_start",
                    mapName = mapName,
                    gameType = gameType,
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
            return gameRoom;
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

                // Clean up an abandoned game: if every participant has disconnected,
                // there is nobody left to receive a result, so drop it immediately
                // instead of letting it occupy a slot until the timer expires.
                int connectedPlayers = game.RedTeam.Concat(game.BlueTeam)
                    .Count(p => p.IsConnected && p.InGame);
                if (connectedPlayers == 0)
                {
                    Console.WriteLine($"[GameManager] Game {game.GameId} removed (all players disconnected)");
                    game.IsActive = false;
                    activeGames.TryRemove(game.GameId, out _);
                    continue;
                }

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

                // Update player stats through server manager
                serverManager.UpdatePlayerGameStats(player.PlayerId, stats.Kills, stats.Deaths, isWinner, goldReward, xpReward);

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

                loginManager?.UpdatePlayerStatusForFriends(player.PlayerId, PlayerStatus.Online);

                player.InGame = false;
                player.CurrentGameId = -1;
                player.TeamId = -1;
                player.IsDead = false;

                // Clear the parallel Player-model in-game flags too, otherwise the
                // player is blocked from re-queueing ("already in a game") even
                // though the match has ended.
                loginManager?.ResetPlayerGameState(player.PlayerId);

                Console.WriteLine($"[GameManager] Game {game.GameId} ended for {player.Username}: {(isWinner ? "WINNER" : "LOSER")} - K/D: {stats.Kills}/{stats.Deaths}, Gold: +{goldReward}, XP: +{xpReward}");
            }

            activeGames.TryRemove(game.GameId, out _);
        }

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

            var shooterPos = new Vector3Data { x = input.originX, y = input.originY, z = input.originZ };
            var aimPos = new Vector3Data { x = input.targetX, y = input.targetY, z = input.targetZ };

            // Server-side shot check: cast the shot ray and test each enemy's
            // last-known position against it. Replaces the old point-blank
            // "both within 15 units" sphere, which made ranged fire impossible.
            float sdx = aimPos.x - shooterPos.x, sdy = aimPos.y - shooterPos.y, sdz = aimPos.z - shooterPos.z;
            float sLen = (float)Math.Sqrt(sdx * sdx + sdy * sdy + sdz * sdz);
            float dirX = sLen > 0.0001f ? sdx / sLen : 0f;
            float dirY = sLen > 0.0001f ? sdy / sLen : 0f;
            float dirZ = sLen > 0.0001f ? sdz / sLen : 1f;

            const float BODY_RADIUS = 1.2f;
            const float HEAD_RADIUS = 0.55f;
            const float HEAD_HEIGHT = 1.6f;
            const float MAX_RANGE = 200f;

            foreach (var target in game.RedTeam.Concat(game.BlueTeam))
            {
                if (target.PlayerId == client.PlayerId || target.TeamId == client.TeamId)
                    continue;

                if (!game.PlayerPositions.ContainsKey(target.PlayerId))
                    continue;

                var targetPos = game.PlayerPositions[target.PlayerId];

                // Distance travelled along the ray before reaching the target.
                float tx = targetPos.x - shooterPos.x, ty = targetPos.y - shooterPos.y, tz = targetPos.z - shooterPos.z;
                float distanceToTarget = tx * dirX + ty * dirY + tz * dirZ;
                if (distanceToTarget < 0f || distanceToTarget > MAX_RANGE)
                    continue;

                // Closest approach distance between the target and the ray.
                float perpX = tx - dirX * distanceToTarget;
                float perpY = ty - dirY * distanceToTarget;
                float perpZ = tz - dirZ * distanceToTarget;
                float perpDist = (float)Math.Sqrt(perpX * perpX + perpY * perpY + perpZ * perpZ);

                // Headshot when the ray passes close to the head centre.
                float hy = (targetPos.y + HEAD_HEIGHT) - shooterPos.y;
                float headDist = tx * dirX + hy * dirY + tz * dirZ;
                float headPerpX = tx - dirX * headDist;
                float headPerpY = hy - dirY * headDist;
                float headPerpZ = tz - dirZ * headDist;
                float headPerp = (float)Math.Sqrt(headPerpX * headPerpX + headPerpY * headPerpY + headPerpZ * headPerpZ);

                bool isHeadshot = headPerp <= HEAD_RADIUS;
                if (perpDist > BODY_RADIUS && !isHeadshot)
                    continue;

                {
                    int damage = CalculateDamage(input.weaponId, distanceToTarget, isHeadshot);

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

                    // Health is server-authoritative: it is reduced only by the
                    // damage handler and reset on respawn. We deliberately do NOT
                    // overwrite it with the client-reported health, otherwise a
                    // client that keeps reporting 100 would erase every hit and
                    // kills could never register.
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

        #region Public Methods

        public int GetActiveGameCount()
        {
            return activeGames.Count;
        }

        public GameRoom GetGameById(int gameId)
        {
            activeGames.TryGetValue(gameId, out GameRoom game);
            return game;
        }

        public bool RemovePlayerFromGame(int playerId)
        {
            foreach (var game in activeGames.Values)
            {
                var player = game.RedTeam.FirstOrDefault(p => p.PlayerId == playerId) ??
                            game.BlueTeam.FirstOrDefault(p => p.PlayerId == playerId);

                if (player != null)
                {
                    player.InGame = false;
                    player.CurrentGameId = -1;
                    player.TeamId = -1;
                    player.IsDead = false;

                    var disconnectData = new PlayerDespawnData
                    {
                        type = "player_despawn",
                        playerId = playerId,
                        reason = "disconnect",
                        timestamp = DateTime.UtcNow
                    };
                    BroadcastToGameExcept(game, playerId, disconnectData);

                    // If the match can no longer be played (fewer than the minimum
                    // number of players remain), forfeit it so the surviving client
                    // is not left stuck in a live game until the timer expires.
                    int remainingConnected = game.RedTeam.Concat(game.BlueTeam)
                        .Count(p => p.InGame && p.IsConnected);
                    if (remainingConnected < MinPlayersPerGame)
                    {
                        ForfeitGame(game);
                    }

                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Ends a game early because a player left and not enough players remain.
        /// The surviving player wins by forfeit; their in-game flags (both the
        /// ClientConnection and the Player-model flags) are cleared so they can
        /// immediately join the queue again.
        /// </summary>
        private void ForfeitGame(GameRoom game)
        {
            game.IsActive = false;

            foreach (var player in game.RedTeam.Concat(game.BlueTeam).ToList())
            {
                // Clear flags on every participant (including any already-disconnected
                // client) so no stale InGame state survives.
                player.InGame = false;
                player.CurrentGameId = -1;
                player.TeamId = -1;
                player.IsDead = false;

                if (!player.IsConnected)
                    continue;

                var stats = game.PlayerStats.TryGetValue(player.PlayerId, out var s)
                    ? s : new InGameStats();

                bool isWinner = true; // opponent left the match
                int goldReward = 100 + (stats.Kills * 10) + 50;
                int xpReward = 50 + (stats.Kills * 5) + 25;

                serverManager.UpdatePlayerGameStats(player.PlayerId, stats.Kills, stats.Deaths, isWinner, goldReward, xpReward);

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

                loginManager?.UpdatePlayerStatusForFriends(player.PlayerId, PlayerStatus.Online);
                loginManager?.ResetPlayerGameState(player.PlayerId);

                Console.WriteLine($"[GameManager] Game {game.GameId} forfeited: {player.Username} wins (opponent disconnected)");
            }

            activeGames.TryRemove(game.GameId, out _);
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

        #endregion
    }

    #region Game Data Models

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
        // Thread-safety: these dictionaries are read by the game-loop thread
        // (SendGameState/EndGame) while packet handlers running on the thread pool
        // mutate them. Plain Dictionary corrupts or throws under concurrent access;
        // ConcurrentDictionary makes those reads/writes safe.
        public ConcurrentDictionary<int, InGameStats> PlayerStats { get; set; } = new ConcurrentDictionary<int, InGameStats>();
        public ConcurrentDictionary<int, Vector3Data> PlayerPositions { get; set; } = new ConcurrentDictionary<int, Vector3Data>();
        public ConcurrentDictionary<int, Vector2Data> PlayerRotations { get; set; } = new ConcurrentDictionary<int, Vector2Data>();
        public ConcurrentDictionary<int, PlayerStateInfo> PlayerStates { get; set; } = new ConcurrentDictionary<int, PlayerStateInfo>();
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