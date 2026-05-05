using ServerFpsProjectZero.Networking;
using ServerFpsProjectZero.Server;
using System;

namespace ServerFpsProjectZero
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.Title = "FPS Game Server";
            Console.WriteLine("========================================");
            Console.WriteLine("  FPS Game Server Starting...");
            Console.WriteLine("========================================\n");

            // Create server manager (single UDP port)
            int serverPort = 7777;
            ServerManager serverManager = new ServerManager(serverPort);

            // Create managers
            LoginManager loginManager = new LoginManager(serverManager);
            GameManager gameManager = new GameManager(serverManager);

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
            Console.WriteLine("  'help' - Show this help menu");
            Console.WriteLine("  'quit' - Shutdown the server\n");
        }

    }
}