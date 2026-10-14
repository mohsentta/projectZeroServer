using ServerFpsProjectZero.Server;
using ServerFpsProjectZero.Shared;
using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;

namespace ServerFpsProjectZero.Models
{
    public class Player
    {
        // Basic Identification
        public int PlayerId { get; private set; }
        public string Username { get; private set; }
        public string Email { get; private set; }
        public string PasswordHash { get; private set; }

        // Connection Information
        public IPEndPoint ClientEndpoint { get; set; }
        public string SessionToken { get; set; }
        public DateTime TokenExpiry { get; set; }
        public bool IsConnected { get; set; }
        public bool IsInGame { get; set; }
        public DateTime LastHeartbeat { get; set; }

        // Matchmaking Data
        public int MMR { get; set; }
        public int Rank { get; set; }
        public DateTime QueueStartTime { get; set; }
        public bool IsInQueue { get; set; }

        // Game Session Data
        public int CurrentGameId { get; set; }
        public int TeamId { get; set; }
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public int Score { get; set; }

        // Player Progression
        public int Level { get; set; }
        public int Experience { get; set; }
        public int ExperienceToNextLevel { get; set; }
        public int Gold { get; set; }
        public int Money { get; set; }

        // Shards, one pool per rarity (see PlayerProfile). Crafting currency, not spendable
        // on the shop in the same way Gold is.
        public int CommonShards { get; set; }
        public int RareShards { get; set; }
        public int EpicShards { get; set; }
        public int LegendaryShards { get; set; }

        // Equipment & Customization
        public PlayerLoadout Loadout { get; set; }
        public PlayerInventory Inventory { get; set; }

        // Player Stats
        public PlayerStats Stats { get; set; }

        // Security & Anti-Cheat
        public List<string> ActiveCheatWarnings { get; set; }
        public int CheatViolations { get; set; }
        public bool IsBanned { get; set; }
        public DateTime? BanExpiry { get; set; }

        // Collections
        public List<int> FriendIds { get; set; }
        public List<int> ClanIds { get; set; }

        // Timestamps
        public DateTime CreatedAt { get; set; }
        public DateTime LastLogin { get; set; }
        public int LoginStreak { get; set; }
        public TimeSpan TotalPlayTime { get; set; }

        // Daily-login reward (session-lifetime; not persisted independently of LoginStreak)
        public bool DailyRewardClaimedToday { get; set; }
        public int DailyRewardGoldGranted { get; set; }
        public int DailyRewardXpGranted { get; set; }

        public Player()
        {
            Inventory = new PlayerInventory();
            Loadout = new PlayerLoadout();
            Stats = new PlayerStats();
            ActiveCheatWarnings = new List<string>();
            FriendIds = new List<int>();
            ClanIds = new List<int>();
        }

        public Player(int playerId, string username, string email, string passwordHash)
        {
            PlayerId = playerId;
            Username = username;
            Email = email;
            PasswordHash = passwordHash;

            SessionToken = GenerateToken();
            TokenExpiry = DateTime.UtcNow.AddDays(7);
            IsConnected = false;
            IsInGame = false;
            LastHeartbeat = DateTime.UtcNow;

            MMR = 1200;
            Rank = 1;
            IsInQueue = false;

            CurrentGameId = -1;
            TeamId = -1;
            Kills = 0;
            Deaths = 0;
            Score = 0;

            Level = 1;
            Experience = 0;
            ExperienceToNextLevel = CalculateExperienceNeeded(2);
            Gold = 500;
            Money = 500;

            // New accounts start with no shards - they are earned.
            CommonShards = 0;
            RareShards = 0;
            EpicShards = 0;
            LegendaryShards = 0;

            // Initialize with default items.
            //
            // ITEM ID CONVENTION - ids are globally unique across every item type:
            //     1     - 999    weapons, including grenades and melee
            //     1000  - 1999   skins
            //     2000+          reserved
            // 1..4 matches the client's own weapon registry (WeaponManager.LoadWeaponData):
            // 1 Assault Rifle, 2 Pistol, 3 Combat Knife, 4 Frag Grenade. Grenades are a
            // WeaponType here, so they live in the weapon range rather than their own.
            Inventory = new PlayerInventory();
            Inventory.UnlockedItems = new List<int> { 1, 2, 3, 4, 1001 };
            Inventory.LootBoxes = new List<LootBox>();

            Loadout = new PlayerLoadout();
            Stats = new PlayerStats();
            ActiveCheatWarnings = new List<string>();
            CheatViolations = 0;
            IsBanned = false;

            FriendIds = new List<int>();
            ClanIds = new List<int>();

            CreatedAt = DateTime.UtcNow;
            LastLogin = DateTime.UtcNow;
            TotalPlayTime = TimeSpan.Zero;
        }

