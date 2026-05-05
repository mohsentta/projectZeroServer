// Shared/NetworkDataModels.cs
using System;
using System.Collections.Generic;

namespace ServerFpsProjectZero.Shared
{
    #region Core Data Types

    [Serializable]
    public class Vector3Data
    {
        public float x;
        public float y;
        public float z;
    }

    [Serializable]
    public class PlayerLoadout
    {
        public int PrimaryWeaponId { get; set; }
        public int SecondaryWeaponId { get; set; }
        public int MeleeWeaponId { get; set; }
        public int GrenadeId { get; set; }
        public int PlayerSkinId { get; set; }
        public int WeaponSkinId { get; set; }

        public PlayerLoadout()
        {
            PrimaryWeaponId = 1;
            SecondaryWeaponId = 2;
            MeleeWeaponId = 3;
            GrenadeId = 4;
            PlayerSkinId = 1;
            WeaponSkinId = 1;
        }
    }

    [Serializable]
    public class PlayerStats
    {
        public int TotalMatches { get; set; }
        public int TotalWins { get; set; }
        public int TotalLosses { get; set; }
        public int TotalKills { get; set; }
        public int TotalDeaths { get; set; }
        public int TotalHeadshots { get; set; }
        public int TotalAssists { get; set; }
        public float WinRate { get; set; }
        public float KDRatio { get; set; }
    }

    [Serializable]
    public class PlayerInventory
    {
        public List<int> OwnedWeapons { get; set; }
        public List<int> OwnedSkins { get; set; }
        public List<int> OwnedGrenades { get; set; }
        public List<LootBox> LootBoxes { get; set; }

        public PlayerInventory()
        {
            OwnedWeapons = new List<int>();
            OwnedSkins = new List<int>();
            OwnedGrenades = new List<int>();
            LootBoxes = new List<LootBox>();
        }
    }

    [Serializable]
    public class LootBox
    {
        public int LootBoxId { get; set; }
        public string Type { get; set; }
        public DateTime AcquiredAt { get; set; }
    }

    [Serializable]
    public class PlayerProfile
    {
        public int PlayerId { get; set; }
        public string Username { get; set; }
        public string Email { get; set; }
        public int Level { get; set; }
        public int Experience { get; set; }
        public int ExperienceToNextLevel { get; set; }
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

    [Serializable]
    public class PlayerData
    {
        public int PlayerId { get; set; }
        public string Username { get; set; }
        public int Level { get; set; }
        public int MMR { get; set; }
        public int Rank { get; set; }
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public int Score { get; set; }
        public PlayerLoadout Loadout { get; set; }
        public PlayerStats Stats { get; set; }
    }

    #endregion

    #region Packet Wrappers

    [Serializable]
    public class ProfileDataWrapper
    {
        public string type;
        public PlayerProfile profile;
        public DateTime timestamp;
    }

    #endregion

    #region Request/Response Models

    [Serializable]
    public class LoginRequest
    {
        public string type;
        public string username;
        public string password;
        public DateTime timestamp;
    }

    [Serializable]
    public class LoginResponse
    {
        public string type;
        public bool success;
        public int playerId;
        public string token;
        public string message;
        public DateTime timestamp;
        public string username;
    }

    [Serializable]
    public class RegisterRequest
    {
        public string type;
        public string username;
        public string password;
        public string email;
        public DateTime timestamp;
    }

    [Serializable]
    public class RegisterResponse
    {
        public string type;
        public bool success;
        public string message;
        public DateTime timestamp;
    }

    [Serializable]
    public class GetProfileRequest
    {
        public string type;
        public string token;
        public int playerId;
    }

    [Serializable]
    public class JoinQueueRequest
    {
        public string type;
        public string token;
    }

    [Serializable]
    public class LeaveQueueRequest
    {
        public string type;
        public string token;
    }

    [Serializable]
    public class GetQueueStatusRequest
    {
        public string type;
        public string token;
    }

    [Serializable]
    public class QueueStatusData
    {
        public string type;
        public int queueSize;
        public int position;
        public int estimatedWaitTime;
        public int playersNeeded;
        public bool inQueue;
        public DateTime timestamp;
    }

    [Serializable]
    public class QueueResponseData
    {
        public string type;
        public bool success;
        public string message;
        public int queueSize;
        public int position;
        public int estimatedWaitTime;
        public int playersNeeded;
    }

