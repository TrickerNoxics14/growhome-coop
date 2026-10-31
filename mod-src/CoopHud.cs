using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace CoopMod
{
    // The mod's own HUD: one uGUI canvas set up like the game's HUD canvas (UpgradeCanvas: screen space on the GUI camera, the same scaler and
    // aspect keeper), so its pieces get the game's look, font and scaling. It is the mod's and not a copy of the game's canvas, because the game's
    // switches itself off whenever no M.O.M message is up or time stands still. Built again for every GUIManager (every level), like CoopMenu.
    //
    // Pieces: the join request ("NAME WANTS TO JOIN  Y LET IN  N REFUSE"; it does not take input or stop play: the host keeps climbing and
    // answers with Y / N or LET IN / REFUSE in the co-op menu, as before), the B.U.D. LINK line, M.O.M messages the game's panel cannot take,
    // the chat log and the SAY> line, the name tags over other players and the speedrun timer (HudPieces.cs, HudLayer.cs). Everything that used
    // to be drawn over the game with IMGUI is here now, on the game's GUI camera, in the game's font and art.
    public static partial class CoopHud
    {
        public const string Name = "CoopHud";
        static GUIManager builtFor;
        public static Canvas Canvas;
        public static RectTransform Root;          // the aspect keeper every piece is placed in (the game's HUD panels sit in theirs)
        public static Font GameFont;
        public static Color TextNormal = new Color(0.66f, 0.97f, 1f, 1f), TextDim = new Color(0.66f, 0.97f, 1f, 0.8f);
        public static int Builds;
        public static string LastProblem = "";

        // the join request strip
        public static RectTransform Request;
        public static Text RequestTop, RequestKeys;
        public static Image RequestArt;
        public const string RequestKeysText = "Y LET IN     N REFUSE";
        public static string RequestTopFor(string name) { return "M.O.M>>> " + Party.Short((name ?? "").ToUpperInvariant(), Party.NameLength) + " WANTS TO JOIN"; }
        static string shownFor; static float lastStinger = -99f;
        public static bool RequestShownForTest { get { return Request != null && Request.gameObject.activeInHierarchy; } }

        public static void Tick(CoopBehaviour mod)
        {
            if (!Singleton<GUIManager>.Exists) return;
            var gm = Singleton<GUIManager>.Instance;
            if (gm != builtFor || Canvas == null)
            {
                if (!Singleton<AbilityHUD>.Exists || Singleton<AbilityHUD>.Instance.m_canvas == null) return;      // a level without the game's HUD
                builtFor = gm;
                try { Build(gm); Builds++; CoopBehaviour.Log("ui: co-op HUD canvas built (font " + (GameFont == null ? "none" : GameFont.name) + ")"); }
                catch (Exception e) { LastProblem = SharedWorld.Short(e); CoopBehaviour.Log("ui: could not build the co-op HUD: " + LastProblem); Canvas = null; return; }
            }
            RequestTick(mod);
        }

        static void Build(GUIManager gm)
        {
            Canvas = null; Root = null; Request = null;
            var hud = Singleton<AbilityHUD>.Instance;
            var gameCanvas = hud.m_canvas.GetComponent<Canvas>();
            var gameScaler = hud.m_canvas.GetComponent<CanvasScaler>();
            if (hud.m_textBottomLine != null) { GameFont = hud.m_textBottomLine.font; TextDim = hud.m_textBottomLine.color; }
            if (hud.m_textTopLine != null) TextNormal = hud.m_textTopLine.color;
            FindArt(hud);

            var old = gm.transform.Find(Name);
            if (old != null) UnityEngine.Object.Destroy(old.gameObject);
            var go = new GameObject(Name, typeof(RectTransform));
            go.layer = hud.m_canvas.layer;
            go.transform.SetParent(gm.transform, false);
            Canvas = go.AddComponent<Canvas>();
            Canvas.renderMode = RenderMode.ScreenSpaceCamera;
            Canvas.worldCamera = gm.ActiveCamera;
            if (gameCanvas != null) { Canvas.planeDistance = gameCanvas.planeDistance; Canvas.sortingOrder = gameCanvas.sortingOrder + 1; }
            var scaler = go.AddComponent<CanvasScaler>();
            if (gameScaler != null)
            {
                scaler.uiScaleMode = gameScaler.uiScaleMode; scaler.referenceResolution = gameScaler.referenceResolution; scaler.screenMatchMode = gameScaler.screenMatchMode;
                scaler.matchWidthOrHeight = gameScaler.matchWidthOrHeight; scaler.referencePixelsPerUnit = gameScaler.referencePixelsPerUnit; scaler.scaleFactor = gameScaler.scaleFactor;
            }
            // no GraphicRaycaster: the HUD never takes a click

            var keeperGo = new GameObject("AspectKeeper", typeof(RectTransform));
            keeperGo.layer = go.layer;
            keeperGo.transform.SetParent(go.transform, false);
            Root = (RectTransform)keeperGo.transform;
            Stretch(Root);
            var gameKeeper = hud.m_canvas.transform.Find("AspectKeeper");
            var gameFitter = gameKeeper == null ? null : gameKeeper.GetComponent<AspectRatioFitter>();
            if (gameFitter != null) { var f = keeperGo.AddComponent<AspectRatioFitter>(); f.aspectMode = gameFitter.aspectMode; f.aspectRatio = gameFitter.aspectRatio; KeeperMode = gameFitter.aspectMode; KeeperAspect = gameFitter.aspectRatio; }

            BuildRequest(hud);
            foreach (var l in Layers) l.Forget();
            BuildSay();
        }

        static void Stretch(RectTransform rt) { rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.pivot = new Vector2(0.5f, 0.5f); rt.anchoredPosition = Vector2.zero; rt.sizeDelta = Vector2.zero; }

        // The art of one of the game's own HUD images (sprite, slicing, colour, material), on a RectTransform of the mod's: nothing of the game's
        // scripts comes along.
        public static Image ArtLike(Image src, Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            if (src != null) { img.sprite = src.sprite; img.type = src.type; img.color = src.color; img.material = src.material; img.fillCenter = src.fillCenter; }
            else img.color = new Color(0.02f, 0.08f, 0.18f, 0.8f);
            return img;
        }

        public static Text TextLike(Text src, Transform parent, string name, int size, Color color, TextAnchor anchor)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.font = GameFont != null ? GameFont : src != null ? src.font : null;
            t.fontSize = size; t.color = color; t.alignment = anchor;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow; t.supportRichText = false;
            if (src != null) { t.lineSpacing = src.lineSpacing; t.fontStyle = src.fontStyle; }
            return t;
        }

        // ---- the join request -------------------------------------------------------------------------------------------------------------

        public static readonly Vector2 RequestSize = new Vector2(360f, 58f);
        public const float RequestY = -118f;                    // below the game's warning band at the top of the HUD

        static void BuildRequest(AbilityHUD hud)
        {
            Image topArt = null;
            if (hud.m_sideHintPanel != null)
            {
                var t = hud.m_sideHintPanel.Find("TopBackground");
                if (t != null) topArt = t.GetComponent<Image>();
            }
            RequestArt = ArtLike(topArt, Root, "JoinRequest");
            Request = (RectTransform)RequestArt.transform;
            Request.anchorMin = Request.anchorMax = new Vector2(0.5f, 1f); Request.pivot = new Vector2(0.5f, 1f);
            Request.sizeDelta = RequestSize; Request.anchoredPosition = new Vector2(0f, RequestY);
            RequestTop = TextLike(hud.m_textTopLine, Request, "Top", 22, TextNormal, TextAnchor.MiddleCenter);
            var rt = (RectTransform)RequestTop.transform; rt.anchorMin = new Vector2(0f, 0.5f); rt.anchorMax = new Vector2(1f, 1f); rt.sizeDelta = new Vector2(-16f, 0f); rt.anchoredPosition = Vector2.zero;
            RequestKeys = TextLike(hud.m_textBottomLine, Request, "Keys", 18, CoopUI.Pink, TextAnchor.MiddleCenter);
            rt = (RectTransform)RequestKeys.transform; rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(1f, 0.5f); rt.sizeDelta = new Vector2(-16f, 0f); rt.anchoredPosition = Vector2.zero;
            RequestKeys.text = RequestKeysText;
            Request.gameObject.SetActive(false);
            shownFor = null;
        }

        static void RequestTick(CoopBehaviour mod)
        {
            if (Request == null) return;
            bool inMenu = Singleton<GUIManager>.Exists && Singleton<GUIManager>.Instance.AnyMenuActive;
            bool want = mod != null && mod.JoinRequestWanted && !inMenu;       // under a menu: the co-op menu's LET IN / REFUSE lines, the start / pause menu's notice line (MenuNotice)
            if (!want) { if (Request.gameObject.activeSelf) Request.gameObject.SetActive(false); shownFor = null; return; }
            string top = RequestTopFor(mod.PendingNameForTest);
            if (RequestTop.text != top) { RequestTop.text = top; FitText(RequestTop, Request.sizeDelta.x - 16f, 22, 14); }
            if (!Request.gameObject.activeSelf)
            {
                Request.gameObject.SetActive(true);
                if (shownFor != top && Time.realtimeSinceStartup - lastStinger > 0.3f)
                {
                    // the game's own "panel opens" effect (AbilityHUD.UpdatePanel)
                    lastStinger = Time.realtimeSinceStartup;
                    try { ExtendedMonoBehaviour.PlayStinger("HUD.SideHintOpen"); GUIManager.GUIBlurTrigger(0.9f, 0.95f); } catch (Exception) { }
                }
                shownFor = top;
            }
            Dodge();
        }

        // smaller until it fits (long names)
        public static void FitText(Text t, float width, int size, int min)
        {
            t.fontSize = size;
            while (t.fontSize > min && t.preferredWidth > width) t.fontSize -= 2;
        }

        // The game's M.O.M side panel may be up at the same time: the strip moves left of it rather than lie on top of it.
        static readonly Vector3[] corners = new Vector3[4], mine = new Vector3[4];
        static void Dodge()
        {
            Request.anchoredPosition = new Vector2(0f, RequestY);
            if (!Singleton<AbilityHUD>.Exists) return;
            var panel = Singleton<AbilityHUD>.Instance.m_sideHintPanel;
            var cam = Canvas.worldCamera;
            if (panel == null || !panel.gameObject.activeInHierarchy || cam == null) return;
            Rect a = ScreenRect(Request, mine, cam), b = Rect.MinMaxRect(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
            foreach (string part in new[] { "TopBackground", "BottomBackground" })
            {
                var t = panel.Find(part) as RectTransform;
                if (t == null || !t.gameObject.activeInHierarchy) continue;
                var r = ScreenRect(t, corners, cam);
                b = Rect.MinMaxRect(Mathf.Min(b.xMin, r.xMin), Mathf.Min(b.yMin, r.yMin), Mathf.Max(b.xMax, r.xMax), Mathf.Max(b.yMax, r.yMax));
            }
            if (b.xMin > b.xMax || !a.Overlaps(b)) return;
            float pixels = a.xMax - b.xMin + 8f;
            float scale = Canvas.scaleFactor > 0f ? Canvas.scaleFactor : 1f;
            Request.anchoredPosition = new Vector2(-pixels / scale, RequestY);
        }

        public static Rect ScreenRect(RectTransform t, Vector3[] buf, Camera cam)
        {
            t.GetWorldCorners(buf);
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                var p = RectTransformUtility.WorldToScreenPoint(cam, buf[i]);
                x0 = Mathf.Min(x0, p.x); y0 = Mathf.Min(y0, p.y); x1 = Mathf.Max(x1, p.x); y1 = Mathf.Max(y1, p.y);
            }
            return Rect.MinMaxRect(x0, y0, x1, y1);
        }
    }
}
