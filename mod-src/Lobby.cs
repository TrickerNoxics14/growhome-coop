using System;
using System.Collections.Generic;
using System.Text;
using Steamworks;

namespace CoopMod
{
    // A short invite code is just the Steam lobby id written in a friendly alphabet (no 0/O/1/I), e.g. K7QM-2XD9-HP4TB.
    public static class InviteCode
    {
        const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // 32 symbols = 5 bits each; 13 symbols cover 64 bits

        public static string Encode(ulong id)
        {
            var chars = new char[13];
            for (int i = 12; i >= 0; i--) { chars[i] = Alphabet[(int)(id & 31UL)]; id >>= 5; }
            string s = new string(chars);
            return s.Substring(0, 4) + "-" + s.Substring(4, 4) + "-" + s.Substring(8, 5);
        }

        public static bool TryDecode(string text, out ulong id)
        {
            id = 0;
            if (text == null) return false;
            var sb = new StringBuilder();
            foreach (char c in text.ToUpperInvariant())
            {
                if (c == '-' || c == ' ') continue;
                if (c == '0') sb.Append('O'); else if (c == '1') sb.Append('I');   // forgive look-alikes
                else sb.Append(c);
            }
            string s = sb.ToString();
            if (s.Length != 13) return false;
            ulong v = 0;
            foreach (char c in s)
            {
                int k = Alphabet.IndexOf(c);
                if (k < 0) return false;
                v = (v << 5) | (ulong)k;
            }
            id = v;
            return true;
        }
    }

    // Steam lobby = the "room" that holds everybody who used the same code. Everyone who sees a member connects to them directly.
    public class Lobby
    {
        public CSteamID Id;
        public bool InLobby;
        public string Code = "";
        public string Status = "";
        public Action<ulong> OnMember;     // called for every member we learn about (including ourselves)
        readonly HashSet<ulong> talk = new HashSet<ulong>();   // the room members I may hear from right now (admitted, not refused)

        public bool IsOpen;                  // the room I am in is public (listed, anybody may join)
        public readonly List<Room> Rooms = new List<Room>();
        public bool Listing;                 // a room-list request is running
        bool wantOpen, wantListed, wantFriends;
        public ulong HostId;                 // who owned the room when I got in: the only one I ever treat as the host (Steam hands a room to somebody else when its owner leaves)
        CallResult<LobbyMatchList_t> listed;
        CallResult<LobbyCreated_t> created;
        CallResult<LobbyEnter_t> entered;
        Callback<LobbyChatUpdate_t> update;
        Callback<LobbyInvite_t> invited;
        Callback<GameLobbyJoinRequested_t> joinAsked;

        // A friend invited me to their party (shown in the party menu as JOIN NAME'S PARTY), or I accepted an invite / "Join game" in Steam itself.
        public ulong InviteLobby, InviteFrom;
        public float InviteAt = -999f;
        public Action<ulong> OnJoinAsked;

        public Lobby()
        {
            created = CallResult<LobbyCreated_t>.Create(OnCreated);
            entered = CallResult<LobbyEnter_t>.Create(OnEntered);
            update = Callback<LobbyChatUpdate_t>.Create(OnUpdate);
            listed = CallResult<LobbyMatchList_t>.Create(OnListed);
            invited = Callback<LobbyInvite_t>.Create(OnInvited);
            joinAsked = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
        }

        void OnInvited(LobbyInvite_t r)
        {
            if (r.m_ulSteamIDLobby == 0 || (InLobby && r.m_ulSteamIDLobby == Id.m_SteamID)) return;
            InviteLobby = r.m_ulSteamIDLobby; InviteFrom = r.m_ulSteamIDUser; InviteAt = UnityEngine.Time.realtimeSinceStartup;
            string who = "A FRIEND";
            try { who = PlayerNames.Clean(SteamFriends.GetFriendPersonaName(new CSteamID(r.m_ulSteamIDUser)), "A FRIEND").ToUpperInvariant(); } catch (Exception) { }
            CoopBehaviour.Log("party: invited by " + r.m_ulSteamIDUser + " to " + r.m_ulSteamIDLobby);
            CoopUI.Toast(who + " INVITED YOU TO A PARTY");
        }