    [Serializable]
    public class QueueTimeoutData
    {
        public string type;
        public string message;
        public DateTime timestamp;
    }

    [Serializable]
    public class LogoutRequest
    {
        public string type;
        public string token;
    }

    [Serializable]
    public class HeartbeatPacket
    {
        public string type;
        public string token;
        public DateTime timestamp;
    }

    [Serializable]
    public class ErrorData
    {
        public string type;
        public string message;
        public int errorCode;
        public DateTime timestamp;
    }

    #endregion

    #region Game Models

    [Serializable]
    public class GameStartData
    {
        public string type;
        public Map mapName;
        public GameType gameType;
        public int gameId;
        public int teamId;
        public List<GamePlayerData> players;
        public DateTime timestamp;
    }

    [Serializable]
    public class GamePlayerData
    {
        public int playerId;
        public string username;
        public int level;
        public int mmr;
        public int rank;
        public int teamId;
        public PlayerLoadout loadout;
    }

    [Serializable]
    public class GameStateData
    {
        public string type;
        public int gameId;
        public int timeRemaining;
        public int redScore;
        public int blueScore;
        public PlayerStatsData playerStats;
        public List<OtherPlayerData> otherPlayers;
        public DateTime timestamp;
    }

    [Serializable]
    public class PlayerStatsData
    {
        public int kills;
        public int deaths;
        public int score;
    }

    [Serializable]
    public class OtherPlayerData
    {
        public int playerId;
        public string username;
        public int teamId;
        public Vector3Data position;
        public float health;
        public bool isAlive;
    }

    [Serializable]
    public class MatchResult
    {
        public bool IsWin { get; set; }
        public int GoldReward { get; set; }
        public int ExperienceReward { get; set; }
        public int Kills { get; set; }
        public int Deaths { get; set; }
    }

    [Serializable]
    public class MovementInput
    {
        public string type;
        public string token;
        public int gameId;
        public float positionX;
        public float positionY;
        public float positionZ;
        public float rotation;
        public DateTime timestamp;
    }

    [Serializable]
    public class ShootInput
    {
        public string type;
        public string token;
        public int gameId;
        public float originX;
        public float originY;
        public float originZ;
        public float targetX;
        public float targetY;
        public float targetZ;
        public int weaponId;
        public DateTime timestamp;
    }

    [Serializable]
    public class AbilityInput
    {
        public string type;
        public string token;
        public int gameId;
        public string abilityName;
        public float targetX;
        public float targetY;
        public float targetZ;
        public DateTime timestamp;
    }

    #endregion

    #region Weapon Pickup Models

    [Serializable]
    public class WeaponData
    {
        public int weaponId;
        public string weaponName;
        public string weaponType;
        public int damage;
        public float fireRate;
        public float range;
        public int magazineSize;
        public int maxAmmo;
        public float reloadTime;
        public string rarity;
        public int price;
    }

    [Serializable]
    public class WeaponPickupData
    {
        public string type;
        public string token;
        public int gameId;
        public int weaponId;
        public int ammoAmount;
        public Vector3Data position;
        public DateTime timestamp;
    }

    [Serializable]
    public class WeaponPickupConfirm
    {
        public string type;
        public int weaponId;
        public int ammoAmount;
        public bool success;
        public string message;
        public DateTime timestamp;
    }

    [Serializable]
    public class WeaponDropData
    {
        public string type;
        public string token;
        public int gameId;
        public int weaponId;
        public int ammoAmount;
        public Vector3Data position;
        public DateTime timestamp;
    }

    [Serializable]
    public class WeaponSpawnData
    {
        public string type;
        public int weaponId;
        public string weaponName;
        public Vector3Data position;
        public bool isActive;
        public float respawnTime;
        public DateTime timestamp;
    }

    [Serializable]
    public class AmmoPickupData
    {
        public string type;
        public string token;
        public int gameId;
        public int weaponId;
        public int ammoAmount;
        public Vector3Data position;
        public DateTime timestamp;
    }

    #endregion

    #region New Gameplay Network Models

    [Serializable]
    public class PlayerSpawnData
    {
        public string type;
        public int playerId;
        public string username;
        public int teamId;
        public Vector3Data position;
        public float health;
        public PlayerLoadout loadout;
        public DateTime timestamp;
    }

