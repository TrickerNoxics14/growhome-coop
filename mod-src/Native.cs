using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace CoopMod
{
    // The way into the game's own UI, instead of drawing over it.
    //  1. Each time the game builds its menus (once per level), the menus and the HUD are written to coopmod-ui.log as they are laid out in the level data:
    //     object paths, components, button events, texts, fonts, canvases. Code alone cannot show that, and the native co-op menu will be built from it.
    //  2. Co-op messages ("SIGNAL ACQUIRED: ...") appear in the game's own M.O.M side panel. When the HUD cannot show them (start screen, a menu open,
    //     the game really paused, the F1 display hidden) or the panel stays busy with the game's own message, they fall back to the overlay toast.
    //     The game's own M.O.M prompts are left alone: while one is on the panel or waiting for it, co-op messages use the overlay (adding a side-panel
    //     message makes MOMPrompts delete its prompt, and doing that from inside the HUD's own queue loop throws), and the prompt timer that every
    //     side-panel message resets is put back, so co-op traffic does not stop the game's prompts from ever coming.
    public static class NativeUI
    {
        public const string MsgPrefix = "CoopMsg";
        public static int Priority = 1000;          // the game's default; lower wins. Equal means a game message already showing is never pushed away.
        public static float ShowSeconds = 5f;
        public const float WaitLimit = 3f;          // longest a message waits behind a game message before it goes to the overlay instead
        public const float ShortenTo = 2.5f;        // with more messages waiting, each one is shown at least this long, then the next
        const int MaxPending = 6;
        const float StaleAfter = 15f;

        class Pending { public string Text; public float Born; }
        static readonly List<Pending> pending = new List<Pending>();
        static string activeName, activeText;
        static float activeSince, currentSince = -1f;
        static int serial;

        public static int NativeShown, OverlayUsed;        // counters for the audit
        public static string LastNativeText = "";

        // ---- 2. messages --------------------------------------------------------------------------------------------------------------

        // Called by CoopUI.Toast. False means "show it on the overlay now".
        public static bool Offer(string text)
        {
            if (!CanShow()) return false;
            pending.Add(new Pending { Text = text, Born = Time.realtimeSinceStartup });
            while (pending.Count > MaxPending) { ToOverlay(pending[0].Text); pending.RemoveAt(0); }
            return true;
        }

        public static bool CanShow()
        {
            try
            {
                if (CoopUI.Hidden || Time.timeScale < 0.001f || !AbilityHUD.s_allowVisibility) return false;
                if (!Singleton<AbilityHUD>.Exists || Singleton<AbilityHUD>.Instance.m_canvas == null || Singleton<AbilityHUD>.Instance.m_textBottomLine == null) return false;
                if (Singleton<GUIManager>.Exists && Singleton<GUIManager>.Instance.AnyMenuActive) return false;
                if (IsCurrent(MomPromptName) || IsQueued(MomPromptName)) return false;
                return WorldSync.InWorld();
            }
            catch (Exception) { return false; }
        }

        // ---- the game's own M.O.M prompts ----
        public const string MomPromptName = "MOMPrompt";
        static FieldInfo momTimeField;
        static MOMPrompts momPrompts;
        static float momSaved = float.NaN;

        static MOMPrompts Prompts()
        {
            if (momPrompts == null) momPrompts = (MOMPrompts)UnityEngine.Object.FindObjectOfType(typeof(MOMPrompts));
            if (momTimeField == null) momTimeField = typeof(MOMPrompts).GetField("m_timeOfLastMOMMessage", BindingFlags.Instance | BindingFlags.NonPublic);
            return momTimeField == null ? null : momPrompts;
        }

        static void SaveMomTimer() { var p = Prompts(); momSaved = p == null ? float.NaN : (float)momTimeField.GetValue(p); }

        static void RestoreMomTimer()
        {
            if (float.IsNaN(momSaved)) return;
            var p = Prompts();
            if (p != null) momTimeField.SetValue(p, momSaved);
            momSaved = float.NaN;
        }

        public static float MomTimerForTest { get { var p = Prompts(); return p == null ? float.NaN : (float)momTimeField.GetValue(p); } }

        public static bool Busy { get { return activeName != null || pending.Count > 0; } }

        static void ToOverlay(string text) { OverlayUsed++; CoopUI.OverlayToast(text); }

        static FieldInfo currentField, queuedField;
        static List<AbilityHUD.HUDMessage> HudList(bool current)
        {
            if (!Singleton<AbilityHUD>.Exists) return null;
            if (currentField == null)
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                currentField = typeof(AbilityHUD).GetField("m_currentHUDs", flags);
                queuedField = typeof(AbilityHUD).GetField("m_HUDMessages", flags);
            }
            var f = current ? currentField : queuedField;
            return f == null ? null : f.GetValue(Singleton<AbilityHUD>.Instance) as List<AbilityHUD.HUDMessage>;
        }

        static bool InList(List<AbilityHUD.HUDMessage> list, string name)
        {
            if (list == null) return false;
            foreach (var m in list) if (m != null && m.m_name == name) return true;
            return false;
        }

        public static bool IsCurrent(string name) { return InList(HudList(true), name); }
        public static bool IsQueued(string name) { return InList(HudList(false), name); }
        public static string ActiveName { get { return activeName; } }

        // Every frame (Bootstrap.Update).
        public static void Tick()
        {
            try { DumpTick(); } catch (Exception e) { CoopBehaviour.Log("ui: menu dump failed: " + SharedWorld.Short(e)); dumpedFor = Singleton<GUIManager>.Exists ? Singleton<GUIManager>.Instance : null; }
            try { MessageTick(); }
            catch (Exception e)
            {
                CoopBehaviour.Log("ui: HUD message failed, using the overlay: " + SharedWorld.Short(e));
                if (activeText != null && currentSince < 0f) ToOverlay(activeText);
                activeName = null; activeText = null;
                foreach (var p in pending) ToOverlay(p.Text);
                pending.Clear();
            }
        }

        static void MessageTick()
        {
            float now = Time.realtimeSinceStartup;
            if (activeName != null)
            {
                if (!Singleton<AbilityHUD>.Exists) { if (currentSince < 0f) ToOverlay(activeText); activeName = null; }      // level unloaded under it
                else if (IsCurrent(activeName))
                {
                    if (currentSince < 0f) { currentSince = now; RestoreMomTimer(); }     // it may have reached the panel only now, from the HUD's queue
                    if (pending.Count > 0 && now - currentSince > ShortenTo) { AbilityHUD.HideHUDMessage(activeName); activeName = null; }
                }
                else if (IsQueued(activeName))
                {
                    // never shown yet and the panel stays busy, or pushed off by a more important game message: let the game have it
                    // the panel is taken: this one and everything waiting go to the overlay now, instead of each waiting its turn behind the same game message
                    if (currentSince < 0f && now - activeSince > WaitLimit)
                    {
                        AbilityHUD.HideHUDMessage(activeName); ToOverlay(activeText); activeName = null;
                        foreach (var p in pending) ToOverlay(p.Text);
                        pending.Clear();
                    }
                    else if (currentSince >= 0f) { AbilityHUD.HideHUDMessage(activeName); activeName = null; }
                }
                else
                {
                    if (currentSince < 0f) ToOverlay(activeText);     // gone without ever showing
                    activeName = null;                                 // shown and timed out
                }
            }
            foreach (var p in pending) if (now - p.Born > StaleAfter) ToOverlay(p.Text);      // late, but never lost
            pending.RemoveAll(p => now - p.Born > StaleAfter);
            if (activeName != null || pending.Count == 0) return;
            if (!CanShow()) { foreach (var p in pending) ToOverlay(p.Text); pending.Clear(); return; }

            var next = pending[0]; pending.RemoveAt(0);
            var msg = new AbilityHUD.HUDMessage(MsgPrefix + (++serial), false, Priority, AbilityHUD.HUDFlags.SideHint, ShowSeconds);
            string mom = Localisation.GetString("$MOM");
            msg.m_sideAlertText = string.Empty;
            msg.m_stringTop = string.IsNullOrEmpty(mom) || mom == "$MOM" ? "M.O.M" : mom;
            msg.m_stringBottom = next.Text;
            msg.m_sprite = null;
            msg.m_hudFlags = AbilityHUD.HUDFlags.SideHint;
            activeName = msg.m_name; activeText = next.Text; activeSince = now; currentSince = -1f;
            SaveMomTimer();
            AbilityHUD.ShowHUDMessage(msg);
            if (IsCurrent(activeName)) { currentSince = now; RestoreMomTimer(); }
            NativeShown++; LastNativeText = next.Text;
        }

        // For the audit: drop everything in flight without showing it anywhere.
        public static void Reset()
        {
            if (activeName != null && Singleton<AbilityHUD>.Exists) { try { AbilityHUD.HideHUDMessage(activeName); } catch (Exception) { } }
            activeName = null; activeText = null; pending.Clear(); momSaved = float.NaN;
        }

        // ---- 1. menu and HUD dump -----------------------------------------------------------------------------------------------------

        static GUIManager dumpedFor;
        public static int Dumps;
        public static string DumpPath { get { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "coopmod-ui.log"); } }

        static void DumpTick()
        {
            if (!Singleton<GUIManager>.Exists) return;
            var gm = Singleton<GUIManager>.Instance;
            if (gm == dumpedFor || gm.m_Menus == null || gm.m_Menus.Count == 0) return;
            dumpedFor = gm;
            var sb = new StringBuilder();
            sb.AppendLine("==== game UI in level " + Application.loadedLevel + " " + Application.loadedLevelName + " (" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ", language " + PlayerProfile.Language + ")");
            sb.AppendLine("GUIManager " + PathOf(gm.transform) + ": " + gm.m_Menus.Count + " menus");
            foreach (var m in gm.m_Menus)
            {
                if (m == null) { sb.AppendLine("menu (missing)"); continue; }
                var h = m.GetComponent<GUIHandler>();
                var um = m as UnityGUIMenu;
                sb.AppendLine("menu " + m.name + "  type " + m.GetType().Name + "  handler " + (h == null ? "none" : h.GetType().Name) + "  setCamera " + m.m_SetCameraPosition + "  glitch " + m.m_glitchOnOpen + "  locations " + m.m_Locations.Count + "  filter " + m.m_FilterWhenActive
                    + (um != null ? "  default " + (um.m_defaultSelection == null ? "none" : um.m_defaultSelection.name) : ""));
            }
            foreach (var m in gm.m_Menus) if (m != null) { sb.AppendLine("---- menu " + m.name); Tree(sb, m.transform, 0, 400); }
            if (Singleton<AbilityHUD>.Exists)
            {
                var hud = Singleton<AbilityHUD>.Instance;
                sb.AppendLine("---- hud " + PathOf(hud.transform) + "  canvas " + (hud.m_canvas == null ? "none" : PathOf(hud.m_canvas.transform)) + "  sidePanel " + (hud.m_sideHintPanel == null ? "none" : PathOf(hud.m_sideHintPanel)) + "  controllerPanel " + (hud.m_controllerPanel == null ? "none" : PathOf(hud.m_controllerPanel)));
                var mom = HintTextDatabase.GetMessageByName("MOMPrompt");
                sb.AppendLine("hud database: MOMPrompt priority " + (mom == null ? "missing" : mom.m_priority.ToString()) + ";  $MOM = \"" + Localisation.GetString("$MOM") + "\"");
                if (hud.m_canvas != null) Tree(sb, hud.m_canvas.transform, 0, 400);
            }
            else sb.AppendLine("---- hud: none in this level");
            // a fresh file each game start (only this session's levels), and never more than about 2 MB
            bool fresh = Dumps == 0 || !File.Exists(DumpPath) || new FileInfo(DumpPath).Length > 2000000;
            if (fresh) File.WriteAllText(DumpPath, sb.ToString()); else File.AppendAllText(DumpPath, sb.ToString());
            Dumps++;
            CoopBehaviour.Log("ui: wrote the game's " + gm.m_Menus.Count + " menus and HUD to coopmod-ui.log");
        }

        public static string PathOf(Transform t)
        {
            string p = t.name;
            for (var x = t.parent; x != null; x = x.parent) p = x.name + "/" + p;
            return p;
        }

        static void Tree(StringBuilder sb, Transform t, int depth, int budget)
        {
            int left = budget;
            TreeInner(sb, t, depth, ref left);
            if (left <= 0) sb.AppendLine(new string(' ', depth * 2) + "... (cut off)");
        }

        static void TreeInner(StringBuilder sb, Transform t, int depth, ref int left)
        {
            if (left-- <= 0) return;
            string pad = new string(' ', depth * 2);
            var line = new StringBuilder(pad + t.name + (t.gameObject.activeSelf ? "" : " [off]") + " layer " + LayerMask.LayerToName(t.gameObject.layer));
            var rt = t as RectTransform;
            if (rt != null) line.Append(" rect " + rt.anchoredPosition + " " + rt.sizeDelta);
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null || c is Transform) continue;
                line.Append(" | " + c.GetType().Name);
                var text = c as Text;
                if (text != null) line.Append(" \"" + text.text.Replace("\n", "\\n") + "\" font " + (text.font == null ? "none" : text.font.name) + " " + text.fontSize);
                var canvas = c as Canvas;
                if (canvas != null) line.Append(" " + canvas.renderMode + " camera " + (canvas.worldCamera == null ? "none" : canvas.worldCamera.name) + " order " + canvas.sortingOrder);
                var sel = c as Selectable;
                if (sel != null) line.Append(" nav " + sel.navigation.mode + " interactable " + sel.interactable);
                var button = c as Button;
                if (button != null)
                {
                    int n = button.onClick.GetPersistentEventCount();
                    for (int i = 0; i < n; i++)
                    {
                        var target = button.onClick.GetPersistentTarget(i);
                        line.Append(" onClick " + (target == null ? "none" : target.GetType().Name + "(" + target.name + ")") + "." + button.onClick.GetPersistentMethodName(i));
                    }
                }
            }
            sb.AppendLine(line.ToString());
            for (int i = 0; i < t.childCount; i++) TreeInner(sb, t.GetChild(i), depth + 1, ref left);
        }
    }
}
