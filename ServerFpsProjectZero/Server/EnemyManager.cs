using ProjectZero.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using ServerFpsProjectZero.Shared;

namespace ServerFpsProjectZero.Server
{
    /// <summary>
    /// What one enemy is doing. Enemies are melee-only, so the state machine is small:
    /// walk toward the closest player, and swing when in range.
    /// </summary>
    public class Enemy
    {
        public int EnemyId;
        public int Level;

        public float X, Y, Z;
        public float Yaw;

        public int Health;
        public int MaxHealth;
        public float Speed;

        /// <summary>Player being chased, or -1 when nobody is in aggro range.</summary>
        public int TargetPlayerId = -1;

        /// <summary>Server time (seconds since run start) when this enemy may swing again.</summary>
        public double NextAttackAt;

        /// <summary>Server time the current swing started, for the client's attack animation.</summary>
        public double AttackAnimUntil;

        public bool IsDead;

        public float DistanceTo(float x, float z)
        {
            float dx = X - x, dz = Z - z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }
    }

    public class EnemyDeathEvent
    {
        public Enemy Enemy;
        public int KillerPlayerId = -1;
        public string KillerName;
        public bool Killed;
        public bool DroppedShards;
        public ShardTier Tier;
        public int ShardsGranted;
    }

    public class EnemyAttackEvent
    {
        public int EnemyId;
        public int TargetPlayerId;
        public int Damage;
        public bool WasKillingBlow;
    }

    public enum EnemyPhase
    {
        /// <summary>Counting down to the next wave.</summary>
        Intermission,

        /// <summary>Enemies alive and fighting.</summary>
        Fighting,

        /// <summary>Run over - all players down.</summary>
        Finished,
    }

    /// <summary>
    /// Owns the enemies for one co-op run: spawning waves, chasing players, melee damage, and
    /// rolling the shard drop on death.
    ///
    /// Deliberately deterministic. Every decision that involves randomness takes its roll as a
    /// parameter rather than calling a generator internally, so a test or a simulation can drive
    /// the whole run from a fixed sequence. The game loop supplies the rolls.
    /// </summary>
    public class EnemyManager
    {
        private readonly Dictionary<int, Enemy> enemies = new Dictionary<int, Enemy>();
        private readonly Random random = new Random();

        private int nextEnemyId = 1;
        private double runTime;

        public int GameId { get; }
        public int WaveNumber { get; private set; }
        public int WaveTotal { get; }
        public EnemyPhase Phase { get; private set; } = EnemyPhase.Intermission;

        /// <summary>Seconds remaining before the next wave spawns.</summary>
        public double NextWaveAt { get; private set; }

        /// <summary>Shards earned by each player during this run, by tier.</summary>
        public Dictionary<int, int[]> ShardsEarned { get; } = new Dictionary<int, int[]>();

        /// <summary>Enemies killed by each player during this run.</summary>
        public Dictionary<int, int> Kills { get; } = new Dictionary<int, int>();

        // Tunables for the wave curve.
        private const double FirstWaveDelay = 6.0;
        private const double IntermissionDuration = 8.0;

        /// <summary>Enemies per wave. Grows, so later waves are genuinely harder.</summary>
        private const int BaseEnemiesPerWave = 3;
        private const float EnemiesPerWaveGrowth = 1.6f;

        /// <summary>
        /// Enemy level for a wave, and how much it varies. Level is the difficulty axis, so this
        /// is what drives both lethality and shard quality.
        /// </summary>
        private static int BaseLevelForWave(int wave) => 1 + (wave - 1);

        /// <summary>How far from a player enemies spawn, so they are never on top of someone.</summary>
        private const float SpawnRingMin = 18f;
        private const float SpawnRingMax = 28f;

        public EnemyManager(int gameId, int waveTotal = 20)
        {
            GameId = gameId;
            WaveTotal = Math.Max(1, waveTotal);
            NextWaveAt = FirstWaveDelay;
        }

