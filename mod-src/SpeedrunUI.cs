using System;
using System.Collections.Generic;
using UnityEngine;

namespace CoopMod
{
    // The speedrun timer on the mod's HUD canvas (CoopHud, uGUI in the game's font and panel art; no IMGUI): a panel at the top left of the game's HUD (above its altitude panel; the race strip sits in the gap of the M.O.M column) with the category, VALID / INVALID, the splits
    // around the current one with their +/- against the PB, the big timer and PB / sum of best; a results card when a run finishes.
    // F1 (the co-op display) does not hide it; the menu's TIMER: SHOW/HIDE does. Under a game menu the panel, strip and cards wait (the game hides
    // its own HUD there too, and the SPEEDRUN page shows the time); the race countdown stays, the start-screen race needs it.
    // The layout functions (PanelRect, ResultsRect, StripRect, RaceResultsRect) are pure and in screen pixels, as before.
    public static class SpeedrunUI
    {
        public static readonly Color Ahead = new Color(0.45f, 1f, 0.55f, 1f);
        public static readonly Color Behind = new Color(1f, 0.38f, 0.38f, 1f);
        public static readonly Color GoldC = new Color(1f, 0.84f, 0.3f, 1f);
        public static readonly Color Grey = new Color(0.66f, 0.75f, 0.8f, 0.6f);
        public const int MaxRowsShown = 8;

        public static int DrawnFrames;
        public static string LastProblem = "";

        // Where the panel goes: the top left of the game's HUD, above its altitude panel (CoopHud.ZoneLeft), for any resolution.
        // Pure, so the audit checks 800x450, 1280x720 and 4K. RowsThatFit: the splits shown so the panel stays in that zone.
        public static Rect PanelRect(float sw, float sh, float s, int rows)
        {
            var z = CoopHud.ZoneLeft(sw, sh);
            float w = Mathf.Min(300f * s, z.width);
            float h = Mathf.Min((40f + 18f * Math.Min(rows, RowsThatFit(sw, sh, s)) + 66f) * s, z.height);
            return new Rect(z.x, z.y, w, h);
        }
        public static int RowsThatFit(float sw, float sh, float s) { return Math.Max(0, (int)((CoopHud.ZoneLeft(sw, sh).height / s - 106f) / 18f)); }

        public static Rect ResultsRect(float sw, float sh, float s)
        {
            float w = Mathf.Min(420f * s, sw - 32f * s), h = Mathf.Min(150f * s, sh - 32f * s);
            return new Rect((sw - w) / 2f, sh * 0.30f, w, h);
        }

        // the splits in the order shown: ordered as defined; 100% (unordered) as reached, then the ones still to do
        public static List<int> DisplayOrder(SplitEngine e)
        {
            var l = new List<int>();
            if (!e.Unordered) { for (int i = 0; i < e.Count; i++) l.Add(i); return l; }
            l.AddRange(e.Order);
            for (int i = 0; i < e.Count; i++) if (!l.Contains(i)) l.Add(i);
            return l;
        }

        // the window of at most 8 rows around the current split
        public static void Window(SplitEngine e, out int first, out int count)
        {
            count = Math.Min(MaxRowsShown, e.Count);
            int cur = e.Unordered ? e.Order.Count : e.Current;
            first = Math.Max(0, Math.Min(cur - 3, e.Count - count));
        }