    [Serializable]
    public class PlayerDespawnData
    {
        public string type;
        public int playerId;
        public string reason;
        public DateTime timestamp;
    }

    [Serializable]
    public class HitData
    {
        public string type;
        public int attackerId;
        public string attackerName;
        public int targetId;
        public string targetName;
        public int damage;
        public int weaponId;
        public Vector3Data hitPoint;
        public Vector3Data hitNormal;
        public bool isHeadshot;
        public bool isKill;
        public DateTime timestamp;
    }

    [Serializable]
    public class KillData
    {
        public string type;
        public int killerId;
        public string killerName;
        public int victimId;
        public string victimName;
        public int weaponId;
        public bool isHeadshot;
        public int killerTeamId;
        public int victimTeamId;
        public int killerScore;
        public int victimScore;
        public DateTime timestamp;
    }

    [Serializable]
    public class PlayerStateData
    {
        public string type;
        public string token;
        public int gameId;
        public int playerId;
        public float positionX;
        public float positionY;
        public float positionZ;
        public float rotation;
        public int health;
        public int currentAmmo;
        public bool isReloading;
        public bool isCrouching;
        public bool isSprinting;
        public bool isAiming;
        public DateTime timestamp;
    }

    [Serializable]
    public class GameEndData
    {
        public string type;
        public bool isWin;
        public MatchResult result;
        public List<GameEndPlayerStats> playerStats;
        public DateTime timestamp;
    }

    [Serializable]
    public class GameEndPlayerStats
    {
        public int playerId;
        public string username;
        public int teamId;
        public int kills;
        public int deaths;
        public int assists;
        public int score;
        public int headshots;
        public bool isMVP;
    }

    [Serializable]
    public class ScoreboardData
    {
        public string type;
        public int gameId;
        public List<ScoreboardPlayerData> players;
        public DateTime timestamp;
    }

    [Serializable]
    public class ScoreboardPlayerData
    {
        public int playerId;
        public string username;
        public int teamId;
        public int kills;
        public int deaths;
        public int assists;
        public int score;
        public int ping;
        public bool isAlive;
    }

    [Serializable]
    public class ChatMessage
    {
        public string type;
        public string token;
        public int gameId;
        public int playerId;
        public string username;
        public string message;
        public int teamId;
        public DateTime timestamp;
    }

    [Serializable]
    public class PingData
    {
        public string type;
        public string token;
        public int gameId;
        public int playerId;
        public string pingType;
        public Vector3Data position;
        public DateTime timestamp;
    }

    [Serializable]
    public class PlayerDeathData
    {
        public string type;
        public string token;
        public int gameId;
        public int playerId;
        public int killerId;
        public DateTime timestamp;
    }

    [Serializable]
    public class PlayerRespawnData
    {
        public string type;
        public string token;
        public int gameId;
        public int playerId;
        public Vector3Data spawnPosition;
        public DateTime timestamp;
    }

    #endregion

    #region Shop/Item Models

    [Serializable]
    public class PurchaseResponse
    {
        public bool Success { get; set; }
        public int NewGoldBalance { get; set; }
        public string Message { get; set; }
    }

    [Serializable]
    public class EquipResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    [Serializable]
    public class LootBoxReward
    {
        public string ItemName { get; set; }
        public int ItemId { get; set; }
        public string Rarity { get; set; }
        public int Quantity { get; set; }
    }

    #endregion

    #region Enums

    public enum GameState
    {
        WaitingForPlayers,
        Starting,
        InProgress,
        Ending,
        Ended
    }

    public enum PlayerState
    {
        Alive,
        Dead,
        Spectating,
        Disconnected
    }

    public enum DamageType
    {
        Bullet,
        Explosion,
        Melee,
        FallDamage,
        Poison,
        Ability
    }

    public enum HitboxType
    {
        Head,
        Chest,
        Stomach,
        LeftArm,
        RightArm,
        LeftLeg,
        RightLeg
    }

    public enum WeaponType
    {
        AssaultRifle,
        Pistol,
        SMG,
        Sniper,
        Shotgun,
        Melee,
        Grenade
    }

    public enum WeaponRarity
    {
        Common,
        Uncommon,
        Rare,
        Epic,
        Legendary
    }
    public enum GameType
    {
        TeamDeathmatch,
    }
    public enum Map
    {
        Office
    }

    #endregion
}