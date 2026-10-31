using System.Collections.Generic;
using UnityEngine;

namespace CoopMod
{
    // What is left of the old overlay: the colours of the mod's HUD pieces (the game's pale cyan, the pink-red of a selected item), the F1 switch,
    // the M.O.M message routing and the chat key rules. Nothing here draws: the HUD pieces are uGUI on the mod's own canvas (CoopHud,
    // HudPieces.cs) and the windows are pages of the game's own menu (CoopMenu, NativeMenus.cs). The mod makes no IMGUI calls at all.
    public static class CoopUI
    {
        public static readonly Color Cyan = new Color(0.66f, 0.97f, 1f, 1f);
        public static readonly Color CyanDim = new Color(0.66f, 0.97f, 1f, 0.55f);
        public static readonly Color White = new Color(0.95f, 1f, 1f, 1f);
        public static readonly Color Pink = new Color(1f, 0.42f, 0.5f, 1f);      // the game's selected-item colour
        public static readonly Color Panel = new Color(0.02f, 0.08f, 0.18f, 0.80f);

        public static bool Hidden;
        public static bool ShownForTest;      // the self test needs the display on, whatever the player chose with F1

        // the scale the old overlay used (1 at 720 lines); the HUD pieces keep their sizes with it
        public static float Scale { get { return ScaleFor(Screen.height); } }
        public static float ScaleFor(float screenHeight) { return Mathf.Max(0.8f, screenHeight / 720f); }

        // The shake of a panel that has just opened: 0.3 s of real time, then perfectly still (game time may be slowed or stopped).
        public static float GlitchFor(float openedAtRealtime) { return Mathf.Clamp01(1f - (Time.realtimeSinceStartup - openedAtRealtime) / 0.3f); }

        // ---- M.O.M transmissions --------------------------------------------------------------------------------------------------------

        public class ToastItem { public string Msg; public float Created, Born = -1f; }       // Born: when it first showed (-1: still waiting)
        static readonly List<ToastItem> toasts = new List<ToastItem>();
        public const float ToastLife = 6.5f, ToastWait = 15f;

        // Shown in the game's own M.O.M panel when the HUD can show it (NativeUI); otherwise the co-op menu's subtitle says it (when that menu is
        // up), the notice line of the start or pause menu does (MenuNotice) or the mod's own M.O.M panel on its HUD canvas does (HudPieces.DrawMirror).
        public static void Toast(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            if (!NativeUI.Offer(message.ToUpper())) OverlayToast(message);
        }

        // A two-line M.O.M message with its own top line (the invite code) and show time; the fallback gets both lines in one.
        public static void Toast2(string top, string bottom, float seconds)
        {
            if (string.IsNullOrEmpty(top) && string.IsNullOrEmpty(bottom)) return;
            if (!NativeUI.Offer(bottom.ToUpper(), top.ToUpper(), seconds)) OverlayToast(top + "  " + bottom);
        }

        public static void OverlayToast(string message)
        {
            var t = new ToastItem { Msg = message.ToUpper(), Created = Time.realtimeSinceStartup };
            if (CoopMenuHandler.ShowNotice(t.Msg)) t.Born = Time.realtimeSinceStartup;      // said in the co-op menu's subtitle line
            toasts.Add(t);
            while (toasts.Count > 6) toasts.RemoveAt(0);     // room for a whole batch handed back by the M.O.M panel (NativeUI keeps at most 6 waiting)
        }

        // the messages for the HUD's M.O.M panel now: shown ones until 6.5 s after they first showed, waiting ones for 15 s at most (F1 hiding them)
        public static List<ToastItem> ToastsToShow(float now)
        {
            toasts.RemoveAll(t => t.Born >= 0f ? now - t.Born > ToastLife : now - t.Created > ToastWait);
            return toasts;
        }

        // Under a game menu that has no place for them, waiting messages do not age: they show when it closes, never dropped unseen (MenuNotice).
        public static void HoldWaiting(float now) { foreach (var t in toasts) if (t.Born < 0f) t.Created = now; }

        // The oldest waiting message, now shown elsewhere (the notice line of the start or pause menu); null when none waits.
        public static ToastItem TakeWaiting(float now)
        {
            foreach (var t in toasts) if (t.Born < 0f) { t.Born = now; return t; }
            return null;
        }

        public static int WaitingCountForTest() { int n = 0; foreach (var t in toasts) if (t.Born < 0f) n++; return n; }

        public static List<string> OverlayToastsForTest() { var l = new List<string>(); foreach (var t in toasts) l.Add(t.Msg); return l; }
        public static void ClearOverlayToastsForTest() { toasts.Clear(); CoopMenuHandler.ClearNotice(); }

        // ---- chat keys -------------------------------------------------------------------------------------------------------------------

        // What a key event means for the chat line: 1 = send, 2 = close, 0 = nothing. Any Enter counts (also with Shift, on the number pad, or when only the character arrives).
        public static int ChatKeyAction(Event ev)
        {
            if (ev == null || ev.type != EventType.KeyDown) return 0;
            if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter || ev.character == '\n' || ev.character == '\r') return 1;
            if (ev.keyCode == KeyCode.Escape) return 2;
            return 0;
        }
    }
}