        public static void Draw(HudLayer L)
        {
            if (!L.Begin(true)) return;
            try
            {
                if (Preview != null) { DrawPreview(L); return; }
                var node = SpeedrunNet.Node;
                float now = Time.realtimeSinceStartup;
                float s = L.S;
                bool menu = Singleton<GUIManager>.Exists && Singleton<GUIManager>.Instance.AnyMenuActive;
                var run = Speedrun.Current;
                // a co-op member shows the leader's run (there is no run of my own)
                if ((run == null || (!Speedrun.SlotActive && !run.Live)) && node != null && node.CoopMember && node.Mirror != null) run = node.Mirror;
                float bottom = 16f * s;
                if (run != null && !Speedrun.TimerHidden)
                {
                    bool mirror = node != null && run == node.Mirror;
                    bool recent = run.FinishedAtRealtime > 0f && now - run.FinishedAtRealtime < 20f;
                    if (Speedrun.SlotActive || run.Live || recent || mirror)
                    {
                        if (!menu)
                        {
                            bottom = DrawFor(L, run, Speedrun.SplitsShown);
                            if (mirror && node.HostLost) { var pr = PanelRect(L.Sw, L.Sh, s, 0); L.Label(new Rect(pr.x, bottom - 20f * s, pr.width - 12f * s, 18f * s), HostLostText, Behind, 12, TextAnchor.MiddleRight); }
                        }
                        bool raceCard = node != null && node.ShowResults(now);
                        if (run.State == SrState.Finished && recent && !menu && !raceCard) DrawResults(L, run);
                    }
                }
                if (node != null)
                {
                    var race = node.Race;
                    if (race.Racers.Count > 1 && (race.Ph == RaceFsm.Phase.Counting || race.Ph == RaceFsm.Phase.Running || (race.Ph == RaceFsm.Phase.Over && now - race.OverAt < 20f)) && !menu) DrawRaceStrip(L, race, bottom, now);
                    if (race.Ph == RaceFsm.Phase.Counting || (race.Ph == RaceFsm.Phase.Running && race.GoAt > 0f && now - race.GoAt < 1.2f)) DrawCountdown(L, race.GoAt - now);
                    if (node.ShowResults(now) && !menu) DrawRaceResults(L, race);
                }
            }
            catch (Exception e) { if (LastProblem.Length == 0) CoopBehaviour.Log("speedrun: timer display " + e.GetType().Name + " " + e.Message); LastProblem = SharedWorld.Short(e); }
            finally { L.End(); }
        }

        // the race strip: one line per racer, live, in the gap of the game's M.O.M column (CoopHud.ZoneRight); `top` is kept for callers
        public static Rect StripRect(float sw, float sh, float s, float top, int racers)
        {
            var z = CoopHud.ZoneRight(sw, sh);
            float w = Mathf.Min(300f * s, z.width);
            float h = Mathf.Min((10f + 18f * Math.Min(racers, RacersThatFit(sw, sh, s))) * s, z.height);
            return new Rect(z.x, z.y, w, h);
        }
        public static int RacersThatFit(float sw, float sh, float s) { return Math.Max(1, (int)((CoopHud.ZoneRight(sw, sh).height / s - 10f) / 18f)); }

        public static string RacerLine(RaceFsm race, SrRacer r, int place, float now)
        {
            string name = Party.Short(string.IsNullOrEmpty(r.Name) ? (r.Me ? "YOU" : "PLAYER") : r.Name, 10);
            string where;
            if (!r.Started) where = race.Ph == RaceFsm.Phase.Counting ? "READY" : "WAITING";
            else if (r.State == SrState.Finished) where = "FIN";
            else if (r.State == SrState.Reset) where = "RESET";
            else if (r.State == SrState.Dnf) where = r.Note.Length > 0 ? r.Note : "DNF";
            else if (r.Offline) where = "OFFLINE";
            else if (r.SplitIdx != SrMsg.NoSplit && !race.Spectating)
            {
                var defs = SrCategories.Splits(race.Cat, race.Seeds);
                where = r.SplitIdx < defs.Count ? defs[r.SplitIdx].Label : "SPLIT " + (r.SplitIdx + 1);
            }
            else where = r.SplitIdx != SrMsg.NoSplit ? "SPLIT " + (r.SplitIdx + 1) : "START";
            return place + ". " + name + "  " + where + "  " + SpeedTimer.Format(r.LiveMs(now));
        }

        public const string HostLostText = "HOST LOST";

        public static float DrawRaceStrip(HudLayer L, RaceFsm race, float top, float now)
        {
            float s = L.S;
            var ranked = race.Ranked();
            int n = Math.Min(ranked.Count, Math.Min(8, RacersThatFit(L.Sw, L.Sh, s)));
            var r = StripRect(L.Sw, L.Sh, s, top, n);
            L.Panel(r);
            var places = RaceFsm.Places(ranked);
            float x = r.x + 12f * s, w = r.width - 24f * s, y = r.y + 5f * s;
            for (int i = 0; i < n; i++)
            {
                var q = ranked[i];
                Color c = q.Me ? CoopUI.White : q.State == SrState.Finished ? GoldC : (q.Offline || !q.Running) ? Grey : CoopUI.Cyan;
                L.Label(new Rect(x, y, w, 18f * s), L.Cut(RacerLine(race, q, places[i], now), 12, w), c, 12, TextAnchor.MiddleLeft);
                y += 18f * s;
            }
            return r.yMax;
        }

