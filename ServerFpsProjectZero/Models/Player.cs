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
        public TimeSpan TotalPlayTime { get; set; }

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

            // Initialize with default items
            Inventory = new PlayerInventory();
            Inventory.OwnedWeapons = new List<int> { 1, 2, 3, 4 };
            Inventory.OwnedSkins = new List<int> { 1 };
            Inventory.OwnedGrenades = new List<int> { 4 };
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

        public bool HasItem(int itemId, string itemType)
        {
            return itemType switch
            {
                "weapon" => Inventory.OwnedWeapons.Contains(itemId),
                "skin" => Inventory.OwnedSkins.Contains(itemId),
                "grenade" => Inventory.OwnedGrenades.Contains(itemId),
                _ => false
            };
        }

        public void AddItem(int itemId, string itemType)
        {
            switch (itemType)
            {
                case "weapon":
                    if (!Inventory.OwnedWeapons.Contains(itemId))
                        Inventory.OwnedWeapons.Add(itemId);
                    break;
                case "skin":
                    if (!Inventory.OwnedSkins.Contains(itemId))
                        Inventory.OwnedSkins.Add(itemId);
                    break;
                case "grenade":
                    if (!Inventory.OwnedGrenades.Contains(itemId))
                        Inventory.OwnedGrenades.Add(itemId);
                    break;
            }
        }

        public bool RemoveItem(int itemId, string itemType)
        {
            return itemType switch
            {
                "weapon" => Inventory.OwnedWeapons.Remove(itemId),
                "skin" => Inventory.OwnedSkins.Remove(itemId),
                "grenade" => Inventory.OwnedGrenades.Remove(itemId),
                _ => false
            };
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
                MMR = this.MMR,
                Rank = this.Rank,
                TotalStats = this.Stats,
                Inventory = this.Inventory,
                Loadout = this.Loadout,
                TotalPlayTime = this.TotalPlayTime,
                CreatedAt = this.CreatedAt,
                LastLogin = this.LastLogin
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