        void OnJoinRequested(GameLobbyJoinRequested_t r)
        {
            CoopBehaviour.Log("party: Steam asks to join " + r.m_steamIDLobby.m_SteamID + " (invite from " + r.m_steamIDFriend.m_SteamID + ")");
            if (OnJoinAsked != null) OnJoinAsked(r.m_steamIDLobby.m_SteamID);
        }

        public void ClearInvite() { InviteLobby = 0; InviteFrom = 0; InviteAt = -999f; }
        public bool InviteWaiting { get { return InviteLobby != 0 && (!InLobby || InviteLobby != Id.m_SteamID) && UnityEngine.Time.realtimeSinceStartup - InviteAt < 600f; } }

        // While Steam is still making or entering a room, a second request is refused: it would make a second room nobody tracks (a double press).
        float busyUntil;
        public bool Busy { get { return UnityEngine.Time.realtimeSinceStartup < busyUntil; } }

        // friendsOnly: a private party my Steam friends can see and join from Steam ("Join game"); the host lets their own friends in without asking
        public void Create(bool open, bool listed, bool friendsOnly = false)
        {
            if (InLobby || Busy) return;
            busyUntil = UnityEngine.Time.realtimeSinceStartup + 20f;
            wantOpen = open && !friendsOnly; wantListed = listed && !friendsOnly; wantFriends = friendsOnly;
            Status = "making the party...";
            var type = friendsOnly ? ELobbyType.k_ELobbyTypeFriendsOnly : (open || listed) ? ELobbyType.k_ELobbyTypePublic : ELobbyType.k_ELobbyTypeInvisible;
            created.Set(SteamMatchmaking.CreateLobby(type, 8));
        }

        public void Join(ulong lobbyId)
        {
            if (InLobby || Busy) return;
            busyUntil = UnityEngine.Time.realtimeSinceStartup + 20f;
            Status = "joining...";
            entered.Set(SteamMatchmaking.JoinLobby(new CSteamID(lobbyId)));
        }

        // Leaving forgets the party's host too, so the players set up in coop-peers.txt (no party) are heard again; the party's players are dropped by the mod.
        public void Leave() { if (InLobby) SteamMatchmaking.LeaveLobby(Id); InLobby = false; IsOpen = false; Code = ""; talk.Clear(); HostId = 0; busyUntil = 0f; }

        void OnCreated(LobbyCreated_t r, bool ioFailure)
        {
            busyUntil = 0f;
            if (ioFailure || r.m_eResult != EResult.k_EResultOK) { Status = "could not make the party (" + r.m_eResult + ")"; CoopUI.Toast("COULD NOT MAKE THE ROOM. TRY AGAIN"); CoopBehaviour.Log("lobby: create failed " + r.m_eResult + " io=" + ioFailure); return; }
            Id = new CSteamID(r.m_ulSteamIDLobby);
            InLobby = true; IsOpen = wantOpen; HostId = SteamUser.GetSteamID().m_SteamID;
            SteamMatchmaking.SetLobbyData(Id, "gh", "1");
            SteamMatchmaking.SetLobbyData(Id, "mode", wantOpen ? RoomRules.Open : RoomRules.Private);
            if (wantFriends) SteamMatchmaking.SetLobbyData(Id, "friends", "1");
            SteamMatchmaking.SetLobbyData(Id, "name", RoomRules.RoomName(SteamFriends.GetPersonaName()));
            Code = InviteCode.Encode(Id.m_SteamID);
            Status = "invite code: " + Code; CoopUI.Toast("INVITE CODE READY");
            CoopBehaviour.Log("lobby: created " + Id.m_SteamID + ", code " + Code);
            SyncMembers();
        }

        // Steam said no to a join (a code for no room, a full room, a host gone). The page that asked has closed by then, so M.O.M says it
        // (on the start or pause menu's notice line, the co-op menu's subtitle or the HUD), as the old status panel did.
        public const string JoinNoText = "could not join (code wrong, party full or the host left)";
        public void JoinDidNotWork()
        {
            Status = JoinNoText;
            CoopUI.Toast("COULD NOT JOIN: CODE WRONG, PARTY FULL OR THE HOST LEFT");
        }

