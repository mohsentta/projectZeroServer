using ServerFpsProjectZero.Networking;
using ServerFpsProjectZero.Server;
using System;

namespace ServerFpsProjectZero
{
    class Program
    {
        /// <summary>
        /// Server build version. BUMP THIS ON EVERY SERVER CHANGE.
        ///
        /// It is printed in the startup banner so any log immediately shows which build is
        /// running. This exists because a stale server binary silently wasted several rounds:
        /// the client was sending correctly while an older server ignored the packet, and
        /// nothing in the output revealed it.
        /// </summary>
        public const string ServerVersion = "0.3.0";

        /// <summary>Assembly build time, so two builds of the same version are still distinguishable.</summary>
        static string BuildStamp()
        {
            try
            {
                var path = System.Reflection.Assembly.GetExecutingAssembly().Location;
                return System.IO.File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch
            {
                return "unknown";
            }
        }

        static void Main(string[] args)
        {
            Console.Title = "FPS Game Server";
            Console.WriteLine("========================================");
            Console.WriteLine($"  FPS Game Server  v{ServerVersion}");
            Console.WriteLine($"  build {BuildStamp()}");
            Console.WriteLine("========================================\n");

            // Database connection string
            string connectionString = "Data Source=FPSGame.db;Mode=ReadWriteCreate;";

            // Create server manager (single UDP port)
            int serverPort = 7777;
            ServerManager serverManager = new ServerManager(serverPort);

            // Create managers
            GameManager gameManager = new GameManager(serverManager);
            // Shoot diagnostics default to ON. "--no-shootdebug" turns them off; the
            // "shootdebug" console command toggles them at runtime.
            if (Array.Exists(args, a => string.Equals(a, "--no-shootdebug", StringComparison.OrdinalIgnoreCase)))
                gameManager.ShootDebug = false;

            // Packet trace is OFF by default (movement arrives at 20Hz and floods the console).
            //   --packettrace      log shoot packets
            //   --packettrace-all  log every packet type, movement included
            if (Array.Exists(args, a => string.Equals(a, "--packettrace", StringComparison.OrdinalIgnoreCase)))
                serverManager.TracePackets = true;

            if (Array.Exists(args, a => string.Equals(a, "--packettrace-all", StringComparison.OrdinalIgnoreCase)))
            {
                serverManager.TracePackets = true;
                serverManager.TraceAllPackets = true;
            }
            LoginManager loginManager = new LoginManager(serverManager, gameManager, connectionString);
            FriendsManager friendsManager = new FriendsManager(connectionString, serverManager, loginManager);
            LobbyManager lobbyManager = new LobbyManager(serverManager, gameManager);
            lobbyManager.SetLoginManager(loginManager);
            lobbyManager.SetFriendsManager(friendsManager);
            gameManager.SetLoginManager(loginManager);

            // Wire up friends-related packet handlers
            serverManager.OnGetFriendsPacket += (json, endpoint) =>
            {
                try
                {
                    var request = Newtonsoft.Json.JsonConvert.DeserializeObject<Shared.GetFriendsRequest>(json);
                    var response = friendsManager.GetFriends(request.token);
                    serverManager.SendPacket(response, endpoint);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Friends] Error processing get_friends: {ex.Message}");
                }
            };

            serverManager.OnSendFriendRequestPacket += (json, endpoint) =>
            {
                try
                {
                    var request = Newtonsoft.Json.JsonConvert.DeserializeObject<Shared.SendFriendRequest>(json);
                    var response = friendsManager.SendFriendRequest(request.token, request.targetPlayerId, request.targetUsername);
                    serverManager.SendPacket(response, endpoint);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Friends] Error processing send_friend_request: {ex.Message}");
                }
            };

            serverManager.OnRespondFriendRequestPacket += (json, endpoint) =>
            {
                try
                {
                    var request = Newtonsoft.Json.JsonConvert.DeserializeObject<Shared.RespondToFriendRequest>(json);
                    var response = friendsManager.RespondToFriendRequest(request.token, request.requestId, request.accept);
                    serverManager.SendPacket(response, endpoint);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Friends] Error processing respond_friend_request: {ex.Message}");
                }
            };

