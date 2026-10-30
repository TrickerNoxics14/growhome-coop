using System;
using System.Collections.Generic;
using UnityEngine;

namespace CoopMod
{
    // The speedrun timer on screen (OnGUI, in the mod's HUD style): a panel at the top right with the category, VALID / INVALID, the splits
    // around the current one with their +/- against the PB, the big timer and PB / sum of best; a results card when a run finishes.
    // F1 (the co-op display) does not hide it; the menu's TIMER: SHOW/HIDE does.
    public static class SpeedrunUI
    {
        public static readonly Color Ahead = new Color(0.45f, 1f, 0.55f, 1f);
        public static readonly Color Behind = new Color(1f, 0.38f, 0.38f, 1f);
        public static readonly Color GoldC = new Color(1f, 0.84f, 0.3f, 1f);
        public static readonly Color Grey = new Color(0.66f, 0.75f, 0.8f, 0.6f);
        public const int MaxRowsShown = 8;

        public static int DrawnFrames;
        public static string LastProblem = "";

        // Where the panel goes: inside the screen at the top right, for any resolution. Pure, so the audit checks 1280x720 and 4K.
        public static Rect PanelRect(float sw, float sh, float s, int rows)
        {
            float w = Mathf.Min(300f * s, sw - 32f * s);
            float h = Mathf.Min((40f + 18f * rows + 66f) * s, sh - 32f * s);
            return new Rect(sw - w - 16f * s, 16f * s, w, h);
        }

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

