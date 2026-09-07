using Newtonsoft.Json;
using ServerFpsProjectZero.Models;
using ServerFpsProjectZero.Networking;
using ServerFpsProjectZero.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace ServerFpsProjectZero.Server
{
    /// <summary>
    /// Party/lobby system: players create or join a lobby by short code, ready up,
    /// and the host starts the match. Starting reuses GameManager.CreateGame as the
    /// single authority for match creation (same path as matchmaking).
    /// </summary>
    public class LobbyManager
    {
        private readonly ServerManager serverManager;
        private readonly GameManager gameManager;
        private LoginManager loginManager;

        private readonly ConcurrentDictionary<string, Lobby> lobbies =
            new ConcurrentDictionary<string, Lobby>(StringComparer.OrdinalIgnoreCase);

        private const int LobbyCodeLength = 6;
        private static readonly Random Rng = new Random();

        public LobbyManager(ServerManager serverManager, GameManager gameManager)
        {
            this.serverManager = serverManager;
            this.gameManager = gameManager;
        }

        public void SetLoginManager(LoginManager loginManager)
        {
            this.loginManager = loginManager;
        }

        #region Packet handlers

        public void HandleCreateLobby(string jsonData, IPEndPoint endpoint)
        {
            var req = Deserialize<CreateLobbyRequest>(jsonData, endpoint);
            if (req == null) return;

            var player = ResolvePlayer(req.token, endpoint);
            if (player == null) return;

            // A player can only be in one lobby at a time.
            var existing = GetLobbyForPlayer(player.PlayerId);
            if (existing != null)
            {
                SendLobbyError(endpoint, "You are already in a lobby");
                return;
            }

            var lobby = new Lobby
            {
                LobbyId = GenerateLobbyCode(),
                HostPlayerId = player.PlayerId,
                CreatedAt = DateTime.UtcNow
            };
            lobby.Members.Add(player);

            if (lobbies.TryAdd(lobby.LobbyId, lobby))
            {
                Console.WriteLine($"[Lobby] {player.Username} created lobby {lobby.LobbyId}");
                BroadcastLobbyUpdate(lobby);
            }
            else
            {
                SendLobbyError(endpoint, "Failed to create lobby (code collision); please retry");
            }
        }

        public void HandleJoinLobby(string jsonData, IPEndPoint endpoint)
        {
            var req = Deserialize<JoinLobbyRequest>(jsonData, endpoint);
            if (req == null) return;

            var player = ResolvePlayer(req.token, endpoint);
            if (player == null) return;

            if (string.IsNullOrEmpty(req.lobbyId) ||
                !lobbies.TryGetValue(req.lobbyId.Trim(), out var lobby))
            {
                SendLobbyError(endpoint, "Lobby not found");
                return;
            }

            lock (lobby.Sync)
            {
                if (lobby.Status != "waiting")
                {
                    SendLobbyError(endpoint, "Lobby is no longer accepting players");
                    return;
                }

                if (lobby.Members.Any(m => m.PlayerId == player.PlayerId))
                {
                    SendLobbyError(endpoint, "You are already in this lobby");
                    return;
                }

                if (lobby.Members.Count >= Lobby.MaxPlayers)
                {
                    SendLobbyError(endpoint, "Lobby is full");
                    return;
                }

                lobby.Members.Add(player);
                Console.WriteLine($"[Lobby] {player.Username} joined lobby {lobby.LobbyId}");
            }

            BroadcastLobbyUpdate(lobby);
        }

        public void HandleLeaveLobby(string jsonData, IPEndPoint endpoint)
        {
            var req = Deserialize<LeaveLobbyRequest>(jsonData, endpoint);
            if (req == null) return;

            var player = ResolvePlayer(req.token, endpoint);
            if (player == null) return;

            if (string.IsNullOrEmpty(req.lobbyId) ||
                !lobbies.TryGetValue(req.lobbyId.Trim(), out var lobby))
                return;

            RemovePlayerFromLobby(player, lobby, left: true);
        }

        public void HandleLobbyReady(string jsonData, IPEndPoint endpoint)
        {
            var req = Deserialize<LobbyReadyRequest>(jsonData, endpoint);
            if (req == null) return;

            var player = ResolvePlayer(req.token, endpoint);
            if (player == null) return;

            if (string.IsNullOrEmpty(req.lobbyId) ||
                !lobbies.TryGetValue(req.lobbyId.Trim(), out var lobby))
                return;

            lock (lobby.Sync)
            {
                if (lobby.Status != "waiting") return;
                lobby.Ready[player.PlayerId] = req.ready;
            }

            BroadcastLobbyUpdate(lobby);
        }

        public void HandleLobbyStart(string jsonData, IPEndPoint endpoint)
        {
            var req = Deserialize<LobbyStartRequest>(jsonData, endpoint);
            if (req == null) return;

            var player = ResolvePlayer(req.token, endpoint);
            if (player == null) return;

            if (string.IsNullOrEmpty(req.lobbyId) ||
                !lobbies.TryGetValue(req.lobbyId.Trim(), out var lobby))
            {
                SendLobbyError(endpoint, "Lobby not found");
                return;
            }

            // Only the host may start.
            if (player.PlayerId != lobby.HostPlayerId)
            {
                SendLobbyError(endpoint, "Only the host can start the game");
                return;
            }

            List<Player> members;
            lock (lobby.Sync)
            {
                if (lobby.Status != "waiting")
                    return;

                if (lobby.Members.Count < 2)
                {
                    SendLobbyError(endpoint, "Need 2 players to start");
                    return;
                }

                // All members must be ready.
                foreach (var m in lobby.Members)
                {
                    if (!lobby.Ready.TryGetValue(m.PlayerId, out var ready) || !ready)
                    {
                        SendLobbyError(endpoint, $"{m.Username} is not ready");
                        return;
                    }
                }

                lobby.Status = "starting";
                members = lobby.Members.ToList();
            }

            // Remove the lobby before creating the game so players are no longer
            // considered "in a lobby"; the match is authoritative from here on.
            lobbies.TryRemove(lobby.LobbyId, out _);

            Console.WriteLine($"[Lobby] Lobby {lobby.LobbyId} starting ({members.Count} players)");

            // Reuse the matchmaking authority path.
            gameManager.CreateGame(members, GameType.TeamDeathmatch, Map.Office);

            // Tell any remaining clients the lobby is closed.
            BroadcastLobbyUpdate(lobby, status: "closed", message: "Game starting");
        }

        #endregion

        #region Disconnect handling

        /// <summary>Removes a disconnected player from any lobby they were in.</summary>
        public void OnPlayerDisconnected(int playerId)
        {
            var lobby = GetLobbyForPlayer(playerId);
            if (lobby == null) return;

            var player = lobby.Members.FirstOrDefault(m => m.PlayerId == playerId);
            if (player == null) return;

            RemovePlayerFromLobby(player, lobby, left: false);
        }

        #endregion

        #region Internals

        private Player ResolvePlayer(string token, IPEndPoint endpoint)
        {
            if (string.IsNullOrEmpty(token))
            {
                SendLobbyError(endpoint, "Missing token");
                return null;
            }

            var player = loginManager?.GetPlayerByToken(token);
            if (player == null || !player.IsConnected)
                SendLobbyError(endpoint, "Invalid or expired session");

            return player;
        }

        private void RemovePlayerFromLobby(Player player, Lobby lobby, bool left)
        {
            bool removed;
            lock (lobby.Sync)
            {
                removed = lobby.Members.RemoveAll(m => m.PlayerId == player.PlayerId) > 0;
                lobby.Ready.Remove(player.PlayerId);
            }

            if (!removed) return;

            Console.WriteLine($"[Lobby] {player.Username} left lobby {lobby.LobbyId}");

            bool empty;
            lock (lobby.Sync)
            {
                empty = lobby.Members.Count == 0;

                // If the host left, promote the first remaining member (or close).
                if (lobby.HostPlayerId == player.PlayerId && !empty)
                    lobby.HostPlayerId = lobby.Members[0].PlayerId;
            }

            if (empty)
            {
                lobbies.TryRemove(lobby.LobbyId, out _);
                Console.WriteLine($"[Lobby] Lobby {lobby.LobbyId} closed (empty)");
            }
            else
            {
                BroadcastLobbyUpdate(lobby);
            }
        }

        private Lobby GetLobbyForPlayer(int playerId)
        {
            return lobbies.Values.FirstOrDefault(l =>
                l.Members.Any(m => m.PlayerId == playerId));
        }

        private void BroadcastLobbyUpdate(Lobby lobby, string status = null, string message = null)
        {
            foreach (var member in lobby.Members.ToList())
            {
                var client = serverManager.GetClientById(member.PlayerId);
                if (client == null || !client.IsConnected) continue;

                var update = BuildUpdate(lobby, status, message);
                serverManager.SendPacket(update, client);
            }
        }

        private LobbyUpdateData BuildUpdate(Lobby lobby, string status = null, string message = null)
        {
            List<LobbyMemberInfo> members;
            string effectiveStatus;
            lock (lobby.Sync)
            {
                effectiveStatus = status ?? lobby.Status;
                members = lobby.Members.Select(m => new LobbyMemberInfo
                {
                    playerId = m.PlayerId,
                    username = m.Username,
                    ready = lobby.Ready.TryGetValue(m.PlayerId, out var r) && r
                }).ToList();
            }

            return new LobbyUpdateData
            {
                type = "lobby_update",
                lobbyId = lobby.LobbyId,
                hostPlayerId = lobby.HostPlayerId,
                members = members,
                status = effectiveStatus,
                message = message ?? "",
                timestamp = DateTime.UtcNow
            };
        }

        private string GenerateLobbyCode()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I
            var code = new char[LobbyCodeLength];
            for (int i = 0; i < code.Length; i++)
                code[i] = chars[Rng.Next(chars.Length)];
            return new string(code);
        }

        private T Deserialize<T>(string jsonData, IPEndPoint endpoint)
        {
            try
            {
                return JsonConvert.DeserializeObject<T>(jsonData);
            }
            catch (Exception ex)
            {
                SendLobbyError(endpoint, "Invalid request");
                Console.WriteLine($"[Lobby] Deserialize error: {ex.Message}");
                return default;
            }
        }

        private void SendLobbyError(IPEndPoint endpoint, string message)
        {
            var response = new
            {
                type = "lobby_error",
                message = message,
                timestamp = DateTime.UtcNow
            };
            serverManager.SendPacket(response, endpoint);
        }

        #endregion
    }

    public class Lobby
    {
        public const int MaxPlayers = 2;

        public string LobbyId { get; set; }
        public int HostPlayerId { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Status { get; set; } = "waiting"; // waiting | starting | closed
        public List<Player> Members { get; } = new List<Player>();
        public Dictionary<int, bool> Ready { get; } = new Dictionary<int, bool>();
        public readonly object Sync = new object();
    }
}
