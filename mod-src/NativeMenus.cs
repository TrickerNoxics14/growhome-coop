using System;
using System.Collections.Generic;
using UnityEngine;

namespace CoopMod
{
    // MAKE A ROOM (F2) and JOIN A ROOM (F3) as pages of the co-op menu, which is a copy of the game's own Settings menu (CoopMenu.cs).
    // They used to be windows drawn over the game. What they do is unchanged: the same three kinds of room (and keys 1/2/3, Enter = the room the
    // settings pick), the same 20-letter upper-case code, a bad code leaves the page open with the reason, 5 listed rooms, REFRESH, Esc cancels,
    // the room list is asked for on opening, and following the leader closes the page.
    public partial class CoopBehaviour
    {
        public const string EntryHotkey = "hotkey", EntryMenu = "menu";
        string pageEntry = "";          // how the page was opened: by its key (BACK closes the menu) or from the co-op menu (BACK goes back to it)
        int pageReturn = CoopMenu.ScreenParty;    // the screen of the co-op menu BACK returns to (F2 / F3 pressed on its SPEEDRUN screen: that one)
        bool pageLive;                  // the page stays across a menu stacked on top of it (the pause menu) and closed again
        int roomPage, roomDefault = 1;
        public int? RoomDefaultForTest;
        public List<Room> RoomsForTest;
        public int RefreshCountForTest;
        readonly Dictionary<ulong, string> pendingNamesForTest = new Dictionary<ulong, string>();

        bool PageShowing(string page) { return menuPage == page && CoopMenu.Showing && CoopMenu.ScreenNow == CoopMenu.ScreenParty; }
        bool joinOpen { get { return PageShowing(CoopMenu.PageJoin); } }
        bool hostMenuOpen { get { return PageShowing(CoopMenu.PageHost); } }

        public string MenuPageNow { get { return menuPage; } }
        public string PageEntryForTest { get { return pageEntry; } }
        public bool OnTypingPage { get { return menuPage == CoopMenu.PageHost || menuPage == CoopMenu.PageJoin; } }
        public string JoinTextNow { get { return joinText; } }
        public void SetJoinText(string t) { joinText = (t ?? "").ToUpperInvariant(); }
        public bool HostMenuOpenForTest { get { return hostMenuOpen; } }

        List<Room> RoomsNow() { return RoomsForTest ?? (lobby != null ? lobby.Rooms : new List<Room>()); }

        // the menu closed (not just hidden under another one): no page is left open
        public void MenuClosed() { menuPage = ""; pageEntry = ""; pageLive = false; pageReturn = CoopMenu.ScreenParty; }

        // BACK / cancel on a page. True = close the whole menu (the page was opened by its key), false = back to the menu's main page.
        public bool LeavePage()
        {
            bool close = pageEntry == EntryHotkey;
            menuPage = ""; pageEntry = ""; pageLive = false;
            if (!close) CoopMenu.ScreenNow = pageReturn;      // back to the screen the page was opened over
            pageReturn = CoopMenu.ScreenParty;
            return close;
        }

        // the page is done (a room picked, a code that decodes): it closes the way BACK would
        void PageDone()
        {
            var h = CoopMenu.Handler;
            if (!CoopMenu.Showing || h == null) { menuPage = ""; pageEntry = ""; pageLive = false; return; }
            h.StopTyping();
            if (LeavePage()) CoopMenu.Menu.Close();
            else h.Refresh(false);
        }

        void CloseCoopPage() { PageDone(); }

        // the party is starting: the page gives way, the menu stays
        void LeavePageNow()
        {
            menuPage = ""; pageEntry = ""; pageLive = false;
            var h = CoopMenu.Handler;
            if (h != null && CoopMenu.Showing) { h.StopTyping(); h.Refresh(false); }
        }

        void SetPage(string page, string entry)
        {
            menuPage = page; pageEntry = entry; pageLive = true;
            if (page == CoopMenu.PageJoin)
            {
                joinText = ""; joinMsg = ""; roomPage = 0;
                if (lobby != null) lobby.RequestRooms();
            }
            if (page == CoopMenu.PageHost) roomDefault = RoomDefaultForTest ?? RoomRules.RoomChoice(ReadSmallFile(Here("coop-roomdefault.txt")));
        }

