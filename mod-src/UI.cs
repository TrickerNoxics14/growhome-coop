using System.Collections.Generic;
using UnityEngine;

namespace CoopMod
{
    // The mod's on-screen bits, styled after the game's own HUD and menus: its pixel font ("Fonts/block"), pale cyan text, dark-blue panels with
    // white corner brackets, dashed rules under headings, a pink-red ">>>" on the selected item, and M.O.M's "M.O.M>>>" transmissions.
    public static class CoopUI
    {
        public static readonly Color Cyan = new Color(0.66f, 0.97f, 1f, 1f);
        public static readonly Color CyanDim = new Color(0.66f, 0.97f, 1f, 0.55f);
        public static readonly Color White = new Color(0.95f, 1f, 1f, 1f);
        public static readonly Color Pink = new Color(1f, 0.42f, 0.5f, 1f);      // the game's selected-item colour
        public static readonly Color Panel = new Color(0.02f, 0.08f, 0.18f, 0.80f);

        public static bool Hidden;

        static bool built;
        static Font font;
        static Texture2D px;
        static GUIStyle field;

        static float S { get { return Mathf.Max(0.8f, Screen.height / 720f); } }

        static void Build()
        {
            if (built) return;
            built = true;
            px = new Texture2D(1, 1);
            px.SetPixel(0, 0, Color.white); px.Apply(); px.hideFlags = HideFlags.HideAndDontSave;
            try { font = (Font)Resources.Load("Fonts/block", typeof(Font)); } catch (System.Exception) { }
            if (font == null) CoopBehaviour.Log("ui: game font not found, using default");
            field = Style(22, TextAnchor.MiddleCenter);
            field.normal.textColor = White; field.focused.textColor = White;
            GUI.skin.settings.cursorColor = Pink;
            GUI.skin.settings.selectionColor = new Color(0.3f, 0.7f, 0.9f, 0.5f);
        }

        static GUIStyle Style(int size, TextAnchor anchor)
        {
            var s = new GUIStyle();
            if (font != null) s.font = font;
            s.fontSize = Mathf.RoundToInt(size * S);
            s.alignment = anchor;
            s.normal.textColor = Cyan;
            s.clipping = TextClipping.Overflow;
            return s;
        }

        public static void Fill(Rect r, Color c) { var old = GUI.color; GUI.color = c; GUI.DrawTexture(r, px); GUI.color = old; }

        // ---- building blocks ---------------------------------------------------------------------------------------------------------

        // Dark panel, faint scanlines, thin outline and bright corner brackets, like the game's HUD frames. 'glitch' > 0 jitters it sideways (used when a window opens).
        public static void Frame(Rect r, float glitch)
        {
            Build();
            if (glitch > 0f) r.x += Mathf.Sin(Time.realtimeSinceStartup * 90f) * 4f * glitch * S;
            Fill(r, Panel);
            for (float y = r.y + 2f * S; y < r.yMax - 1f; y += 3f * S) Fill(new Rect(r.x, y, r.width, 1f), new Color(0f, 0f, 0f, 0.10f));
            float t = Mathf.Max(1f, S);
            Fill(new Rect(r.x, r.y, r.width, t), CyanDim); Fill(new Rect(r.x, r.yMax - t, r.width, t), CyanDim);
            Fill(new Rect(r.x, r.y, t, r.height), CyanDim); Fill(new Rect(r.xMax - t, r.y, t, r.height), CyanDim);
            float L = 14f * S, w = 2f * S;
            Fill(new Rect(r.x - w, r.y - w, L, w), White); Fill(new Rect(r.x - w, r.y - w, w, L), White);
            Fill(new Rect(r.xMax - L + w, r.y - w, L, w), White); Fill(new Rect(r.xMax, r.y - w, w, L), White);
            Fill(new Rect(r.x - w, r.yMax, L, w), White); Fill(new Rect(r.x - w, r.yMax - L + w, w, L), White);
            Fill(new Rect(r.xMax - L + w, r.yMax, L, w), White); Fill(new Rect(r.xMax, r.yMax - L + w, w, L), White);
        }

        public static void Frame(Rect r) { Frame(r, 0f); }

        // The shake of a window that has just opened: 0.3 s of real time, then perfectly still (game time may be slowed or stopped, which once made the windows shake for ever).
        public static float GlitchFor(float openedAtRealtime) { return Mathf.Clamp01(1f - (Time.realtimeSinceStartup - openedAtRealtime) / 0.3f); }

