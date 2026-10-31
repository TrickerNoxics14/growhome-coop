using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CoopMod
{
    // The native menus that replaced the windows drawn over the game (quick-audit group "ui", in the world): MAKE A ROOM and JOIN A ROOM as pages
    // of the co-op menu, the invite code typed into a uGUI InputField on a menu line, the F2 / F3 / T / Y / N keys, the join request on the mod's
    // own HUD canvas and the invite code said by M.O.M. Everything is driven through the game's own paths (the input module's submit event,
    // the menu's buttons, the keys' own handler) and checked structurally: no screen capture.
    // Steam is never asked to make or join a room here: sinks stand in for it.
    public partial class SelfTest
    {
        public const string NativeMenuIds = "HOTKEYS,MAKE-ROOM-MENU,JOIN-ROOM-MENU,TEXT-ROW,JOIN-REQUEST-HUD,CODE-NOTICE";

        static IEnumerator Frames(int n) { for (int i = 0; i < n; i++) yield return null; }

        static bool SelectionIn(GUIMenu menu)
        {
            var es = EventSystem.current;
            return es != null && es.currentSelectedGameObject != null && es.currentSelectedGameObject.activeInHierarchy && es.currentSelectedGameObject.transform.IsChildOf(menu.transform);
        }

        static List<string> Labels(CoopMenuHandler h) { var l = new List<string>(); foreach (var t in h.RowTextsForTest) l.Add(t == null ? "" : t.text); return l; }

        static Event Key(char c) { return new Event { type = EventType.KeyDown, character = c, keyCode = KeyCode.None }; }

        // "" when a click at the centre of `at` reaches `want` (and nothing under `notUnder`); otherwise what it reached
        static string ClickLandsOn(EventSystem es, Camera cam, RectTransform at, GameObject want, Transform notUnder)
        {
            if (at == null) return "nothing to click";
            var c = new Vector3[4]; at.GetWorldCorners(c);
            Vector2 p = RectTransformUtility.WorldToScreenPoint(cam, (c[0] + c[2]) * 0.5f);
            var hits = new List<RaycastResult>();
            es.RaycastAll(new PointerEventData(es) { position = p }, hits);
            bool under = false; foreach (var r in hits) if (notUnder != null && r.gameObject != null && r.gameObject.transform.IsChildOf(notUnder)) under = true;
            var got = hits.Count == 0 ? null : ExecuteEvents.GetEventHandler<IPointerClickHandler>(hits[0].gameObject);
            if (got == want && !under) return "";
            var names = new List<string>(); foreach (var r in hits) { if (names.Count == 3) break; names.Add(r.gameObject == null ? "?" : r.gameObject.name); }
            return "at " + p + " " + hits.Count + " hits [" + string.Join(",", names.ToArray()) + "] handler " + (got == null ? "none" : got.name) + (under ? " and the field" : "");
        }

        void Submit()
        {
            var es = EventSystem.current;
            if (es != null && es.currentSelectedGameObject != null) ExecuteEvents.Execute(es.currentSelectedGameObject, new BaseEventData(es), ExecuteEvents.submitHandler);
        }

        IEnumerator TestNativeMenus()
        {
            var cb = GetComponent<CoopBehaviour>();
            var gm = Singleton<GUIManager>.Exists ? Singleton<GUIManager>.Instance : null;
            var menu = CoopMenu.Menu;
            var h = CoopMenu.Handler;
            bool online = cb != null && cb.CoopMenuState().Online;
            if (cb == null || gm == null || menu == null || h == null || !online)
            {
                foreach (var id in NativeMenuIds.Split(',')) Check(id, "the native co-op menus can be tested", false, "mod=" + (cb != null) + " gui=" + (gm != null) + " menu=" + (menu != null) + " steam=" + online);
                yield break;
            }
            if (gm.AnyMenuActive) gm.CloseAllMenus();
            yield return null;
            bool inLobby = cb.CoopMenuState().InLobby;
            var rooms = new List<string>(); var joins = new List<ulong>(); var answers = new List<bool>();
            cb.RoomSinkForTest = (o, l) => rooms.Add(o + "/" + l);
            cb.JoinSinkForTest = id => joins.Add(id);
            cb.AnswerSinkForTest = a => answers.Add(a);
            CoopMenuHandler.KeyboardModeForTest = true;
            bool hiddenWas = CoopUI.Hidden;
            string codeFile = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "my-invite-code.txt");
            string codeFileWas = File.Exists(codeFile) ? File.ReadAllText(codeFile) : null;
            bool codeSavedWas = cb.CodeSavedForTest;
            try
            {
                // ---- HOTKEYS: F2 / F3 open the native pages, in the world and over the pause menu; the same key closes; T opens the chat ----
                var keys = new List<string>();
                if (!inLobby)
                {
                    cb.HotkeyForTest(KeyCode.F2); yield return StartCoroutine(Frames(2));
                    bool f2 = gm.AnyMenuActive && CoopMenu.Showing && cb.HostMenuOpenForTest && SelectionIn(menu) && InputManager.TestString("ui") && h.Line1.text == "MAKE A ROOM";
                    cb.HotkeyForTest(KeyCode.F3); yield return StartCoroutine(Frames(3));
                    bool sw = cb.JoinOpenForTest && !cb.HostMenuOpenForTest && CoopMenu.Showing;
                    cb.HotkeyForTest(KeyCode.F3); yield return StartCoroutine(Frames(2));
                    bool f3closed = !gm.AnyMenuActive && !cb.JoinOpenForTest && !InputManager.TestString("cooptype") && !CoopField.AnyFocused;
                    cb.HotkeyForTest(KeyCode.F2); yield return StartCoroutine(Frames(2));
                    bool again = cb.HostMenuOpenForTest;
                    cb.HotkeyForTest(KeyCode.F2); yield return StartCoroutine(Frames(2));
                    bool f2closed = !gm.AnyMenuActive && !cb.HostMenuOpenForTest;
                    keys.Add("F2 opens MAKE A ROOM=" + f2); keys.Add("F3 switches to JOIN=" + sw); keys.Add("F3 again closes=" + f3closed); keys.Add("F2 toggles=" + (again && f2closed));
                }
                else
                {
                    cb.HotkeyForTest(KeyCode.F2); yield return StartCoroutine(Frames(2));
                    keys.Add("F2 in a room opens nothing=" + !gm.AnyMenuActive);
                }
                var pause = CoopMenu.Find(gm, "PauseMenu");
                bool pauseOk = false;
                if (pause != null)
                {
                    gm.ActivateMenu(pause); yield return StartCoroutine(Frames(2));
                    cb.HotkeyForTest(KeyCode.F3); yield return StartCoroutine(Frames(3));
                    bool stacked = cb.JoinOpenForTest && !pause.gameObject.activeInHierarchy && menu.gameObject.activeInHierarchy;
                    h.BackForTest(); yield return StartCoroutine(Frames(2));
                    pauseOk = stacked && pause.gameObject.activeInHierarchy && !menu.gameObject.activeInHierarchy && !cb.JoinOpenForTest && !InputManager.TestString("cooptype");
                    gm.CloseAllMenus(); yield return StartCoroutine(Frames(2));
                }
                keys.Add("F3 over the pause menu stacks and BACK returns to it=" + pauseOk);
                bool chatWas = cb.ChatOpen;
                cb.HotkeyForTest(KeyCode.T); yield return StartCoroutine(Frames(3));
                bool chat = cb.ChatOpen;
                if (cb.ChatOpen && !chatWas) cb.HandleChatKeys(false, true);
                yield return null;
                keys.Add("T opens the chat=" + (chat && !cb.ChatOpen));
                bool allKeys = true; foreach (var k in keys) if (k.EndsWith("=False")) allKeys = false;
                Check("HOTKEYS", "F2 opens the MAKE A ROOM page and F3 the JOIN A ROOM page of the game's own (copied) menu, on the game's selection and input set; the same key closes them; over the pause menu they stack and BACK returns to it; T still opens the chat",
                    allKeys, string.Join("; ", keys.ToArray()));

                // ---- MAKE-ROOM-MENU ----
                if (!inLobby)
                {
                    var probs = new List<string>();
                    string expIds = "host_public,host_accept,host_private,back";
                    string expLabels = "PUBLIC ROOM|LISTED, YOU ACCEPT|PRIVATE ROOM|BACK";
                    foreach (int c in new[] { 1, 4, 2 })
                    {
                        cb.RoomDefaultForTest = c;
                        cb.HotkeyForTest(KeyCode.F2); yield return StartCoroutine(Frames(2));
                        string ids = string.Join(",", h.RowIdsForTest.ToArray()), labels = string.Join("|", Labels(h).ToArray());
                        if (ids != expIds || labels != expLabels) probs.Add("rows " + ids + " / " + labels);
                        string want = CoopMenu.HostRowFor(c);
                        if (h.DefaultIdForTest != want || h.SelectedIdForTest != want) probs.Add("setting " + c + ": default " + h.DefaultIdForTest + " selected " + h.SelectedIdForTest + " want " + want);
                        if (h.Line2.text != CoopMenu.HostHint(want)) probs.Add("setting " + c + ": subtitle \"" + h.Line2.text + "\"");
                        string lay = RowsLayout(h, CoopMenu.RowsFor(cb.CoopMenuState(), out string _a, out string _b), menu);
                        if (lay.Length > 0) probs.Add("layout " + lay);
                        Submit();                                   // Enter / pad A on the selected line, as the game's input module sends it
                        yield return StartCoroutine(Frames(2));
                        string exp = c == 1 ? "True/True" : c == 4 ? "False/True" : "False/False";
                        if (rooms.Count != 1 || rooms[0] != exp || gm.AnyMenuActive) probs.Add("setting " + c + ": Enter made [" + string.Join(",", rooms.ToArray()) + "] want " + exp + " menuOpen=" + gm.AnyMenuActive);
                        rooms.Clear();
                    }
                    cb.RoomDefaultForTest = null;
                    foreach (int n in new[] { 1, 2, 3 })
                    {
                        cb.HotkeyForTest(KeyCode.F2); yield return StartCoroutine(Frames(2));
                        h.HostKey(n); yield return StartCoroutine(Frames(2));
                        string exp = n == 1 ? "True/True" : n == 2 ? "False/True" : "False/False";
                        if (rooms.Count != 1 || rooms[0] != exp || gm.AnyMenuActive) probs.Add("key " + n + " made [" + string.Join(",", rooms.ToArray()) + "] want " + exp);
                        rooms.Clear();
                    }
                    cb.HotkeyForTest(KeyCode.F2); yield return StartCoroutine(Frames(2));
                    var priv = h.ButtonFor("host_private");
                    if (priv != null) EventSystem.current.SetSelectedGameObject(priv.gameObject);
                    yield return StartCoroutine(Frames(2));
                    if (h.Line2.text != CoopMenu.HostHint("host_private")) probs.Add("subtitle does not follow the selection: \"" + h.Line2.text + "\"");
                    h.OnMenuCancel(); yield return StartCoroutine(Frames(2));
                    if (gm.AnyMenuActive || rooms.Count != 0) probs.Add("cancel: menuOpen=" + gm.AnyMenuActive + " made " + rooms.Count);
                    Check("MAKE-ROOM-MENU", "MAKE A ROOM is a page of the game's menu: PUBLIC ROOM / LISTED, YOU ACCEPT / PRIVATE ROOM / BACK inside the panel, the room setting's line selected so Enter makes it, keys 1/2/3 as before, the subtitle names the selected line, cancel makes nothing",
                        probs.Count == 0, probs.Count == 0 ? "3 settings, 3 keys, subtitle and cancel ok" : string.Join("; ", probs.ToArray()));
                }
                else Check("MAKE-ROOM-MENU", "MAKE A ROOM is not offered while I am already in a room (F2 copies the code)", !gm.AnyMenuActive, "in a room");

                // ---- JOIN-ROOM-MENU ----
                {
                    var probs = new List<string>();
                    cb.RoomsForTest = new List<Room>();
                    cb.HotkeyForTest(KeyCode.F3); yield return StartCoroutine(Frames(3));
                    var field = h.CodeField;
                    string ids = string.Join(",", h.RowIdsForTest.ToArray());
                    if (ids != "join_code,join_go,rooms_refresh,back") probs.Add("rows " + ids);
                    string w1, w2; CoopMenu.RowsFor(cb.CoopMenuState(), out w1, out w2);
                    if (h.Line1.text != "JOIN A ROOM" || h.Line2.text != w2) probs.Add("title \"" + h.Line1.text + "\" / \"" + h.Line2.text + "\" want " + w2);
                    bool focused = field != null && field.isFocused && CoopField.AnyFocused && InputManager.TestString("cooptype") && !InputManager.TestString("ui");
                    if (!focused) probs.Add("the code line is not typing the frame after opening (keyboard)");
                    foreach (char c in "ab1c") h.FeedForTest(Key(c));
                    if (field == null || field.text != "AB1C" || cb.JoinTextNow != "AB1C") probs.Add("typed ab1c, got \"" + (field == null ? "" : field.text) + "\"");
                    foreach (char c in "xxxxxxxxxxxxxxxxxxxxxxxxx") h.FeedForTest(Key(c));
                    if (field != null && field.text.Length != CoopMenu.CodeLimit) probs.Add("limit: " + field.text.Length + " letters");
                    // keys that belong to the game while typing: nothing happens
                    cb.AddPendingForTest(777UL, "ANNA");
                    cb.HotkeyForTest(KeyCode.Y); cb.HotkeyForTest(KeyCode.N); cb.HotkeyForTest(KeyCode.T);
                    if (answers.Count != 0 || cb.ChatOpen || cb.PendingCountRaw != 1) probs.Add("Y/N/T acted while typing: answers " + answers.Count + " chat " + cb.ChatOpen);
                    cb.ClearPendingForTest();
                    // a bad code: the reason in the subtitle, the page stays and keeps typing
                    if (field != null) field.text = "NOTACODE";
                    h.FeedForTest(Event.KeyboardEvent("return")); yield return StartCoroutine(Frames(2));
                    if (!cb.JoinOpenForTest || joins.Count != 0 || !h.Line2.text.StartsWith("THAT CODE") || field == null || !field.isFocused) probs.Add("bad code: open=" + cb.JoinOpenForTest + " joined " + joins.Count + " says \"" + h.Line2.text + "\" typing=" + (field != null && field.isFocused));
                    // a good code: joined, the page closes, every key back
                    if (field != null) field.text = InviteCode.Encode(123456789UL).ToLowerInvariant();
                    h.FeedForTest(Event.KeyboardEvent("return")); yield return StartCoroutine(Frames(2));
                    if (joins.Count != 1 || joins[0] != 123456789UL || gm.AnyMenuActive || CoopField.AnyFocused || InputManager.TestString("cooptype")) probs.Add("good code: joined [" + string.Join(",", joins.ConvertAll(x => x.ToString()).ToArray()) + "] menuOpen=" + gm.AnyMenuActive + " typing=" + CoopField.AnyFocused);
                    joins.Clear();
                    // Esc while typing: closed with nothing joined
                    cb.HotkeyForTest(KeyCode.F3); yield return StartCoroutine(Frames(3));
                    foreach (char c in "abc") h.FeedForTest(Key(c));
                    h.FeedForTest(Event.KeyboardEvent("escape"));
                    for (int i = 0; i < 10 && cb.JoinOpenForTest; i++) yield return null;
                    yield return null;
                    if (cb.JoinOpenForTest || gm.AnyMenuActive || joins.Count != 0 || InputManager.TestString("cooptype")) probs.Add("Esc: open=" + cb.JoinOpenForTest + " joined " + joins.Count);
                    // the pad: no typing on opening (it would eat the stick), JOIN WITH CODE selected
                    CoopMenuHandler.KeyboardModeForTest = false;
                    cb.HotkeyForTest(KeyCode.F3); yield return StartCoroutine(Frames(3));
                    if (h.DefaultIdForTest != "join_go" || h.SelectedIdForTest != "join_go" || (field != null && field.isFocused)) probs.Add("pad: default " + h.DefaultIdForTest + " selected " + h.SelectedIdForTest + " typing " + (field != null && field.isFocused));
                    h.BackForTest(); yield return StartCoroutine(Frames(2));
                    CoopMenuHandler.KeyboardModeForTest = true;
                    // five listed rooms: three to a page, MORE shows the other two, REFRESH asks again, a room line joins that room
                    cb.RoomsForTest = new List<Room>();
                    for (int i = 0; i < 5; i++) cb.RoomsForTest.Add(new Room { Id = 901UL + (ulong)i, Name = "Room " + (char)('A' + i), Players = 1 + i % 3, Max = 8, Ask = i % 2 == 1 });
                    cb.HotkeyForTest(KeyCode.F3); yield return StartCoroutine(Frames(3));
                    ids = string.Join(",", h.RowIdsForTest.ToArray());
                    if (ids != "join_code,join_go,room:901,room:902,room:903,rooms_more,rooms_refresh,back") probs.Add("rooms rows " + ids);
                    var want = CoopMenu.RowsFor(cb.CoopMenuState(), out w1, out w2);
                    string lay = RowsLayout(h, want, menu);
                    if (lay.Length > 0) probs.Add("rooms layout " + lay);
                    var labels = Labels(h);
                    if (labels.Count > 3 && labels[3] != CoopMenu.RoomLabel(cb.RoomsForTest[1])) probs.Add("room label \"" + labels[3] + "\"");
                    yield return StartCoroutine(WaitReal(CoopMenuHandler.SettleTime + 0.1f));
                    var more = h.ButtonFor("rooms_more");
                    if (more != null) more.onClick.Invoke();
                    yield return null;
                    ids = string.Join(",", h.RowIdsForTest.ToArray());
                    if (!ids.Contains("room:904") || !ids.Contains("room:905") || ids.Contains("room:901")) probs.Add("MORE: " + ids);
                    yield return StartCoroutine(WaitReal(CoopMenuHandler.SettleTime + 0.1f));
                    int refreshed = cb.RefreshCountForTest;
                    var refresh = h.ButtonFor("rooms_refresh");
                    if (refresh != null) refresh.onClick.Invoke();
                    yield return null;
                    if (cb.RefreshCountForTest != refreshed + 1) probs.Add("REFRESH did not ask again");
                    yield return StartCoroutine(WaitReal(CoopMenuHandler.SettleTime + 0.1f));
                    var r5 = h.ButtonFor("room:905");
                    if (r5 != null) r5.onClick.Invoke();
                    yield return StartCoroutine(Frames(2));
                    if (joins.Count != 1 || joins[0] != 905UL || gm.AnyMenuActive) probs.Add("room line: joined [" + string.Join(",", joins.ConvertAll(x => x.ToString()).ToArray()) + "] menuOpen=" + gm.AnyMenuActive);
                    joins.Clear();
                    // the subtitle for every state (pure)
                    var st = new CoopMenu.State { Online = true, Page = CoopMenu.PageJoin, Rooms = new List<Room>() };
                    st.Listing = true; CoopMenu.RowsFor(st, out w1, out w2); bool looking = w2 == "LOOKING...";
                    st.Listing = false; CoopMenu.RowsFor(st, out w1, out w2); bool none = w2 == "NO ROOMS RIGHT NOW";
                    st.JoinMsg = "That code does not look right. Letters and numbers, 13 in all."; CoopMenu.RowsFor(st, out w1, out w2); bool msg = w2.StartsWith("THAT CODE") && w2.Length <= 44;
                    if (!looking || !none || !msg) probs.Add("subtitles looking=" + looking + " none=" + none + " message=" + msg);
                    Check("JOIN-ROOM-MENU", "JOIN A ROOM is a page of the game's menu: the code typed into the code line (upper case, 20 at most, game keys quiet meanwhile), Enter joins a good code and closes, a bad code keeps the page with the reason, Esc closes with nothing joined, a pad starts on JOIN WITH CODE, 5 rooms 3 to a page with MORE, REFRESH asks again, a room line joins it",
                        probs.Count == 0, probs.Count == 0 ? "typing, limit, keys, bad/good code, Esc, pad, rooms, refresh ok" : string.Join("; ", probs.ToArray()));
                }

                // ---- TEXT-ROW: the code line is the game's own line (its highlight), the field never takes the mouse ----
                {
                    var probs = new List<string>();
                    cb.RoomsForTest = new List<Room>();
                    cb.HotkeyForTest(KeyCode.F3); yield return StartCoroutine(Frames(3));
                    var row = h.TextRowForTest; var field = h.CodeField;
                    var es = EventSystem.current;
                    if (row == null || field == null || es == null) probs.Add("no code line");
                    else
                    {
                        var button = row.GetComponent<Button>(); var anim = row.GetComponent<Animator>(); var tAnim = h.Template.GetComponent<Animator>();
                        if (button == null || button.transition != h.Template.GetComponent<Button>().transition) probs.Add("not a game button");
                        if (anim == null || tAnim == null || anim.runtimeAnimatorController != tAnim.runtimeAnimatorController) probs.Add("not the game's highlight animation");
                        var group = field.GetComponent<CanvasGroup>();
                        if (group == null || group.blocksRaycasts) probs.Add("the field would take the mouse");
                        if (field.navigation.mode != Navigation.Mode.None) probs.Add("the field is in the menu's navigation");
                        var label = row.transform.Find("ButText") == null ? null : row.transform.Find("ButText").GetComponent<Text>();
                        if (label == null || field.textComponent == null || field.textComponent.font != label.font || label.text != "CODE>") probs.Add("font/label");
                        if (es.currentSelectedGameObject != row) probs.Add("the code line is not the selection");
                        var cam = menu.GetComponent<Canvas>() == null ? null : menu.GetComponent<Canvas>().worldCamera;
                        var rt = (RectTransform)row.transform;
                        var corners = new Vector3[4]; rt.GetWorldCorners(corners);
                        // a click where the player sees the line (its label, then the typed text): it must reach the line, never the field
                        string atLabel = ClickLandsOn(es, cam, label == null ? null : (RectTransform)label.transform, row, field.transform);
                        string atField = ClickLandsOn(es, cam, field.textComponent == null ? null : (RectTransform)field.textComponent.transform, row, field.transform);
                        if (atLabel.Length > 0 || atField.Length > 0)
                        {
                            var go = h.ButtonFor("join_go"); var goText = go == null ? null : go.transform.Find("ButText");
                            string control = goText == null ? "no JOIN WITH CODE line" : ClickLandsOn(es, cam, (RectTransform)goText, go.gameObject, null);
                            probs.Add("click on the label: " + (atLabel.Length > 0 ? atLabel : "ok") + "; on the typed text: " + (atField.Length > 0 ? atField : "ok") + "; control JOIN WITH CODE: " + (control.Length > 0 ? control : "ok") + "; screen " + Screen.width + "x" + Screen.height + " camera " + (cam == null ? "none" : cam.name));
                        }
                        var frt = (RectTransform)field.transform; var fc = new Vector3[4]; frt.GetWorldCorners(fc);
                        if (!(fc[0].x >= corners[0].x - 0.01f && fc[2].x <= corners[2].x + 0.01f)) probs.Add("the field sticks out of its line");
                    }
                    h.BackForTest(); yield return StartCoroutine(Frames(2));
                    Check("TEXT-ROW", "the code line is a copy of the game's own menu line (button, highlight animation, font, selected), and the field in it is not hit by the mouse, not in the navigation and stays inside the line",
                        probs.Count == 0, probs.Count == 0 ? "ok" : string.Join("; ", probs.ToArray()));
                }

                // ---- JOIN-REQUEST-HUD ----
                {
                    var probs = new List<string>();
                    if (gm.AnyMenuActive) gm.CloseAllMenus();
                    CoopUI.Hidden = false;
                    cb.AddPendingForTest(4242UL, "ANNA");
                    yield return StartCoroutine(Frames(2));
                    var canvas = CoopHud.Canvas;
                    if (canvas == null || canvas.transform.parent != gm.transform || canvas.renderMode != RenderMode.ScreenSpaceCamera || canvas.worldCamera != gm.ActiveCamera || canvas.GetComponent<GraphicRaycaster>() != null)
                        probs.Add("HUD canvas " + (canvas == null ? "missing" : canvas.renderMode + " camera " + (canvas.worldCamera == null ? "none" : canvas.worldCamera.name)));
                    if (!CoopHud.RequestShownForTest) probs.Add("the request is not shown");
                    else
                    {
                        if (CoopHud.RequestTop.text != "M.O.M>>> ANNA WANTS TO JOIN" || CoopHud.RequestKeys.text != "Y LET IN     N REFUSE") probs.Add("says \"" + CoopHud.RequestTop.text + "\" / \"" + CoopHud.RequestKeys.text + "\"");
                        Font game = Singleton<AbilityHUD>.Exists && Singleton<AbilityHUD>.Instance.m_textBottomLine != null ? Singleton<AbilityHUD>.Instance.m_textBottomLine.font : null;
                        if (game == null || CoopHud.RequestTop.font != game || CoopHud.RequestKeys.font != game) probs.Add("not the game's HUD font");
                        var r = CoopHud.ScreenRect(CoopHud.Request, new Vector3[4], canvas.worldCamera);
                        if (r.xMin < 0f || r.yMin < 0f || r.xMax > Screen.width || r.yMax > Screen.height) probs.Add("off screen " + r);
                    }
                    cb.HotkeyForTest(KeyCode.F3); yield return StartCoroutine(Frames(2));
                    if (CoopHud.RequestShownForTest) probs.Add("shown over the JOIN A ROOM page");
                    h.BackForTest(); yield return StartCoroutine(Frames(2));
                    if (!inLobby)
                    {
                        cb.HotkeyForTest(KeyCode.F2); yield return StartCoroutine(Frames(2));
                        if (CoopHud.RequestShownForTest) probs.Add("shown over the MAKE A ROOM page");
                        h.BackForTest(); yield return StartCoroutine(Frames(2));
                    }
                    CoopUI.Hidden = true; yield return StartCoroutine(Frames(2));
                    if (CoopHud.RequestShownForTest) probs.Add("F1 does not hide it");
                    cb.HotkeyForTest(KeyCode.Y); yield return null;
                    if (answers.Count != 1 || !answers[0]) probs.Add("Y while hidden did not let in (" + answers.Count + ")");
                    yield return StartCoroutine(Frames(2));
                    if (CoopHud.RequestShownForTest) probs.Add("still shown after the answer");
                    CoopUI.Hidden = false;
                    cb.AddPendingForTest(4243UL, "BOB");
                    cb.CoopMenuAction("refuse");
                    if (answers.Count != 2 || answers[1]) probs.Add("the menu's REFUSE did not refuse");
                    cb.ClearPendingForTest();
                    Check("JOIN-REQUEST-HUD", "a player asking to join is shown on the mod's own HUD canvas (the game's GUI camera, no clicks) in the game's HUD font, not over the co-op pages, hidden by F1; Y still lets in while hidden, the menu's REFUSE refuses",
                        probs.Count == 0, probs.Count == 0 ? "ok" : string.Join("; ", probs.ToArray()));
                }

                // ---- CODE-NOTICE: the invite code is said by M.O.M (no card drawn over the game), once, copied and saved ----
                {
                    var probs = new List<string>();
                    int offers = NativeUI.Offers;
                    const string code = "TEST-CODE-ABCDE";
                    cb.CodeNoticeForTest(code);
                    if (!cb.CodeSavedForTest || NativeUI.Offers != offers + 1 || !NativeUI.LastOffered.Contains("INVITE CODE " + code) || !NativeUI.LastOffered.Contains("F3")) probs.Add("offered " + (NativeUI.Offers - offers) + " \"" + NativeUI.LastOffered + "\"");
                    string saved = File.Exists(codeFile) ? File.ReadAllText(codeFile) : "";
                    if (saved != code) probs.Add("my-invite-code.txt \"" + saved + "\"");
                    try { var te = new TextEditor(); te.Paste(); if (te.content == null || te.content.text != code) probs.Add("clipboard \"" + (te.content == null ? "" : te.content.text) + "\""); } catch (Exception e) { probs.Add("clipboard " + e.GetType().Name); }
                    int after = NativeUI.Offers; string last = NativeUI.LastOffered;
                    yield return StartCoroutine(Frames(3));
                    if (NativeUI.LastOffered != last && NativeUI.LastOffered.Contains(code)) probs.Add("offered a second time");
                    Check("CODE-NOTICE", "a new room's invite code is copied, saved to my-invite-code.txt and said once by M.O.M (INVITE CODE ... / FRIENDS PRESS F3), from Update, with no card drawn over the game",
                        probs.Count == 0, probs.Count == 0 ? "ok" : string.Join("; ", probs.ToArray()));
                    NativeUI.Reset(); CoopUI.ClearOverlayToastsForTest();
                }
                Note("menu keys: " + (NativeUI.KeysLine.Length > 0 ? NativeUI.KeysLine : "not read"));
            }
            finally
            {
                cb.RoomSinkForTest = null; cb.JoinSinkForTest = null; cb.AnswerSinkForTest = null;
                cb.RoomsForTest = null; cb.RoomDefaultForTest = null; cb.ClearPendingForTest();
                CoopMenuHandler.KeyboardModeForTest = null;
                CoopUI.Hidden = hiddenWas;
                cb.CodeSavedForTest = codeSavedWas;
                try { if (codeFileWas != null) File.WriteAllText(codeFile, codeFileWas); else if (File.Exists(codeFile)) File.Delete(codeFile); } catch (Exception) { }
                if (gm != null && gm.AnyMenuActive) gm.CloseAllMenus();
            }
        }
    }
}
