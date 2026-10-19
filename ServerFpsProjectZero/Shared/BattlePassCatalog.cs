// Shared/BattlePassCatalog.cs
//
// The season definition: how much XP each tier costs and what every tier pays out on the
// free and premium tracks.
//
// Like ItemCatalog this file is mirrored into the server project. The client renders the
// track from this data and the server validates claims against the exact same table, so a
// tampered client cannot invent a reward that does not exist.
using System;
using System.Collections.Generic;

namespace ServerFpsProjectZero.Shared
{
    public static class BattlePassCatalog
    {
        public const int SeasonId = 1;
        public const string SeasonName = "OPERATION BLACKOUT";

        public const int MaxTier = 30;

        /// <summary>XP required to move from one tier to the next.</summary>
        public const int XpPerTier = 250;

        /// <summary>Gold price of the premium track. Premium is bought with earned Gold, not real money.</summary>
        public const int PremiumPrice = 1200;

        public const int MaxXp = (MaxTier - 1) * XpPerTier;

        private static readonly List<BattlePassReward> rewards = Build();
        private static readonly BattlePassReward[] freeByTier = IndexTrack(premium: false);
        private static readonly BattlePassReward[] premiumByTier = IndexTrack(premium: true);

        public static IReadOnlyList<BattlePassReward> Rewards { get { return rewards; } }

        /// <summary>The reward for one tier/track, or null when that tier has none.</summary>
        public static BattlePassReward GetReward(int tier, bool premium)
        {
            if (tier < 1 || tier > MaxTier) return null;
            BattlePassReward[] track = premium ? premiumByTier : freeByTier;
            return track[tier];
        }

        /// <summary>Reward shown on the free track for a tier.</summary>
        public static BattlePassReward GetFreeReward(int tier)
        {
            return GetReward(tier, false);
        }

        public static BattlePassReward GetPremiumReward(int tier)
        {
            return GetReward(tier, true);
        }

        /// <summary>
        /// Current tier for an XP total. Tier 1 is available immediately at 0 XP, so the
        /// first 250 XP unlocks tier 2 rather than tier 1.
        /// </summary>
        public static int TierForXp(int xp)
        {
            if (xp < 0) xp = 0;
            int tier = 1 + xp / XpPerTier;
            if (tier > MaxTier) tier = MaxTier;
            return tier;
        }

        /// <summary>Cumulative XP needed to reach a tier. Tier 1 costs 0.</summary>
        public static int XpForTier(int tier)
        {
            if (tier <= 1) return 0;
            if (tier > MaxTier) tier = MaxTier;
            return (tier - 1) * XpPerTier;
        }

        public static bool IsMaxTier(BattlePassState state)
        {
            return state != null && state.Tier >= MaxTier;
        }

        /// <summary>
        /// Recomputes every derived field on a state object. Call this after changing Xp or
        /// claimed lists; it also repairs null lists on a profile deserialized from an older save.
        /// </summary>
        public static void Normalize(BattlePassState state)
        {
            if (state == null) return;

            if (state.ClaimedFreeTiers == null) state.ClaimedFreeTiers = new List<int>();
            if (state.ClaimedPremiumTiers == null) state.ClaimedPremiumTiers = new List<int>();

            // A new season starts everyone over. Claimed lists and premium are season-scoped,
            // so carrying them forward would hand out last season's rewards again.
            if (state.SeasonId != SeasonId)
            {
                state.SeasonId = SeasonId;
                state.Xp = 0;
                state.PremiumUnlocked = false;
                state.ClaimedFreeTiers.Clear();
                state.ClaimedPremiumTiers.Clear();
            }

            if (state.Xp < 0) state.Xp = 0;
            if (state.Xp > MaxXp) state.Xp = MaxXp;

            state.SeasonName = SeasonName;
            state.MaxTier = MaxTier;
            state.Tier = TierForXp(state.Xp);

            if (state.Tier >= MaxTier)
            {
                state.XpIntoCurrentTier = XpPerTier;
                state.XpForNextTier = 0;
            }
            else
            {
                state.XpIntoCurrentTier = state.Xp - XpForTier(state.Tier);
                state.XpForNextTier = XpPerTier;
            }

            state.UnclaimedCount = CountUnclaimed(state);
        }