        public static void DashedRule(float x, float y, float width, Color c)
        {
            float dash = 6f * S, gap = 4f * S, h = Mathf.Max(1f, S);
            for (float dx = 0f; dx < width; dx += dash + gap) Fill(new Rect(x + dx, y, Mathf.Min(dash, width - dx), h), c);
        }

        public static void Text(Rect r, string s, Color c, int size, TextAnchor anchor)
        {
            Build();
            var st = Style(size, anchor);
            st.normal.textColor = new Color(0f, 0.04f, 0.12f, 0.85f);      // faint shadow keeps it readable on bright sand
            GUI.Label(new Rect(r.x + S, r.y + S, r.width, r.height), s, st);
            st.normal.textColor = c;
            GUI.Label(r, s, st);
        }

        static float Width(string s, int size)
        {
            Build();
            return Style(size, TextAnchor.MiddleLeft).CalcSize(new GUIContent(s)).x;
        }

        // Heading with a dashed rule, like "STATUS REPORT" in the pause menu.
        static void Header(Rect panel, string title, float y)
        {
            Text(new Rect(panel.x + 14f * S, y, panel.width - 28f * S, 22f * S), title, White, 15, TextAnchor.MiddleLeft);
            DashedRule(panel.x + 14f * S, y + 24f * S, panel.width - 28f * S, CyanDim);
        }

        // ---- M.O.M transmissions (toasts) ----------------------------------------------------------------------------------------------

        class ToastItem { public string Msg; public float Born; }
        static readonly List<ToastItem> toasts = new List<ToastItem>();

        public static void Toast(string message)
        {
            toasts.Add(new ToastItem { Msg = message.ToUpper(), Born = Time.realtimeSinceStartup });
            while (toasts.Count > 4) toasts.RemoveAt(0);
        }

        static void DrawToasts()
        {
            toasts.RemoveAll(t => Time.realtimeSinceStartup - t.Born > 6.5f);
            float y = 16f * S;
            foreach (var t in toasts)
            {
                float age = Time.realtimeSinceStartup - t.Born;
                int shown = Mathf.Clamp((int)(age * 48f), 0, t.Msg.Length);                // typewriter, like the game's own prompts
                float alpha = age > 5.5f ? Mathf.Clamp01((6.5f - age)) : 1f;
                string full = "M.O.M>>> " + t.Msg;
                string visible = "M.O.M>>> " + t.Msg.Substring(0, shown);
                float w = Width(full, 14) + 24f * S, h = 26f * S;
                var r = new Rect(16f * S, y, w, h);
                var old = GUI.color; GUI.color = new Color(1f, 1f, 1f, alpha);
                Frame(r);
                Text(new Rect(r.x + 12f * S, r.y, w, h), visible, Cyan, 14, TextAnchor.MiddleLeft);
                GUI.color = old;
                y += h + 8f * S;
            }
        }

        // ---- label above another player's BUD ----------------------------------------------------------------------------------------

        public static void Marker(Vector2 screen, string name, float meters)
        {
            if (Hidden) return;
            Build();
            string s = name + "  " + meters.ToString("F0") + " M";
            float w = Width(s, 13) + 18f * S, h = 22f * S;
            var r = new Rect(screen.x - w / 2f, screen.y - h, w, h);
            Frame(r);
            Text(r, s, White, 13, TextAnchor.MiddleCenter);
        }

        // ---- bottom-left status panel ------------------------------------------------------------------------------------------------

        public class Chip { public string Name; public bool Ok; }

