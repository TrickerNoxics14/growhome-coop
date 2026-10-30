using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace CoopMod
{
    // The game hides Save & Quit (and refuses to close) while its "block quitting" counter is above 0: a growing trunk, a bud, a respawn, a cutscene
    // or a teleport raise it and lower it again when done. A routine cut off half way (a plant part removed while growing, say) never lowers it,
    // and then Save & Quit is gone for the rest of the session. This guard logs both counters, and when nothing that really blocks is going on it
    // lowers them through the game's own Unblock calls, so Save & Quit shows and works again.
    public static class QuitGuard
    {
        static readonly FieldInfo quitField = typeof(PlayerProfile).GetField("m_blockApplicationQuit", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo pauseField = typeof(PlayerProfile).GetField("m_blockPauseMenu", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo attemptField = typeof(PlantBranch).GetField("m_AttemptingGrowth", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo teleportField = typeof(BudTeleporter).GetField("m_teleportState", BindingFlags.NonPublic | BindingFlags.Instance);

        public const float CalmInPause = 0.6f;
        public static int TestReasons;              // the audit pretends something really blocks
        public static int Repairs, Released;         // counters lowered by the guard / blocks given back for removed plant parts
        public static string LastRepair = "";
        public static bool LastRepairOpen;              // the pause menu was open when the last repair was made (Save & Quit was turned back on by the guard then)
        static int lastQuit, lastPause;
        static bool wasOpen; static float nextCheck, calmSince = -1f;
        static GUIManager seenGm; static GUIMenu pauseMenu; static PauseMenuHandler pauseHandler;

        public static int QuitBlock() { return Singleton<PlayerProfile>.Exists && quitField != null ? (int)quitField.GetValue(Singleton<PlayerProfile>.Instance) : 0; }
        public static int PauseBlock() { return Singleton<PlayerProfile>.Exists && pauseField != null ? (int)pauseField.GetValue(Singleton<PlayerProfile>.Instance) : 0; }

        static bool Attempting(PlantBranch b) { return attemptField != null && (bool)attemptField.GetValue(b); }

        // ---- plant parts being destroyed this frame -------------------------------------------------------------------------------
        // Unity's Destroy waits for the end of the frame, so a branch the mod (or the game) is removing is still found by FindObjectsOfType in that
        // frame and would count as "really blocking". Its instance id goes into this set for the frame (ids only: the guard never keeps a
        // reference to a branch), and the set is emptied when the next frame starts.
        static readonly HashSet<int> doomedIds = new HashSet<int>();
        static int doomedFrame = -1;
        static void DoomedFrame() { int f = Time.frameCount; if (f != doomedFrame) { doomedIds.Clear(); doomedFrame = f; } }
        public static void Doom(PlantBranch b) { if (b == null) return; DoomedFrame(); doomedIds.Add(b.GetInstanceID()); }
        public static bool Doomed(PlantBranch b) { if (b == null) return true; DoomedFrame(); return doomedIds.Contains(b.GetInstanceID()); }
        public static int DoomedCount { get { DoomedFrame(); return doomedIds.Count; } }

        // ---- the counters in the log, about once a minute ---------------------------------------------------------------------
        public const float StatusEvery = 60f;
        public static int StatusLogs; public static float FirstStatusAt = -1f, StatusMinGap = float.MaxValue;
        static float nextStatus = -1f, lastStatusAt = -1f;

        static void StatusTick(float now, int q, int p)
        {
            if (nextStatus >= 0f && now < nextStatus) return;
            nextStatus = now + StatusEvery;
            if (lastStatusAt >= 0f) StatusMinGap = Mathf.Min(StatusMinGap, now - lastStatusAt);
            if (FirstStatusAt < 0f) FirstStatusAt = now;
            lastStatusAt = now; StatusLogs++;
            string held = q + p > 0 ? Reasons() : null;
            CoopBehaviour.Log("quit guard: counters quit=" + q + " pause=" + p + " repairs=" + Repairs + " given back=" + Released + " doomed now=" + DoomedCount
                + (q + p == 0 ? " (nothing blocks Save & Quit)" : held != null ? " (held by " + held + ")" : " (nothing really blocks: lowered once it stays calm)"));
        }

        // the game raised the counter for this branch and has not lowered it yet (trunk growth, or a triggered bud still growing)
        public static bool HoldsQuit(PlantBranch b) { return b != null && b.m_Growing && (b.IsTrunk() || b.m_BudTriggered); }

        // how many Save & Quit blocks growing plant parts hold right now (each one raised the counter once and lowers it when it stops growing)
        public static int PlantHolds()
        {
            int n = 0;
            foreach (PlantBranch b in UnityEngine.Object.FindObjectsOfType(typeof(PlantBranch))) if (!Doomed(b) && HoldsQuit(b)) n++;
            return n;
        }

        // Before the mod removes plant parts: a branch that still holds the counter can never lower it once it is gone, so it is lowered for it here.
        public static void ReleaseHeld(GameObject doomed)
        {
            if (doomed == null) return;
            ReleaseBranches(doomed.GetComponentsInChildren<PlantBranch>(true));
        }

        public static int ReleaseBranches(IEnumerable<PlantBranch> branches)
        {
            if (!Singleton<PlayerProfile>.Exists) return 0;
            int n = 0;
            foreach (var b in branches)
            {
                if (b == null || Doomed(b)) continue;        // already given back this frame (never twice)
                if (HoldsQuit(b)) { PlayerProfile.UnblockApplicationQuit(); b.m_Growing = false; n++; }
                Doom(b);
            }
            if (n > 0) { Released += n; CoopBehaviour.Log("quit guard: " + n + " growing plant parts removed; their block on Save & Quit is given back (now " + QuitBlock() + ")"); }
            return n;
        }

        // What really blocks quitting right now, or null when nothing does.
        public static string Reasons()
        {
            if (TestReasons > 0) return "a test reason";
            if (SaveSystem.loadingScene) return "a load";
            if (CutsceneController.s_ActiveCutscene != null) return "the cutscene " + CutsceneController.s_ActiveCutscene.name;
            var root = ExtendedMonoBehaviour.PlayerRoot;
            if (root != null && !root.activeSelf) return "a respawn";
            if (teleportField != null)
                foreach (BudTeleporter t in UnityEngine.Object.FindObjectsOfType(typeof(BudTeleporter)))
                    if (Convert.ToInt32(teleportField.GetValue(t)) != 0) return "the teleporter " + t.name;
            foreach (PlantBranch b in UnityEngine.Object.FindObjectsOfType(typeof(PlantBranch)))
                if (!Doomed(b) && (HoldsQuit(b) || Attempting(b))) return "the growing plant part " + b.name;
            return null;
        }

        static void FindPause()
        {
            if (!Singleton<GUIManager>.Exists) { pauseMenu = null; pauseHandler = null; return; }
            var gm = Singleton<GUIManager>.Instance;
            if (gm == seenGm && pauseMenu != null) return;
            seenGm = gm;
            pauseMenu = gm.m_Menus == null ? null : CoopMenu.Find(gm, "PauseMenu");
            pauseHandler = pauseMenu == null ? null : pauseMenu.GetComponent<PauseMenuHandler>();
        }

        public static bool PauseOpen { get { return pauseMenu != null && pauseMenu.gameObject.activeInHierarchy; } }
        public static GameObject QuitButton { get { return pauseHandler != null ? pauseHandler.m_QuitButton : null; } }

        // a Save & Quit (or the game's own reset) is on its way: its pause block is meant to stay
        static bool Leaving()
        {
            if (Singleton<FadeScreen>.Exists && FadeScreen.FadeInProgress) return true;
            if (Singleton<PauseManager>.Exists && PauseManager.ResetRequested) return true;
            return SaveSystem.loadingScene;
        }

        static string Rows()
        {
            var group = pauseMenu == null ? null : pauseMenu.transform.Find("AspectKeeper/MenuGroupPanel");
            if (group == null) return "?";
            var names = new List<string>();
            for (int i = 0; i < group.childCount; i++) if (group.GetChild(i).gameObject.activeSelf) names.Add(group.GetChild(i).name.Replace("Button", ""));
            return string.Join(",", names.ToArray());
        }

        public static void Tick(float now)
        {
            if (!Singleton<PlayerProfile>.Exists) return;
            FindPause();
            int q = QuitBlock(), p = PauseBlock();
            StatusTick(now, q, p);
            if (q != lastQuit || p != lastPause)
            {
                string why = q > lastQuit || p > lastPause ? Reasons() : null;
                CoopBehaviour.Log("quit guard: quit block " + lastQuit + " -> " + q + ", pause block " + lastPause + " -> " + p + (why != null ? " (" + why + ")" : ""));
                lastQuit = q; lastPause = p;
            }
            bool open = PauseOpen;
            if (open && !wasOpen)
            {
                nextCheck = now;
                var qb = QuitButton;
                CoopBehaviour.Log("pause menu: rows " + Rows() + "; save and quit " + (qb != null && qb.activeSelf ? "on" : "off") + "; quit block " + q + ", pause block " + p);
            }
            wasOpen = open;
            if (q == 0 && p == 0) { calmSince = -1f; ShowQuit(open); return; }
            if (now < nextCheck) return;
            nextCheck = now + (open ? 0.2f : 1f);
            string reason = Reasons();
            if (reason != null || Leaving())
            {
                calmSince = -1f;
                if (!open && reason != null) nextCheck = now + 3f;    // a real block (a growing trunk can hold one for minutes): the scene scan waits longer outside the menu
                return;
            }
            // the calm has to last (a routine between its block and unblock calls is never cut): a moment in the pause menu, 2 s outside it
            if (calmSince < 0f) { calmSince = now; return; }
            if (now - calmSince < (open ? CalmInPause : 2f)) return;
            bool inWorld = WorldSync.InWorld();
            int fixedQ = 0, fixedP = 0;
            while (QuitBlock() > 0 && fixedQ < 1000) { PlayerProfile.UnblockApplicationQuit(); fixedQ++; }
            if (inWorld) while (PauseBlock() > 0 && fixedP < 1000) { PlayerProfile.UnblockPauseMenu(); fixedP++; }
            if (fixedQ + fixedP > 0)
            {
                Repairs++;
                LastRepair = "quit block " + q + " -> " + QuitBlock() + ", pause block " + p + " -> " + PauseBlock(); LastRepairOpen = open;
                CoopBehaviour.Log("quit guard: nothing blocks quitting now, but the game's counters were left raised; " + LastRepair + (open ? " (pause menu open: Save & Quit is back)" : ""));
            }
            lastQuit = QuitBlock(); lastPause = PauseBlock();
            calmSince = -1f;
            ShowQuit(open);
        }

        // the pause menu opened while the counter was raised: the game hid Save & Quit; once the counter is 0 it comes back
        static void ShowQuit(bool open)
        {
            if (!open) return;
            var qb = QuitButton;
            if (qb != null && !qb.activeSelf && PlayerProfile.CanApplicationQuit) qb.SetActive(true);
        }
    }
}