        private string GenerateToken()
        {
            using (var rng = RandomNumberGenerator.Create())
            {
                byte[] tokenData = new byte[32];
                rng.GetBytes(tokenData);
                return Convert.ToBase64String(tokenData);
            }
        }

        private int CalculateExperienceNeeded(int targetLevel)
        {
            return (int)(100 * Math.Pow(targetLevel, 1.5));
        }

        public bool ValidateToken(string token)
        {
            return SessionToken == token && TokenExpiry > DateTime.UtcNow && !IsBanned;
        }

        public void RenewToken()
        {
            SessionToken = GenerateToken();
            TokenExpiry = DateTime.UtcNow.AddDays(7);
        }

        public void AddExperience(int exp)
        {
            Experience += exp;
            while (Experience >= ExperienceToNextLevel)
            {
                LevelUp();
            }
        }

        private void LevelUp()
        {
            Level++;
            Experience -= ExperienceToNextLevel;
            ExperienceToNextLevel = CalculateExperienceNeeded(Level + 1);
            Gold += 100;
        }

        public void AddGold(int amount)
        {
            Gold += amount;
        }

        public bool DeductGold(int amount)
        {
            if (Gold >= amount)
            {
                Gold -= amount;
                return true;
            }
            return false;
        }

        public void RecordMatchResult(bool won, int kills, int deaths, int score)
        {
            Kills += kills;
            Deaths += deaths;
            Score += score;

            // Update stats
            Stats.TotalMatches++;
            if (won) Stats.TotalWins++;
            else Stats.TotalLosses++;

            Stats.TotalKills += kills;
            Stats.TotalDeaths += deaths;

            Stats.WinRate = Stats.TotalMatches > 0 ? (float)Stats.TotalWins / Stats.TotalMatches * 100f : 0f;
            Stats.KDRatio = Stats.TotalDeaths > 0 ? (float)Stats.TotalKills / Stats.TotalDeaths : Stats.TotalKills;

            // Calculate MMR change
            int mmrChange = CalculateMMRChange(won, kills, deaths);
            MMR += mmrChange;
            UpdateRank();

            // Add rewards
            int goldReward = CalculateGoldReward(won, kills);
            int expReward = CalculateExpReward(won, kills);

            AddGold(goldReward);
            AddExperience(expReward);
        }

        private int CalculateMMRChange(bool won, int kills, int deaths)
        {
            int baseChange = won ? 25 : -25;
            float kdRatio = deaths > 0 ? (float)kills / deaths : kills;
            float performanceBonus = Math.Min(15, kdRatio * 5);
            return baseChange + (int)performanceBonus;
        }

        private int CalculateGoldReward(bool won, int kills)
        {
            int baseReward = won ? 100 : 50;
            int killReward = kills * 10;
            return baseReward + killReward;
        }

        private int CalculateExpReward(bool won, int kills)
        {
            int baseExp = won ? 200 : 100;
            int killExp = kills * 15;
            return baseExp + killExp;
        }

        private void UpdateRank()
        {
            if (MMR < 1000) Rank = 1;
            else if (MMR < 1150) Rank = 2;
            else if (MMR < 1300) Rank = 3;
            else if (MMR < 1450) Rank = 4;
            else if (MMR < 1600) Rank = 5;
            else if (MMR < 1750) Rank = 6;
            else if (MMR < 1900) Rank = 7;
            else if (MMR < 2050) Rank = 8;
            else if (MMR < 2200) Rank = 9;
            else Rank = 10;
        }