        public static string CountdownText(float left) { return left > 0f ? "RACE IN " + Mathf.CeilToInt(left) : "GO"; }

        // in the middle of the screen; over a game menu (it stays up there) in the free part right of the menu's title and lines
        public static void DrawCountdown(HudLayer L, float left)
        {
            float s = L.S, x0 = 0f, x1 = L.Sw;
            float edge = MenuLinesRight(L);
            if (edge > 0f && edge < L.Sw * 0.8f) x0 = edge + 8f * s;
            L.Label(new Rect(x0, L.Sh * 0.38f, x1 - x0, 80f * s), CountdownText(left), left > 0f ? CoopUI.White : Ahead, 46, TextAnchor.MiddleCenter);
        }

        // the right edge (layer pixels) of the open game menu's title and lines, or -1 with no menu up
        static readonly Vector3[] menuCorners = new Vector3[4];
        static float MenuLinesRight(HudLayer L)
        {
            if (!Singleton<GUIManager>.Exists || !Singleton<GUIManager>.Instance.AnyMenuActive) return -1f;
            var m = CoopMenu.ActiveGameMenu();
            if (m == null) return -1f;
            var c = m.GetComponent<Canvas>(); var cam = c == null ? null : c.worldCamera;
            float right = -1f;
            foreach (string part in new[] { "AspectKeeper/MenuGroupPanel", "AspectKeeper/TitlePanel" })
            {
                var t = m.transform.Find(part) as RectTransform;
                if (t == null || !t.gameObject.activeInHierarchy) continue;
                var r = CoopHud.ScreenRect(t, menuCorners, cam);
                right = Mathf.Max(right, r.xMax * (L.Sw / Mathf.Max(1f, Screen.width)));
            }
            return right;
        }

        public static Rect RaceResultsRect(float sw, float sh, float s, int racers)
        {
            float w = Mathf.Min(460f * s, sw - 32f * s), h = Mathf.Min((54f + 22f * racers) * s, sh - 32f * s);
            return new Rect((sw - w) / 2f, Mathf.Max(16f * s, Mathf.Min(sh * 0.28f, sh - h - 16f * s)), w, h);
        }

        public static void DrawRaceResults(HudLayer L, RaceFsm race)
        {
            float s = L.S;
            var ranked = race.Ranked();
            int n = Math.Min(ranked.Count, 8);
            var r = RaceResultsRect(L.Sw, L.Sh, s, n);
            if (race.OverAt != cardFor) { cardFor = race.OverAt; CoopHud.Appear(); }
            L.Panel(r, 1f, CoopUI.GlitchFor(race.OverAt));
            float x = r.x + 16f * s, w = r.width - 32f * s, y = r.y + 10f * s;
            L.Label(new Rect(x, y, w, 24f * s), "RACE RESULTS  " + SrCategories.Label(race.Cat), CoopUI.White, 16, TextAnchor.MiddleCenter);
            y += 34f * s;
            var places = RaceFsm.Places(ranked);
            long best = ranked.Count > 0 && ranked[0].State == SrState.Finished ? ranked[0].ElapsedMs : -1;
            for (int i = 0; i < n; i++)
            {
                var q = ranked[i];
                string name = Party.Short(string.IsNullOrEmpty(q.Name) ? (q.Me ? "YOU" : "PLAYER") : q.Name, 14) + (q.Me ? " (YOU)" : "");
                bool timed = q.State == SrState.Finished;
                string t = timed ? SpeedTimer.Format(q.ElapsedMs) + (best >= 0 && i > 0 ? "  " + SpeedTimer.Delta(q.ElapsedMs - best) : "") + (q.Valid ? "" : "  INVALID")
                         : q.State == SrState.Reset ? "RESET" : q.Offline ? "OFFLINE" : q.Running ? "STILL RUNNING" : "DNF";
                Color c = places[i] == 1 && q.State == SrState.Finished ? GoldC : q.Me ? CoopUI.White : CoopUI.Cyan;
                L.Label(new Rect(x, y, w, 20f * s), places[i] + ". " + name, c, 14, TextAnchor.MiddleLeft);
                L.Label(new Rect(x, y, w, 20f * s), t, c, 14, TextAnchor.MiddleRight, timed);
                y += 22f * s;
            }
        }
        static float cardFor = -1f, resultsFor = -1f;

