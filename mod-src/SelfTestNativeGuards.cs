using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CoopMod
{
    // What co-op says while a game menu is up, and the keys around the native pages (quick-audit group "ui"):
    //  MENU-NOTICE (start screen): a join request, the invite code of a room just made and a join Steam refused are said on the start menu's own
    //    notice line (a copy of its line: the game's font, nothing to select, inside the screen, clear of the other lines), never lost.
    //  MESSAGES-KEPT (world): the pause menu says M.O.M messages on its notice line; under another game menu messages and chat lines do not age,
    //    and show on the HUD once it closes (15 s and more later).
    //  PAGE-ESC (world): Esc on a page closes it like BACK while the game runs, with the pause key muted until the key is up, and the page opened
    //    over the SPEEDRUN screen goes back to that screen.
    //  TYPING-GUARDS (world): the mouse cannot stop the code typing, Backspace plays no cancel sound, a cutscene's input set never gets the typed
    //    keys (code line and chat), T over the pause menu leaves it its selection.
    //  LINK-FIT (pure): the B.U.D. LINK line keeps the room code and the notice when the players do not fit.
    // Everything goes through the mod's own code paths, structurally (no screen capture); Steam is never asked for anything (sinks stand in).
    public partial class SelfTest
    {
        public const string StartNoticeIds = "MENU-NOTICE";
        public const string NativeGuardIds = "MESSAGES-KEPT,PAGE-ESC,TYPING-GUARDS";

        static string Overlaps(RectTransform a, Transform group, Transform except)
        {
            var ca = new Vector3[4]; a.GetWorldCorners(ca);
            var ra = Rect.MinMaxRect(ca[0].x, ca[0].y, ca[2].x, ca[2].y);
            for (int i = 0; i < group.childCount; i++)
            {
                var c = group.GetChild(i) as RectTransform;
                if (c == null || c == except || !c.gameObject.activeInHierarchy) continue;
                var cb = new Vector3[4]; c.GetWorldCorners(cb);
                var rb = Rect.MinMaxRect(cb[0].x, cb[0].y, cb[2].x, cb[2].y);
                float ox = Mathf.Min(ra.xMax, rb.xMax) - Mathf.Max(ra.xMin, rb.xMin), oy = Mathf.Min(ra.yMax, rb.yMax) - Mathf.Max(ra.yMin, rb.yMin);
                if (ox > 0.5f && oy > 0.5f && oy > 0.01f * Mathf.Max(ra.height, 1f)) return c.name;
            }
            return "";
        }

        // The game's own mouse handling: hovering a line selects it (a move first clears the selection). A selection that moved onto the very
        // line the real pointer is over was moved by the player's mouse, not by the mod; any other move (or onto the notice line) still counts.
        static Vector2[] ScreenCorners(RectTransform rt, Camera cam)
        {
            var wc = new Vector3[4]; rt.GetWorldCorners(wc);
            var sc = new Vector2[4];
            for (int k = 0; k < 4; k++) sc[k] = RectTransformUtility.WorldToScreenPoint(cam, wc[k]);
            return sc;
        }

        class Settled { public float Max, Took; public bool Still; }

        // for a failure's log only: the pause menu's scale, panel and rows (local position, size), and its layout settings
        static string PauseLayoutText(GUIMenu pause)
        {
            var sb = new System.Text.StringBuilder();
            var ak = pause.transform.Find("AspectKeeper") as RectTransform;
            var g = pause.transform.Find("AspectKeeper/MenuGroupPanel") as RectTransform;
            sb.Append("menu scale ").Append(pause.transform.lossyScale.ToString("F4"));
            if (ak != null) sb.Append(" keeper ").Append(ak.rect.ToString()).Append(" scale ").Append(ak.localScale.ToString("F4"));
            if (g == null) return sb.ToString();
            sb.Append(" panel ").Append(g.rect.ToString()).Append(" at ").Append(g.anchoredPosition.ToString("F2")).Append(" scale ").Append(g.localScale.ToString("F4"));
            var vl = g.GetComponent<VerticalLayoutGroup>();
            if (vl != null) sb.Append(" layout spacing ").Append(vl.spacing).Append(" align ").Append(vl.childAlignment).Append(" expand ").Append(vl.childForceExpandHeight).Append(" pad ").Append(vl.padding.top).Append("/").Append(vl.padding.bottom);
            foreach (var comp in g.GetComponents<Component>()) sb.Append(" [").Append(comp.GetType().Name).Append("]");
            for (int i = 0; i < g.childCount; i++)
            {
                var c = g.GetChild(i) as RectTransform;
                if (c == null) continue;
                var le = c.GetComponent<LayoutElement>();
                sb.Append("; ").Append(c.name).Append(c.gameObject.activeSelf ? "" : "(off)").Append(" ").Append(c.anchoredPosition.ToString("F2")).Append(" ").Append(c.rect.size.ToString("F2"))
                  .Append(" scale ").Append(c.localScale.ToString("F3"));
                if (le != null) sb.Append(" le ").Append(le.ignoreLayout ? "ignore " : "").Append(le.minHeight).Append("/").Append(le.preferredHeight).Append("/").Append(le.flexibleHeight);
            }
            return sb.ToString();
        }

        // re-takes the screen corners of the given lines each frame until they stood still for 3 frames in a row (at most 2 s of real time), so a
        // menu that is still settling after it opened is not taken for one moved by the notice line; d ends with the settled corners
        static IEnumerator Settle(Dictionary<RectTransform, Vector2[]> d, Camera cam, Settled r)
        {
            float from = Time.realtimeSinceStartup; int still = 0;
            while (still < 3 && Time.realtimeSinceStartup - from < 2f)
            {
                yield return null;
                float moved = 0f;
                foreach (var c in new List<RectTransform>(d.Keys))
                {
                    if (c == null) { d.Remove(c); continue; }
                    var sc = ScreenCorners(c, cam);
                    for (int k = 0; k < 4; k++) moved = Mathf.Max(moved, (sc[k] - d[c][k]).magnitude);
                    d[c] = sc;
                }
                r.Max = Mathf.Max(r.Max, moved);
                still = moved < 0.05f ? still + 1 : 0;
            }
            r.Still = still >= 3; r.Took = Time.realtimeSinceStartup - from;
        }

        static bool UnderPointer(GameObject go, GUIMenu menu)
        {
            if (go == null || menu == null) return false;
            var rt = go.transform as RectTransform;
            Canvas cv = null;
            for (var t = go.transform; t != null && cv == null; t = t.parent) cv = t.GetComponent<Canvas>();
            if (rt == null || cv == null || !go.transform.IsChildOf(menu.transform)) return false;
            var cam = cv.renderMode == RenderMode.ScreenSpaceOverlay ? null : cv.worldCamera;
            return RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition, cam);
        }

        // the notice line of a menu: built, a copy of its own line, inert; "" when all is well
        static string NoticeLineProblems(GameObject line, Transform like)
        {
            if (line == null) return "no notice line";
            if (like == null) return "no line to compare with";
            if (line.transform.parent != like.parent) return "not in the menu's list of lines";
            if (line.GetComponent<Selectable>() != null || line.GetComponentInChildren<Selectable>() != null) return "selectable";
            if (line.GetComponent<Animator>() != null) return "has the highlight animation";
            var t = line.transform.Find("ButText") == null ? null : line.transform.Find("ButText").GetComponent<Text>();
            var lt = like.Find("ButText") == null ? null : like.Find("ButText").GetComponent<Text>();
            if (t == null || lt == null || t.font != lt.font) return "font " + (t == null || t.font == null ? "none" : t.font.name) + " not the menu's " + (lt == null || lt.font == null ? "none" : lt.font.name);
            if (t.color != lt.color) return "colour " + t.color + " not the menu's " + lt.color;
            return "";
        }

        IEnumerator WaitNotice(GUIMenu m, string starts, float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end && !MenuNotice.TextNow(m).StartsWith(starts)) yield return null;
        }

        // ---- MENU-NOTICE: on the start screen, before the game starts --------------------------------------------------------------
        IEnumerator TestStartNotice()
        {
            var cb = GetComponent<CoopBehaviour>();
            var gm = Singleton<GUIManager>.Exists ? Singleton<GUIManager>.Instance : null;
            var start = gm == null ? null : CoopMenu.Find(gm, "StartMenu");
            bool online = cb != null && cb.IsOnline;
            if (cb == null || start == null || !online || !cb.HasLobbyForTest)
            {
                Check("MENU-NOTICE", "the start menu's notice line can be tested", false, "mod=" + (cb != null) + " start=" + (start != null) + " steam=" + online);
                yield break;
            }
            if (CoopMenu.Menu != null && CoopMenu.Menu.gameObject.activeInHierarchy) CoopMenu.Menu.Close();
            yield return StartCoroutine(Frames(2));
            var probs = new List<string>();
            var answers = new List<bool>();
            cb.AnswerSinkForTest = a => answers.Add(a);
            bool hiddenWas = CoopUI.Hidden, shownWas = CoopUI.ShownForTest; CoopUI.ShownForTest = true; CoopUI.Hidden = false;      // the display on, whatever the player chose with F1 (the settings tick reads coop-ui-hidden.txt again)
            string codeFile = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "my-invite-code.txt");
            string codeFileWas = File.Exists(codeFile) ? File.ReadAllText(codeFile) : null;
            bool codeSavedWas = cb.CodeSavedForTest;
            string statusWas = cb.LobbyStatusForTest;
            NativeUI.Reset(); CoopUI.ClearOverlayToastsForTest(); MenuNotice.ResetForTest();
            yield return StartCoroutine(Frames(2));      // let MenuNotice.Tick run on the cleared state: a message shown before the reset (e.g. guest mode) is taken down by the tick, not by the reset
            try
            {
                var line = MenuNotice.StartLineForTest;
                var like = start.transform.Find("Panel/ButtonPanel/SettingsButton");
                string lp = NoticeLineProblems(line, like);
                if (lp.Length > 0) probs.Add("start line: " + lp);
                string pp = NoticeLineProblems(MenuNotice.PauseLineForTest, gm == null ? null : CoopMenu.Find(gm, "PauseMenu") == null ? null : CoopMenu.Find(gm, "PauseMenu").transform.Find("AspectKeeper/MenuGroupPanel/ButtonCamera"));
                if (pp.Length > 0) probs.Add("pause line: " + pp);
                if (!CoopMenu.StartMenuShowing()) probs.Add("the start menu is not the menu on screen");
                if (line != null && line.activeInHierarchy) probs.Add("shown with nothing to say: \"" + MenuNotice.TextNow(start) + "\"");

                // 1. a player asks to join while the host sits on the start menu
                var es = EventSystem.current;
                var cam0 = start.GetComponent<Canvas>() == null ? null : start.GetComponent<Canvas>().worldCamera;
                var linesBefore = new Dictionary<RectTransform, Vector2[]>();      // where the menu's own lines sit on screen before the notice line shows
                if (line != null)
                    for (int i = 0; i < line.transform.parent.childCount; i++)
                    {
                        var c = line.transform.parent.GetChild(i) as RectTransform;
                        if (c == null || c == line.transform || !c.gameObject.activeInHierarchy) continue;
                        linesBefore[c] = ScreenCorners(c, cam0);
                    }
                var settled = new Settled();      // a start menu that only just opened may still be settling: "before" is taken once its lines stand still
                yield return StartCoroutine(Settle(linesBefore, cam0, settled));
                if (settled.Max > 0.05f) CoopBehaviour.Log("audit: the start menu's lines were still moving after it opened (up to " + settled.Max.ToString("0.0") + " px a frame); " + (settled.Still ? "still after " : "not still after ") + settled.Took.ToString("0.00") + " s");
                if (!settled.Still) probs.Add("the start menu's lines never stood still before the notice (up to " + settled.Max.ToString("0.0") + " px a frame)");
                var selBefore = es == null ? null : es.currentSelectedGameObject;
                var mouseBefore = Input.mousePosition;
                bool mouseUsed = false;      // the player's real mouse moved, pressed or scrolled while the request went up (the game's input module then selects on hover, or clears the selection)
                cb.AddPendingForTest(5151UL, "ANNA");
                for (int f = 0; f < 3; f++)
                {
                    yield return null;
                    if ((Input.mousePosition - mouseBefore).sqrMagnitude > 0.01f || Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2) || Input.mouseScrollDelta.sqrMagnitude > 0f) mouseUsed = true;
                }
                foreach (var kv in linesBefore)
                {
                    if (kv.Key == null || !kv.Key.gameObject.activeInHierarchy) continue;
                    var now = ScreenCorners(kv.Key, cam0);
                    float shift = 0f;
                    for (int k = 0; k < 4; k++) shift = Mathf.Max(shift, (now[k] - kv.Value[k]).magnitude);
                    if (shift > 0.5f) probs.Add("the notice line moves the menu's lines (" + kv.Key.name + " by " + shift.ToString("0.0") + " px)");
                }
                string want = CoopHud.RequestTopFor("ANNA") + "\n" + CoopHud.RequestKeysText;
                string got = MenuNotice.TextNow(start);
                if (got != want) probs.Add("join request says \"" + got.Replace("\n", "/") + "\"");
                if (line != null && line.activeInHierarchy)
                {
                    var cam = start.GetComponent<Canvas>() == null ? null : start.GetComponent<Canvas>().worldCamera;
                    Rect r;
                    if (cam != null && !Inside((RectTransform)line.transform, cam, 2f, out r)) probs.Add("the notice line is off screen " + r);
                    string over = Overlaps((RectTransform)line.transform, line.transform.parent, line.transform);
                    if (over.Length > 0) probs.Add("the notice line lies on " + over);
                    var t = line.transform.Find("ButText").GetComponent<Text>();
                    if (t.cachedTextGenerator != null && t.cachedTextGenerator.lineCount > 2) probs.Add("the notice needs " + t.cachedTextGenerator.lineCount + " lines");
                }
                var selNow = es == null ? null : es.currentSelectedGameObject;
                bool byMouse = selNow != null && selNow != line && UnderPointer(selNow, start);      // the player's real mouse hovered that line (the game selects on hover)
                if (byMouse) CoopBehaviour.Log("audit: the start menu's selection went to " + selNow.name + ", the line under the real mouse pointer (the game's hover)");
                bool clearedByMouse = selNow == null && selBefore != null && mouseUsed;      // the game's input module drops the selection when the real mouse moves (and the pointer is off the lines)
                if (clearedByMouse) CoopBehaviour.Log("audit: the start menu's selection was cleared while the real mouse moved, pressed or scrolled (the game's input module)");
                if (es != null && ((selNow != selBefore && !byMouse && !clearedByMouse) || (line != null && selNow == line))) probs.Add("the start menu's selection moved to " + (selNow == null ? "none" : selNow.name) + " (from " + (selBefore == null ? "none" : selBefore.name) + ")");
                if (CoopHud.RequestShownForTest) probs.Add("the HUD strip shows under the menu too");
                cb.HotkeyForTest(KeyCode.Y);
                yield return StartCoroutine(Frames(3));
                if (answers.Count != 1 || !answers[0]) probs.Add("Y on the start menu did not let in (" + answers.Count + ")");
                if (MenuNotice.TextNow(start).Length > 0) probs.Add("still says \"" + MenuNotice.TextNow(start).Replace("\n", "/") + "\" after the answer");
                cb.ClearPendingForTest();

                // 2. a room made from the start menu: its invite code (the M.O.M message that could not reach the hidden HUD)
                const string code = "TEST-START-CODE";
                cb.CodeNoticeForTest(code);
                yield return StartCoroutine(WaitNotice(start, "M.O.M>>> INVITE CODE", 2f));
                got = MenuNotice.TextNow(start);
                if (!got.StartsWith("M.O.M>>> INVITE CODE " + code) || !got.Contains("F3")) probs.Add("code notice says \"" + got + "\"");
                NativeUI.Reset(); CoopUI.ClearOverlayToastsForTest(); MenuNotice.ResetForTest();
                yield return StartCoroutine(Frames(2));

                // 3. Steam says no to a join asked from the start menu
                cb.JoinDidNotWorkForTest();
                yield return StartCoroutine(WaitNotice(start, "M.O.M>>> COULD NOT JOIN", 2f));
                got = MenuNotice.TextNow(start);
                if (!got.StartsWith("M.O.M>>> COULD NOT JOIN")) probs.Add("refused join says \"" + got + "\"");
                if (cb.LobbyStatusForTest != Lobby.JoinNoText) probs.Add("room status \"" + cb.LobbyStatusForTest + "\"");

                // 4. F1 hides it, as it hides the HUD pieces
                CoopUI.Hidden = true; yield return StartCoroutine(Frames(2));
                if (MenuNotice.TextNow(start).Length > 0) probs.Add("F1 does not hide it");
                CoopUI.Hidden = false;
                Check("MENU-NOTICE", "on the start menu co-op speaks on the menu's own notice line (a copy of its line: the game's font and colour, nothing to select, inside the screen, clear of the other lines): a player asking to join with Y / N, the invite code of a room just made, a join Steam refused; F1 hides it",
                    probs.Count == 0, probs.Count == 0 ? "join request, Y, invite code, refused join, F1 ok" : string.Join("; ", probs.ToArray()));
            }
            finally
            {
                cb.AnswerSinkForTest = null; cb.ClearPendingForTest();
                CoopUI.Hidden = hiddenWas; CoopUI.ShownForTest = shownWas;
                cb.CodeSavedForTest = codeSavedWas;
                cb.LobbyStatusForTest = statusWas;
                try { if (codeFileWas != null) File.WriteAllText(codeFile, codeFileWas); else if (File.Exists(codeFile)) File.Delete(codeFile); } catch (Exception) { }
                NativeUI.Reset(); CoopUI.ClearOverlayToastsForTest(); MenuNotice.ResetForTest();
            }
        }

        // ---- MESSAGES-KEPT, PAGE-ESC, TYPING-GUARDS: in the world -----------------------------------------------------------------------
        IEnumerator TestNativeGuards()
        {
            var cb = GetComponent<CoopBehaviour>();
            var gm = Singleton<GUIManager>.Exists ? Singleton<GUIManager>.Instance : null;
            var menu = CoopMenu.Menu;
            var h = CoopMenu.Handler;
            var pause = gm == null ? null : CoopMenu.Find(gm, "PauseMenu");
            var options = gm == null ? null : CoopMenu.Find(gm, "OptionsMenu");
            bool online = cb != null && cb.IsOnline;
            if (cb == null || gm == null || menu == null || h == null || pause == null || options == null || !online)
            {
                foreach (var id in NativeGuardIds.Split(',')) Check(id, "the native menu guards can be tested", false, "mod=" + (cb != null) + " gui=" + (gm != null) + " menu=" + (menu != null) + " pause=" + (pause != null) + " steam=" + online);
                yield break;
            }
            if (gm.AnyMenuActive) gm.CloseAllMenus();
            yield return StartCoroutine(Frames(2));
            bool hiddenWas = CoopUI.Hidden, showChatWas = CoopBehaviour.ShowChat, shownWas = CoopUI.ShownForTest;
            CoopUI.ShownForTest = true;
            CoopUI.Hidden = false; CoopBehaviour.ShowChat = true;
            CoopMenuHandler.KeyboardModeForTest = true;
            var joins = new List<ulong>(); cb.JoinSinkForTest = id => joins.Add(id);
            var rooms = new List<string>(); cb.RoomSinkForTest = (o, l) => rooms.Add(o + "/" + l);
            Chat.Line chatLine = null;
            object fakeCutscene = new object(); bool fakePushed = false;
            string stackWas = Singleton<InputManager>.Exists ? Singleton<InputManager>.Instance.m_Active : "";
            NativeUI.Reset(); CoopUI.ClearOverlayToastsForTest(); MenuNotice.ResetForTest();
            try
            {
                // ---- MESSAGES-KEPT ----
                {
                    var probs = new List<string>();
                    gm.ActivateMenu(pause); yield return StartCoroutine(Frames(2));
                    var pauseCv = pause.GetComponentInParent<Canvas>();
                    var pauseCam = pauseCv == null || pauseCv.renderMode == RenderMode.ScreenSpaceOverlay ? null : pauseCv.worldCamera;
                    var rowsBefore = new Dictionary<RectTransform, Vector2[]>();      // where the pause menu's own rows sit before the notice line shows
                    if (MenuNotice.PauseLineForTest != null)
                    {
                        var rows = MenuNotice.PauseLineForTest.transform.parent;
                        for (int i = 0; i < rows.childCount; i++)
                        {
                            var c = rows.GetChild(i) as RectTransform;
                            if (c == null || c == MenuNotice.PauseLineForTest.transform || !c.gameObject.activeInHierarchy) continue;
                            rowsBefore[c] = ScreenCorners(c, pauseCam);
                        }
                    }
                    // the menu may still be settling just after it opened: take "before" only once the rows stood still for 3 frames (no notice yet),
                    // so a move afterwards can only come from the notice line
                    var settled = new Settled();
                    yield return StartCoroutine(Settle(rowsBefore, pauseCam, settled));
                    if (settled.Max > 0.05f) CoopBehaviour.Log("audit: the pause menu's rows were still moving after it opened (up to " + settled.Max.ToString("0.0") + " px a frame); " + (settled.Still ? "still after " : "not still after ") + settled.Took.ToString("0.00") + " s");
                    if (!settled.Still) probs.Add("the pause menu's rows never stood still before the notice (up to " + settled.Max.ToString("0.0") + " px a frame)");
                    string layoutBefore = PauseLayoutText(pause);
                    CoopUI.Toast("AUDIT NOTICE ONE");
                    yield return StartCoroutine(WaitNotice(pause, "M.O.M>>> AUDIT NOTICE ONE", 2f));
                    yield return StartCoroutine(Frames(2));      // the layout pass after the line went up
                    bool rowsMoved = false;
                    foreach (var kv in rowsBefore)
                    {
                        if (kv.Key == null || !kv.Key.gameObject.activeInHierarchy) continue;
                        var rowNow = ScreenCorners(kv.Key, pauseCam);
                        float shift = 0f;
                        for (int k = 0; k < 4; k++) shift = Mathf.Max(shift, (rowNow[k] - kv.Value[k]).magnitude);
                        if (shift > 0.5f) { rowsMoved = true; probs.Add("the pause notice moves the menu's rows (" + kv.Key.name + " by " + shift.ToString("0.0") + " px)"); }
                    }
                    if (rowsMoved) CoopBehaviour.Log("audit: pause layout before the notice: " + layoutBefore + " | after: " + PauseLayoutText(pause));
                    if (MenuNotice.TextNow(pause) != "M.O.M>>> AUDIT NOTICE ONE") probs.Add("the pause menu says \"" + MenuNotice.TextNow(pause) + "\"");
                    var line = MenuNotice.PauseLineForTest;
                    var group = pause.transform.Find("AspectKeeper/MenuGroupPanel") as RectTransform;
                    if (line != null && line.activeInHierarchy && group != null)
                    {
                        Canvas.ForceUpdateCanvases();
                        for (int i = 0; i < group.childCount; i++)
                        {
                            var c = group.GetChild(i) as RectTransform;
                            if (c != null && c.gameObject.activeInHierarchy && !Inside(c, group, 2f)) probs.Add(c.name + " sticks out of the pause panel");
                        }
                        string over = Overlaps((RectTransform)line.transform, group, line.transform);
                        if (over.Length > 0) probs.Add("the pause notice lies on " + over);
                    }
                    if (!SelectionIn(pause)) probs.Add("the pause menu lost its selection");
                    NativeUI.Reset(); CoopUI.ClearOverlayToastsForTest(); MenuNotice.ResetForTest();
                    // Settings over the pause menu: no notice line there, the message and a friend's chat line wait without ageing. Should the
                    // window lose focus meanwhile, the game pauses itself and stacks its pause menu on Settings: then the pause menu's notice line
                    // says the message (shown, not lost); the chat line still waits.
                    gm.ActivateMenu(options); yield return StartCoroutine(Frames(2));
                    if (!options.gameObject.activeInHierarchy) probs.Add("Settings did not open over the pause menu");
                    holdPause = true;
                    CoopUI.Toast("AUDIT NOTICE TWO");
                    Chat.Add("ANNA", "AUDIT COME TO THE TOP", Time.realtimeSinceStartup, false);
                    chatLine = Chat.Lines[Chat.Lines.Count - 1];
                    const string two = "M.O.M>>> AUDIT NOTICE TWO";
                    bool selfPaused = false, shownOnPause = false, lostMenu = false;
                    float end = Time.realtimeSinceStartup + CoopUI.ToastWait + 1.5f;
                    while (Time.realtimeSinceStartup < end)
                    {
                        if (Singleton<PauseManager>.Exists && PauseManager.Paused) selfPaused = true;
                        if (MenuNotice.LastShown == two) shownOnPause = true;
                        if (!gm.AnyMenuActive) lostMenu = true;
                        yield return null;
                    }
                    bool waiting = CoopUI.OverlayToastsForTest().Contains("AUDIT NOTICE TWO") && CoopUI.WaitingCountForTest() >= 1;
                    if (lostMenu) probs.Add("every menu closed during the wait");
                    if (!waiting && !shownOnPause) probs.Add("the message was dropped after " + (CoopUI.ToastWait + 1.5f) + " s under game menus (" + CoopUI.WaitingCountForTest() + " waiting, self-paused " + selfPaused + ")");
                    if (selfPaused) Note("MESSAGES-KEPT: the window lost focus during the wait and the game paused itself; the message " + (shownOnPause ? "was said on the pause menu's notice line" : "kept waiting"));
                    if (Singleton<PauseManager>.Exists && PauseManager.Paused) yield return StartCoroutine(GameResume());
                    if (gm.AnyMenuActive) gm.CloseAllMenus();
                    holdPause = false;
                    yield return StartCoroutine(Frames(3));
                    for (float until = Time.realtimeSinceStartup + 2f; waiting && Time.realtimeSinceStartup < until && !CoopHud.MirrorLayer.AllText().Contains("AUDIT NOTICE TWO"); ) yield return null;      // typed in, as the game types its prompts
                    string mirror = CoopHud.MirrorLayer.AllText(), chat = CoopHud.ChatLayer.AllText();
                    if (waiting && !mirror.Contains("AUDIT NOTICE TWO")) probs.Add("after the menus the HUD says \"" + mirror + "\"");
                    float age = Time.realtimeSinceStartup - chatLine.At;
                    if (age > 3f) probs.Add("the chat line aged " + age.ToString("F1") + " s under the menus");
                    if (CoopBehaviour.ShowChat && !chat.Contains("AUDIT COME TO THE TOP")) probs.Add("after the menus the chat log says \"" + chat + "\"");
                    Chat.Lines.Remove(chatLine); chatLine = null;
                    NativeUI.Reset(); CoopUI.ClearOverlayToastsForTest(); MenuNotice.ResetForTest();
                    Check("MESSAGES-KEPT", "the pause menu says M.O.M messages on its own notice line (its rows still inside the panel, the selection kept); under another game menu a message and a friend's chat line do not age and show on the HUD when it closes, " + (CoopUI.ToastWait + 1.5f) + " s later",
                        probs.Count == 0, probs.Count == 0 ? "pause notice, held message and chat line ok" : string.Join("; ", probs.ToArray()));
                }

                // ---- PAGE-ESC ----
                {
                    var probs = new List<string>();
                    yield return StartCoroutine(Frames(2));
                    bool inLobby = cb.CoopMenuState().InLobby;
                    KeyCode key = inLobby ? KeyCode.F3 : KeyCode.F2;
                    cb.HotkeyForTest(key); yield return StartCoroutine(Frames(3));
                    if (!CoopMenu.Showing || cb.MenuPageNow.Length == 0) probs.Add("the page did not open");
                    if (key == KeyCode.F3) { h.StopTyping(); yield return null; }          // JOIN A ROOM: Esc reaches the page once the typing is stopped (Down)
                    int closes = CoopMenuHandler.EscCloses;
                    if (!h.EscClosesHere) probs.Add("Esc would not close the page in the world");
                    h.EscForTest(); yield return null;
                    bool muted = h.EscHeldForTest && InputManager.TestString("cooptype") && !InputManager.TestString("ui");
                    if (!muted) probs.Add("the pause key is not muted while Esc is down (stack " + Singleton<InputManager>.Instance.m_Active + ")");
                    for (int i = 0; i < 6 && CoopMenu.Showing; i++) yield return null;
                    yield return StartCoroutine(Frames(2));
                    bool paused = Singleton<PauseManager>.Exists && PauseManager.Paused;
                    if (CoopMenu.Showing || gm.AnyMenuActive || pause.gameObject.activeInHierarchy || paused || CoopMenuHandler.EscCloses != closes + 1 || h.EscHeldForTest || InputManager.TestString("cooptype") || rooms.Count != 0 || joins.Count != 0)
                        probs.Add("after Esc: coop=" + CoopMenu.Showing + " menu=" + gm.AnyMenuActive + " pause=" + pause.gameObject.activeInHierarchy + " paused=" + paused + " closes " + (CoopMenuHandler.EscCloses - closes) + " held=" + h.EscHeldForTest + " made " + rooms.Count + " joined " + joins.Count);
                    // the SPEEDRUN screen, then F3, then BACK: back on the SPEEDRUN screen
                    CoopMenu.OpenSpeedrunFromPause(); yield return StartCoroutine(Frames(2));
                    bool onSr = CoopMenu.Showing && CoopMenu.ScreenNow == CoopMenu.ScreenSpeedrun;
                    cb.HotkeyForTest(KeyCode.F3); yield return StartCoroutine(Frames(3));
                    bool onJoin = cb.JoinOpenForTest;
                    h.BackForTest(); yield return StartCoroutine(Frames(2));
                    if (!onSr || !onJoin || !CoopMenu.Showing || CoopMenu.ScreenNow != CoopMenu.ScreenSpeedrun || cb.MenuPageNow.Length != 0 || h.Title == null || h.Title.text != "SPEEDRUN")
                        probs.Add("SPEEDRUN, F3, BACK: speedrun=" + onSr + " join=" + onJoin + " showing=" + CoopMenu.Showing + " screen=" + CoopMenu.ScreenNow + " page \"" + cb.MenuPageNow + "\" title \"" + (h.Title == null ? "" : h.Title.text) + "\"");
                    gm.CloseAllMenus(); yield return StartCoroutine(Frames(2));
                    CoopMenu.ScreenNow = CoopMenu.ScreenParty;
                    Check("PAGE-ESC", "Esc (the game's pause key) on a co-op page in the world closes it like BACK, as it closed the old window: the pause key muted until the key is up, no pause menu, nothing made or joined; a page opened over the SPEEDRUN screen goes BACK to that screen",
                        probs.Count == 0, probs.Count == 0 ? "Esc and BACK to SPEEDRUN ok" : string.Join("; ", probs.ToArray()));
                }

                // ---- TYPING-GUARDS ----
                {
                    var probs = new List<string>();
                    cb.RoomsForTest = new List<Room> { new Room { Id = 951UL, Name = "Room Z", Players = 1, Max = 8 } };
                    cb.HotkeyForTest(KeyCode.F3); yield return StartCoroutine(Frames(3));
                    var row = h.TextRowForTest; var field = h.CodeField; var es = EventSystem.current;
                    if (!h.TypingForTest || field == null || !field.isFocused || es == null) probs.Add("not typing on opening");
                    else
                    {
                        foreach (char c in "ab") h.FeedForTest(Key(c));
                        if (h.CancelSoundOnForTest) probs.Add("the menu still plays its cancel sound on Backspace while typing");
                        // the mouse: a move clears the selection, a hover selects the line under it
                        int reclaims = CoopMenuHandler.RowReclaims;
                        es.SetSelectedGameObject(null); yield return StartCoroutine(Frames(2));
                        var roomLine = h.ButtonFor("room:951");
                        if (roomLine != null) es.SetSelectedGameObject(roomLine.gameObject);
                        yield return StartCoroutine(Frames(2));
                        if (!h.TypingForTest || !field.isFocused || es.currentSelectedGameObject != row || !InputManager.TestString("cooptype") || CoopMenuHandler.RowReclaims < reclaims + 2)
                            probs.Add("mouse: typing=" + h.TypingForTest + " focused=" + field.isFocused + " selected " + (es.currentSelectedGameObject == null ? "none" : es.currentSelectedGameObject.name) + " reclaimed " + (CoopMenuHandler.RowReclaims - reclaims));
                        h.FeedForTest(Key('c'));
                        if (field.text != "ABC") probs.Add("typed after the mouse: \"" + field.text + "\"");
                        if (joins.Count != 0) probs.Add("the hovered room was joined");
                        // a cutscene starting while I type: its set goes under mine
                        int took = CoopBehaviour.TypingReclaims;
                        InputManager.Push(fakeCutscene, "cutscene"); fakePushed = true;
                        yield return StartCoroutine(Frames(2));
                        if (!InputManager.TestString("cooptype") || CoopBehaviour.TypingReclaims != took + 1) probs.Add("code line: the cutscene's set stays on top (" + Singleton<InputManager>.Instance.m_Active + ")");
                        h.BackForTest(); yield return StartCoroutine(Frames(2));
                        if (!h.CancelSoundOnForTest) probs.Add("the cancel sound is not back after typing");
                        if (!InputManager.TestString("cutscene")) probs.Add("after the typing the cutscene has not got the keys (" + Singleton<InputManager>.Instance.m_Active + ")");
                        InputManager.Pop(fakeCutscene); fakePushed = false;
                        yield return null;
                        // the same for the chat line
                        took = CoopBehaviour.TypingReclaims;
                        cb.OpenChatForTest(""); yield return StartCoroutine(Frames(2));
                        InputManager.Push(fakeCutscene, "cutscene"); fakePushed = true;
                        yield return StartCoroutine(Frames(2));
                        if (!cb.ChatOpen || !InputManager.TestString("cooptype") || CoopBehaviour.TypingReclaims != took + 1) probs.Add("chat: open=" + cb.ChatOpen + " the cutscene's set on top (" + Singleton<InputManager>.Instance.m_Active + ")");
                        cb.HandleChatKeys(false, true); yield return null;
                        if (cb.ChatOpen || !InputManager.TestString("cutscene")) probs.Add("chat closed: the cutscene has not got the keys (" + Singleton<InputManager>.Instance.m_Active + ")");
                        InputManager.Pop(fakeCutscene); fakePushed = false;
                        yield return null;
                    }
                    cb.RoomsForTest = null;
                    if (gm.AnyMenuActive) gm.CloseAllMenus();
                    yield return StartCoroutine(Frames(2));
                    // T over the pause menu: no chat, the menu keeps its line
                    gm.ActivateMenu(pause); yield return StartCoroutine(Frames(2));
                    var um = pause as UnityGUIMenu;
                    if (es != null && um != null && um.m_defaultSelection != null) es.SetSelectedGameObject(um.m_defaultSelection);
                    yield return null;
                    var before = es == null ? null : es.currentSelectedGameObject;
                    cb.HotkeyForTest(KeyCode.T); yield return StartCoroutine(Frames(3));
                    if (cb.ChatOpen || es == null || es.currentSelectedGameObject != before || before == null || !SelectionIn(pause))
                        probs.Add("T over the pause menu: chat=" + cb.ChatOpen + " selected " + (es == null || es.currentSelectedGameObject == null ? "none" : es.currentSelectedGameObject.name) + " was " + (before == null ? "none" : before.name));
                    gm.CloseAllMenus(); yield return StartCoroutine(Frames(2));
                    string stackNow = Singleton<InputManager>.Exists ? Singleton<InputManager>.Instance.m_Active : "";
                    if (stackNow != stackWas) probs.Add("input stack \"" + stackNow + "\" was \"" + stackWas + "\"");
                    Check("TYPING-GUARDS", "while the invite code is typed the mouse never stops it (the code line takes the selection back, no room is joined), Backspace plays no cancel sound; a cutscene that starts while the code or the chat is typed gets no typed key (its set goes under the typing and gets the keys after); T over the pause menu opens nothing and leaves the menu its selection",
                        probs.Count == 0, probs.Count == 0 ? "mouse, cancel sound, cutscene (code and chat), T over pause ok" : string.Join("; ", probs.ToArray()));
                }
            }
            finally
            {
                if (fakePushed) InputManager.Pop(fakeCutscene);
                holdPause = false;
                if (chatLine != null) Chat.Lines.Remove(chatLine);
                cb.JoinSinkForTest = null; cb.RoomSinkForTest = null; cb.RoomsForTest = null;
                CoopMenuHandler.KeyboardModeForTest = null;
                CoopUI.Hidden = hiddenWas; CoopBehaviour.ShowChat = showChatWas; CoopUI.ShownForTest = shownWas;
                CoopMenu.ScreenNow = CoopMenu.ScreenParty;
                NativeUI.Reset(); CoopUI.ClearOverlayToastsForTest(); MenuNotice.ResetForTest();
                if (gm != null && gm.AnyMenuActive) gm.CloseAllMenus();
            }
        }

        // ---- LINK-FIT (pure) ----
        void CheckLinkFit()
        {
            var probs = new List<string>();
            Func<string, float> w = s => 7f * (s ?? "").Length;
            Func<string, float, string> cut = (s, room) => { int n = (int)(room / 7f) - 3; return n <= 0 ? "" : s.Substring(0, Math.Min(n, s.Length)).TrimEnd() + "..."; };
            var peers = new List<KeyValuePair<string, bool>>();
            for (int i = 0; i < 7; i++) peers.Add(new KeyValuePair<string, bool>("PLAYERNAME" + (char)('A' + i) + "XYZ", i % 2 == 0));
            var st = new CoopHud.LinkState { Online = true, InWorld = true, InLobby = true, Code = "ABCD-EFGH-IJKL", Notice = "the leader's world is ready (432123 bytes) a friend's plant updated", Peers = peers };
            var segs = CoopHud.LinkSegments(st);
            foreach (float room in new[] { 424f, 678f })
            {
                var f = CoopHud.LinkFit(segs, w, cut, room);
                float total = 0f; foreach (var g in f) total += w(g.Text);
                bool title = f.Count > 0 && f[0].Kind == CoopHud.LinkKindTitle;
                bool code = f.Exists(g => g.Kind == CoopHud.LinkKindCode && g.Text.Contains("ABCD-EFGH-IJKL"));
                bool notice = f.Exists(g => g.Kind == CoopHud.LinkKindNotice && g.Text.Contains("LEADER"));
                bool more = f.Exists(g => g.Text.StartsWith("   +"));
                int shownPeers = f.FindAll(g => g.Text.StartsWith("   >>> ")).Count;
                string plus = f.Find(g => g.Text.StartsWith("   +")).Text;
                bool counted = more && plus != null && plus.Trim() == "+" + (7 - shownPeers);
                if (total > room + 0.5f || !title || !code || !notice || !counted) probs.Add(room + " px: width " + total + " title=" + title + " code=" + code + " notice=" + notice + " +N=" + counted + " [" + string.Join("|", f.ConvertAll(g => g.Text).ToArray()) + "]");
            }
            var small = CoopHud.LinkSegments(new CoopHud.LinkState { Online = true, InWorld = true, InLobby = true, Code = "X", Peers = new List<KeyValuePair<string, bool>> { new KeyValuePair<string, bool>("ANNA", true) } });
            var sf = CoopHud.LinkFit(small, w, cut, 678f);
            if (sf.Count != small.Count) probs.Add("a line that fits was changed");
            Check("LINK-FIT", "the B.U.D. LINK line too wide for its place keeps its title, the room code and the notice (cut with ...), and the players that do not fit become +N; a line that fits is unchanged",
                probs.Count == 0, probs.Count == 0 ? "424 and 678 px with 7 long names and a long notice ok" : string.Join("; ", probs.ToArray()));
        }
    }
}