        void OnEntered(LobbyEnter_t r, bool ioFailure)
        {
            busyUntil = 0f;
            if (ioFailure || r.m_EChatRoomEnterResponse != 1)
            {
                JoinDidNotWork();
                CoopBehaviour.Log("lobby: join failed, response " + r.m_EChatRoomEnterResponse + " io=" + ioFailure);
                return;
            }
            Id = new CSteamID(r.m_ulSteamIDLobby);
            InLobby = true; HostId = SteamMatchmaking.GetLobbyOwner(Id).m_SteamID;
            Code = InviteCode.Encode(Id.m_SteamID);
            Status = "joined " + Code; CoopUI.Toast("LOBBY LINK ESTABLISHED");
            CoopBehaviour.Log("lobby: joined " + Id.m_SteamID);
            SyncMembers();
        }

        void OnUpdate(LobbyChatUpdate_t u)
        {
            if (!InLobby || u.m_ulSteamIDLobby != Id.m_SteamID) return;
            RefreshTalk();
            if ((u.m_rgfChatMemberStateChange & (uint)EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0)
            {
                CoopBehaviour.Log("lobby: " + u.m_ulSteamIDUserChanged + " joined");
                if (OnMember != null) OnMember(u.m_ulSteamIDUserChanged);
            }
        }

        void SyncMembers()
        {
            RefreshTalk();
            int n = SteamMatchmaking.GetNumLobbyMembers(Id);
            for (int i = 0; i < n; i++)
            {
                ulong m = SteamMatchmaking.GetLobbyMemberByIndex(Id, i).m_SteamID;
                if (OnMember != null) OnMember(m);
            }
        }

        // ---- rooms: who may join, and the public list ----------------------------------------------------------------------------------

        // Nothing the room says is trusted once its owner is not the host I joined (Steam handed it on): the new owner could have written it.
        string Data(string key) { return InLobby && !RoomRules.HostChanged(HostId, OwnerId()) ? SteamMatchmaking.GetLobbyData(Id, key) : ""; }
        public string Mode { get { return RoomRules.Mode(Data("mode")); } }
        public bool FriendsOnly { get { return Data("friends") == "1"; } }
        public string Get(string key) { return Data(key) ?? ""; }
        public void Set(string key, string value) { if (IsOwner) SteamMatchmaking.SetLobbyData(Id, key, value ?? ""); }
        public void SetMine(string key, string value) { if (InLobby) SteamMatchmaking.SetLobbyMemberData(Id, key, value ?? ""); }
        public string MemberData(ulong user, string key) { if (!InLobby) return ""; return SteamMatchmaking.GetLobbyMemberData(Id, new CSteamID(user), key) ?? ""; }
        public bool Invite(ulong friend) { return InLobby && friend != 0 && SteamMatchmaking.InviteUserToLobby(Id, new CSteamID(friend)); }

        // everybody in the party right now, in Steam's order (at most 16)
        public List<ulong> Members()
        {
            var list = new List<ulong>();
            if (!InLobby) return list;
            int n = SteamMatchmaking.GetNumLobbyMembers(Id);
            for (int i = 0; i < n && list.Count < 16; i++) { ulong m = SteamMatchmaking.GetLobbyMemberByIndex(Id, i).m_SteamID; if (m != 0 && !list.Contains(m)) list.Add(m); }
            return list;
        }

        public bool IsOwner { get { return InLobby && HostId == SteamUser.GetSteamID().m_SteamID && OwnerId() == HostId; } }

        // May I connect to this member of the room?
        public bool Allowed(ulong user)
        {
            return InLobby && IsMember(user) && RoomRules.Allowed(Data("mode"), HostId, user, Data("ok"), Data("no"));
        }

        // true once the host has said yes or no to this player
        public bool Decided(ulong user) { return RoomRules.ParseIds(Data("ok")).Contains(user) || RoomRules.ParseIds(Data("no")).Contains(user); }

        // the yes list keeps only players still in the party, so it never fills up; false when the player could not be written into it
        public bool Approve(ulong user)
        {
            if (!IsOwner) return false;
            string kept = "";
            foreach (ulong id in RoomRules.ParseIds(Data("ok"))) if (IsMember(id)) kept = RoomRules.AddId(kept, id);
            string now = RoomRules.AddId(kept, user);
            SteamMatchmaking.SetLobbyData(Id, "ok", now); RefreshTalk();
            return RoomRules.ParseIds(now).Contains(user);
        }
        public void Deny(ulong user) { if (IsOwner) { SteamMatchmaking.SetLobbyData(Id, "no", RoomRules.AddId(Data("no"), user)); RefreshTalk(); } }

        // What the room says about me: 0 = fine, 1 = waiting for the host, 2 = the host refused me
        public int MyAdmission()
        {
            if (!InLobby || IsOwner) return 0;
            ulong me = SteamUser.GetSteamID().m_SteamID;
            if (RoomRules.ParseIds(Data("no")).Contains(me)) return 2;
            return RoomRules.Allowed(Data("mode"), HostId, me, Data("ok"), Data("no")) ? 0 : 1;
        }

        // Look again at who is in the room (people who joined, a host who has just said yes).
        public void Poll() { if (InLobby && RoomRules.HostChanged(HostId, OwnerId())) { Leave(); Status = "the leader left, the party is over"; CoopBehaviour.Log("lobby: the host left, room closed"); return; } if (InLobby) { IsOpen = Mode == RoomRules.Open; SyncMembers(); } }

        public void RequestRooms()
        {
            if (Listing) return;
            Listing = true; Status = "looking for parties...";
            SteamMatchmaking.AddRequestLobbyListStringFilter("gh", "1", ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(20);
            listed.Set(SteamMatchmaking.RequestLobbyList());
        }

        void OnListed(LobbyMatchList_t r, bool ioFailure)
        {
            Listing = false; Rooms.Clear();
            if (ioFailure) { Status = "could not get the party list"; return; }
            for (int i = 0; i < (int)r.m_nLobbiesMatching && i < 20; i++)
            {
                var id = SteamMatchmaking.GetLobbyByIndex(i);
                Rooms.Add(new Room { Id = id.m_SteamID, Ask = RoomRules.Mode(SteamMatchmaking.GetLobbyData(id, "mode")) != RoomRules.Open, Name = RoomRules.RoomName(SteamMatchmaking.GetLobbyData(id, "name")), Players = SteamMatchmaking.GetNumLobbyMembers(id), Max = SteamMatchmaking.GetLobbyMemberLimit(id) });
            }
            Status = Rooms.Count == 0 ? "no public parties right now" : Rooms.Count + (Rooms.Count == 1 ? " public party" : " public parties");
        }

        // The lobby owner is the player who made the code: the host whose world everybody plays in (0 when not in a lobby).
        // Is this player's connection allowed to talk to me right now? (Cached: rebuilt whenever members or the room's data change.)
        public bool MayTalk(ulong user) { return InLobby && talk.Contains(user); }

        public void RefreshTalk()
        {
            talk.Clear();
            if (!InLobby) return;
            string mode = Data("mode"), ok = Data("ok"), no = Data("no");
            int n = SteamMatchmaking.GetNumLobbyMembers(Id);
            for (int i = 0; i < n; i++)
            {
                ulong m = SteamMatchmaking.GetLobbyMemberByIndex(Id, i).m_SteamID;
                if (m != 0 && RoomRules.Allowed(mode, HostId, m, ok, no)) talk.Add(m);
            }
        }

        public ulong OwnerId() { return InLobby ? SteamMatchmaking.GetLobbyOwner(Id).m_SteamID : 0UL; }

        // Used by the P2P layer: is this Steam user in my lobby right now?
        public bool IsMember(ulong user)
        {
            if (!InLobby) return false;
            int n = SteamMatchmaking.GetNumLobbyMembers(Id);
            for (int i = 0; i < n; i++) if (SteamMatchmaking.GetLobbyMemberByIndex(Id, i).m_SteamID == user) return true;
            return false;
        }
    }
}