            serverManager.OnRemoveFriendPacket += (json, endpoint) =>
            {
                try
                {
                    var request = Newtonsoft.Json.JsonConvert.DeserializeObject<Shared.RemoveFriendRequest>(json);
                    var response = friendsManager.RemoveFriend(request.token, request.friendId);
                    serverManager.SendPacket(response, endpoint);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Friends] Error processing remove_friend: {ex.Message}");
                }
            };

            // Wire up lobby packet handlers
            serverManager.OnCreateLobbyPacket += (json, endpoint) => lobbyManager.HandleCreateLobby(json, endpoint);
            serverManager.OnJoinLobbyPacket += (json, endpoint) => lobbyManager.HandleJoinLobby(json, endpoint);
            serverManager.OnLeaveLobbyPacket += (json, endpoint) => lobbyManager.HandleLeaveLobby(json, endpoint);
            serverManager.OnLobbyReadyPacket += (json, endpoint) => lobbyManager.HandleLobbyReady(json, endpoint);
            serverManager.OnLobbyStartPacket += (json, endpoint) => lobbyManager.HandleLobbyStart(json, endpoint);
            serverManager.OnLobbySyncPacket += (json, endpoint) => lobbyManager.HandleLobbySync(json, endpoint);
            serverManager.OnLobbyInvitePacket += (json, endpoint) => lobbyManager.HandleLobbyInvite(json, endpoint);
            serverManager.OnLobbyInviteRespondPacket += (json, endpoint) => lobbyManager.HandleLobbyInviteRespond(json, endpoint);

            // Remove a disconnected player from any lobby they were in.
            serverManager.OnClientDisconnected += client =>
            {
                try
                {
                    lobbyManager.OnPlayerDisconnected(client.PlayerId);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Lobby] Disconnect cleanup error: {ex.Message}");
                }
            };

            // Connect LoginManager with FriendsManager for status updates
            loginManager.SetFriendsManager(friendsManager);

            // Start server
            serverManager.Start();
            Console.WriteLine($"Server listening on UDP port {serverPort}\n");

            // Start managers
            loginManager.Start();
            gameManager.Start();

            Console.WriteLine("\nServer is running. Commands:");
            Console.WriteLine("  'players' - Show active players");
            Console.WriteLine("  'clients' - Show connected clients");
            Console.WriteLine("  'games' - Show active games");
            Console.WriteLine("  'friends' - Show friends statistics");
            Console.WriteLine("  'restart' - Wipe server data (drop all tables) and reinitialize");
            Console.WriteLine("  'help' - Show this menu");
            Console.WriteLine("  'quit' - Shutdown server\n");

            // Command loop
            string command;
            do
            {
                command = Console.ReadLine()?.ToLower();

                switch (command)
                {
                    case "players":
                        loginManager.PrintActivePlayers();
                        break;
                    case "clients":
                        serverManager.PrintActiveClients();
                        break;
                    case "games":
                        gameManager.PrintActiveGames();
                        break;
                    case "friends":
                        PrintFriendsStats(friendsManager);
                        break;
                    case "shootdebug":
                        gameManager.ShootDebug = !gameManager.ShootDebug;
                        Console.WriteLine($"[GameManager] Shoot debug {(gameManager.ShootDebug ? "ON" : "OFF")}");
                        break;
                    case "restart":
                    case "wipe":
                        loginManager.ResetDatabase();
                        break;
                    case "help":
                        PrintHelp();
                        break;
                    case "quit":
                    case "exit":
                        Console.WriteLine("\nShutting down server...");
                        break;
                    default:
                        if (!string.IsNullOrEmpty(command) && command != "quit" && command != "exit")
                        {
                            Console.WriteLine($"Unknown command: {command}");
                        }
                        break;
                }

            } while (command != "quit" && command != "exit");

            // Cleanup
            gameManager.Stop();
            loginManager.Stop();
            serverManager.Stop();

            Console.WriteLine("Server shut down successfully.");
        }

        static void PrintHelp()
        {
            Console.WriteLine("\nAvailable Commands:");
            Console.WriteLine("  'players' - Show active players and their details");
            Console.WriteLine("  'clients' - Show connected client connections");
            Console.WriteLine("  'games' - Show active game sessions");
            Console.WriteLine("  'friends' - Show friends system statistics");
            Console.WriteLine("  'restart' - Wipe server data (drop all tables) and reinitialize");
            Console.WriteLine("  'help' - Show this help menu");
            Console.WriteLine("  'quit' - Shutdown the server\n");
        }

        static void PrintFriendsStats(FriendsManager friendsManager)
        {
            Console.WriteLine("\n[Friends System Statistics]");
            Console.WriteLine("Friends system is active and handling:");
            Console.WriteLine("  - Friend requests");
            Console.WriteLine("  - Friends list management");
            Console.WriteLine("  - Online status updates");
            Console.WriteLine("  - Real-time friend notifications");

            // You can add more detailed statistics here if you add methods to FriendsManager
            // For example: total friendships, pending requests count, etc.
            Console.WriteLine();
        }
    }
}