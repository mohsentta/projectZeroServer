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
    public class Vector2Data
    {
        public float x;
        public float y;
    }

    /// <summary>
    /// What the player is carrying right now, and which skin each of their guns wears.
    ///
    /// This is a selection, not a copy: every id here must also appear in
    /// <see cref="PlayerInventory.UnlockedItems"/>.
    /// </summary>
    [Serializable]
    public class PlayerLoadout
    {
        // The slot -> item assignments.
        public int PrimaryWeaponId { get; set; }
        public int SecondaryWeaponId { get; set; }
        public int MeleeWeaponId { get; set; }
        public int GrenadeId { get; set; }

        /// <summary>
        /// Skin applied to each gun, keyed by WEAPON id (not by slot).
        ///
        /// Weapon-keyed is deliberate. A gun skin belongs to one specific gun, so
        /// weaponId -> skinId records the assignment once and it follows that gun: swapping a
        /// different weapon into the primary slot never forgets the original's skin, and the
        /// same weapon in two slots wears one consistent skin. A slot-keyed map would lose the
        /// assignment the moment the weapon left the slot.
        ///
        /// A weapon with no entry wears its default skin.
        /// </summary>
        public Dictionary<int, int> WeaponSkins { get; set; }

        /// <summary>Character skins the player owns, most-preferred first.</summary>
        public List<int> CharacterSkins { get; set; }

        /// <summary>The character skin currently worn. Must be one of <see cref="CharacterSkins"/>.</summary>
        public int PlayerSkinId { get; set; }

        public PlayerLoadout()
        {
            PrimaryWeaponId = 1;
            SecondaryWeaponId = 2;
            MeleeWeaponId = 3;
            GrenadeId = 4;

            WeaponSkins = new Dictionary<int, int>();
            CharacterSkins = new List<int>();
            PlayerSkinId = 2001;
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
        /// <summary>
        /// Every non-stackable item this player has unlocked: guns, skins, grenades, melees.
        ///
        /// One flat list, because item ids are globally unique across every item type - there
        /// is only ever one item 4 in the game. Owning something is therefore a single
        /// Contains, with no type to look up and no per-type list to search.
        ///
        /// This holds only non-stackables. Stackable things (Gold, Money, shards, crates) are
        /// counts, because "do you own it" and "how many do you have" are different questions.
        /// </summary>
        public List<int> UnlockedItems { get; set; }

        public List<LootBox> LootBoxes { get; set; }

        /// <summary>
        /// Unopened crates, one count per rarity.
        ///
        /// These are counts rather than list entries on purpose: the existing LootBoxes list
        /// gives every crate an id and an acquired timestamp, which is unnecessary for a
        /// stackable consumable and awkward to grant in bulk. The list is kept as-is for
        /// anything that still needs individual crates.
        /// </summary>
        public int CommonCrates { get; set; }
        public int RareCrates { get; set; }
        public int EpicCrates { get; set; }
        public int LegendaryCrates { get; set; }

        public PlayerInventory()
        {
            UnlockedItems = new List<int>();
            LootBoxes = new List<LootBox>();

            CommonCrates = 0;
            RareCrates = 0;
            EpicCrates = 0;
            LegendaryCrates = 0;
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
        public int Money { get; set; }

        // Shards, one pool per rarity. Used for crafting/upgrading rather than direct purchase,
        // which is why they are separate pools instead of a single balance plus a tier.
        public int CommonShards { get; set; }
        public int RareShards { get; set; }
        public int EpicShards { get; set; }
        public int LegendaryShards { get; set; }

        public int MMR { get; set; }
        public int Rank { get; set; }
        public PlayerStats TotalStats { get; set; }
        public PlayerInventory Inventory { get; set; }
        public PlayerLoadout Loadout { get; set; }
        public TimeSpan TotalPlayTime { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastLogin { get; set; }
        public int LoginStreak { get; set; }
        public bool ClaimedDailyRewardToday { get; set; }
        public int DailyRewardGoldGranted { get; set; }
        public int DailyRewardXpGranted { get; set; }

        /// <summary>
        /// Season progress for this account. Sent with the profile so the battle pass panel
        /// has everything it needs on login without a second round trip.
        /// </summary>
        public BattlePassState BattlePass { get; set; }

        public PlayerProfile()
        {
            TotalStats = new PlayerStats();
            Inventory = new PlayerInventory();
            Loadout = new PlayerLoadout();
            BattlePass = new BattlePassState();
        }
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

        public PlayerData()
        {
            Loadout = new PlayerLoadout();
            Stats = new PlayerStats();
        }
    }

    #endregion

    #region Battle Pass

    /// <summary>What a battle pass tier hands out.</summary>
    public enum RewardKind
    {
        Item,
        Gold,
        Shards,
        Crate,
    }

    /// <summary>
    /// One reward on the season track. The full table is built in BattlePassCatalog and is
    /// identical on the client and the server; only the player's progress travels over the wire.
    /// </summary>
    [Serializable]
    public class BattlePassReward
    {
        public int Tier { get; set; }

        /// <summary>True for the premium track, false for the free track.</summary>
        public bool Premium { get; set; }

        public RewardKind Kind { get; set; }

        /// <summary>Item id for <see cref="RewardKind.Item"/>; 0 otherwise.</summary>
        public int ItemId { get; set; }

        /// <summary>Gold/shard/crate amount. Ignored for items.</summary>
        public int Amount { get; set; }

        /// <summary>Rarity tier used by shard and crate rewards.</summary>
        public ShardTier ShardTier { get; set; }

        /// <summary>Display text, e.g. "250 Gold" or "Vector-9".</summary>
        public string Label { get; set; }

        /// <summary>Display rarity, e.g. "Rare".</summary>
        public string Rarity { get; set; }
    }

    /// <summary>
    /// A player's battle pass progress. Claimed tiers are lists rather than a high-water mark
    /// so a player can claim out of order within what they have already unlocked.
    /// </summary>
    [Serializable]
    public class BattlePassState
    {
        public int SeasonId { get; set; }

        public string SeasonName { get; set; }

        public int Xp { get; set; }

        /// <summary>Current tier, 1..<see cref="MaxTier"/>. Tier 1 is available at 0 XP.</summary>
        public int Tier { get; set; }

        public int MaxTier { get; set; }

        public bool PremiumUnlocked { get; set; }

        public List<int> ClaimedFreeTiers { get; set; }

        public List<int> ClaimedPremiumTiers { get; set; }

        /// <summary>XP earned inside the current tier.</summary>
        public int XpIntoCurrentTier { get; set; }

        /// <summary>XP needed to finish the current tier; 0 at max tier.</summary>
        public int XpForNextTier { get; set; }

        /// <summary>How many unlocked rewards have not been claimed yet.</summary>
        public int UnclaimedCount { get; set; }

        public BattlePassState()
        {
            SeasonId = 1;
            Tier = 1;
            MaxTier = 30;
            ClaimedFreeTiers = new List<int>();
            ClaimedPremiumTiers = new List<int>();
        }
    }

    /// <summary>Ask the server for the current battle pass state.</summary>
    [Serializable]
    public class BattlePassGetRequest
    {
        public string type;      // "battlepass_get"
        public string token;
    }

    /// <summary>Claim one tier's reward. <c>premium</c> selects the track.</summary>
    [Serializable]
    public class BattlePassClaimRequest
    {
        public string type;      // "battlepass_claim"
        public string token;
        public int tier;
        public bool premium;
    }

    /// <summary>Buy the premium track for this season with Gold.</summary>
    [Serializable]
    public class BattlePassBuyRequest
    {
        public string type;      // "battlepass_buy_premium"
        public string token;
    }

    [Serializable]
    public class BattlePassStateResponse
    {
        public string type = "battlepass_state";
        public BattlePassState state;
        public int premiumPrice;
        public int gold;
    }

    [Serializable]
    public class BattlePassClaimResult
    {
        public string type = "battlepass_claim_result";
        public bool success;
        public string message;
        public int tier;
        public bool premium;
        public BattlePassState state;
        public BattlePassReward reward;
        public PlayerInventory inventory;
        public int gold;
    }

    [Serializable]
    public class BattlePassPurchaseResult
    {
        public string type = "battlepass_purchase_result";
        public bool success;
        public string message;
        public BattlePassState state;
        public int gold;
    }

    /// <summary>
    /// Pushed when the player earns season XP (match end, co-op run). The panel updates its
    /// progress bar and shows a "+XP" notice without polling the server.
    /// </summary>
    [Serializable]
    public class BattlePassXpUpdate
    {
        public string type = "battlepass_xp";
        public BattlePassState state;
        public int xpGained;
        public int tiersGained;
        public string source;
    }

    #endregion

    #region Friends System Models

    [Serializable]
    public class FriendData
    {
        public int FriendId { get; set; }
        public string Username { get; set; }
        public PlayerStatus Status { get; set; }
        public int CurrentGameId { get; set; } // -1 if not in game
        public DateTime FriendsSince { get; set; }
    }

    [Serializable]
    public class FriendRequest
    {
        public int RequestId { get; set; }
        public int FromPlayerId { get; set; }
        public string FromUsername { get; set; }
        public int ToPlayerId { get; set; }
        public DateTime SentAt { get; set; }
        public FriendRequestStatus Status { get; set; }
    }

    public enum PlayerStatus
    {
        Offline,
        Online,
        InGame,
        InQueue
    }

    public enum FriendRequestStatus
    {
        Pending,
        Accepted,
        Declined
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

    #region Friend Request/Response Models

    [Serializable]
    public class GetFriendsRequest
    {
        public string type;
        public string token;
    }

    [Serializable]
    public class GetFriendsResponse
    {
        public string type;
        public bool success;
        public List<FriendData> friends;
        public List<FriendRequest> pendingRequests;
        public string message;
    }

    [Serializable]
    public class SendFriendRequest
    {
        public string type;
        public string token;
        public int targetPlayerId;
        public string targetUsername;
    }

    [Serializable]
    public class RespondToFriendRequest
    {
        public string type;
        public string token;
        public int requestId;
        public bool accept;
    }

    [Serializable]
    public class RemoveFriendRequest
    {
        public string type;
        public string token;
        public int friendId;
    }

    [Serializable]
    public class FriendStatusUpdate
    {
        public string type;
        public int playerId;
        public string username;
        public PlayerStatus status;
        public int currentGameId;
        public DateTime timestamp;
    }

    [Serializable]
    public class FriendResponse
    {
        public string type;
        public bool success;
        public string message;
        public FriendData friend;
    }

    [Serializable]
    public class FriendRequestNotification
    {
        public string type;
        public FriendRequest request;
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
        public Vector2Data rotation;
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
        public Vector3Data position;
        public Vector2Data rotation;
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

        /// <summary>
        /// The player the shooter's own raycast struck, or -1 for no player.
        ///
        /// This is how line of sight is communicated. The server holds no map geometry, so it
        /// cannot tell a wall from a clear shot; the client can, because its raycast is real
        /// physics and stops on geometry. If an enemy is behind a wall the ray hits the wall,
        /// this stays -1, and the server applies no damage - the wall case is handled by
        /// omission rather than by a lookup.
        ///
        /// Everything else stays server-authoritative: the server still checks that the id is
        /// a real participant, not the shooter, not a teammate, alive, and within range.
        /// </summary>
        public int hitPlayerId = -1;

        /// <summary>
        /// The co-op enemy the shot struck, or -1.
        ///
        /// Separate from hitPlayerId rather than reusing one field, because players and enemies
        /// are different id spaces resolved by different managers, and a co-op shot can only
        /// ever name one of them.
        /// </summary>
        public int hitEnemyId = -1;

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
        /// <summary>
        /// Packet discriminator. Without this the reply was an untyped object, so the client's
        /// dispatch switch never matched "equip_response" and every equip silently timed out.
        /// </summary>
        public string type = "equip_response";

        public bool Success { get; set; }
        public string Message { get; set; }

        /// <summary>
        /// The loadout after the change, so the client does not have to guess which slot moved
        /// or wait for a full profile refresh.
        /// </summary>
        public PlayerLoadout Loadout { get; set; }

        public PlayerInventory Inventory { get; set; }
        public int Gold { get; set; }
    }

    /// <summary>
    /// Equip an owned item. <c>slotType</c> is "primary", "secondary", "melee", "grenade",
    /// "weapon" (derive the slot from the weapon), "weapon_skin" or "character". Empty means
    /// "work it out from the item".
    /// </summary>
    [Serializable]
    public class EquipRequest
    {
        public string type;      // "equip"
        public string token;
        public int itemId;
        public string slotType;
    }

    /// <summary>Ask for the current inventory and loadout.</summary>
    [Serializable]
    public class GetInventoryRequest
    {
        public string type;      // "get_inventory"
        public string token;
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
        Grenade,

        // Added with the expanded catalogue. Appended (not inserted) so existing serialized
        // numeric values keep their meaning.
        LMG,
        DMR,
        Revolver,
        BattleRifle,
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

        /// <summary>
        /// Players versus waves of AI melee enemies. Runs its own queue and lobby list,
        /// separate from TeamDeathmatch.
        /// </summary>
        CoopSurvival,
    }
    public enum Map
    {
        Office
    }

    /// <summary>
    /// Shard tiers, cheapest first.
    ///
    /// The numeric ordering is load-bearing: which tiers an enemy can drop, and how likely the
    /// higher ones are, is derived from the enemy's level. Do not reorder.
    /// </summary>
    public enum ShardTier
    {
        Common = 0,
        Rare = 1,
        Epic = 2,
        Legendary = 3,
    }

    #endregion

    #region Co-op Enemies

    /// <summary>
    /// One live enemy, as replicated to clients.
    ///
    /// Enemies are melee-only. Level is the whole difficulty curve: it scales health, damage,
    /// speed and the shard drop table together, so there is one enemy archetype rather than a
    /// type per behaviour.
    /// </summary>
    [Serializable]
    public class CoopEnemyState
    {
        public int enemyId;
        public int level;

        public float x;
        public float y;
        public float z;

        /// <summary>Facing in degrees, for the client to orient the model.</summary>
        public float yaw;

        public int health;
        public int maxHealth;

        /// <summary>Enemy id currently being attacked by this one, or -1.</summary>
        public int targetPlayerId = -1;

        /// <summary>Briefly set when the enemy swings, so the client can play the attack animation.</summary>
        public bool isAttacking;
    }

    /// <summary>Sent when an enemy enters the world.</summary>
    [Serializable]
    public class CoopEnemySpawn
    {
        public string type = "coop_enemy_spawn";
        public List<CoopEnemyState> enemies;
        public int waveNumber;
        public int waveTotal;
    }

    /// <summary>
    /// Periodic reconciliation of live enemies. Full snapshots rather than deltas: enemy counts
    /// are small, and a snapshot cannot drift.
    /// </summary>
    [Serializable]
    public class CoopEnemyStateUpdate
    {
        public string type = "coop_enemy_state";
        public List<CoopEnemyState> enemies;
        public long serverTick;
    }

    /// <summary>
    /// An enemy died. Carries the killer so the client can show its own hit confirmation, and
    /// the shard grant so the HUD can report what it earned.
    /// </summary>
    [Serializable]
    public class CoopEnemyDeath
    {
        public string type = "coop_enemy_death";
        public int enemyId;
        public int enemyLevel;

        public int killerPlayerId = -1;
        public string killerName;

        /// <summary>False when the enemy despawned without being killed, e.g. at wave end.</summary>
        public bool killed;

        // The shard drop. tier is only meaningful when shardsGranted > 0.
        public bool droppedShards;
        public int shardsGranted;
        public ShardTier tier;
        public int killerShardBalance;

        // Where it died, for a death effect.
        public float x;
        public float y;
        public float z;
    }

    /// <summary>Wave lifecycle, so the HUD can show progress and countdowns.</summary>
    [Serializable]
    public class CoopWaveState
    {
        public string type = "coop_wave_state";
        public int waveNumber;
        public int waveTotal;

        /// <summary>Enemies still alive this wave.</summary>
        public int enemiesRemaining;

        /// <summary>Seconds until the next wave starts. Zero while a wave is in progress.</summary>
        public float nextWaveIn;

        /// <summary>True between waves.</summary>
        public bool isIntermission;

        /// <summary>How many players are still standing.</summary>
        public int playersAlive;
    }

    /// <summary>The player's shard balance, pushed whenever it changes.</summary>
    [Serializable]
    public class ShardBalanceUpdate
    {
        public string type = "shard_balance";
        public int commonShards;
        public int rareShards;
        public int epicShards;
        public int legendaryShards;

        /// <summary>What was just earned, for a floating "+2 rare shards" style message.</summary>
        public bool granted;
        public int grantedAmount;
        public ShardTier grantedTier;
    }

    /// <summary>A co-op run ended, with the totals the player earned.</summary>
    [Serializable]
    public class CoopRunSummary
    {
        public string type = "coop_run_summary";
        public int wavesSurvived;
        public int enemiesKilled;
        public int commonShards;
        public int rareShards;
        public int epicShards;
        public int legendaryShards;
    }

    #endregion

    #region Lobby

    [Serializable]
    public class CreateLobbyRequest
    {
        public string type;
        public string token;

        /// <summary>
        /// Which mode this party is for. Co-op runs live in their own lobby list, so a party
        /// created here is only joinable by someone looking for the same mode.
        /// </summary>
        public GameType gameType = GameType.TeamDeathmatch;
    }

    [Serializable]
    public class JoinLobbyRequest
    {
        public string type;
        public string token;
        public string lobbyId;
    }

    [Serializable]
    public class LeaveLobbyRequest
    {
        public string type;
        public string token;
        public string lobbyId;
    }

    [Serializable]
    public class LobbyReadyRequest
    {
        public string type;
        public string token;
        public string lobbyId;
        public bool ready;
    }

    [Serializable]
    public class LobbyStartRequest
    {
        public string type;
        public string token;
        public string lobbyId;
    }

    [Serializable]
    public class LobbySyncRequest
    {
        public string type;          // "lobby_sync"
        public string token;
    }

    /// <summary>Asks the server to invite a specific friend into the caller's lobby.</summary>
    [Serializable]
    public class LobbyInviteRequest
    {
        public string type;          // "lobby_invite"
        public string token;
        public int targetPlayerId;
    }

    /// <summary>Accept or decline an invite this player received.</summary>
    [Serializable]
    public class LobbyInviteRespondRequest
    {
        public string type;          // "lobby_invite_respond"
        public string token;
        public int fromPlayerId;     // who sent the invite
        public bool accept;
    }

    /// <summary>
    /// Delivered to the invited player. Carries enough context to show who is asking and
    /// which lobby they would be joining, so accepting needs no extra round trip.
    /// </summary>
    [Serializable]
    public class LobbyInviteNotification
    {
        public string type;          // "lobby_invite_notification"
        public string lobbyId;
        public int fromPlayerId;
        public string fromUsername;
        public int memberCount;
        public int maxPlayers;
        public DateTime timestamp;
    }

    /// <summary>
    /// Result of an invite attempt, sent back to the inviter so the friends panel can
    /// report "invite sent", "they are offline", "lobby is full" and so on.
    /// </summary>
    [Serializable]
    public class LobbyInviteResult
    {
        public string type;          // "lobby_invite_result"
        public bool success;
        public int targetPlayerId;
        public string targetUsername;
        public string message;
        public DateTime timestamp;
    }

    /// <summary>Tells the inviter that their invite was accepted, declined or expired.</summary>
    [Serializable]
    public class LobbyInviteUpdate
    {
        public string type;          // "lobby_invite_update"
        public int otherPlayerId;
        public string otherUsername;
        public string state;         // "accepted" | "declined" | "expired"
        public string message;
        public DateTime timestamp;
    }

    [Serializable]
    public class LobbyMemberInfo
    {
        public int playerId;
        public string username;
        public bool ready;
    }

    [Serializable]
    public class LobbyUpdateData
    {
        public string type;          // "lobby_update"
        public string lobbyId;
        public int hostPlayerId;
        public List<LobbyMemberInfo> members;
        public string status;        // "waiting" | "starting" | "closed"
        public string message;

        /// <summary>Mode this party will play, so the client can label it.</summary>
        public GameType gameType = GameType.TeamDeathmatch;

        /// <summary>How many players this mode accepts. Co-op takes more than a duel.</summary>
        public int maxPlayers = 2;

        public DateTime timestamp;
    }

    [Serializable]
    public class LobbyErrorData
    {
        public string type;          // "lobby_error"
        public string message;
        public DateTime timestamp;
    }

    #endregion
}