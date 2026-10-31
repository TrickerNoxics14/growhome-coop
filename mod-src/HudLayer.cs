using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace CoopMod
{
    // One piece of the mod's HUD (CoopHud): a group of uGUI Images and Texts on the mod's own canvas, which is the game's GUI camera with the
    // game's scaler. Each frame the piece lays itself out with Panel / Fill / Label in screen pixels (top-left origin, the same numbers the
    // overlay used, so the pure layout functions stay the single layout source); the layer turns that into pooled uGUI objects in the game's
    // HUD font and the game's M.O.M panel art, and switches off whatever the piece did not use this frame. Nothing here is IMGUI.
    public class HudLayer
    {
        public readonly string Name;
        public RectTransform Root;
        readonly List<Image> imgs = new List<Image>();
        readonly List<Text> texts = new List<Text>();
        readonly List<bool> mono = new List<bool>();
        Text measure;
        int ni, nt, order;
        public float S = 1f, Sw = 1280f, Sh = 720f, Sf = 1f;    // the overlay's scale, the screen in pixels, the canvas scale factor
        public int Frames;                                     // frames this layer was laid out (the audit's proof it runs)
        public bool Visible { get { return Root != null && Root.gameObject.activeSelf; } }

        public HudLayer(string name) { Name = name; }

        // on a new canvas (a new level): the old objects died with the old canvas
        public void Forget() { Root = null; imgs.Clear(); texts.Clear(); mono.Clear(); measure = null; }

        bool Ensure()
        {
            if (CoopHud.Canvas == null) return false;
            if (Root != null) return true;
            Forget();
            var go = new GameObject(Name, typeof(RectTransform));
            go.layer = CoopHud.Canvas.gameObject.layer;
            go.transform.SetParent(CoopHud.Canvas.transform, false);
            Root = (RectTransform)go.transform;
            Root.anchorMin = Vector2.zero; Root.anchorMax = Vector2.one; Root.pivot = new Vector2(0.5f, 0.5f); Root.anchoredPosition = Vector2.zero; Root.sizeDelta = Vector2.zero;
            var m = new GameObject("Measure", typeof(RectTransform));
            m.layer = go.layer;
            m.transform.SetParent(Root, false);
            measure = m.AddComponent<Text>();
            measure.font = CoopHud.GameFont;
            measure.horizontalOverflow = HorizontalWrapMode.Overflow; measure.verticalOverflow = VerticalWrapMode.Overflow; measure.supportRichText = false;
            m.SetActive(false);
            return true;
        }

        // Starts a frame. False: no canvas yet, or the piece is not shown (then everything of it is off).
        public bool Begin(bool show)
        {
            if (!Ensure()) return false;
            if (!show) { if (Root.gameObject.activeSelf) Root.gameObject.SetActive(false); return false; }
            if (!Root.gameObject.activeSelf) Root.gameObject.SetActive(true);
            Sf = CoopHud.Canvas.scaleFactor > 0.0001f ? CoopHud.Canvas.scaleFactor : 1f;
            var cr = ((RectTransform)CoopHud.Canvas.transform).rect;
            Sw = cr.width * Sf; Sh = cr.height * Sf;
            if (Sw < 1f || Sh < 1f) { Sw = Screen.width; Sh = Screen.height; }
            S = CoopUI.ScaleFor(Sh);
            ni = nt = order = 0;
            return true;
        }

        public void End()
        {
            for (int i = ni; i < imgs.Count; i++) if (imgs[i] != null && imgs[i].gameObject.activeSelf) imgs[i].gameObject.SetActive(false);
            for (int i = nt; i < texts.Count; i++) if (texts[i] != null && texts[i].gameObject.activeSelf) texts[i].gameObject.SetActive(false);
            Frames++;
        }

        public void Hide() { if (Root != null && Root.gameObject.activeSelf) Root.gameObject.SetActive(false); }

        void Place(RectTransform rt, Rect r)
        {
            if (rt.GetSiblingIndex() != order) rt.SetSiblingIndex(order);
            order++;
            var p = new Vector2(r.x / Sf, -r.y / Sf); var z = new Vector2(r.width / Sf, r.height / Sf);
            if (rt.anchoredPosition != p) rt.anchoredPosition = p;
            if (rt.sizeDelta != z) rt.sizeDelta = z;
        }

        RectTransform NewItem(string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = Root.gameObject.layer;
            go.transform.SetParent(Root, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            return rt;
        }

        Image TakeImage()
        {
            if (ni < imgs.Count && imgs[ni] == null) { imgs.Clear(); ni = 0; }     // destroyed under us: start the pool again
            if (ni == imgs.Count) imgs.Add(NewItem("Art" + imgs.Count).gameObject.AddComponent<Image>());
            var img = imgs[ni++];
            if (!img.gameObject.activeSelf) img.gameObject.SetActive(true);
            return img;
        }

        // the game's M.O.M panel art (HintPanel's background), faded by alpha; 'glitch' > 0 jitters it sideways for a moment, as a panel that just opened
        public Image Panel(Rect r, float alpha, float glitch)
        {
            if (glitch > 0f) r.x += Mathf.Sin(Time.realtimeSinceStartup * 90f) * 4f * glitch * S;
            var img = TakeImage();
            var src = CoopHud.PanelArt;
            if (src != null)
            {
                if (img.sprite != src.sprite) { img.sprite = src.sprite; img.type = src.type; img.material = src.material; img.fillCenter = src.fillCenter; }
                var c = src.color; c.a *= alpha;
                if (img.color != c) img.color = c;
            }
            else { var c = CoopUI.Panel; c.a *= alpha; if (img.sprite != null) img.sprite = null; if (img.color != c) img.color = c; }
            Place(img.rectTransform, r);
            return img;
        }

        public Image Panel(Rect r) { return Panel(r, 1f, 0f); }

        // a plain block of colour (the chat line's backing, the SAY> bar)
        public Image Fill(Rect r, Color c)
        {
            var img = TakeImage();
            if (img.sprite != null) img.sprite = null;
            if (img.color != c) img.color = c;
            Place(img.rectTransform, r);
            return img;
        }

        public int FontSize(int size) { return Mathf.Max(1, Mathf.RoundToInt(size * S / Sf)); }

        public static Font FontFor(bool fixedWidth) { return fixedWidth && CoopHud.FixedFont != null ? CoopHud.FixedFont : CoopHud.GameFont; }

        // a line of text in the game's HUD font (fixedWidth: the game's block_fixed, for times), with the soft shadow the overlay had
        public Text Label(Rect r, string s, Color c, int size, TextAnchor anchor, bool fixedWidth)
        {
            if (nt < texts.Count && texts[nt] == null) { texts.Clear(); mono.Clear(); nt = 0; }
            if (nt == texts.Count)
            {
                var rt = NewItem("Text" + texts.Count);
                var t0 = rt.gameObject.AddComponent<Text>();
                t0.horizontalOverflow = HorizontalWrapMode.Overflow; t0.verticalOverflow = VerticalWrapMode.Overflow; t0.supportRichText = false;
                var sh = rt.gameObject.AddComponent<Shadow>();
                sh.effectColor = new Color(0f, 0.04f, 0.12f, 0.85f);
                texts.Add(t0); mono.Add(false);
            }
            var t = texts[nt]; mono[nt] = fixedWidth; nt++;
            if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            var f = FontFor(fixedWidth);
            if (t.font != f) t.font = f;
            int fs = FontSize(size);
            if (t.fontSize != fs) t.fontSize = fs;
            if (t.alignment != anchor) t.alignment = anchor;
            if (t.color != c) t.color = c;
            s = s ?? "";
            if (t.text != s) t.text = s;
            var shadow = t.GetComponent<Shadow>();
            if (shadow != null) { var d = new Vector2(S / Sf, -S / Sf); if (shadow.effectDistance != d) shadow.effectDistance = d; var sc = new Color(0f, 0.04f, 0.12f, 0.85f * c.a); if (shadow.effectColor != sc) shadow.effectColor = sc; }
            Place(t.rectTransform, r);
            return t;
        }

        public Text Label(Rect r, string s, Color c, int size, TextAnchor anchor) { return Label(r, s, c, size, anchor, false); }

        // how wide a text is on screen, in pixels (the game font at that size)
        public float Width(string s, int size, bool fixedWidth)
        {
            if (!Ensure() || string.IsNullOrEmpty(s)) return 0f;
            var f = FontFor(fixedWidth);
            if (f == null) return s.Length * size * S * 0.6f;
            measure.font = f; measure.fontSize = FontSize(size); measure.text = s;
            float w = measure.preferredWidth * Sf;
            return w > 0f ? w : s.Length * size * S * 0.6f;
        }

        public float Width(string s, int size) { return Width(s, size, false); }

        // the text cut (with "...") so it is at most maxWidth pixels wide
        public string Cut(string s, int size, float maxWidth)
        {
            if (string.IsNullOrEmpty(s) || Width(s, size) <= maxWidth) return s ?? "";
            int n = s.Length;
            while (n > 0 && Width(s.Substring(0, n) + "...", size) > maxWidth) n = Math.Max(0, n - Math.Max(1, (n / 8)));
            return n <= 0 ? "" : s.Substring(0, n).TrimEnd() + "...";
        }

        // ---- for the audit -----------------------------------------------------------------------------------------------------------
        public List<Text> ShownTexts() { var l = new List<Text>(); if (!Visible) return l; for (int i = 0; i < nt && i < texts.Count; i++) if (texts[i] != null && texts[i].gameObject.activeSelf) l.Add(texts[i]); return l; }
        public List<bool> ShownFixed() { var l = new List<bool>(); if (!Visible) return l; for (int i = 0; i < nt && i < texts.Count; i++) if (texts[i] != null && texts[i].gameObject.activeSelf) l.Add(mono[i]); return l; }
        public List<Image> ShownImages() { var l = new List<Image>(); if (!Visible) return l; for (int i = 0; i < ni && i < imgs.Count; i++) if (imgs[i] != null && imgs[i].gameObject.activeSelf) l.Add(imgs[i]); return l; }
        public string AllText() { var sb = new System.Text.StringBuilder(); foreach (var t in ShownTexts()) sb.Append(t.text).Append('|'); return sb.ToString(); }
    }
}
