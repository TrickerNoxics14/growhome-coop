using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CoopMod
{
    // A real game menu for co-op, opened from a CO-OP PARTY line in the start menu and a CO-OP line in the pause menu. It is the game's own Settings menu
    // copied at runtime (same frame, font, sounds, mouse and controller navigation, glitch effect and camera behaviour), with its lines replaced by the
    // co-op actions. The lines follow the state live. On the start screen it is the party lobby: the members of the party with who is ready, invite
    // friends, ready up, and the leader's START GAME that takes everybody in. In the game: the party's code, chat, re-join, leave.
    // MAKE A ROOM (F2) and JOIN A ROOM (F3) are pages of this same menu; the invite code is typed into a line of it (a uGUI InputField, CoopField.cs).
    public static class CoopMenu
    {
        public const string MenuName = "CoopMenu";
        public const string EntryName = "ButtonCoop";
        public const string StartEntryName = "ButtonParty";
        public const int MaxRows = 7;            // lines that do something, BACK included; the member list comes on top of them

        public struct State
        {
            public bool Online, InLobby, Busy, IsHost, InWorld, HostConnected, DisplayHidden;
            public bool AtStart, Following, Ready, HostPlaying, Waiting;
            public string Code, Pending, Players, Status, Invite, Page, Leader;
            public List<Party.Member> Members;
            public List<Party.Friend> Friends;
            public int FriendPage;
            // the SPEEDRUN page (Speedrun.FillMenuState)
            public int Screen;
            public string SrCategory, SrMode, SrRunState, SrTime, SrInvalidWhy, SrPb;
            public bool SrActive, SrRunning, SrFinished, SrValid, SrSplitsShown, SrLiveSplit, SrTimerHidden, SrIsMember, SrInParty;
            public bool SrRaceArmed, SrRaceHost, SrCountdown; public int SrRaceReady, SrRaceTotal;
            // the MAKE A ROOM / JOIN A ROOM pages
            public string JoinMsg; public bool Listing; public List<Room> Rooms; public int RoomPage, RoomDefault;
            // the in-world main page: the co-op keys (they used to be on the status panel drawn over the game), one menu line per '|'
            public string Keys;
        }
        public const string KeysSolo = "F2 MAKE A ROOM  F3 JOIN A ROOM|T CHAT  F1 HIDE THE DISPLAY", KeysLinked = "F2 CODE  F3 JOIN  T CHAT|F7 RE-JOIN  F4 FIND PLAYER";

        public const string PageHost = "host", PageJoin = "join";
        public const int RoomsShown = 5, RoomsPerPage = 3;     // the 5 rooms the old window listed, 3 to a page
        public const string CodeAllowed = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789- ";
        public const int CodeLimit = 20;

        // the line of the MAKE A ROOM page the room setting picks (1 public, 4 listed and I accept, 2 private)
        public static string HostRowFor(int choice) { return choice == 4 ? "host_accept" : choice == 2 ? "host_private" : "host_public"; }

        // the old window's subtitles, now in the menu's own subtitle line, for the line that is selected
        public static string HostHint(string id)
        {
            switch (id)
            {
                case "host_public": return "LISTED FOR EVERYBODY. ANYONE CAN JOIN";
                case "host_accept": return "LISTED, BUT EACH PLAYER WAITS FOR YOUR YES";
                case "host_private": return "NOT LISTED. CODE ONLY. YOU ACCEPT EACH PLAYER";
                case "back": return "NO ROOM IS MADE";
            }
            return "PICK THE KIND OF ROOM";
        }

        public static string RoomLabel(Room r)
        {
            if (r == null) return "";
            return Party.Short((r.Name ?? "").ToUpperInvariant(), Party.NameLength) + "  " + (r.Ask ? "ASK " : "") + r.Players + "/" + r.Max;
        }

        // a message cut to the subtitle line at a word break
        public static string Fit(string s, int max)
        {
            s = (s ?? "").ToUpperInvariant().Trim();
            if (s.Length <= max) return s;
            int cut = s.LastIndexOf(' ', max);
            return (cut > max / 2 ? s.Substring(0, cut) : s.Substring(0, max)).TrimEnd(' ', '.', ',');
        }

        static List<Row> HostRows(State s, out string line1, out string line2)
        {
            line1 = "MAKE A ROOM";
            line2 = HostHint(HostRowFor(s.RoomDefault));
            return new List<Row> { new Row("host_public", "PUBLIC ROOM"), new Row("host_accept", "LISTED, YOU ACCEPT"), new Row("host_private", "PRIVATE ROOM"), new Row("back", "BACK") };
        }

        static List<Row> JoinRows(State s, out string line1, out string line2)
        {
            var all = s.Rooms ?? new List<Room>();
            int count = Math.Min(all.Count, RoomsShown);
            int pages = Math.Max(1, (count + RoomsPerPage - 1) / RoomsPerPage), page = Math.Min(Math.Max(s.RoomPage, 0), pages - 1);
            line1 = "JOIN A ROOM";
            line2 = !string.IsNullOrEmpty(s.JoinMsg) ? Fit(s.JoinMsg, 44) : s.Listing ? "LOOKING..." : count == 0 ? "NO ROOMS RIGHT NOW" : "TYPE A CODE OR PICK A ROOM";
            var code = new Row("join_code", "CODE>"); code.Text = true;
            var rows = new List<Row> { code, new Row("join_go", "JOIN WITH CODE") };
            for (int i = page * RoomsPerPage; i < count && i < (page + 1) * RoomsPerPage; i++) rows.Add(new Row("room:" + all[i].Id, RoomLabel(all[i])));
            if (pages > 1) rows.Add(new Row("rooms_more", "MORE ROOMS  " + (page + 1) + "/" + pages));
            rows.Add(new Row("rooms_refresh", "REFRESH"));
            rows.Add(new Row("back", "BACK"));
            return rows;
        }

        public static int RoomPages(int rooms) { int count = Math.Min(rooms, RoomsShown); return Math.Max(1, (count + RoomsPerPage - 1) / RoomsPerPage); }

        // the room a JOIN A ROOM line names (0 when it is no room line)
        public static ulong RoomIdOf(string id) { ulong v; return id != null && id.StartsWith("room:") && ulong.TryParse(id.Substring(5), out v) ? v : 0UL; }

        public const int ScreenParty = 0, ScreenSpeedrun = 1;
        public static int ScreenNow;                 // which page the one copied menu shows: the party / co-op lines or the speedrun lines
        public const string SpeedrunStartName = "ButtonSpeedrun", SpeedrunPauseName = "ButtonSpeedrunPause";
        public static Button SpeedrunStartEntry, SpeedrunPauseEntry;

        // The SPEEDRUN page. On the start screen: pick the category and mode, start; in the game: reset, end, hide the timer. Pure like RowsFor.
        public static List<Row> SpeedrunRows(State s, out string line1, out string line2)
        {
            var rows = new List<Row>();
            if (s.AtStart)
            {
                line1 = "NEW GAME IN A SPEEDRUN SAVE";
                line2 = "YOUR OWN SAVE IS NOT TOUCHED";
                if (s.SrCountdown)
                {
                    line1 = "THE RACE IS STARTING";
                    line2 = "GET READY";
                    rows.Add(new Row("sr_info", "RACE: " + s.SrCategory, true));
                }
                else if (s.SrRaceArmed && s.SrRaceHost)
                {
                    line1 = "WAITING FOR RACERS " + s.SrRaceReady + "/" + s.SrRaceTotal;
                    line2 = "EVERYBODY GETS A FRESH SPEEDRUN SAVE";
                    rows.Add(new Row("sr_info", "RACE: " + s.SrCategory, true));
                    rows.Add(new Row("sr_go", "START RACE NOW " + s.SrRaceReady + "/" + s.SrRaceTotal + " READY"));
                    rows.Add(new Row("sr_cancel", "CANCEL RACE"));
                }
                else if (s.SrRaceArmed)
                {
                    line1 = "RACE ARMED: " + s.SrCategory;
                    line2 = "YOUR OWN SAVE IS NOT TOUCHED";
                    rows.Add(new Row("sr_info", "WAITING FOR THE LEADER", true));
                    rows.Add(new Row("sr_leave", "LEAVE THE RACE"));
                }
                else if (s.SrIsMember) rows.Add(new Row("sr_info", "LEADER PICKS: " + s.SrMode + " " + s.SrCategory, true));
                else
                {
                    rows.Add(new Row("sr_cat", "CATEGORY: " + s.SrCategory));
                    rows.Add(new Row("sr_mode", "MODE: " + s.SrMode));
                    rows.Add(new Row("sr_start", s.SrMode == "RACE" ? "START RACE" : "START RUN"));
                }
                rows.Add(new Row("sr_pb", string.IsNullOrEmpty(s.SrPb) ? "NO PB YET" : "PB " + s.SrPb, true));
                rows.Add(new Row("sr_splits", s.SrSplitsShown ? "SPLITS: SHOW" : "SPLITS: HIDE"));
                rows.Add(new Row("sr_livesplit", s.SrLiveSplit ? "LIVESPLIT: ON" : "LIVESPLIT: OFF"));
            }
            else if (s.SrRunning)
            {
                line1 = s.SrCategory + "  " + s.SrTime;
                line2 = s.SrValid ? "RUN VALID" : "INVALID: " + s.SrInvalidWhy;
                rows.Add(new Row("sr_reset", "RESET RUN"));
                if (s.SrMode == "SOLO") rows.Add(new Row("sr_restart", "RESET AND RESTART"));
                rows.Add(new Row("sr_end", "END RUN (DNF)"));
                if (s.SrMode == "RACE" && s.SrRaceHost) rows.Add(new Row("sr_endrace", "END RACE"));
                rows.Add(new Row("sr_hide", s.SrTimerHidden ? "TIMER: HIDE" : "TIMER: SHOW"));
            }
            else if (s.SrActive)
            {
                line1 = (s.SrFinished && s.SrRunState == "FINISHED" ? "RUN FINISHED  " : "RUN ENDED  ") + s.SrTime;
                line2 = "SAVE AND QUIT LEAVES THE SPEEDRUN SAVE";
                rows.Add(new Row("sr_quit", "SAVE AND QUIT"));
                rows.Add(new Row("sr_hide", s.SrTimerHidden ? "TIMER: HIDE" : "TIMER: SHOW"));
            }
            else
            {
                line1 = "NO SPEEDRUN NOW";
                line2 = "START ONE FROM THE START MENU";
            }
            var capped = new List<Row>();
            int actions = 0;
            foreach (var r in rows) { if (r.Inert) capped.Add(r); else if (actions < MaxRows - 1) { capped.Add(r); actions++; } }
            capped.Add(new Row("back", "BACK"));
            if (line1.Length > 34) line1 = line1.Substring(0, 34);
            if (line2.Length > 44) line2 = line2.Substring(0, 44);
            return capped;
        }

        public struct Row
        {
            public string Id, Label; public bool Inert;     // inert: a line of the member list, shown but never selected or pressed
            public bool Text;                               // a line you type into (the invite code)
            public Row(string id, string label) { Id = id; Label = label; Inert = false; Text = false; }
            public Row(string id, string label, bool inert) { Id = id; Label = label; Inert = inert; Text = false; }
        }

        // The lines for a state: member lines first, then at most MaxRows lines that do something, BACK always last. Pure, so the audit checks every state.
        public static List<Row> RowsFor(State s, out string line1, out string line2)
        {
            if (s.Screen == ScreenSpeedrun) return SpeedrunRows(s, out line1, out line2);
            if (s.Online && !s.Following && s.Page == PageJoin) return JoinRows(s, out line1, out line2);
            if (s.Online && !s.Following && s.Page == PageHost && !s.InLobby) return HostRows(s, out line1, out line2);
            var rows = new List<Row>();
            string leader = Party.Short(string.IsNullOrEmpty(s.Leader) ? "THE LEADER" : s.Leader, Party.NameLength);
            if (!s.Online)
            {
                line1 = s.AtStart ? "PARTIES NEED STEAM" : "CO-OP NEEDS STEAM";
                line2 = "OPEN STEAM, THEN START FROM THE CO-OP LAUNCHER";
            }
            else if (s.Following)
            {
                line1 = s.IsHost ? "STARTING THE GAME" : "JOINING " + leader + "'S GAME";
                line2 = s.IsHost ? "YOUR PARTY FOLLOWS YOU IN" : "LOADING THEIR WORLD";
            }
            else if (s.InLobby && s.Page == "friends")
            {
                var f = s.Friends ?? new List<Party.Friend>();
                int pages = Party.Pages(f.Count), page = Math.Min(Math.Max(s.FriendPage, 0), pages - 1);
                line1 = "INVITE FRIENDS";
                line2 = f.Count == 0 ? "NO FRIENDS ONLINE RIGHT NOW" : f.Count + " ONLINE" + (pages > 1 ? "  PAGE " + (page + 1) + " OF " + pages : "");
                for (int i = page * Party.FriendsPerPage; i < f.Count && i < (page + 1) * Party.FriendsPerPage; i++)
                    rows.Add(new Row("invite:" + f[i].Id, (f[i].Invited ? "INVITED " : "INVITE ") + Party.Short(f[i].Name, Party.NameLength)));
                if (pages > 1) rows.Add(new Row("friends_more", "MORE FRIENDS"));
            }
            else if (!s.InLobby && s.Busy)
            {
                line1 = "CONNECTING TO STEAM";
                line2 = string.IsNullOrEmpty(s.Status) ? "ONE MOMENT" : s.Status;
            }
            else if (!s.InLobby)
            {
                line1 = s.AtStart ? "NO PARTY YET" : string.IsNullOrEmpty(s.Players) ? "NOT IN A PARTY" : "WITH " + s.Players;
                line2 = !string.IsNullOrEmpty(s.Status) ? s.Status : s.AtStart ? "MAKE A PARTY, THEN INVITE FRIENDS" : "HOST A PARTY OR JOIN ONE";
                if (!string.IsNullOrEmpty(s.Invite)) rows.Add(new Row("accept_invite", "JOIN " + Party.Short(s.Invite, 12) + "'S PARTY"));
                rows.Add(new Row("host_friends", "HOST FRIENDS PARTY"));
                rows.Add(new Row("host_public", "HOST PUBLIC PARTY"));
                if (!s.AtStart)
                {
                    rows.Add(new Row("host_accept", "HOST PARTY, I ACCEPT"));
                    rows.Add(new Row("host_private", "HOST PRIVATE PARTY"));
                }
                rows.Add(new Row("join", "JOIN A PARTY"));
            }
            else
            {
                line1 = (s.IsHost ? "YOUR PARTY  CODE " : "PARTY  CODE ") + s.Code;
                var m = s.Members ?? new List<Party.Member>();
                int ready = Party.ReadyCount(m);
                if (s.AtStart)
                {
                    line2 = s.Waiting ? "WAITING FOR " + leader + " TO LET YOU IN"
                          : m.Count + (m.Count == 1 ? " PLAYER" : " PLAYERS") + " IN THE PARTY" + (m.Count > 1 ? ", " + ready + " READY" : "");
                    int shown = m.Count <= Party.MaxShown ? m.Count : Party.MaxShown - 1;
                    for (int i = 0; i < shown; i++) rows.Add(new Row("member", Party.RosterLabel(m[i]), true));
                    if (m.Count > shown) rows.Add(new Row("more", "+" + (m.Count - shown) + " MORE", true));
                }
                else line2 = string.IsNullOrEmpty(s.Players) ? (string.IsNullOrEmpty(s.Status) ? "WAITING FOR PLAYERS" : s.Status) : "WITH " + s.Players;
                if (!string.IsNullOrEmpty(s.Pending))
                {
                    rows.Add(new Row("let_in", "LET IN " + s.Pending));
                    rows.Add(new Row("refuse", "REFUSE " + s.Pending));
                }
                if (s.AtStart)
                {
                    if (s.IsHost) rows.Add(new Row("start", m.Count > 1 ? "START GAME  " + ready + "/" + (m.Count - 1) + " READY" : "START GAME"));
                    else if (!s.Waiting) rows.Add(s.HostPlaying ? new Row("follow", "JOIN THE GAME") : new Row("ready", s.Ready ? "READY: YES" : "READY: NO"));
                }
                rows.Add(new Row("friends", "INVITE FRIENDS"));
                rows.Add(new Row("copy", "COPY INVITE CODE"));
                if (!s.AtStart)
                {
                    if (s.InWorld) rows.Add(new Row("chat", "CHAT"));
                    if (!s.IsHost && s.HostConnected && s.InWorld) rows.Add(new Row("rejoin", "RE-JOIN HOST WORLD"));
                }
                rows.Add(new Row("leave", "LEAVE PARTY"));
            }
            if (!string.IsNullOrEmpty(s.Keys) && s.Online && !s.AtStart && !s.Following && !s.Busy && string.IsNullOrEmpty(s.Page)) { var k = s.Keys.Split('|'); for (int i = k.Length - 1; i >= 0; i--) rows.Insert(0, new Row("keys", k[i], true)); }
            if (s.Online && !s.AtStart && !s.Following && !(s.InLobby && s.Page == "friends"))
                rows.Add(new Row("display", s.DisplayHidden ? "CO-OP DISPLAY: OFF" : "CO-OP DISPLAY: ON"));
            var capped = new List<Row>();
            int actions = 0;
            foreach (var r in rows) { if (r.Inert) capped.Add(r); else if (actions < MaxRows - 1) { capped.Add(r); actions++; } }
            capped.Add(new Row("back", "BACK"));
            if (line1.Length > 34) line1 = line1.Substring(0, 34);
            if (line2.Length > 44) line2 = line2.Substring(0, 44);
            return capped;
        }

        // ---- building it into the game's menus (once for every GUIManager, i.e. every level) ------------------------------------------

        static GUIManager builtFor;
        public static UnityGUIMenu Menu;
        public static Button Entry, StartEntry;
        public static int Builds;
        public static string LastProblem = "";

        public static void Tick(CoopBehaviour mod)
        {
            if (!Singleton<GUIManager>.Exists) return;
            var gm = Singleton<GUIManager>.Instance;
            if (gm == builtFor) { PauseEntryTick(); return; }
            if (gm.m_Menus == null || gm.m_Menus.Count == 0) return;
            builtFor = gm;
            try { Build(gm, mod); Builds++; CoopBehaviour.Log("ui: CO-OP menu added to the pause menu" + (StartEntry != null ? " and the start menu" : "")); }
            catch (Exception e) { LastProblem = SharedWorld.Short(e); CoopBehaviour.Log("ui: could not add the CO-OP menu (the F2/F3 keys still work): " + LastProblem); }
        }

        public static GUIMenu Find(GUIManager gm, string name) { foreach (var m in gm.m_Menus) if (m != null && m.name == name) return m; return null; }

        static Transform Child(Transform t, string path) { var c = t.Find(path); if (c == null) throw new Exception("no " + path + " under " + t.name); return c; }

        static void Build(GUIManager gm, CoopBehaviour mod)
        {
            Menu = null; Entry = null; StartEntry = null;
            if (Find(gm, MenuName) != null) return;
            var options = Find(gm, "OptionsMenu");
            var pause = Find(gm, "PauseMenu");
            if (options == null || pause == null) throw new Exception("no " + (options == null ? "OptionsMenu" : "PauseMenu") + " in this level");

            // 1. the menu: a copy of Settings with its own handler
            var go = (GameObject)UnityEngine.Object.Instantiate(options.gameObject);
            go.SetActive(false);
            go.name = MenuName;
            go.transform.SetParent(options.transform.parent, false);
            go.transform.localPosition = options.transform.localPosition; go.transform.localRotation = options.transform.localRotation; go.transform.localScale = options.transform.localScale;
            foreach (var h in go.GetComponents<GUIHandler>()) UnityEngine.Object.DestroyImmediate(h);
            foreach (var l in go.GetComponentsInChildren<LocaliseButtonText>(true)) UnityEngine.Object.DestroyImmediate(l);   // it would translate player names; the labels are ours
            var menu = go.GetComponent<UnityGUIMenu>();
            var handler = go.AddComponent<CoopMenuHandler>();
            handler.Mod = mod;
            var root = Child(go.transform, "AspectKeeper");
            handler.Group = Child(root, "MenuGroupPanel");
            handler.Template = Child(handler.Group, "ButtonBack").gameObject;
            handler.Template.SetActive(false);
            for (int i = handler.Group.childCount - 1; i >= 0; i--)
            {
                var c = handler.Group.GetChild(i).gameObject;
                if (c != handler.Template) UnityEngine.Object.DestroyImmediate(c);
            }
            handler.Line1 = Child(root, "TitlePanel/CompanyName").GetComponent<Text>();
            handler.Line2 = Child(root, "TitlePanel/DaysSince").GetComponent<Text>();
            var days = root.Find("TitlePanel/DaysSinceValue"); if (days != null) days.gameObject.SetActive(false);
            var title = root.Find("ButText"); if (title != null) { handler.Title = title.GetComponent<Text>(); handler.Title.text = "CO-OP"; }
            var ver = root.Find("UN11Panel/VersionText"); if (ver != null) ver.GetComponent<Text>().text = Localisation.GetString(ver.GetComponent<Text>().text);
            handler.Setup();
            handler.Menu = menu;
            gm.m_Menus.Add(menu);
            Menu = menu;

            // 2. the way in from the game: a CO-OP line in the pause menu, right under Settings
            var pauseGroup = Child(pause.transform, "AspectKeeper/MenuGroupPanel");
            Entry = AddLine(Child(pauseGroup, "ButtonCamera"), 1, EntryName, "CO-OP", OpenFromPause);

            // 3. the way in from the start screen: a CO-OP PARTY line in the start menu, right above Settings (a level without it still has the pause line)
            try
            {
                var start = Find(gm, "StartMenu");
                if (start != null) StartEntry = AddLine(Child(start.transform, "Panel/ButtonPanel/SettingsButton"), 0, StartEntryName, "CO-OP PARTY", OpenFromStart);
            }
            catch (Exception e) { LastProblem = "start menu: " + SharedWorld.Short(e); CoopBehaviour.Log("ui: no CO-OP PARTY line in the start menu: " + LastProblem); }

            // 4. speedruns: SPEEDRUN above CO-OP PARTY in the start menu, and under CO-OP in the pause menu while a run is in its save slot
            SpeedrunStartEntry = null; SpeedrunPauseEntry = null;
            try
            {
                if (StartEntry != null) SpeedrunStartEntry = AddLine(StartEntry.transform, 0, SpeedrunStartName, "SPEEDRUN", OpenSpeedrunFromStart);
                SpeedrunPauseEntry = AddLine(Entry.transform, 1, SpeedrunPauseName, "SPEEDRUN", OpenSpeedrunFromPause);
                SpeedrunPauseEntry.gameObject.SetActive(SpeedrunLineWanted);
            }
            catch (Exception e) { LastProblem = "speedrun lines: " + SharedWorld.Short(e); CoopBehaviour.Log("ui: no SPEEDRUN line: " + LastProblem); }

            // 5. the notice line at the bottom of the start and pause menus: what co-op has to say while they are up (MenuNotice)
            try { MenuNotice.Build(Find(gm, "StartMenu"), pause); }
            catch (Exception e) { LastProblem = "notice line: " + SharedWorld.Short(e); CoopBehaviour.Log("ui: no notice line in the start / pause menu: " + LastProblem); }
        }

        public static void OpenSpeedrunFromStart()
        {
            if (Menu == null || !Singleton<GUIManager>.Exists || FadeScreen.FadeInProgress) return;
            GUIMonoBehaviour.PlayPositiveClickSound();
            ScreenNow = ScreenSpeedrun;
            Singleton<GUIManager>.Instance.ActivateMenu(Menu);
        }

        public static void OpenSpeedrunFromPause()
        {
            if (Menu == null || !Singleton<GUIManager>.Exists || FadeScreen.FadeInProgress) return;
            GUIMonoBehaviour.PlayPositiveClickSound();
            ScreenNow = ScreenSpeedrun;
            Singleton<GUIManager>.Instance.ActivateMenu(Menu);
        }

        // the pause menu's SPEEDRUN line is there only while a run is live (after END RUN the game's own Save & Quit takes me out)
        public static bool SpeedrunPauseForTest;
        public static bool SpeedrunLineWanted { get { return SpeedrunPauseForTest || (Speedrun.SlotActive && Speedrun.Current != null && Speedrun.Current.Live); } }
        static void PauseEntryTick()
        {
            if (SpeedrunPauseEntry == null) return;
            bool want = SpeedrunLineWanted;
            if (SpeedrunPauseEntry.gameObject.activeSelf != want) SpeedrunPauseEntry.gameObject.SetActive(want);
            FitPauseRows();
        }

        // The pause panel holds 6 lines of 50; with CO-OP, the notice line's place and SPEEDRUN it has 7 or 8, so the lines get a little lower instead of running out of the panel.
        static Transform fitGroup; static int fitCount = -1;
        static readonly List<LayoutElement> fitRows = new List<LayoutElement>();
        static readonly List<float> fitMin = new List<float>(), fitPref = new List<float>();
        public static float PauseRowHeight = 50f;
        static void FitPauseRows()
        {
            var group = SpeedrunPauseEntry == null ? null : SpeedrunPauseEntry.transform.parent;
            if (group == null) return;
            if (group != fitGroup)
            {
                fitGroup = group; fitCount = -1; fitRows.Clear(); fitMin.Clear(); fitPref.Clear();
                for (int i = 0; i < group.childCount; i++)
                {
                    var le = group.GetChild(i).GetComponent<LayoutElement>();
                    if (le == null) continue;
                    fitRows.Add(le); fitMin.Add(le.minHeight); fitPref.Add(le.preferredHeight);
                }
            }
            int count = 0;
            // the notice line (MenuNotice) always has its place counted, shown or not: it is out of the layout and sits under the last row, so the
            // rows never move when a message comes or goes (a resting mouse pointer would otherwise hover, and select, another row)
            for (int i = 0; i < group.childCount; i++) if (group.GetChild(i).gameObject.activeSelf || group.GetChild(i).name == MenuNotice.LineName) count++;
            if (count == fitCount) return;
            fitCount = count;
            var rt = group as RectTransform;
            float panel = rt != null ? rt.rect.height : 343f;
            float full = 0f; foreach (float p in fitPref) full = Mathf.Max(full, p);
            if (full <= 0f) full = 50f;
            float h = count > 0 && count * full > panel ? Mathf.Floor(panel / count) : -1f;
            PauseRowHeight = h > 0f ? h : full;
            for (int i = 0; i < fitRows.Count; i++)
            {
                if (fitRows[i] == null) continue;
                fitRows[i].minHeight = h > 0f ? Mathf.Min(fitMin[i], h) : fitMin[i];
                fitRows[i].preferredHeight = h > 0f ? h : fitPref[i];
            }
        }

        // a copy of one of the menu's own lines, placed next to it (offset 0 = above, 1 = below), with its own label and action
        static Button AddLine(Transform like, int offset, string name, string text, UnityEngine.Events.UnityAction action)
        {
            var line = (GameObject)UnityEngine.Object.Instantiate(like.gameObject);
            line.name = name;
            line.transform.SetParent(like.parent, false);
            line.transform.SetSiblingIndex(like.GetSiblingIndex() + offset);
            line.transform.localScale = like.localScale;
            Child(line.transform, "ButText").GetComponent<Text>().text = text;
            var button = line.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(action);
            return button;
        }

        public static void OpenFromPause()
        {
            if (Menu == null || !Singleton<GUIManager>.Exists || FadeScreen.FadeInProgress) return;
            GUIMonoBehaviour.PlayPositiveClickSound();
            ScreenNow = ScreenParty;
            Singleton<GUIManager>.Instance.ActivateMenu(Menu);
        }

        public static void OpenFromStart() { OpenFromStart(true); }

        public static void OpenFromStart(bool sound)
        {
            if (Menu == null || !Singleton<GUIManager>.Exists || FadeScreen.FadeInProgress) return;
            ScreenNow = ScreenParty;
            if (sound) GUIMonoBehaviour.PlayPositiveClickSound();
            Singleton<GUIManager>.Instance.ActivateMenu(Menu);
            var h = Menu.GetComponent<CoopMenuHandler>();
            if (!sound && h != null) h.SettleAll();     // opened by itself: a key meant for the start menu must not press a party line
        }

        public static bool PartyMenuShowing() { return Menu != null && Menu.gameObject.activeInHierarchy && Singleton<StartScreen>.Exists; }

        // the co-op menu is the menu on screen (not hidden under another one stacked on it)
        public static bool Showing { get { return Menu != null && Menu.gameObject.activeInHierarchy && Singleton<GUIManager>.Exists && Singleton<GUIManager>.Instance.AnyMenuActive; } }

        public static CoopMenuHandler Handler { get { return Menu == null ? null : Menu.GetComponent<CoopMenuHandler>(); } }

        // the game menu on screen now, if any (the GUIManager keeps only the top one active)
        public static GUIMenu ActiveGameMenu()
        {
            if (!Singleton<GUIManager>.Exists || !Singleton<GUIManager>.Instance.AnyMenuActive) return null;
            foreach (var m in Singleton<GUIManager>.Instance.m_Menus) if (m != null && m.gameObject.activeInHierarchy) return m;
            return null;
        }

        // The start menu shows CONTINUE / NEW GAME for the save folder in use; after guest mode starts or ends it is set up again (else NEW GAME
        // could clear my own save without the START OVER question).
        public static void RefreshStartMenu()
        {
            try
            {
                if (!Singleton<GUIManager>.Exists) return;
                var start = Find(Singleton<GUIManager>.Instance, "StartMenu");
                if (start == null || start.Handler == null) return;
                start.Handler.ActivateElements();
                // the buttons swapped (CONTINUE / NEW GAME): a selection left on a hidden one would leave keys and pad dead on the start menu
                var es = UnityEngine.EventSystems.EventSystem.current;
                var um = start as UnityGUIMenu;
                if (es != null && um != null && um.m_defaultSelection != null && start.gameObject.activeInHierarchy && StartMenuShowing())
                {
                    var cur = es.currentSelectedGameObject;
                    if (cur == null || !cur.activeInHierarchy) es.SetSelectedGameObject(um.m_defaultSelection);
                }
            }
            catch (Exception e) { CoopBehaviour.Log("ui: start menu refresh failed " + SharedWorld.Short(e)); }
        }

        // the start menu is the menu on screen (not covered by this one or another)
        public static bool StartMenuShowing()
        {
            if (!Singleton<GUIManager>.Exists) return false;
            var start = Find(Singleton<GUIManager>.Instance, "StartMenu");
            return start != null && start.gameObject.activeInHierarchy && (Menu == null || !Menu.gameObject.activeInHierarchy);
        }
    }

    public class CoopMenuHandler : GUIHandler
    {
        public CoopBehaviour Mod;
        public Transform Group;
        public GameObject Template;
        public Text Line1, Line2, Title;

        public const float SettleTime = 0.5f;     // a line that just changed what it does ignores presses this long (a press meant for its old meaning)
        public const float InertHeight = 30f, MinActionHeight = 34f;
        const int InertFont = 26, MinFont = 20;

        class View
        {
            public GameObject Go; public Button Button; public Text Text; public LayoutElement Layout;
            public string Id = "", Label; public float ChangedAt = -99f; public int BaseFont;
        }

        // the invite-code line: a cloned menu line with a CoopField to the right of its "CODE>" label
        View textView; CoopField codeField; RectTransform fieldRect;
        string lastPage = "", hintFor;
        bool typing, escArmed, escHeld, cancelSoundMuted; int escReleaseFrame = -1;
        public static int EscCloses, RowReclaims;      // for the audit
        public CoopField CodeField { get { return codeField; } }
        public GameObject TextRowForTest { get { return textView == null ? null : textView.Go; } }
        public bool TypingForTest { get { return typing; } }
        public static bool? KeyboardModeForTest;

        // M.O.M messages that the game's panel cannot take while this menu is up are said in its subtitle line for 4 s of real time (they used to be
        // drawn over the game); then the subtitle goes back to what the page says.
        public static string NoticeText = "";
        static float noticeUntil = -1f;
        public static bool NoticeUp { get { return Time.realtimeSinceStartup < noticeUntil; } }
        public static bool ShowNotice(string msg)
        {
            var h = CoopMenu.Handler;
            if (!CoopMenu.Showing || h == null || h.Line2 == null) return false;
            NoticeText = CoopMenu.Fit("M.O.M>>> " + msg, 44);
            noticeUntil = Time.realtimeSinceStartup + 4f;
            h.Line2.text = NoticeText; h.hintFor = null;
            return true;
        }
        public static void ClearNotice() { noticeUntil = -1f; }
        public static bool KeyboardMode
        {
            get
            {
                if (KeyboardModeForTest.HasValue) return KeyboardModeForTest.Value;
                try { return !Singleton<InputMethodTracker>.Exists || InputMethodTracker.CurrentMethod != InputMethodTracker.InputMethod.GamePad; } catch (Exception) { return true; }
            }
        }
        string Page { get { return Mod == null ? "" : Mod.MenuPageNow; } }

        readonly List<View> actionPool = new List<View>(), inertPool = new List<View>(), shown = new List<View>();
        string shownKey = "";
        float nextRefresh;
        float actionHeight = 50f, templateMin = -1f, templatePreferred = -1f, templateFlexible = -1f, groupHeight = -1f, groupSizeY;
        bool hasOverride; CoopMenu.State overrideState;

        public float LastActionHeight { get; private set; }
        public bool Grown { get; private set; }
        public List<string> RowIdsForTest { get { var l = new List<string>(); foreach (var v in shown) l.Add(v.Id); return l; } }
        public List<GameObject> RowObjectsForTest { get { var l = new List<GameObject>(); foreach (var v in shown) l.Add(v.Go); return l; } }
        public List<bool> RowInertForTest { get { var l = new List<bool>(); foreach (var v in shown) l.Add(v.Button == null); return l; } }
        public List<Text> RowTextsForTest { get { var l = new List<Text>(); foreach (var v in shown) l.Add(v.Text); return l; } }
        public Button ButtonFor(string id) { foreach (var v in shown) if (v.Id == id && v.Button != null) return v.Button; return null; }
        public void SetStateForTest(CoopMenu.State s) { hasOverride = true; overrideState = s; Refresh(false); }
        public void ClearStateForTest() { hasOverride = false; Refresh(false); }

        // the original row size, read from the line the menu is made of
        public void Setup()
        {
            var le = Template.GetComponent<LayoutElement>();
            if (le != null) { templateMin = le.minHeight; templatePreferred = le.preferredHeight; templateFlexible = le.flexibleHeight; }
            actionHeight = templatePreferred > 0f ? templatePreferred : templateMin > 0f ? templateMin : 50f;
            var rt = (RectTransform)Group;
            groupSizeY = rt.sizeDelta.y;
            var vlg = Group.GetComponent<HorizontalOrVerticalLayoutGroup>();
            CoopBehaviour.Log("ui: co-op menu rows " + actionHeight + " high (min " + templateMin + " preferred " + templatePreferred + " flexible " + templateFlexible + "), panel "
                + groupSizeY + (vlg != null ? ", spacing " + vlg.spacing + " padding " + vlg.padding.top + "/" + vlg.padding.bottom + " expand " + vlg.childForceExpandHeight : ", no layout group"));
        }

        public override void ActivateElements() { }
        public override void OnMenuOpen() { if (Mod != null) Mod.MenuOpened(); Refresh(true); }
        public override void OnMenuClose() { ClearNotice(); StopTyping(); escArmed = false; DropEscHold(); if (Mod != null) Mod.MenuClosed(); lastPage = ""; }
        void OnDisable() { StopTyping(); escArmed = false; DropEscHold(); }       // hidden under a menu stacked on it (the pause menu): never left typing

        // The game polls the raw MenuCancel button for every menu. While the code is being typed only Esc may leave the page (a keyboard key
        // behind MenuCancel such as Backspace must not); the pad's B stops the typing first.
        public override void OnMenuCancel()
        {
            if (escArmed) return;                                    // Esc while typing: the page closes once the key is up (CloseAfterEsc)
            if (typing && !Input.GetKey(KeyCode.Escape) && !Input.GetKeyUp(KeyCode.Escape))
            {
                if (!Input.GetKey(KeyCode.Backspace) && !Input.GetKeyUp(KeyCode.Backspace)) StopTyping();
                return;
            }
            Back();
        }
        public override bool OnMenuChange(string name, int value) { return true; }

        public void SettleAll() { foreach (var v in shown) v.ChangedAt = Time.realtimeSinceStartup; }
        public void Unsettle() { foreach (var v in shown) v.ChangedAt = -99f; }

        void Back()
        {
            StopTyping();
            if (Mod != null && Mod.OnTypingPage)
            {
                // MAKE A ROOM / JOIN A ROOM: opened by its key it closes the whole menu (as Esc closed the old window), opened from the menu it goes back
                if (Mod.LeavePage()) { if (Menu != null) Menu.Close(); }
                else Refresh(false);
                return;
            }
            if (Mod != null && Mod.BlockBack) { Mod.BackRefused(); return; }
            if (Mod != null && Mod.MenuBack()) { Refresh(false); return; }   // a page inside the menu (friends) goes back to the main page first
            if (Menu != null) Menu.Close();
        }

        public void BackForTest() { Back(); }

        public override void OnMenuClick(string id)
        {
            ClearNotice();          // a press says something new: the subtitle shows what the page says (a message the press makes is shown after it)
            if (id == "back") { Back(); return; }
            if (id == "member" || id == "more" || id == "keys") return;
            if (id == "join_code") { BeginTyping(); return; }        // Submit or a click on the code line: type into it
            if (Mod != null) Mod.CoopMenuAction(id);
            if (Menu != null && Menu.gameObject.activeInHierarchy) Refresh(false);
        }

        public override void OnUpdate(float deltaTime)
        {
            base.OnUpdate(deltaTime);
            if (codeField != null) codeField.Tick();
            if (typing) KeepTyping();
            if (escArmed) CloseAfterEsc();
            else if (!typing && EscClosesHere && XInput.GetButtonDown(0, "Start")) ArmEsc();
            if (Page == CoopMenu.PageHost && !FadeScreen.FadeInProgress)
            {
                int n = Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1) ? 1 : Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2) ? 2
                      : Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3) ? 3 : 0;
                if (n > 0) HostKey(n);
            }
            if (Page == CoopMenu.PageHost) ShowHostHint(false);
            if (Time.realtimeSinceStartup < nextRefresh) return;
            nextRefresh = Time.realtimeSinceStartup + 0.3f;
            Refresh(false);
        }

        // keys 1, 2, 3 on MAKE A ROOM, as in the old window: public, listed and I accept, private
        public static string HostKeyRow(int n) { return n == 1 ? "host_public" : n == 2 ? "host_accept" : n == 3 ? "host_private" : null; }
        public void HostKey(int n)
        {
            string id = HostKeyRow(n);
            if (id == null || Page != CoopMenu.PageHost) return;
            GUIMonoBehaviour.PlayPositiveClickSound();
            OnMenuClick(id);
        }

        // the subtitle names what the selected line of MAKE A ROOM makes
        void ShowHostHint(bool force)
        {
            var es = EventSystem.current;
            string id = null;
            if (es != null && es.currentSelectedGameObject != null) foreach (var v in shown) if (v.Go == es.currentSelectedGameObject) id = v.Id;
            if (id == null || NoticeUp || (!force && id == hintFor)) return;
            hintFor = id;
            if (Line2 != null) Line2.text = CoopMenu.HostHint(id);
        }

        // ---- typing the invite code ----------------------------------------------------------------------------------------------------

        public void BeginTyping()
        {
            if (codeField == null || textView == null || !textView.Go.activeInHierarchy || Page != CoopMenu.PageJoin || escArmed) return;
            if (!typing)
            {
                typing = true;
                // a set of its own: no "ui" input reaches the game while typing (the pause key, the menu's own key navigation)
                InputManager.Push(codeField, "cooptype");
                // Backspace is the game's MenuCancel key: the menu would play its cancel sound for every letter rubbed out
                var um = Menu as UnityGUIMenu;
                if (um != null && um.m_playCancelSoundOnB) { um.m_playCancelSoundOnB = false; cancelSoundMuted = true; }
            }
            if (!codeField.isFocused && !codeField.Opening) codeField.Open();
            var es = EventSystem.current;
            if (es != null && es.currentSelectedGameObject != textView.Go) es.SetSelectedGameObject(textView.Go, null);
        }

        public void StopTyping()
        {
            if (codeField != null) codeField.Close();
            if (typing) { typing = false; if (codeField != null) InputManager.Pop(codeField); }
            if (cancelSoundMuted) { cancelSoundMuted = false; var um = Menu as UnityGUIMenu; if (um != null) um.m_playCancelSoundOnB = true; }
        }

        public bool CancelSoundOnForTest { get { var um = Menu as UnityGUIMenu; return um != null && um.m_playCancelSoundOnB; } }

        public void TextRowSelected() { if (KeyboardMode && Page == CoopMenu.PageJoin) BeginTyping(); }

        // The code line lost the selection. Every way of leaving it on purpose (Up / Down, BACK, a page change, a line pressed or the menu
        // closing) stops the typing first; what is left is the mouse (a move switches the input module to the mouse and clears the selection,
        // hovering a line selects it). That never stops the typing: the line takes the selection back (KeepTyping) and the keys stay the field's.
        public void TextRowDeselected() { }

        // every frame while typing: the code line stays the selection, and the typing set stays on top of the game's input stack
        void KeepTyping()
        {
            var es = EventSystem.current;
            if (es != null && textView != null && textView.Go.activeInHierarchy && es.currentSelectedGameObject != textView.Go) { es.SetSelectedGameObject(textView.Go, null); RowReclaims++; }
            if (codeField != null) CoopBehaviour.KeepTypingOnTop(codeField);
        }

        // ---- Esc on a page --------------------------------------------------------------------------------------------------------------
        // Esc is the game's Start button (its pause key), not MenuCancel. On MAKE A ROOM / JOIN A ROOM (not typing: the field has Esc then), and on
        // any co-op page in the world while the game runs, it closes like BACK, as Esc closed the old windows. Its own set goes on the input
        // stack at once, so the pause key (which acts when Start comes up) never sees it; the page closes when the key is up (CloseAfterEsc).
        // Paused (opened from the pause menu) it stays the game's: resume, as on the game's own Settings pages.
        public bool EscClosesHere
        {
            get
            {
                if (Mod == null || !CoopMenu.Showing || FadeScreen.FadeInProgress) return false;
                if (Mod.OnTypingPage) return true;
                bool paused = false; try { paused = Singleton<PauseManager>.Exists && PauseManager.Paused; } catch (Exception) { }
                return WorldSync.InWorld() && !paused;
            }
        }

        void ArmEsc()
        {
            escArmed = true; escReleaseFrame = -1;
            if (!escHeld) { escHeld = true; InputManager.Push(this, "cooptype"); }
        }

        void DropEscHold() { if (escHeld) { escHeld = false; InputManager.Pop(this); } }

        public void EscForTest() { if (!typing && EscClosesHere) ArmEsc(); }
        public bool EscHeldForTest { get { return escHeld; } }

        // called by the code line while it is selected; true = the keys were the field's this frame
        public bool PumpTyping()
        {
            if (escArmed) { var e = new Event(); while (Event.PopEvent(e)) { } return true; }
            if (codeField == null || !typing) return false;
            if (!codeField.isFocused) return codeField.Opening;
            return Typed(codeField.Pump());
        }

        public bool FeedForTest(Event e) { if (codeField == null) return false; if (!typing) BeginTyping(); Typed(codeField.FeedForTest(e)); return true; }

        bool Typed(int what)
        {
            if (Mod != null && codeField != null) Mod.SetJoinText(codeField.text);
            switch (what)
            {
                case CoopField.Submit:
                    ClearNotice();
                    GUIMonoBehaviour.PlayPositiveClickSound();
                    if (Mod != null) Mod.CoopMenuAction("join_go");       // a bad code leaves the page open and the field typing, as the old window did
                    if (Menu != null && Menu.gameObject.activeInHierarchy) Refresh(false);
                    break;
                case CoopField.Cancel:
                    // Esc: the page closes when the key is up, so the same key does not also reach the menu under it or the pause key
                    escArmed = true; escReleaseFrame = -1;
                    codeField.Close();
                    break;
                case CoopField.Up:
                case CoopField.Down:
                    StopTyping();
                    var sel = textView.Button;
                    var next = sel == null ? null : what == CoopField.Up ? sel.FindSelectableOnUp() : sel.FindSelectableOnDown();
                    if (next != null && EventSystem.current != null) { EventSystem.current.SetSelectedGameObject(next.gameObject, null); GUIMonoBehaviour.PlayTempGUISound("MenuSounds.Move"); }
                    break;
            }
            return true;
        }

        void CloseAfterEsc()
        {
            bool held = Input.GetKey(KeyCode.Escape);
            if (escHeld && !held) { try { held = XInput.GetButton(0, "Start"); } catch (Exception) { } }      // the pad's Start too
            if (held) { escReleaseFrame = -1; return; }
            if (escReleaseFrame < 0) { escReleaseFrame = Time.frameCount; return; }
            if (Time.frameCount <= escReleaseFrame) return;
            escArmed = false;
            StopTyping();
            DropEscHold();
            EscCloses++;
            GUIMonoBehaviour.PlayTempGUISound("MenuSounds.Cancel");
            Back();
        }

        View TakeText()
        {
            if (textView != null) return textView;
            var go = (GameObject)Instantiate(Template);
            go.name = "CoopCodeRow";
            go.transform.SetParent(Group, false);
            var v = new View { Go = go };
            var t = go.transform.Find("ButText");
            if (t != null) v.Text = t.GetComponent<Text>();
            v.Layout = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            v.Button = go.GetComponent<Button>();
            v.Button.onClick = new Button.ButtonClickedEvent();
            var me = v;
            v.Button.onClick.AddListener(delegate { Click(me); });
            v.BaseFont = v.Text != null ? v.Text.fontSize : 39;
            var row = go.AddComponent<CoopTextRow>();
            row.Handler = this;

            // the field: to the right of the label, the same font, size and colour as the label (a copy of it), never hit by the mouse
            var fieldGo = new GameObject("CodeField", typeof(RectTransform));
            fieldGo.SetActive(false);
            fieldGo.layer = go.layer;
            fieldGo.transform.SetParent(go.transform, false);
            fieldRect = (RectTransform)fieldGo.transform;
            var group = fieldGo.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false; group.interactable = true;
            Text typed = null, hint = null;
            if (v.Text != null)
            {
                typed = MakeText(v.Text, fieldGo.transform, "Typed", "", 1f);
                hint = MakeText(v.Text, fieldGo.transform, "Placeholder", "TYPE IT HERE", 0.45f);
            }
            codeField = fieldGo.AddComponent<CoopField>();
            codeField.textComponent = typed;
            codeField.placeholder = hint;
            codeField.characterLimit = CoopMenu.CodeLimit;
            codeField.lineType = InputField.LineType.SingleLine;
            codeField.Allowed = CoopMenu.CodeAllowed;
            codeField.transition = Selectable.Transition.None;
            var nav = new Navigation(); nav.mode = Navigation.Mode.None; codeField.navigation = nav;
            if (typed != null) codeField.selectionColor = new Color(typed.color.r, typed.color.g, typed.color.b, 0.35f);
            fieldGo.SetActive(true);
            textView = v;
            PlaceField();
            return v;
        }

        static Text MakeText(Text like, Transform parent, string name, string text, float alpha)
        {
            var go = (GameObject)Instantiate(like.gameObject);
            go.name = name;
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero; rt.sizeDelta = Vector2.zero;
            var t = go.GetComponent<Text>();
            t.text = text;
            t.alignment = TextAnchor.MiddleLeft;
            t.supportRichText = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.color = new Color(t.color.r, t.color.g, t.color.b, t.color.a * alpha);
            for (int i = go.transform.childCount - 1; i >= 0; i--) DestroyImmediate(go.transform.GetChild(i).gameObject);
            return t;
        }

        // the field takes the label's place minus the label's own width
        void PlaceField()
        {
            if (fieldRect == null || textView == null || textView.Text == null) return;
            var label = (RectTransform)textView.Text.transform;
            float lw = textView.Text.preferredWidth + 14f;
            fieldRect.anchorMin = label.anchorMin; fieldRect.anchorMax = label.anchorMax; fieldRect.pivot = label.pivot;
            float w = Mathf.Max(40f, label.sizeDelta.x - lw);
            fieldRect.sizeDelta = new Vector2(w, label.sizeDelta.y);
            fieldRect.anchoredPosition = new Vector2(label.anchoredPosition.x - label.sizeDelta.x * label.pivot.x + lw + w * fieldRect.pivot.x, label.anchoredPosition.y);
            fieldRect.localScale = Vector3.one;
            if (codeField.textComponent != null) codeField.textComponent.fontSize = textView.Text.fontSize;
            if (codeField.placeholder is Text) ((Text)codeField.placeholder).fontSize = textView.Text.fontSize;
        }

        string DefaultId(CoopMenu.State s)
        {
            if (s.Screen == CoopMenu.ScreenParty && Page == CoopMenu.PageHost) return CoopMenu.HostRowFor(s.RoomDefault);
            if (s.Screen == CoopMenu.ScreenParty && Page == CoopMenu.PageJoin) return KeyboardMode ? "join_code" : "join_go";
            return null;
        }

        void Click(View v)
        {
            if (FadeScreen.FadeInProgress || v == null || !shown.Contains(v)) return;
            if (Time.realtimeSinceStartup - v.ChangedAt < SettleTime) return;
            GUIMonoBehaviour.PlayPositiveClickSound();
            OnMenuClick(v.Id);
        }

        View Take(List<View> pool, int index, bool inert)
        {
            if (index < pool.Count) return pool[index];
            var go = (GameObject)Instantiate(Template);
            go.name = (inert ? "CoopInfo" : "CoopRow") + pool.Count;
            go.transform.SetParent(Group, false);
            var v = new View { Go = go };
            var t = go.transform.Find("ButText");
            if (t != null) v.Text = t.GetComponent<Text>();
            v.Layout = go.GetComponent<LayoutElement>();
            if (v.Layout == null) v.Layout = go.AddComponent<LayoutElement>();
            if (inert)
            {
                // a member line: the same font and place as a menu line, but nothing to select, highlight or press
                var b = go.GetComponent<Button>(); if (b != null) DestroyImmediate(b);
                var an = go.GetComponent<Animator>(); if (an != null) DestroyImmediate(an);
                var img = go.GetComponent<Image>(); if (img != null) img.enabled = false;
                foreach (string part in new[] { "Arrow", "Stars" }) { var p = go.transform.Find(part); if (p != null) p.gameObject.SetActive(false); }
                v.BaseFont = InertFont;
            }
            else
            {
                v.Button = go.GetComponent<Button>();
                v.Button.onClick = new Button.ButtonClickedEvent();
                var me = v;
                v.Button.onClick.AddListener(delegate { Click(me); });
                v.BaseFont = v.Text != null ? v.Text.fontSize : 39;
            }
            pool.Add(v);
            return v;
        }

        // A label is made smaller until it fits its line (long names, "START GAME  2/3 READY").
        static void SetLabel(View v, string label)
        {
            if (v.Text == null || v.Label == label) return;
            v.Label = label;
            v.Text.text = label;
            v.Text.fontSize = v.BaseFont;
            float width = ((RectTransform)v.Text.transform).rect.width;
            if (width <= 1f) return;
            while (v.Text.fontSize > MinFont && v.Text.preferredWidth > width) v.Text.fontSize -= 2;
        }

        public void Refresh(bool opening)
        {
            if (Mod == null || Template == null) return;
            string l1, l2;
            var state = hasOverride ? overrideState : Mod.CoopMenuState();
            var rows = CoopMenu.RowsFor(state, out l1, out l2);
            if (Line1 != null) Line1.text = l1;
            if (Line2 != null) Line2.text = NoticeUp ? NoticeText : l2;
            if (Title != null) Title.text = state.Screen == CoopMenu.ScreenSpeedrun ? "SPEEDRUN" : state.AtStart ? "PARTY" : "CO-OP";
            string page = state.Screen == CoopMenu.ScreenParty && state.Online && !state.Following ? (Page == CoopMenu.PageHost && state.InLobby ? "" : Page) : "";
            bool pageChanged = page != lastPage;
            if (pageChanged && !opening && NoticeUp && Line2 != null) { ClearNotice(); Line2.text = l2; }
            if (pageChanged)
            {
                lastPage = page; hintFor = null;
                if (page != CoopMenu.PageJoin) { StopTyping(); escArmed = false; }
                if (page == CoopMenu.PageJoin && codeField != null && Mod != null) codeField.text = Mod.JoinTextNow ?? "";
            }
            string key = "";
            foreach (var r in rows) key += (r.Inert ? "~" : "") + r.Id + "=" + r.Label + "|";
            var es = EventSystem.current;
            if (key != shownKey || opening)
            {
                shownKey = key;
                // remember which line the player is on, so the selection stays on the same action when lines are added or removed above it
                string selectedId = null;
                if (es != null && es.currentSelectedGameObject != null)
                    foreach (var v in shown) if (v.Button != null && v.Go == es.currentSelectedGameObject) selectedId = v.Id;
                var before = new List<View>(shown);
                shown.Clear();
                int a = 0, n = 0;
                foreach (var r in rows)
                {
                    var v = r.Text ? TakeText() : r.Inert ? Take(inertPool, n++, true) : Take(actionPool, a++, false);
                    v.Go.SetActive(true);
                    v.Go.transform.SetAsLastSibling();
                    // a line that now does something else, or now names somebody else (LET IN EVE after LET IN BOB), waits a moment before it takes a press
                    bool named = r.Id == "let_in" || r.Id == "refuse" || r.Id == "accept_invite";
                    if (!r.Inert && !opening && (v.Id != r.Id || !before.Contains(v) || (named && v.Label != r.Label))) v.ChangedAt = Time.realtimeSinceStartup;
                    v.Id = r.Id;
                    shown.Add(v);
                }
                for (int i = a; i < actionPool.Count; i++) { actionPool[i].Go.SetActive(false); actionPool[i].Id = ""; }
                for (int i = n; i < inertPool.Count; i++) { inertPool[i].Go.SetActive(false); inertPool[i].Id = ""; }
                if (textView != null && !shown.Contains(textView)) { textView.Go.SetActive(false); textView.Id = ""; }
                for (int i = 0; i < rows.Count; i++) SetLabel(shown[i], rows[i].Label);
                if (textView != null && shown.Contains(textView)) PlaceField();
                LayoutRows();
                var follow = selectedId == null ? null : ButtonFor(selectedId);
                if (!opening && !pageChanged && follow != null && es != null && gameObject.activeInHierarchy) es.SetSelectedGameObject(follow.gameObject, null);
            }
            string defId = DefaultId(state);
            var first = (defId == null ? null : ButtonFor(defId)) ?? FirstButton();
            var um = Menu as UnityGUIMenu;
            if (um != null && first != null) um.m_defaultSelection = first.gameObject;
            // the selected line may just have gone away: keep the keyboard / controller on a visible line
            if (es != null && first != null && gameObject.activeInHierarchy && !opening)
            {
                var sel = es.currentSelectedGameObject;
                if (pageChanged || sel == null || !sel.activeInHierarchy || !sel.transform.IsChildOf(transform) || sel.GetComponent<Selectable>() == null) es.SetSelectedGameObject(first.gameObject, null);
            }
            // a page that just opened starts where the old window did: JOIN A ROOM typing (keyboard), MAKE A ROOM on the room the settings pick
            if (pageChanged && page == CoopMenu.PageJoin && KeyboardMode && gameObject.activeInHierarchy && defId == "join_code") BeginTyping();
            if (page == CoopMenu.PageHost) ShowHostHint(true);
        }

        public string DefaultIdForTest { get { var um = Menu as UnityGUIMenu; if (um == null || um.m_defaultSelection == null) return null; foreach (var v in shown) if (v.Go == um.m_defaultSelection) return v.Id; return null; } }
        public string SelectedIdForTest { get { var es = EventSystem.current; if (es == null || es.currentSelectedGameObject == null) return null; foreach (var v in shown) if (v.Go == es.currentSelectedGameObject) return v.Id; return null; } }

        Button FirstButton() { foreach (var v in shown) if (v.Button != null) return v.Button; return null; }

        // Member lines are short; the menu lines get smaller when many are needed, and only if that is not enough the panel grows.
        void LayoutRows()
        {
            var rt = (RectTransform)Group;
            if (groupHeight <= 1f && gameObject.activeInHierarchy && rt.rect.height > 1f) groupHeight = rt.rect.height - (rt.sizeDelta.y - groupSizeY);
            float avail = groupHeight > 1f ? groupHeight : groupSizeY;
            var vlg = Group.GetComponent<HorizontalOrVerticalLayoutGroup>();
            float spacing = vlg != null ? vlg.spacing : 0f, pad = vlg != null ? vlg.padding.top + vlg.padding.bottom : 0f;
            int inert = 0, acts = 0;
            foreach (var v in shown) if (v.Button == null) inert++; else acts++;
            float fixedPart = pad + spacing * Mathf.Max(0, shown.Count - 1) + inert * InertHeight;
            float h = actionHeight;
            if (acts > 0 && fixedPart + acts * h > avail) h = Mathf.Max(MinActionHeight, Mathf.Floor((avail - fixedPart) / acts));
            float need = fixedPart + acts * h;
            bool original = inert == 0 && h >= actionHeight;
            foreach (var v in shown)
            {
                if (v.Button != null && original) { v.Layout.minHeight = templateMin; v.Layout.preferredHeight = templatePreferred; v.Layout.flexibleHeight = templateFlexible; continue; }
                float rowH = v.Button == null ? InertHeight : h;
                v.Layout.minHeight = rowH; v.Layout.preferredHeight = rowH; v.Layout.flexibleHeight = 0f;
            }
            Grown = need > avail + 0.5f;
            rt.sizeDelta = new Vector2(rt.sizeDelta.x, Grown ? groupSizeY + (need - avail) : groupSizeY);
            LastActionHeight = h;
        }
    }
}
