using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using ServerFpsProjectZero.Models;
using ServerFpsProjectZero.Networking;
using ServerFpsProjectZero.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace ServerFpsProjectZero.Server
{
    public class FriendsManager
    {
        private readonly string connectionString;
        private readonly ServerManager serverManager;
        private readonly LoginManager loginManager;
        private bool makeTestFriends = true;

        // Cache for online friend status
        private ConcurrentDictionary<int, PlayerStatus> onlineStatusCache;

        public FriendsManager(string connectionString, ServerManager serverManager, LoginManager loginManager)
        {
            this.connectionString = connectionString;
            this.serverManager = serverManager;
            this.loginManager = loginManager;
            this.onlineStatusCache = new ConcurrentDictionary<int, PlayerStatus>();

            InitializeDatabase();
            if (makeTestFriends)
                InitializeTestFriends();
        }

        private void InitializeDatabase()
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();

                // Drop existing tables so schema is always fresh
                using (var drop = connection.CreateCommand())
                {
                    drop.CommandText = "DROP TABLE IF EXISTS FriendRequests";
                    drop.ExecuteNonQuery();
                    drop.CommandText = "DROP TABLE IF EXISTS Friends";
                    drop.ExecuteNonQuery();
                }

                // Friends table
                string createFriendsTable = @"
                    CREATE TABLE IF NOT EXISTS Friends (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        PlayerId INTEGER NOT NULL,
                        FriendId INTEGER NOT NULL,
                        FriendsSince DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        FOREIGN KEY (PlayerId) REFERENCES Players(PlayerId),
                        FOREIGN KEY (FriendId) REFERENCES Players(PlayerId),
                        UNIQUE(PlayerId, FriendId)
                    )";

                // Friend Requests table
                string createFriendRequestsTable = @"
                    CREATE TABLE IF NOT EXISTS FriendRequests (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        FromPlayerId INTEGER NOT NULL,
                        ToPlayerId INTEGER NOT NULL,
                        Status TEXT NOT NULL DEFAULT 'Pending',
                        SentAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        RespondedAt DATETIME,
                        FOREIGN KEY (FromPlayerId) REFERENCES Players(PlayerId),
                        FOREIGN KEY (ToPlayerId) REFERENCES Players(PlayerId)
                    )";

                using (var command = new SqliteCommand(createFriendsTable, connection))
                {
                    command.ExecuteNonQuery();
                }

                using (var command = new SqliteCommand(createFriendRequestsTable, connection))
                {
                    command.ExecuteNonQuery();
                }

                // Create indexes for better performance
                string[] indexes = new string[]
                {
                    "CREATE INDEX IF NOT EXISTS idx_friends_playerid ON Friends(PlayerId)",
                    "CREATE INDEX IF NOT EXISTS idx_friends_friendid ON Friends(FriendId)",
                    "CREATE INDEX IF NOT EXISTS idx_friendrequests_from ON FriendRequests(FromPlayerId)",
                    "CREATE INDEX IF NOT EXISTS idx_friendrequests_to ON FriendRequests(ToPlayerId)",
                    "CREATE INDEX IF NOT EXISTS idx_friendrequests_status ON FriendRequests(Status)"
                };

                foreach (string index in indexes)
                {
                    using (var command = new SqliteCommand(index, connection))
                    {
                        command.ExecuteNonQuery();
                    }
                }
            }

            Console.WriteLine("[FriendsManager] Database initialized");
        }

        #region Test Friends Initialization

        public void InitializeTestFriends()
        {
            // Check if friends table already has data
            if (GetFriendsCount() > 0) return;

            // Get player IDs by username (assuming players were created in LoginManager)
            var playerIds = GetPlayerIdsByUsernames();

            if (playerIds.Count < 10)
            {
                Console.WriteLine("[FriendsManager] Not enough test players found, skipping friend initialization");
                return;
            }

            // Create friendships with some sample data
            // Player1 is friends with Player2, Player3, and ProPlayer
            AddTestFriendship(playerIds["player1"], playerIds["player2"]);
            AddTestFriendship(playerIds["player1"], playerIds["player3"]);
            AddTestFriendship(playerIds["player1"], playerIds["proplayer"]);

            // Player2 is friends with Player1, ProPlayer, and Veteran
            AddTestFriendship(playerIds["player2"], playerIds["proplayer"]);
            AddTestFriendship(playerIds["player2"], playerIds["veteran"]);

            // Player3 is friends with Player1, Newbie, and Casual
            AddTestFriendship(playerIds["player3"], playerIds["newbie"]);
            AddTestFriendship(playerIds["player3"], playerIds["casual"]);

            // ProPlayer is friends with Player1, Player2, Veteran, and Tryhard
            AddTestFriendship(playerIds["proplayer"], playerIds["veteran"]);
            AddTestFriendship(playerIds["proplayer"], playerIds["tryhard"]);

            // Veteran is friends with Player2, ProPlayer, and Sniper
            AddTestFriendship(playerIds["veteran"], playerIds["sniper"]);

            // Tryhard is friends with ProPlayer and Sniper
            AddTestFriendship(playerIds["tryhard"], playerIds["sniper"]);

            // Sniper is friends with Veteran, Tryhard, and Shotgun
            AddTestFriendship(playerIds["sniper"], playerIds["shotgun"]);

            // Shotgun is friends with Sniper
            // (already created above)

            // Casual is friends with Player3 and Newbie
            AddTestFriendship(playerIds["casual"], playerIds["newbie"]);

            // Newbie is friends with Player3 and Casual
            // (already created above)

            // Create some pending friend requests
            AddTestFriendRequest(playerIds["newbie"], playerIds["proplayer"], "Pending");
            AddTestFriendRequest(playerIds["casual"], playerIds["tryhard"], "Pending");
            AddTestFriendRequest(playerIds["shotgun"], playerIds["player1"], "Pending");
            AddTestFriendRequest(playerIds["tryhard"], playerIds["player2"], "Pending");
            AddTestFriendRequest(playerIds["veteran"], playerIds["casual"], "Pending");

            // Create some old/resolved friend requests for history
            AddTestFriendRequest(playerIds["player1"], playerIds["veteran"], "Accepted", DateTime.UtcNow.AddDays(-30));
            AddTestFriendRequest(playerIds["player2"], playerIds["casual"], "Declined", DateTime.UtcNow.AddDays(-15));

            Console.WriteLine("[FriendsManager] Test friends and requests initialized successfully");
        }

        private int GetFriendsCount()
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM Friends";
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private Dictionary<string, int> GetPlayerIdsByUsernames()
        {
            var playerIds = new Dictionary<string, int>();

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();

                string query = "SELECT PlayerId, Username FROM Players";
                using (var command = new SqliteCommand(query, connection))
                {
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string username = reader["Username"].ToString();
                            int playerId = Convert.ToInt32(reader["PlayerId"]);
                            playerIds[username] = playerId;
                        }
                    }
                }
            }

            return playerIds;
        }

        private void AddTestFriendship(int playerId1, int playerId2)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();

                // Check if friendship already exists
                var checkCommand = connection.CreateCommand();
                checkCommand.CommandText = @"
                    SELECT COUNT(*) FROM Friends 
                    WHERE (PlayerId = @playerId1 AND FriendId = @playerId2) 
                       OR (PlayerId = @playerId2 AND FriendId = @playerId1)";
                checkCommand.Parameters.AddWithValue("@playerId1", playerId1);
                checkCommand.Parameters.AddWithValue("@playerId2", playerId2);

                int count = Convert.ToInt32(checkCommand.ExecuteScalar());
                if (count > 0) return;

                // Random friends since date (between 1 and 60 days ago)
                var random = new Random();
                DateTime friendsSince = DateTime.UtcNow.AddDays(-random.Next(1, 60));

                // Add friendship in both directions
                var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO Friends (PlayerId, FriendId, FriendsSince)
                    VALUES (@playerId, @friendId, @friendsSince)";

                // First direction
                command.Parameters.AddWithValue("@playerId", playerId1);
                command.Parameters.AddWithValue("@friendId", playerId2);
                command.Parameters.AddWithValue("@friendsSince", friendsSince);
                command.ExecuteNonQuery();

                // Second direction
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@playerId", playerId2);
                command.Parameters.AddWithValue("@friendId", playerId1);
                command.Parameters.AddWithValue("@friendsSince", friendsSince);
                command.ExecuteNonQuery();
            }
        }

        private void AddTestFriendRequest(int fromPlayerId, int toPlayerId, string status, DateTime? sentAt = null)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();

                // Check if request already exists
                var checkCommand = connection.CreateCommand();
                checkCommand.CommandText = @"
                    SELECT COUNT(*) FROM FriendRequests 
                    WHERE FromPlayerId = @fromPlayerId AND ToPlayerId = @toPlayerId AND Status = @status";
                checkCommand.Parameters.AddWithValue("@fromPlayerId", fromPlayerId);
                checkCommand.Parameters.AddWithValue("@toPlayerId", toPlayerId);
                checkCommand.Parameters.AddWithValue("@status", status);

                int count = Convert.ToInt32(checkCommand.ExecuteScalar());
                if (count > 0) return;

                DateTime requestSentAt = sentAt ?? DateTime.UtcNow.AddDays(-new Random().Next(1, 7));
                DateTime? respondedAt = null;

                if (status != "Pending")
                {
                    respondedAt = requestSentAt.AddDays(new Random().Next(1, 3));
                }

                var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO FriendRequests (FromPlayerId, ToPlayerId, Status, SentAt, RespondedAt)
                    VALUES (@fromPlayerId, @toPlayerId, @status, @sentAt, @respondedAt)";

                command.Parameters.AddWithValue("@fromPlayerId", fromPlayerId);
                command.Parameters.AddWithValue("@toPlayerId", toPlayerId);
                command.Parameters.AddWithValue("@status", status);
                command.Parameters.AddWithValue("@sentAt", requestSentAt);
                command.Parameters.AddWithValue("@respondedAt", (object)respondedAt ?? DBNull.Value);

                command.ExecuteNonQuery();
            }
        }

        #endregion

        #region Friend Management

        public GetFriendsResponse GetFriends(string token)
        {
            var client = serverManager.GetClientByToken(token);
            if (client == null)
            {
                return new GetFriendsResponse
                {
                    type = "get_friends_response",
                    success = false,
                    message = "Invalid session token"
                };
            }

            try
            {
                var response = new GetFriendsResponse
                {
                    type = "get_friends_response",
                    success = true,
                    friends = new List<FriendData>(),
                    pendingRequests = new List<FriendRequest>()
                };

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();

                    // Get friends list
                    string getFriendsQuery = @"
                        SELECT DISTINCT 
                            CASE 
                                WHEN f.PlayerId = @playerId THEN f.FriendId 
                                ELSE f.PlayerId 
                            END as FriendId,
                            p.Username,
                            f.FriendsSince
                        FROM Friends f
                        JOIN Players p ON p.PlayerId = CASE 
                            WHEN f.PlayerId = @playerId THEN f.FriendId 
                            ELSE f.PlayerId 
                        END
                        WHERE f.PlayerId = @playerId OR f.FriendId = @playerId";

                    using (var command = new SqliteCommand(getFriendsQuery, connection))
                    {
                        command.Parameters.AddWithValue("@playerId", client.PlayerId);

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                int friendId = Convert.ToInt32(reader["FriendId"]);
                                var friend = new FriendData
                                {
                                    FriendId = friendId,
                                    Username = reader["Username"].ToString(),
                                    Status = GetPlayerStatus(friendId),
                                    CurrentGameId = GetCurrentGameId(friendId),
                                    FriendsSince = Convert.ToDateTime(reader["FriendsSince"])
                                };

                                response.friends.Add(friend);
                            }
                        }
                    }

                    // Get pending friend requests
                    string getRequestsQuery = @"
                        SELECT fr.Id, fr.FromPlayerId, p.Username as FromUsername, 
                               fr.ToPlayerId, fr.Status, fr.SentAt
                        FROM FriendRequests fr
                        JOIN Players p ON p.PlayerId = fr.FromPlayerId
                        WHERE fr.ToPlayerId = @playerId AND fr.Status = 'Pending'
                        ORDER BY fr.SentAt DESC";

                    using (var command = new SqliteCommand(getRequestsQuery, connection))
                    {
                        command.Parameters.AddWithValue("@playerId", client.PlayerId);

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var request = new FriendRequest
                                {
                                    RequestId = Convert.ToInt32(reader["Id"]),
                                    FromPlayerId = Convert.ToInt32(reader["FromPlayerId"]),
                                    FromUsername = reader["FromUsername"].ToString(),
                                    ToPlayerId = Convert.ToInt32(reader["ToPlayerId"]),
                                    Status = (FriendRequestStatus)Enum.Parse(typeof(FriendRequestStatus), reader["Status"].ToString()),
                                    SentAt = Convert.ToDateTime(reader["SentAt"])
                                };

                                response.pendingRequests.Add(request);
                            }
                        }
                    }
                }

                return response;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FriendsManager] Error getting friends: {ex.Message}");
                return new GetFriendsResponse
                {
                    type = "get_friends_response",
                    success = false,
                    message = "Failed to retrieve friends list"
                };
            }
        }

        public FriendResponse SendFriendRequest(string token, int targetPlayerId, string targetUsername)
        {
            var client = serverManager.GetClientByToken(token);
            if (client == null)
            {
                return new FriendResponse
                {
                    type = "friend_response",
                    success = false,
                    message = "Invalid session token"
                };
            }

            try
            {
                // Validate target player exists
                var targetPlayer = loginManager.GetPlayerById(targetPlayerId);
                if (targetPlayer == null)
                {
                    return new FriendResponse
                    {
                        type = "friend_response",
                        success = false,
                        message = "Player not found"
                    };
                }

                // Can't send friend request to yourself
                if (client.PlayerId == targetPlayerId)
                {
                    return new FriendResponse
                    {
                        type = "friend_response",
                        success = false,
                        message = "Cannot send friend request to yourself"
                    };
                }

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();

                    // Check if already friends
                    string checkFriendsQuery = @"
                        SELECT COUNT(*) FROM Friends 
                        WHERE (PlayerId = @playerId AND FriendId = @targetId) 
                           OR (PlayerId = @targetId AND FriendId = @playerId)";

                    using (var command = new SqliteCommand(checkFriendsQuery, connection))
                    {
                        command.Parameters.AddWithValue("@playerId", client.PlayerId);
                        command.Parameters.AddWithValue("@targetId", targetPlayerId);
                        int friendCount = Convert.ToInt32(command.ExecuteScalar());

                        if (friendCount > 0)
                        {
                            return new FriendResponse
                            {
                                type = "friend_response",
                                success = false,
                                message = "Already friends with this player"
                            };
                        }
                    }

                    // Check if pending request already exists
                    string checkRequestQuery = @"
                        SELECT COUNT(*) FROM FriendRequests 
                        WHERE ((FromPlayerId = @playerId AND ToPlayerId = @targetId) 
                            OR (FromPlayerId = @targetId AND ToPlayerId = @playerId))
                        AND Status = 'Pending'";

                    using (var command = new SqliteCommand(checkRequestQuery, connection))
                    {
                        command.Parameters.AddWithValue("@playerId", client.PlayerId);
                        command.Parameters.AddWithValue("@targetId", targetPlayerId);
                        int requestCount = Convert.ToInt32(command.ExecuteScalar());

                        if (requestCount > 0)
                        {
                            return new FriendResponse
                            {
                                type = "friend_response",
                                success = false,
                                message = "Friend request already pending"
                            };
                        }
                    }

                    // Send friend request
                    string insertRequestQuery = @"
                        INSERT INTO FriendRequests (FromPlayerId, ToPlayerId, Status, SentAt)
                        VALUES (@fromPlayerId, @toPlayerId, 'Pending', @sentAt);
                        SELECT last_insert_rowid();";

                    int requestId;
                    DateTime sentAt = DateTime.UtcNow;

                    using (var command = new SqliteCommand(insertRequestQuery, connection))
                    {
                        command.Parameters.AddWithValue("@fromPlayerId", client.PlayerId);
                        command.Parameters.AddWithValue("@toPlayerId", targetPlayerId);
                        command.Parameters.AddWithValue("@sentAt", sentAt);
                        requestId = Convert.ToInt32(command.ExecuteScalar());
                    }

                    // Notify target player if online
                    var targetClient = serverManager.GetClientById(targetPlayerId);
                    if (targetClient != null && targetClient.IsConnected)
                    {
                        var notification = new FriendRequestNotification
                        {
                            type = "friend_request_notification",
                            request = new FriendRequest
                            {
                                RequestId = requestId,
                                FromPlayerId = client.PlayerId,
                                FromUsername = client.Username,
                                ToPlayerId = targetPlayerId,
                                SentAt = sentAt,
                                Status = FriendRequestStatus.Pending
                            },
                            timestamp = DateTime.UtcNow
                        };

                        serverManager.SendPacket(notification, targetClient);
                    }

                    Console.WriteLine($"[FriendsManager] Friend request sent from {client.Username} to {targetUsername}");

                    return new FriendResponse
                    {
                        type = "friend_response",
                        success = true,
                        message = $"Friend request sent to {targetUsername}",
                        friend = new FriendData
                        {
                            FriendId = targetPlayerId,
                            Username = targetUsername,
                            Status = GetPlayerStatus(targetPlayerId),
                            CurrentGameId = GetCurrentGameId(targetPlayerId)
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FriendsManager] Error sending friend request: {ex.Message}");
                return new FriendResponse
                {
                    type = "friend_response",
                    success = false,
                    message = "Failed to send friend request"
                };
            }
        }

        public FriendResponse RespondToFriendRequest(string token, int requestId, bool accept)
        {
            var client = serverManager.GetClientByToken(token);
            if (client == null)
            {
                return new FriendResponse
                {
                    type = "friend_response",
                    success = false,
                    message = "Invalid session token"
                };
            }

            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();

                    // Get the friend request
                    string getRequestQuery = @"
                        SELECT FromPlayerId, ToPlayerId, Status 
                        FROM FriendRequests 
                        WHERE Id = @requestId AND ToPlayerId = @playerId";

                    int fromPlayerId = 0;
                    string currentStatus = "";

                    using (var command = new SqliteCommand(getRequestQuery, connection))
                    {
                        command.Parameters.AddWithValue("@requestId", requestId);
                        command.Parameters.AddWithValue("@playerId", client.PlayerId);

                        using (var reader = command.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                fromPlayerId = Convert.ToInt32(reader["FromPlayerId"]);
                                currentStatus = reader["Status"].ToString();
                            }
                            else
                            {
                                return new FriendResponse
                                {
                                    type = "friend_response",
                                    success = false,
                                    message = "Friend request not found"
                                };
                            }
                        }
                    }

                    if (currentStatus != "Pending")
                    {
                        return new FriendResponse
                        {
                            type = "friend_response",
                            success = false,
                            message = "This request has already been handled"
                        };
                    }

                    string newStatus = accept ? "Accepted" : "Declined";
                    DateTime respondedAt = DateTime.UtcNow;

                    // Update request status
                    string updateRequestQuery = @"
                        UPDATE FriendRequests 
                        SET Status = @status, RespondedAt = @respondedAt
                        WHERE Id = @requestId";

                    using (var command = new SqliteCommand(updateRequestQuery, connection))
                    {
                        command.Parameters.AddWithValue("@status", newStatus);
                        command.Parameters.AddWithValue("@respondedAt", respondedAt);
                        command.Parameters.AddWithValue("@requestId", requestId);
                        command.ExecuteNonQuery();
                    }

                    // If accepted, add to friends table
                    if (accept)
                    {
                        string addFriendQuery = @"
                            INSERT INTO Friends (PlayerId, FriendId, FriendsSince)
                            VALUES (@playerId, @friendId, @friendsSince)";

                        using (var command = new SqliteCommand(addFriendQuery, connection))
                        {
                            command.Parameters.AddWithValue("@playerId", client.PlayerId);
                            command.Parameters.AddWithValue("@friendId", fromPlayerId);
                            command.Parameters.AddWithValue("@friendsSince", respondedAt);
                            command.ExecuteNonQuery();
                        }

                        // Also add reverse relationship
                        using (var command = new SqliteCommand(addFriendQuery, connection))
                        {
                            command.Parameters.AddWithValue("@playerId", fromPlayerId);
                            command.Parameters.AddWithValue("@friendId", client.PlayerId);
                            command.Parameters.AddWithValue("@friendsSince", respondedAt);
                            command.ExecuteNonQuery();
                        }

                        // Notify the sender if online
                        var senderClient = serverManager.GetClientById(fromPlayerId);
                        if (senderClient != null && senderClient.IsConnected)
                        {
                            var notification = new FriendStatusUpdate
                            {
                                type = "friend_request_accepted",
                                playerId = client.PlayerId,
                                username = client.Username,
                                status = GetPlayerStatus(client.PlayerId),
                                currentGameId = GetCurrentGameId(client.PlayerId),
                                timestamp = DateTime.UtcNow
                            };

                            serverManager.SendPacket(notification, senderClient);
                        }

                        Console.WriteLine($"[FriendsManager] Friend request accepted: {fromPlayerId} and {client.PlayerId}");
                    }

                    // Get the username of the player who sent the request
                    string fromUsername = loginManager.GetPlayerById(fromPlayerId)?.Username ?? "Unknown";

                    return new FriendResponse
                    {
                        type = "friend_response",
                        success = true,
                        message = accept ? $"You are now friends with {fromUsername}" : "Friend request declined",
                        friend = accept ? new FriendData
                        {
                            FriendId = fromPlayerId,
                            Username = fromUsername,
                            Status = GetPlayerStatus(fromPlayerId),
                            CurrentGameId = GetCurrentGameId(fromPlayerId),
                            FriendsSince = respondedAt
                        } : null
                    };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FriendsManager] Error responding to friend request: {ex.Message}");
                return new FriendResponse
                {
                    type = "friend_response",
                    success = false,
                    message = "Failed to process friend request response"
                };
            }
        }

        public FriendResponse RemoveFriend(string token, int friendId)
        {
            var client = serverManager.GetClientByToken(token);
            if (client == null)
            {
                return new FriendResponse
                {
                    type = "friend_response",
                    success = false,
                    message = "Invalid session token"
                };
            }

            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();

                    // Remove friendship
                    string removeFriendQuery = @"
                        DELETE FROM Friends 
                        WHERE (PlayerId = @playerId AND FriendId = @friendId) 
                           OR (PlayerId = @friendId AND FriendId = @playerId)";

                    using (var command = new SqliteCommand(removeFriendQuery, connection))
                    {
                        command.Parameters.AddWithValue("@playerId", client.PlayerId);
                        command.Parameters.AddWithValue("@friendId", friendId);
                        int rowsAffected = command.ExecuteNonQuery();

                        if (rowsAffected == 0)
                        {
                            return new FriendResponse
                            {
                                type = "friend_response",
                                success = false,
                                message = "Friend not found in your friends list"
                            };
                        }
                    }

                    // Notify the removed friend if online
                    var friendClient = serverManager.GetClientById(friendId);
                    if (friendClient != null && friendClient.IsConnected)
                    {
                        var notification = new FriendStatusUpdate
                        {
                            type = "friend_removed",
                            playerId = client.PlayerId,
                            username = client.Username,
                            status = PlayerStatus.Offline,
                            currentGameId = -1,
                            timestamp = DateTime.UtcNow
                        };

                        serverManager.SendPacket(notification, friendClient);
                    }

                    Console.WriteLine($"[FriendsManager] Friendship removed: {client.PlayerId} and {friendId}");

                    return new FriendResponse
                    {
                        type = "friend_response",
                        success = true,
                        message = "Friend removed successfully"
                    };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FriendsManager] Error removing friend: {ex.Message}");
                return new FriendResponse
                {
                    type = "friend_response",
                    success = false,
                    message = "Failed to remove friend"
                };
            }
        }

        #endregion

        #region Status Management

        public void UpdatePlayerStatus(int playerId, PlayerStatus status)
        {
            onlineStatusCache[playerId] = status;

            // Notify all online friends about status change
            NotifyFriendsOfStatusChange(playerId, status);
        }

        public PlayerStatus GetPlayerStatus(int playerId)
        {
            if (onlineStatusCache.TryGetValue(playerId, out PlayerStatus status))
            {
                return status;
            }

            // Check if player is online
            var client = serverManager.GetClientById(playerId);
            if (client != null && client.IsConnected)
            {
                if (client.InGame)
                {
                    return PlayerStatus.InGame;
                }
                else if (client.InQueue)
                {
                    return PlayerStatus.InQueue;
                }
                else
                {
                    return PlayerStatus.Online;
                }
            }

            return PlayerStatus.Offline;
        }

        public int GetCurrentGameId(int playerId)
        {
            var client = serverManager.GetClientById(playerId);
            return client?.CurrentGameId ?? -1;
        }

        private void NotifyFriendsOfStatusChange(int playerId, PlayerStatus status)
        {
            try
            {
                var playerClient = serverManager.GetClientById(playerId);
                if (playerClient == null) return;

                List<int> friendIds = new List<int>();

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();

                    string query = @"
                        SELECT CASE 
                            WHEN PlayerId = @playerId THEN FriendId 
                            ELSE PlayerId 
                        END as FriendId
                        FROM Friends 
                        WHERE PlayerId = @playerId OR FriendId = @playerId";

                    using (var command = new SqliteCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@playerId", playerId);
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                friendIds.Add(Convert.ToInt32(reader["FriendId"]));
                            }
                        }
                    }
                }

                // Send status update to all online friends
                var update = new FriendStatusUpdate
                {
                    type = "friend_status_update",
                    playerId = playerId,
                    username = playerClient.Username,
                    status = status,
                    currentGameId = GetCurrentGameId(playerId),
                    timestamp = DateTime.UtcNow
                };

                foreach (var friendId in friendIds)
                {
                    var friendClient = serverManager.GetClientById(friendId);
                    if (friendClient != null && friendClient.IsConnected)
                    {
                        serverManager.SendPacket(update, friendClient);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FriendsManager] Error notifying friends: {ex.Message}");
            }
        }

        public void OnPlayerDisconnected(int playerId)
        {
            UpdatePlayerStatus(playerId, PlayerStatus.Offline);
        }

        #endregion
    }
}