        /// <summary>
        /// Starts the run now instead of after the opening countdown.
        ///
        /// Used by tests to avoid depending on wall-clock waits, and available to a future
        /// "skip prep" option. Behaviourally identical to the countdown expiring.
        /// </summary>
        public void BeginRunImmediately()
        {
            runTime = 0;
            NextWaveAt = 0;
            Phase = EnemyPhase.Intermission;
        }

        public IReadOnlyCollection<Enemy> LiveEnemies => enemies.Values;

        public int AliveCount => enemies.Values.Count(e => !e.IsDead);

        public bool IsFinished => Phase == EnemyPhase.Finished;

        /// <summary>Snapshot of live enemies for the clients.</summary>
        public List<CoopEnemyState> Snapshot()
        {
            return enemies.Values
                .Where(e => !e.IsDead)
                .Select(ToState)
                .ToList();
        }

        public static CoopEnemyState ToState(Enemy e)
        {
            return new CoopEnemyState
            {
                enemyId = e.EnemyId,
                level = e.Level,
                x = e.X,
                y = e.Y,
                z = e.Z,
                yaw = e.Yaw,
                health = Math.Max(0, e.Health),
                maxHealth = e.MaxHealth,
                targetPlayerId = e.TargetPlayerId,
                isAttacking = false,
            };
        }

        public CoopWaveState WaveState(int playersAlive)
        {
            return new CoopWaveState
            {
                waveNumber = WaveNumber,
                waveTotal = WaveTotal,
                enemiesRemaining = AliveCount,
                nextWaveIn = Phase == EnemyPhase.Intermission
                    ? (float)Math.Max(0, NextWaveAt - runTime)
                    : 0f,
                isIntermission = Phase == EnemyPhase.Intermission,
                playersAlive = playersAlive,
            };
        }

        /// <summary>
        /// Advances one tick.
        ///
        /// <paramref name="players"/> must be the currently-alive players with their authoritative
        /// positions. Damage is reported through <paramref name="attacks"/> rather than applied
        /// here, so the caller owns all mutation of player state and the locking around it.
        /// </summary>
        public void Tick(
            double deltaSeconds,
            IReadOnlyList<CoopPlayerSnapshot> players,
            out List<EnemyDeathEvent> deaths,
            out List<EnemyAttackEvent> attacks)
        {
            deaths = new List<EnemyDeathEvent>();
            attacks = new List<EnemyAttackEvent>();

            if (Phase == EnemyPhase.Finished) return;

            runTime += deltaSeconds;

            // A run with nobody in the world is not over - it has not started. Ending here was
            // ending the run at wave 0, before the first player's position had been reported.
            if (players.Count == 0)
            {
                return;
            }

            if (Phase == EnemyPhase.Intermission)
            {
                if (runTime >= NextWaveAt)
                    SpawnWave(players);
                return;   // enemies do not act during an intermission
            }

            MoveAndAttack(deltaSeconds, players, attacks);

            // Reap any enemy that died this tick.
            foreach (var dead in enemies.Values.Where(e => e.IsDead).ToList())
            {
                enemies.Remove(dead.EnemyId);
            }

            if (AliveCount == 0)
            {
                if (WaveNumber >= WaveTotal)
                {
                    Phase = EnemyPhase.Finished;
                    return;
                }

                Phase = EnemyPhase.Intermission;
                NextWaveAt = runTime + IntermissionDuration;
            }
        }

