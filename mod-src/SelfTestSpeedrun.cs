using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace CoopMod
{
    // The speedrun checks (quick-audit group "speedrun"). The pure ones run first; the live ones run on the start screen before the audit's own game:
    // two real runs in the speedrun save slot (Any% reset from the menu, then Any% to the switch with 100% and All Star Seeds timed alongside),
    // with the game's events played by calling the game's own functions, so nobody has to play. The owner's save is hashed before and after.
    public partial class SelfTest
    {
        static readonly string[] SrLiveIds = { "SR-MENU", "SR-START-FRAME", "SR-CLOUD-OFF", "SR-NEWGAME-FRESH", "SR-EVENTS-LIVE", "SR-BACKUP-GUARD", "SR-SKIN-GUARD", "SR-PERF", "SR-UI",
                                               "SR-RESET-RESTORE", "SR-SLOT-ISOLATION", "SR-LEVER-END", "SR-100-END", "SR-ASS-END", "SR-RECORDS-LIVE", "SR-RACE-ISOLATION" };
        readonly HashSet<string> srReported = new HashSet<string>();

        void SrCheck(string id, string what, bool ok, string detail) { srReported.Add(id); Check(id, what, ok, detail); }

        // a live check that could not run because an earlier step went wrong still says so (FAIL, never silent)
        void SrFailRest(string why)
        {
            foreach (var id in SrLiveIds) if (!srReported.Contains(id)) SrCheck(id, "speedrun live check", false, "not reached: " + why);
        }

        static IEnumerator WaitRealUntil(Cond cond, float timeout)
        {
            float end = Time.realtimeSinceStartup + timeout;
            while (Time.realtimeSinceStartup < end)
            {
                bool ok = false;
                try { ok = cond(); } catch (Exception) { }
                if (ok) yield break;
                yield return null;
            }
        }

        static SrEvent SrAcc(string name) { return new SrEvent { Kind = SrEvKind.Accomplishment, Name = name }; }
        static SrEvent SrStateEv(SrEvKind kind, float crystals, int seeds, int bank) { return new SrEvent { Kind = kind, HasState = true, Crystals = crystals, Seeds = seeds, DataBank = bank }; }
        static string SrStates(SplitEngine e) { var sb = new System.Text.StringBuilder(); foreach (var s in e.States) sb.Append(s == SrSplitState.Done ? 'D' : s == SrSplitState.Skipped ? 'S' : 'P'); return sb.ToString(); }

        // ---- pure ------------------------------------------------------------------------------------------------------------------------

        void CheckSpeedrunPure()
        {
            Try("SR-TIMER", "speedrun timer checks ran", CheckSrTimer);
            Try("SR-SPLITS-ANY", "Any% split checks ran", CheckSrSplitsAny);
            Try("SR-SPLITS-ASS", "All Star Seeds split checks ran", CheckSrSplitsAss);
            Try("SR-SPLITS-100", "100% split checks ran", CheckSrSplits100);
            Try("SR-COMPARE", "comparison checks ran", CheckSrCompare);
            Try("SR-RECORDS", "records checks ran", CheckSrRecords);
            Try("SR-MENU-ROWS", "speedrun menu line checks ran", CheckSrMenuRows);
            Try("SR-VALIDITY", "validity checks ran", CheckSrValidity);
            Try("SR-LIVESPLIT", "LiveSplit checks ran", CheckSrLiveSplit);
        }

        void CheckSrTimer()
        {
            long fake = 1000000;
            var t = new SpeedTimer(() => fake);
            t.StartAt(fake);
            fake += SpeedTimer.FromMs(1234);
            bool a = t.ElapsedMs == 1234;
            fake += SpeedTimer.FromMs(1000); t.StopAt(fake); fake += SpeedTimer.FromMs(5000);
            bool b = t.ElapsedMs == 2234;
            var t2 = new SpeedTimer(() => fake); t2.StartAt(fake);
            long thirty = 30L * 3600000L + 1234;
            fake += SpeedTimer.FromMs(thirty);
            bool c = t2.ElapsedMs == thirty;
            SrCheck("SR-TIMER", "the timer gives exact milliseconds with an injected clock, stops for good, and a 30 h run does not overflow", a && b && c, "1234=" + a + " stopped 2234=" + b + " 30h=" + c + " (" + t2.ElapsedMs + ")");
            string f = SpeedTimer.Format(0) + " " + SpeedTimer.Format(61234) + " " + SpeedTimer.Format(3723450) + " " + SpeedTimer.Format(thirty) + " " + SpeedTimer.Delta(-1250) + " " + SpeedTimer.Delta(62340) + " " + SpeedTimer.Delta(400);
            SrCheck("SR-TIMER", "times read like h:mm:ss.cc and deltas like +1.25", f == "0:00.00 1:01.23 1:02:03.45 30:00:01.23 -1.25 +1:02.34 +0.40", f);
            float ts = Time.timeScale;
            Time.timeScale = 0f;
            var rt = new SpeedTimer(null); rt.StartAt(rt.Now());
            Thread.Sleep(40);
            long ms = rt.ElapsedMs;
            Time.timeScale = ts;
            SrCheck("SR-TIMER", "the real clock runs on while the game is stopped (time scale 0): real time, loads and menus count", ms >= 35 && ms < 2000, ms + " ms over a 40 ms wait at time scale 0");
        }

        void CheckSrSplitsAny()
        {
            var e = new SplitEngine(SrCategory.Any, 8);
            var ch = new List<int>();
            e.Feed(SrAcc("TrunkReachedIsland2"), 1000, ch);
            e.Feed(SrAcc("FlowerGrowth"), 3000, ch);
            int before = e.Order.Count;
            e.Feed(SrAcc("TrunkReachedIsland2"), 3500, ch);
            bool again = e.Order.Count == before;
            e.Feed(SrAcc("TeleportedSeed"), 4000, ch);
            bool notYet = !e.Ended;
            e.Feed(new SrEvent { Kind = SrEvKind.FinalFadeEnd }, 4500, ch);
            bool notFade = !e.Ended;
            e.Feed(new SrEvent { Kind = SrEvKind.LeverEnd }, 5000, ch);
            string st = SrStates(e);
            bool ok = e.Count == 6 && st == "DSSDDD" && e.Ended && e.EndMs == 5000 && again && notYet && notFade && e.AtMs[3] == 3000;
            SrCheck("SR-SPLITS-ANY", "Any%: splits in order, a later split skips the ones before, a repeat does not split again, only the switch ends the run", ok,
                "states " + st + " end " + e.EndMs + " repeat ignored=" + again + " seed alone ends=" + !notYet + " fade ends=" + !notFade);
            var ng = SrCategories.Splits(SrCategory.AnyNg, 8);
            SrCheck("SR-SPLITS-ANY", "Any% NG+ has the same splits and ends on the switch", ng.Count == 6 && ng[5].Rule == SplitRule.Lever && ng[5].IsEnd, ng.Count + " splits");
        }

        void CheckSrSplitsAss()
        {
            var e = new SplitEngine(SrCategory.AllSeeds, 8);
            var ch = new List<int>();
            long ms = 0;
            foreach (var a in new[] { "TrunkReachedIsland2", "TrunkReachedIsland3", "TrunkReachedAsteroid", "FlowerGrowth", "TeleportedSeed", "1stEndingViewed" }) e.Feed(SrAcc(a), ms += 1000, ch);
            for (int k = 1; k <= 8; k++) e.Feed(SrStateEv(SrEvKind.Seed, 50f, k, 5), ms += 1000, ch);
            bool seedEnds = e.Ended;
            e.Feed(new SrEvent { Kind = SrEvKind.LeverEnd }, ms += 1000, ch);
            bool leverEnds = e.Ended;
            e.Feed(new SrEvent { Kind = SrEvKind.FinalFadeEnd }, 99000, ch);
            string st = SrStates(e);
            bool ok = e.Count == 14 && e.Count <= SrCategories.MaxSplits && st == new string('D', 14) && e.Ended && e.EndMs == 99000 && !seedEnds && !leverEnds;
            SrCheck("SR-SPLITS-ASS", "All Star Seeds: island/flower/first seed/first ending, one split per seed up to N-1, the end only on the fade after the last seed", ok,
                e.Count + " splits, states " + st + ", end " + e.EndMs + ", last seed ends=" + seedEnds + ", switch ends=" + leverEnds);
            var e10 = new SplitEngine(SrCategory.AllSeeds, 50);
            SrCheck("SR-SPLITS-ASS", "a level with an absurd seed total still keeps at most 16 splits", e10.Count <= SrCategories.MaxSplits, e10.Count + " splits for 50 seeds");
        }

        void CheckSrSplits100()
        {
            var ch = new List<int>();
            var e = new SplitEngine(SrCategory.Hundred, 8);
            e.Feed(SrStateEv(SrEvKind.Shard, 50f, 0, 0), 1000, ch);
            e.Feed(SrAcc("TeleportedSeed"), 2000, ch);
            e.Feed(SrStateEv(SrEvKind.DataBank, 50f, 0, 10), 3000, ch);
            e.Feed(SrStateEv(SrEvKind.Seed, 60f, 8, 12), 4000, ch);
            string order = ""; foreach (int i in e.Order) order += e.Defs[i].Id + " ";
            bool early = e.CannotFinish && !e.Ended && order.Trim() == "CRYSTALS50 SEED1 DATABANK10 SEEDS4 SEEDS7";
            SrCheck("SR-SPLITS-100", "100%: splits are taken in any order and shown as reached; the last seed sent before 100 crystals and the full databank cannot finish", early,
                "order " + order + "cannot finish=" + e.CannotFinish + " ended=" + e.Ended);
            var e2 = new SplitEngine(SrCategory.Hundred, 8);
            e2.Feed(SrStateEv(SrEvKind.Shard, 100f, 7, 20), 1000, ch);
            bool notOnCrystal = !e2.Ended;
            e2.Feed(SrStateEv(SrEvKind.DataBank, 100f, 7, 20), 1500, ch);
            bool notOnBank = !e2.Ended;
            e2.Feed(SrStateEv(SrEvKind.Seed, 100f, 8, 20), 2000, ch);
            bool ok = notOnCrystal && notOnBank && e2.Ended && !e2.CannotFinish && e2.EndMs == 2000 && e2.States[e2.Count - 1] == SrSplitState.Done;
            SrCheck("SR-SPLITS-100", "100%: the run ends on the last seed delivered with everything else done, never on a crystal or databank entry", ok,
                "states " + SrStates(e2) + " end " + e2.EndMs + " crystal ends=" + !notOnCrystal + " databank ends=" + !notOnBank);
        }

        void CheckSrCompare()
        {
            var e = new SplitEngine(SrCategory.Any, 8);
            for (int i = 0; i < e.Count; i++) { e.PbAt[i] = (i + 1) * 1000; e.BestSeg[i] = 900; }
            var ch = new List<int>();
            e.Feed(SrAcc("TrunkReachedIsland2"), 800, ch);
            e.Feed(SrAcc("TrunkReachedIsland3"), 2100, ch);
            bool d = e.DeltaMs(0) == -200 && e.DeltaMs(1) == 100 && e.DeltaMs(2) == long.MinValue;
            bool g = e.Gold(0) && !e.Gold(1);
            bool sob = e.SumOfBest() == 5400;
            bool live = e.LiveDelta(2500) == 100 && e.LiveDelta(3500) == 500;
            SrCheck("SR-COMPARE", "deltas against the PB, gold for a new best segment, sum of best and the live comparison", d && g && sob && live,
                "deltas " + e.DeltaMs(0) + "/" + e.DeltaMs(1) + " gold " + e.Gold(0) + "/" + e.Gold(1) + " sob " + e.SumOfBest() + " live " + e.LiveDelta(2500) + "/" + e.LiveDelta(3500));
        }

        static SplitEngine SrFinishedAny(long[] times)
        {
            var e = new SplitEngine(SrCategory.Any, 8);
            var names = new[] { "TrunkReachedIsland2", "TrunkReachedIsland3", "TrunkReachedAsteroid", "FlowerGrowth", "TeleportedSeed" };
            for (int i = 0; i < 5; i++) e.Feed(SrAcc(names[i]), times[i], null);
            e.Feed(new SrEvent { Kind = SrEvKind.LeverEnd }, times[5], null);
            return e;
        }

        void CheckSrRecords()
        {
            string dir = Path.Combine(Path.GetTempPath(), "coopmod-sr-test-" + System.Diagnostics.Process.GetCurrentProcess().Id);
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                string file = Path.Combine(dir, "records-any.txt");
                var rec = new SrRecord { Attempts = 3 };
                var run1 = SrFinishedAny(new long[] { 1000, 2000, 3000, 4000, 5000, 6000 });
                bool pb1 = SpeedrunRecords.Apply(rec, run1, true, "2026-01-01");
                SpeedrunRecords.Save(file, rec);
                var back = SpeedrunRecords.Load(file);
                bool round = back.PbMs == 6000 && back.Attempts == 3 && back.Finished == 1 && back.Splits.Count == 6 && back.Split("ISLAND2").PbMs == 1000 && back.Split("END").BestSegMs == 1000 && back.PbDate == "2026-01-01";
                SrCheck("SR-RECORDS", "a finished run becomes the PB and the file reads back exactly", pb1 && round, "pb=" + pb1 + " read back pb " + back.PbMs + ", " + back.Splits.Count + " splits");
                var run2 = SrFinishedAny(new long[] { 500, 2000, 3000, 4000, 5000, 9000 });   // slower, but a better first segment
                bool pb2 = SpeedrunRecords.Apply(back, run2, true, "2026-01-02");
                SpeedrunRecords.Save(file, back);
                var b2 = SpeedrunRecords.Load(file);
                bool slower = !pb2 && b2.PbMs == 6000 && b2.Split("ISLAND2").PbMs == 1000 && b2.Split("ISLAND2").BestSegMs == 500 && b2.Split("ISLAND3").BestSegMs == 1000 && b2.Finished == 2;
                SrCheck("SR-RECORDS", "a slower run keeps the PB but improves a best segment it beat", slower, "pb " + b2.PbMs + " island2 best " + b2.Split("ISLAND2").BestSegMs + " finished " + b2.Finished);
                bool invalid = !SpeedrunRecords.Apply(b2, SrFinishedAny(new long[] { 1, 2, 3, 4, 5, 6 }), false, "x") && b2.PbMs == 6000 && b2.Finished == 2;
                File.WriteAllText(file, "garbage\r\npb_ms=hello");
                SrRecord fromBak = null; bool threw = false;
                try { fromBak = SpeedrunRecords.Load(file); } catch (Exception) { threw = true; }
                bool bak = !threw && fromBak != null && fromBak.PbMs == 6000 && fromBak.Finished == 1;    // the backup is the version before the last save
                bool hostile = SpeedrunRecords.Parse("version=1\nsplit=a|x|y") == null && SpeedrunRecords.Parse("") == null && SpeedrunRecords.Parse("version=2") == null && SpeedrunRecords.Parse("version=1\npb_ms=-5") == null;
                SrCheck("SR-RECORDS", "an invalid run is never recorded; a damaged file falls back to its backup without an exception; damaged text is refused", invalid && bak && hostile,
                    "invalid kept out=" + invalid + " backup used=" + bak + (fromBak != null ? " (pb " + fromBak.PbMs + ", finished " + fromBak.Finished + ")" : "") + " hostile refused=" + hostile);
            }
            finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { } }
        }

        void CheckSrMenuRows()
        {
            var states = new List<CoopMenu.State>();
            var bas = new CoopMenu.State { Screen = CoopMenu.ScreenSpeedrun, SrCategory = "ALL STAR SEEDS", SrMode = "CO-OP RUN", SrPb = "1:02:31.40", SrTime = "12:34:56.78", SrValid = true, SrSplitsShown = true };
            var s0 = bas; s0.AtStart = true; states.Add(s0);
            var s1 = bas; s1.AtStart = true; s1.SrIsMember = true; s1.SrPb = ""; states.Add(s1);
            var s2 = bas; s2.SrActive = true; s2.SrRunning = true; s2.SrMode = "SOLO"; s2.SrValid = false; s2.SrInvalidWhy = "CANNOT FINISH: LAST SEED SENT EARLY"; states.Add(s2);
            var s3 = bas; s3.SrActive = true; s3.SrRunning = true; s3.SrMode = "RACE"; states.Add(s3);
            var s4 = bas; s4.SrActive = true; s4.SrFinished = true; s4.SrRunState = "FINISHED"; states.Add(s4);
            var s5 = bas; states.Add(s5);
            var s6 = bas; s6.AtStart = true; s6.SrMode = "RACE"; s6.SrRaceArmed = true; s6.SrRaceHost = true; s6.SrRaceReady = 2; s6.SrRaceTotal = 3; states.Add(s6);
            var s7 = bas; s7.AtStart = true; s7.SrMode = "RACE"; s7.SrRaceArmed = true; s7.SrIsMember = true; states.Add(s7);
            var s8 = bas; s8.AtStart = true; s8.SrMode = "RACE"; s8.SrCountdown = true; s8.SrRaceHost = true; states.Add(s8);
            var s9 = bas; s9.SrActive = true; s9.SrRunning = true; s9.SrMode = "RACE"; s9.SrRaceHost = true; states.Add(s9);
            var bad = new List<string>();
            for (int k = 0; k < states.Count; k++)
            {
                string l1, l2;
                var rows = CoopMenu.RowsFor(states[k], out l1, out l2);
                int acts = 0; var ids = new HashSet<string>(); bool dup = false;
                foreach (var r in rows) { if (!r.Inert) acts++; if (!r.Inert && !ids.Add(r.Id)) dup = true; }
                if (acts > CoopMenu.MaxRows || rows[rows.Count - 1].Id != "back" || dup || l1.Length > 34 || l2.Length > 44) bad.Add("state " + k + ": " + Ids(rows));
                if (k == 0 && (!ids.Contains("sr_start") || !ids.Contains("sr_cat") || !ids.Contains("sr_mode"))) bad.Add("leader cannot start: " + Ids(rows));
                if (k == 1 && ids.Contains("sr_start")) bad.Add("a member can start: " + Ids(rows));
                if (k == 2 && (!ids.Contains("sr_reset") || !ids.Contains("sr_restart") || !ids.Contains("sr_end"))) bad.Add("solo run lines: " + Ids(rows));
                if (k == 3 && ids.Contains("sr_restart")) bad.Add("a race can restart alone: " + Ids(rows));
                if (k == 4 && !ids.Contains("sr_quit")) bad.Add("no SAVE AND QUIT after the run: " + Ids(rows));
                if (k == 6 && (!ids.Contains("sr_go") || !ids.Contains("sr_cancel") || ids.Contains("sr_start") || ids.Contains("sr_cat"))) bad.Add("armed race leader: " + Ids(rows));
                if (k == 7 && (!ids.Contains("sr_leave") || ids.Contains("sr_start") || ids.Contains("sr_go"))) bad.Add("armed race member: " + Ids(rows));
                if (k == 8 && (ids.Contains("sr_start") || ids.Contains("sr_go") || ids.Contains("sr_cancel"))) bad.Add("countdown: " + Ids(rows));
                if (k == 9 && (!ids.Contains("sr_endrace") || ids.Contains("sr_restart"))) bad.Add("race leader in the run: " + Ids(rows));
            }
            string p1, p2;
            var party = CoopMenu.RowsFor(new CoopMenu.State { Online = true, AtStart = true }, out p1, out p2);
            if (Ids(party).Contains("sr_")) bad.Add("party page shows speedrun lines");
            SrCheck("SR-MENU-ROWS", "every speedrun page has at most 7 lines that do something, BACK last, no line twice, short titles; members cannot start, races cannot restart alone; an armed race gives the leader START NOW / CANCEL and members LEAVE, the countdown nothing to press, the race leader END RACE", bad.Count == 0,
                bad.Count == 0 ? states.Count + " pages ok" : string.Join(" | ", bad.ToArray()));
        }

        void CheckSrValidity()
        {
            bool any = Speedrun.SkinRule(SrCategory.Any, true, false, SceneProfile.CharacterSkin.Standard) != null
                && Speedrun.SkinRule(SrCategory.Any, false, true, SceneProfile.CharacterSkin.Standard) != null
                && Speedrun.SkinRule(SrCategory.Any, false, false, SceneProfile.CharacterSkin.Ninja) != null
                && Speedrun.SkinRule(SrCategory.Any, false, false, SceneProfile.CharacterSkin.Native) != null
                && Speedrun.SkinRule(SrCategory.Any, false, false, SceneProfile.CharacterSkin.Desert) == null
                && Speedrun.SkinRule(SrCategory.Any, false, false, SceneProfile.CharacterSkin.Facebook) == null;
            bool others = Speedrun.SkinRule(SrCategory.AnyNg, true, true, SceneProfile.CharacterSkin.Ninja) == null && Speedrun.SkinRule(SrCategory.Hundred, true, true, SceneProfile.CharacterSkin.Native) == null;
            bool assist = Speedrun.AssistInvalidates(SrMode.Solo) && Speedrun.AssistInvalidates(SrMode.Race) && !Speedrun.AssistInvalidates(SrMode.Coop);
            long fake = 5000;
            var r = new SrRun(SrCategory.Hundred, SrMode.Solo, 8, () => fake);
            r.Timer.StartAt(fake);
            r.Feed(SrStateEv(SrEvKind.Seed, 40f, 8, 3), null);
            bool early = r.State == CoopMod.SrState.CannotFinish && !r.Valid && r.Live;
            SrCheck("SR-VALIDITY", "Any% is invalid with an unlocked or worn Ninja/Native skin (the always-there skins are fine), NG+ and 100% allow them, assists invalidate solo and race runs, an early last seed makes 100% invalid but keeps the timer running",
                any && others && assist && early, "any%=" + any + " others=" + others + " assist=" + assist + " early seed=" + early);
        }

        void CheckSrLiveSplit()
        {
            bool map = LiveSplit.CommandFor(null, "start", -1) == "starttimer" && LiveSplit.CommandFor(null, "split", 0) == "split" && LiveSplit.CommandFor(null, "skip", 1) == "skipsplit"
                && LiveSplit.CommandFor(new SrRun(SrCategory.Any, SrMode.Solo, 8, null) { State = CoopMod.SrState.Reset }, "end", -1) == "reset"
                && LiveSplit.CommandFor(new SrRun(SrCategory.Any, SrMode.Solo, 8, null) { State = CoopMod.SrState.Finished }, "end", -1) == null;
            var got = new List<string>();
            int port = 0;
            TcpListener listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var c = new LiveSplitClient("127.0.0.1", port);
                c.Send("starttimer"); c.Send("split"); c.Send("reset");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!listener.Pending() && sw.ElapsedMilliseconds < 3000) Thread.Sleep(10);
                if (listener.Pending())
                {
                    using (var sock = listener.AcceptTcpClient())
                    {
                        var st = sock.GetStream();
                        var buf = new byte[256]; string text = "";
                        while (sw.ElapsedMilliseconds < 4000 && text.Split('\n').Length < 4)
                        {
                            if (!st.DataAvailable) { Thread.Sleep(10); continue; }
                            int n = st.Read(buf, 0, buf.Length); if (n <= 0) break;
                            text += System.Text.Encoding.ASCII.GetString(buf, 0, n);
                        }
                        foreach (var l in text.Split('\n')) if (l.Trim().Length > 0) got.Add(l.Trim());
                    }
                }
                c.Close();
            }
            finally { if (listener != null) listener.Stop(); }
            SrCheck("SR-LIVESPLIT", "LiveSplit's TCP server receives start, split and reset in order", map && string.Join(",", got.ToArray()) == "starttimer,split,reset", "map=" + map + " got " + string.Join(",", got.ToArray()));
            var none = new LiveSplitClient("127.0.0.1", port);       // the listener is gone: nobody answers there
            var w = System.Diagnostics.Stopwatch.StartNew();
            bool threw = false;
            try { none.Send("split"); none.Send("reset"); } catch (Exception) { threw = true; }
            double took = w.Elapsed.TotalMilliseconds;
            Thread.Sleep(300);
            none.Close();
            SrCheck("SR-LIVESPLIT", "with no LiveSplit running a send costs the game under 5 ms and throws nothing", !threw && took < 5.0, took.ToString("F2") + " ms, threw=" + threw + ", client: " + none.LastProblem);
        }

        // ---- the UI test lays sample runs out on the real HUD canvas (SpeedrunUI.PreviewForTest; there is no OnGUI any more) ----

        // a race of 8 in every state: finished (a tie), running, offline, reset, DNF, not started
        static RaceFsm SrUiRaceSample()
        {
            var f = new RaceFsm();
            var ids = new List<ulong>(); for (ulong i = 2; i <= 8; i++) ids.Add(i);
            f.ArmAsHost(5, SrCategory.AllSeeds, 8, 1, "A VERY LONG NAME INDEED", ids, 0f);
            foreach (var id in ids) f.OnReady(id, 5, SrMsg.ReadyOk, 0f);
            f.StartCountdown(0f);
            var st = new[] { SrState.Finished, SrState.Finished, SrState.Running, SrState.Running, SrState.Reset, SrState.Dnf, SrState.CannotFinish };
            for (int i = 0; i < st.Length; i++) { SrNetMsg m; SrMsg.TryParse(SrMsg.ProgressMsg(5, 60000 + (i / 2) * 1000, i, 14, st[i], i != 3, 59000), out m); f.OnProgress((ulong)(i + 1), m, 1f); }
            f.Find(4).Offline = true; f.Find(8).Name = "EVE";
            return f;
        }
        static List<SrRun> SrUiSamples()
        {
            long fake = 1000;
            var list = new List<SrRun>();
            var a = new SrRun(SrCategory.Any, SrMode.Solo, 8, () => fake); a.Timer.StartAt(0);
            for (int i = 0; i < a.Engine.Count; i++) { a.Engine.PbAt[i] = i * 1000 + 500; a.Engine.BestSeg[i] = 400; }
            a.Feed(new SrEvent { Kind = SrEvKind.Accomplishment, Name = "TrunkReachedIsland2", Ticks = SpeedTimer.FromMs(300) }, null);
            a.Feed(new SrEvent { Kind = SrEvKind.Accomplishment, Name = "FlowerGrowth", Ticks = SpeedTimer.FromMs(900) }, null);
            list.Add(a);
            var h = new SrRun(SrCategory.Hundred, SrMode.Coop, 8, () => fake); h.Timer.StartAt(0);
            h.Feed(new SrEvent { Kind = SrEvKind.Seed, HasState = true, Crystals = 100f, Seeds = 8, DataBank = 20, Ticks = SpeedTimer.FromMs(700) }, null); h.NewPb = true;
            list.Add(h);
            var c = new SrRun(SrCategory.Hundred, SrMode.Race, 8, () => fake); c.Timer.StartAt(0);
            c.Feed(new SrEvent { Kind = SrEvKind.Seed, HasState = true, Crystals = 10f, Seeds = 8, DataBank = 2 }, null);
            list.Add(c);
            var s = new SrRun(SrCategory.AllSeeds, SrMode.Solo, 8, () => fake); s.Timer.StartAt(0); s.MarkInvalid("ASSIST: F4 FIND PLAYER"); s.State = CoopMod.SrState.Reset;
            list.Add(s);
            return list;
        }

        // ---- live (on the start screen, before the audit's own game) --------------------------------------------------------------------

        static FieldInfo srNewGame;
        static bool SrNewGameFlag()
        {
            if (!Singleton<StartScreen>.Exists) return false;
            if (srNewGame == null) srNewGame = typeof(StartScreen).GetField("m_newGame", BindingFlags.NonPublic | BindingFlags.Instance);
            return srNewGame != null && (bool)srNewGame.GetValue(Singleton<StartScreen>.Instance);
        }

        static string SrListing(string dir)
        {
            if (!Directory.Exists(dir)) return "";
            var l = new List<string>(Directory.GetFiles(dir)); l.Sort(StringComparer.Ordinal);
            return string.Join("|", l.ToArray());
        }

        static string SrLastRunLine(string file)
        {
            try { if (!File.Exists(file)) return ""; var lines = File.ReadAllLines(file); return lines.Length == 0 ? "" : lines[lines.Length - 1]; } catch (Exception) { return ""; }
        }

        // into the slot's world: the opening cutscene is ended the way the game ends it, then wait for the start screen to be gone
        IEnumerator SrIntoWorld(float timeout)
        {
            float end = Time.realtimeSinceStartup + timeout;
            while (Time.realtimeSinceStartup < end && !(WorldSync.InWorld() && !Singleton<StartScreen>.Exists))
            {
                if (ScriptScroller.m_Running)
                {
                    var intro = (IntroSequenceHandler)UnityEngine.Object.FindObjectOfType(typeof(IntroSequenceHandler));
                    if (intro != null) intro.EndIntro();
                }
                yield return null;
            }
            yield return StartCoroutine(WaitReal(1f));
        }

        // a moment as run time, also after the run's timer stopped (MsAt would give the stop time then)
        long SrMs(SrRun run, long ticks) { return SpeedTimer.ToMs(ticks - run.Timer.StartTicks); }

        IEnumerator TestSpeedrunLive()
        {
            var cb = GetComponent<CoopBehaviour>();
            string root = Path.GetFullPath(SaveGuard.Root), sr = Path.Combine(root, "speedrun"), slot = Path.Combine(sr, "slot"), level = Application.loadedLevelName;
            yield return StartCoroutine(WaitRealUntil(() => File.Exists(Path.Combine(root, "seeded.txt")), 10f));
            var before = Snapshot(root, sr);
            string ownP = SaveSystem.m_playerSavePath, ownL = SaveSystem.m_levelFilesPath;
            bool contBefore = SaveSystem.LevelSaveExists(level);
            string backups = Path.Combine(root, "backups"), backupsBefore = SrListing(backups);
            string skinFile = Here("coop-skin.txt"); bool skinExisted = File.Exists(skinFile); string skinOld = skinExisted ? File.ReadAllText(skinFile) : null;
            string recFile = Speedrun.RecordFile(SrCategory.Any, SrMode.Solo);
            var recBefore = SpeedrunRecords.Load(recFile);
            var catBefore = Speedrun.Category; var modeBefore = Speedrun.Mode;
            Speedrun.Mode = SrMode.Solo;
            Note("speedrun: own save " + before.Count + " files, level save " + (contBefore ? "exists" : "none") + ", records before: pb " + recBefore.PbMs + " attempts " + recBefore.Attempts);

            // the SPEEDRUN line of the start menu opens the speedrun page
            var menu = CoopMenu.Menu;
            var handler = menu == null ? null : menu.GetComponent<CoopMenuHandler>();
            var entry = CoopMenu.SpeedrunStartEntry;
            bool placed = entry != null && CoopMenu.StartEntry != null && entry.transform.parent == CoopMenu.StartEntry.transform.parent
                && entry.transform.GetSiblingIndex() + 1 == CoopMenu.StartEntry.transform.GetSiblingIndex() && entry.transform.Find("ButText").GetComponent<UnityEngine.UI.Text>().text == "SPEEDRUN";
            if (handler == null || cb == null || entry == null) { SrCheck("SR-MENU", "the start menu has a SPEEDRUN line", false, "menu=" + (handler != null) + " entry=" + (entry != null) + " " + CoopMenu.LastProblem); SrFailRest("no speedrun menu"); yield break; }
            entry.onClick.Invoke();
            yield return null; yield return null;
            string l1, l2;
            var expected = CoopMenu.RowsFor(cb.CoopMenuState(), out l1, out l2);
            bool open = menu.gameObject.activeInHierarchy && CoopMenu.ScreenNow == CoopMenu.ScreenSpeedrun;
            bool titled = handler.Title != null && handler.Title.text == "SPEEDRUN";
            string ids = string.Join(",", handler.RowIdsForTest.ToArray());
            bool rowsOk = ids == Ids(expected) && ids.Contains("sr_cat") && ids.Contains("sr_start") && ids.EndsWith("back");
            string layout = RowsLayout(handler, expected, menu);
            for (int i = 0; i < 4 && Speedrun.Category != SrCategory.Any; i++) handler.OnMenuClick("sr_cat");
            yield return null;
            bool anyCat = Speedrun.Category == SrCategory.Any && string.Join(",", LabelsOf(handler).ToArray()).Contains("CATEGORY: ANY%");
            SrCheck("SR-MENU", "SPEEDRUN sits right above CO-OP PARTY in the start menu and opens the SPEEDRUN page (category, mode, start, PB, splits, LiveSplit, BACK) inside the panel; CATEGORY cycles",
                placed && open && titled && rowsOk && layout.Length == 0 && anyCat, "placed=" + placed + " open=" + open + " titled=" + titled + " rows " + ids + " layout " + (layout.Length == 0 ? "ok" : layout) + " any%=" + anyCat + "; says \"" + l1 + "\" / \"" + l2 + "\"");

            // nothing starts without the slot
            string why; bool started = Speedrun.BeginNewGame(out why);
            SrCheck("SR-START-FRAME", "without the speedrun save slot nothing starts (fault injected: slot not entered)", !started && !SrNewGameFlag() && (Speedrun.Current == null || !Speedrun.Current.Live), "started=" + started + " why=" + why);

            // with Steam Cloud forced on for one call, the start is refused and nothing is written
            string e1 = Speedrun.EnterSlot(SrCategory.Any);
            bool slotOk = e1 == null && Speedrun.SlotPathsActive();
            bool refused = false; string cloudWhy = "";
            var smt = Type.GetType("SteamManager, Assembly-CSharp");
            var sm = smt == null ? null : UnityEngine.Object.FindObjectOfType(smt) as Component;
            if (sm != null)
            {
                smt.GetField("m_cloudSavable").SetValue(sm, true);
                try { refused = !Speedrun.BeginNewGame(out cloudWhy); }
                finally { smt.GetField("m_cloudSavable").SetValue(sm, false); smt.GetField("m_cloudSaveEnabled").SetValue(sm, false); }
            }
            bool noGame = !SrNewGameFlag() && !WorldSync.InGameOrLoading();
            Speedrun.LeaveSlot("test");
            bool pathsBack = SaveSystem.m_playerSavePath == ownP && SaveSystem.m_levelFilesPath == ownL && !Speedrun.SlotActive;
            SrCheck("SR-CLOUD-OFF", "with Steam Cloud saving on, a speedrun is refused (the game's NEW GAME would delete the cloud save): no game starts, the slot is left, my save is unchanged",
                sm != null && slotOk && refused && (cloudWhy ?? "").Contains("CLOUD") && noGame && pathsBack && SameFiles(before, Snapshot(root, sr)),
                "steam=" + (sm != null) + " slot=" + slotOk + " refused=" + refused + " (" + cloudWhy + ") no game=" + noGame + " paths back=" + pathsBack);

            // run A: START RUN through the menu line, as the player presses it
            try { File.WriteAllText(skinFile, "Ninja"); } catch (Exception) { }
            if (!menu.gameObject.activeInHierarchy) { CoopMenu.OpenSpeedrunFromStart(); yield return null; yield return null; }
            yield return StartCoroutine(WaitRealUntil(() => Speedrun.StartScreenIdle, 10f));
            int frame = Time.frameCount;
            handler.OnMenuClick("sr_start");
            var runA = Speedrun.Current;
            bool frameOk = runA != null && runA.Live && runA.StartFrame == frame && runA.NewGameAtStart && SrNewGameFlag();
            bool inSlot = Speedrun.SlotPathsActive() && Under(SaveSystem.m_playerSavePath, slot) && Under(SaveSystem.m_levelFilesPath, slot);
            SrCheck("SR-START-FRAME", "START RUN starts the timer on the frame of the game's own NEW GAME, and the game says it is a fresh new game, in the slot",
                frameOk && inSlot, "run=" + (runA != null) + " frame " + (runA != null ? runA.StartFrame : -1) + "/" + frame + " new game=" + SrNewGameFlag() + " in slot=" + inSlot + " " + Speedrun.Status);
            if (runA == null || !runA.Live) { SrFailRest("run A did not start"); RestoreSkinFile(skinFile, skinExisted, skinOld); yield break; }
            var shadow = Speedrun.AddShadow(SrCategory.Hundred);
            yield return StartCoroutine(SrIntoWorld(120f));
            if (!WorldSync.InWorld()) { SrFailRest("run A never reached the world"); RestoreSkinFile(skinFile, skinExisted, skinOld); yield break; }

            Try("SR-NEWGAME-FRESH", "fresh new game check ran", delegate
            {
                var pd = Singleton<PlayerProfile>.Instance.m_playerData;
                bool fresh = SceneProfile.SeedCollected == 0 && !SceneProfile.HasAccomplishment("TeleportedSeed") && !SceneProfile.HasAccomplishment("1stEndingViewed");
                bool skins = !pd.m_unlockedNinjaSkin && !pd.m_unlockedNativeSkin && !SceneProfile.HasSkin(SceneProfile.CharacterSkin.Ninja) && !SceneProfile.HasSkin(SceneProfile.CharacterSkin.Native);
                SrCheck("SR-NEWGAME-FRESH", "the slot game is a fresh new game: no seeds, no first seed sent, Any% has no Ninja/Native skin, the run is valid", fresh && skins && runA.Valid,
                    "fresh=" + fresh + " no skins=" + skins + " valid=" + runA.Valid + (runA.Valid ? "" : " (" + runA.Invalid[0] + ")") + "; crystals at the start " + SceneProfile.CrystalsCollected + " (starting count " + SceneProfile.StartCrystalCount + "), " + Singleton<SceneProfile>.Instance.m_numberOfSeeds + " seeds in the level");
            });

            Try("SR-EVENTS-LIVE", "live event check ran", delegate
            {
                long t1 = Speedrun.Clock(); SceneProfile.AddAccomplishment("TrunkReachedIsland2"); long t2 = Speedrun.Clock();
                var ea = runA.Engine;
                bool split = ea.States[0] == SrSplitState.Done && ea.AtMs[0] >= SrMs(runA, t1) && ea.AtMs[0] <= SrMs(runA, t2);
                int n = ea.Order.Count;
                SceneProfile.AddAccomplishment("TrunkReachedIsland2");
                Speedrun.Feed(Speedrun.Snapshot(SrEvKind.Accomplishment, "TrunkReachedIsland2"));
                bool once = ea.Order.Count == n;
                SceneProfile.AddShard();
                bool shard = Math.Abs(shadow.Engine.LastCrystals - SceneProfile.CrystalsCollected) < 0.01f;
                DataBankDatabase.DataBankItem item = null;
                for (int i = 0; i < 20 && item == null; i++) { var it = DataBankDatabase.GetItemByIndex(i); if (it != null && !it.m_unlocked) item = it; }
                int bankBefore = shadow.Engine.LastDataBank;
                if (item != null) DataBankDatabase.UnlockItem(item);
                int count = 0; for (int i = 0; i < 20; i++) { var it = DataBankDatabase.GetItemByIndex(i); if (it != null && it.m_unlocked) count++; }
                bool bank = item != null && shadow.Engine.LastDataBank == count;
                SrCheck("SR-EVENTS-LIVE", "the game's own AddAccomplishment / AddShard / databank scan reach the run inside the call (split stamped in that frame), a repeated accomplishment does not split again",
                    split && once && shard && bank, "split=" + split + " (" + ea.AtMs[0] + " ms) once=" + once + " crystals " + shadow.Engine.LastCrystals + "/" + SceneProfile.CrystalsCollected + " databank " + bankBefore + "->" + shadow.Engine.LastDataBank + "/" + count);
            });

            Try("SR-BACKUP-GUARD", "backup guard check ran", delegate
            {
                string b = WorldSync.BackupOwnWorld("speedrun test", true);
                SrCheck("SR-BACKUP-GUARD", "in the slot no backup of the slot world is made, so my real backups are never pushed out", b == null && SrListing(backups) == backupsBefore, "backup=" + (b ?? "none"));
            });

            Try("SR-RACE-ISOLATION", "isolation check ran", delegate { CheckSrRaceIsolationLive(cb); });

            yield return StartCoroutine(WaitReal(5f));
            string skinNow = "";
            try { skinNow = File.Exists(skinFile) ? File.ReadAllText(skinFile).Trim() : ""; } catch (Exception) { }
            SrCheck("SR-SKIN-GUARD", "with coop-skin.txt saying Ninja the slot game stays on the standard skin after 5 s, and the file is not rewritten",
                SceneProfile.ActiveSkin != SceneProfile.CharacterSkin.Ninja && skinNow == "Ninja", "skin " + SceneProfile.ActiveSkin + ", file says " + skinNow + (cb.CoopMenuState().Online ? "" : " (offline: the skin restore never runs)"));

            Try("SR-PERF", "speedrun cost check ran", delegate
            {
                Speedrun.Tick();
                long m0 = GC.GetTotalMemory(false);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 300; i++) Speedrun.Tick();
                double avg = sw.Elapsed.TotalMilliseconds / 300.0;
                long grew = GC.GetTotalMemory(false) - m0;
                SrCheck("SR-PERF", "the speedrun tick costs under 0.2 ms and makes almost no garbage (300 ticks in a running game)", avg < 0.2 && grew < 64 * 1024, avg.ToString("F4") + " ms each, heap +" + grew + " bytes");
            });

            // the sample runs and race are laid out on the real HUD canvas (uGUI, the game's fonts) for a few frames, in place of the live timer
            SpeedrunUI.PreviewForTest(SrUiSamples(), SrUiRaceSample());
            float pv0 = Time.realtimeSinceStartup;
            while (SpeedrunUI.PreviewFrames < 5 && Time.realtimeSinceStartup - pv0 < 3f) yield return null;
            int srUiFrames = SpeedrunUI.PreviewFrames; string srUiProblem = SpeedrunUI.PreviewProblem;
            var uiTexts = CoopHud.SpeedrunLayer.ShownTexts(); var uiFixed = CoopHud.SpeedrunLayer.ShownFixed();
            var uiImgs = CoopHud.SpeedrunLayer.ShownImages();
            var cam = CoopHud.Canvas != null ? CoopHud.Canvas.worldCamera : null;
            var uiProbs = new List<string>();
            if (CoopHud.Canvas == null || cam == null) uiProbs.Add("no HUD canvas");
            if (uiTexts.Count < 20) uiProbs.Add(uiTexts.Count + " texts shown");
            int timesFixed = 0, bad = 0; string badWhat = "";
            var buf = new Vector3[4];
            for (int i = 0; i < uiTexts.Count; i++)
            {
                var t = uiTexts[i];
                if (t.font != HudLayer.FontFor(uiFixed[i])) { bad++; badWhat = "font " + (t.font == null ? "none" : t.font.name) + " on \"" + t.text + "\""; }
                if (uiFixed[i] && System.Text.RegularExpressions.Regex.IsMatch(t.text, "[0-9]:[0-9][0-9]")) timesFixed++;
                if (cam != null) { var r = CoopHud.ScreenRect(t.rectTransform, buf, cam); if (r.xMin < -2f || r.yMin < -2f || r.xMax > Screen.width + 2f || r.yMax > Screen.height + 2f) { bad++; badWhat = "off screen \"" + t.text + "\" " + r; } }
            }
            if (cam != null) foreach (var im in uiImgs) { var r = CoopHud.ScreenRect(im.rectTransform, buf, cam); if (r.xMin < -8f || r.yMin < -2f || r.xMax > Screen.width + 8f || r.yMax > Screen.height + 2f) { bad++; badWhat = "panel off screen " + r; } }
            if (bad > 0) uiProbs.Add(bad + " wrong, e.g. " + badWhat);
            if (timesFixed < 5) uiProbs.Add("only " + timesFixed + " times in the fixed-width font");
            string sawText = CoopHud.SpeedrunLayer.AllText();
            foreach (string want in new[] { "RACE RESULTS", "RACE IN 3", "GO", "INVALID", "SUM OF BEST" }) if (!sawText.Contains(want)) uiProbs.Add("no \"" + want + "\"");
            SpeedrunUI.StopPreview();
            yield return null; yield return null;
            bool lay = true; string layDetail = "";
            foreach (var size in new[] { new Vector2(800, 450), new Vector2(1280, 720), new Vector2(3840, 2160) })
            {
                float s = CoopUI.ScaleFor(size.y);
                var p = SpeedrunUI.PanelRect(size.x, size.y, s, SpeedrunUI.MaxRowsShown); var q = SpeedrunUI.ResultsRect(size.x, size.y, s);
                var st2 = SpeedrunUI.StripRect(size.x, size.y, s, p.yMax, 8); var rr = SpeedrunUI.RaceResultsRect(size.x, size.y, s, 8);
                if (st2.xMin < 0 || st2.yMin < 0 || st2.xMax > size.x || st2.yMax > size.y || rr.xMin < 0 || rr.yMin < 0 || rr.xMax > size.x || rr.yMax > size.y) { lay = false; layDetail += " " + size + " race strip " + st2 + " results " + rr; }
                bool inside = p.xMin >= 0 && p.yMin >= 0 && p.xMax <= size.x && p.yMax <= size.y && q.xMin >= 0 && q.yMin >= 0 && q.xMax <= size.x && q.yMax <= size.y;
                if (!inside) { lay = false; layDetail += " " + size + " panel " + p + " card " + q; }
            }
            SrCheck("SR-UI", "the timer panel in every state (running, finished with a new PB, cannot finish, reset and invalid, co-op), the race strip, countdown and race results for 8 racers are laid out on the mod's uGUI HUD canvas (no IMGUI) in the game's fonts (times in block_fixed), inside the screen, without an exception; the pure layout stays on screen at 800x450, 1280x720 and 4K",
                srUiFrames >= 3 && srUiProblem.Length == 0 && lay && uiProbs.Count == 0 && SpeedrunUI.LastProblem.Length == 0,
                srUiFrames + " frames laid out, " + uiTexts.Count + " texts, " + timesFixed + " fixed-width times" + (srUiProblem.Length > 0 ? ", problem " + srUiProblem : "") + (uiProbs.Count > 0 ? "; " + string.Join("; ", uiProbs.ToArray()) : "") + (SpeedrunUI.LastProblem.Length > 0 ? ", live panel " + SpeedrunUI.LastProblem : "") + (lay ? "" : ", off screen:" + layDetail));

            bool slotWrote = false;
            Try("SR-SLOT-ISOLATION", "forced saves in the slot ran", delegate
            {
                SaveSystem.SaveLevel(); SaveSystem.SavePlayerProfile(); AutoSave.SaveGame();
                slotWrote = File.Exists(Path.Combine(slot, "PlayerProfile.sav")) && File.Exists(Path.Combine(Path.Combine(slot, "Levels"), level + ".sav"));
            });

            // RESET RUN from the pause menu's speedrun page
            cb.CoopMenuAction("sr_reset");
            bool resetNow = runA.State == CoopMod.SrState.Reset;
            yield return StartCoroutine(WaitRealUntil(() => !Speedrun.SlotActive && Singleton<StartScreen>.Exists && !WorldSync.InGameOrLoading(), 90f));
            var recA = SpeedrunRecords.Load(recFile);
            string lineA = SrLastRunLine(Speedrun.RunsLog);
            bool back = SaveSystem.m_playerSavePath == ownP && SaveSystem.m_levelFilesPath == ownL && !Speedrun.SlotActive;
            SrCheck("SR-RESET-RESTORE", "RESET RUN counts the attempt, logs RESET, takes the game's Save & Quit to the start screen, and my own saves and CONTINUE / NEW GAME are back",
                resetNow && lineA.Contains("|any|solo|RESET|") && recA.Attempts == recBefore.Attempts + 1 && back && SaveSystem.LevelSaveExists(level) == contBefore,
                "reset=" + resetNow + " attempts " + recBefore.Attempts + "->" + recA.Attempts + " paths back=" + back + " level save " + SaveSystem.LevelSaveExists(level) + "/" + contBefore + "; log: " + lineA);
            var afterA = Snapshot(root, sr);
            bool isoA = inSlot && slotWrote && SameFiles(before, afterA) && SrListing(backups) == backupsBefore && Speedrun.LastMainSaveCheck == "same";

            // run B: Any% to the switch, with 100% and All Star Seeds timed alongside on the same events (also: a second run wipes the slot)
            yield return StartCoroutine(WaitRealUntil(() => Speedrun.StartScreenIdle, 60f));
            string whyB = Speedrun.StartSolo(SrCategory.Any, false);
            var runB = Speedrun.Current;
            bool wiped = !File.Exists(Path.Combine(Path.Combine(slot, "Levels"), level + ".sav"));
            if (whyB != null || runB == null || runB == runA) { SrCheck("SR-SLOT-ISOLATION", "my own save files are byte-identical after a slot run", isoA, "run B did not start: " + whyB); SrFailRest("run B did not start: " + whyB); RestoreSkinFile(skinFile, skinExisted, skinOld); yield break; }
            var ass = Speedrun.AddShadow(SrCategory.AllSeeds);
            var hundred = Speedrun.AddShadow(SrCategory.Hundred);
            yield return StartCoroutine(SrIntoWorld(120f));
            if (!WorldSync.InWorld()) { SrCheck("SR-SLOT-ISOLATION", "my own save files are byte-identical after a slot run", isoA, "run B never reached the world"); SrFailRest("run B never reached the world"); RestoreSkinFile(skinFile, skinExisted, skinOld); yield break; }

            SceneProfile.AddAccomplishment("TeleportedSeed");
            yield return StartCoroutine(WaitRealUntil(() => Speedrun.LeverArmed, 6f));
            Try("SR-LEVER-END", "switch check ran", delegate
            {
                var outro = (OutroLever)UnityEngine.Object.FindObjectOfType(typeof(OutroLever));
                var lever = outro != null ? outro.m_leftLeverScript : null;
                var f = typeof(TeleporterLever).GetField("m_onEnable", BindingFlags.NonPublic | BindingFlags.Instance);
                var ev = lever == null || f == null ? null : f.GetValue(lever) as UnityEngine.Events.UnityEvent<bool>;
                long t1 = Speedrun.Clock();
                if (ev != null) ev.Invoke(true);      // what the lever itself does on the frame it passes 89 degrees
                long t2 = Speedrun.Clock();
                bool ok = runB.State == CoopMod.SrState.Finished && runB.Engine.EndMs >= SrMs(runB, t1) && runB.Engine.EndMs <= SrMs(runB, t2) && runB.Engine.States[4] == SrSplitState.Done;
                SrCheck("SR-LEVER-END", "Any% stops inside the switch's own event, after the first seed (the lever's UnityEvent, as the game fires it)", ok && Speedrun.LeverArmed,
                    "armed=" + Speedrun.LeverArmed + " lever=" + (lever != null) + " event=" + (ev != null) + " state " + runB.State + " end " + runB.Engine.EndMs + " ms (window " + SrMs(runB, t1) + "-" + SrMs(runB, t2) + ")");
            });

            Try("SR-100-END", "100% end check ran", delegate
            {
                var sd = Singleton<SceneProfile>.Instance.m_sceneData;
                if (sd.m_shardCount < 99) sd.m_shardCount = 99;
                SceneProfile.AddShard();
                for (int i = 0; i < 20; i++) { var it = DataBankDatabase.GetItemByIndex(i); if (it != null) DataBankDatabase.UnlockItem(it); }
                SceneProfile.AddAccomplishment("1stEndingViewed");
                SceneProfile.AddAccomplishment("AcceptedNewGamePlus");
                int n = Singleton<SceneProfile>.Instance.m_numberOfSeeds;
                sd.m_seedCount = n - 1;
                bool notYet = hundred.Live;
                long t1 = Speedrun.Clock(); SceneProfile.AddSeed(); long t2 = Speedrun.Clock();
                bool ok = notYet && hundred.State == CoopMod.SrState.Finished && hundred.Engine.EndMs >= SrMs(hundred, t1) && hundred.Engine.EndMs <= SrMs(hundred, t2) && !hundred.Engine.CannotFinish;
                SrCheck("SR-100-END", "100% ends inside the game's AddSeed of the last seed, with 100 crystals and all 20 databank entries done first", ok,
                    "state " + hundred.State + " end " + hundred.Engine.EndMs + " ms (window " + SrMs(hundred, t1) + "-" + SrMs(hundred, t2) + "), crystals " + SceneProfile.CrystalsCollected + ", seeds " + SceneProfile.SeedCollected + "/" + n + ", splits " + SrStates(hundred.Engine));
            });

            yield return StartCoroutine(WaitRealUntil(() => !ass.Live, 12f));
            Try("SR-ASS-END", "All Star Seeds end check ran", delegate
            {
                long viewed = SrMs(ass, Speedrun.FinalViewedTicks), want = viewed + (long)(Speedrun.FinalFadeOut * 1000f);
                bool ok = ass.State == CoopMod.SrState.Finished && Speedrun.FinalViewedTicks != 0 && Math.Abs(ass.Engine.EndMs - want) <= 150;
                SrCheck("SR-ASS-END", "All Star Seeds ends when the fade after the last seed is complete (FinalSequenceViewed + the fade-out time, on the camera's cut to the computer)", ok,
                    "state " + ass.State + " end " + ass.Engine.EndMs + " ms, final sequence at " + viewed + " ms + fade " + Speedrun.FinalFadeOut + " s = " + want + " ms, by " + Speedrun.FinalBy);
            });

            if (Singleton<PauseManager>.Exists) PauseManager.ResetLevel();      // the game's own Save & Quit
            yield return StartCoroutine(WaitRealUntil(() => !Speedrun.SlotActive && Singleton<StartScreen>.Exists && !WorldSync.InGameOrLoading(), 90f));
            var recB = SpeedrunRecords.Load(recFile);
            string lineB = SrLastRunLine(Speedrun.RunsLog);
            bool pbOk = recBefore.PbMs >= 0 && recBefore.PbMs <= runB.Engine.EndMs ? recB.PbMs == recBefore.PbMs : recB.PbMs == runB.Engine.EndMs;
            SrCheck("SR-RECORDS-LIVE", "the finished run is logged as FINISHED and the records file holds it as the PB (attempts and finishes counted)",
                runB.State == CoopMod.SrState.Finished && pbOk && recB.Finished == recBefore.Finished + 1 && recB.Attempts == recBefore.Attempts + 2 && lineB.Contains("|any|solo|FINISHED|VALID|"),
                "pb " + recB.PbMs + " (run " + runB.Engine.EndMs + ", before " + recBefore.PbMs + ") finished " + recB.Finished + " attempts " + recB.Attempts + "; log: " + lineB);
            bool backB = SaveSystem.m_playerSavePath == ownP && SaveSystem.m_levelFilesPath == ownL;
            bool isoB = SameFiles(before, Snapshot(root, sr)) && SrListing(backups) == backupsBefore && Speedrun.LastMainSaveCheck == "same" && backB;
            SrCheck("SR-SLOT-ISOLATION", "two slot runs (forced level, profile and autosave writes, a reset, a finish, Save & Quit) leave my own save files, profile and backups byte-identical, and the second run wiped the slot first",
                isoA && isoB && wiped, "run A: in slot=" + inSlot + " wrote there=" + slotWrote + " same=" + isoA + "; run B: same=" + isoB + " slot wiped=" + wiped + "; " + before.Count + " files compared");

            RestoreSkinFile(skinFile, skinExisted, skinOld);
            Speedrun.Category = catBefore; Speedrun.Mode = modeBefore;
            yield return StartCoroutine(WaitRealUntil(() => StartMenuReady() && CoopMenu.StartMenuShowing() && !FadeScreen.FadeInProgress, 60f));
            yield return StartCoroutine(WaitReal(1f));
        }

        static List<string> LabelsOf(CoopMenuHandler h) { var l = new List<string>(); foreach (var t in h.RowTextsForTest) if (t != null) l.Add(t.text); return l; }

        static void RestoreSkinFile(string file, bool existed, string old)
        {
            try { if (existed) File.WriteAllText(file, old); else if (File.Exists(file)) File.Delete(file); } catch (Exception) { }
        }
    }
}
