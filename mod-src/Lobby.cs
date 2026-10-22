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

        public bool IsOpen;                  // the room I am in is public (listed, anybody may join)
        public readonly List<Room> Rooms = new List<Room>();
        public bool Listing;                 // a room-list request is running
        bool wantOpen, wantListed;
        public ulong HostId;                 // who owned the room when I got in: the only one I ever treat as the host (Steam hands a room to somebody else when its owner leaves)
        CallResult<LobbyMatchList_t> listed;
        CallResult<LobbyCreated_t> created;
        CallResult<LobbyEnter_t> entered;
        Callback<LobbyChatUpdate_t> update;

        public Lobby()
        {
            created = CallResult<LobbyCreated_t>.Create(OnCreated);
            entered = CallResult<LobbyEnter_t>.Create(OnEntered);
            update = Callback<LobbyChatUpdate_t>.Create(OnUpdate);
            listed = CallResult<LobbyMatchList_t>.Create(OnListed);
        }

        public void Create(bool open, bool listed)
        {
            if (InLobby) return;
            wantOpen = open; wantListed = listed;
            Status = "creating room...";
            created.Set(SteamMatchmaking.CreateLobby((open || listed) ? ELobbyType.k_ELobbyTypePublic : ELobbyType.k_ELobbyTypeInvisible, 8));
        }

        public void Join(ulong lobbyId)
        {
            Status = "joining...";
            entered.Set(SteamMatchmaking.JoinLobby(new CSteamID(lobbyId)));
        }

        public void Leave() { if (InLobby) { SteamMatchmaking.LeaveLobby(Id); InLobby = false; IsOpen = false; Code = ""; } }

        void OnCreated(LobbyCreated_t r, bool ioFailure)
        {
            if (ioFailure || r.m_eResult != EResult.k_EResultOK) { Status = "could not create an invite code (" + r.m_eResult + ")"; CoopBehaviour.Log("lobby: create failed " + r.m_eResult + " io=" + ioFailure); return; }
            Id = new CSteamID(r.m_ulSteamIDLobby);
            InLobby = true; IsOpen = wantOpen; HostId = SteamUser.GetSteamID().m_SteamID;
            SteamMatchmaking.SetLobbyData(Id, "gh", "1");
            SteamMatchmaking.SetLobbyData(Id, "mode", wantOpen ? RoomRules.Open : RoomRules.Private);
            SteamMatchmaking.SetLobbyData(Id, "name", RoomRules.RoomName(SteamFriends.GetPersonaName()));
            Code = InviteCode.Encode(Id.m_SteamID);
            Status = "invite code: " + Code; CoopUI.Toast("INVITE CODE READY");
            CoopBehaviour.Log("lobby: created " + Id.m_SteamID + ", code " + Code);
            SyncMembers();
        }

        void OnEntered(LobbyEnter_t r, bool ioFailure)
        {
            if (ioFailure || r.m_EChatRoomEnterResponse != 1)
            {
                Status = "could not join (code wrong or the host left)";
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
            if ((u.m_rgfChatMemberStateChange & (uint)EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0)
            {
                CoopBehaviour.Log("lobby: " + u.m_ulSteamIDUserChanged + " joined");
                if (OnMember != null) OnMember(u.m_ulSteamIDUserChanged);
            }
        }

        void SyncMembers()
        {
            int n = SteamMatchmaking.GetNumLobbyMembers(Id);
            for (int i = 0; i < n; i++)
            {
                ulong m = SteamMatchmaking.GetLobbyMemberByIndex(Id, i).m_SteamID;
                if (OnMember != null) OnMember(m);
            }
        }

        // ---- rooms: who may join, and the public list ----------------------------------------------------------------------------------

        string Data(string key) { return InLobby ? SteamMatchmaking.GetLobbyData(Id, key) : ""; }
        public string Mode { get { return RoomRules.Mode(Data("mode")); } }
        public bool IsOwner { get { return InLobby && HostId == SteamUser.GetSteamID().m_SteamID && OwnerId() == HostId; } }

        // May I connect to this member of the room?
        public bool Allowed(ulong user)
        {
            return InLobby && IsMember(user) && RoomRules.Allowed(Data("mode"), HostId, user, Data("ok"), Data("no"));
        }

        // true once the host has said yes or no to this player
        public bool Decided(ulong user) { return RoomRules.ParseIds(Data("ok")).Contains(user) || RoomRules.ParseIds(Data("no")).Contains(user); }

        public void Approve(ulong user) { if (IsOwner) SteamMatchmaking.SetLobbyData(Id, "ok", RoomRules.AddId(Data("ok"), user)); }
        public void Deny(ulong user) { if (IsOwner) SteamMatchmaking.SetLobbyData(Id, "no", RoomRules.AddId(Data("no"), user)); }

        // What the room says about me: 0 = fine, 1 = waiting for the host, 2 = the host refused me
        public int MyAdmission()
        {
            if (!InLobby || IsOwner) return 0;
            ulong me = SteamUser.GetSteamID().m_SteamID;
            if (RoomRules.ParseIds(Data("no")).Contains(me)) return 2;
            return RoomRules.Allowed(Data("mode"), HostId, me, Data("ok"), Data("no")) ? 0 : 1;
        }

        // Look again at who is in the room (people who joined, a host who has just said yes).
        public void Poll() { if (InLobby && RoomRules.HostChanged(HostId, OwnerId())) { Leave(); Status = "the host left the room"; CoopBehaviour.Log("lobby: the host left, room closed"); return; } if (InLobby) { IsOpen = Mode == RoomRules.Open; SyncMembers(); } }

        public void RequestRooms()
        {
            if (Listing) return;
            Listing = true; Status = "looking for rooms...";
            SteamMatchmaking.AddRequestLobbyListStringFilter("gh", "1", ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(20);
            listed.Set(SteamMatchmaking.RequestLobbyList());
        }

        void OnListed(LobbyMatchList_t r, bool ioFailure)
        {
            Listing = false; Rooms.Clear();
            if (ioFailure) { Status = "could not get the room list"; return; }
            for (int i = 0; i < (int)r.m_nLobbiesMatching && i < 20; i++)
            {
                var id = SteamMatchmaking.GetLobbyByIndex(i);
                Rooms.Add(new Room { Id = id.m_SteamID, Ask = RoomRules.Mode(SteamMatchmaking.GetLobbyData(id, "mode")) != RoomRules.Open, Name = RoomRules.RoomName(SteamMatchmaking.GetLobbyData(id, "name")), Players = SteamMatchmaking.GetNumLobbyMembers(id), Max = SteamMatchmaking.GetLobbyMemberLimit(id) });
            }
            Status = Rooms.Count == 0 ? "no rooms right now" : Rooms.Count + " room(s)";
        }

        // The lobby owner is the player who made the code: the host whose world everybody plays in (0 when not in a lobby).
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
