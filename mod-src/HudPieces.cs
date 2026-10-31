using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CoopMod
{
    // The pieces of the mod's HUD that used to be drawn over the game (Bootstrap.OnGUI, now gone):
    //  - the B.U.D. LINK line (what is left of the status panel: who is linked, the room code, world notices; the key hints are in the CO-OP menu),
    //  - M.O.M messages the game's own side panel cannot take (the co-op menu's subtitle says them while it is up),
    //  - the chat log and the SAY> line you type into (a uGUI InputField, CoopField),
    //  - the name tags over the other players,
    //  - the speedrun timer, splits, race strip, countdown and results (SpeedrunUI.Draw).
    // Each piece decides every frame (LateUpdate, real time) whether it shows, with the same rules the overlay had; F1 hides the co-op pieces,
    // the game's menus hide them (the game hides its own HUD under menus too).
    public static partial class CoopHud
    {
        public static Image PanelArt;          // the game's M.O.M panel background (HintPanel/BottomBackground): sprite, slicing, colour
        public static Font FixedFont;          // the game's fixed-width digits font (block_fixed), for times

        public static readonly HudLayer SpeedrunLayer = new HudLayer("CoopSpeedrun");
        public static readonly HudLayer ChatLayer = new HudLayer("CoopChat");
        public static readonly HudLayer LinkLayer = new HudLayer("CoopLink");
        public static readonly HudLayer MirrorLayer = new HudLayer("CoopMessages");
        public static readonly HudLayer TagLayer = new HudLayer("CoopTags");
        public static readonly HudLayer[] Layers = { TagLayer, LinkLayer, ChatLayer, MirrorLayer, SpeedrunLayer };

        public static int DrawProblems; public static string DrawProblem = "";

        static void FindArt(AbilityHUD hud)
        {
            PanelArt = null;
            if (hud.m_sideHintPanel != null)
            {
                var t = hud.m_sideHintPanel.Find("BottomBackground");
                if (t != null) PanelArt = t.GetComponent<Image>();
            }
            if (FixedFont == null)
            {
                try { foreach (var o in Resources.FindObjectsOfTypeAll(typeof(Font))) { var f = o as Font; if (f != null && f.name == "block_fixed") { FixedFont = f; break; } } }
                catch (Exception) { }
                CoopBehaviour.Log("ui: fixed-width font " + (FixedFont != null ? FixedFont.name : "not found (times use the HUD font)"));
            }
        }

        public static bool InMenu { get { return Singleton<GUIManager>.Exists && Singleton<GUIManager>.Instance.AnyMenuActive; } }

        // ---- where the pieces go: the parts of the game's own HUD that none of its panels ever uses -----------------------------------
        // The game's HUD panels sit in a 16:9 aspect keeper (AspectRatioFitter, copied from the game at build). Measured from the game's panels
        // (HUD-LAYOUT dumps them at 800x450 and 1920x1080; fractions of the keeper, x from the left, y from the top):
        //   altitude + islands x .054-.211 y .376-.602   jetpack x .047-.263 y .804-.938   controls x .235-.766 y .831-.971
        //   warning / seeds / upgrades x .211-.789 y .031-.156   data bank x .781-.976 y .042-.222   crystal finder x .763-.919 y .88-.95
        //   M.O.M side panel: top box x .779-.945 y .118-.284, bottom box x .765-.965 y .589-.962
        // Free: Left (above the altitude panel) holds the speedrun timer; Middle (above the jetpack and controls, right of the altitude panel,
        // left of the M.O.M panel, under the warning band) holds the join request at its top, then the M.O.M messages, the chat log, the SAY>
        // line and the B.U.D. LINK line at its bottom; Right (the gap of the M.O.M column between its two boxes) holds the race strip.
        public static AspectRatioFitter.AspectMode KeeperMode = AspectRatioFitter.AspectMode.FitInParent;
        public static float KeeperAspect = 16f / 9f;

        public static Rect Keeper(float sw, float sh)
        {
            float w = sw, h = sh;
            if (KeeperAspect > 0.01f && sh > 0f)
            {
                bool wide = sw / sh > KeeperAspect;
                switch (KeeperMode)
                {
                    case AspectRatioFitter.AspectMode.FitInParent: if (wide) w = sh * KeeperAspect; else h = sw / KeeperAspect; break;
                    case AspectRatioFitter.AspectMode.EnvelopeParent: if (wide) h = sw / KeeperAspect; else w = sh * KeeperAspect; break;
                    case AspectRatioFitter.AspectMode.WidthControlsHeight: h = sw / KeeperAspect; break;
                    case AspectRatioFitter.AspectMode.HeightControlsWidth: w = sh * KeeperAspect; break;
                }
            }
            return new Rect((sw - w) / 2f, (sh - h) / 2f, w, h);
        }

        static Rect Part(float sw, float sh, float x0, float y0, float x1, float y1)
        {
            var k = Keeper(sw, sh);
            return Rect.MinMaxRect(Mathf.Max(0f, k.x + k.width * x0), Mathf.Max(0f, k.y + k.height * y0), Mathf.Min(sw, k.x + k.width * x1), Mathf.Min(sh, k.y + k.height * y1));
        }
        public static Rect ZoneLeft(float sw, float sh) { return Part(sw, sh, 0.016f, 0.015f, 0.205f, 0.36f); }
        public static Rect ZoneMiddle(float sw, float sh) { return Part(sw, sh, 0.225f, 0.255f, 0.755f, 0.795f); }
        public static Rect ZoneRight(float sw, float sh) { return Part(sw, sh, 0.775f, 0.293f, 0.985f, 0.583f); }
        // the top of the middle holds the M.O.M messages, the chat log stays under it
        public static float MessagesBottom(float sw, float sh) { var m = ZoneMiddle(sw, sh); return m.y + Keeper(sw, sh).height * 0.145f; }

        // the game's own "a panel opens" effect (AbilityHUD.UpdatePanel), at most every 0.3 s of real time and never under a menu
        public static void Appear()
        {
            if (InMenu || Time.realtimeSinceStartup - lastStinger < 0.3f) return;
            lastStinger = Time.realtimeSinceStartup;
            try { ExtendedMonoBehaviour.PlayStinger("HUD.SideHintOpen"); GUIManager.GUIBlurTrigger(0.9f, 0.95f); } catch (Exception) { }
        }

        // Every frame from Bootstrap.LateUpdate (after the other players' copies have moved).
        public static void Draw(CoopBehaviour mod)
        {
            if (Canvas == null || mod == null) return;
            bool menu = InMenu;
            Piece(delegate { SpeedrunUI.Draw(SpeedrunLayer); });
            Piece(delegate { DrawTags(mod, menu); });
            Piece(delegate { DrawLink(mod, menu); });
            Piece(delegate { DrawChat(mod, menu); });
            Piece(delegate { DrawSay(mod); });
            Piece(delegate { DrawMirror(mod, menu); });
        }

        static void Piece(Action a)
        {
            try { a(); }
            catch (Exception e) { DrawProblems++; if (DrawProblem.Length == 0) CoopBehaviour.Log("ui: co-op HUD piece: " + SharedWorld.Short(e)); DrawProblem = SharedWorld.Short(e); }
        }

        // ---- the B.U.D. LINK line ------------------------------------------------------------------------------------------------------

        public struct LinkState
        {
            public bool Online, InWorld, InLobby;
            public string Code, Notice, LobbyStatus;
            public List<KeyValuePair<string, bool>> Peers;     // name, connected
        }

        public const int LinkKindTitle = 0, LinkKindOk = 1, LinkKindWaiting = 2, LinkKindCode = 3, LinkKindNotice = 4;
        public struct Seg { public string Text; public int Kind; public Seg(string t, int k) { Text = t; Kind = k; } }

        // What the line says (pure; the audit checks it). Empty: nothing to report, so the line is not shown at all (solo, no room, no notice).
        public static List<Seg> LinkSegments(LinkState s)
        {
            var l = new List<Seg>();
            if (!s.Online || !s.InWorld) return l;
            int peers = s.Peers == null ? 0 : s.Peers.Count;
            string code = s.InLobby ? (s.Code ?? "") : "";
            string notice = (s.Notice ?? "").Trim();
            if (code.Length == 0 && !string.IsNullOrEmpty(s.LobbyStatus)) notice = (s.LobbyStatus + "  " + notice).Trim();     // as the old panel: the room's status only when there is no code
            if (!s.InLobby && peers == 0 && notice.Length == 0) return l;
            l.Add(new Seg("B.U.D. LINK", LinkKindTitle));
            if (s.Peers != null) foreach (var p in s.Peers) l.Add(new Seg("   >>> " + Party.Short((p.Key ?? "").ToUpperInvariant(), Party.NameLength), p.Value ? LinkKindOk : LinkKindWaiting));
            if (code.Length > 0) l.Add(new Seg("   CODE " + code, LinkKindCode));
            if (notice.Length > 0) l.Add(new Seg("   " + notice.ToUpperInvariant(), LinkKindNotice));
            return l;
        }

        public static string LinkLine(LinkState s) { var sb = new System.Text.StringBuilder(); foreach (var g in LinkSegments(s)) sb.Append(g.Text); return sb.ToString(); }

        static Color LinkColour(int kind) { return kind == LinkKindTitle ? CoopUI.White : kind == LinkKindWaiting ? CoopUI.Pink : kind == LinkKindCode ? CoopUI.CyanDim : CoopUI.Cyan; }

        // the bottom line of the middle zone (clear of the game's controls and jetpack panels below it)
        public static Rect LinkRect(float sw, float sh, float s) { var m = ZoneMiddle(sw, sh); return new Rect(m.x, m.yMax - 22f * s, m.width, 22f * s); }

        public static LinkState? LinkOverrideForTest;      // the audit's own state for the line (no real room needed)

        static void DrawLink(CoopBehaviour mod, bool menu)
        {
            var L = LinkLayer;
            var segs = CoopUI.Hidden || menu ? null : LinkSegments(LinkOverrideForTest ?? mod.LinkStateNow());
            if (!L.Begin(segs != null && segs.Count > 0)) return;
            var r = LinkRect(L.Sw, L.Sh, L.S);
            int size = 14;
            for (; size > 10; size -= 2) { float w = 0f; foreach (var g in segs) w += L.Width(g.Text, size); if (w <= r.width) break; }      // smaller until it fits (long names)
            int fs = size;
            segs = LinkFit(segs, t => L.Width(t, fs), (t, w) => L.Cut(t, fs, w), r.width);      // still too wide: the code and the notice stay, the peers give way
            float x = r.x;
            foreach (var g in segs)
            {
                float w = L.Width(g.Text, size);
                L.Label(new Rect(x, r.y, w + 4f * L.S, r.height), g.Text, LinkColour(g.Kind), size, TextAnchor.MiddleLeft);
                x += w;
            }
            L.End();
        }

        // The segments that fit `room`, in their order. Everything fits: unchanged. Otherwise the title and the room code are always kept, the
        // notice keeps at least a third of the line (cut with "..." when longer), and the players that do not fit become one "   +N" (pure: LINK-FIT).
        public static List<Seg> LinkFit(List<Seg> segs, Func<string, float> width, Func<string, float, string> cut, float room)
        {
            float all = 0f; foreach (var g in segs) all += width(g.Text);
            if (all <= room + 0.5f) return segs;
            Seg? title = null, code = null, notice = null; var peers = new List<Seg>();
            foreach (var g in segs)
            {
                if (g.Kind == LinkKindTitle) title = g;
                else if (g.Kind == LinkKindCode) code = g;
                else if (g.Kind == LinkKindNotice) notice = g;
                else peers.Add(g);
            }
            float left = room - (title.HasValue ? width(title.Value.Text) : 0f) - (code.HasValue ? width(code.Value.Text) : 0f);
            float keep = notice.HasValue ? Mathf.Min(width(notice.Value.Text), room / 3f) : 0f;       // what the notice is sure to get
            var shownPeers = new List<Seg>();
            for (int i = 0; i < peers.Count; i++)
            {
                int rest = peers.Count - i - 1;
                float more = rest > 0 ? width("   +" + rest) : 0f;
                float w = width(peers[i].Text);
                if (w + more > left - keep + 0.5f) break;
                shownPeers.Add(peers[i]); left -= w;
            }
            var l = new List<Seg>();
            if (title.HasValue) l.Add(title.Value);
            l.AddRange(shownPeers);
            if (shownPeers.Count < peers.Count) { var m = new Seg("   +" + (peers.Count - shownPeers.Count), LinkKindOk); l.Add(m); left -= width(m.Text); }
            if (code.HasValue) l.Add(code.Value);
            if (notice.HasValue && left > 0f)
            {
                string n = width(notice.Value.Text) <= left + 0.5f ? notice.Value.Text : cut(notice.Value.Text, left);
                if (n.Trim().Length > 0) l.Add(new Seg(n, LinkKindNotice));
            }
            return l;
        }

        // ---- M.O.M messages the game's panel could not take ------------------------------------------------------------------------------

        // M.O.M messages: under the join request, at the top of the middle zone (pure: HUD-LAYOUT checks them at every screen shape)
        public static float MirrorTop(float sw, float sh) { return ZoneMiddle(sw, sh).y; }
        public static Rect MirrorRect(float sw, float sh, float y, float w, float s) { var m = ZoneMiddle(sw, sh); return new Rect(m.x, y, Mathf.Min(w, m.width), 26f * s); }

        static void DrawMirror(CoopBehaviour mod, bool menu)
        {
            var L = MirrorLayer;
            bool show = mod.IsOnline && !CoopUI.Hidden && !menu;      // online, F1 not hiding; under a menu the start / pause menu's notice line says them, or they wait (MenuNotice)
            var items = show ? CoopUI.ToastsToShow(Time.realtimeSinceStartup) : null;
            if (!L.Begin(items != null && items.Count > 0)) return;
            float y = MirrorTop(L.Sw, L.Sh), last = MessagesBottom(L.Sw, L.Sh);
            float room = ZoneMiddle(L.Sw, L.Sh).width;
            float now = Time.realtimeSinceStartup;
            foreach (var t in items)
            {
                if (t.Born < 0f) { t.Born = now; Appear(); }
                float age = now - t.Born;
                int shown = Mathf.Clamp((int)(age * 48f), 0, t.Msg.Length);              // typed in, like the game's own prompts
                float alpha = age > 5.5f ? Mathf.Clamp01(6.5f - age) : 1f;
                string full = L.Cut("M.O.M>>> " + t.Msg, 14, room - 28f * L.S);       // kept inside its zone
                float w = L.Width(full, 14) + 24f * L.S;
                var r = MirrorRect(L.Sw, L.Sh, y, w, L.S);
                if (r.yMax > last + 0.5f) break;                                       // no room left above the chat log: the rest wait their turn
                L.Panel(r, alpha, 0f);
                var c = CoopUI.Cyan; c.a *= alpha;
                L.Label(new Rect(r.x + 12f * L.S, r.y, r.width - 12f * L.S, r.height), full.Substring(0, Mathf.Min(full.Length, 9 + shown)), c, 14, TextAnchor.MiddleLeft);
                y += r.height + 8f * L.S;
            }
            L.End();
        }

        // ---- the chat log ------------------------------------------------------------------------------------------------------------------

        public const float ChatLife = 14f;

        // Under a game menu the log is hidden: the lines still on show do not fade meanwhile (a friend's message is read when the menu closes).
        public static void HoldChat(float now, float dt)
        {
            if (dt <= 0f) return;
            foreach (var l in Chat.Lines) if (now - l.At < ChatLife + 1.5f) l.At = Mathf.Min(now, l.At + dt);
        }

        static void DrawChat(CoopBehaviour mod, bool menu)
        {
            var L = ChatLayer;
            bool open = mod.ChatOpen;
            bool show = mod.IsOnline && ((!CoopUI.Hidden && CoopBehaviour.ShowChat) || open) && (!menu || open);
            float now = Time.realtimeSinceStartup;
            bool any = false;
            if (show) foreach (var l in Chat.Lines) if (open || now - l.At < ChatLife + 1.5f) { any = true; break; }
            if (!L.Begin(show && any)) return;
            float S = L.S, lh = 26f * S;
            var zone = ZoneMiddle(L.Sw, L.Sh);
            float x = zone.x + 4f * S, top = MessagesBottom(L.Sw, L.Sh) + 6f * S;
            float y = (open ? SayRect(L.Sw, L.Sh, S).y : LinkRect(L.Sw, L.Sh, S).y) - 6f * S - lh;      // above the SAY> line / the link line
            int n = 0;
            for (int i = Chat.Lines.Count - 1; i >= 0 && n < 7 && y >= top - 0.5f; i--)
            {
                var l = Chat.Lines[i];
                float a = open ? 1f : Mathf.Clamp01((ChatLife - (now - l.At)) / 1.5f);
                if (a <= 0f) continue;
                string who = (l.Name ?? "").ToUpperInvariant() + ": ";
                float nw = L.Width(who, 16);
                if (nw > zone.xMax - x - 40f * S) { who = L.Cut(who, 16, zone.xMax - x - 40f * S); nw = L.Width(who, 16); }
                string msg = L.Cut(l.Text ?? "", 16, zone.xMax - x - nw - 12f * S);      // kept inside its zone
                float mw = L.Width(msg, 16);
                Color nc = l.Mine ? CoopUI.Cyan : CoopUI.Pink;
                L.Fill(new Rect(x - 4f * S, y, nw + mw + 12f * S, lh), new Color(0f, 0.03f, 0.09f, 0.55f * a));
                L.Label(new Rect(x, y, nw + 4f * S, lh), who, new Color(nc.r, nc.g, nc.b, a), 16, TextAnchor.MiddleLeft);
                L.Label(new Rect(x + nw, y, mw + 4f * S, lh), msg, new Color(CoopUI.White.r, CoopUI.White.g, CoopUI.White.b, a), 16, TextAnchor.MiddleLeft);
                y -= lh; n++;
            }
            L.End();
        }

        // ---- the SAY> line ------------------------------------------------------------------------------------------------------------------

        public static RectTransform SayRoot;
        public static CoopField SayField;
        public static Text SayPrompt, SayKeys;
        static Image sayBack, sayBar;
        public const string SayKeysText = "ENTER SEND   ESC CLOSE";

        static RectTransform Item(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            return rt;
        }

        static void BuildSay()
        {
            var root = new GameObject("CoopSay", typeof(RectTransform));
            root.layer = Canvas.gameObject.layer;
            root.transform.SetParent(Canvas.transform, false);
            SayRoot = (RectTransform)root.transform;
            SayRoot.anchorMin = Vector2.zero; SayRoot.anchorMax = Vector2.one; SayRoot.pivot = new Vector2(0.5f, 0.5f); SayRoot.anchoredPosition = Vector2.zero; SayRoot.sizeDelta = Vector2.zero;
            root.AddComponent<CoopSayLine>();
            sayBack = Item(SayRoot, "Back").gameObject.AddComponent<Image>(); sayBack.color = new Color(0f, 0.03f, 0.09f, 0.9f);
            sayBar = Item(SayRoot, "Bar").gameObject.AddComponent<Image>(); sayBar.color = CoopUI.Pink;
            SayPrompt = TextLike(null, Item(SayRoot, "Prompt"), "Text", 16, CoopUI.Pink, TextAnchor.MiddleLeft); SayPrompt.text = "SAY>";
            SayKeys = TextLike(null, Item(SayRoot, "Keys"), "Text", 12, CoopUI.CyanDim, TextAnchor.MiddleRight); SayKeys.text = SayKeysText;
            Stretch(SayPrompt.rectTransform); Stretch(SayKeys.rectTransform);

            var fieldRt = Item(SayRoot, "SayField");
            fieldRt.gameObject.SetActive(false);
            var group = fieldRt.gameObject.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false; group.interactable = true;
            var typed = TextLike(null, fieldRt, "Typed", 18, CoopUI.White, TextAnchor.MiddleLeft);
            Stretch(typed.rectTransform);
            typed.horizontalOverflow = HorizontalWrapMode.Overflow;
            SayField = fieldRt.gameObject.AddComponent<CoopField>();
            SayField.UpperCase = false;                    // chat keeps what was typed (the game font has lower case; anything it lacks is dropped)
            SayField.textComponent = typed;
            SayField.characterLimit = Chat.MaxText;
            SayField.lineType = InputField.LineType.SingleLine;
            SayField.transition = Selectable.Transition.None;
            var nav = new Navigation(); nav.mode = Navigation.Mode.None; SayField.navigation = nav;
            SayField.selectionColor = new Color(0.3f, 0.7f, 0.9f, 0.5f);
            fieldRt.gameObject.SetActive(true);
            root.SetActive(false);
        }

        static void PlacePx(RectTransform rt, Rect r, float sf)
        {
            var p = new Vector2(r.x / sf, -r.y / sf); var z = new Vector2(r.width / sf, r.height / sf);
            if (rt.anchoredPosition != p) rt.anchoredPosition = p;
            if (rt.sizeDelta != z) rt.sizeDelta = z;
        }

        // right above the link line, in the middle zone
        public static Rect SayRect(float sw, float sh, float s) { var m = ZoneMiddle(sw, sh); var l = LinkRect(sw, sh, s); return new Rect(m.x, l.y - 6f * s - 42f * s, Mathf.Min(640f * s, m.width), 42f * s); }

        public static bool SayShown { get { return SayRoot != null && SayRoot.gameObject.activeInHierarchy; } }

        static void DrawSay(CoopBehaviour mod)
        {
            if (SayRoot == null) return;
            bool open = mod.ChatOpen;
            if (SayRoot.gameObject.activeSelf != open) SayRoot.gameObject.SetActive(open);
            if (!open) return;
            if (SayRoot.GetSiblingIndex() != SayRoot.parent.childCount - 1) SayRoot.SetAsLastSibling();
            float sf = Canvas.scaleFactor > 0.0001f ? Canvas.scaleFactor : 1f;
            var cr = ((RectTransform)Canvas.transform).rect;
            float sw = cr.width * sf, sh = cr.height * sf;
            if (sw < 1f || sh < 1f) { sw = Screen.width; sh = Screen.height; }
            float S = CoopUI.ScaleFor(sh);
            var r = SayRect(sw, sh, S);
            PlacePx(sayBack.rectTransform, r, sf);
            PlacePx(sayBar.rectTransform, new Rect(r.x, r.yMax - 2f * S, r.width, 2f * S), sf);
            PlacePx((RectTransform)SayPrompt.transform.parent, new Rect(r.x + 10f * S, r.y, 70f * S, r.height), sf);
            PlacePx((RectTransform)SayKeys.transform.parent, new Rect(r.xMax - 250f * S, r.y, 240f * S, r.height), sf);
            PlacePx((RectTransform)SayField.transform, new Rect(r.x + 80f * S, r.y + 4f * S, r.width - 340f * S, r.height - 8f * S), sf);
            int fp = Mathf.Max(1, Mathf.RoundToInt(16 * S / sf)), fk = Mathf.Max(1, Mathf.RoundToInt(12 * S / sf)), ff = Mathf.Max(1, Mathf.RoundToInt(18 * S / sf));
            if (SayPrompt.fontSize != fp) SayPrompt.fontSize = fp;
            if (SayKeys.fontSize != fk) SayKeys.fontSize = fk;
            if (SayField.textComponent != null && SayField.textComponent.fontSize != ff) SayField.textComponent.fontSize = ff;
        }

        // ---- name tags over the other players --------------------------------------------------------------------------------------------

        public struct TagInfo { public string Name; public Vector3 Head; public float Meters; }

        public static string TagText(string name, float meters, bool showNames)
        {
            string d = meters.ToString("F0") + " M";
            return showNames && !string.IsNullOrEmpty(name) ? name.ToUpperInvariant() + "  " + d : d;
        }

        // the tag's rectangle for a point on screen (Unity's screen point: y up), kept inside the screen with an 8 px margin
        public static Rect TagRect(Vector2 screen, float w, float h, float sw, float sh, float s)
        {
            float m = 8f * s;
            float x = screen.x - w / 2f, y = (sh - screen.y) - h;
            x = Mathf.Clamp(x, m, Mathf.Max(m, sw - m - w));
            y = Mathf.Clamp(y, m, Mathf.Max(m, sh - m - h));
            return new Rect(x, y, w, h);
        }

        static readonly List<TagInfo> tags = new List<TagInfo>();
        public static int TagsShown;
        public static List<TagInfo> TagsForTest;              // the audit's stand-in for other players

        static void DrawTags(CoopBehaviour mod, bool menu)
        {
            var L = TagLayer;
            tags.Clear();
            var cam = Camera.main;
            if (mod.IsOnline && !CoopUI.Hidden && !menu && cam != null) { if (TagsForTest != null) tags.AddRange(TagsForTest); else mod.FillTags(tags); }
            TagsShown = 0;
            if (!L.Begin(tags.Count > 0)) return;
            foreach (var t in tags)
            {
                Vector3 sp = cam.WorldToScreenPoint(t.Head);
                if (sp.z <= 0f) continue;
                string s = TagText(t.Name, t.Meters, CoopBehaviour.ShowNames);
                float w = L.Width(s, 13) + 18f * L.S, h = 22f * L.S;
                var r = TagRect(new Vector2(sp.x, sp.y), w, h, L.Sw, L.Sh, L.S);
                L.Panel(r);
                L.Label(r, s, CoopUI.White, 13, TextAnchor.MiddleCenter);
                TagsShown++;
            }
            L.End();
        }
    }

    // On the SAY> line while the chat is open (it is the event system's selection then): the input module hands it each frame's update, and it
    // takes it, so the module neither moves a selection nor presses anything with the keys being typed. The typing itself runs in Bootstrap.ChatKeys.
    public class CoopSayLine : MonoBehaviour, IUpdateSelectedHandler
    {
        public void OnUpdateSelected(BaseEventData eventData) { eventData.Use(); }
    }
}
