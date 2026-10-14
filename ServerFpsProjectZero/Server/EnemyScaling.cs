using System;
using System.Collections.Generic;
using ServerFpsProjectZero.Shared;

namespace ServerFpsProjectZero.Server
{
    /// <summary>
    /// Everything that scales with an enemy's level.
    ///
    /// There is one enemy archetype (melee) rather than a type per behaviour. Level is the whole
    /// difficulty axis: it scales health, damage, speed and the shard drop table together, so
    /// the difficulty curve and the reward curve stay in step by construction.
    /// </summary>
    public static class EnemyScaling
    {
        // Level 1 baseline.
        public const int BaseHealth = 100;
        public const int BaseDamage = 10;
        public const float BaseSpeed = 2.6f;

        /// <summary>How far a swing reaches. Melee only, so this stays short.</summary>
        public const float AttackRange = 1.9f;

        /// <summary>Seconds between a single enemy's swings.</summary>
        public const float AttackCooldown = 1.15f;

        /// <summary>Players within this radius are valid targets for an idle enemy.</summary>
        public const float AggroRadius = 60f;

        /// <summary>Enemy radius and height, used for hit validation and separation.</summary>
        public const float BodyRadius = 0.45f;
        public const float BodyHeight = 1.8f;

        /// <summary>Enemies stop this far apart so they do not stack into one point.</summary>
        public const float SeparationDistance = 1.1f;

        // Growth per level. Deliberately modest: enemies get meaningfully harder without
        // becoming bullet sponges, and the reward curve is what carries progression.
        private const float HealthGrowthPerLevel = 0.28f;   // +28% of base per level
        private const float DamageGrowthPerLevel = 0.18f;
        private const float SpeedGrowthPerLevel = 0.035f;

        /// <summary>Speed is capped so high-level enemies stay outrunnable in a straight line.</summary>
        private const float MaxSpeed = 6.5f;

        public static int HealthForLevel(int level)
        {
            level = Math.Max(1, level);
            return (int)Math.Round(BaseHealth * (1f + HealthGrowthPerLevel * (level - 1)));
        }

        public static int DamageForLevel(int level)
        {
            level = Math.Max(1, level);
            return (int)Math.Round(BaseDamage * (1f + DamageGrowthPerLevel * (level - 1)));
        }

        public static float SpeedForLevel(int level)
        {
            level = Math.Max(1, level);
            return Math.Min(MaxSpeed, BaseSpeed * (1f + SpeedGrowthPerLevel * (level - 1)));
        }

        /// <summary>
        /// Seconds between swings at a given level. Higher levels attack a little faster.
        /// </summary>
        public static float AttackCooldownForLevel(int level)
        {
            level = Math.Max(1, level);
            float cooldown = AttackCooldown * (1f - 0.03f * (level - 1));
            return Math.Max(0.45f, cooldown);
        }

        /// <summary>Shard value of one drop at a given tier. Higher tiers are worth more.</summary>
        public static int ShardAmountForTier(ShardTier tier)
        {
            return tier switch
            {
                ShardTier.Common => 1,
                ShardTier.Rare => 1,
                ShardTier.Epic => 1,
                ShardTier.Legendary => 1,
                _ => 1
            };
        }
    }

    /// <summary>
    /// Decides which shard tier an enemy drops.
    ///
    /// The requirement was that the drop is NOT fixed by enemy type: the harder the enemy, the
    /// better the chance of a higher tier. So this is a level-driven distribution, expressed as
    /// weights over the four tiers, with two properties chosen deliberately:
    ///
    ///   * Level 1 is almost always Common, so early kills give a predictable trickle.
    ///   * The expected tier rises smoothly and without a ceiling, so there is always a reason
    ///     to push one more wave.
    ///
    /// Weights are computed rather than tabulated so behaviour stays sane at any level - a
    /// hand-written table would need extending the moment someone reaches a wave nobody
    /// anticipated.
    /// </summary>
    public static class ShardDropTable
    {
        /// <summary>Chance that a kill drops anything at all. Keeps shards feeling earned.</summary>
        public const float BaseDropChance = 0.55f;