        public static void Draw()
        {
            try
            {
                var node = SpeedrunNet.Node;
                float now = Time.realtimeSinceStartup;
                float s = CoopUI.ScaleFor(Screen.height);
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
                        bottom = DrawFor(run, Screen.width, Screen.height, Speedrun.SplitsShown);
                        if (mirror && node.HostLost) CoopUI.Text(new Rect(Screen.width - 316f * s, bottom, 300f * s, 18f * s), "HOST LOST", Behind, 12, TextAnchor.MiddleRight);
                        bool raceCard = node != null && node.ShowResults(now);
                        if (run.State == SrState.Finished && recent && !menu && !raceCard) DrawResults(run, Screen.width, Screen.height);
                    }
                }
                if (node != null)
                {
                    var race = node.Race;
                    if (race.Racers.Count > 1 && (race.Ph == RaceFsm.Phase.Counting || race.Ph == RaceFsm.Phase.Running || (race.Ph == RaceFsm.Phase.Over && now - race.OverAt < 20f))) DrawRaceStrip(race, Screen.width, Screen.height, bottom, now);
                    if (race.Ph == RaceFsm.Phase.Counting || (race.Ph == RaceFsm.Phase.Running && race.GoAt > 0f && now - race.GoAt < 1.2f)) DrawCountdown(race.GoAt - now, Screen.width, Screen.height);
                    if (node.ShowResults(now) && !menu) DrawRaceResults(race, Screen.width, Screen.height);
                }
            }
            catch (Exception e) { if (LastProblem.Length == 0) CoopBehaviour.Log("speedrun: timer display " + e.GetType().Name + " " + e.Message); LastProblem = SharedWorld.Short(e); }
        }

        // the race strip under the timer panel: one line per racer, live
        public static Rect StripRect(float sw, float sh, float s, float top, int racers)
        {
            float w = Mathf.Min(300f * s, sw - 32f * s);
            float h = Mathf.Min((10f + 18f * racers) * s, sh - 32f * s);
            float y = Mathf.Max(16f * s, Mathf.Min(top + 6f * s, sh - h - 16f * s));
            return new Rect(sw - w - 16f * s, y, w, h);
        }

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

        public static float DrawRaceStrip(RaceFsm race, float sw, float sh, float top, float now)
        {
            float s = CoopUI.ScaleFor(sh);
            var ranked = race.Ranked();
            int n = Math.Min(ranked.Count, 8);
            var r = StripRect(sw, sh, s, top, n);
            CoopUI.Frame(r);
            var places = RaceFsm.Places(ranked);
            float x = r.x + 12f * s, w = r.width - 24f * s, y = r.y + 5f * s;
            for (int i = 0; i < n; i++)
            {
                var q = ranked[i];
                Color c = q.Me ? CoopUI.White : q.State == SrState.Finished ? GoldC : (q.Offline || !q.Running) ? Grey : CoopUI.Cyan;
                CoopUI.Text(new Rect(x, y, w, 18f * s), RacerLine(race, q, places[i], now), c, 12, TextAnchor.MiddleLeft);
                y += 18f * s;
            }
            return r.yMax;
        }

        public static void DrawCountdown(float left, float sw, float sh)
        {
            float s = CoopUI.ScaleFor(sh);
            string t = left > 0f ? "RACE IN " + Mathf.CeilToInt(left) : "GO";
            CoopUI.Text(new Rect(0f, sh * 0.38f, sw, 80f * s), t, left > 0f ? CoopUI.White : Ahead, 46, TextAnchor.MiddleCenter);
        }

        public static Rect RaceResultsRect(float sw, float sh, float s, int racers)
        {
            float w = Mathf.Min(460f * s, sw - 32f * s), h = Mathf.Min((54f + 22f * racers) * s, sh - 32f * s);
            return new Rect((sw - w) / 2f, Mathf.Max(16f * s, Mathf.Min(sh * 0.28f, sh - h - 16f * s)), w, h);
        }

        public static void DrawRaceResults(RaceFsm race, float sw, float sh)
        {
            float s = CoopUI.ScaleFor(sh);
            var ranked = race.Ranked();
            int n = Math.Min(ranked.Count, 8);
            var r = RaceResultsRect(sw, sh, s, n);
            CoopUI.Frame(r, CoopUI.GlitchFor(race.OverAt));
            float x = r.x + 16f * s, w = r.width - 32f * s, y = r.y + 10f * s;
            CoopUI.Text(new Rect(x, y, w, 24f * s), "RACE RESULTS  " + SrCategories.Label(race.Cat), CoopUI.White, 16, TextAnchor.MiddleCenter);
            y += 34f * s;
            var places = RaceFsm.Places(ranked);
            long best = ranked.Count > 0 && ranked[0].State == SrState.Finished ? ranked[0].ElapsedMs : -1;
            for (int i = 0; i < n; i++)
            {
                var q = ranked[i];
                string name = Party.Short(string.IsNullOrEmpty(q.Name) ? (q.Me ? "YOU" : "PLAYER") : q.Name, 14) + (q.Me ? " (YOU)" : "");
                string t = q.State == SrState.Finished ? SpeedTimer.Format(q.ElapsedMs) + (best >= 0 && i > 0 ? "  " + SpeedTimer.Delta(q.ElapsedMs - best) : "") + (q.Valid ? "" : "  INVALID")
                         : q.State == SrState.Reset ? "RESET" : q.Offline ? "OFFLINE" : q.Running ? "STILL RUNNING" : "DNF";
                Color c = places[i] == 1 && q.State == SrState.Finished ? GoldC : q.Me ? CoopUI.White : CoopUI.Cyan;
                CoopUI.Text(new Rect(x, y, w, 20f * s), places[i] + ". " + name, c, 14, TextAnchor.MiddleLeft);
                CoopUI.Text(new Rect(x, y, w, 20f * s), t, c, 14, TextAnchor.MiddleRight);
                y += 22f * s;
            }
        }

        static Color TimerColour(SrRun run, long ms)
        {
            if (run.State == SrState.Finished) return run.NewPb ? GoldC : CoopUI.White;
            if (run.State == SrState.CannotFinish || !run.Valid) return Behind;
            if (!run.Live) return Grey;
            long d = run.Engine.LiveDelta(ms);
            return d == long.MinValue ? CoopUI.White : d <= 0 ? Ahead : Behind;
        }

        public static float DrawFor(SrRun run, float sw, float sh, bool splits)
        {
            float s = CoopUI.ScaleFor(sh);
            var e = run.Engine;
            int first, count;
            Window(e, out first, out count);
            if (!splits) count = 0;
            var r = PanelRect(sw, sh, s, count);
            CoopUI.Frame(r);
            float x = r.x + 12f * s, w = r.width - 24f * s, y = r.y + 8f * s;
            string head = SrCategories.Label(run.Category) + "  " + SrCategories.ModeLabel(run.Mode) + (run.Mode == SrMode.Coop ? " (UNOFFICIAL)" : "");
            CoopUI.Text(new Rect(x, y, w, 20f * s), head, CoopUI.White, 13, TextAnchor.MiddleLeft);
            CoopUI.Text(new Rect(x, y, w, 20f * s), run.Valid ? "VALID" : "INVALID", run.Valid ? Ahead : Behind, 13, TextAnchor.MiddleRight);
            y += 22f * s;
            CoopUI.DashedRule(x, y, w, CoopUI.CyanDim);
            y += 6f * s;
            var order = DisplayOrder(e);
            for (int k = first; k < first + count && k < order.Count; k++)
            {
                int i = order[k];
                var st = e.States[i];
                bool next = !e.Unordered ? i == e.Current : k == e.Order.Count;
                Color lc = st == SrSplitState.Skipped ? Grey : next && run.Live ? CoopUI.White : CoopUI.Cyan;
                CoopUI.Text(new Rect(x, y, w * 0.55f, 18f * s), (next && run.Live ? "> " : "") + e.Defs[i].Label, lc, 12, TextAnchor.MiddleLeft);
                string t = st == SrSplitState.Done ? SpeedTimer.Format(e.AtMs[i]) : st == SrSplitState.Skipped ? "-" : e.PbAt[i] >= 0 ? SpeedTimer.Format(e.PbAt[i]) : "-";
                CoopUI.Text(new Rect(x, y, w, 18f * s), t, st == SrSplitState.Done ? CoopUI.Cyan : Grey, 12, TextAnchor.MiddleRight);
                long d = e.DeltaMs(i);
                if (d != long.MinValue)
                    CoopUI.Text(new Rect(x, y, w - 86f * s, 18f * s), SpeedTimer.Delta(d), e.Gold(i) ? GoldC : d <= 0 ? Ahead : Behind, 12, TextAnchor.MiddleRight);
                y += 18f * s;
            }
            long ms = run.FinalMs;
            CoopUI.Text(new Rect(x, y + 2f * s, w, 36f * s), SpeedTimer.Format(ms), TimerColour(run, ms), 28, TextAnchor.MiddleRight);
            string state = run.State == SrState.CannotFinish ? "CANNOT FINISH" : run.State == SrState.Running ? "" : run.State == SrState.Finished ? (run.NewPb ? "NEW PB" : "FINISHED") : run.State.ToString().ToUpper();
            if (state.Length > 0) CoopUI.Text(new Rect(x, y + 2f * s, w, 36f * s), state, run.State == SrState.Finished ? GoldC : Behind, 12, TextAnchor.MiddleLeft);
            y += 40f * s;
            var rec = Speedrun.RecordFor(run.Category, run.Mode);
            long sob = e.SumOfBest();
            string foot = "PB " + (rec.PbMs >= 0 ? SpeedTimer.Format(rec.PbMs) : "-") + "   SUM OF BEST " + (sob >= 0 ? SpeedTimer.Format(sob) : "-");
            CoopUI.Text(new Rect(x, y, w, 18f * s), foot, CoopUI.CyanDim, 11, TextAnchor.MiddleLeft);
            DrawnFrames++;
            return r.yMax;
        }

        public static void DrawResults(SrRun run, float sw, float sh)
        {
            float s = CoopUI.ScaleFor(sh);
            var r = ResultsRect(sw, sh, s);
            CoopUI.Frame(r, CoopUI.GlitchFor(run.FinishedAtRealtime));
            float x = r.x + 16f * s, w = r.width - 32f * s;
            CoopUI.Text(new Rect(x, r.y + 10f * s, w, 24f * s), SrCategories.Label(run.Category) + (run.NewPb ? "  NEW PERSONAL BEST" : "  RUN FINISHED"), run.NewPb ? GoldC : CoopUI.White, 16, TextAnchor.MiddleCenter);
            CoopUI.Text(new Rect(x, r.y + 40f * s, w, 50f * s), SpeedTimer.Format(run.Engine.EndMs), run.NewPb ? GoldC : CoopUI.Cyan, 34, TextAnchor.MiddleCenter);
            var rec = Speedrun.RecordFor(run.Category, run.Mode);
            string pb = run.NewPb ? "SAVED AS YOUR PB" : rec.PbMs >= 0 ? "PB " + SpeedTimer.Format(rec.PbMs) + "  (" + SpeedTimer.Delta(run.Engine.EndMs - rec.PbMs) + ")" : "";
            if (!run.Valid) pb = "INVALID: " + run.Invalid[0];
            CoopUI.Text(new Rect(x, r.y + 100f * s, w, 22f * s), pb, run.Valid ? CoopUI.Cyan : Behind, 13, TextAnchor.MiddleCenter);
        }
    }
}
