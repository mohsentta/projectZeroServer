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

        /// <summary>
        /// When true, every shoot packet logs its hit-test arithmetic. On by default while we
        /// are still diagnosing hit registration; pass --no-shootdebug or use the
        /// "shootdebug" console command to turn it off once shooting is trusted.
        /// </summary>
        public bool ShootDebug { get; set; } = true;
        private Thread gameUpdateThread;

        // Configuration
        private const int TICK_RATE = 20;
        private const float TICK_TIME = 1f / TICK_RATE;
        private const float GAME_DURATION = 600f;
        private const int MinPlayersPerGame = 2;

        /// <summary>
        /// Height above the character root (the feet) that counts as the head, and how far the
        /// reported impact point may sit from it and still count as a headshot. Headshots are
        /// positional now that hit resolution is target-by-id rather than a ray test.
        /// </summary>
        private const float HEAD_HEIGHT = 1.6f;
        private const float HEADSHOT_TOLERANCE = 0.35f;

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

            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];

                // Look up the live connection by the stable PlayerId key rather than
                // the session token: the token is a second source of truth that could
                // diverge from ServerManager.tokenToClient if a token is ever renewed,
                // which would silently drop the player from the match.
                var client = serverManager.GetClientById(player.PlayerId);
                if (client != null)
                {
                    if (i % 2 == 0)
                        redTeam.Add(client);
                    else
                        blueTeam.Add(client);
                }
                else
                {
                    Console.WriteLine($"[GameManager] WARNING: no client connection for matched player {player.Username} (ID {player.PlayerId}); skipping");
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

            // A co-op run owns an enemy simulation; every other game type leaves it null, and
            // that null is what routes the game loop down the PvP path.
            if (gameType == GameType.CoopSurvival)
            {
                gameRoom.Enemies = new EnemyManager(gameId);
                Console.WriteLine($"[Coop] Game {gameId} starting a survival run for " +
                                  $"{redTeam.Count + blueTeam.Count} player(s)");
            }

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

                // Co-op runs its own loop: there are no team scores to win with, so the run ends
                // when the enemies finish the players rather than on the clock alone.
                if (game.GameType == GameType.CoopSurvival)
                {
                    TickCoop(game);
                    continue;
                }

                if (game.TimeRemaining <= 0 || game.RedScore >= 10 || game.BlueScore >= 10)
                {
                    EndGame(game);
                    continue;
                }

                SendGameState(game);
            }
        }

        /// <summary>
        /// Resolves a shot that named a co-op enemy.
        ///
        /// Line of sight is the client's call, exactly as it is for player targets - the server
        /// holds no map geometry. What the server enforces: the game must be a co-op run, the
        /// enemy must exist and be alive, and the shooter must be within weapon range of it.
        /// </summary>
        private void HandleEnemyShot(GameRoom game, ClientConnection client, ShootInput input)
        {
            if (game.Enemies == null)
            {
                // A player-target shot in a co-op run, or an enemy id sent during PvP. Either way
                // there is nothing to damage.
                if (ShootDebug)
                    Console.WriteLine($"[Shoot] ignored: game {game.GameId} has no enemies (type {game.GameType})");

                return;
            }

            if (!game.PlayerPositions.TryGetValue(client.PlayerId, out var shooterPos))
            {
                if (ShootDebug)
                    Console.WriteLine($"[Shoot] ignored: no known position for {client.Username}");

                return;
            }

            var target = game.Enemies.LiveEnemies.FirstOrDefault(e => e.EnemyId == input.hitEnemyId);
            if (target == null)
            {
                if (ShootDebug)
                    Console.WriteLine($"[Shoot] ignored: enemy {input.hitEnemyId} does not exist or is already dead");

                return;
            }

            // Range check. The client reports where its ray hit, which for an enemy is on the
            // enemy's body, so measure shooter to that point rather than to the enemy's feet.
            float dx = input.targetX - shooterPos.x;
            float dy = input.targetY - shooterPos.y;
            float dz = input.targetZ - shooterPos.z;
            float distance = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);

            const float MAX_ENEMY_RANGE = 200f;
            if (distance > MAX_ENEMY_RANGE)
            {
                if (ShootDebug)
                    Console.WriteLine($"[Shoot] ignored: enemy {target.EnemyId} is {distance:0.#}m away " +
                                      $"(max {MAX_ENEMY_RANGE})");

                return;
            }

            int damage = CalculateDamage(input.weaponId, distance, isHeadshot: false);

            EnemyDeathEvent death;
            lock (game.StateLock)
            {
                death = game.Enemies.ApplyDamage(target.EnemyId, damage, client.PlayerId, client.Username);
            }

            if (ShootDebug)
            {
                Console.WriteLine($"[Shoot] {client.Username} hit enemy {target.EnemyId} " +
                                  $"(level {target.Level}) for {damage} at {distance:0.#}m " +
                                  $"-> health {Math.Max(0, target.Health)}/{target.MaxHealth}" +
                                  (death != null ? " KILL" : ""));
            }

            if (death != null)
                HandleEnemyDeath(game, death);
        }

        /// <summary>
        /// One tick of a co-op run: advance the enemies, resolve their swings against players,
        /// roll shard drops for anything the players killed, and broadcast the result.
        /// </summary>
        private void TickCoop(GameRoom game)
        {
            if (game.Enemies == null)
                return;

            var players = BuildCoopSnapshots(game);

            game.Enemies.Tick((float)TICK_TIME, players, out var deaths, out var attacks);

            // Enemy swings. Damage is applied here, not in the simulation, so all mutation of
            // player state stays under the same lock the PvP path uses.
            foreach (var attack in attacks)
                ApplyEnemyAttack(game, attack);

            // Enemy deaths, including the shard roll.
            foreach (var death in deaths)
                HandleEnemyDeath(game, death);

            // Broadcast the state of the world.
            var wave = game.Enemies.WaveState(players.Count);
            var snapshot = game.Enemies.Snapshot();

            foreach (var player in game.RedTeam.Concat(game.BlueTeam))
            {
                if (!player.InGame || !player.IsConnected) continue;

                serverManager.SendPacket(new CoopEnemyStateUpdate
                {
                    enemies = snapshot,
                    serverTick = DateTime.UtcNow.Ticks,
                }, player);

                serverManager.SendPacket(wave, player);
            }

            // A run ends when nobody is left standing. Note this asks about participants rather
            // than about snapshots: a player whose first movement packet has not arrived yet has
            // no known position and therefore no snapshot, and treating that as "down" ended the
            // run at wave 0 the instant the match started.
            var participants = game.RedTeam.Concat(game.BlueTeam)
                .Where(p => p.InGame && p.IsConnected)
                .ToList();

            bool anyAlive = participants.Any(p =>
                !game.PlayerStates.TryGetValue(p.PlayerId, out var s) || s.isAlive);

            if (participants.Count == 0 || !anyAlive || game.Enemies.IsFinished)
            {
                EndCoopRun(game);
            }
        }

        /// <summary>Alive players and their authoritative positions, for the enemy simulation.</summary>
        private List<CoopPlayerSnapshot> BuildCoopSnapshots(GameRoom game)
        {
            var snapshots = new List<CoopPlayerSnapshot>();

            foreach (var player in game.RedTeam.Concat(game.BlueTeam))
            {
                if (!player.InGame || !player.IsConnected) continue;

                if (game.PlayerStates.TryGetValue(player.PlayerId, out var state) && !state.isAlive)
                    continue;

                if (!game.PlayerPositions.TryGetValue(player.PlayerId, out var position))
                    continue;

                snapshots.Add(new CoopPlayerSnapshot
                {
                    PlayerId = player.PlayerId,
                    X = position.x,
                    Y = position.y,
                    Z = position.z,
                    Health = state?.health ?? 100,
                });
            }

            return snapshots;
        }

        /// <summary>Applies one enemy swing to its target, and reports the hit.</summary>
        private void ApplyEnemyAttack(GameRoom game, EnemyAttackEvent attack)
        {
            var victim = game.RedTeam.Concat(game.BlueTeam)
                .FirstOrDefault(p => p.PlayerId == attack.TargetPlayerId);
            if (victim == null) return;

            HitData hitData;
            bool killed = false;

            lock (game.StateLock)
            {
                if (!game.PlayerStates.TryGetValue(victim.PlayerId, out var state) || !state.isAlive)
                    return;

                state.health -= attack.Damage;
                killed = state.health <= 0;
                if (killed) state.health = 0;

                hitData = new HitData
                {
                    type = "player_hit",
                    // attackerId 0 marks "not a player" - the client uses this to distinguish an
                    // enemy hit from a player's, and to avoid looking up a player that is not there.
                    attackerId = 0,
                    attackerName = "enemy",
                    targetId = victim.PlayerId,
                    targetName = victim.Username,
                    damage = attack.Damage,
                    weaponId = 0,
                    isHeadshot = false,
                    isKill = killed,
                    timestamp = DateTime.UtcNow,
                };
            }

            serverManager.SendPacket(hitData, victim);

            if (killed)
            {
                HandlePlayerDeath(victim, game);
                BroadcastCoop(new
                {
                    type = "coop_player_down",
                    playerId = victim.PlayerId,
                    username = victim.Username,
                    timestamp = DateTime.UtcNow,
                }, game);
            }
        }

        /// <summary>
        /// Credits an enemy kill: the killer's run totals, their persistent shard balance, and
        /// the death broadcast.
        /// </summary>
        private void HandleEnemyDeath(GameRoom game, EnemyDeathEvent death)
        {
            int killerShardBalance = 0;

            if (death.DroppedShards && death.KillerPlayerId >= 0)
            {
                // Run totals, for the end-of-run summary.
                var earned = game.CoopShards.GetOrAdd(death.KillerPlayerId, _ => new int[4]);
                lock (game.StateLock)
                {
                    earned[(int)death.Tier] += death.ShardsGranted;
                }

                // Persistent balance, so shards survive the run.
                var account = loginManager?.GetPlayerById(death.KillerPlayerId);
                if (account != null)
                {
                    switch (death.Tier)
                    {
                        case ShardTier.Common: account.CommonShards += death.ShardsGranted; break;
                        case ShardTier.Rare: account.RareShards += death.ShardsGranted; break;
                        case ShardTier.Epic: account.EpicShards += death.ShardsGranted; break;
                        case ShardTier.Legendary: account.LegendaryShards += death.ShardsGranted; break;
                    }

                    killerShardBalance = death.Tier switch
                    {
                        ShardTier.Common => account.CommonShards,
                        ShardTier.Rare => account.RareShards,
                        ShardTier.Epic => account.EpicShards,
                        _ => account.LegendaryShards,
                    };

                    // Persist immediately: a shard should not be lost because the server dies
                    // before the player logs out.
                    loginManager.UpdatePlayerShards(account);
                }

                if (ShootDebug)
                {
                    Console.WriteLine($"[Coop] {death.KillerName} killed a level {death.Enemy.Level} enemy " +
                                      $"-> {death.ShardsGranted}x {death.Tier} shards (balance {killerShardBalance})");
                }
            }

            var deathPacket = new CoopEnemyDeath
            {
                enemyId = death.Enemy.EnemyId,
                enemyLevel = death.Enemy.Level,
                killerPlayerId = death.KillerPlayerId,
                killerName = death.KillerName,
                killed = death.Killed,
                droppedShards = death.DroppedShards,
                shardsGranted = death.ShardsGranted,
                tier = death.Tier,
                killerShardBalance = killerShardBalance,
                x = death.Enemy.X,
                y = death.Enemy.Y,
                z = death.Enemy.Z,
            };

            BroadcastCoop(deathPacket, game);

            // Tell the killer their own balance changed, so the HUD can update without a re-login.
            if (death.DroppedShards && death.KillerPlayerId >= 0)
            {
                var killer = game.RedTeam.Concat(game.BlueTeam)
                    .FirstOrDefault(p => p.PlayerId == death.KillerPlayerId);
                var account = loginManager?.GetPlayerById(death.KillerPlayerId);

                if (killer != null && account != null)
                {
                    serverManager.SendPacket(new ShardBalanceUpdate
                    {
                        commonShards = account.CommonShards,
                        rareShards = account.RareShards,
                        epicShards = account.EpicShards,
                        legendaryShards = account.LegendaryShards,
                        granted = true,
                        grantedAmount = death.ShardsGranted,
                        grantedTier = death.Tier,
                    }, killer);
                }
            }
        }

        /// <summary>Emits the end-of-run summary and closes the game.</summary>
        private void EndCoopRun(GameRoom game)
        {
            if (game.Enemies == null) return;

            foreach (var player in game.RedTeam.Concat(game.BlueTeam))
            {
                if (!player.IsConnected) continue;

                var earned = game.CoopShards.TryGetValue(player.PlayerId, out var totals)
                    ? totals : new int[4];

                serverManager.SendPacket(new CoopRunSummary
                {
                    wavesSurvived = game.Enemies.WaveNumber,
                    enemiesKilled = game.Enemies.KillsBy(player.PlayerId),
                    commonShards = earned[(int)ShardTier.Common],
                    rareShards = earned[(int)ShardTier.Rare],
                    epicShards = earned[(int)ShardTier.Epic],
                    legendaryShards = earned[(int)ShardTier.Legendary],
                }, player);
            }

            Console.WriteLine($"[Coop] Game {game.GameId} run over at wave {game.Enemies.WaveNumber}");

            // Reuse the normal teardown; the co-op path just does not compute a winner.
            EndGame(game);
        }

        private void BroadcastCoop(object packet, GameRoom game)
        {
            foreach (var player in game.RedTeam.Concat(game.BlueTeam))
            {
                if (player.InGame && player.IsConnected)
                    serverManager.SendPacket(packet, player);
            }
        }

        private void SendGameState(GameRoom game)        {
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

            // Reject non-finite input: a NaN/Infinity position or rotation would
            // poison the shared game-state dictionaries and corrupt hit detection
            // for every player in the match.
            if (input.position == null || input.rotation == null ||
                !IsFinite(input.position.x, input.position.y, input.position.z) ||
                !IsFinite(input.rotation.x, input.rotation.y))
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

            if (ShootDebug)
            {
                if (input == null)
                {
                    Console.WriteLine("[Shoot] rejected: payload did not deserialize");
                    return;
                }

                if (client == null)
                {
                    Console.WriteLine("[Shoot] rejected: unknown session token");
                    return;
                }

                Console.WriteLine($"[Shoot] {client.Username} (player {client.PlayerId}, team {client.TeamId}) " +
                                  $"inGame={client.InGame} gameId={client.CurrentGameId} " +
                                  $"weapon={input.weaponId} " +
                                  $"reported origin=({input.originX:0.##},{input.originY:0.##},{input.originZ:0.##}) " +
                                  $"target=({input.targetX:0.##},{input.targetY:0.##},{input.targetZ:0.##})");
            }

            if (client == null || !client.InGame)
                return;

            // Anti-spam: the fastest legit weapon (rifle) fires at ~10 shots/s
            // (0.1s apart). Allow a little margin, then drop anything faster.
            var now = DateTime.UtcNow;
            const double MinShotIntervalMs = 60; // ~16 shots/s ceiling
            if ((now - client.LastShotAt).TotalMilliseconds < MinShotIntervalMs)
                return;
            client.LastShotAt = now;

            if (!activeGames.TryGetValue(client.CurrentGameId, out var game))
                return;

            // A shot names either a player or a co-op enemy, never both: they are different id
            // spaces resolved by different managers. Enemy hits are handled first and return,
            // so the player-target logic below stays exactly as it was.
            if (input.hitEnemyId >= 0)
            {
                HandleEnemyShot(game, client, input);
                return;
            }

            // Reject non-finite shoot coordinates (see movement handler).
            if (!IsFinite(input.originX, input.originY, input.originZ) ||
                !IsFinite(input.targetX, input.targetY, input.targetZ))
                return;

            // Line of sight comes from the client: hitPlayerId is the player its raycast struck,
            // or -1. The client's ray is real physics and stops on walls, so an enemy behind
            // cover reports -1 and no damage is applied. The server holds no map geometry, so
            // this is the one fact it delegates - everything else below is enforced here.
            //
            // No geometric ray test is performed. It would add nothing: it tests against
            // last-known positions and has no concept of walls, so it would pass a through-wall
            // shot. The ray-vs-capsule hit test that used to live here has been removed.
            if (input.hitPlayerId < 0)
            {
                if (ShootDebug)
                    Console.WriteLine($"[Shoot] no player hit (hitPlayerId={input.hitPlayerId}) " +
                                      $"- no damage. A wall between shooter and target reports -1 here.");

                return;
            }

            if (input.hitPlayerId == client.PlayerId)
            {
                if (ShootDebug)
                    Console.WriteLine($"[Shoot] ignored: {client.Username} named themselves as the target");

                return;
            }

            // Find the named participant in this match.
            ClientConnection target = null;
            foreach (var candidate in game.RedTeam.Concat(game.BlueTeam))
            {
                if (candidate.PlayerId == input.hitPlayerId)
                {
                    target = candidate;
                    break;
                }
            }

            if (target == null)
            {
                if (ShootDebug)
                    Console.WriteLine($"[Shoot] ignored: player {input.hitPlayerId} is not in game " +
                                      $"{game.GameId} (forged or stale id)");

                return;
            }

            if (target.TeamId == client.TeamId)
            {
                if (ShootDebug)
                    Console.WriteLine($"[Shoot] ignored: {target.Username} is on the shooter's own team");

                return;
            }

            if (target.IsDead)
            {
                if (ShootDebug)
                    Console.WriteLine($"[Shoot] ignored: {target.Username} is already dead");

                return;
            }

            // Range gates damage falloff, and rejects a client naming a target on the far side
            // of the map. Falls back to the reported target point when no position is known.
            var targetPos = game.PlayerPositions.TryGetValue(target.PlayerId, out var tp)
                ? tp
                : new Vector3Data { x = input.targetX, y = input.targetY, z = input.targetZ };

            float ddx = targetPos.x - input.originX;
            float ddy = targetPos.y - input.originY;
            float ddz = targetPos.z - input.originZ;
            float distanceToTarget = (float)Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);

            const float MAX_RANGE = 200f;
            if (distanceToTarget > MAX_RANGE)
            {
                if (ShootDebug)
                    Console.WriteLine($"[Shoot] ignored: target {target.Username} is " +
                                      $"{distanceToTarget:0.#}m away (max {MAX_RANGE})");

                return;
            }

            // Headshot is positional: the reported impact point near the target's head height.
            float headHeight = targetPos.y + HEAD_HEIGHT;
            bool isHeadshot = Math.Abs(input.targetY - headHeight) <= HEADSHOT_TOLERANCE;

            if (ShootDebug)
            {
                Console.WriteLine($"[Shoot] {client.Username} hit {target.Username} " +
                                  $"(player {target.PlayerId}): distance={distanceToTarget:0.##}m " +
                                  $"headshot={isHeadshot} weapon={input.weaponId}");
            }

            {
                int damage = CalculateDamage(input.weaponId, distanceToTarget, isHeadshot);

                // Serialize the read-modify-write of health/kills/score so two
                // concurrent shoot packets cannot lose a decrement or kill credit.
                // Network broadcasts happen outside the lock.
                bool kill = false;
                HitData hitData;
                lock (game.StateLock)
                {
                    var targetState = game.PlayerStates.TryGetValue(target.PlayerId, out var existing)
                        ? existing : new PlayerStateInfo();

                    targetState.health -= damage;
                    game.PlayerStates[target.PlayerId] = targetState;

                    hitData = new HitData
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
                        kill = true;

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
                    }
                }

                if (kill)
                {
                    var shooterStats = game.PlayerStats[client.PlayerId];
                    var targetStats = game.PlayerStats[target.PlayerId];

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

        /// <summary>True when every value is a finite number (not NaN/Infinity).</summary>
        private static bool IsFinite(params float[] values)
        {
            foreach (var v in values)
            {
                if (float.IsNaN(v) || float.IsInfinity(v))
                    return false;
            }
            return true;
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
        // Guards read-modify-write updates to mutable state (PlayerStateInfo.health,
        // InGameStats kills/deaths/score, team scores) that are performed by
        // thread-pool shoot handlers. ConcurrentDictionary protects the collections
        // themselves; this lock protects the values inside them.
        public readonly object StateLock = new object();
        // Thread-safety: these dictionaries are read by the game-loop thread
        // (SendGameState/EndGame) while packet handlers running on the thread pool
        // mutate them. Plain Dictionary corrupts or throws under concurrent access;
        // ConcurrentDictionary makes those reads/writes safe.
        public ConcurrentDictionary<int, InGameStats> PlayerStats { get; set; } = new ConcurrentDictionary<int, InGameStats>();
        public ConcurrentDictionary<int, Vector3Data> PlayerPositions { get; set; } = new ConcurrentDictionary<int, Vector3Data>();
        public ConcurrentDictionary<int, Vector2Data> PlayerRotations { get; set; } = new ConcurrentDictionary<int, Vector2Data>();
        public ConcurrentDictionary<int, PlayerStateInfo> PlayerStates { get; set; } = new ConcurrentDictionary<int, PlayerStateInfo>();

        /// <summary>
        /// Enemy simulation for a co-op run. Null for every other game type, which is what
        /// distinguishes the two loops.
        /// </summary>
        public EnemyManager Enemies { get; set; }

        /// <summary>Shard totals earned per player during this run, mirrored to persistent balances as they drop.</summary>
        public ConcurrentDictionary<int, int[]> CoopShards { get; set; } = new ConcurrentDictionary<int, int[]>();
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