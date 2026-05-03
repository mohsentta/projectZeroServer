using ServerFpsProjectZero.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ServerFpsProjectZero.Matchmaking
{
    public class MatchmakingQueue
    {
        private readonly ConcurrentQueue<QueuedPlayer> queue;
        private readonly Dictionary<int, QueuedPlayer> playerLookup;
        private readonly List<GameSession> activeGames;
        private readonly Timer matchmakingTimer;
        private bool isRunning;

        // Configuration
        public const int PLAYERS_PER_GAME = 2;
        private const int QUEUE_CLEANUP_INTERVAL_MS = 30000; // 30 seconds
        private const int MATCHMAKING_CHECK_INTERVAL_MS = 5000; // 5 seconds
        private const int QUEUE_TIMEOUT_MS = 120000; // 2 minutes
        private const int MMR_TOLERANCE = 150; // MMR range tolerance

        // Events
        public event Action<List<Player>, GameSession> OnGameMatched;
        public event Action<QueuedPlayer> OnPlayerEnteredQueue;
        public event Action<QueuedPlayer> OnPlayerLeftQueue;
        public event Action<QueuedPlayer> OnPlayerTimedOut;
        public event Action<int> OnQueueStatusChanged;

        public int QueueSize => queue.Count;

        public MatchmakingQueue()
        {
            queue = new ConcurrentQueue<QueuedPlayer>();
            playerLookup = new Dictionary<int, QueuedPlayer>();
            activeGames = new List<GameSession>();
            isRunning = true;

            // Start matchmaking timer
            matchmakingTimer = new Timer(ProcessMatchmaking, null, MATCHMAKING_CHECK_INTERVAL_MS, MATCHMAKING_CHECK_INTERVAL_MS);

            // Start cleanup timer
            var cleanupTimer = new Timer(CleanupQueue, null, QUEUE_CLEANUP_INTERVAL_MS, QUEUE_CLEANUP_INTERVAL_MS);

            Console.WriteLine("[Matchmaking] Queue system initialized");
        }

        public bool AddToQueue(Player player)
        {
            lock (playerLookup)
            {
                if (playerLookup.ContainsKey(player.PlayerId))
                {
                    Console.WriteLine($"[Matchmaking] Player {player.Username} already in queue");
                    return false;
                }

                if (player.IsInGame)
                {
                    Console.WriteLine($"[Matchmaking] Player {player.Username} is already in a game");
                    return false;
                }

                var queuedPlayer = new QueuedPlayer
                {
                    Player = player,
                    QueueEnterTime = DateTime.UtcNow,
                    MMR = player.MMR,
                    PreferredTeam = null // Can be set to 0 or 1 for preferred team
                };

                queue.Enqueue(queuedPlayer);
                playerLookup[player.PlayerId] = queuedPlayer;
                player.IsInQueue = true;
                player.QueueStartTime = DateTime.UtcNow;

                Console.WriteLine($"[Matchmaking] ✓ Player {player.Username} (MMR: {player.MMR}) added to queue. Queue size: {queue.Count}");
                OnPlayerEnteredQueue?.Invoke(queuedPlayer);
                OnQueueStatusChanged?.Invoke(queue.Count);

                return true;
            }
        }

        public bool RemoveFromQueue(Player player)
        {
            lock (playerLookup)
            {
                if (!playerLookup.ContainsKey(player.PlayerId))
                {
                    return false;
                }

                // Remove from queue (this is O(n) but acceptable for small queue sizes)
                var tempList = new List<QueuedPlayer>();
                while (queue.TryDequeue(out var queuedPlayer))
                {
                    if (queuedPlayer.Player.PlayerId != player.PlayerId)
                    {
                        tempList.Add(queuedPlayer);
                    }
                    else
                    {
                        OnPlayerLeftQueue?.Invoke(queuedPlayer);
                    }
                }

                // Re-enqueue remaining players
                foreach (var qp in tempList)
                {
                    queue.Enqueue(qp);
                }

                playerLookup.Remove(player.PlayerId);
                player.IsInQueue = false;

                Console.WriteLine($"[Matchmaking] ✗ Player {player.Username} removed from queue. Queue size: {queue.Count}");
                OnQueueStatusChanged?.Invoke(queue.Count);

                return true;
            }
        }

        private void ProcessMatchmaking(object state)
        {
            if (!isRunning) return;

            lock (playerLookup)
            {
                if (queue.Count < PLAYERS_PER_GAME)
                {
                    return;
                }

                // Get all players from queue
                var allPlayers = queue.ToList();

                // Group players by MMR range
                var matchedPlayers = FindBestMatch(allPlayers);

                if (matchedPlayers.Count >= PLAYERS_PER_GAME)
                {
                    // Take exactly PLAYERS_PER_GAME players
                    var gamePlayers = matchedPlayers.Take(PLAYERS_PER_GAME).ToList();

                    // Remove these players from queue
                    foreach (var queuedPlayer in gamePlayers)
                    {
                        RemoveFromQueue(queuedPlayer.Player);
                    }

                    // Create game session
                    CreateGameSession(gamePlayers);
                }
            }
        }

        private List<QueuedPlayer> FindBestMatch(List<QueuedPlayer> players)
        {
            if (players.Count < PLAYERS_PER_GAME)
                return new List<QueuedPlayer>();

            // Sort by MMR
            var sortedPlayers = players.OrderBy(p => p.MMR).ToList();

            // Find the best contiguous group of PLAYERS_PER_GAME players with smallest MMR spread
            List<QueuedPlayer> bestMatch = null;
            int smallestSpread = int.MaxValue;

            for (int i = 0; i <= sortedPlayers.Count - PLAYERS_PER_GAME; i++)
            {
                var group = sortedPlayers.Skip(i).Take(PLAYERS_PER_GAME).ToList();
                int spread = group.Last().MMR - group.First().MMR;

                if (spread < smallestSpread && spread <= MMR_TOLERANCE * 2)
                {
                    smallestSpread = spread;
                    bestMatch = group;

                    // Early exit if we found a perfect match (very tight spread)
                    if (spread <= MMR_TOLERANCE)
                        break;
                }
            }

            // If no good match found within tolerance, take the best available
            if (bestMatch == null && sortedPlayers.Count >= PLAYERS_PER_GAME)
            {
                bestMatch = sortedPlayers.Take(PLAYERS_PER_GAME).ToList();
                Console.WriteLine($"[Matchmaking] Warning: Using match with MMR spread of {(bestMatch.Last().MMR - bestMatch.First().MMR)}");
            }

            return bestMatch ?? new List<QueuedPlayer>();
        }

        private void CreateGameSession(List<QueuedPlayer> players)
        {
            // Assign teams (5 players per team, balanced by MMR)
            var teamRed = new List<Player>();
            var teamBlue = new List<Player>();

            // Sort by MMR for balanced team assignment
            var sortedByMMR = players.OrderByDescending(p => p.MMR).ToList();

            for (int i = 0; i < sortedByMMR.Count; i++)
            {
                if (i % 2 == 0)
                    teamRed.Add(sortedByMMR[i].Player);
                else
                    teamBlue.Add(sortedByMMR[i].Player);
            }

            // Create game session
            var gameSession = new GameSession
            {
                GameId = GenerateGameId(),
                TeamRed = teamRed,
                TeamBlue = teamBlue,
                CreatedAt = DateTime.UtcNow,
                Status = GameStatus.WaitingForPlayers
            };

            // Assign team IDs to players
            foreach (var player in teamRed)
            {
                player.TeamId = 0; // Red team
                player.CurrentGameId = gameSession.GameId;
                player.IsInQueue = false;
                player.IsInGame = true;
            }

            foreach (var player in teamBlue)
            {
                player.TeamId = 1; // Blue team
                player.CurrentGameId = gameSession.GameId;
                player.IsInQueue = false;
                player.IsInGame = true;
            }

            activeGames.Add(gameSession);

            // Calculate average MMR for the game
            float avgMMR = (float)players.Average(p => p.MMR);

            Console.WriteLine($"\n[Matchmaking] ★ MATCH FOUND! ★");
            Console.WriteLine($"  Game ID: {gameSession.GameId}");
            Console.WriteLine($"  Players: {players.Count}/10");
            Console.WriteLine($"  Average MMR: {avgMMR:F0}");
            Console.WriteLine($"  MMR Spread: {players.Max(p => p.MMR) - players.Min(p => p.MMR)}");
            Console.WriteLine($"  Teams: Red={teamRed.Count}, Blue={teamBlue.Count}");
            Console.WriteLine($"  Red Team: {string.Join(", ", teamRed.Select(p => p.Username))}");
            Console.WriteLine($"  Blue Team: {string.Join(", ", teamBlue.Select(p => p.Username))}");

            // Start game session
            gameSession.StartGame();

            // Notify listeners
            OnGameMatched?.Invoke(players.Select(p => p.Player).ToList(), gameSession);
        }

        private void CleanupQueue(object state)
        {
            lock (playerLookup)
            {
                var timedOutPlayers = new List<QueuedPlayer>();

                foreach (var kvp in playerLookup)
                {
                    var queuedPlayer = kvp.Value;
                    var timeInQueue = DateTime.UtcNow - queuedPlayer.QueueEnterTime;

                    if (timeInQueue.TotalMilliseconds > QUEUE_TIMEOUT_MS)
                    {
                        timedOutPlayers.Add(queuedPlayer);
                    }
                }

                foreach (var timedOutPlayer in timedOutPlayers)
                {
                    RemoveFromQueue(timedOutPlayer.Player);
                    timedOutPlayer.Player.IsInQueue = false;
                    OnPlayerTimedOut?.Invoke(timedOutPlayer);
                    Console.WriteLine($"[Matchmaking] ⏰ Player {timedOutPlayer.Player.Username} timed out of queue (waited too long)");
                }
            }
        }

        private int GenerateGameId()
        {
            return Math.Abs(Guid.NewGuid().GetHashCode());
        }

        public List<QueuedPlayer> GetQueueStatus()
        {
            lock (playerLookup)
            {
                return queue.ToList();
            }
        }

        public void Stop()
        {
            isRunning = false;
            matchmakingTimer?.Dispose();

            // Clear queue
            lock (playerLookup)
            {
                queue.Clear();
                playerLookup.Clear();
            }

            Console.WriteLine("[Matchmaking] Queue system stopped");
        }
    }

    public class QueuedPlayer
    {
        public Player Player { get; set; }
        public DateTime QueueEnterTime { get; set; }
        public int MMR { get; set; }
        public int? PreferredTeam { get; set; }

        public int QueueTimeMs => (int)(DateTime.UtcNow - QueueEnterTime).TotalMilliseconds;
    }

    public class GameSession
    {
        public int GameId { get; set; }
        public List<Player> TeamRed { get; set; }
        public List<Player> TeamBlue { get; set; }
        public DateTime CreatedAt { get; set; }
        public GameStatus Status { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }

        // Game stats
        public int RedTeamScore { get; set; }
        public int BlueTeamScore { get; set; }
        public int TimeRemaining { get; set; } = 600; // 10 minutes in seconds

        // Game timer
        private Timer gameTimer;

        public void StartGame()
        {
            Status = GameStatus.InProgress;
            StartTime = DateTime.UtcNow;
            Console.WriteLine($"[GameSession] Game {GameId} started!");

            // Start game timer
            gameTimer = new Timer(UpdateGameTimer, null, 1000, 1000);
        }

        private void UpdateGameTimer(object state)
        {
            if (Status != GameStatus.InProgress)
                return;

            TimeRemaining--;

            if (TimeRemaining <= 0 || RedTeamScore >= 100 || BlueTeamScore >= 100)
            {
                EndGame();
            }
        }

        public void EndGame()
        {
            Status = GameStatus.Finished;
            EndTime = DateTime.UtcNow;
            gameTimer?.Dispose();

            // Determine winner
            bool redWon = RedTeamScore > BlueTeamScore;

            Console.WriteLine($"[GameSession] Game {GameId} ended! Winner: {(redWon ? "RED" : "BLUE")} team");
            Console.WriteLine($"  Final Score: Red {RedTeamScore} - {BlueTeamScore} Blue");

            // Record match results for all players
            foreach (var player in TeamRed)
            {
                bool won = redWon;
                player.RecordMatchResult(won, player.Kills, player.Deaths, player.Score);
                player.IsInGame = false;
                player.CurrentGameId = -1;
            }

            foreach (var player in TeamBlue)
            {
                bool won = !redWon;
                player.RecordMatchResult(won, player.Kills, player.Deaths, player.Score);
                player.IsInGame = false;
                player.CurrentGameId = -1;
            }
        }

        public void UpdateScore(int teamId, int killCount)
        {
            if (teamId == 0)
                RedTeamScore += killCount;
            else
                BlueTeamScore += killCount;
        }
    }

    public enum GameStatus
    {
        WaitingForPlayers,
        InProgress,
        Finished,
        Cancelled
    }
}