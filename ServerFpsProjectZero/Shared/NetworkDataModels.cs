// ServerFpsProjectZero.Shared - host-game contracts (friends, lobby, gameplay).
// Domain, reward, battle pass and queue contracts live in ProjectZero.Core.
using System;
using System.Collections.Generic;
using ProjectZero.Core;

namespace ServerFpsProjectZero.Shared
{
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
    public enum FriendRequestStatus
    {
        Pending,
        Accepted,
        Declined
    }
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
}
