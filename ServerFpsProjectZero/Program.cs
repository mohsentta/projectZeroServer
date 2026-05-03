using ServerFpsProjectZero.Networking;
using ServerFpsProjectZero.Networking;
using ServerFpsProjectZero.Server; // Add this
using System;
using System;
using System.Threading;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks;

namespace ServerFpsProjectZero
{
    class Program
    {
        private static LoginServer loginServer;
        private static GameServer gameServer;
        private static bool isRunning = true;

        static async Task Main(string[] args)
        {
            Console.WriteLine("=========================================");
            Console.WriteLine("   FPS Game Server - Starting...");
            Console.WriteLine("=========================================\n");

            // Start Login/Auth Server (UDP for authentication)
            loginServer = new LoginServer(7777);

            // Start Game Server (for matchmaking and game logic)
            gameServer = new GameServer();
            gameServer.Start(7778); // Use different port for game logic

            // Subscribe to events
            loginServer.OnPlayerLoggedIn += (player) =>
            {
                Console.WriteLine($"\n[EVENT] ✓ {player.Username} logged in! (MMR: {player.MMR}, Rank: {player.Rank})");
                loginServer.PrintActivePlayers();
            };

            loginServer.OnPlayerDisconnected += (player) =>
            {
                Console.WriteLine($"\n[EVENT] ✗ {player.Username} disconnected");
                loginServer.PrintActivePlayers();
            };

            loginServer.OnFailedLogin += (reason, endpoint) =>
            {
                Console.WriteLine($"\n[EVENT] ✗ Failed login: {reason} from {endpoint.Address}");
            };

            // Start servers
            await loginServer.Start();
            Console.WriteLine($"✓ Authentication server started on port 7777");
            Console.WriteLine($"✓ Game server started on port 7778");

            Console.WriteLine("\n=========================================");
            Console.WriteLine("   Server is Running!");
            Console.WriteLine("=========================================");
            Console.WriteLine("\nTest Accounts:");
            Console.WriteLine("  ┌─────────────────────────────────────┐");
            Console.WriteLine("  │ Username    │ Password    │ MMR     │");
            Console.WriteLine("  ├─────────────────────────────────────┤");
            Console.WriteLine("  │ player1     │ password123 │ 1200    │");
            Console.WriteLine("  │ player2     │ password123 │ 1350    │");
            Console.WriteLine("  │ player3     │ password123 │ 1100    │");
            Console.WriteLine("  │ proplayer   │ pro123      │ 1850    │");
            Console.WriteLine("  │ newbie      │ test123     │ 1000    │");
            Console.WriteLine("  │ veteran     │ vet123      │ 1500    │");
            Console.WriteLine("  │ casual      │ casual123   │ 1150    │");
            Console.WriteLine("  │ tryhard     │ try123      │ 1700    │");
            Console.WriteLine("  └─────────────────────────────────────┘");

            Console.WriteLine("\n=========================================");
            Console.WriteLine("Commands:");
            Console.WriteLine("  • Press 'A' - Show active players");
            Console.WriteLine("  • Press 'Q' - Show queue status");
            Console.WriteLine("  • Press 'G' - Show active games");
            Console.WriteLine("  • Press 'C' - Clear console");
            Console.WriteLine("  • Press 'Ctrl+C' - Stop server");
            Console.WriteLine("=========================================\n");

            // Handle console input
            while (isRunning)
            {
                var key = Console.ReadKey(true);
                switch (key.Key)
                {
                    case ConsoleKey.A:
                        loginServer.PrintActivePlayers();
                        break;
                    case ConsoleKey.Q:
                        Console.WriteLine($"\n[Queue Status] Players in queue: {loginServer.GetQueueSize()}");
                        break;
                    case ConsoleKey.C:
                        Console.Clear();
                        Console.WriteLine("Console cleared!");
                        break;
                }
            }

            // Graceful shutdown
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                isRunning = false;
                Console.WriteLine("\n\n[SHUTDOWN] Stopping servers...");
                loginServer.Stop();
                gameServer.Stop();
                Console.WriteLine("[SHUTDOWN] Servers stopped. Goodbye!");
            };
        }
    }
}

// Extension method for LoginServer to expose queue size
public static class LoginServerExtensions
{
    public static int GetQueueSize(this LoginServer server)
    {
        // This would need to be implemented in LoginServer
        // For now, return 0
        return 0;
    }
}