        /// <summary>
        /// Spawns the next wave, positioned on a ring around the players.
        ///
        /// Spawning and positioning are one operation on purpose: splitting them into two calls
        /// leaves a window where enemies exist at the origin, which is both wrong and a free kill
        /// if a shot lands in between.
        /// </summary>
        public List<Enemy> SpawnWave(IReadOnlyList<CoopPlayerSnapshot> players)
        {
            WaveNumber++;

            int level = BaseLevelForWave(WaveNumber);
            int count = BaseEnemiesPerWave + (int)Math.Floor((WaveNumber - 1) * EnemiesPerWaveGrowth);

            var spawned = new List<Enemy>(count);
            for (int i = 0; i < count; i++)
            {
                // Levels vary slightly within a wave so a wave is not uniform, but the wave
                // number remains the dominant term.
                int enemyLevel = Math.Max(1, level + random.Next(-1, 2));
                spawned.Add(Spawn(level: enemyLevel, x: 0f, y: 0f, z: 0f));
            }

            PlaceAroundPlayers(spawned, players);
            Phase = EnemyPhase.Fighting;
            return spawned;
        }

        /// <summary>Creates one enemy at a position. Position is set by the caller for waves.</summary>
        public Enemy Spawn(int level, float x, float y, float z)
        {
            var enemy = new Enemy
            {
                EnemyId = nextEnemyId++,
                Level = Math.Max(1, level),
                X = x,
                Y = y,
                Z = z,
                Health = EnemyScaling.HealthForLevel(level),
                MaxHealth = EnemyScaling.HealthForLevel(level),
                Speed = EnemyScaling.SpeedForLevel(level),
            };
            enemies[enemy.EnemyId] = enemy;
            return enemy;
        }

        /// <summary>
        /// Positions a freshly spawned wave on a ring around the players, so enemies come from
        /// off to the sides rather than materialising on top of anyone.
        /// </summary>
        private void PlaceAroundPlayers(List<Enemy> wave, IReadOnlyList<CoopPlayerSnapshot> players)
        {
            if (players.Count == 0 || wave.Count == 0) return;

            float angleStep = (float)(Math.PI * 2.0 / wave.Count);
            float baseAngle = (float)(random.NextDouble() * Math.PI * 2.0);

            for (int i = 0; i < wave.Count; i++)
            {
                var anchor = players[i % players.Count];
                float angle = baseAngle + angleStep * i;
                float radius = SpawnRingMin + (float)random.NextDouble() * (SpawnRingMax - SpawnRingMin);

                wave[i].X = anchor.X + (float)Math.Cos(angle) * radius;
                wave[i].Y = anchor.Y;
                wave[i].Z = anchor.Z + (float)Math.Sin(angle) * radius;
            }
        }

        private void MoveAndAttack(
            double deltaSeconds,
            IReadOnlyList<CoopPlayerSnapshot> players,
            List<EnemyAttackEvent> attacks)
        {
            var alive = enemies.Values.Where(e => !e.IsDead).ToList();

            foreach (var enemy in alive)
            {
                // Retarget: chase the closest living player within aggro range.
                var target = NearestPlayer(enemy, players);
                if (target == null)
                {
                    enemy.TargetPlayerId = -1;
                    continue;
                }

                enemy.TargetPlayerId = target.PlayerId;

                float dx = target.X - enemy.X;
                float dz = target.Z - enemy.Z;
                float distance = (float)Math.Sqrt(dx * dx + dz * dz);
                if (distance > 0.0001f)
                {
                    enemy.Yaw = (float)(Math.Atan2(dx, dz) * 180.0 / Math.PI);
                }

                if (distance <= EnemyScaling.AttackRange)
                {
                    // In range: swing on cooldown instead of walking into the player.
                    if (runTime >= enemy.NextAttackAt)
                    {
                        enemy.NextAttackAt = runTime + EnemyScaling.AttackCooldownForLevel(enemy.Level);
                        enemy.AttackAnimUntil = runTime + 0.35;

                        attacks.Add(new EnemyAttackEvent
                        {
                            EnemyId = enemy.EnemyId,
                            TargetPlayerId = target.PlayerId,
                            Damage = EnemyScaling.DamageForLevel(enemy.Level),
                        });
                    }
                    continue;
                }

                // Walk toward the player, but stop short of other enemies so they do not stack.
                float step = enemy.Speed * (float)deltaSeconds;
                float moveX = dx / distance * step;
                float moveZ = dz / distance * step;

                var (pushX, pushZ) = Separation(enemy, alive);
                enemy.X += moveX + pushX;
                enemy.Z += moveZ + pushZ;
            }
        }