        /// <summary>
        /// True when the player has unlocked this item.
        ///
        /// Item ids are globally unique across every item type, so ownership is a single
        /// Contains against one flat list. This previously needed an itemType string and
        /// switched on it, which meant a typo silently reported "not owned" and could not be
        /// used to validate the loadout (whose ids carry no type at all).
        /// </summary>
        public bool HasItem(int itemId)
        {
            return Inventory != null
                && Inventory.UnlockedItems != null
                && Inventory.UnlockedItems.Contains(itemId);
        }

        /// <summary>Unlocks an item. Idempotent - owning something twice is not meaningful.</summary>
        public void UnlockItem(int itemId)
        {
            if (Inventory == null) return;
            if (Inventory.UnlockedItems == null) Inventory.UnlockedItems = new List<int>();
            if (!Inventory.UnlockedItems.Contains(itemId))
                Inventory.UnlockedItems.Add(itemId);
        }

        /// <summary>Removes a previously unlocked item.</summary>
        public bool RemoveItem(int itemId)
        {
            return Inventory != null
                && Inventory.UnlockedItems != null
                && Inventory.UnlockedItems.Remove(itemId);
        }

        public void ReportCheatViolation(string violation)
        {
            CheatViolations++;
            ActiveCheatWarnings.Add($"{violation} at {DateTime.UtcNow}");

            if (CheatViolations >= 3)
            {
                BanPlayer(TimeSpan.FromDays(7));
            }
        }

        public void BanPlayer(TimeSpan duration)
        {
            IsBanned = true;
            BanExpiry = DateTime.UtcNow.Add(duration);
            IsConnected = false;
            IsInGame = false;
        }

        public void ResetGameStats()
        {
            Kills = 0;
            Deaths = 0;
            Score = 0;
            CurrentGameId = -1;
            TeamId = -1;
        }

        public Shared.PlayerData GetPublicData()
        {
            return new Shared.PlayerData
            {
                PlayerId = this.PlayerId,
                Username = this.Username,
                Level = this.Level,
                MMR = this.MMR,
                Rank = this.Rank,
                Kills = this.Kills,
                Deaths = this.Deaths,
                Score = this.Score,
                Loadout = this.Loadout,
                Stats = this.Stats
            };
        }

        public PlayerProfile GetFullProfile()
        {
            return new PlayerProfile
            {
                PlayerId = this.PlayerId,
                Username = this.Username,
                Email = this.Email,
                Level = this.Level,
                Experience = this.Experience,
                ExperienceToNextLevel = this.ExperienceToNextLevel,
                Gold = this.Gold,
                // Money was missing here, so it was read from the database but never actually
                // sent to the client. Mapped now alongside the shards.
                Money = this.Money,
                CommonShards = this.CommonShards,
                RareShards = this.RareShards,
                EpicShards = this.EpicShards,
                LegendaryShards = this.LegendaryShards,
                MMR = this.MMR,
                Rank = this.Rank,
                TotalStats = this.Stats,
                Inventory = this.Inventory,
                Loadout = this.Loadout,
                TotalPlayTime = this.TotalPlayTime,
                CreatedAt = this.CreatedAt,
                LastLogin = this.LastLogin,
                LoginStreak = this.LoginStreak,
                ClaimedDailyRewardToday = this.DailyRewardClaimedToday,
                DailyRewardGoldGranted = this.DailyRewardGoldGranted,
                DailyRewardXpGranted = this.DailyRewardXpGranted
            };
        }

        public void UpdateHeartbeat()
        {
            LastHeartbeat = DateTime.UtcNow;
        }

        public bool IsHeartbeatStale(int timeoutSeconds = 30)
        {
            return (DateTime.UtcNow - LastHeartbeat).TotalSeconds > timeoutSeconds;
        }
    }
}