        public static void StatusBar(string who, List<Chip> players, string hint, string codeText, string notice)
        {
            if (Hidden) return;
            Build();
            DrawToasts();
            float pad = 12f * S, rowH = 22f * S;
            string title = "B.U.D. LINK   " + who;
            float w1 = Width(title, 14);
            foreach (var p in players) w1 += Width("   >>> " + p.Name.ToUpper(), 14);
            float w2 = Width(hint, 12) + (codeText.Length > 0 ? Width("   CODE " + codeText, 13) : 0f);
            float w3 = notice.Length > 0 ? Width(notice, 12) : 0f;
            float w = Mathf.Min(Screen.width - 28f * S, Mathf.Max(w1, Mathf.Max(w2, w3)) + pad * 2f);
            int rows = notice.Length > 0 ? 3 : 2;
            float h = pad * 2f + rowH * rows + 4f * S;
            var r = new Rect(16f * S, Screen.height - h - 16f * S, w, h);
            Frame(r);
            float x = r.x + pad, y = r.y + pad;
            Text(new Rect(x, y, 700f * S, rowH), title, White, 14, TextAnchor.MiddleLeft);
            x += Width(title, 14);
            foreach (var p in players)
            {
                string s = "   >>> " + p.Name.ToUpper();
                Text(new Rect(x, y, 500f * S, rowH), s, p.Ok ? Cyan : Pink, 14, TextAnchor.MiddleLeft);
                x += Width(s, 14);
            }
            y += rowH;
            DashedRule(r.x + pad, y + 1f * S, w - pad * 2f, CyanDim);
            y += 4f * S;
            string line2 = hint + (codeText.Length > 0 ? "   CODE " + codeText : "");
            Text(new Rect(r.x + pad, y, w - pad, rowH), line2, CyanDim, 12, TextAnchor.MiddleLeft);
            if (notice.Length > 0) Text(new Rect(r.x + pad, y + rowH, w - pad, rowH), notice, Cyan, 12, TextAnchor.MiddleLeft);
        }

        // ---- popups ------------------------------------------------------------------------------------------------------------------

        public static void CodePopup(string code, float openedAt)
        {
            Build();
            float w = 600f * S, h = 160f * S;
            var r = new Rect((Screen.width - w) / 2f, 70f * S, w, h);
            Frame(r, GlitchFor(openedAt));
            Header(r, "INVITE CODE", r.y + 12f * S);
            int shown = Mathf.Clamp((int)((Time.realtimeSinceStartup - openedAt) * 20f), 0, code.Length);
            Text(new Rect(r.x, r.y + 52f * S, w, 50f * S), code.Substring(0, shown), White, 36, TextAnchor.MiddleCenter);
            Text(new Rect(r.x, r.y + 116f * S, w, 24f * S), "COPIED   FRIENDS PRESS F3 AND TYPE IT IN", CyanDim, 12, TextAnchor.MiddleCenter);
        }

        public const int JoinNone = 0, JoinCode = 1, JoinCancel = 2, JoinRefresh = 3, JoinRoom = 4;

        // The join window: type a friend's code, or click one of the public rooms. Everything is clickable with the mouse; Enter joins the typed code, Esc closes.
        public static string JoinWindow(string textIn, string message, float openedAt, List<Room> rooms, bool listing, out int action, out int room)
        {
            Build();
            action = JoinNone; room = -1;
            float w = 600f * S, h = 440f * S;
            var r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            Frame(r, GlitchFor(openedAt));
            Header(r, "M.O.M>>> JOIN A ROOM", r.y + 12f * S);

            Text(new Rect(r.x + 30f * S, r.y + 50f * S, w - 60f * S, 20f * S), "TYPE AN INVITE CODE", CyanDim, 12, TextAnchor.MiddleLeft);
            var fr = new Rect(r.x + 30f * S, r.y + 72f * S, w - 230f * S, 42f * S);
            Fill(fr, new Color(0f, 0.03f, 0.09f, 0.9f));
            Fill(new Rect(fr.x, fr.yMax - 2f * S, fr.width, 2f * S), Pink);
            GUI.SetNextControlName("codebox");
            field.fontSize = Mathf.RoundToInt(22 * S);
            string t = GUI.TextField(fr, textIn, 20, field).ToUpperInvariant();
            GUI.FocusControl("codebox");
            if (Button(new Rect(fr.xMax + 20f * S, fr.y, 140f * S, fr.height), "JOIN")) action = JoinCode;

            Text(new Rect(r.x + 30f * S, r.y + 128f * S, w - 60f * S, 20f * S), "OR PICK A LISTED ROOM", CyanDim, 12, TextAnchor.MiddleLeft);
            float y = r.y + 152f * S, rowH = 34f * S;
            Fill(new Rect(r.x + 30f * S, y, w - 60f * S, 2f * S), CyanDim);
            if (rooms.Count == 0) Text(new Rect(r.x + 30f * S, y + 4f * S, w - 60f * S, rowH), listing ? "LOOKING..." : "NO ROOMS RIGHT NOW", CyanDim, 14, TextAnchor.MiddleLeft);
            for (int i = 0; i < rooms.Count && i < 5; i++)
            {
                var rr = new Rect(r.x + 30f * S, y + 4f * S + i * rowH, w - 60f * S, rowH);
                bool hover = rr.Contains(Event.current.mousePosition);
                Text(rr, (hover ? ">>>" : "") + rooms[i].Name.ToUpper(), hover ? Pink : Cyan, 16, TextAnchor.MiddleLeft);
                Text(rr, (rooms[i].Ask ? "ASK   " : "") + rooms[i].Players + "/" + rooms[i].Max, hover ? Pink : CyanDim, 14, TextAnchor.MiddleRight);
                if (GUI.Button(rr, GUIContent.none, GUIStyle.none)) { action = JoinRoom; room = i; }
            }

            Text(new Rect(r.x + 20f * S, r.yMax - 110f * S, w - 40f * S, 22f * S), message.ToUpper(), Pink, 12, TextAnchor.MiddleCenter);
            float bw = 160f * S, bh = 38f * S, by = r.yMax - bh - 18f * S;
            if (Button(new Rect(r.x + 60f * S, by, bw, bh), "REFRESH")) action = JoinRefresh;
            if (Button(new Rect(r.xMax - 60f * S - bw, by, bw, bh), "CANCEL")) action = JoinCancel;
            var ev = Event.current;
            if (ev.type == EventType.KeyDown)
            {
                if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter) action = JoinCode;
                if (ev.keyCode == KeyCode.Escape) action = JoinCancel;
            }
            return t;
        }