        static Color TimerColour(SrRun run, long ms)
        {
            if (run.State == SrState.Finished) return run.NewPb ? GoldC : CoopUI.White;
            if (run.State == SrState.CannotFinish || !run.Valid) return Behind;
            if (!run.Live) return Grey;
            long d = run.Engine.LiveDelta(ms);
            return d == long.MinValue ? CoopUI.White : d <= 0 ? Ahead : Behind;
        }

        public static float DrawFor(HudLayer L, SrRun run, bool splits)
        {
            float s = L.S;
            var e = run.Engine;
            int first, count;
            Window(e, out first, out count);
            if (!splits) count = 0;
            int fit = RowsThatFit(L.Sw, L.Sh, s);
            if (count > fit) { int cur = e.Unordered ? e.Order.Count : e.Current; first = Math.Max(0, Math.Min(cur - fit / 2, e.Count - fit)); count = fit; }     // a small window: fewer splits around the current one
            var r = PanelRect(L.Sw, L.Sh, s, count);
            L.Panel(r);
            float x = r.x + 12f * s, w = r.width - 24f * s, y = r.y + 8f * s;
            string head = SrCategories.Label(run.Category) + "  " + SrCategories.ModeLabel(run.Mode) + (run.Mode == SrMode.Coop ? " (UNOFFICIAL)" : "");
            string valid = run.Valid ? "VALID" : "INVALID";
            L.Label(new Rect(x, y, w, 20f * s), L.Cut(head, 13, w - L.Width(valid, 13) - 8f * s), CoopUI.White, 13, TextAnchor.MiddleLeft);
            L.Label(new Rect(x, y, w, 20f * s), valid, run.Valid ? Ahead : Behind, 13, TextAnchor.MiddleRight);
            float timeW = Mathf.Min(86f * s, w * 0.36f);          // the time column; the delta sits left of it
            y += 22f * s;
            L.Fill(new Rect(x, y, w, Mathf.Max(1f, s)), CoopUI.CyanDim);          // the rule under the heading
            y += 6f * s;
            var order = DisplayOrder(e);
            for (int k = first; k < first + count && k < order.Count; k++)
            {
                int i = order[k];
                var st = e.States[i];
                bool next = !e.Unordered ? i == e.Current : k == e.Order.Count;
                Color lc = st == SrSplitState.Skipped ? Grey : next && run.Live ? CoopUI.White : CoopUI.Cyan;
                L.Label(new Rect(x, y, w - 2f * timeW, 18f * s), L.Cut((next && run.Live ? "> " : "") + e.Defs[i].Label, 12, w - 2f * timeW - 2f * s), lc, 12, TextAnchor.MiddleLeft);
                string t = st == SrSplitState.Done ? SpeedTimer.Format(e.AtMs[i]) : st == SrSplitState.Skipped ? "-" : e.PbAt[i] >= 0 ? SpeedTimer.Format(e.PbAt[i]) : "-";
                L.Label(new Rect(x, y, w, 18f * s), t, st == SrSplitState.Done ? CoopUI.Cyan : Grey, 12, TextAnchor.MiddleRight, true);
                long d = e.DeltaMs(i);
                if (d != long.MinValue)
                    L.Label(new Rect(x, y, w - timeW, 18f * s), SpeedTimer.Delta(d), e.Gold(i) ? GoldC : d <= 0 ? Ahead : Behind, 12, TextAnchor.MiddleRight, true);
                y += 18f * s;
            }
            long ms = run.FinalMs;
            L.Label(new Rect(x, y + 2f * s, w, 36f * s), SpeedTimer.Format(ms), TimerColour(run, ms), 28, TextAnchor.MiddleRight, true);
            string state = run.State == SrState.CannotFinish ? "CANNOT FINISH" : run.State == SrState.Running ? "" : run.State == SrState.Finished ? (run.NewPb ? "NEW PB" : "FINISHED") : run.State.ToString().ToUpper();
            if (state.Length > 0) L.Label(new Rect(x, y + 2f * s, w, 36f * s), state, run.State == SrState.Finished ? GoldC : Behind, 12, TextAnchor.MiddleLeft);
            y += 40f * s;
            var rec = Speedrun.RecordFor(run.Category, run.Mode);
            long sob = e.SumOfBest();
            string foot = "PB " + (rec.PbMs >= 0 ? SpeedTimer.Format(rec.PbMs) : "-") + "   SUM OF BEST " + (sob >= 0 ? SpeedTimer.Format(sob) : "-");
            L.Label(new Rect(x, y, w, 18f * s), L.Cut(foot, 11, w), CoopUI.CyanDim, 11, TextAnchor.MiddleLeft);
            DrawnFrames++;
            return r.yMax;
        }