        /// <summary>Drop chance rises slowly with level, capped so a drop is never guaranteed.</summary>
        private const float DropChanceGrowthPerLevel = 0.012f;
        private const float MaxDropChance = 0.9f;

        /// <summary>
        /// How many tiers above Common start to appear, and how fast each climbs.
        /// Rare appears early, Legendary is a genuine event until high levels.
        /// </summary>
        private const float RareStartLevel = 2f;
        private const float EpicStartLevel = 4f;
        private const float LegendaryStartLevel = 10f;

        // Rare grows slowly on purpose. An earlier tuning had it outpacing Epic+Legendary
        // combined even at level 20, which made the higher tiers feel unreachable - the point of
        // the curve is that pushing further is always worth it.
        private const float RareWeightPerLevel = 0.22f;
        private const float EpicWeightPerLevel = 0.34f;
        private const float LegendaryWeightPerLevel = 0.20f;

        /// <summary>Common's weight decays as levels rise, so higher tiers take over.</summary>
        private const float CommonWeightDecayPerLevel = 0.12f;
        private const float MinCommonWeight = 0.6f;

        /// <summary>Chance the kill produces a drop at all.</summary>
        public static float DropChanceForLevel(int level)
        {
            level = Math.Max(1, level);
            return Math.Min(MaxDropChance, BaseDropChance + DropChanceGrowthPerLevel * (level - 1));
        }

        /// <summary>
        /// Weight of each tier at a given level. Weights are relative, not probabilities; use
        /// <see cref="RollTier"/> for the actual pick.
        /// </summary>
        public static float[] WeightsForLevel(int level)
        {
            level = Math.Max(1, level);

            float common = Math.Max(MinCommonWeight, 10f - CommonWeightDecayPerLevel * (level - 1));
            float rare = Math.Max(0f, (level - RareStartLevel + 1f)) * RareWeightPerLevel;
            float epic = Math.Max(0f, (level - EpicStartLevel + 1f)) * EpicWeightPerLevel;
            float legendary = Math.Max(0f, (level - LegendaryStartLevel + 1f)) * LegendaryWeightPerLevel;

            return new[] { common, rare, epic, legendary };
        }

        /// <summary>
        /// Rolls a tier for this level, or returns false when the kill drops nothing.
        ///
        /// <paramref name="roll01"/> and <paramref name="tierRoll01"/> are supplied by the caller
        /// so this stays deterministic and testable without seeding a generator here.
        /// </summary>
        public static bool TryRollTier(int level, double roll01, double tierRoll01, out ShardTier tier)
        {
            tier = ShardTier.Common;

            if (roll01 > DropChanceForLevel(level))
                return false;

            var weights = WeightsForLevel(level);
            float total = 0f;
            foreach (var w in weights) total += w;
            if (total <= 0f) return false;

            double target = Math.Clamp(tierRoll01, 0.0, 0.999999) * total;
            float cumulative = 0f;
            for (int i = 0; i < weights.Length; i++)
            {
                cumulative += weights[i];
                if (target < cumulative)
                {
                    tier = (ShardTier)i;
                    return true;
                }
            }

            tier = (ShardTier)(weights.Length - 1);
            return true;
        }

        /// <summary>Expected tier value for a level, for documentation and tuning output.</summary>
        public static double ExpectedTier(int level)
        {
            var weights = WeightsForLevel(level);
            double total = 0, weighted = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                total += weights[i];
                weighted += weights[i] * i;
            }
            return total <= 0 ? 0 : weighted / total;
        }

        /// <summary>Human-readable distribution, used by the tuning console command.</summary>
        public static string DescribeLevel(int level)
        {
            var weights = WeightsForLevel(level);
            double total = 0;
            foreach (var w in weights) total += w;

            var parts = new List<string>();
            for (int i = 0; i < weights.Length; i++)
            {
                var name = ((ShardTier)i).ToString();
                double pct = total > 0 ? weights[i] / total * 100.0 : 0;
                parts.Add($"{name} {pct:0.#}%");
            }

            return $"level {level}: drop {DropChanceForLevel(level) * 100:0.#}%, " +
                   $"{string.Join(", ", parts)} (expected tier {ExpectedTier(level):0.##})";
        }
    }
}