        /// <summary>Adds XP and returns how many tiers were gained.</summary>
        public static int AddXp(BattlePassState state, int amount)
        {
            if (state == null || amount <= 0) return 0;

            Normalize(state);
            int before = state.Tier;

            long target = (long)state.Xp + amount;
            if (target > MaxXp) target = MaxXp;
            state.Xp = (int)target;

            Normalize(state);
            return state.Tier - before;
        }

        public static bool IsClaimed(BattlePassState state, int tier, bool premium)
        {
            if (state == null) return false;
            List<int> list = premium ? state.ClaimedPremiumTiers : state.ClaimedFreeTiers;
            return list != null && list.Contains(tier);
        }

        /// <summary>True when the tier is unlocked and the reward has not already been taken.</summary>
        public static bool CanClaim(BattlePassState state, int tier, bool premium)
        {
            if (state == null) return false;
            if (tier < 1 || tier > MaxTier) return false;
            if (tier > state.Tier) return false;
            if (premium && !state.PremiumUnlocked) return false;
            if (GetReward(tier, premium) == null) return false;
            return !IsClaimed(state, tier, premium);
        }

        public static int CountUnclaimed(BattlePassState state)
        {
            if (state == null) return 0;

            int count = 0;
            foreach (BattlePassReward reward in rewards)
            {
                if (reward.Tier > state.Tier) continue;
                if (reward.Premium && !state.PremiumUnlocked) continue;
                if (!IsClaimed(state, reward.Tier, reward.Premium)) count++;
            }
            return count;
        }

        private static BattlePassReward[] IndexTrack(bool premium)
        {
            var index = new BattlePassReward[MaxTier + 1];
            foreach (BattlePassReward reward in rewards)
            {
                if (reward.Premium != premium) continue;
                if (reward.Tier < 1 || reward.Tier > MaxTier) continue;
                index[reward.Tier] = reward;
            }
            return index;
        }

        private static List<BattlePassReward> Build()
        {
            var list = new List<BattlePassReward>();

            // ---- Free track ----
            list.Add(Gold(1, false, 250));
            list.Add(Shards(2, false, ShardTier.Common, 20));
            list.Add(Crate(3, false, ShardTier.Common));
            list.Add(Item(4, false, 1002));          // Desert Strike (AR-4 skin)
            list.Add(Item(5, false, 5));             // Vector-9
            list.Add(Gold(6, false, 300));
            list.Add(Shards(7, false, ShardTier.Rare, 15));
            list.Add(Crate(8, false, ShardTier.Rare));
            list.Add(Item(9, false, 1003));          // Bloodline (Vector-9 skin)
            list.Add(Item(10, false, 6));            // Breacher-12
            list.Add(Gold(11, false, 400));
            list.Add(Item(12, false, 1014));         // Deep Sea (Vector-9 skin)
            list.Add(Crate(13, false, ShardTier.Epic));
            list.Add(Item(14, false, 1006));         // Gold Standard (M9 skin)
            list.Add(Item(15, false, 8));            // Bastion-60
            list.Add(Gold(16, false, 500));
            list.Add(Shards(17, false, ShardTier.Rare, 20));
            list.Add(Item(18, false, 2002));         // Urban Ghost (character)
            list.Add(Item(19, false, 1011));         // Gunmetal (knife skin)
            list.Add(Item(20, false, 9));            // M14 Marksman
            list.Add(Gold(21, false, 600));
            list.Add(Shards(22, false, ShardTier.Rare, 30));
            list.Add(Crate(23, false, ShardTier.Legendary));
            list.Add(Item(24, false, 1008));         // Carbon Fiber (M14 skin)
            list.Add(Item(25, false, 10));           // .44 Judge
            list.Add(Gold(26, false, 700));
            list.Add(Shards(27, false, ShardTier.Epic, 25));
            list.Add(Item(28, false, 2004));         // Night Ops (character)
            list.Add(Item(29, false, 1009));         // Crimson Fang (Jackal skin)
            list.Add(Item(30, false, 12));           // Trident-3

            // ---- Premium track ----
            list.Add(Gold(1, true, 500));
            list.Add(Item(2, true, 1013));           // Solar Flare (AR-4 skin)
            list.Add(Shards(3, true, ShardTier.Rare, 20));
            list.Add(Item(4, true, 2003));           // Desert Fox (character)
            list.Add(Item(5, true, 11));             // Jackal PDW
            list.Add(Crate(6, true, ShardTier.Epic));
            list.Add(Item(7, true, 1007));           // Toxic Bloom (Bastion skin)
            list.Add(Gold(8, true, 600));
            list.Add(Shards(9, true, ShardTier.Rare, 30));
            list.Add(Item(10, true, 2005));          // Crimson Guard (character)
            list.Add(Item(11, true, 1015));          // Whiteout (M14 skin)
            list.Add(Gold(12, true, 800));
            list.Add(Item(13, true, 1016));          // Royal Decree (.44 skin)
            list.Add(Item(14, true, 1004));          // Neon Circuit (Breacher skin)
            list.Add(Item(15, true, 2006));          // Golden Warlord (character)
            list.Add(Crate(16, true, ShardTier.Legendary));
            list.Add(Item(17, true, 1005));          // Arctic Digital (Longbow skin)
            list.Add(Gold(18, true, 900));
            list.Add(Shards(19, true, ShardTier.Epic, 30));
            list.Add(Item(20, true, 7));             // Longbow .50
            list.Add(Gold(21, true, 1000));
            list.Add(Shards(22, true, ShardTier.Legendary, 40));
            list.Add(Item(23, true, 1012));          // Ember (.44 skin)
            list.Add(Item(24, true, 1010));          // Void Rift (Trident skin)
            list.Add(Crate(25, true, ShardTier.Legendary));
            list.Add(Item(26, true, 2008));          // Frostbite (character)
            list.Add(Shards(27, true, ShardTier.Epic, 40));
            list.Add(Shards(28, true, ShardTier.Legendary, 50));
            list.Add(Gold(29, true, 1500));
            list.Add(Item(30, true, 2007));          // Shadow Operative (character)

            return list;
        }

