using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using ServerFpsProjectZero.Matchmaking;
using ServerFpsProjectZero.Models;
using ServerFpsProjectZero.Server;
using ServerFpsProjectZero.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ServerFpsProjectZero.Networking
{
    public class LoginManager
    {
        private ServerManager serverManager;
        private GameManager gameManager;
        private FriendsManager friendsManager;

        // Store connected players
        private ConcurrentDictionary<int, Player> connectedPlayers;
        private ConcurrentDictionary<string, Player> tokenToPlayer;

        // SQLite connection string
        private readonly string connectionString;

        // Matchmaking
        private MatchmakingQueue matchmakingQueue;

        // Events
        public event Action<Player> OnPlayerLoggedIn;
        public event Action<Player> OnPlayerDisconnected;
        public event Action<string, IPEndPoint> OnFailedLogin;

        public LoginManager(ServerManager serverManager, GameManager _gameManager, string _connectionString = "players.db")
        {
            this.serverManager = serverManager;
            gameManager = _gameManager;

            connectedPlayers = new ConcurrentDictionary<int, Player>();
            tokenToPlayer = new ConcurrentDictionary<string, Player>();

            connectionString = _connectionString;

            // Initialize matchmaking
            matchmakingQueue = new MatchmakingQueue();

            // Subscribe to matchmaking events
            matchmakingQueue.OnGameMatched += HandleGameMatched;
            matchmakingQueue.OnPlayerEnteredQueue += HandlePlayerEnteredQueue;
            matchmakingQueue.OnPlayerLeftQueue += HandlePlayerLeftQueue;
            matchmakingQueue.OnPlayerTimedOut += HandlePlayerTimedOut;
            matchmakingQueue.OnQueueStatusChanged += HandleQueueStatusChanged;

            // Subscribe to server events
            serverManager.OnLoginPacket += HandleLogin;
            serverManager.OnRegisterPacket += HandleRegister;
            serverManager.OnHeartbeatPacket += HandleHeartbeat;
            serverManager.OnLogoutPacket += HandleLogout;
            serverManager.OnGetProfilePacket += HandleGetProfile;
            serverManager.OnJoinQueuePacket += HandleJoinQueue;
            serverManager.OnLeaveQueuePacket += HandleLeaveQueue;
            serverManager.OnGetQueueStatusPacket += HandleGetQueueStatus;
            serverManager.OnClientDisconnected += HandleClientDisconnected;
            serverManager.OnPlayerGameStatsUpdate += UpdatePlayerGameStats;

            // Initialize database and create test accounts
            InitializeDatabase();
            InitializeTestPlayers();
        }

        public void SetFriendsManager(FriendsManager manager)
        {
            this.friendsManager = manager;
        }

        public void Start()
        {
            var playerCount = GetPlayerCount();
            Console.WriteLine($"[LoginManager] Player database ready with {playerCount} players");
            Console.WriteLine($"[LoginManager] Matchmaking queue active (requires {MatchmakingQueue.PLAYERS_PER_GAME} players per game)");
        }

        public void Stop()
        {
            // Stop matchmaking
            matchmakingQueue?.Stop();

            // Disconnect all players
            foreach (var player in connectedPlayers.Values)
            {
                OnPlayerDisconnected?.Invoke(player);
            }

            connectedPlayers.Clear();
            tokenToPlayer.Clear();

            Console.WriteLine("[LoginManager] Stopped");
        }

        /// <summary>
        /// Wipes all persistent server data (drops every table in the database)
        /// and reinitializes the schema + test accounts. Bound to the 'restart'
        /// console command. Called on-demand, never automatically at startup.
        /// </summary>
        public void ResetDatabase()
        {
            Console.WriteLine("\n[LoginManager] Wiping server data...");

            // Disconnect any currently connected players and clear in-memory state.
            foreach (var player in connectedPlayers.Values)
            {
                if (player.IsConnected)
                    friendsManager?.UpdatePlayerStatus(player.PlayerId, PlayerStatus.Offline);
            }
            connectedPlayers.Clear();
            tokenToPlayer.Clear();

            // Drop every user table (ignore SQLite internal sqlite_* tables).
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                var names = new List<string>();
                var listCmd = connection.CreateCommand();
                listCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
                using (var reader = listCmd.ExecuteReader())
                {
                    while (reader.Read())
                        names.Add(reader.GetString(0));
                }

                var dropCmd = connection.CreateCommand();
                foreach (var table in names)
                {
                    dropCmd.CommandText = $"DROP TABLE IF EXISTS \"{table}\"";
                    dropCmd.ExecuteNonQuery();
                    Console.WriteLine($"[LoginManager] Dropped table '{table}'");
                }
            }

            // Recreate the schema and seed test accounts.
            InitializeDatabase();
            InitializeTestPlayers();

            Console.WriteLine("[LoginManager] Server data wiped and reinitialized.");
        }

        #region Database Initialization

        private void InitializeDatabase()
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();

                var command = connection.CreateCommand();

                // NOTE: Tables are deliberately NOT dropped on startup anymore, so
                // player data persists across normal restarts. Wiping happens on
                // demand via the 'restart' console command -> LoginManager.ResetDatabase().

                // Create players table
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Players (
                        PlayerId INTEGER PRIMARY KEY,
                        Username TEXT UNIQUE NOT NULL,
                        Email TEXT UNIQUE NOT NULL,
                        PasswordHash TEXT NOT NULL,
                        Salt TEXT NOT NULL,
                        Level INTEGER DEFAULT 1,
                        Experience INTEGER DEFAULT 0,
                        ExperienceToNextLevel INTEGER DEFAULT 150,
                        Gold INTEGER DEFAULT 500,
                        Money REAL DEFAULT 0.0,
                        CommonShards INTEGER DEFAULT 0,
                        RareShards INTEGER DEFAULT 0,
                        EpicShards INTEGER DEFAULT 0,
                        LegendaryShards INTEGER DEFAULT 0,
                        MMR INTEGER DEFAULT 1000,
                        Rank INTEGER DEFAULT 1,
                        TotalMatches INTEGER DEFAULT 0,
                        TotalWins INTEGER DEFAULT 0,
                        TotalLosses INTEGER DEFAULT 0,
                        TotalKills INTEGER DEFAULT 0,
                        TotalDeaths INTEGER DEFAULT 0,
                        TotalHeadshots INTEGER DEFAULT 0,
                        TotalAssists INTEGER DEFAULT 0,
                        WinRate REAL DEFAULT 0.0,
                        KDRatio REAL DEFAULT 0.0,
                        TotalPlayTimeTicks BIGINT DEFAULT 0,
                        CreatedAt TEXT NOT NULL,
                        LastLogin TEXT NOT NULL,
                        LoginStreak INTEGER DEFAULT 0
                    )";
                command.ExecuteNonQuery();

                // Add columns introduced after a database may already exist. CREATE TABLE IF
                // NOT EXISTS silently does nothing for an existing table, so without this an
                // older FPSGame.db would never gain the new columns and every SELECT against
                // them would throw "no such column".
                AddColumnIfMissing(connection, "Players", "CommonShards", "INTEGER DEFAULT 0");
                AddColumnIfMissing(connection, "Players", "RareShards", "INTEGER DEFAULT 0");
                AddColumnIfMissing(connection, "Players", "EpicShards", "INTEGER DEFAULT 0");
                AddColumnIfMissing(connection, "Players", "LegendaryShards", "INTEGER DEFAULT 0");

                // Create inventory table
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS PlayerInventory (
                        PlayerId INTEGER PRIMARY KEY,
                        UnlockedItems TEXT DEFAULT '[]',
                        LootBoxes TEXT DEFAULT '[]',
                        CommonCrates INTEGER DEFAULT 0,
                        RareCrates INTEGER DEFAULT 0,
                        EpicCrates INTEGER DEFAULT 0,
                        LegendaryCrates INTEGER DEFAULT 0,
                        FOREIGN KEY (PlayerId) REFERENCES Players(PlayerId)
                    )";
                command.ExecuteNonQuery();

                // Unlocked items replaced the per-type columns (OwnedWeapons, OwnedSkins,
                // OwnedGrenades) with one flat list, because item ids are globally unique.
                bool unlockedColumnAdded = AddColumnIfMissing(connection, "PlayerInventory", "UnlockedItems", "TEXT DEFAULT '[]'");

                // The old columns still hold each player's unlocks, so fold them into the new
                // list rather than starting everyone from empty. The old columns are left in
                // place as a safety net; nothing reads them once this has run.
                if (unlockedColumnAdded)
                {
                    FoldLegacyOwnedItemsIntoUnlocked(connection);
                }

                // Unopened crate counts, added after PlayerInventory already existed.
                AddColumnIfMissing(connection, "PlayerInventory", "CommonCrates", "INTEGER DEFAULT 0");
                AddColumnIfMissing(connection, "PlayerInventory", "RareCrates", "INTEGER DEFAULT 0");
                AddColumnIfMissing(connection, "PlayerInventory", "EpicCrates", "INTEGER DEFAULT 0");
                AddColumnIfMissing(connection, "PlayerInventory", "LegendaryCrates", "INTEGER DEFAULT 0");

                // Create loadout table. The slot columns carry a SkinId for weapon slots, so a
                // slot remembers the skin applied to whatever weapon currently sits in it.
                // The legacy column names are kept so an existing database keeps working.
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS PlayerLoadout (
                        PlayerId INTEGER PRIMARY KEY,
                        PrimaryWeaponId INTEGER DEFAULT 1,
                        SecondaryWeaponId INTEGER DEFAULT 2,
                        MeleeWeaponId INTEGER DEFAULT 3,
                        GrenadeId INTEGER DEFAULT 4,
                        PlayerSkinId INTEGER DEFAULT 1,
                        WeaponSkinId INTEGER DEFAULT 1,
                        PrimarySkinId INTEGER DEFAULT 0,
                        SecondarySkinId INTEGER DEFAULT 0,
                        MeleeSkinId INTEGER DEFAULT 0,
                        GrenadeSkinId INTEGER DEFAULT 0,
                        UpdatedAt TEXT,
                        FOREIGN KEY (PlayerId) REFERENCES Players(PlayerId)
                    )";
                command.ExecuteNonQuery();

                // Per-gun skin assignments, keyed by weapon id rather than slot.
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS PlayerWeaponSkins (
                        PlayerId INTEGER NOT NULL,
                        WeaponId INTEGER NOT NULL,
                        SkinId INTEGER NOT NULL,
                        UpdatedAt TEXT,
                        PRIMARY KEY (PlayerId, WeaponId),
                        FOREIGN KEY (PlayerId) REFERENCES Players(PlayerId)
                    )";
                command.ExecuteNonQuery();

                // Character skins the player owns, in preference order.
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS PlayerCharacterSkins (
                        PlayerId INTEGER NOT NULL,
                        SkinId INTEGER NOT NULL,
                        SortOrder INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (PlayerId, SkinId),
                        FOREIGN KEY (PlayerId) REFERENCES Players(PlayerId)
                    )";
                command.ExecuteNonQuery();

                // Columns added after PlayerLoadout may already exist in a live database.
                AddColumnIfMissing(connection, "PlayerLoadout", "PrimarySkinId", "INTEGER DEFAULT 0");
                AddColumnIfMissing(connection, "PlayerLoadout", "SecondarySkinId", "INTEGER DEFAULT 0");
                AddColumnIfMissing(connection, "PlayerLoadout", "MeleeSkinId", "INTEGER DEFAULT 0");
                AddColumnIfMissing(connection, "PlayerLoadout", "GrenadeSkinId", "INTEGER DEFAULT 0");
                AddColumnIfMissing(connection, "PlayerLoadout", "UpdatedAt", "TEXT");

                // The old single WeaponSkinId applied to whichever gun was primary. Fold it into
                // the per-gun table so a player who had a skin set does not silently lose it.
                FoldLegacyWeaponSkinIntoAssignments(connection);

                // Character skins had no per-player list before, so a migrated player would
                // otherwise have an empty list while still wearing PlayerSkinId - i.e. wearing a
                // skin they do not appear to own. Give everyone the skin they are wearing.
                BackfillCharacterSkinsFromWorn(connection);

                // Create indices
                command.CommandText = "CREATE INDEX IF NOT EXISTS idx_players_username ON Players(Username)";
                command.ExecuteNonQuery();
                command.CommandText = "CREATE INDEX IF NOT EXISTS idx_players_email ON Players(Email)";
                command.ExecuteNonQuery();
                command.CommandText = "CREATE INDEX IF NOT EXISTS idx_players_mmr ON Players(MMR)";
                command.ExecuteNonQuery();

                // Lightweight migration for databases created by an older schema:
                // add any newer columns without dropping or wiping existing data.
                EnsureColumn(connection, "Players", "LoginStreak", "INTEGER DEFAULT 0");

                Console.WriteLine("[Database] SQLite database initialized successfully");
            }
        }

        /// <summary>
        /// Adds a column to an existing table when it is missing (lightweight
        /// migration for databases created by an older schema). Preserves data.
        /// </summary>
        private void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
        {
            var check = connection.CreateCommand();
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
            bool exists = Convert.ToInt64(check.ExecuteScalar()) > 0;
            if (exists) return;

            var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
            alter.ExecuteNonQuery();
            Console.WriteLine($"[Database] Migrated table '{table}': added column '{column}' {definition}");
        }

        private int GetPlayerCount()
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM Players";
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        #endregion

        #region Test Player Initialization

        private void InitializeTestPlayers()
        {
            var playerCount = GetPlayerCount();

            // Only create test players if database is empty
            if (playerCount > 0) return;

            CreateTestPlayer("player1", "player1@test.com", "password123", 1200, 500, 5);
            CreateTestPlayer("player2", "player2@test.com", "password123", 1350, 750, 8);
            CreateTestPlayer("player3", "player3@test.com", "password123", 1100, 300, 3);
            CreateTestPlayer("proplayer", "pro@test.com", "pro123", 1850, 2500, 15);
            CreateTestPlayer("newbie", "newbie@test.com", "test123", 1000, 100, 1);
            CreateTestPlayer("veteran", "vet@test.com", "vet123", 1500, 1200, 10);
            CreateTestPlayer("casual", "casual@test.com", "casual123", 1150, 400, 4);
            CreateTestPlayer("tryhard", "tryhard@test.com", "try123", 1700, 1800, 12);
            CreateTestPlayer("sniper", "sniper@test.com", "sniper123", 1400, 900, 7);
            CreateTestPlayer("shotgun", "shotgun@test.com", "shotgun123", 1250, 600, 6);

            Console.WriteLine("[Database] Player database initialized with 10 test players");
        }

        private void CreateTestPlayer(string username, string email, string password, int mmr, int gold, int level)
        {
            string salt = GenerateSalt();
            string passwordHash = HashPassword(password, salt);

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var command = connection.CreateCommand();
                    command.Transaction = transaction;

                    // Insert player
                    command.CommandText = @"
                        INSERT INTO Players (
                            Username, Email, PasswordHash, Salt, Level, Experience, 
                            ExperienceToNextLevel, Gold, MMR, Rank,
                            CommonShards, RareShards, EpicShards, LegendaryShards,
                            TotalMatches, TotalWins, TotalLosses, TotalKills, TotalDeaths,
                            TotalHeadshots, TotalAssists, WinRate, KDRatio,
                            TotalPlayTimeTicks, CreatedAt, LastLogin
                        ) VALUES (
                            @Username, @Email, @PasswordHash, @Salt, @Level, @Experience,
                            @ExperienceToNextLevel, @Gold, @MMR, @Rank,
                            @CommonShards, @RareShards, @EpicShards, @LegendaryShards,
                            @TotalMatches, @TotalWins, @TotalLosses, @TotalKills, @TotalDeaths,
                            @TotalHeadshots, @TotalAssists, @WinRate, @KDRatio,
                            @TotalPlayTimeTicks, @CreatedAt, @LastLogin
                        )";

                    command.Parameters.AddWithValue("@Username", username);
                    command.Parameters.AddWithValue("@Email", email);
                    command.Parameters.AddWithValue("@PasswordHash", passwordHash);
                    command.Parameters.AddWithValue("@Salt", salt);
                    command.Parameters.AddWithValue("@Level", level);
                    command.Parameters.AddWithValue("@Experience", level * 100);
                    command.Parameters.AddWithValue("@ExperienceToNextLevel", CalculateExpToNextLevel(level));
                    command.Parameters.AddWithValue("@Gold", gold);
                    command.Parameters.AddWithValue("@MMR", mmr);
                    command.Parameters.AddWithValue("@Rank", CalculateRankFromMMR(mmr));
                    command.Parameters.AddWithValue("@CommonShards", 0);
                    command.Parameters.AddWithValue("@RareShards", 0);
                    command.Parameters.AddWithValue("@EpicShards", 0);
                    command.Parameters.AddWithValue("@LegendaryShards", 0);
                    command.Parameters.AddWithValue("@TotalMatches", level * 10);
                    command.Parameters.AddWithValue("@TotalWins", level * 6);
                    command.Parameters.AddWithValue("@TotalLosses", level * 4);
                    command.Parameters.AddWithValue("@TotalKills", level * 50);
                    command.Parameters.AddWithValue("@TotalDeaths", level * 30);
                    command.Parameters.AddWithValue("@TotalHeadshots", level * 15);
                    command.Parameters.AddWithValue("@TotalAssists", level * 20);
                    command.Parameters.AddWithValue("@WinRate", 60.0);
                    command.Parameters.AddWithValue("@KDRatio", 1.666);
                    command.Parameters.AddWithValue("@TotalPlayTimeTicks", TimeSpan.FromHours(level * 5).Ticks);
                    command.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow.AddDays(-level * 7).ToString("o"));
                    command.Parameters.AddWithValue("@LastLogin", DateTime.UtcNow.AddDays(-1).ToString("o"));
                    command.ExecuteNonQuery();

                    // Get the auto-generated PlayerId
                    command.CommandText = "SELECT last_insert_rowid()";
                    var playerId = Convert.ToInt32(command.ExecuteScalar());

                    // Insert inventory.
                    //
                    // ITEM ID CONVENTION - ids are globally unique across every item type:
                    //     1     - 999    weapons, including grenades and melee
                    //     1000  - 1999   gun skins
                    //     2000  - 2999   character skins
                    // 1..4 matches the client's weapon registry (WeaponManager.LoadWeaponData):
                    // 1 Assault Rifle, 2 Pistol, 3 Combat Knife, 4 Frag Grenade.
                    command.CommandText = @"
                        INSERT INTO PlayerInventory (PlayerId, UnlockedItems, LootBoxes,
                                                     CommonCrates, RareCrates, EpicCrates, LegendaryCrates)
                        VALUES (@PlayerId, @UnlockedItems, @LootBoxes,
                                @CommonCrates, @RareCrates, @EpicCrates, @LegendaryCrates)";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@PlayerId", playerId);
                    command.Parameters.AddWithValue("@UnlockedItems", "[1,2,3,4,2001]");
                    command.Parameters.AddWithValue("@LootBoxes", "[]");
                    // New accounts start with no crates - they are earned.
                    command.Parameters.AddWithValue("@CommonCrates", 0);
                    command.Parameters.AddWithValue("@RareCrates", 0);
                    command.Parameters.AddWithValue("@EpicCrates", 0);
                    command.Parameters.AddWithValue("@LegendaryCrates", 0);
                    command.ExecuteNonQuery();

                    // Insert loadout
                    command.CommandText = @"
                        INSERT INTO PlayerLoadout (PlayerId, PrimaryWeaponId, SecondaryWeaponId,
                                                   MeleeWeaponId, GrenadeId, PlayerSkinId, WeaponSkinId,
                                                   PrimarySkinId, SecondarySkinId, MeleeSkinId, GrenadeSkinId,
                                                   UpdatedAt)
                        VALUES (@PlayerId, 1, 2, 3, 4, 2001, 0, 0, 0, 0, 0, @UpdatedAt)";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@PlayerId", playerId);
                    command.Parameters.AddWithValue("@UpdatedAt", DateTime.UtcNow.ToString("o"));
                    command.ExecuteNonQuery();

                    // The player starts owning the one character skin they are wearing.
                    command.CommandText = @"
                        INSERT OR REPLACE INTO PlayerCharacterSkins (PlayerId, SkinId, SortOrder)
                        VALUES (@PlayerId, 2001, 0)";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@PlayerId", playerId);
                    command.ExecuteNonQuery();

                    transaction.Commit();
                }
            }
        }

        private int CreateNewPlayer(string username, string email, string passwordHash, string salt)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var command = connection.CreateCommand();
                    command.Transaction = transaction;

                    command.CommandText = @"
                        INSERT INTO Players (
                            Username, Email, PasswordHash, Salt, CreatedAt, LastLogin
                        ) VALUES (
                            @Username, @Email, @PasswordHash, @Salt, @CreatedAt, @LastLogin
                        )";

                    command.Parameters.AddWithValue("@Username", username);
                    command.Parameters.AddWithValue("@Email", email);
                    command.Parameters.AddWithValue("@PasswordHash", passwordHash);
                    command.Parameters.AddWithValue("@Salt", salt);
                    command.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow.ToString("o"));
                    command.Parameters.AddWithValue("@LastLogin", DateTime.UtcNow.ToString("o"));
                    command.ExecuteNonQuery();

                    // Get the new PlayerId
                    command.CommandText = "SELECT last_insert_rowid()";
                    int newPlayerId = Convert.ToInt32(command.ExecuteScalar());

                    // Create inventory and loadout entries
                    command.CommandText = "INSERT INTO PlayerInventory (PlayerId) VALUES (@PlayerId)";
                    command.Parameters.AddWithValue("@PlayerId", newPlayerId);
                    command.ExecuteNonQuery();

                    command.CommandText = "INSERT INTO PlayerLoadout (PlayerId) VALUES (@PlayerId)";
                    command.ExecuteNonQuery();

                    transaction.Commit();
                    return newPlayerId;
                }
            }
        }

        private int CalculateRankFromMMR(int mmr)
        {
            if (mmr < 1000) return 1;
            if (mmr < 1150) return 2;
            if (mmr < 1300) return 3;
            if (mmr < 1450) return 4;
            if (mmr < 1600) return 5;
            if (mmr < 1750) return 6;
            if (mmr < 1900) return 7;
            if (mmr < 2050) return 8;
            if (mmr < 2200) return 9;
            return 10;
        }

        #endregion

        #region Database Operations

        private PlayerProfile GetPlayerProfileFromDb(string username)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT p.*, i.UnlockedItems, i.LootBoxes,
                           i.CommonCrates, i.RareCrates, i.EpicCrates, i.LegendaryCrates,
                           l.PrimaryWeaponId, l.SecondaryWeaponId, l.MeleeWeaponId, l.GrenadeId, 
                           l.PlayerSkinId
                    FROM Players p
                    LEFT JOIN PlayerInventory i ON p.PlayerId = i.PlayerId
                    LEFT JOIN PlayerLoadout l ON p.PlayerId = l.PlayerId
                    WHERE LOWER(p.Username) = LOWER(@Username)";
                command.Parameters.AddWithValue("@Username", username);

                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        var profile = MapReaderToPlayerProfile(reader);
                        LoadLoadoutExtras(connection, profile);
                        return profile;
                    }
                }
            }
            return null;
        }

        private PlayerProfile GetPlayerProfileFromDbById(int playerId)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT p.*, i.UnlockedItems, i.LootBoxes,
                           i.CommonCrates, i.RareCrates, i.EpicCrates, i.LegendaryCrates,
                           l.PrimaryWeaponId, l.SecondaryWeaponId, l.MeleeWeaponId, l.GrenadeId, 
                           l.PlayerSkinId
                    FROM Players p
                    LEFT JOIN PlayerInventory i ON p.PlayerId = i.PlayerId
                    LEFT JOIN PlayerLoadout l ON p.PlayerId = l.PlayerId
                    WHERE p.PlayerId = @PlayerId";
                command.Parameters.AddWithValue("@PlayerId", playerId);

                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        var profile = MapReaderToPlayerProfile(reader);
                        LoadLoadoutExtras(connection, profile);
                        return profile;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Adds a column to an existing table, ignoring the error when it is already there.
        ///
        /// SQLite has no "ADD COLUMN IF NOT EXISTS", and CREATE TABLE IF NOT EXISTS does nothing
        /// for a table that already exists - so a database created before a column was
        /// introduced would never gain it.
        /// </summary>
        /// <returns>True when the column was actually added by this call.</returns>
        private static bool AddColumnIfMissing(SqliteConnection connection, string table, string column, string definition)
        {
            try
            {
                using (var alter = connection.CreateCommand())
                {
                    alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
                    alter.ExecuteNonQuery();
                    Console.WriteLine($"[Database] Added column {table}.{column}");
                    return true;
                }
            }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column name"))
            {
                // Already present - the normal case for an up-to-date database.
                return false;
            }
        }

        /// <summary>
        /// One-time migration: copies OwnedWeapons / OwnedSkins / OwnedGrenades into the single
        /// UnlockedItems list.
        ///
        /// The per-type columns were replaced because item ids are globally unique, so splitting
        /// them by type was redundant - and actively harmful, since a bare loadout id could not be
        /// checked against a list that needed to be told the type first.
        ///
        /// Entries that already exist in UnlockedItems are skipped, so running this twice is safe.
        /// </summary>
        private void FoldLegacyOwnedItemsIntoUnlocked(SqliteConnection connection)
        {
            var legacyColumns = new[] { "OwnedWeapons", "OwnedSkins", "OwnedGrenades" };

            // Only attempt this if the legacy columns are actually present.
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var probe = connection.CreateCommand())
            {
                probe.CommandText = "SELECT name FROM pragma_table_info('PlayerInventory')";
                using (var reader = probe.ExecuteReader())
                {
                    while (reader.Read())
                        present.Add(reader.GetString(0));
                }
            }

            var usable = legacyColumns.Where(c => present.Contains(c)).ToList();
            if (usable.Count == 0)
                return;

            int migrated = 0;
            using (var read = connection.CreateCommand())
            {
                read.CommandText = $@"SELECT PlayerId, {string.Join(", ", usable)}
                                      FROM PlayerInventory";
                using (var reader = read.ExecuteReader())
                {
                    var rows = new List<(long PlayerId, List<int> Ids)>();
                    while (reader.Read())
                    {
                        long playerId = reader.GetInt64(0);
                        var ids = new List<int>();

                        for (int i = 0; i < usable.Count; i++)
                        {
                            if (reader.IsDBNull(i + 1)) continue;
                            var json = reader.GetString(i + 1);
                            if (string.IsNullOrWhiteSpace(json)) continue;

                            try
                            {
                                var parsed = JsonConvert.DeserializeObject<List<int>>(json);
                                if (parsed != null) ids.AddRange(parsed);
                            }
                            catch (JsonException)
                            {
                                // Leave a malformed legacy value alone rather than failing startup.
                            }
                        }

                        if (ids.Count > 0)
                            rows.Add((playerId, ids));
                    }
                    reader.Close();

                    using (var write = connection.CreateCommand())
                    {
                        write.CommandText = "UPDATE PlayerInventory SET UnlockedItems = @Items WHERE PlayerId = @PlayerId";
                        var itemsParam = write.Parameters.Add("@Items", SqliteType.Text);
                        var idParam = write.Parameters.Add("@PlayerId", SqliteType.Integer);

                        foreach (var (playerId, ids) in rows)
                        {
                            // Distinct: the same id could appear in two legacy columns.
                            itemsParam.Value = JsonConvert.SerializeObject(ids.Distinct().OrderBy(x => x).ToList());
                            idParam.Value = playerId;
                            write.ExecuteNonQuery();
                            migrated++;
                        }
                    }
                }
            }

            Console.WriteLine($"[Database] Migrated owned items into UnlockedItems for {migrated} player(s) " +
                              $"from {string.Join(", ", usable)}");
        }

        /// <summary>
        /// One-time migration: the old single WeaponSkinId column applied to whichever gun was
        /// in the primary slot. Copies it into the per-gun assignment table as the skin for that
        /// player's primary weapon, so an existing assignment is not silently lost.
        /// </summary>
        private void FoldLegacyWeaponSkinIntoAssignments(SqliteConnection connection)
        {
            // Only run while the new table is still empty for that player.
            int migrated = 0;
            using (var read = connection.CreateCommand())
            {
                read.CommandText = @"
                    SELECT l.PlayerId, l.PrimaryWeaponId, l.WeaponSkinId
                    FROM PlayerLoadout l
                    WHERE l.WeaponSkinId IS NOT NULL
                      AND l.WeaponSkinId > 0
                      AND NOT EXISTS (SELECT 1 FROM PlayerWeaponSkins s WHERE s.PlayerId = l.PlayerId)";
                using (var reader = read.ExecuteReader())
                {
                    var rows = new List<(long PlayerId, int WeaponId, int SkinId)>();
                    while (reader.Read())
                    {
                        rows.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2)));
                    }
                    reader.Close();

                    if (rows.Count == 0) return;

                    using (var write = connection.CreateCommand())
                    {
                        write.CommandText = @"INSERT OR REPLACE INTO PlayerWeaponSkins
                                              (PlayerId, WeaponId, SkinId, UpdatedAt)
                                              VALUES (@PlayerId, @WeaponId, @SkinId, @UpdatedAt)";
                        var p = write.Parameters.Add("@PlayerId", SqliteType.Integer);
                        var w = write.Parameters.Add("@WeaponId", SqliteType.Integer);
                        var s = write.Parameters.Add("@SkinId", SqliteType.Integer);
                        write.Parameters.AddWithValue("@UpdatedAt", DateTime.UtcNow.ToString("o"));

                        foreach (var (playerId, weaponId, skinId) in rows)
                        {
                            p.Value = playerId;
                            w.Value = weaponId;
                            s.Value = skinId;
                            write.ExecuteNonQuery();
                            migrated++;
                        }
                    }
                }
            }

            if (migrated > 0)
                Console.WriteLine($"[Database] Migrated legacy WeaponSkinId into per-gun assignments for {migrated} player(s)");
        }

        /// <summary>
        /// Loads the per-gun skin assignments and the owned character skins for a profile.
        ///
        /// These live in their own tables rather than as columns on PlayerLoadout, so they are
        /// fetched separately. Called while the profile reader is still open, on the same
        /// connection.
        /// </summary>
        private void LoadLoadoutExtras(SqliteConnection connection, PlayerProfile profile)
        {
            if (profile?.Loadout == null) return;

            profile.Loadout.WeaponSkins ??= new Dictionary<int, int>();
            profile.Loadout.CharacterSkins ??= new List<int>();

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT WeaponId, SkinId FROM PlayerWeaponSkins WHERE PlayerId = @PlayerId";
                cmd.Parameters.AddWithValue("@PlayerId", profile.PlayerId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    // Last write wins if a duplicate somehow exists; the primary key prevents it.
                    profile.Loadout.WeaponSkins[reader.GetInt32(0)] = reader.GetInt32(1);
                }
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT SkinId FROM PlayerCharacterSkins
                                    WHERE PlayerId = @PlayerId
                                    ORDER BY SortOrder, SkinId";
                cmd.Parameters.AddWithValue("@PlayerId", profile.PlayerId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    profile.Loadout.CharacterSkins.Add(reader.GetInt32(0));
                }
            }

            // Never advertise wearing a skin the player does not own.
            if (profile.Loadout.CharacterSkins.Count > 0
                && !profile.Loadout.CharacterSkins.Contains(profile.Loadout.PlayerSkinId))
            {
                profile.Loadout.PlayerSkinId = profile.Loadout.CharacterSkins[0];
            }
        }

        /// <summary>
        /// Gives every player the character skin they are currently wearing.
        ///
        /// The list of owned character skins is new, so a player migrating from the old schema
        /// had an empty list while PlayerSkinId still pointed at a skin. Without this they would
        /// be wearing something they do not own, and any "is this skin owned" check would fail.
        /// Idempotent: players who already own their worn skin are skipped.
        /// </summary>
        private void BackfillCharacterSkinsFromWorn(SqliteConnection connection)
        {
            int backfilled = 0;
            using (var read = connection.CreateCommand())
            {
                read.CommandText = @"
                    SELECT l.PlayerId, l.PlayerSkinId
                    FROM PlayerLoadout l
                    WHERE l.PlayerSkinId IS NOT NULL
                      AND l.PlayerSkinId > 0
                      AND NOT EXISTS (
                          SELECT 1 FROM PlayerCharacterSkins s
                          WHERE s.PlayerId = l.PlayerId AND s.SkinId = l.PlayerSkinId)";
                using (var reader = read.ExecuteReader())
                {
                    var rows = new List<(long PlayerId, int SkinId)>();
                    while (reader.Read())
                        rows.Add((reader.GetInt64(0), reader.GetInt32(1)));
                    reader.Close();

                    if (rows.Count == 0) return;

                    using var write = connection.CreateCommand();
                    write.CommandText = @"INSERT OR REPLACE INTO PlayerCharacterSkins
                                          (PlayerId, SkinId, SortOrder) VALUES (@PlayerId, @SkinId, 0)";
                    var p = write.Parameters.Add("@PlayerId", SqliteType.Integer);
                    var s = write.Parameters.Add("@SkinId", SqliteType.Integer);

                    foreach (var (playerId, skinId) in rows)
                    {
                        p.Value = playerId;
                        s.Value = skinId;
                        write.ExecuteNonQuery();
                        backfilled++;
                    }
                }
            }

            if (backfilled > 0)
                Console.WriteLine($"[Database] Backfilled owned character skins for {backfilled} player(s)");
        }

        private PlayerProfile MapReaderToPlayerProfile(SqliteDataReader reader)
        {
            var profile = new PlayerProfile
            {
                PlayerId = reader.GetInt32(reader.GetOrdinal("PlayerId")),
                Username = reader.GetString(reader.GetOrdinal("Username")),
                Email = reader.GetString(reader.GetOrdinal("Email")),
                Level = reader.GetInt32(reader.GetOrdinal("Level")),
                Experience = reader.GetInt32(reader.GetOrdinal("Experience")),
                ExperienceToNextLevel = reader.GetInt32(reader.GetOrdinal("ExperienceToNextLevel")),
                Gold = reader.GetInt32(reader.GetOrdinal("Gold")),
                Money = (int)reader.GetDouble(reader.GetOrdinal("Money")),
                CommonShards = reader.GetInt32(reader.GetOrdinal("CommonShards")),
                RareShards = reader.GetInt32(reader.GetOrdinal("RareShards")),
                EpicShards = reader.GetInt32(reader.GetOrdinal("EpicShards")),
                LegendaryShards = reader.GetInt32(reader.GetOrdinal("LegendaryShards")),
                MMR = reader.GetInt32(reader.GetOrdinal("MMR")),
                Rank = reader.GetInt32(reader.GetOrdinal("Rank")),
                TotalStats = new PlayerStats
                {
                    TotalMatches = reader.GetInt32(reader.GetOrdinal("TotalMatches")),
                    TotalWins = reader.GetInt32(reader.GetOrdinal("TotalWins")),
                    TotalLosses = reader.GetInt32(reader.GetOrdinal("TotalLosses")),
                    TotalKills = reader.GetInt32(reader.GetOrdinal("TotalKills")),
                    TotalDeaths = reader.GetInt32(reader.GetOrdinal("TotalDeaths")),
                    TotalHeadshots = reader.GetInt32(reader.GetOrdinal("TotalHeadshots")),
                    TotalAssists = reader.GetInt32(reader.GetOrdinal("TotalAssists")),
                    WinRate = (float)reader.GetDouble(reader.GetOrdinal("WinRate")),
                    KDRatio = (float)reader.GetDouble(reader.GetOrdinal("KDRatio"))
                },
                Inventory = new PlayerInventory
                {
                    UnlockedItems = JsonConvert.DeserializeObject<List<int>>(reader.GetString(reader.GetOrdinal("UnlockedItems"))),
                    LootBoxes = JsonConvert.DeserializeObject<List<LootBox>>(reader.GetString(reader.GetOrdinal("LootBoxes"))),
                    CommonCrates = reader.GetInt32(reader.GetOrdinal("CommonCrates")),
                    RareCrates = reader.GetInt32(reader.GetOrdinal("RareCrates")),
                    EpicCrates = reader.GetInt32(reader.GetOrdinal("EpicCrates")),
                    LegendaryCrates = reader.GetInt32(reader.GetOrdinal("LegendaryCrates"))
                },
                Loadout = new PlayerLoadout
                {
                    PrimaryWeaponId = reader.GetInt32(reader.GetOrdinal("PrimaryWeaponId")),
                    SecondaryWeaponId = reader.GetInt32(reader.GetOrdinal("SecondaryWeaponId")),
                    MeleeWeaponId = reader.GetInt32(reader.GetOrdinal("MeleeWeaponId")),
                    GrenadeId = reader.GetInt32(reader.GetOrdinal("GrenadeId")),
                    PlayerSkinId = reader.GetInt32(reader.GetOrdinal("PlayerSkinId")),
                    // Per-gun skins and character skins live in their own tables; loaded by
                    // LoadLoadoutExtras so this mapper stays a pure column read.
                    WeaponSkins = new Dictionary<int, int>(),
                    CharacterSkins = new List<int>()
                },
                TotalPlayTime = TimeSpan.FromTicks(reader.GetInt64(reader.GetOrdinal("TotalPlayTimeTicks"))),
                CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
                LastLogin = DateTime.Parse(reader.GetString(reader.GetOrdinal("LastLogin"))),
                LoginStreak = reader.GetInt32(reader.GetOrdinal("LoginStreak"))
            };

            return profile;
        }

        private bool UsernameExists(string username)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM Players WHERE LOWER(Username) = LOWER(@Username)";
                command.Parameters.AddWithValue("@Username", username);
                return Convert.ToInt32(command.ExecuteScalar()) > 0;
            }
        }

        private bool EmailExists(string email)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM Players WHERE LOWER(Email) = LOWER(@Email)";
                command.Parameters.AddWithValue("@Email", email);
                return Convert.ToInt32(command.ExecuteScalar()) > 0;
            }
        }

        private void UpdatePlayerProfile(Player player)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = @"
                    UPDATE Players SET 
                        Level = @Level,
                        Experience = @Experience,
                        ExperienceToNextLevel = @ExperienceToNextLevel,
                        Gold = @Gold,
                        Money = @Money,
                        CommonShards = @CommonShards,
                        RareShards = @RareShards,
                        EpicShards = @EpicShards,
                        LegendaryShards = @LegendaryShards,
                        MMR = @MMR,
                        Rank = @Rank,
                        TotalMatches = @TotalMatches,
                        TotalWins = @TotalWins,
                        TotalLosses = @TotalLosses,
                        TotalKills = @TotalKills,
                        TotalDeaths = @TotalDeaths,
                        TotalHeadshots = @TotalHeadshots,
                        TotalAssists = @TotalAssists,
                        WinRate = @WinRate,
                        KDRatio = @KDRatio,
                        TotalPlayTimeTicks = @TotalPlayTimeTicks,
                        LastLogin = @LastLogin,
                        LoginStreak = @LoginStreak
                    WHERE PlayerId = @PlayerId";

                command.Parameters.AddWithValue("@Level", player.Level);
                command.Parameters.AddWithValue("@Experience", player.Experience);
                command.Parameters.AddWithValue("@ExperienceToNextLevel", player.ExperienceToNextLevel);
                command.Parameters.AddWithValue("@Gold", player.Gold);
                command.Parameters.AddWithValue("@Money", player.Money);
                command.Parameters.AddWithValue("@CommonShards", player.CommonShards);
                command.Parameters.AddWithValue("@RareShards", player.RareShards);
                command.Parameters.AddWithValue("@EpicShards", player.EpicShards);
                command.Parameters.AddWithValue("@LegendaryShards", player.LegendaryShards);
                command.Parameters.AddWithValue("@MMR", player.MMR);
                command.Parameters.AddWithValue("@Rank", player.Rank);

                if (player.Stats != null)
                {
                    command.Parameters.AddWithValue("@TotalMatches", player.Stats.TotalMatches);
                    command.Parameters.AddWithValue("@TotalWins", player.Stats.TotalWins);
                    command.Parameters.AddWithValue("@TotalLosses", player.Stats.TotalLosses);
                    command.Parameters.AddWithValue("@TotalKills", player.Stats.TotalKills);
                    command.Parameters.AddWithValue("@TotalDeaths", player.Stats.TotalDeaths);
                    command.Parameters.AddWithValue("@TotalHeadshots", player.Stats.TotalHeadshots);
                    command.Parameters.AddWithValue("@TotalAssists", player.Stats.TotalAssists);
                    command.Parameters.AddWithValue("@WinRate", player.Stats.WinRate);
                    command.Parameters.AddWithValue("@KDRatio", player.Stats.KDRatio);
                }

                command.Parameters.AddWithValue("@TotalPlayTimeTicks", player.TotalPlayTime.Ticks);
                command.Parameters.AddWithValue("@LastLogin", DateTime.UtcNow.ToString("o"));
                command.Parameters.AddWithValue("@LoginStreak", player.LoginStreak);
                command.Parameters.AddWithValue("@PlayerId", player.PlayerId);

                command.ExecuteNonQuery();
            }
        }

        private void UpdatePlayerInventory(Player player)
        {
            if (player.Inventory == null) return;

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = @"
                    UPDATE PlayerInventory SET
                        UnlockedItems = @UnlockedItems,
                        LootBoxes = @LootBoxes,
                        CommonCrates = @CommonCrates,
                        RareCrates = @RareCrates,
                        EpicCrates = @EpicCrates,
                        LegendaryCrates = @LegendaryCrates
                    WHERE PlayerId = @PlayerId";

                command.Parameters.AddWithValue("@UnlockedItems", JsonConvert.SerializeObject(player.Inventory.UnlockedItems));
                command.Parameters.AddWithValue("@LootBoxes", JsonConvert.SerializeObject(player.Inventory.LootBoxes));
                command.Parameters.AddWithValue("@CommonCrates", player.Inventory.CommonCrates);
                command.Parameters.AddWithValue("@RareCrates", player.Inventory.RareCrates);
                command.Parameters.AddWithValue("@EpicCrates", player.Inventory.EpicCrates);
                command.Parameters.AddWithValue("@LegendaryCrates", player.Inventory.LegendaryCrates);
                command.Parameters.AddWithValue("@PlayerId", player.PlayerId);

                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Persists the loadout: the slot assignments, the per-gun skin assignments, and the
        /// owned character skins.
        ///
        /// The per-gun table is rewritten wholesale for this player rather than diffed - the map
        /// is tiny, and a clean delete-and-insert cannot drift out of sync with the in-memory
        /// state the way a partial update can.
        /// </summary>
        private void UpdatePlayerLoadout(Player player)
        {
            if (player.Loadout == null) return;

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var transaction = connection.BeginTransaction();

                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
                        UPDATE PlayerLoadout SET
                            PrimaryWeaponId = @PrimaryWeaponId,
                            SecondaryWeaponId = @SecondaryWeaponId,
                            MeleeWeaponId = @MeleeWeaponId,
                            GrenadeId = @GrenadeId,
                            PlayerSkinId = @PlayerSkinId,
                            UpdatedAt = @UpdatedAt
                        WHERE PlayerId = @PlayerId";
                    command.Parameters.AddWithValue("@PrimaryWeaponId", player.Loadout.PrimaryWeaponId);
                    command.Parameters.AddWithValue("@SecondaryWeaponId", player.Loadout.SecondaryWeaponId);
                    command.Parameters.AddWithValue("@MeleeWeaponId", player.Loadout.MeleeWeaponId);
                    command.Parameters.AddWithValue("@GrenadeId", player.Loadout.GrenadeId);
                    command.Parameters.AddWithValue("@PlayerSkinId", player.Loadout.PlayerSkinId);
                    command.Parameters.AddWithValue("@UpdatedAt", DateTime.UtcNow.ToString("o"));
                    command.Parameters.AddWithValue("@PlayerId", player.PlayerId);
                    command.ExecuteNonQuery();
                }

                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM PlayerWeaponSkins WHERE PlayerId = @PlayerId";
                    command.Parameters.AddWithValue("@PlayerId", player.PlayerId);
                    command.ExecuteNonQuery();
                }

                // Only positive skin ids are stored; 0 means "wearing the default skin", which is
                // the absence of an assignment rather than an assignment to skin 0.
                var skins = player.Loadout.WeaponSkins;
                if (skins != null && skins.Count > 0)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = @"INSERT OR REPLACE INTO PlayerWeaponSkins
                                            (PlayerId, WeaponId, SkinId, UpdatedAt)
                                            VALUES (@PlayerId, @WeaponId, @SkinId, @UpdatedAt)";
                    var p = command.Parameters.Add("@PlayerId", SqliteType.Integer);
                    var w = command.Parameters.Add("@WeaponId", SqliteType.Integer);
                    var s = command.Parameters.Add("@SkinId", SqliteType.Integer);
                    command.Parameters.AddWithValue("@UpdatedAt", DateTime.UtcNow.ToString("o"));

                    foreach (var pair in skins)
                    {
                        if (pair.Value <= 0) continue;
                        p.Value = player.PlayerId;
                        w.Value = pair.Key;
                        s.Value = pair.Value;
                        command.ExecuteNonQuery();
                    }
                }

                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM PlayerCharacterSkins WHERE PlayerId = @PlayerId";
                    command.Parameters.AddWithValue("@PlayerId", player.PlayerId);
                    command.ExecuteNonQuery();
                }

                var characters = player.Loadout.CharacterSkins;
                if (characters != null && characters.Count > 0)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = @"INSERT OR REPLACE INTO PlayerCharacterSkins
                                            (PlayerId, SkinId, SortOrder)
                                            VALUES (@PlayerId, @SkinId, @SortOrder)";
                    var p = command.Parameters.Add("@PlayerId", SqliteType.Integer);
                    var s = command.Parameters.Add("@SkinId", SqliteType.Integer);
                    var o = command.Parameters.Add("@SortOrder", SqliteType.Integer);

                    for (int i = 0; i < characters.Count; i++)
                    {
                        p.Value = player.PlayerId;
                        s.Value = characters[i];
                        o.Value = i;
                        command.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
            }
        }

        #endregion

        #region Packet Handlers

        private async void HandleLogin(string jsonData, IPEndPoint clientEndpoint)
        {
            var loginRequest = JsonConvert.DeserializeObject<LoginRequest>(jsonData);

            if (loginRequest == null || string.IsNullOrEmpty(loginRequest.username) || string.IsNullOrEmpty(loginRequest.password))
            {
                SendLoginResponse(clientEndpoint, false, 0, null, "Username and password required");
                OnFailedLogin?.Invoke("Missing credentials", clientEndpoint);
                return;
            }

            // Check if player is already logged in
            var playerProfile = GetPlayerProfileFromDb(loginRequest.username);

            if (playerProfile == null)
            {
                SendLoginResponse(clientEndpoint, false, 0, null, "Invalid username or password");
                OnFailedLogin?.Invoke("Invalid credentials", clientEndpoint);
                Console.WriteLine($"[LoginManager] Failed login attempt for '{loginRequest.username}' from {clientEndpoint.Address}");
                return;
            }

            // Reconnect support: if this account already has a session, tear down the
            // old one first instead of hard-rejecting. On a clean client quit the old
            // session is gone via logout/heartbeat; this covers the case where the
            // client crashed or the UDP socket died without a logout, which previously
            // locked the account ("Player already logged in") until a server restart.
            if (connectedPlayers.TryGetValue(playerProfile.PlayerId, out var existingPlayer))
            {
                Console.WriteLine($"[LoginManager] Player '{playerProfile.Username}' reconnecting - replacing existing session");
                DisconnectPlayer(existingPlayer);
            }

            // Create Player instance from profile
            var player = CreatePlayerFromProfile(playerProfile);
            player.ClientEndpoint = clientEndpoint;
            player.IsConnected = true;
            player.LastHeartbeat = DateTime.UtcNow;

            // Grant any due daily-login reward using the PREVIOUS login time
            // (player.LastLogin still holds the value loaded from the profile
            // here, because we have not overwritten it with "now" yet).
            ApplyDailyLogin(player);

            player.LastLogin = DateTime.UtcNow;

            // Create client connection in server manager
            var client = serverManager.CreateClient(player.PlayerId, player.Username, clientEndpoint);
            player.SessionToken = client.SessionToken;

            // Store player in memory
            connectedPlayers[player.PlayerId] = player;
            tokenToPlayer[player.SessionToken] = player;

            // Update the player profile (now includes the refreshed LastLogin,
            // LoginStreak and any daily-login reward that was just granted).
            UpdatePlayerProfile(player);

            // Send success response
            SendLoginResponse(clientEndpoint, true, player.PlayerId, player.SessionToken, "Login successful");

            Console.WriteLine($"[LoginManager] [OK] Player '{player.Username}' (ID: {player.PlayerId}, MMR: {player.MMR}) logged in from {clientEndpoint.Address}");

            OnPlayerLoggedIn?.Invoke(player);
            friendsManager?.UpdatePlayerStatus(player.PlayerId, PlayerStatus.Online);
        }

        private Player CreatePlayerFromProfile(PlayerProfile profile)
        {
            return new Player(profile.PlayerId, profile.Username, profile.Email, "")
            {
                MMR = profile.MMR,
                Rank = profile.Rank,
                Level = profile.Level,
                Experience = profile.Experience,
                ExperienceToNextLevel = profile.ExperienceToNextLevel,
                Gold = profile.Gold,
                Money = profile.Money,
                // Must be carried over too: UpdatePlayerProfile writes player.CommonShards etc.
                // straight back to the database, so anything not copied here would be reset to
                // zero on the player's next save.
                CommonShards = profile.CommonShards,
                RareShards = profile.RareShards,
                EpicShards = profile.EpicShards,
                LegendaryShards = profile.LegendaryShards,
                Inventory = profile.Inventory ?? new PlayerInventory(),
                Loadout = profile.Loadout ?? new PlayerLoadout(),
                Stats = profile.TotalStats ?? new PlayerStats(),
                TotalPlayTime = profile.TotalPlayTime,
                CreatedAt = profile.CreatedAt,
                LastLogin = profile.LastLogin,
                LoginStreak = profile.LoginStreak
            };
        }

        private async void HandleRegister(string jsonData, IPEndPoint clientEndpoint)
        {
            var registerRequest = JsonConvert.DeserializeObject<RegisterRequest>(jsonData);

            if (registerRequest == null)
            {
                SendRegisterResponse(clientEndpoint, false, "Invalid registration data");
                return;
            }

            if (string.IsNullOrEmpty(registerRequest.username) ||
                string.IsNullOrEmpty(registerRequest.password) ||
                string.IsNullOrEmpty(registerRequest.email))
            {
                SendRegisterResponse(clientEndpoint, false, "All fields are required");
                return;
            }

            if (registerRequest.username.Length < 3 || registerRequest.username.Length > 20)
            {
                SendRegisterResponse(clientEndpoint, false, "Username must be between 3 and 20 characters");
                return;
            }

            if (!IsValidEmail(registerRequest.email))
            {
                SendRegisterResponse(clientEndpoint, false, "Invalid email format");
                return;
            }

            if (registerRequest.password.Length < 6)
            {
                SendRegisterResponse(clientEndpoint, false, "Password must be at least 6 characters");
                return;
            }

            if (UsernameExists(registerRequest.username))
            {
                SendRegisterResponse(clientEndpoint, false, "Username already taken");
                return;
            }

            if (EmailExists(registerRequest.email))
            {
                SendRegisterResponse(clientEndpoint, false, "Email already registered");
                return;
            }

            string salt = GenerateSalt();
            string passwordHash = HashPassword(registerRequest.password, salt);

            int playerId = CreateNewPlayer(registerRequest.username, registerRequest.email, passwordHash, salt);

            if (playerId > 0)
            {
                SendRegisterResponse(clientEndpoint, true, "Registration successful! Please login.");
                Console.WriteLine($"[LoginManager] [OK] New player registered: '{registerRequest.username}' (ID: {playerId})");
            }
            else
            {
                SendRegisterResponse(clientEndpoint, false, "Registration failed. Please try again.");
            }
        }

        private async void HandleHeartbeat(string jsonData, IPEndPoint clientEndpoint)
        {
            var heartbeat = JsonConvert.DeserializeObject<HeartbeatPacket>(jsonData);

            if (heartbeat == null || string.IsNullOrEmpty(heartbeat.token))
            {
                return;
            }

            if (tokenToPlayer.TryGetValue(heartbeat.token, out Player player))
            {
                player.UpdateHeartbeat();

                // Update client heartbeat in server manager
                var client = serverManager.GetClientByToken(heartbeat.token);
                if (client != null)
                {
                    serverManager.UpdateHeartbeat(client);
                }

                var response = new { type = "heartbeat_response", timestamp = DateTime.UtcNow };
                serverManager.SendPacket(response, clientEndpoint);
            }
        }

        private async void HandleLogout(string jsonData, IPEndPoint clientEndpoint)
        {
            var logoutRequest = JsonConvert.DeserializeObject<LogoutRequest>(jsonData);

            if (logoutRequest == null || string.IsNullOrEmpty(logoutRequest.token))
            {
                return;
            }

            if (tokenToPlayer.TryGetValue(logoutRequest.token, out Player player))
            {
                DisconnectPlayer(player);

                var response = new { type = "logout_response", success = true, message = "Logged out successfully" };
                serverManager.SendPacket(response, clientEndpoint);
            }
        }

        private void HandleClientDisconnected(ClientConnection client)
        {
            if (tokenToPlayer.TryGetValue(client.SessionToken, out Player player))
            {
                DisconnectPlayer(player);
            }
        }

        private void UpdatePlayerGameStats(int playerId, int kills, int deaths, bool isWinner, int goldReward, int xpReward)
        {
            if (connectedPlayers.TryGetValue(playerId, out Player player))
            {
                player.Kills += kills;
                player.Deaths += deaths;
                player.AddGold(goldReward);
                player.AddExperience(xpReward);

                if (player.Stats != null)
                {
                    player.Stats.TotalMatches++;
                    if (isWinner)
                        player.Stats.TotalWins++;
                    else
                        player.Stats.TotalLosses++;

                    player.Stats.TotalKills += kills;
                    player.Stats.TotalDeaths += deaths;

                    // Update derived stats
                    player.Stats.WinRate = (float)player.Stats.TotalWins / player.Stats.TotalMatches * 100f;
                    player.Stats.KDRatio = player.Stats.TotalDeaths > 0 ?
                        (float)player.Stats.TotalKills / player.Stats.TotalDeaths :
                        player.Stats.TotalKills;
                }

                // Save to database
                UpdatePlayerProfile(player);
                UpdatePlayerInventory(player);
                UpdatePlayerLoadout(player);

                Console.WriteLine($"[LoginManager] {player.Username} updated stats - Kills: {player.Kills}, Deaths: {player.Deaths}");
            }
        }

        private void DisconnectPlayer(Player player)
        {
            if (player == null) return;

            friendsManager?.OnPlayerDisconnected(player.PlayerId);

            // Remove the player from any active game first so the remaining
            // clients receive a player_despawn and stop rendering them.
            if (player.IsInGame)
                gameManager?.RemovePlayerFromGame(player.PlayerId);

            // Remove from queue if in queue
            matchmakingQueue.RemoveFromQueue(player);

            // Remove the matching ClientConnection from the server manager's
            // lookups too. Without this, a reconnect (login that replaces an old
            // session) leaves a stale ClientConnection in heartbeatMap, which later
            // timeouts out and removes the NEW connection for the same PlayerId.
            // Use the event-free variant to avoid recursing back into this method.
            var client = serverManager.GetClientById(player.PlayerId);
            if (client != null)
                serverManager.RemoveClientConnection(client);

            // Save player data to database before disconnecting
            UpdatePlayerProfile(player);
            UpdatePlayerInventory(player);
            UpdatePlayerLoadout(player);

            // Update player state
            player.IsConnected = false;
            player.IsInGame = false;
            player.IsInQueue = false;

            // Find and remove token
            var token = tokenToPlayer.FirstOrDefault(kvp => kvp.Value.PlayerId == player.PlayerId).Key;
            if (token != null)
            {
                tokenToPlayer.TryRemove(token, out _);
            }

            connectedPlayers.TryRemove(player.PlayerId, out _);

            Console.WriteLine($"[LoginManager] Player '{player.Username}' disconnected");
            OnPlayerDisconnected?.Invoke(player);
        }

        /// <summary>
        /// Clears the in-game flags on the Player model for a player who is no
        /// longer in a game (used when the game ends or is forfeited). The
        /// ClientConnection flags are owned by GameManager; this clears the
        /// parallel Player-model flags that block matchmaking re-entry.
        /// </summary>
        public void ResetPlayerGameState(int playerId)
        {
            if (connectedPlayers.TryGetValue(playerId, out Player player))
            {
                player.IsInGame = false;
                player.IsInQueue = false;
                player.CurrentGameId = -1;
                player.TeamId = -1;
                player.Kills = 0;
                player.Deaths = 0;
                player.Score = 0;
            }
        }

        private void HandleGetProfile(string jsonData, IPEndPoint clientEndpoint)
        {
            var profileRequest = JsonConvert.DeserializeObject<GetProfileRequest>(jsonData);

            if (profileRequest == null || string.IsNullOrEmpty(profileRequest.token))
            {
                serverManager.SendErrorResponse(clientEndpoint, "Invalid token", 401);
                return;
            }

            if (!tokenToPlayer.TryGetValue(profileRequest.token, out Player player))
            {
                serverManager.SendErrorResponse(clientEndpoint, "Invalid or expired token", 401);
                return;
            }

            var profile = player.GetFullProfile();
            var response = new ProfileDataWrapper
            {
                type = "profile_data",
                profile = profile,
                timestamp = DateTime.UtcNow
            };
            serverManager.SendPacket(response, clientEndpoint);

            Console.WriteLine($"[LoginManager] Sent profile data for player '{player.Username}'");
        }

        private void HandleJoinQueue(string jsonData, IPEndPoint clientEndpoint)
        {
            var queueRequest = JsonConvert.DeserializeObject<JoinQueueRequest>(jsonData);

            if (queueRequest == null || string.IsNullOrEmpty(queueRequest.token))
            {
                serverManager.SendErrorResponse(clientEndpoint, "Invalid token", 401);
                return;
            }

            if (!tokenToPlayer.TryGetValue(queueRequest.token, out Player player))
            {
                serverManager.SendErrorResponse(clientEndpoint, "Invalid or expired token", 401);
                return;
            }

            if (player.IsInGame)
            {
                var response = new QueueResponseData
                {
                    type = "queue_response",
                    success = false,
                    message = "Player is already in a game"
                };
                serverManager.SendPacket(response, clientEndpoint);
                return;
            }

            bool added = matchmakingQueue.AddToQueue(player);

            if (added)
            {
                friendsManager?.UpdatePlayerStatus(player.PlayerId, PlayerStatus.InQueue);
            }

            var queueResponse = new QueueResponseData
            {
                type = "queue_response",
                success = added,
                message = added ? "Added to matchmaking queue" : "Failed to add to queue",
                queueSize = matchmakingQueue.QueueSize,
                position = matchmakingQueue.GetQueuePosition(player.PlayerId),
                estimatedWaitTime = EstimateWaitTime(),
                playersNeeded = Math.Max(0, MatchmakingQueue.PLAYERS_PER_GAME - matchmakingQueue.QueueSize)
            };

            serverManager.SendPacket(queueResponse, clientEndpoint);
        }

        private void HandleLeaveQueue(string jsonData, IPEndPoint clientEndpoint)
        {
            var leaveRequest = JsonConvert.DeserializeObject<LeaveQueueRequest>(jsonData);

            if (leaveRequest == null || string.IsNullOrEmpty(leaveRequest.token))
            {
                serverManager.SendErrorResponse(clientEndpoint, "Invalid token", 401);
                return;
            }

            if (!tokenToPlayer.TryGetValue(leaveRequest.token, out Player player))
            {
                serverManager.SendErrorResponse(clientEndpoint, "Invalid or expired token", 401);
                return;
            }

            bool removed = matchmakingQueue.RemoveFromQueue(player);

            if (removed)
            {
                friendsManager?.UpdatePlayerStatus(player.PlayerId, PlayerStatus.Online);
            }

            var response = new QueueResponseData
            {
                type = "queue_response",
                success = removed,
                message = removed ? "Removed from matchmaking queue" : "Not in queue",
                queueSize = matchmakingQueue.QueueSize
            };

            serverManager.SendPacket(response, clientEndpoint);
        }

        private void HandleGetQueueStatus(string jsonData, IPEndPoint clientEndpoint)
        {
            var statusRequest = JsonConvert.DeserializeObject<GetQueueStatusRequest>(jsonData);

            if (statusRequest == null || string.IsNullOrEmpty(statusRequest.token))
            {
                serverManager.SendErrorResponse(clientEndpoint, "Invalid token", 401);
                return;
            }

            if (!tokenToPlayer.TryGetValue(statusRequest.token, out Player player))
            {
                serverManager.SendErrorResponse(clientEndpoint, "Invalid or expired token", 401);
                return;
            }

            var response = new QueueStatusData
            {
                type = "queue_status",
                queueSize = matchmakingQueue.QueueSize,
                position = matchmakingQueue.GetQueuePosition(player.PlayerId),
                estimatedWaitTime = EstimateWaitTime(),
                playersNeeded = Math.Max(0, MatchmakingQueue.PLAYERS_PER_GAME - matchmakingQueue.QueueSize),
                inQueue = player.IsInQueue,
                timestamp = DateTime.UtcNow
            };

            serverManager.SendPacket(response, clientEndpoint);
        }

        #endregion

        #region Matchmaking Event Handlers

        private void HandleGameMatched(List<Player> players, GameSession gameSession)
        {
            Console.WriteLine($"\n[LoginManager] Matchmaking complete with {players.Count} players; creating game.");

            // Mark matched players as InGame for their friends.
            foreach (var player in players)
            {
                if (player.IsConnected)
                    friendsManager?.UpdatePlayerStatus(player.PlayerId, PlayerStatus.InGame);
            }

            // GameManager.CreateGame is the single authority here: it assigns a
            // stable game id and balanced teams and sends one authoritative
            // game_start (+ player_spawn) packet to each participant. Calling it
            // once avoids the previous bug of creating N GameRooms per match and
            // sending conflicting game_start packets with mismatched ids/teams.
            gameManager.CreateGame(players, GameType.TeamDeathmatch, Map.Office);
        }

        private void HandlePlayerEnteredQueue(QueuedPlayer queuedPlayer)
        {
            var player = queuedPlayer.Player;
            if (player != null && player.IsConnected)
            {
                SendQueueStatusUpdate(player);
                Console.WriteLine($"[Matchmaking] Player {player.Username} entered queue. Position: {matchmakingQueue.GetQueuePosition(player.PlayerId)}");
            }
        }

        private void HandlePlayerLeftQueue(QueuedPlayer queuedPlayer)
        {
            var player = queuedPlayer.Player;
            if (player != null && player.IsConnected)
            {
                SendQueueStatusUpdate(player);
                Console.WriteLine($"[Matchmaking] Player {player.Username} left queue");
            }
        }

        private void HandlePlayerTimedOut(QueuedPlayer queuedPlayer)
        {
            var player = queuedPlayer.Player;
            if (player != null && player.IsConnected)
            {
                var timeoutPacket = new QueueTimeoutData
                {
                    type = "queue_timeout",
                    message = "Matchmaking queue timed out. Please try again.",
                    timestamp = DateTime.UtcNow
                };
                serverManager.SendPacket(timeoutPacket, player.ClientEndpoint);
                Console.WriteLine($"[Matchmaking] Player {player.Username} timed out of queue");
            }
        }

        private void HandleQueueStatusChanged(int queueSize)
        {
            foreach (var player in connectedPlayers.Values)
            {
                if (player.IsInQueue && player.IsConnected)
                {
                    SendQueueStatusUpdate(player);
                }
            }
        }

        private void SendQueueStatusUpdate(Player player)
        {
            if (player != null && player.IsConnected)
            {
                var statusPacket = new QueueStatusData
                {
                    type = "queue_status",
                    queueSize = matchmakingQueue.QueueSize,
                    position = matchmakingQueue.GetQueuePosition(player.PlayerId),
                    estimatedWaitTime = EstimateWaitTime(),
                    playersNeeded = Math.Max(0, MatchmakingQueue.PLAYERS_PER_GAME - matchmakingQueue.QueueSize),
                    inQueue = player.IsInQueue,
                    timestamp = DateTime.UtcNow
                };

                serverManager.SendPacket(statusPacket, player.ClientEndpoint);
            }
        }

        #endregion

        #region Helper Methods

        private int EstimateWaitTime()
        {
            int queueSize = matchmakingQueue.QueueSize;
            int playersNeeded = Math.Max(0, MatchmakingQueue.PLAYERS_PER_GAME - queueSize);
            return (playersNeeded / 2) * 30;
        }

        private int CalculateExpToNextLevel(int level)
        {
            return 100 + (level * 50);
        }

        private string GenerateSalt()
        {
            byte[] saltBytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(saltBytes);
            }
            return Convert.ToBase64String(saltBytes);
        }

        private string HashPassword(string password, string salt)
        {
            using (var sha256 = SHA256.Create())
            {
                string combined = password + salt;
                byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(combined));
                return Convert.ToBase64String(hashBytes);
            }
        }

        private bool IsValidEmail(string email)
        {
            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Response Methods

        private void SendLoginResponse(IPEndPoint endpoint, bool success, int playerId, string token, string message)
        {
            var response = new LoginResponse
            {
                type = "login_response",
                success = success,
                playerId = playerId,
                token = token,
                message = message,
                timestamp = DateTime.UtcNow
            };

            serverManager.SendPacket(response, endpoint);
        }

        private void SendRegisterResponse(IPEndPoint endpoint, bool success, string message)
        {
            var response = new RegisterResponse
            {
                type = "register_response",
                success = success,
                message = message,
                timestamp = DateTime.UtcNow
            };

            serverManager.SendPacket(response, endpoint);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Persists just the four shard columns for one account.
        ///
        /// Separate from UpdatePlayerProfile on purpose: shards are granted mid-run, by a drop,
        /// and a full profile write from the game-loop thread would race the packet handlers that
        /// also write the profile. This touches only the four columns it owns.
        /// </summary>
        public void UpdatePlayerShards(Player player)
        {
            if (player == null) return;

            try
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    UPDATE Players SET
                        CommonShards = @CommonShards,
                        RareShards = @RareShards,
                        EpicShards = @EpicShards,
                        LegendaryShards = @LegendaryShards
                    WHERE PlayerId = @PlayerId";
                command.Parameters.AddWithValue("@CommonShards", player.CommonShards);
                command.Parameters.AddWithValue("@RareShards", player.RareShards);
                command.Parameters.AddWithValue("@EpicShards", player.EpicShards);
                command.Parameters.AddWithValue("@LegendaryShards", player.LegendaryShards);
                command.Parameters.AddWithValue("@PlayerId", player.PlayerId);
                command.ExecuteNonQuery();
            }
            catch (SqliteException ex)
            {
                // A failed shard write must not take the run down; the in-memory balance is still
                // correct, and the next logout writes the full profile.
                Console.WriteLine($"[Database] Failed to persist shards for {player.Username}: {ex.Message}");
            }
        }

        public Player GetPlayerByToken(string token)
        {
            tokenToPlayer.TryGetValue(token, out Player player);
            return player;
        }

        public Player GetPlayerById(int playerId)
        {
            connectedPlayers.TryGetValue(playerId, out Player player);
            return player;
        }

        public PlayerProfile GetPlayerProfile(int playerId)
        {
            // First check connected players
            var player = GetPlayerById(playerId);
            if (player != null)
            {
                return player.GetFullProfile();
            }

            // Fall back to database
            return GetPlayerProfileFromDbById(playerId);
        }

        public void UpdatePlayerStatusForFriends(int playerId, PlayerStatus status)
        {
            friendsManager?.UpdatePlayerStatus(playerId, status);
        }

        public void PrintActivePlayers()
        {
            Console.WriteLine($"\n[LoginManager] Active Players: {connectedPlayers.Count}");
            foreach (var player in connectedPlayers.Values)
            {
                string queueStatus = player.IsInQueue ? $" (In Queue - Pos: {matchmakingQueue.GetQueuePosition(player.PlayerId)})" : "";
                string gameStatus = player.IsInGame ? " (In Game)" : "";
                Console.WriteLine($"  - {player.Username} (ID: {player.PlayerId}, MMR: {player.MMR}, Rank: {player.Rank}){queueStatus}{gameStatus}");
            }
            Console.WriteLine($"\n[Matchmaking] Queue Size: {matchmakingQueue.QueueSize}/{MatchmakingQueue.PLAYERS_PER_GAME}");
        }

        private void ApplyDailyLogin(Player player)
        {
            if (player == null) return;

            DateTime now = DateTime.UtcNow;
            DateTime last = player.LastLogin;

            // Already logged in today: no new reward, just report the state so the
            // client can reflect that today's reward is unavailable.
            if (last != DateTime.MinValue && last.Date == now.Date)
            {
                player.DailyRewardClaimedToday = true;
                player.DailyRewardGoldGranted = 0;
                player.DailyRewardXpGranted = 0;
                Console.WriteLine($"[LoginManager] Player '{player.Username}' already claimed today's login reward.");
                return;
            }

            // The streak continues if the previous login was yesterday, otherwise it resets.
            player.LoginStreak = (last != DateTime.MinValue && last.Date == now.Date.AddDays(-1))
                ? player.LoginStreak + 1
                : 1;

            // Reward scales with the streak up to a modest cap.
            int bonus = Math.Min(player.LoginStreak - 1, 6);
            int gold = 100 + bonus * 25; // day1 = 100 gold, day7+ = 250 gold
            int xp = 50 + bonus * 15;    // day1 = 50 XP,   day7+ = 140 XP

            player.AddGold(gold);
            player.AddExperience(xp); // AddExperience handles level-ups

            player.DailyRewardClaimedToday = true;
            player.DailyRewardGoldGranted = gold;
            player.DailyRewardXpGranted = xp;

            Console.WriteLine($"[LoginManager] Daily login reward for '{player.Username}': streak={player.LoginStreak}, +{gold} gold, +{xp} XP");
        }

        #endregion
    }
}