        /// <summary>Simple separation so a crowd does not collapse into a single point.</summary>
        private (float x, float z) Separation(Enemy self, List<Enemy> others)
        {
            float pushX = 0f, pushZ = 0f;

            foreach (var other in others)
            {
                if (other.EnemyId == self.EnemyId) continue;

                float dx = self.X - other.X;
                float dz = self.Z - other.Z;
                float distSq = dx * dx + dz * dz;
                if (distSq >= EnemyScaling.SeparationDistance * EnemyScaling.SeparationDistance || distSq < 0.000001f)
                    continue;

                float dist = (float)Math.Sqrt(distSq);
                float overlap = (EnemyScaling.SeparationDistance - dist) * 0.5f;
                pushX += dx / dist * overlap;
                pushZ += dz / dist * overlap;
            }

            return (pushX, pushZ);
        }

        private static CoopPlayerSnapshot NearestPlayer(Enemy enemy, IReadOnlyList<CoopPlayerSnapshot> players)
        {
            CoopPlayerSnapshot best = null;
            float bestDistance = EnemyScaling.AggroRadius;

            foreach (var player in players)
            {
                float distance = enemy.DistanceTo(player.X, player.Z);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = player;
                }
            }

            return best;
        }

        /// <summary>
        /// Applies damage from a player's shot.
        ///
        /// Returns the death event when this was the killing blow, otherwise null. The shard roll
        /// is taken here so the killer is credited exactly once, on the killing blow only.
        /// </summary>
        public EnemyDeathEvent ApplyDamage(int enemyId, int damage, int attackerPlayerId, string attackerName)
        {
            if (!enemies.TryGetValue(enemyId, out var enemy) || enemy.IsDead)
                return null;

            enemy.Health -= Math.Max(0, damage);
            if (enemy.Health > 0)
                return null;

            enemy.IsDead = true;

            var death = new EnemyDeathEvent
            {
                Enemy = enemy,
                KillerPlayerId = attackerPlayerId,
                KillerName = attackerName,
                Killed = true,
            };

            if (attackerPlayerId >= 0)
            {
                Kills.TryGetValue(attackerPlayerId, out int kills);
                Kills[attackerPlayerId] = kills + 1;
            }

            // Roll the shard drop. Level is the only input, which is the requirement: harder
            // enemies are likelier to give better shards, independent of what kind of enemy it is.
            if (ShardDropTable.TryRollTier(enemy.Level, random.NextDouble(), random.NextDouble(), out var tier))
            {
                death.DroppedShards = true;
                death.Tier = tier;
                death.ShardsGranted = EnemyScaling.ShardAmountForTier(tier);

                if (attackerPlayerId >= 0)
                {
                    if (!ShardsEarned.TryGetValue(attackerPlayerId, out var earned))
                    {
                        earned = new int[4];
                        ShardsEarned[attackerPlayerId] = earned;
                    }
                    earned[(int)tier] += death.ShardsGranted;
                }
            }

            return death;
        }

        /// <summary>
        /// Damage from an enemy swing, resolved against a player's current health so the caller
        /// can report whether it was fatal.
        /// </summary>
        public static bool WouldKill(int currentHealth, int damage) => currentHealth - damage <= 0;

        public int[] EarnedBy(int playerId)
        {
            return ShardsEarned.TryGetValue(playerId, out var earned) ? earned : new int[4];
        }

        public int KillsBy(int playerId)
        {
            return Kills.TryGetValue(playerId, out int kills) ? kills : 0;
        }
    }

    /// <summary>
    /// The slice of a player the enemy simulation needs. Keeps EnemyManager independent of the
    /// connection and game-room types.
    /// </summary>
    public class CoopPlayerSnapshot
    {
        public int PlayerId;
        public float X;
        public float Y;
        public float Z;
        public int Health;
    }
}
