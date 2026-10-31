using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CoopMod
{
    // The HUD pieces that replaced the overlay drawn over the game (quick-audit group "ui", in the world): no IMGUI anywhere in the mod, the mod's
    // own uGUI canvas, the B.U.D. LINK line, M.O.M messages the game's panel cannot take, the SAY> line and the chat log, the name tags and F1.
    // Everything is checked structurally inside the game (the uGUI objects, their fonts, where their corners are on screen): no screen capture.
    public partial class SelfTest
    {
        public const string NativeHudIds = "NO-IMGUI,HUD-CANVAS,LINK-LINE,MOM-MIRROR,CHAT-HUD,NAME-TAGS,F1-HIDE";

        // ---- NO-IMGUI: no OnGUI in the mod, and no method of the mod calls into IMGUI (GUI, GUILayout, GUIStyle, GUIUtility, GUISkin) ----
        static readonly Dictionary<short, OpCode> opcodes = new Dictionary<short, OpCode>();
        static readonly string[] imguiTypes = { "UnityEngine.GUI", "UnityEngine.GUILayout", "UnityEngine.GUIStyle", "UnityEngine.GUIUtility", "UnityEngine.GUISkin", "UnityEngine.GUILayoutUtility" };

        static int OperandSize(OpCode op, byte[] il, int at)
        {
            switch (op.OperandType)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineI8: case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch: return 4 + 4 * BitConverter.ToInt32(il, at);
                default: return 4;
            }
        }

        // every method the mod's code calls (resolved from the IL of each method body); scanned: how many bodies; unresolved: tokens that would not resolve
        static List<string> ImguiCalls(Assembly asm, out int bodies, out int calls, out int unresolved, out bool sawPopEvent)
        {
            if (opcodes.Count == 0)
                foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)) { var o = (OpCode)f.GetValue(null); opcodes[o.Value] = o; }
            var bad = new List<string>();
            bodies = calls = unresolved = 0; sawPopEvent = false;
            var flags = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var t in asm.GetTypes())
            {
                var methods = new List<MethodBase>();
                methods.AddRange(t.GetMethods(flags)); methods.AddRange(t.GetConstructors(flags));
                foreach (var m in methods)
                {
                    MethodBody body = null;
                    try { body = m.GetMethodBody(); } catch (Exception) { }
                    if (body == null) continue;
                    byte[] il = body.GetILAsByteArray();
                    if (il == null) continue;
                    bodies++;
                    Type[] ta = t.IsGenericType ? t.GetGenericArguments() : null;
                    Type[] ma = m.IsGenericMethod ? m.GetGenericArguments() : null;
                    int i = 0;
                    while (i < il.Length)
                    {
                        short v = il[i++];
                        if (v == 0xFE && i < il.Length) v = (short)(0xFE00 | il[i++]);
                        OpCode op;
                        if (!opcodes.TryGetValue(v, out op)) break;          // not an opcode we know: stop this body (counted below)
                        int size = OperandSize(op, il, i);
                        if (op.OperandType == OperandType.InlineMethod && i + 4 <= il.Length)
                        {
                            int token = BitConverter.ToInt32(il, i);
                            try
                            {
                                var callee = m.Module.ResolveMethod(token, ta, ma);
                                calls++;
                                string dt = callee.DeclaringType == null ? "" : callee.DeclaringType.FullName;
                                if (Array.IndexOf(imguiTypes, dt) >= 0) bad.Add(t.Name + "." + m.Name + " calls " + dt + "." + callee.Name);
                                if (dt == "UnityEngine.Event" && callee.Name == "PopEvent") sawPopEvent = true;
                            }
                            catch (Exception) { unresolved++; }
                        }
                        i += size;
                    }
                }
            }
            return bad;
        }

        void CheckNoImgui()
        {
            var asm = typeof(CoopBehaviour).Assembly;
            var withOnGui = new List<string>();
            foreach (var t in asm.GetTypes())
                if (typeof(MonoBehaviour).IsAssignableFrom(t) && t.GetMethod("OnGUI", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null) withOnGui.Add(t.Name);
            int bodies, calls, unresolved; bool control;
            var bad = ImguiCalls(asm, out bodies, out calls, out unresolved, out control);
            Check("NO-IMGUI", "nothing in the mod draws over the game with IMGUI: no script of the mod has an OnGUI, and no method of the mod calls GUI, GUILayout, GUIStyle, GUIUtility or GUISkin (every call in its code read from the compiled IL; the scan finds CoopField's Event.PopEvent as a control)",
                withOnGui.Count == 0 && bad.Count == 0 && control && calls > 2000 && unresolved * 50 < calls,
                "OnGUI in [" + string.Join(",", withOnGui.ToArray()) + "], IMGUI calls [" + string.Join("; ", bad.ToArray()) + "], " + bodies + " method bodies, " + calls + " calls read, " + unresolved + " unresolved, control " + control);
        }

        static CoopHud.LinkState st(bool world, bool lobby, string code, string notice, string status, KeyValuePair<string, bool>[] peers)
        {
            return new CoopHud.LinkState { Online = true, InWorld = world, InLobby = lobby, Code = code, Notice = notice, LobbyStatus = status, Peers = new List<KeyValuePair<string, bool>>(peers) };
        }

        static string TextsOf(HudLayer l) { return l.AllText(); }

        static bool Inside(RectTransform rt, Camera cam, float margin, out Rect r)
        {
            r = CoopHud.ScreenRect(rt, new Vector3[4], cam);
            return r.xMin >= -margin && r.yMin >= -margin && r.xMax <= Screen.width + margin && r.yMax <= Screen.height + margin;
        }

        // every shown Text of a layer: the game's font and inside the screen; "" when all is well
        static string LayerProblems(HudLayer l, Camera cam)
        {
            var texts = l.ShownTexts(); var fixedW = l.ShownFixed();
            for (int i = 0; i < texts.Count; i++)
            {
                var t = texts[i];
                if (t.font == null || t.font != HudLayer.FontFor(fixedW[i])) return "\"" + t.text + "\" in font " + (t.font == null ? "none" : t.font.name);
                Rect r;
                if (cam != null && !Inside(t.rectTransform, cam, 2f, out r)) return "\"" + t.text + "\" off screen " + r;
            }
            return "";
        }

        IEnumerator TestNativeHud()
        {
            try { CheckNoImgui(); } catch (Exception e) { Check("NO-IMGUI", "the IMGUI scan ran", false, Describe(e)); }
            var cb = GetComponent<CoopBehaviour>();
            var gm = Singleton<GUIManager>.Exists ? Singleton<GUIManager>.Instance : null;
            bool online = cb != null && cb.IsOnline;
            if (cb == null || gm == null || CoopHud.Canvas == null || !online || !WorldSync.InWorld())
            {
                foreach (var id in NativeHudIds.Split(',')) if (id != "NO-IMGUI") Check(id, "the co-op HUD can be tested", false, "mod=" + (cb != null) + " gui=" + (gm != null) + " canvas=" + (CoopHud.Canvas != null) + " steam=" + online + " world=" + WorldSync.InWorld());
                yield break;
            }
            if (gm.AnyMenuActive) gm.CloseAllMenus();
            bool hiddenWas = CoopUI.Hidden, shownWas = CoopUI.ShownForTest;
            CoopUI.ShownForTest = true; CoopUI.Hidden = false;      // the player may have hidden the co-op display (F1): the pieces are tested with it on
            var cam = CoopHud.Canvas.worldCamera;
            Font gameFont = Singleton<AbilityHUD>.Exists && Singleton<AbilityHUD>.Instance.m_textBottomLine != null ? Singleton<AbilityHUD>.Instance.m_textBottomLine.font : null;
            try
            {
                // ---- HUD-CANVAS ----
                {
                    var probs = new List<string>();
                    var c = CoopHud.Canvas;
                    if (c.transform.parent != gm.transform) probs.Add("not under the game's GUI");
                    if (c.renderMode != RenderMode.ScreenSpaceCamera || c.worldCamera != gm.ActiveCamera) probs.Add(c.renderMode + " camera " + (c.worldCamera == null ? "none" : c.worldCamera.name));
                    if (c.GetComponentsInChildren<GraphicRaycaster>(true).Length > 0) probs.Add("it takes clicks");
                    var game = Singleton<AbilityHUD>.Instance.m_canvas != null ? Singleton<AbilityHUD>.Instance.m_canvas.GetComponent<Canvas>() : null;
                    if (game != null && c.sortingOrder <= game.sortingOrder) probs.Add("not above the game's HUD");
                    if (game != null && c.transform.IsChildOf(game.transform)) probs.Add("inside the game's own canvas");
                    if (gameFont == null || CoopHud.GameFont != gameFont) probs.Add("font " + (CoopHud.GameFont == null ? "none" : CoopHud.GameFont.name) + " is not the game's HUD font");
                    foreach (var l in CoopHud.Layers) if (l.Root == null || l.Root.parent != c.transform) probs.Add(l.Name + " not on the canvas");
                    if (CoopHud.SayRoot == null || CoopHud.SayRoot.parent != c.transform) probs.Add("SAY> line not on the canvas");
                    int odd = 0; string oddName = "";
                    foreach (var t in c.GetComponentsInChildren<Text>(true)) if (t.font != CoopHud.GameFont && (CoopHud.FixedFont == null || t.font != CoopHud.FixedFont)) { odd++; oddName = t.name + " " + (t.font == null ? "none" : t.font.name); }
                    if (odd > 0) probs.Add(odd + " texts in another font, e.g. " + oddName);
                    if (CoopHud.DrawProblem.Length > 0) probs.Add("a piece threw: " + CoopHud.DrawProblem);
                    Check("HUD-CANVAS", "the co-op HUD is one uGUI canvas of the mod's own under the game's GUI, on the game's GUI camera, above the game's HUD, never taking clicks; every piece (link line, M.O.M messages, chat, SAY>, name tags, speedrun) is on it, every text in the game's HUD font (" + (gameFont == null ? "?" : gameFont.name) + ") or its fixed-width font for times",
                        probs.Count == 0, probs.Count == 0 ? "ok, fixed font " + (CoopHud.FixedFont == null ? "not found" : CoopHud.FixedFont.name) + ", " + c.GetComponentsInChildren<Text>(true).Length + " texts" : string.Join("; ", probs.ToArray()));
                }

                // ---- LINK-LINE: the pure line, the live line, the keys on the CO-OP page ----
                {
                    var probs = new List<string>();
                    var none = new KeyValuePair<string, bool>[0];
                    var two = new[] { new KeyValuePair<string, bool>("Anna", true), new KeyValuePair<string, bool>("Bob", false) };
                    string a;
                    if ((a = CoopHud.LinkLine(st(true, false, "", "", "", none))) != "") probs.Add("solo \"" + a + "\"");
                    if ((a = CoopHud.LinkLine(st(true, true, "ABC123", "", "in a room", none))) != "B.U.D. LINK   CODE ABC123") probs.Add("host \"" + a + "\"");
                    if ((a = CoopHud.LinkLine(st(true, true, "X", "", "", two))) != "B.U.D. LINK   >>> ANNA   >>> BOB   CODE X") probs.Add("peers \"" + a + "\"");
                    var segs = CoopHud.LinkSegments(st(true, true, "X", "", "", two));
                    if (segs.Count != 4 || segs[1].Kind != CoopHud.LinkKindOk || segs[2].Kind != CoopHud.LinkKindWaiting) probs.Add("connected / connecting colours");
                    if ((a = CoopHud.LinkLine(st(true, false, "", "sending world 40%", "waiting for the host", none))) != "B.U.D. LINK   WAITING FOR THE HOST  SENDING WORLD 40%") probs.Add("notice without code \"" + a + "\"");
                    if ((a = CoopHud.LinkLine(st(true, true, "X", "sending world 40%", "waiting", none))) != "B.U.D. LINK   CODE X   SENDING WORLD 40%") probs.Add("notice with code \"" + a + "\"");
                    if ((a = CoopHud.LinkLine(st(false, true, "X", "", "", two))) != "") probs.Add("start screen \"" + a + "\"");
                    // the CO-OP page carries the keys the old panel listed
                    var ms = new CoopMenu.State { Online = true, InWorld = true, Keys = CoopMenu.KeysSolo, Page = "" };
                    string l1, l2;
                    var rows = CoopMenu.RowsFor(ms, out l1, out l2);
                    var kparts = CoopMenu.KeysSolo.Split('|');
                    if (rows.Count < 2 || rows[0].Id != "keys" || !rows[0].Inert || rows[0].Label != kparts[0] || rows[1].Id != "keys" || rows[1].Label != kparts[1]) probs.Add("menu keys row " + (rows.Count > 0 ? rows[0].Id : "none"));
                    ms.AtStart = true;
                    foreach (var r in CoopMenu.RowsFor(ms, out l1, out l2)) if (r.Id == "keys") probs.Add("keys row on the start screen");
                    // live
                    CoopHud.LinkOverrideForTest = st(true, true, "AUDITCODE", "", "", two);
                    yield return StartCoroutine(Frames(3));
                    string live = TextsOf(CoopHud.LinkLayer).Replace("|", "");
                    if (live != "B.U.D. LINK   >>> ANNA   >>> BOB   CODE AUDITCODE") probs.Add("live line \"" + live + "\"");
                    string lp = LayerProblems(CoopHud.LinkLayer, cam); if (lp.Length > 0) probs.Add(lp);
                    foreach (var t in CoopHud.LinkLayer.ShownTexts()) if (t.text.Contains("BOB") && t.color != CoopUI.Pink) probs.Add("a connecting player is not in the selected colour");
                    CoopHud.LinkOverrideForTest = st(true, false, "", "", "", none);
                    yield return StartCoroutine(Frames(2));
                    if (CoopHud.LinkLayer.ShownTexts().Count > 0) probs.Add("shown with nothing to report");
                    CoopHud.LinkOverrideForTest = st(true, true, "AUDITCODE", "", "", two);
                    cb.HotkeyForTest(KeyCode.F3); yield return StartCoroutine(Frames(3));
                    if (!CoopMenu.Showing) probs.Add("(the co-op menu did not open)");
                    else if (CoopHud.LinkLayer.ShownTexts().Count > 0) probs.Add("shown under a menu");
                    if (gm.AnyMenuActive) gm.CloseAllMenus();
                    yield return StartCoroutine(Frames(2));
                    Check("LINK-LINE", "the B.U.D. LINK line (who is linked, connecting players in the selected colour, the room code, world notices) is one plain line of the game's HUD font, shown only when there is something to report, never under a menu or at the start screen; the co-op keys moved to the CO-OP page",
                        probs.Count == 0, probs.Count == 0 ? "ok" : string.Join("; ", probs.ToArray()));
                }

                // ---- MOM-MIRROR: a message the game's M.O.M panel cannot take ----
                {
                    var probs = new List<string>();
                    NativeUI.Reset(); CoopUI.ClearOverlayToastsForTest();
                    CoopUI.OverlayToast("audit mirror one");
                    float t0 = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - t0 < 1.5f) yield return null;      // typed in at 48 letters a second
                    string seen = TextsOf(CoopHud.MirrorLayer);
                    if (!seen.Contains("M.O.M>>> AUDIT MIRROR ONE")) probs.Add("panel says \"" + seen + "\"");
                    string mp = LayerProblems(CoopHud.MirrorLayer, cam); if (mp.Length > 0) probs.Add(mp);
                    var art = CoopHud.MirrorLayer.ShownImages();
                    if (art.Count == 0) probs.Add("no panel art");
                    else if (CoopHud.PanelArt != null && art[0].sprite != CoopHud.PanelArt.sprite) probs.Add("not the game's M.O.M panel art");
                    cb.HotkeyForTest(KeyCode.F3); yield return StartCoroutine(Frames(3));
                    var h = CoopMenu.Handler;
                    if (CoopHud.MirrorLayer.ShownTexts().Count > 0) probs.Add("shown over the co-op menu");
                    CoopUI.Toast("audit notice two");
                    yield return StartCoroutine(Frames(2));
                    if (h == null || h.Line2 == null || !h.Line2.text.StartsWith("M.O.M>>> AUDIT NOTICE TWO") || !CoopMenuHandler.NoticeUp) probs.Add("the co-op menu's subtitle says \"" + (h != null && h.Line2 != null ? h.Line2.text : "") + "\"");
                    if (!CoopUI.OverlayToastsForTest().Contains("AUDIT NOTICE TWO")) probs.Add("the test list lost it");
                    if (gm.AnyMenuActive) gm.CloseAllMenus();
                    CoopMenuHandler.ClearNotice();
                    yield return StartCoroutine(Frames(2));
                    CoopUI.Hidden = true; yield return StartCoroutine(Frames(2));
                    if (CoopHud.MirrorLayer.ShownTexts().Count > 0) probs.Add("F1 does not hide it");
                    CoopUI.Hidden = false;
                    while (Time.realtimeSinceStartup - t0 < CoopUI.ToastLife + 0.5f) yield return null;
                    if (CoopUI.OverlayToastsForTest().Contains("AUDIT MIRROR ONE")) probs.Add("still there after 6.5 s");
                    CoopUI.ClearOverlayToastsForTest();
                    Check("MOM-MIRROR", "a co-op message the game's M.O.M panel cannot take is typed into a copy of the game's M.O.M panel art on the mod's HUD in the game's font (no overlay), is said in the co-op menu's subtitle while that menu is up, F1 hides it, and it is gone 6.5 s after it showed",
                        probs.Count == 0, probs.Count == 0 ? "ok" : string.Join("; ", probs.ToArray()));
                }

                // ---- CHAT-HUD: the SAY> line types the game's way and the chat log is native ----
                {
                    var probs = new List<string>();
                    if (cb.ChatOpen) cb.HandleChatKeys(false, true);
                    sink.Messages.Clear();
                    cb.HotkeyForTest(KeyCode.T); yield return StartCoroutine(Frames(3));
                    var f = CoopHud.SayField;
                    var es = EventSystem.current;
                    if (!cb.ChatOpen || !CoopHud.SayShown || f == null || !f.isFocused || !CoopField.AnyFocused) probs.Add("T: open=" + cb.ChatOpen + " shown=" + CoopHud.SayShown + " typing=" + (f != null && f.isFocused));
                    if (es == null || CoopHud.SayRoot == null || es.currentSelectedGameObject != CoopHud.SayRoot.gameObject) probs.Add("not the selection");
                    if (!InputManager.TestString("cooptype") || InputManager.TestString("ui")) probs.Add("the game's keys are not muted while typing");
                    if (f != null && f.text != "") probs.Add("the T was typed: \"" + f.text + "\"");
                    if (f != null)
                    {
                        if (f.textComponent == null || f.textComponent.font != CoopHud.GameFont) probs.Add("field not in the game font");
                        Rect r; if (!Inside((RectTransform)f.transform, cam, 2f, out r)) probs.Add("field off screen " + r);
                        if (CoopHud.SayPrompt.text != "SAY>" || CoopHud.SayKeys.text != CoopHud.SayKeysText) probs.Add("labels");
                    }
                    foreach (char c in "hi there") cb.ChatFeedForTest(Key(c));
                    if (cb.ChatTextNow != "hi there") probs.Add("typed \"" + cb.ChatTextNow + "\"");
                    cb.HotkeyForTest(KeyCode.Y); cb.HotkeyForTest(KeyCode.T);
                    if (!cb.ChatOpen) probs.Add("closed by a hotkey while typing");
                    cb.ChatFeedForTest(new Event { type = EventType.KeyDown, keyCode = KeyCode.Return, character = '\n' });
                    yield return null;
                    var mine = Chat.Lines.Count > 0 ? Chat.Lines[Chat.Lines.Count - 1] : null;
                    if (cb.ChatOpen || CountType(Chat.Msg) != 1 || mine == null || !mine.Mine || mine.Text != "hi there") probs.Add("Enter: open=" + cb.ChatOpen + " sent " + CountType(Chat.Msg) + " last \"" + (mine == null ? "" : mine.Text) + "\"");
                    if (InputManager.TestString("cooptype") || CoopField.AnyFocused) probs.Add("still typing after Enter");
                    if (es != null && CoopHud.SayRoot != null && es.currentSelectedGameObject == CoopHud.SayRoot.gameObject) probs.Add("still selected after Enter");
                    yield return StartCoroutine(Frames(2));
                    if (CoopHud.SayShown) probs.Add("the SAY> line is still up");
                    // Esc closes (once the key is up) and sends nothing
                    sink.Messages.Clear();
                    cb.HotkeyForTest(KeyCode.T); yield return StartCoroutine(Frames(3));
                    cb.ChatFeedForTest(Key('x'));
                    cb.ChatFeedForTest(new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape });
                    yield return StartCoroutine(Frames(4));
                    if (cb.ChatOpen || CountType(Chat.Msg) != 0 || InputManager.TestString("cooptype")) probs.Add("Esc: open=" + cb.ChatOpen + " sent " + CountType(Chat.Msg));
                    if (PauseManager.Paused) probs.Add("Esc while typing paused the game");
                    // the chat log
                    cb.HandleChat(9091UL, "Ana", Chat.Encode("audit hello"));
                    yield return StartCoroutine(Frames(2));
                    bool sawName = false, sawText = false, sawMine = false;
                    foreach (var t in CoopHud.ChatLayer.ShownTexts())
                    {
                        if (t.text == "ANA: " && Mathf.Abs(t.color.r - CoopUI.Pink.r) < 0.01f) sawName = true;
                        if (t.text == "audit hello") sawText = true;
                        if (t.text.EndsWith(": ") && t.text != "ANA: " && Mathf.Abs(t.color.r - CoopUI.Cyan.r) < 0.01f) sawMine = true;
                    }
                    if (!CoopBehaviour.ShowChat) probs.Add("(chat display is off in the settings)");
                    else if (!sawName || !sawText || !sawMine) probs.Add("log shows " + TextsOf(CoopHud.ChatLayer));
                    string cp = LayerProblems(CoopHud.ChatLayer, cam); if (cp.Length > 0) probs.Add(cp);
                    Check("CHAT-HUD", "T opens the SAY> line on the mod's HUD: a uGUI InputField in the game's font, the event system's selection, the game's keys muted (no pause, no BUD), the T not typed; typing, Enter sends once and closes, Esc closes and sends nothing, hotkeys do nothing while typing; the chat log is native text (others' names in the selected colour, mine in cyan)",
                        probs.Count == 0, probs.Count == 0 ? "ok" : string.Join("; ", probs.ToArray()));
                }

                // ---- NAME-TAGS ----
                {
                    var probs = new List<string>();
                    if (CoopHud.TagText("Anna", 12.4f, true) != "ANNA  12 M" || CoopHud.TagText("Anna", 12.4f, false) != "12 M") probs.Add("text \"" + CoopHud.TagText("Anna", 12.4f, true) + "\"");
                    var tr = CoopHud.TagRect(new Vector2(-500f, -500f), 100f, 20f, 1280f, 720f, 1f);
                    if (tr.xMin < 8f || tr.yMin < 8f || tr.xMax > 1272f || tr.yMax > 712f) probs.Add("not kept on screen " + tr);
                    tr = CoopHud.TagRect(new Vector2(640f, 360f), 100f, 20f, 1280f, 720f, 1f);
                    if (Mathf.Abs(tr.x - 590f) > 0.01f || Mathf.Abs(tr.y - 340f) > 0.01f) probs.Add("not over the point " + tr);
                    var main = Camera.main;
                    if (main == null) probs.Add("no game camera");
                    else
                    {
                        Vector3 ahead = main.transform.position + main.transform.forward * 10f;
                        CoopHud.TagsForTest = new List<CoopHud.TagInfo> { new CoopHud.TagInfo { Name = "Audit", Head = ahead, Meters = 10f } };
                        yield return StartCoroutine(Frames(2));
                        var texts = CoopHud.TagLayer.ShownTexts();
                        if (CoopHud.TagsShown != 1 || texts.Count != 1 || texts[0].text != (CoopBehaviour.ShowNames ? "AUDIT  10 M" : "10 M")) probs.Add("live tag " + CoopHud.TagsShown + " \"" + TextsOf(CoopHud.TagLayer) + "\"");
                        else
                        {
                            Vector3 sp = main.WorldToScreenPoint(ahead);
                            var r = CoopHud.ScreenRect(texts[0].rectTransform, new Vector3[4], cam);
                            if (Mathf.Abs(r.center.x - sp.x) > 2f || Mathf.Abs(r.yMin - sp.y) > 2f) probs.Add("tag at " + r + " for point " + sp);
                            string tp = LayerProblems(CoopHud.TagLayer, cam); if (tp.Length > 0) probs.Add(tp);
                        }
                        CoopHud.TagsForTest = new List<CoopHud.TagInfo> { new CoopHud.TagInfo { Name = "Audit", Head = main.transform.position - main.transform.forward * 10f, Meters = 10f } };
                        yield return StartCoroutine(Frames(2));
                        if (CoopHud.TagsShown != 0) probs.Add("shown for a player behind the camera");
                        CoopHud.TagsForTest = new List<CoopHud.TagInfo> { new CoopHud.TagInfo { Name = "Audit", Head = ahead, Meters = 10f } };
                        cb.HotkeyForTest(KeyCode.F3); yield return StartCoroutine(Frames(3));
                        if (CoopHud.TagLayer.ShownTexts().Count > 0) probs.Add("shown under a menu");
                        if (gm.AnyMenuActive) gm.CloseAllMenus();
                        yield return StartCoroutine(Frames(2));
                    }
                    Check("NAME-TAGS", "the tag over another player is native text on the game's M.O.M label art, placed from the game camera on the player's head, kept inside the screen, gone when the player is behind the camera or a menu is up",
                        probs.Count == 0, probs.Count == 0 ? "ok" : string.Join("; ", probs.ToArray()));
                }

                // ---- F1-HIDE: F1 hides every co-op piece and brings it back ----
                {
                    var probs = new List<string>();
                    CoopHud.LinkOverrideForTest = new CoopHud.LinkState { Online = true, InWorld = true, InLobby = true, Code = "F1CODE", Peers = new List<KeyValuePair<string, bool>>() };
                    if (Camera.main != null) CoopHud.TagsForTest = new List<CoopHud.TagInfo> { new CoopHud.TagInfo { Name = "Audit", Head = Camera.main.transform.position + Camera.main.transform.forward * 10f, Meters = 10f } };
                    cb.HandleChat(9092UL, "Bea", Chat.Encode("audit f1"));
                    CoopUI.OverlayToast("audit f1 message");
                    cb.AnswerSinkForTest = delegate { };
                    cb.AddPendingForTest(4250UL, "CARL");
                    yield return StartCoroutine(Frames(3));
                    var pieces = new Dictionary<string, Func<bool>>
                    {
                        { "link", () => CoopHud.LinkLayer.ShownTexts().Count > 0 }, { "chat", () => CoopHud.ChatLayer.ShownTexts().Count > 0 },
                        { "message", () => CoopHud.MirrorLayer.ShownTexts().Count > 0 }, { "tag", () => CoopHud.TagLayer.ShownTexts().Count > 0 },
                        { "request", () => CoopHud.RequestShownForTest },
                    };
                    var on = new List<string>(); foreach (var p in pieces) if (p.Value()) on.Add(p.Key);
                    if (!CoopBehaviour.ShowChat) on.Add("chat");
                    if (on.Count != pieces.Count) probs.Add("with F1 off only [" + string.Join(",", on.ToArray()) + "] showed");
                    CoopUI.Hidden = true; yield return StartCoroutine(Frames(2));
                    var still = new List<string>(); foreach (var p in pieces) if (p.Value()) still.Add(p.Key);
                    if (still.Count > 0) probs.Add("F1 left [" + string.Join(",", still.ToArray()) + "]");
                    if (NativeUI.CanShow()) probs.Add("the M.O.M panel would still take messages");
                    CoopUI.Hidden = false; yield return StartCoroutine(Frames(2));
                    var back = new List<string>(); foreach (var p in pieces) if (p.Value()) back.Add(p.Key);
                    if (!CoopBehaviour.ShowChat) back.Add("chat");
                    if (back.Count != pieces.Count) probs.Add("after F1 again only [" + string.Join(",", back.ToArray()) + "]");
                    cb.ClearPendingForTest(); cb.AnswerSinkForTest = null;
                    Check("F1-HIDE", "F1 hides every co-op piece on the HUD (link line, chat log, M.O.M messages, name tags, the join request) and brings them back",
                        probs.Count == 0, probs.Count == 0 ? "ok" : string.Join("; ", probs.ToArray()));
                }
            }
            finally
            {
                CoopHud.LinkOverrideForTest = null; CoopHud.TagsForTest = null;
                if (cb.ChatOpen) cb.HandleChatKeys(false, true);
                cb.ClearPendingForTest(); cb.AnswerSinkForTest = null;
                CoopUI.ClearOverlayToastsForTest();
                CoopUI.ShownForTest = shownWas; CoopUI.Hidden = hiddenWas;
                if (gm != null && gm.AnyMenuActive) gm.CloseAllMenus();
            }
        }
    }
}