        // F2 / F3. In the world it opens the co-op menu on that page (the game keeps running, as with the old window); over the pause menu or the start
        // menu it stacks on them (BACK returns to them); while the co-op menu is up it switches page, and the same key again closes it.
        void OpenPage(string page)
        {
            if (!Singleton<GUIManager>.Exists || FadeScreen.FadeInProgress || CoopMenu.Menu == null) return;
            var h = CoopMenu.Handler;
            if (h == null || following || hostStarting) return;
            if (CoopMenu.Showing)
            {
                if (CoopMenu.ScreenNow == CoopMenu.ScreenParty && menuPage == page) { h.BackForTest(); return; }
                if (Mod_BlockedByRace()) return;
                if (menuPage.Length == 0) pageReturn = CoopMenu.ScreenNow;      // a page over the SPEEDRUN screen: BACK goes back to it
                CoopMenu.ScreenNow = CoopMenu.ScreenParty;
                SetPage(page, pageEntry == EntryHotkey ? EntryHotkey : EntryMenu);
                GUIMonoBehaviour.PlayPositiveClickSound();
                h.Refresh(false); h.Unsettle();
                return;
            }
            var active = CoopMenu.ActiveGameMenu();
            if (active != null && active.name != "PauseMenu" && active.name != "StartMenu") return;      // some other game screen is up (it keeps the keys)
            if (active == null && !WorldSync.InWorld()) return;                                          // loading, intro screens: nothing to open over
            GUIMonoBehaviour.PlayPositiveClickSound();
            CoopMenu.ScreenNow = CoopMenu.ScreenParty; pageReturn = CoopMenu.ScreenParty;
            Singleton<GUIManager>.Instance.ActivateMenu(CoopMenu.Menu);
            if (!CoopMenu.Showing) return;
            SetPage(page, EntryHotkey);
            h.Refresh(false); h.Unsettle();        // the key that opened it is no Submit: the first Enter may pick at once, as in the old window
        }

        bool Mod_BlockedByRace() { return RaceArmed; }

        // What the lines of the two pages do (true = it was one of theirs).
        bool PageAction(string id)
        {
            if (menuPage == CoopMenu.PageHost)
            {
                if (id == "host_public") { MakeRoom(true, true); PageDone(); return true; }
                if (id == "host_accept") { MakeRoom(false, true); PageDone(); return true; }
                if (id == "host_private") { MakeRoom(false, false); PageDone(); return true; }
                return false;
            }
            if (menuPage != CoopMenu.PageJoin) return false;
            if (id == "join_go") { SubmitJoin(); return true; }
            if (id == "rooms_more") { roomPage = (roomPage + 1) % CoopMenu.RoomPages(RoomsNow().Count); return true; }
            if (id == "rooms_refresh") { RefreshCountForTest++; if (lobby != null) lobby.RequestRooms(); return true; }
            ulong room = CoopMenu.RoomIdOf(id);
            if (room != 0)
            {
                bool listed = false;
                var rooms = RoomsNow();
                for (int i = 0; i < rooms.Count && i < CoopMenu.RoomsShown; i++) if (rooms[i].Id == room) listed = true;
                if (listed) { JoinLobby(room); PageDone(); }
                return true;
            }
            return false;
        }

        // The mod's keys, read in Update; the audit runs the very same code.
        void HandleKey(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.F2: if (Online) MakeInviteCode(); break;
                case KeyCode.F3: if (Online) OpenJoinBox(); break;
                case KeyCode.T: if (Online && !chatOpen && !joinOpen && !hostMenuOpen && !CoopField.AnyFocused && WorldSync.InWorld() && !CoopHud.InMenu) OpenChat(); break;      // under a game menu (the pause menu) the menu keeps its keys and its selection
                case KeyCode.Y:
                case KeyCode.N:
                    if (pending.Count > 0 && !chatOpen && !joinOpen && !hostMenuOpen && !CoopField.AnyFocused) AnswerJoinRequest(k == KeyCode.Y);
                    break;
            }
        }

        public void HotkeyForTest(KeyCode k) { HandleKey(k); }

        // a fake player asking to join (with AnswerSinkForTest no real answer reaches Steam)
        public void AddPendingForTest(ulong id, string name) { if (!pending.Contains(id)) pending.Add(id); pendingNamesForTest[id] = name; }
        public void ClearPendingForTest() { foreach (var id in pendingNamesForTest.Keys) pending.Remove(id); pendingNamesForTest.Clear(); }
        public string PendingNameForTest { get { return PendingName(); } }
        public int PendingCountRaw { get { return pending.Count; } }
        public bool JoinRequestWanted { get { return pending.Count > 0 && !joinOpen && !hostMenuOpen && !CoopUI.Hidden; } }
        public string JoinMsgForTest { get { return joinMsg; } }
        public int PageReturnForTest { get { return pageReturn; } }
        public void JoinDidNotWorkForTest() { if (lobby != null) lobby.JoinDidNotWork(); }
        public string LobbyStatusForTest { get { return lobby == null ? "" : lobby.Status; } set { if (lobby != null) lobby.Status = value; } }
        public bool HasLobbyForTest { get { return lobby != null; } }
    }
}
