using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CoopMod
{
    // The last native-UI checks (quick-audit group "ui", in the world), all structural inside the game, no screen capture:
    //  NO-IMGUI (live part): an IMGUI watch runs for the whole audit and sees, every frame, whose OnGUI ran (the game's own
    //    GUIUtility.s_OriginalID, which Unity sets for every OnGUI call); nothing of the mod may ever run one, on the start screen, in the
    //    world, in the pause, with the chat open, with the join page open and while a speedrun runs.
    //  NATIVE-FONT: every text of the co-op menu and the co-op HUD is in the game's font (times in its fixed-width font).
    //  MENU-NAV: the co-op pages are walked with the game's own move events (what its input module sends for the arrows / stick).
    //  PAUSE-OVER-COOP: the game's own pause and resume over an open co-op page leave nothing behind.
    //  HUD-LAYOUT: every co-op HUD piece inside the screen, clear of the game's HUD panels and of each other, at 800x450 and 1920x1080.
    public partial class SelfTest
    {
        public const string NativeCIds = "NO-IMGUI,NATIVE-FONT,MENU-NAV,PAUSE-OVER-COOP,HUD-LAYOUT";

        // ---- the IMGUI watch ----------------------------------------------------------------------------------------------------------
        static readonly FieldInfo fGuiOwner = typeof(GUIUtility).GetField("s_OriginalID", BindingFlags.NonPublic | BindingFlags.Static);
        static readonly FieldInfo fInputStack = typeof(InputManager).GetField("m_StackDepth", BindingFlags.NonPublic | BindingFlags.Instance);
        const int NoGuiPass = int.MinValue + 77;
        public static readonly string[] GameAssemblies = { "Assembly-CSharp", "Assembly-CSharp-firstpass", "Assembly-UnityScript-firstpass", "UnityEngine", "UnityEngine.UI" };

        class ImguiTally { public int Frames, Passes, Game, Foreign, LiveForeign, Scans; public string Who = "", GameWho = ""; }
        readonly Dictionary<string, ImguiTally> imguiTally = new Dictionary<string, ImguiTally>();
        readonly Dictionary<int, string> guiOwners = new Dictionary<int, string>();
        string imguiWatchProblem = "";
        bool holdPause;                 // the pause checks keep the game paused (Update otherwise resumes an unfocused window at once)

        static bool DeclaresOnGui(Type t)
        {
            for (; t != null && t != typeof(MonoBehaviour) && t != typeof(object); t = t.BaseType)
                if (t.GetMethod("OnGUI", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null) return true;
            return false;
        }
        static bool IsGameAssembly(Type t) { return Array.IndexOf(GameAssemblies, t.Assembly.GetName().Name) >= 0; }

        string ImguiContext(CoopBehaviour cb)
        {
            if (Speedrun.Current != null && Speedrun.Current.Live && Speedrun.SlotActive && WorldSync.InWorld()) return "speedrun";
            if (!WorldSync.InWorld()) return Singleton<StartScreen>.Exists ? "start" : "other";
            if (cb != null && cb.ChatOpen) return "chat";
            if (cb != null && cb.JoinOpenForTest) return "join";
            if (Singleton<PauseManager>.Exists && PauseManager.Paused) return "pause";
            if (Singleton<GUIManager>.Exists && Singleton<GUIManager>.Instance.AnyMenuActive) return "menu";
            return "world";
        }

        // every live script that has an OnGUI: the game's are known, any other one (the mod's, or anybody's) is counted against the mod
        void ScanOnGuiOwners(ImguiTally t)
        {
            t.Scans++;
            foreach (MonoBehaviour b in UnityEngine.Object.FindObjectsOfType(typeof(MonoBehaviour)))
            {
                if (b == null) continue;
                var ty = b.GetType();
                if (!DeclaresOnGui(ty)) continue;
                bool game = IsGameAssembly(ty);
                guiOwners[b.GetInstanceID()] = (game ? "game:" : "other:") + ty.FullName;
                if (!game && b.enabled) { t.LiveForeign++; if (t.Who.Length < 200) t.Who += ty.FullName + " "; }
            }
        }

        IEnumerator ImguiWatch()
        {
            var cb = GetComponent<CoopBehaviour>();
            if (fGuiOwner == null) { imguiWatchProblem = "GUIUtility.s_OriginalID not found"; yield break; }
            var eof = new WaitForEndOfFrame();
            try { fGuiOwner.SetValue(null, NoGuiPass); } catch (Exception e) { imguiWatchProblem = Describe(e); yield break; }
            while (true)
            {
                yield return eof;                  // after every OnGUI of this frame
                int id;
                try { id = (int)fGuiOwner.GetValue(null); fGuiOwner.SetValue(null, NoGuiPass); }
                catch (Exception e) { imguiWatchProblem = Describe(e); yield break; }
                string ctx = ImguiContext(cb);
                ImguiTally t;
                if (!imguiTally.TryGetValue(ctx, out t)) imguiTally[ctx] = t = new ImguiTally();
                t.Frames++;
                if (t.Frames % 120 == 1) ScanOnGuiOwners(t);
                if (id == NoGuiPass) continue;
                t.Passes++;
                string who;
                if (!guiOwners.TryGetValue(id, out who)) { ScanOnGuiOwners(t); if (!guiOwners.TryGetValue(id, out who)) guiOwners[id] = who = "unknown:" + id; }
                if (who.StartsWith("game:")) { string n = who.Substring(5); t.Game++; if (t.GameWho.IndexOf(n) < 0 && t.GameWho.Length < 160) t.GameWho += n + " "; }
                else { t.Foreign++; if (t.Who.IndexOf(who) < 0 && t.Who.Length < 200) t.Who += who + " "; }
            }
        }

        // "" when every needed context was watched long enough, the watch saw the game's own IMGUI pass there (it works) and nothing else ran one
        string ImguiRuntimeProblems(List<string> need, out string summary)
        {
            var probs = new List<string>(); var sum = new List<string>();
            if (imguiWatchProblem.Length > 0) probs.Add("watch: " + imguiWatchProblem);
            foreach (var kv in imguiTally)
            {
                var t = kv.Value;
                sum.Add(kv.Key + " " + t.Frames + "f/" + t.Passes + "p game[" + t.GameWho.Trim() + "] other " + t.Foreign + "/" + t.LiveForeign);
                if (t.Foreign > 0 || t.LiveForeign > 0) probs.Add(kv.Key + ": IMGUI not the game's: " + t.Who.Trim());
            }
            foreach (var c in need)
            {
                ImguiTally t;
                if (!imguiTally.TryGetValue(c, out t)) { probs.Add(c + " never watched"); continue; }
                if (t.Frames < 120) probs.Add(c + " watched " + t.Frames + " frames (< 120)");
                if (t.Game < 1) probs.Add(c + ": the watch never saw the game's own IMGUI pass (it would not see the mod's either)");
                if (t.Scans < 1) probs.Add(c + ": live scripts never scanned");
            }
            summary = string.Join("; ", sum.ToArray());
            return string.Join("; ", probs.ToArray());
        }

        // the start screen was watched long enough before the audit's game starts (a quick run without "ui" does not wait)
        bool StartWatched() { ImguiTally t; return !On("ui") || (imguiTally.TryGetValue("start", out t) && t.Frames >= 150); }

        static int InputDepth() { return Singleton<InputManager>.Exists && fInputStack != null ? (int)fInputStack.GetValue(Singleton<InputManager>.Instance) : -1; }

        // the game's own pause path (PauseManager.OnApplicationFocus(false): what a lost window focus does), and its own resume
        static void GamePause()
        {
            var m = typeof(PauseManager).GetMethod("OnApplicationFocus", BindingFlags.NonPublic | BindingFlags.Instance);
            if (m != null && Singleton<PauseManager>.Exists) m.Invoke(Singleton<PauseManager>.Instance, new object[] { false });
        }
        IEnumerator GameResume()
        {
            if (Singleton<PauseManager>.Exists && PauseManager.Paused) PauseManager.ResumeRequested = true;
            for (int i = 0; i < 30 && PauseManager.Paused; i++) yield return null;
            yield return StartCoroutine(Frames(3));
        }

        // ---- the visible box of a uGUI graphic, in screen pixels ------------------------------------------------------------------------
        static readonly Vector3[] vbuf = new Vector3[4];
        static Rect VisibleRect(Graphic g, Camera cam)
        {
            var rt = g.rectTransform;
            Rect local = rt.rect;
            var t = g as Text;
            if (t != null)
            {
                float w = Mathf.Min(local.width, t.preferredWidth), h = Mathf.Min(local.height, t.preferredHeight);
                if (t.horizontalOverflow == HorizontalWrapMode.Overflow) w = t.preferredWidth;
                if (t.verticalOverflow == VerticalWrapMode.Overflow) h = t.preferredHeight;
                int a = (int)t.alignment; int col = a % 3, row = a / 3;      // UpperLeft..LowerRight
                float x = col == 0 ? local.xMin : col == 1 ? local.center.x - w / 2f : local.xMax - w;
                float y = row == 0 ? local.yMax - h : row == 1 ? local.center.y - h / 2f : local.yMin;
                local = new Rect(x, y, w, h);
            }
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            var pts = new[] { new Vector3(local.xMin, local.yMin), new Vector3(local.xMin, local.yMax), new Vector3(local.xMax, local.yMax), new Vector3(local.xMax, local.yMin) };
            foreach (var p in pts)
            {
                var s = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(p));
                x0 = Mathf.Min(x0, s.x); y0 = Mathf.Min(y0, s.y); x1 = Mathf.Max(x1, s.x); y1 = Mathf.Max(y1, s.y);
            }
            return Rect.MinMaxRect(x0, y0, x1, y1);
        }

        static Rect Union(Rect a, Rect b) { if (a.width <= 0f && a.height <= 0f) return b; return Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax)); }
        static bool Hit(Rect a, Rect b) { return a.xMin < b.xMax - 0.5f && b.xMin < a.xMax - 0.5f && a.yMin < b.yMax - 0.5f && b.yMin < a.yMax - 0.5f; }
        static string R(Rect r) { return "(" + r.xMin.ToString("F0") + "," + r.yMin.ToString("F0") + ")-(" + r.xMax.ToString("F0") + "," + r.yMax.ToString("F0") + ")"; }

        // every shown graphic of the piece under `root` (texts by their glyph box, images by their rect); fully transparent ones are left out
        static List<KeyValuePair<string, Rect>> PieceBoxes(Transform root, Camera cam)
        {
            var l = new List<KeyValuePair<string, Rect>>();
            if (root == null || !root.gameObject.activeInHierarchy) return l;
            foreach (var g in root.GetComponentsInChildren<Graphic>(false))
            {
                if (!g.enabled || !g.gameObject.activeInHierarchy || g.color.a < 0.02f) continue;
                var t = g as Text; if (t != null && string.IsNullOrEmpty(t.text)) continue;
                l.Add(new KeyValuePair<string, Rect>(t != null ? "\"" + (t.text.Length > 16 ? t.text.Substring(0, 16) : t.text) + "\"" : g.name, VisibleRect(g, cam)));
            }
            return l;
        }

        // the game's own HUD panels as they lie on screen (the ones that come and go too: a piece must not sit where one of them shows)
        static List<KeyValuePair<string, Rect>> GamePanels(AbilityHUD hud, out string active)
        {
            var l = new List<KeyValuePair<string, Rect>>(); var on = new List<string>();
            var canvas = hud.m_canvas == null ? null : hud.m_canvas.GetComponent<Canvas>();
            var cam = canvas == null ? null : canvas.worldCamera;
            var panels = new Dictionary<string, RectTransform>
            {
                { "ControlPanel", hud.m_controllerPanel }, { "WarningPanel", hud.m_warningPanel }, { "AltitudePanel", hud.m_altitudePanel }, { "IslandsPanel", hud.m_islandsPanel },
                { "UpgradePanel", hud.m_upgradePanel }, { "JetpackPanel", hud.m_jetpackPanel }, { "SeedPanel", hud.m_seedPanel }, { "DataBankPanel", hud.m_dataBankPanel },
                { "CrystalDetectorPanel", hud.m_finderPanel },
            };
            if (hud.m_sideHintPanel != null)
                foreach (string part in new[] { "TopBackground", "BottomBackground" }) { var t = hud.m_sideHintPanel.Find(part) as RectTransform; if (t != null) panels["HintPanel/" + part] = t; }
            foreach (var kv in panels)
            {
                if (kv.Value == null) continue;
                var r = CoopHud.ScreenRect(kv.Value, vbuf, cam);
                if (r.width < 1f || r.height < 1f) continue;
                if (kv.Value.gameObject.activeInHierarchy) on.Add(kv.Key);
                l.Add(new KeyValuePair<string, Rect>(kv.Key, r));
            }
            active = string.Join(",", on.ToArray());
            return l;
        }

        static void CheckMenuTexts(GUIMenu menu, Font game, List<string> probs)
        {
            int odd = 0; string oddName = "";
            foreach (var t in menu.GetComponentsInChildren<Text>(true)) if (t.font != game) { odd++; if (oddName.Length < 120) oddName += t.name + "(" + (t.font == null ? "none" : t.font.name) + ") "; }
            if (odd > 0) probs.Add(menu.name + ": " + odd + " texts not in the game font: " + oddName);
        }

        static bool IsTimeText(string s)
        {
            if (s.Trim() == "-") return true;          // a time not there yet (no PB for that split)
            bool digit = false;
            foreach (char c in s) { if (char.IsDigit(c)) digit = true; else if (c != ':' && c != '.' && c != '+' && c != '-' && c != ' ') return false; }
            return digit && (s.IndexOf(':') >= 0 || s.IndexOf('.') >= 0);
        }

        void MoveSel(MoveDirection d)
        {
            var es = EventSystem.current;
            if (es == null || es.currentSelectedGameObject == null) return;
            var ev = new AxisEventData(es) { moveDir = d, moveVector = d == MoveDirection.Down ? new Vector2(0f, -1f) : new Vector2(0f, 1f) };
            ExecuteEvents.Execute(es.currentSelectedGameObject, ev, ExecuteEvents.moveHandler);
        }

        // the selectable lines of the page, walked down and up with the game's own move events
        IEnumerator WalkPage(string page, CoopMenuHandler h, GUIMenu menu, List<string> probs)
        {
            var es = EventSystem.current;
            var ids = h.RowIdsForTest; var inert = h.RowInertForTest; var rows = h.RowObjectsForTest;
            var want = new List<string>();
            var tAnim = h.Template == null ? null : h.Template.GetComponent<Animator>();
            var tButton = h.Template == null ? null : h.Template.GetComponent<Button>();
            for (int i = 0; i < ids.Count; i++)
            {
                if (inert[i]) continue;
                want.Add(ids[i]);
                var b = rows[i].GetComponent<Button>(); var a = rows[i].GetComponent<Animator>();
                if (b == null || tButton == null || b.transition != tButton.transition || b.navigation.mode != tButton.navigation.mode) probs.Add(page + " " + ids[i] + ": not the game's line (transition/navigation)");
                if (a == null || tAnim == null || a.runtimeAnimatorController != tAnim.runtimeAnimatorController) probs.Add(page + " " + ids[i] + ": not the game's highlight");
            }
            if (want.Count < 2 || es == null) { probs.Add(page + ": " + want.Count + " lines to walk"); yield break; }
            es.SetSelectedGameObject(h.ButtonFor(want[0]).gameObject);
            yield return null;
            var down = new List<string> { h.SelectedIdForTest };
            for (int i = 0; i < want.Count; i++) { MoveSel(MoveDirection.Down); yield return null; down.Add(h.SelectedIdForTest); if (!SelectionIn(menu)) probs.Add(page + ": the selection left the menu"); }
            var up = new List<string> { h.SelectedIdForTest };
            for (int i = 0; i < want.Count; i++) { MoveSel(MoveDirection.Up); yield return null; up.Add(h.SelectedIdForTest); }
            var wantDown = new List<string>(want); wantDown.Add(want[want.Count - 1]);          // the last line holds (no wrap, as the game's menus)
            var wantUp = new List<string>(want); wantUp.Reverse(); wantUp.Add(want[0]);
            string gotD = string.Join(",", down.ToArray()), gotU = string.Join(",", up.ToArray());
            if (gotD != string.Join(",", wantDown.ToArray())) probs.Add(page + " down: " + gotD + " want " + string.Join(",", wantDown.ToArray()));
            if (gotU != string.Join(",", wantUp.ToArray())) probs.Add(page + " up: " + gotU + " want " + string.Join(",", wantUp.ToArray()));
            if (CoopField.AnyFocused) probs.Add(page + ": the pad walk started typing");
        }

        IEnumerator TestNativeC()
        {
            var cb = GetComponent<CoopBehaviour>();
            var gm = Singleton<GUIManager>.Exists ? Singleton<GUIManager>.Instance : null;
            var hud = Singleton<AbilityHUD>.Exists ? Singleton<AbilityHUD>.Instance : null;
            var menu = CoopMenu.Menu; var h = CoopMenu.Handler;
            if (cb == null || gm == null || hud == null || menu == null || h == null || CoopHud.Canvas == null || !cb.IsOnline || !WorldSync.InWorld())
            {
                foreach (var id in NativeCIds.Split(',')) Check(id, "the native co-op UI can be tested", false, "mod=" + (cb != null) + " gui=" + (gm != null) + " hud=" + (hud != null) + " menu=" + (menu != null) + " canvas=" + (CoopHud.Canvas != null) + " world=" + WorldSync.InWorld());
                yield break;
            }
            if (gm.AnyMenuActive) gm.CloseAllMenus();
            bool hiddenWas = CoopUI.Hidden, shownWas = CoopUI.ShownForTest;
            CoopUI.ShownForTest = true; CoopUI.Hidden = false;
            Font game = hud.m_textBottomLine != null ? hud.m_textBottomLine.font : null;
            var savedChat = new List<Chat.Line>(Chat.Lines);
            int w0 = Screen.width, h0 = Screen.height; bool full0 = Screen.fullScreen;
            try
            {
                // ---- NATIVE-FONT ----
                {
                    var probs = new List<string>();
                    if (game == null || CoopHud.GameFont != game) probs.Add("the HUD font is " + (CoopHud.GameFont == null ? "none" : CoopHud.GameFont.name) + ", the game's is " + (game == null ? "none" : game.name));
                    if (CoopHud.FixedFont == null || CoopHud.FixedFont.name != "block_fixed") probs.Add("fixed-width font " + (CoopHud.FixedFont == null ? "not found" : CoopHud.FixedFont.name));
                    string missing = "", missingFixed = "";
                    if (game != null) foreach (char c in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-.,!?:'>%/()" + CoopMenu.CodeAllowed) if (c != ' ' && !game.HasCharacter(c) && missing.IndexOf(c) < 0) missing += c;
                    if (CoopHud.FixedFont != null) foreach (char c in "0123456789:.+-") if (!CoopHud.FixedFont.HasCharacter(c)) missingFixed += c;
                    if (missing.Length > 0 || missingFixed.Length > 0) probs.Add("glyphs missing: \"" + missing + "\" fixed \"" + missingFixed + "\"");
                    // every page of the co-op menu: open it so its lines exist, then every text of the menu (pooled lines too)
                    cb.RoomsForTest = new List<Room>(); for (int i = 0; i < 5; i++) cb.RoomsForTest.Add(new Room { Id = 801UL + (ulong)i, Name = "Font room " + i, Players = 1, Max = 4 });
                    CoopMenuHandler.KeyboardModeForTest = true;
                    cb.HotkeyForTest(KeyCode.F3); yield return StartCoroutine(Frames(3));
                    var field = h.CodeField;
                    if (field != null) { field.text = ""; h.FeedForTest(Key('q')); h.FeedForTest(Key('7')); }
                    if (field == null || field.text != "Q7") probs.Add("a typed lower-case letter: \"" + (field == null ? "" : field.text) + "\" (want Q7)");
                    if (field != null && (field.textComponent == null || field.textComponent.font != game)) probs.Add("the code line types in another font");
                    CheckMenuTexts(menu, game, probs);
                    if (!cb.CoopMenuState().InLobby) { cb.HotkeyForTest(KeyCode.F2); yield return StartCoroutine(Frames(3)); CheckMenuTexts(menu, game, probs); }
                    if (gm.AnyMenuActive) gm.CloseAllMenus();
                    yield return StartCoroutine(Frames(2));
                    // the HUD: the speedrun pieces drawn, times in the fixed font and every other text in the game's font
                    SpeedrunUI.PreviewForTest(SrUiSamples(), SrUiRaceSample());
                    yield return StartCoroutine(Frames(3));
                    int times = 0;
                    var st = CoopHud.SpeedrunLayer.ShownTexts();
                    foreach (var t in st)
                    {
                        bool time = IsTimeText(t.text);
                        if (time) times++;
                        bool digits = false; foreach (char c in t.text) if (char.IsDigit(c)) digits = true;
                        if (time && t.font != CoopHud.FixedFont) probs.Add("time \"" + t.text + "\" in " + (t.font == null ? "none" : t.font.name));
                        else if (!time && t.font != game && !(t.font == CoopHud.FixedFont && digits)) probs.Add("\"" + t.text + "\" in " + (t.font == null ? "none" : t.font.name));
                    }
                    if (times < 3) probs.Add("only " + times + " times on the speedrun preview (" + st.Count + " texts)");
                    SpeedrunUI.StopPreview();
                    int odd = 0; string oddName = "";
                    foreach (var t in CoopHud.Canvas.GetComponentsInChildren<Text>(true)) if (t.font != game && t.font != CoopHud.FixedFont) { odd++; oddName = t.name; }
                    if (odd > 0) probs.Add(odd + " HUD texts in another font, e.g. " + oddName);
                    // a chat line in lower case keeps its letters (the font has them; it is not forced to upper case)
                    cb.HandleChat(9093UL, "Font", Chat.Encode("lower case ok"));
                    yield return StartCoroutine(Frames(2));
                    bool lower = false; foreach (var t in CoopHud.ChatLayer.ShownTexts()) if (t.text == "lower case ok" && t.font == game) lower = true;
                    if (CoopBehaviour.ShowChat && !lower) probs.Add("a lower-case chat line: " + CoopHud.ChatLayer.AllText());
                    Check("NATIVE-FONT", "every text of the co-op menu pages and of the co-op HUD is in the game's own font (" + (game == null ? "?" : game.name) + "), times in its fixed-width font (block_fixed); the font has every letter the mod shows; a typed lower-case code letter comes out upper case",
                        probs.Count == 0, probs.Count == 0 ? "ok, " + times + " times in block_fixed" : string.Join("; ", probs.ToArray()));
                }

                // ---- MENU-NAV: the game's move events walk every co-op page ----
                {
                    var probs = new List<string>();
                    CoopMenuHandler.KeyboardModeForTest = false;      // a pad: the lines are walked, the code line is not typed into
                    var pause = CoopMenu.Find(gm, "PauseMenu");
                    if (pause != null && CoopMenu.Entry != null)
                    {
                        gm.ActivateMenu(pause); yield return StartCoroutine(Frames(2));
                        CoopMenu.Entry.onClick.Invoke(); yield return StartCoroutine(Frames(3));
                        if (!CoopMenu.Showing || cb.MenuPageNow != "") probs.Add("the main page did not open (" + cb.MenuPageNow + ")");
                        else yield return StartCoroutine(WalkPage("main", h, menu, probs));
                        gm.CloseAllMenus(); yield return StartCoroutine(Frames(2));
                    }
                    else probs.Add("no pause menu or CO-OP line");
                    if (!cb.CoopMenuState().InLobby)
                    {
                        cb.HotkeyForTest(KeyCode.F2); yield return StartCoroutine(Frames(3));
                        if (cb.MenuPageNow != CoopMenu.PageHost) probs.Add("MAKE A ROOM did not open");
                        else yield return StartCoroutine(WalkPage("host", h, menu, probs));
                        if (gm.AnyMenuActive) gm.CloseAllMenus(); yield return StartCoroutine(Frames(2));
                    }
                    cb.HotkeyForTest(KeyCode.F3); yield return StartCoroutine(Frames(3));
                    if (cb.MenuPageNow != CoopMenu.PageJoin) probs.Add("JOIN A ROOM did not open");
                    else yield return StartCoroutine(WalkPage("join", h, menu, probs));
                    if (gm.AnyMenuActive) gm.CloseAllMenus(); yield return StartCoroutine(Frames(2));
                    CoopMenuHandler.KeyboardModeForTest = true;
                    Check("MENU-NAV", "the co-op pages (CO-OP, MAKE A ROOM, JOIN A ROOM with rooms) are walked line by line with the game's own move events (arrows / stick / d-pad), down and back up, member lines skipped, the selection never leaving the menu; every line is the game's own (its highlight animation, transition and navigation)",
                        probs.Count == 0, probs.Count == 0 ? "3 pages walked" : string.Join("; ", probs.ToArray()));
                }

                // ---- PAUSE-OVER-COOP (and the pause part of the IMGUI watch) ----
                {
                    var probs = new List<string>();
                    if (gm.AnyMenuActive) gm.CloseAllMenus();
                    yield return StartCoroutine(Frames(3));
                    int depth0 = InputDepth();
                    holdPause = true;
                    // (a) typing the code: the game's pause key / focus pause goes to the field's own key set, so it either does not pause or pauses cleanly
                    cb.HotkeyForTest(KeyCode.F3); yield return StartCoroutine(Frames(3));
                    bool typing = CoopField.AnyFocused;
                    GamePause(); yield return StartCoroutine(Frames(3));
                    bool pausedA = PauseManager.Paused;
                    var pauseMenu = CoopMenu.Find(gm, "PauseMenu");
                    if (!typing) probs.Add("(a) not typing after F3");
                    if (pausedA && (pauseMenu == null || !pauseMenu.gameObject.activeInHierarchy || CoopField.AnyFocused)) probs.Add("(a) paused over the typing without a clean pause menu");
                    if (!pausedA && (!CoopField.AnyFocused || !cb.JoinOpenForTest)) probs.Add("(a) the typing was lost without a pause");
                    yield return StartCoroutine(GameResume());
                    if (gm.AnyMenuActive) gm.CloseAllMenus();
                    yield return StartCoroutine(Frames(3));
                    // (b) the page open (not typing), the game pauses itself, then resumes: nothing of the page is left
                    CoopMenuHandler.KeyboardModeForTest = false;
                    cb.HotkeyForTest(KeyCode.F3); yield return StartCoroutine(Frames(3));
                    bool openB = cb.JoinOpenForTest && CoopMenu.Showing && !CoopField.AnyFocused;
                    GamePause(); yield return StartCoroutine(Frames(3));
                    bool pausedB = PauseManager.Paused && pauseMenu != null && pauseMenu.gameObject.activeInHierarchy;
                    yield return StartCoroutine(GameResume());
                    string leftB = Leftovers(cb, gm, depth0);
                    if (!openB || !pausedB || leftB.Length > 0) probs.Add("(b) page open=" + openB + " paused=" + pausedB + (leftB.Length > 0 ? " left: " + leftB : ""));
                    // (c) paused first, the join page stacked on the pause menu and typing, then the game's resume (the pause watched meanwhile)
                    CoopMenuHandler.KeyboardModeForTest = true;
                    GamePause(); yield return StartCoroutine(Frames(130));
                    bool pausedC = PauseManager.Paused;
                    cb.HotkeyForTest(KeyCode.F3); yield return StartCoroutine(Frames(3));
                    bool stacked = cb.JoinOpenForTest && CoopMenu.Showing && CoopField.AnyFocused && PauseManager.Paused;
                    yield return StartCoroutine(GameResume());
                    string leftC = Leftovers(cb, gm, depth0);
                    if (!pausedC || !stacked || leftC.Length > 0) probs.Add("(c) paused=" + pausedC + " stacked and typing=" + stacked + (leftC.Length > 0 ? " left: " + leftC : ""));
                    holdPause = false;
                    Check("PAUSE-OVER-COOP", "the game's own pause and resume over an open co-op page (typing or not, stacked on the pause menu or under it) leave nothing behind: every menu closed, no typing, the game's input stack back where it was, the mouse and cursor tracker given back, the game running",
                        probs.Count == 0, probs.Count == 0 ? "typing pause " + (pausedA ? "paused cleanly" : "kept the typing") + ", page and stacked pause resumed clean, input depth " + depth0 : string.Join("; ", probs.ToArray()));
                }

                // ---- the other contexts of the IMGUI watch: the world, the chat open, the join page open (120 frames each at least) ----
                if (gm.AnyMenuActive) gm.CloseAllMenus();
                yield return StartCoroutine(Frames(130));
                cb.HotkeyForTest(KeyCode.T); yield return StartCoroutine(Frames(130));
                if (cb.ChatOpen) cb.HandleChatKeys(false, true);
                yield return StartCoroutine(Frames(3));
                cb.HotkeyForTest(KeyCode.F3); yield return StartCoroutine(Frames(130));
                if (gm.AnyMenuActive) gm.CloseAllMenus();
                yield return StartCoroutine(Frames(3));
                {
                    var need = new List<string> { "start", "world", "pause", "chat", "join" };
                    if (On("speedrun")) need.Add("speedrun");
                    string summary, p = ImguiRuntimeProblems(need, out summary);
                    Check("NO-IMGUI", "nothing draws with IMGUI while the game runs: an IMGUI watch on the game's own GUIUtility saw every frame whose OnGUI ran, and on the start screen, in the world, in the pause, with the chat open, with the join page open" + (On("speedrun") ? " and while a speedrun ran" : "") + " it was only ever the game's own (at least 120 frames each; the game's passes prove the watch sees them)",
                        p.Length == 0, (p.Length == 0 ? "ok: " : p + " || ") + summary);
                }

                // ---- HUD-LAYOUT ----
                {
                    var probs = new List<string>(); var notes = new List<string>();
                    yield return StartCoroutine(LayoutAt(cb, gm, hud, probs, notes));
                    bool sized = false;
                    if (!full0)
                    {
                        int tw = w0 == 1920 && h0 == 1080 ? 800 : 1920, th = w0 == 1920 && h0 == 1080 ? 450 : 1080;
                        Screen.SetResolution(tw, th, false);
                        float t0 = Time.realtimeSinceStartup;
                        while ((Screen.width != tw || Screen.height != th) && Time.realtimeSinceStartup - t0 < 5f) yield return null;
                        yield return StartCoroutine(Frames(5));
                        if (Screen.width == tw && Screen.height == th) { sized = true; yield return StartCoroutine(LayoutAt(cb, gm, hud, probs, notes)); }
                        else probs.Add("the window could not be made " + tw + "x" + th + " (it is " + Screen.width + "x" + Screen.height + ")");
                        Screen.SetResolution(w0, h0, full0);
                        t0 = Time.realtimeSinceStartup;
                        while ((Screen.width != w0 || Screen.height != h0) && Time.realtimeSinceStartup - t0 < 5f) yield return null;
                        yield return StartCoroutine(Frames(5));
                        if (Screen.width != w0 || Screen.height != h0) probs.Add("the window did not come back to " + w0 + "x" + h0);
                    }
                    else probs.Add("full screen: the 1920x1080 layout was not tried");
                    // the pure rects at other shapes (4:3, 16:10, 16:9, 21:9 and 4K): inside the screen, the speedrun panel clear of the link line
                    foreach (var sz in new[] { new Vector2(1024f, 768f), new Vector2(1680f, 1050f), new Vector2(1280f, 720f), new Vector2(2560f, 1080f), new Vector2(3840f, 2160f), new Vector2(800f, 450f) })
                    {
                        float s = CoopUI.ScaleFor(sz.y);
                        var rects = new Dictionary<string, Rect> { { "link", CoopHud.LinkRect(sz.x, sz.y, s) }, { "say", CoopHud.SayRect(sz.x, sz.y, s) }, { "mirror", CoopHud.MirrorRect(sz.x, sz.y, CoopHud.MirrorTop(sz.x, sz.y), sz.x, s) }, { "panel", SpeedrunUI.PanelRect(sz.x, sz.y, s, 8) }, { "strip", SpeedrunUI.StripRect(sz.x, sz.y, s, 0f, 8) }, { "results", SpeedrunUI.ResultsRect(sz.x, sz.y, s) } };
                        foreach (var kv in rects) if (kv.Value.xMin < 0f || kv.Value.yMin < 0f || kv.Value.xMax > sz.x || kv.Value.yMax > sz.y) probs.Add(kv.Key + " off a " + sz.x + "x" + sz.y + " screen: " + R(kv.Value));
                    }
                    Check("HUD-LAYOUT", "every co-op HUD piece (link line, chat log, SAY> line, M.O.M messages, join request, speedrun timer and race strip, countdown, result cards, name tags), shown one at a time, lies inside the screen, clear of every panel of the game's HUD (M.O.M side panel, altitude, jetpack, seeds, upgrades, controls, warnings, crystal finder) and of the other pieces, at " + w0 + "x" + h0 + " and 1920x1080; the countdown over the pause menu stays clear of its lines",
                        probs.Count == 0 && sized, (probs.Count == 0 ? "ok; " : string.Join("; ", probs.ToArray()) + " || ") + string.Join(" | ", notes.ToArray()));
                }
            }
            finally
            {
                holdPause = false;
                SpeedrunUI.StopPreview();
                CoopHud.LinkOverrideForTest = null; CoopHud.TagsForTest = null;
                if (cb.ChatOpen) cb.HandleChatKeys(false, true);
                cb.ClearPendingForTest(); cb.AnswerSinkForTest = null; cb.RoomsForTest = null;
                CoopMenuHandler.KeyboardModeForTest = null;
                CoopUI.ClearOverlayToastsForTest();
                Chat.Lines.Clear(); Chat.Lines.AddRange(savedChat);
                AbilityHUD.HideHUDMessage("AuditLayoutMsg");
                if (Screen.width != w0 || Screen.height != h0) Screen.SetResolution(w0, h0, full0);
                if (Singleton<PauseManager>.Exists && PauseManager.Paused) PauseManager.ResumeRequested = true;
                CoopUI.ShownForTest = shownWas; CoopUI.Hidden = hiddenWas;
                if (gm != null && gm.AnyMenuActive) gm.CloseAllMenus();
            }
        }

        // what an open co-op page left behind after the game's resume ("" = nothing)
        static string Leftovers(CoopBehaviour cb, GUIManager gm, int depth0)
        {
            var l = new List<string>();
            if (PauseManager.Paused || Time.timeScale < 0.99f) l.Add("still paused (timeScale " + Time.timeScale + ")");
            if (gm.AnyMenuActive) l.Add("a menu is up");
            if (CoopField.AnyFocused || InputManager.TestString("cooptype")) l.Add("still typing");
            if (InputDepth() != depth0) l.Add("input depth " + InputDepth() + " (was " + depth0 + ")");
            if (cb.WantMouseForTest || cb.TrackerFreedForTest) l.Add("the mouse is still held");
            if (Singleton<InputMethodTracker>.Exists && !Singleton<InputMethodTracker>.Instance.enabled) l.Add("the game's cursor tracker is off");
            if (cb.MenuPageNow != "" || cb.JoinOpenForTest) l.Add("page \"" + cb.MenuPageNow + "\"");
            return string.Join(", ", l.ToArray());
        }

        // one screen size: each piece alone with test content, its boxes against the screen, the game's panels and the pieces before it
        IEnumerator LayoutAt(CoopBehaviour cb, GUIManager gm, AbilityHUD hud, List<string> probs, List<string> notes)
        {
            var cam = CoopHud.Canvas.worldCamera;
            float W = Screen.width, H = Screen.height;
            string at = (int)W + "x" + (int)H;
            // the game's M.O.M side panel up (the join request moves aside for it; nothing may lie on it)
            var msg = new AbilityHUD.HUDMessage("AuditLayoutMsg", true, -2000, AbilityHUD.HUDFlags.SideHint);
            msg.m_sideAlertText = ""; msg.m_stringTop = "M.O.M"; msg.m_stringBottom = "AUDIT LAYOUT MESSAGE WITH A FEW WORDS IN IT";
            AbilityHUD.ShowHUDMessage(msg);
            float t0 = Time.realtimeSinceStartup;
            while ((hud.m_sideHintPanel == null || !hud.m_sideHintPanel.gameObject.activeInHierarchy) && Time.realtimeSinceStartup - t0 < 3f) yield return null;
            yield return StartCoroutine(WaitReal(0.6f));
            string active;
            var panels = GamePanels(hud, out active);
            if (active.IndexOf("HintPanel") < 0 && (hud.m_sideHintPanel == null || !hud.m_sideHintPanel.gameObject.activeInHierarchy)) probs.Add(at + ": the game's M.O.M panel did not come up for the test");
            var pl = new List<string>(); foreach (var p in panels) pl.Add(p.Key + R(p.Value));
            notes.Add(at + " game panels (active " + active + "): " + string.Join(" ", pl.ToArray()));

            var pieces = new List<KeyValuePair<string, Rect>>();
            Chat.Lines.Clear(); CoopUI.ClearOverlayToastsForTest(); cb.ClearPendingForTest(); CoopHud.TagsForTest = new List<CoopHud.TagInfo>();
            var solo = new CoopHud.LinkState { Online = true, InWorld = true, Peers = new List<KeyValuePair<string, bool>>() };
            CoopHud.LinkOverrideForTest = solo;
            string longText = new string('W', 30) + " " + new string('M', 30) + " " + new string('W', 38);

            for (int step = 0; step < 10; step++)
            {
                string name = ""; Transform root = null, extra = null; bool pair = true;
                switch (step)
                {
                    case 0:
                        name = "link";
                        CoopHud.LinkOverrideForTest = new CoopHud.LinkState { Online = true, InWorld = true, InLobby = true, Code = "ABCDEFGHJKLMN", Notice = "sending world 40%",
                            Peers = new List<KeyValuePair<string, bool>> { new KeyValuePair<string, bool>("A VERY LONG STEAM NAME", true), new KeyValuePair<string, bool>("ANOTHER LONG NAME", false), new KeyValuePair<string, bool>("CARL", true) } };
                        yield return StartCoroutine(Frames(3)); root = CoopHud.LinkLayer.Root; break;
                    case 1:
                        name = "chat";
                        for (int i = 0; i < 7; i++) Chat.Lines.Add(new Chat.Line { Name = i % 2 == 0 ? "A VERY LONG STEAM NAME" : "BOB", Text = i == 3 ? longText : "line " + i, At = Time.realtimeSinceStartup, Mine = i == 5 });
                        yield return StartCoroutine(Frames(3)); root = CoopHud.ChatLayer.Root; break;
                    case 2:
                        name = "say";
                        cb.HotkeyForTest(KeyCode.T); yield return StartCoroutine(Frames(3));
                        foreach (char c in longText) cb.ChatFeedForTest(Key(c));
                        for (int i = 0; i < 7; i++) Chat.Lines.Add(new Chat.Line { Name = "A VERY LONG STEAM NAME", Text = longText, At = Time.realtimeSinceStartup });
                        yield return StartCoroutine(Frames(2));
                        root = CoopHud.SayRoot; extra = CoopHud.ChatLayer.Root;          // the SAY> line with the chat log over it (open)
                        break;
                    case 3:
                        name = "mirror";
                        CoopUI.OverlayToast("audit layout " + longText); CoopUI.OverlayToast("audit layout two");
                        yield return StartCoroutine(WaitReal(2.6f));
                        root = CoopHud.MirrorLayer.Root; break;
                    case 4:
                        name = "request";
                        cb.AnswerSinkForTest = delegate { }; cb.AddPendingForTest(4777UL, "A VERY LONG STEAM NAME");
                        yield return StartCoroutine(Frames(3)); root = CoopHud.Request; break;
                    case 5:
                        name = "timer";
                        var runs = SrUiSamples(); runs.RemoveRange(1, runs.Count - 1);
                        SpeedrunUI.PreviewPartForTest(runs, SrUiRaceSample(), "timer");
                        yield return StartCoroutine(Frames(3)); root = CoopHud.SpeedrunLayer.Root; break;
                    case 6:
                        name = "cards"; pair = false;           // shown at the end of a run, over the middle of the screen: inside it and clear of the game's panels
                        var fin = SrUiSamples(); var one = fin[1]; one.State = SrState.Finished; one.FinishedAtRealtime = Time.realtimeSinceStartup;
                        SpeedrunUI.PreviewPartForTest(new List<SrRun> { one }, null, "cards");
                        yield return StartCoroutine(Frames(3)); root = CoopHud.SpeedrunLayer.Root; break;
                    case 7:
                        name = "countdown"; pair = false;
                        SpeedrunUI.PreviewPartForTest(new List<SrRun>(), null, "countdown");
                        yield return StartCoroutine(Frames(3)); root = CoopHud.SpeedrunLayer.Root; break;
                    case 9:
                        name = "strip";
                        SpeedrunUI.PreviewPartForTest(new List<SrRun>(), SrUiRaceSample(), "strip");
                        yield return StartCoroutine(Frames(3)); root = CoopHud.SpeedrunLayer.Root; break;
                    case 8:
                        name = "tags"; pair = false;            // they follow the players: kept inside the screen even for a player far off to the side
                        if (Camera.main != null)
                        {
                            var m = Camera.main.transform;
                            CoopHud.TagsForTest = new List<CoopHud.TagInfo> {
                                new CoopHud.TagInfo { Name = "A VERY LONG STEAM NAME", Head = m.position + m.forward * 2f - m.right * 40f + m.up * 30f, Meters = 999f },
                                new CoopHud.TagInfo { Name = "BOB", Head = m.position + m.forward * 2f + m.right * 40f - m.up * 30f, Meters = 12f } };
                        }
                        yield return StartCoroutine(Frames(3)); root = CoopHud.TagLayer.Root; break;
                }
                var boxes = PieceBoxes(root, cam);
                if (extra != null) boxes.AddRange(PieceBoxes(extra, cam));
                if (boxes.Count == 0) probs.Add(at + " " + name + ": nothing shown for the test");
                Rect all = new Rect();
                foreach (var b in boxes)
                {
                    var r = b.Value; all = Union(all, r);
                    if (r.xMin < 0f || r.yMin < 0f || r.xMax > W || r.yMax > H) probs.Add(at + " " + name + " " + b.Key + " off screen " + R(r));
                    if (name == "tags") continue;
                    foreach (var p in panels) if (Hit(r, p.Value)) probs.Add(at + " " + name + " " + b.Key + " " + R(r) + " on the game's " + p.Key + " " + R(p.Value));
                }
                notes.Add(at + " " + name + " " + R(all));
                if (pair)
                {
                    foreach (var other in pieces) if (Hit(all, other.Value) && !(name == "say" && other.Key == "chat")) probs.Add(at + " " + name + " " + R(all) + " on the " + other.Key + " " + R(other.Value));
                    pieces.Add(new KeyValuePair<string, Rect>(name, all));
                }
                // the countdown also over the pause menu: clear of its title and lines
                if (name == "countdown")
                {
                    var pauseMenu = CoopMenu.Find(gm, "PauseMenu");
                    if (pauseMenu != null)
                    {
                        gm.ActivateMenu(pauseMenu); yield return StartCoroutine(Frames(3));
                        var cd = PieceBoxes(CoopHud.SpeedrunLayer.Root, cam);
                        var mcam = pauseMenu.GetComponent<Canvas>() == null ? cam : pauseMenu.GetComponent<Canvas>().worldCamera;
                        foreach (string part in new[] { "AspectKeeper/MenuGroupPanel", "AspectKeeper/TitlePanel" })
                        {
                            var t = pauseMenu.transform.Find(part) as RectTransform;
                            if (t == null) { probs.Add("the pause menu has no " + part); continue; }
                            var pr = CoopHud.ScreenRect(t, vbuf, mcam);
                            foreach (var b in cd) if (Hit(b.Value, pr)) probs.Add(at + " countdown " + R(b.Value) + " on the pause menu's " + part.Substring(12) + " " + R(pr));
                        }
                        if (cd.Count == 0) probs.Add(at + " countdown not shown over the pause menu");
                        gm.CloseAllMenus(); yield return StartCoroutine(Frames(2));
                    }
                }
                // put the piece away again
                switch (step)
                {
                    case 0: CoopHud.LinkOverrideForTest = solo; break;
                    case 1: Chat.Lines.Clear(); break;
                    case 2: if (cb.ChatOpen) cb.HandleChatKeys(false, true); Chat.Lines.Clear(); break;
                    case 3: CoopUI.ClearOverlayToastsForTest(); break;
                    case 4: cb.ClearPendingForTest(); cb.AnswerSinkForTest = null; break;
                    case 5: case 6: case 7: case 9: SpeedrunUI.StopPreview(); break;
                    case 8: CoopHud.TagsForTest = new List<CoopHud.TagInfo>(); break;
                }
                yield return StartCoroutine(Frames(2));
            }
            AbilityHUD.HideHUDMessage("AuditLayoutMsg");
            CoopHud.LinkOverrideForTest = null; CoopHud.TagsForTest = null;
            yield return StartCoroutine(Frames(2));
        }
    }
}
