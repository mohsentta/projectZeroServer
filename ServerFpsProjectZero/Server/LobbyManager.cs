using ProjectZero.Core;
using Newtonsoft.Json;
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
        private FriendsManager friendsManager;

        private readonly ConcurrentDictionary<string, Lobby> lobbies =
            new ConcurrentDictionary<string, Lobby>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Pending party invites, keyed by "inviteeId:inviterId" so a player can hold at most
        /// one invite from a given friend and the accept path can be a single atomic lookup.
        /// </summary>
        private readonly ConcurrentDictionary<string, PendingInvite> pendingInvites =
            new ConcurrentDictionary<string, PendingInvite>();

        private static readonly TimeSpan InviteLifetime = TimeSpan.FromSeconds(90);

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

        public void SetFriendsManager(FriendsManager friendsManager)
        {
            this.friendsManager = friendsManager;
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

            // The mode is fixed at creation. Co-op parties get their own capacity and will start
            // a survival run rather than a duel.
            var mode = req?.gameType ?? GameType.TeamDeathmatch;

            var lobby = new Lobby
            {
                LobbyId = GenerateLobbyCode(),
                HostPlayerId = player.PlayerId,
                CreatedAt = DateTime.UtcNow,
                GameType = mode
            };
            lobby.Members.Add(player);

            if (lobbies.TryAdd(lobby.LobbyId, lobby))
            {
                Console.WriteLine($"[Lobby] {player.Username} created {mode} lobby {lobby.LobbyId} " +
                                  $"(capacity {lobby.Capacity})");
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

                if (lobby.Members.Count >= lobby.Capacity)
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

        /// <summary>
        /// Reports the caller's current lobby back to them, or an error when they are in none.
        ///
        /// The transport is UDP, so a create/join response can be lost even though the
        /// server applied it. Without a way to ask "what lobby am I in?", the client would
        /// be left believing it has no lobby while the server holds it in one. This lets the
        /// client reconcile after a missing acknowledgement instead of retrying blindly.
        /// </summary>
        public void HandleLobbySync(string jsonData, IPEndPoint endpoint)
        {
            var req = Deserialize<LobbySyncRequest>(jsonData, endpoint);
            if (req == null) return;

            var player = ResolvePlayer(req.token, endpoint);
            if (player == null) return;

            var lobby = GetLobbyForPlayer(player.PlayerId);
            if (lobby == null)
            {
                SendLobbyError(endpoint, "You are not in a lobby");
                return;
            }

            serverManager.SendPacket(BuildUpdate(lobby), endpoint);
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

                lobby.Status = "in_game";
                members = lobby.Members.ToList();
            }

            Console.WriteLine($"[Lobby] Lobby {lobby.LobbyId} starting ({members.Count} players)");

            // Reuse the matchmaking authority path. Keep the lobby alive so the party can
            // return to the same roster after the match instead of being dissolved.
            gameManager.CreateGame(members, lobby.GameType, Map.Office);

            BroadcastLobbyUpdate(lobby, status: "in_game", message: "Game starting");
        }

        /// <summary>
        /// Reopens the party after its match ends. The lobby was retained during the game so
        /// the same roster/code is available when the post-game screen returns to the menu.
        /// </summary>
        public void OnGameEnded(List<Player> players)
        {
            if (players == null || players.Count == 0) return;

            Lobby lobby = GetLobbyForPlayer(players[0].PlayerId);
            if (lobby == null) return;

            lock (lobby.Sync)
            {
                lobby.Status = "waiting";
                lobby.Ready.Clear();
            }

            BroadcastLobbyUpdate(lobby, status: "waiting", message: "Match over — ready up again");
        }

        #endregion

        #region Party invites

        /// <summary>
        /// Invite a specific friend into the caller's lobby. The friend gets a notification
        /// they can accept; accepting puts them straight into this lobby, so the player never
        /// has to read out or type a code.
        /// </summary>
        public void HandleLobbyInvite(string jsonData, IPEndPoint endpoint)
        {
            var req = Deserialize<LobbyInviteRequest>(jsonData, endpoint);
            if (req == null) return;

            var inviter = ResolvePlayer(req.token, endpoint);
            if (inviter == null) return;

            var lobby = GetLobbyForPlayer(inviter.PlayerId);
            if (lobby == null)
            {
                SendInviteResult(endpoint, false, req.targetPlayerId, null, "You are not in a lobby");
                return;
            }

            var target = loginManager?.GetPlayerById(req.targetPlayerId);
            if (target == null)
            {
                SendInviteResult(endpoint, false, req.targetPlayerId, null, "That player no longer exists");
                return;
            }

            // Only friends may be invited, so a lobby code or player id cannot be used by a
            // stranger to push themselves into someone's party.
            if (friendsManager == null || !friendsManager.AreFriends(inviter.PlayerId, target.PlayerId))
            {
                SendInviteResult(endpoint, false, target.PlayerId, target.Username, "You can only invite friends");
                return;
            }

            if (target.PlayerId == inviter.PlayerId)
            {
                SendInviteResult(endpoint, false, target.PlayerId, target.Username, "You are already in the lobby");
                return;
            }

            if (!target.IsConnected)
            {
                SendInviteResult(endpoint, false, target.PlayerId, target.Username, $"{target.Username} is offline");
                return;
            }

            if (GetLobbyForPlayer(target.PlayerId) != null)
            {
                SendInviteResult(endpoint, false, target.PlayerId, target.Username, $"{target.Username} is already in a party");
                return;
            }

            int memberCount;
            lock (lobby.Sync)
            {
                if (lobby.Status != "waiting")
                {
                    SendInviteResult(endpoint, false, target.PlayerId, target.Username, "Your party is no longer accepting players");
                    return;
                }

                if (lobby.Members.Count >= lobby.Capacity)
                {
                    SendInviteResult(endpoint, false, target.PlayerId, target.Username, "Your party is full");
                    return;
                }

                memberCount = lobby.Members.Count;
            }

            var invite = new PendingInvite
            {
                LobbyId = lobby.LobbyId,
                InviterPlayerId = inviter.PlayerId,
                InviterUsername = inviter.Username,
                InviteePlayerId = target.PlayerId,
                CreatedAt = DateTime.UtcNow
            };

            pendingInvites[InviteKey(target.PlayerId, inviter.PlayerId)] = invite;

            var targetClient = serverManager.GetClientById(target.PlayerId);
            if (targetClient == null || !targetClient.IsConnected)
            {
                SendInviteResult(endpoint, false, target.PlayerId, target.Username, $"{target.Username} went offline");
                return;
            }

            serverManager.SendPacket(new LobbyInviteNotification
            {
                type = "lobby_invite_notification",
                lobbyId = lobby.LobbyId,
                fromPlayerId = inviter.PlayerId,
                fromUsername = inviter.Username,
                memberCount = memberCount,
                maxPlayers = lobby.Capacity,
                timestamp = DateTime.UtcNow
            }, targetClient);

            Console.WriteLine($"[Lobby] {inviter.Username} invited {target.Username} to {lobby.LobbyId}");

            SendInviteResult(endpoint, true, target.PlayerId, target.Username, $"Invite sent to {target.Username}");
        }

        /// <summary>Accept or decline an invite. Accepting joins the lobby immediately.</summary>
        public void HandleLobbyInviteRespond(string jsonData, IPEndPoint endpoint)
        {
            var req = Deserialize<LobbyInviteRespondRequest>(jsonData, endpoint);
            if (req == null) return;

            var invitee = ResolvePlayer(req.token, endpoint);
            if (invitee == null) return;

            string key = InviteKey(invitee.PlayerId, req.fromPlayerId);

            if (!pendingInvites.TryRemove(key, out var invite))
            {
                SendLobbyError(endpoint, "That invite is no longer available");
                return;
            }

            var inviterName = invite.InviterUsername;

            if (DateTime.UtcNow - invite.CreatedAt > InviteLifetime)
            {
                NotifyInviter(invite.InviterPlayerId, invitee, "expired", "The invite expired");
                SendLobbyError(endpoint, "That invite expired");
                return;
            }

            if (!req.accept)
            {
                Console.WriteLine($"[Lobby] {invitee.Username} declined the invite from {inviterName}");
                NotifyInviter(invite.InviterPlayerId, invitee, "declined", $"{invitee.Username} declined");
                return;
            }

            if (!lobbies.TryGetValue(invite.LobbyId, out var lobby))
            {
                SendLobbyError(endpoint, "That party no longer exists");
                return;
            }

            if (GetLobbyForPlayer(invitee.PlayerId) != null)
            {
                SendLobbyError(endpoint, "You are already in a party");
                return;
            }

            bool joined = false;
            lock (lobby.Sync)
            {
                if (lobby.Status == "waiting" && lobby.Members.Count < lobby.Capacity &&
                    !lobby.Members.Any(m => m.PlayerId == invitee.PlayerId))
                {
                    lobby.Members.Add(invitee);
                    joined = true;
                }
            }

            if (!joined)
            {
                SendLobbyError(endpoint, "That party is full or no longer accepting players");
                return;
            }

            Console.WriteLine($"[Lobby] {invitee.Username} accepted {inviterName}'s invite and joined {lobby.LobbyId}");

            NotifyInviter(invite.InviterPlayerId, invitee, "accepted", $"{invitee.Username} joined your party");

            // BroadcastLobbyUpdate includes the new member, so both clients re-render from the
            // authoritative list and the invitee's panel switches to its in-lobby state.
            BroadcastLobbyUpdate(lobby);
        }

        /// <summary>Drops any invites involving a player who left, so stale prompts cannot be accepted.</summary>
        private void ClearInvitesFor(int playerId)
        {
            foreach (var kvp in pendingInvites)
            {
                if (kvp.Value.InviterPlayerId == playerId || kvp.Value.InviteePlayerId == playerId)
                    pendingInvites.TryRemove(kvp.Key, out _);
            }
        }

        private void NotifyInviter(int inviterPlayerId, Player invitee, string state, string message)
        {
            var inviterClient = serverManager.GetClientById(inviterPlayerId);
            if (inviterClient == null || !inviterClient.IsConnected) return;

            serverManager.SendPacket(new LobbyInviteUpdate
            {
                type = "lobby_invite_update",
                otherPlayerId = invitee.PlayerId,
                otherUsername = invitee.Username,
                state = state,
                message = message,
                timestamp = DateTime.UtcNow
            }, inviterClient);
        }

        private void SendInviteResult(IPEndPoint endpoint, bool success, int targetPlayerId,
                                      string targetUsername, string message)
        {
            serverManager.SendPacket(new LobbyInviteResult
            {
                type = "lobby_invite_result",
                success = success,
                targetPlayerId = targetPlayerId,
                targetUsername = targetUsername,
                message = message,
                timestamp = DateTime.UtcNow
            }, endpoint);
        }

        private static string InviteKey(int inviteePlayerId, int inviterPlayerId)
        {
            return inviteePlayerId + ":" + inviterPlayerId;
        }

        private class PendingInvite
        {
            public string LobbyId { get; set; }
            public int InviterPlayerId { get; set; }
            public string InviterUsername { get; set; }
            public int InviteePlayerId { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        #endregion

        #region Disconnect handling

        /// <summary>Removes a disconnected player from any lobby they were in.</summary>
        public void OnPlayerDisconnected(int playerId)
        {
            // Drop pending invites first so nobody can accept an invite from a player who
            // has already gone away.
            ClearInvitesFor(playerId);

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
                    ready = lobby.Ready.TryGetValue(m.PlayerId, out var r) && r,
                    loadout = m.Loadout
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
                gameType = lobby.GameType,
                maxPlayers = lobby.Capacity,
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
        /// <summary>Team deathmatch is 5v5.</summary>
        public const int MaxPlayers = 10;

        /// <summary>
        /// Co-op accepts a full squad, since the enemies scale with the players present and
        /// there is no opposing team to balance against.
        /// </summary>
        public const int MaxCoopPlayers = 4;

        public string LobbyId { get; set; }
        public int HostPlayerId { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Status { get; set; } = "waiting"; // waiting | starting | closed

        /// <summary>
        /// Mode this party will play. Set at creation and never changed: a party is either a
        /// duel or a co-op run, and the lobby list is filtered by it so players looking for one
        /// mode never see the other.
        /// </summary>
        public GameType GameType { get; set; } = GameType.TeamDeathmatch;

        /// <summary>Capacity for this lobby's mode.</summary>
        public int Capacity => GameType == GameType.CoopSurvival ? MaxCoopPlayers : MaxPlayers;

        public List<Player> Members { get; } = new List<Player>();
        public Dictionary<int, bool> Ready { get; } = new Dictionary<int, bool>();
        public readonly object Sync = new object();
    }
}