        public static void DrawResults(HudLayer L, SrRun run)
        {
            float s = L.S;
            var r = ResultsRect(L.Sw, L.Sh, s);
            if (run.FinishedAtRealtime != resultsFor) { resultsFor = run.FinishedAtRealtime; CoopHud.Appear(); }
            L.Panel(r, 1f, CoopUI.GlitchFor(run.FinishedAtRealtime));
            float x = r.x + 16f * s, w = r.width - 32f * s;
            L.Label(new Rect(x, r.y + 10f * s, w, 24f * s), SrCategories.Label(run.Category) + (run.NewPb ? "  NEW PERSONAL BEST" : "  RUN FINISHED"), run.NewPb ? GoldC : CoopUI.White, 16, TextAnchor.MiddleCenter);
            L.Label(new Rect(x, r.y + 40f * s, w, 50f * s), SpeedTimer.Format(run.Engine.EndMs), run.NewPb ? GoldC : CoopUI.Cyan, 34, TextAnchor.MiddleCenter, true);
            var rec = Speedrun.RecordFor(run.Category, run.Mode);
            string pb = run.NewPb ? "SAVED AS YOUR PB" : rec.PbMs >= 0 ? "PB " + SpeedTimer.Format(rec.PbMs) + "  (" + SpeedTimer.Delta(run.Engine.EndMs - rec.PbMs) + ")" : "";
            if (!run.Valid) pb = "INVALID: " + run.Invalid[0];
            L.Label(new Rect(x, r.y + 100f * s, w, 22f * s), pb, run.Valid ? CoopUI.Cyan : Behind, 13, TextAnchor.MiddleCenter);
        }

        // ---- the audit's preview: sample runs and a race laid out on the real HUD for a few frames, instead of the live timer ----
        public static List<SrRun> Preview;
        public static RaceFsm PreviewRace;
        public static int PreviewFrames;
        public static string PreviewProblem = "";

        public static void PreviewForTest(List<SrRun> runs, RaceFsm race) { PreviewOnly = null; PreviewFrames = 0; PreviewProblem = ""; PreviewRace = race; Preview = runs; }
        public static void StopPreview() { Preview = null; PreviewRace = null; PreviewOnly = null; }

        // The layout audit (HUD-LAYOUT) lays the pieces out one group at a time, where play puts them:
        // "timer" = the timer panel, "strip" = the race strip, "countdown", "cards" = the results cards. Null: everything (SR-UI).
        public static string PreviewOnly;
        public static void PreviewPartForTest(List<SrRun> runs, RaceFsm race, string part) { PreviewForTest(runs, race); PreviewOnly = part; }

        static void DrawPreview(HudLayer L)
        {
            if (PreviewOnly != null) { DrawPreviewPart(L); return; }
            try
            {
                foreach (var r in Preview) { DrawFor(L, r, true); if (r.State == SrState.Finished) DrawResults(L, r); }
                if (PreviewRace != null)
                {
                    float b = DrawRaceStrip(L, PreviewRace, 200f, Time.realtimeSinceStartup);
                    DrawCountdown(L, 2.4f); DrawCountdown(L, -0.2f);
                    DrawRaceResults(L, PreviewRace);
                    if (b <= 0f) PreviewProblem = "strip";
                }
                PreviewFrames++;
            }
            catch (Exception e) { PreviewProblem = SharedWorld.Short(e); }
        }

        static void DrawPreviewPart(HudLayer L)
        {
            try
            {
                float now = Time.realtimeSinceStartup, bottom = 16f * L.S;
                if (PreviewOnly == "timer") foreach (var r in Preview) bottom = DrawFor(L, r, true);
                else if (PreviewOnly == "strip") { if (PreviewRace != null) DrawRaceStrip(L, PreviewRace, bottom, now); }
                else if (PreviewOnly == "countdown") DrawCountdown(L, 2.4f);
                else if (PreviewOnly == "cards")
                {
                    foreach (var r in Preview) if (r.State == SrState.Finished) DrawResults(L, r);
                    if (PreviewRace != null) DrawRaceResults(L, PreviewRace);
                }
                PreviewFrames++;
            }
            catch (Exception e) { PreviewProblem = SharedWorld.Short(e); }
        }
    }
}