        // Choose what kind of room to make: 1 = public, 4 = listed but the host accepts, 2 = private (code only), 3 = cancel (also keys 1, 2, 3 and Esc).
        public static int HostMenu(float openedAt, int defaultChoice)
        {
            Build();
            int choice = 0;
            float w = 560f * S, h = 330f * S;
            var r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            Frame(r, GlitchFor(openedAt));
            Header(r, "M.O.M>>> MAKE A ROOM", r.y + 12f * S);
            float bw = 440f * S, bh = 38f * S, bx = r.x + (w - bw) / 2f;
            if (Button(new Rect(bx, r.y + 56f * S, bw, bh), "PUBLIC ROOM")) choice = 1;
            Text(new Rect(bx, r.y + 92f * S, bw, 18f * S), "LISTED FOR EVERYBODY. ANYONE CAN JOIN", CyanDim, 11, TextAnchor.MiddleCenter);
            if (Button(new Rect(bx, r.y + 120f * S, bw, bh), "LISTED, YOU ACCEPT")) choice = 4;
            Text(new Rect(bx, r.y + 156f * S, bw, 18f * S), "LISTED, BUT EACH PLAYER WAITS FOR YOUR YES", CyanDim, 11, TextAnchor.MiddleCenter);
            if (Button(new Rect(bx, r.y + 184f * S, bw, bh), "PRIVATE ROOM")) choice = 2;
            Text(new Rect(bx, r.y + 220f * S, bw, 18f * S), "NOT LISTED. CODE ONLY. YOU ACCEPT EACH PLAYER", CyanDim, 11, TextAnchor.MiddleCenter);
            if (Button(new Rect(bx, r.y + 250f * S, bw, bh), "CANCEL")) choice = 3;
            var ev = Event.current;
            if (ev.type == EventType.KeyDown)
            {
                if (ev.keyCode == KeyCode.Alpha1 || ev.keyCode == KeyCode.Keypad1) choice = 1;
                if (ev.keyCode == KeyCode.Alpha2 || ev.keyCode == KeyCode.Keypad2) choice = 4;
                if (ev.keyCode == KeyCode.Alpha3 || ev.keyCode == KeyCode.Keypad3) choice = 2;
                if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter) choice = defaultChoice;   // Enter picks the room chosen in the launcher's settings
                if (ev.keyCode == KeyCode.Escape) choice = 3;
            }
            return choice;
        }

        // A player wants into my private room: Y accepts, N refuses (read in Update).
        public static void JoinRequest(string name)
        {
            Build();
            float w = 560f * S, h = 96f * S;
            var r = new Rect((Screen.width - w) / 2f, 70f * S, w, h);
            Frame(r, 0f);
            Header(r, "M.O.M>>> " + name.ToUpper() + " WANTS TO JOIN", r.y + 12f * S);
            Text(new Rect(r.x, r.y + 56f * S, w, 24f * S), "Y ACCEPT      N REFUSE", Pink, 16, TextAnchor.MiddleCenter);
        }

        // ---- chat ------------------------------------------------------------------------------------------------------------------------

        // The last few messages bottom-left; they fade out after a while unless the chat box is open.
        public static void ChatLog(List<Chat.Line> lines, float now, bool open)
        {
            Build();
            const float life = 14f;
            float lh = 26f * S, x = 20f * S, y = Screen.height - (open ? 190f : 146f) * S;
            int shown = 0;
            for (int i = lines.Count - 1; i >= 0 && shown < 7; i--)
            {
                var l = lines[i];
                float a = open ? 1f : Mathf.Clamp01((life - (now - l.At)) / 1.5f);
                if (a <= 0f) continue;
                string who = l.Name.ToUpperInvariant() + ": ";
                float nw = Width(who, 16);
                Color nameColor = l.Mine ? Cyan : Pink;
                Fill(new Rect(x - 4f * S, y, nw + Width(l.Text, 16) + 12f * S, lh), new Color(0f, 0.03f, 0.09f, 0.55f * a));
                Text(new Rect(x, y, nw + 4f * S, lh), who, new Color(nameColor.r, nameColor.g, nameColor.b, a), 16, TextAnchor.MiddleLeft);
                Text(new Rect(x + nw, y, 900f * S, lh), l.Text, new Color(White.r, White.g, White.b, a), 16, TextAnchor.MiddleLeft);
                y -= lh; shown++;
            }
        }

        // What a key event means for the chat box: 1 = send, 2 = close, 0 = nothing. Any Enter counts (also with Shift, on the number pad, or when only the character arrives).
        public static int ChatKeyAction(Event ev)
        {
            if (ev == null || ev.type != EventType.KeyDown) return 0;
            if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter || ev.character == '\n' || ev.character == '\r') return 1;
            if (ev.keyCode == KeyCode.Escape) return 2;
            return 0;
        }

        static GUIStyle chatField;

        // The box you type into (T opens it, Enter sends, Esc closes). Enter is taken here BEFORE the text field sees it: a text field turns Enter into a new line.
        public static string ChatBox(string textIn, out bool send, out bool cancel)
        {
            Build();
            send = cancel = false;
            float w = 640f * S, h = 42f * S;
            var r = new Rect(20f * S, Screen.height - 160f * S, w, h);
            Fill(r, new Color(0f, 0.03f, 0.09f, 0.9f));
            Fill(new Rect(r.x, r.yMax - 2f * S, r.width, 2f * S), Pink);
            Text(new Rect(r.x + 10f * S, r.y, 70f * S, h), "SAY>", Pink, 16, TextAnchor.MiddleLeft);
            Text(new Rect(r.xMax - 250f * S, r.y, 240f * S, h), "ENTER SEND   ESC CLOSE", CyanDim, 12, TextAnchor.MiddleRight);

            var ev = Event.current;
            int action = ChatKeyAction(ev);
            if (action != 0) { if (action == 1) send = true; else cancel = true; ev.Use(); return textIn; }

            if (chatField == null) { chatField = Style(18, TextAnchor.MiddleLeft); chatField.normal.textColor = White; chatField.focused.textColor = White; chatField.wordWrap = false; chatField.clipping = TextClipping.Clip; }
            chatField.fontSize = Mathf.RoundToInt(18 * S);
            var fr = new Rect(r.x + 80f * S, r.y + 4f * S, w - 340f * S, h - 8f * S);
            GUI.SetNextControlName("chatbox");
            string t = GUI.TextField(fr, textIn, Chat.MaxText, chatField);
            GUI.FocusControl("chatbox");
            return t.Replace("\n", "").Replace("\r", "");        // a chat message is one line, whatever reaches the box
        }

        // Menu-style button: plain cyan text; when hovered the game's pink ">>>" appears in front, like "Resume" in the pause menu.
        static bool Button(Rect r, string label)
        {
            bool hover = r.Contains(Event.current.mousePosition);
            Text(r, hover ? ">>>" + label : label, hover ? Pink : Cyan, 18, TextAnchor.MiddleCenter);
            float t = Mathf.Max(1f, S);
            Fill(new Rect(r.x, r.yMax - t, r.width, t), hover ? Pink : CyanDim);
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }
    }
}
