using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace CoopMod
{
    // What co-op has to say while the game's own start menu or pause menu is up (the HUD is hidden under menus, as the game hides its own):
    // a line of that menu, made from one of its own lines like the co-op menu's member lines (the game's font, size and colour, nothing to select or
    // press), at the bottom of its list of lines. It says, in this order:
    //  - a player asking to join: "M.O.M>>> ANNA WANTS TO JOIN" / "Y LET IN     N REFUSE" (Y and N answer from any screen, as before),
    //  - the M.O.M messages that came while the menu is up (the invite code of a room just made, a join that did not work, a friend's messages),
    //    one after the other, each 6.5 s of real time, as the HUD's M.O.M panel shows them.
    // Under any other game menu the waiting messages and the chat lines do not age (CoopUI.HoldWaiting, HoldChat): they show once it closes.
    public static class MenuNotice
    {
        public const string LineName = "CoopNotice";
        public const int FontMax = 24, FontMin = 14;
        public static Text StartText, PauseText;
        static GameObject startLine, pauseLine;
        static CoopUI.ToastItem current;
        static float lastTick = -1f;
        public static string LastShown = "";
        public static int Shown;                      // how many different notices were put up (for the audit)

        public static void Build(GUIMenu start, GUIMenu pause)
        {
            startLine = pauseLine = null; StartText = PauseText = null; current = null;
            if (pause != null)
            {
                var like = pause.transform.Find("AspectKeeper/MenuGroupPanel/ButtonCamera");
                if (like != null) { pauseLine = Make(like); PauseText = TextOf(pauseLine); }
            }
            if (start != null)
            {
                var like = start.transform.Find("Panel/ButtonPanel/SettingsButton");
                if (like != null) { startLine = Make(like); StartText = TextOf(startLine); }
            }
        }

        static Text TextOf(GameObject line) { var t = line == null ? null : line.transform.Find("ButText"); return t == null ? null : t.GetComponent<Text>(); }

        // a copy of the menu's own line with everything that selects, highlights or presses taken off; its label wraps onto two lines
        static GameObject Make(Transform like)
        {
            var go = (GameObject)UnityEngine.Object.Instantiate(like.gameObject);
            go.name = LineName;
            go.transform.SetParent(like.parent, false);
            go.transform.SetAsLastSibling();
            go.transform.localScale = like.localScale;
            // out of the menu's layout: showing or hiding the line must never move the menu's own lines (a resting mouse pointer would then hover,
            // and so select, another line); Place puts it under the last line instead
            var le = go.GetComponent<LayoutElement>(); if (le == null) le = go.AddComponent<LayoutElement>();
            le.ignoreLayout = true;
            var b = go.GetComponent<Button>(); if (b != null) UnityEngine.Object.DestroyImmediate(b);
            var an = go.GetComponent<Animator>(); if (an != null) UnityEngine.Object.DestroyImmediate(an);
            var img = go.GetComponent<Image>(); if (img != null) img.enabled = false;
            foreach (var l in go.GetComponentsInChildren<LocaliseButtonText>(true)) UnityEngine.Object.DestroyImmediate(l);   // the text is ours, never a key to translate
            foreach (string part in new[] { "Arrow", "Stars" }) { var p = go.transform.Find(part); if (p != null) p.gameObject.SetActive(false); }
            var label = go.transform.Find("ButText");
            if (label != null)
            {
                var rt = (RectTransform)label;
                float left = rt.anchorMin.x == rt.anchorMax.x ? rt.anchoredPosition.x - rt.sizeDelta.x * rt.pivot.x : 0f;
                rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0.5f, 0.5f);
                rt.offsetMin = new Vector2(Mathf.Max(0f, left), 1f); rt.offsetMax = new Vector2(-8f, -1f);
                var t = label.GetComponent<Text>();
                t.text = "";
                t.supportRichText = false;
                t.alignment = TextAnchor.MiddleLeft;
                t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Truncate;
                t.resizeTextForBestFit = true; t.resizeTextMinSize = FontMin; t.resizeTextMaxSize = FontMax; t.fontSize = FontMax;
                for (int i = label.childCount - 1; i >= 0; i--) UnityEngine.Object.DestroyImmediate(label.GetChild(i).gameObject);   // "NEW" markers and the like
            }
            go.SetActive(false);
            return go;
        }

        // Every frame from Bootstrap.Update (real time: the start screen and the pause stop game time).
        public static void Tick(CoopBehaviour mod)
        {
            float now = Time.realtimeSinceStartup;
            float dt = lastTick < 0f ? 0f : Mathf.Clamp(now - lastTick, 0f, 1f);
            lastTick = now;
            bool menu = CoopHud.InMenu;
            if (menu && !CoopUI.Hidden)
            {
                CoopUI.HoldWaiting(now);
                if (mod != null && !mod.ChatOpen) CoopHud.HoldChat(now, dt);
            }
            var active = menu ? CoopMenu.ActiveGameMenu() : null;
            string where = active == null ? "" : active.name;
            GameObject line = where == "StartMenu" ? startLine : where == "PauseMenu" ? pauseLine : null;
            Text text = line == startLine ? StartText : line == pauseLine ? PauseText : null;
            string say = line == null || text == null || mod == null || !mod.IsOnline || CoopUI.Hidden ? "" : TextFor(mod, now);
            Show(startLine, StartText, line == startLine ? say : "");
            Show(pauseLine, PauseText, line == pauseLine ? say : "");
        }

        static string TextFor(CoopBehaviour mod, float now)
        {
            if (mod.JoinRequestWanted) return CoopHud.RequestTopFor(mod.PendingNameForTest) + "\n" + CoopHud.RequestKeysText;
            if (current != null && now - current.Born > CoopUI.ToastLife) current = null;
            if (current == null) current = CoopUI.TakeWaiting(now);
            return current == null ? "" : "M.O.M>>> " + current.Msg;
        }

        static void Show(GameObject line, Text text, string say)
        {
            if (line == null || text == null) return;
            bool want = say.Length > 0;
            if (want) Place(line);
            if (line.activeSelf != want) line.SetActive(want);
            if (!want) return;
            if (text.text != say)
            {
                text.text = say;
                if (say != LastShown)
                {
                    LastShown = say; Shown++;
                    try { ExtendedMonoBehaviour.PlayStinger("HUD.SideHintOpen"); } catch (Exception) { }      // the game's own "a message is up" sound
                }
            }
        }

        // the line sits right under the menu's last shown line, the same size, as the menu's layout would put one more line, but without the layout
        // making room for it (that would move every other line)
        static void Place(GameObject line)
        {
            var rt = line.transform as RectTransform;
            var parent = line.transform.parent;
            if (rt == null || parent == null) return;
            RectTransform last = null;
            for (int i = parent.childCount - 1; i >= 0 && last == null; i--)
            {
                var c = parent.GetChild(i) as RectTransform;
                if (c != null && c != rt && c.gameObject.activeSelf) last = c;
            }
            if (last == null) return;
            var vl = parent.GetComponent<VerticalLayoutGroup>();
            float gap = vl == null ? 0f : vl.spacing;
            var pos = last.anchoredPosition - new Vector2(0f, last.rect.height + gap);
            if (rt.anchorMin != last.anchorMin) rt.anchorMin = last.anchorMin;
            if (rt.anchorMax != last.anchorMax) rt.anchorMax = last.anchorMax;
            if (rt.pivot != last.pivot) rt.pivot = last.pivot;
            if (rt.sizeDelta != last.sizeDelta) rt.sizeDelta = last.sizeDelta;
            if (rt.anchoredPosition != pos) rt.anchoredPosition = pos;
        }

        // ---- for the audit ----
        public static GameObject StartLineForTest { get { return startLine; } }
        public static GameObject PauseLineForTest { get { return pauseLine; } }
        public static string TextNow(GUIMenu m)
        {
            var line = m == null ? null : m.name == "StartMenu" ? startLine : m.name == "PauseMenu" ? pauseLine : null;
            var t = line == startLine ? StartText : PauseText;
            return line != null && line.activeInHierarchy && t != null ? t.text : "";
        }
        public static void ResetForTest() { current = null; LastShown = ""; }
    }
}