        private static BattlePassReward Gold(int tier, bool premium, int amount)
        {
            return Make(tier, premium, RewardKind.Gold, amount, 0, ShardTier.Common);
        }

        private static BattlePassReward Shards(int tier, bool premium, ShardTier shardTier, int amount)
        {
            return Make(tier, premium, RewardKind.Shards, amount, 0, shardTier);
        }

        private static BattlePassReward Crate(int tier, bool premium, ShardTier shardTier)
        {
            return Make(tier, premium, RewardKind.Crate, 1, 0, shardTier);
        }

        private static BattlePassReward Item(int tier, bool premium, int itemId)
        {
            return Make(tier, premium, RewardKind.Item, 1, itemId, ShardTier.Common);
        }

        private static BattlePassReward Make(
            int tier, bool premium, RewardKind kind, int amount, int itemId, ShardTier shardTier)
        {
            var reward = new BattlePassReward
            {
                Tier = tier,
                Premium = premium,
                Kind = kind,
                Amount = amount,
                ItemId = itemId,
                ShardTier = shardTier,
            };

            reward.Label = Describe(reward);
            reward.Rarity = RarityOf(reward);
            return reward;
        }

        /// <summary>Human-readable reward text used by both the UI and server messages.</summary>
        public static string Describe(BattlePassReward reward)
        {
            if (reward == null) return "";

            switch (reward.Kind)
            {
                case RewardKind.Gold:
                    return reward.Amount + " Gold";
                case RewardKind.Shards:
                    return reward.Amount + " " + reward.ShardTier + " Shards";
                case RewardKind.Crate:
                    return reward.ShardTier + " Crate";
                case RewardKind.Item:
                    return ItemCatalog.GetName(reward.ItemId);
                default:
                    return "Reward";
            }
        }

        private static string RarityOf(BattlePassReward reward)
        {
            if (reward == null) return WeaponRarity.Common.ToString();

            switch (reward.Kind)
            {
                case RewardKind.Item:
                    return ItemCatalog.GetRarity(reward.ItemId);
                case RewardKind.Gold:
                    return "Currency";
                case RewardKind.Shards:
                case RewardKind.Crate:
                    return reward.ShardTier.ToString();
                default:
                    return WeaponRarity.Common.ToString();
            }
        }
    }
}
