using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace CoopMod
{
    // Speedrun mode: a real-time (RTA) timer that starts on the frame a new game is started and stops on each category's own end, with splits,
    // live +/- against the personal best, and an isolated save slot so a run can never touch the owner's own save.
    // speedrun.com rules: Any% / Any% NG+ end when the computer switch is pulled after the first Star Seed; All Star Seeds ends when the fade
    // after the last seed is fully black; 100% ends on the last seed delivered with all 100 crystals and the whole databank done.
    //
    // Layers: SpeedTimer, SplitEngine and SrCategories are pure (no Unity, testable with a fake clock). Speedrun is the live part: the save slot,
    // the game hooks, the end detectors, validity and the menu. Part 2 (race, co-op run) plugs in through the members marked EXTENSION.

    public enum SrCategory { Any = 0, AnyNg = 1, Hundred = 2, AllSeeds = 3 }
    public enum SrMode { Solo = 0, Race = 1, Coop = 2 }
    public enum SrState { Running = 0, Finished = 1, Reset = 2, Dnf = 3, Crashed = 4, CannotFinish = 5 }
    public enum SrEvKind { Accomplishment = 0, Shard = 1, Seed = 2, DataBank = 3, LeverEnd = 4, FinalFadeEnd = 5 }
    public enum SrSplitState { Pending = 0, Done = 1, Skipped = 2 }
    public enum SplitRule { Acc, Crystals, DataBank, Seeds, Lever, FinalFade, HundredEnd }

    public delegate long SrClock();

    // One thing that happened in the game, stamped inside the game's own callback, with the counters as they were at that moment.
    public struct SrEvent
    {
        public SrEvKind Kind; public string Name; public long Ticks; public int Frame;
        public bool HasState; public float Crystals; public int Seeds, DataBank;
        public int Arg;          // a databank entry: its number (0-19), else -1
    }

    // ---- the clock (pure) ------------------------------------------------------------------------------------------------------------

    // Real time from the high-resolution counter: never paused, so loads, menus with the game stopped and alt-tab all count (RTA).
    public class SpeedTimer
    {
        public static readonly long Frequency = Stopwatch.Frequency;
        public static long SystemNow() { return Stopwatch.GetTimestamp(); }

        readonly SrClock clock;
        public long StartTicks, StopTicks;
        public bool Started, Stopped;

        public SpeedTimer(SrClock c) { clock = c ?? SystemNow; }
        public long Now() { return clock(); }
        public void StartAt(long t) { StartTicks = t; Started = true; Stopped = false; }
        public void StopAt(long t) { if (!Started || Stopped) return; StopTicks = Math.Max(t, StartTicks); Stopped = true; }
        public long MsAt(long t) { if (!Started) return 0; return ToMs((Stopped ? StopTicks : t) - StartTicks); }
        public long ElapsedMs { get { return MsAt(Now()); } }

        // split in two so 30 hours of ticks never overflow
        public static long ToMs(long ticks) { if (ticks <= 0) return 0; return ticks / Frequency * 1000 + ticks % Frequency * 1000 / Frequency; }
        public static long FromMs(long ms) { if (ms <= 0) return 0; return ms / 1000 * Frequency + ms % 1000 * Frequency / 1000; }

        // 0:12.34, 12:34.56, 1:02:03.45
        public static string Format(long ms)
        {
            if (ms < 0) ms = 0;
            long cs = ms / 10, h = cs / 360000, m = cs / 6000 % 60, s = cs / 100 % 60, c = cs % 100;
            return h > 0 ? h + ":" + m.ToString("00") + ":" + s.ToString("00") + "." + c.ToString("00")
                         : m + ":" + s.ToString("00") + "." + c.ToString("00");
        }

        // +1.25, -12.40, +1:02.34
        public static string Delta(long ms)
        {
            string sign = ms < 0 ? "-" : "+";
            long a = Math.Abs(ms), cs = a / 10;
            if (cs < 6000) return sign + (cs / 100) + "." + (cs % 100).ToString("00");
            return sign + Format(a);
        }
    }

    // ---- categories and their splits (pure) -------------------------------------------------------------------------------------------

    public class SplitDef
    {
        public string Id, Label, Name; public SplitRule Rule; public int Arg; public bool IsEnd;
        public SplitDef(string id, string label, SplitRule rule, string name, int arg, bool end) { Id = id; Label = label; Rule = rule; Name = name; Arg = arg; IsEnd = end; }
    }

    public static class SrCategories
    {
        public const int MaxSplits = 16;
        public static readonly SrCategory[] All = { SrCategory.Any, SrCategory.AnyNg, SrCategory.Hundred, SrCategory.AllSeeds };

        public static string Label(SrCategory c)
        {
            switch (c) { case SrCategory.AnyNg: return "ANY% NG+"; case SrCategory.Hundred: return "100%"; case SrCategory.AllSeeds: return "ALL STAR SEEDS"; default: return "ANY%"; }
        }
        public static string FileKey(SrCategory c)
        {
            switch (c) { case SrCategory.AnyNg: return "anyng"; case SrCategory.Hundred: return "100"; case SrCategory.AllSeeds: return "ass"; default: return "any"; }
        }
        public static bool TryParseKey(string key, out SrCategory c)
        {
            foreach (var x in All) if (FileKey(x) == key) { c = x; return true; }
            c = SrCategory.Any; return false;
        }
        public static string ModeLabel(SrMode m) { return m == SrMode.Race ? "RACE" : m == SrMode.Coop ? "CO-OP RUN" : "SOLO"; }
        public static bool Unordered(SrCategory c) { return c == SrCategory.Hundred; }
        public static int ClampSeeds(int n) { return Math.Max(1, Math.Min(10, n)); }

        static SplitDef Acc(string id, string label, string acc) { return new SplitDef(id, label, SplitRule.Acc, acc, 0, false); }

        // The split list of a category (at most 16). seeds = the level's star seed total (SceneProfile.m_numberOfSeeds, normally 8).
        public static List<SplitDef> Splits(SrCategory c, int seeds)
        {
            int n = ClampSeeds(seeds);
            var l = new List<SplitDef>();
            if (c == SrCategory.Hundred)
            {
                l.Add(Acc("SEED1", "FIRST SEED", "TeleportedSeed"));
                l.Add(Acc("ENDING1", "FIRST ENDING", "1stEndingViewed"));
                l.Add(new SplitDef("CRYSTALS50", "50 CRYSTALS", SplitRule.Crystals, null, 50, false));
                l.Add(new SplitDef("CRYSTALS100", "100 CRYSTALS", SplitRule.Crystals, null, 100, false));
                l.Add(new SplitDef("DATABANK10", "DATABANK 10", SplitRule.DataBank, null, 10, false));
                l.Add(new SplitDef("DATABANK20", "DATABANK 20", SplitRule.DataBank, null, 20, false));
                int half = (n + 1) / 2;
                if (half < n) l.Add(new SplitDef("SEEDS" + half, "SEEDS " + half + "/" + n, SplitRule.Seeds, null, half, false));
                if (n - 1 > half) l.Add(new SplitDef("SEEDS" + (n - 1), "SEEDS " + (n - 1) + "/" + n, SplitRule.Seeds, null, n - 1, false));
                l.Add(new SplitDef("END", "LAST SEED", SplitRule.HundredEnd, null, n, true));
                return l;
            }
            l.Add(Acc("ISLAND2", "ISLAND 2", "TrunkReachedIsland2"));
            l.Add(Acc("ISLAND3", "ISLAND 3", "TrunkReachedIsland3"));
            l.Add(Acc("ASTEROID", "ASTEROID", "TrunkReachedAsteroid"));
            l.Add(Acc("FLOWER", "STAR FLOWER", "FlowerGrowth"));
            l.Add(Acc("SEED1", "FIRST SEED", "TeleportedSeed"));
            if (c == SrCategory.AllSeeds)
            {
                l.Add(Acc("ENDING1", "FIRST ENDING", "1stEndingViewed"));
                for (int k = 1; k < n; k++) l.Add(new SplitDef("SEEDS" + k, "SEEDS " + k + "/" + n, SplitRule.Seeds, null, k, false));
                l.Add(new SplitDef("END", "LAST SEED SENT", SplitRule.FinalFade, null, 0, true));
            }
            else l.Add(new SplitDef("END", "SWITCH", SplitRule.Lever, null, 0, true));
            return l;
        }
    }

    // ---- splits, end and comparison (pure) ---------------------------------------------------------------------------------------------

    public class SplitEngine
    {
        public readonly SrCategory Category;
        public readonly bool Unordered;
        public readonly int SeedTotal;
        public readonly List<SplitDef> Defs;
        public readonly long[] AtMs, PbAt, BestSeg;
        public readonly SrSplitState[] States;
        public readonly List<int> Order = new List<int>();   // the splits in the order they were reached
        public int Current;                                  // ordered: the next split to reach
        public bool Ended, CannotFinish;
        public long EndMs = -1;
        public float LastCrystals = -1f; public int LastSeeds = -1, LastDataBank = -1;   // the counters the last event carried

        public SplitEngine(SrCategory c, int seedTotal)
        {
            Category = c; Unordered = SrCategories.Unordered(c); SeedTotal = SrCategories.ClampSeeds(seedTotal);
            Defs = SrCategories.Splits(c, SeedTotal);
            int n = Defs.Count;
            AtMs = new long[n]; PbAt = new long[n]; BestSeg = new long[n]; States = new SrSplitState[n];
            for (int i = 0; i < n; i++) { AtMs[i] = -1; PbAt[i] = -1; BestSeg[i] = -1; }
        }

        public int Count { get { return Defs.Count; } }
        public int DoneCount { get { int d = 0; foreach (var s in States) if (s == SrSplitState.Done) d++; return d; } }

        bool Matches(SplitDef d, SrEvent e)
        {
            switch (d.Rule)
            {
                case SplitRule.Acc: return e.Kind == SrEvKind.Accomplishment && e.Name == d.Name;
                case SplitRule.Crystals: return e.HasState && e.Crystals >= d.Arg;
                case SplitRule.DataBank: return (e.HasState && e.DataBank >= d.Arg) || (d.Arg >= 20 && e.Kind == SrEvKind.Accomplishment && e.Name == "CompletedDataBank");
                case SplitRule.Seeds: return e.HasState && e.Seeds >= d.Arg;
                case SplitRule.Lever: return e.Kind == SrEvKind.LeverEnd;
                case SplitRule.FinalFade: return e.Kind == SrEvKind.FinalFadeEnd;
                case SplitRule.HundredEnd: return e.Kind == SrEvKind.Seed && e.HasState && AllDone(e);
            }
            return false;
        }

        // the 100% set: every seed delivered, 100 crystals, all 20 databank entries
        bool AllDone(SrEvent e) { return e.Seeds >= SeedTotal && e.Crystals >= 100f && e.DataBank >= 20; }

        // Feeds one event at its time; the splits it reached go into 'changed' (a skipped split as its index too). Returns how many changed.
        public int Feed(SrEvent e, long ms, List<int> changed)
        {
            if (Ended) return 0;
            if (e.HasState) { LastCrystals = e.Crystals; LastSeeds = e.Seeds; LastDataBank = e.DataBank; }
            int before = changed != null ? changed.Count : 0;
            if (Unordered)
            {
                for (int i = 0; i < Defs.Count && !Ended; i++)
                {
                    if (States[i] != SrSplitState.Pending || !Matches(Defs[i], e)) continue;
                    Reach(i, ms, changed);
                }
                // the rules time 100% on a seed only: the last seed sent before everything else is done can never finish this run
                if (!Ended && e.Kind == SrEvKind.Seed && e.HasState && e.Seeds >= SeedTotal && !AllDone(e)) CannotFinish = true;
            }
            else
            {
                for (int i = Current; i < Defs.Count && !Ended; i++)
                {
                    if (!Matches(Defs[i], e)) continue;
                    for (int j = Current; j < i; j++) if (States[j] == SrSplitState.Pending) { States[j] = SrSplitState.Skipped; if (changed != null) changed.Add(j); }
                    Reach(i, ms, changed);
                    Current = i + 1;
                }
            }
            return changed != null ? changed.Count - before : 0;
        }

        void Reach(int i, long ms, List<int> changed)
        {
            // the end: whatever was not reached is skipped first, so the last split reported is the end itself
            if (Defs[i].IsEnd)
                for (int j = 0; j < Defs.Count; j++) if (j != i && States[j] == SrSplitState.Pending) { States[j] = SrSplitState.Skipped; if (changed != null) changed.Add(j); }
            States[i] = SrSplitState.Done; AtMs[i] = ms; Order.Add(i);
            if (changed != null) changed.Add(i);
            if (Defs[i].IsEnd) { Ended = true; EndMs = ms; }
        }

        // the time since the split reached before this one (the run start for the first); -1 when not reached
        public long SegmentMs(int i)
        {
            if (i < 0 || i >= Defs.Count || States[i] != SrSplitState.Done) return -1;
            int at = Order.IndexOf(i);
            return at <= 0 ? AtMs[i] : AtMs[i] - AtMs[Order[at - 1]];
        }

        // A segment counts as a clean segment for best segments only when the split right before it (ordered) was reached too.
        public bool CleanSegment(int i)
        {
            if (SegmentMs(i) < 0) return false;
            if (Unordered || i == 0) return true;
            return States[i - 1] == SrSplitState.Done;
        }

        public long DeltaMs(int i) { return i >= 0 && i < Defs.Count && States[i] == SrSplitState.Done && PbAt[i] >= 0 ? AtMs[i] - PbAt[i] : long.MinValue; }
        public bool Gold(int i) { return CleanSegment(i) && BestSeg[i] >= 0 && SegmentMs(i) < BestSeg[i]; }

        public long SumOfBest()
        {
            long sum = 0;
            for (int i = 0; i < Defs.Count; i++) { if (BestSeg[i] < 0) return -1; sum += BestSeg[i]; }
            return sum;
        }

        // The live comparison while the timer runs: behind as soon as the next split's PB time has passed, else the last split's delta.
        public long LiveDelta(long nowMs)
        {
            if (!Unordered && Current < Defs.Count && PbAt[Current] >= 0 && nowMs > PbAt[Current]) return nowMs - PbAt[Current];
            for (int k = Order.Count - 1; k >= 0; k--) { long d = DeltaMs(Order[k]); if (d != long.MinValue) return d; }
            return long.MinValue;
        }
    }

    // ---- one run -----------------------------------------------------------------------------------------------------------------------

    public class SrRun
    {
        public SrCategory Category; public SrMode Mode; public uint RunId;
        public SpeedTimer Timer; public SplitEngine Engine;
        public SrState State = SrState.Running;
        public readonly List<string> Invalid = new List<string>();
        public string StartedAt = "", EndReason = "";
        public int StartFrame; public float StartCrystals = -1f;
        public bool Shadow;          // a test-only tracker fed the same events: never logged or recorded
        public bool EnteredWorld;    // the game got going (a later trip back to the start screen is a reset)
        public bool NewGameAtStart;  // the game itself said "new game" on the start frame
        public bool NewPb;
        public float FinishedAtRealtime = -1f;

        public SrRun(SrCategory c, SrMode m, int seeds, SrClock clock)
        {
            Category = c; Mode = m; Timer = new SpeedTimer(clock); Engine = new SplitEngine(c, seeds);
        }

        public bool Valid { get { return Invalid.Count == 0; } }
        public bool Live { get { return State == SrState.Running || State == SrState.CannotFinish; } }
        public long ElapsedMs { get { return Timer.ElapsedMs; } }
        public long FinalMs { get { return State == SrState.Finished ? Engine.EndMs : Timer.ElapsedMs; } }
        public void MarkInvalid(string why) { if (!string.IsNullOrEmpty(why) && !Invalid.Contains(why)) Invalid.Add(why); }

        public void Feed(SrEvent e, List<int> changed)
        {
            if (!Live) return;
            Engine.Feed(e, Timer.MsAt(e.Ticks), changed);
            if (Engine.CannotFinish && State == SrState.Running) { State = SrState.CannotFinish; MarkInvalid("CANNOT FINISH: LAST SEED SENT EARLY"); }
            if (Engine.Ended) { State = SrState.Finished; Timer.StopAt(e.Ticks); FinishedAtRealtime = Time.realtimeSinceStartup; }
        }
    }

    // ---- the live speedrun mode --------------------------------------------------------------------------------------------------------

    public static class Speedrun
    {
        public static SrCategory Category = SrCategory.Any;
        public static SrMode Mode = SrMode.Solo;
        public static bool SplitsShown = true, TimerHidden, LiveSplitOn, Ghosts = true;
        public static SrRun Current;                               // the run on screen (also after it ended, until the next one)
        public static readonly List<SrRun> Shadows = new List<SrRun>();
        public static SrClock Clock = SpeedTimer.SystemNow;
        public static string Status = "";
        public static string LastMainSaveCheck = "";               // "same" or what changed, after the slot was left
        public static bool LeverArmed;
        public static string FinalBy = "";                         // which All Star Seeds detector ended a run: "camera" or "fallback"
        public static long FinalViewedTicks;
        public static float FinalFadeOut;

        // EXTENSION (part 2): a race or co-op run keeps the slot while it waits on the start screen; isolation and the starters for the other modes
        public static bool HoldSlot;
        public delegate string ModeStarter(SrCategory cat);          // returns null when started, else what to tell the player
        public static ModeStarter RaceStarter, CoopStarter;
        public delegate void RunChanged(SrRun run, string what, int split);   // what: start, split, skip, end
        public static RunChanged Changed;
        public static uint ForcedRunId;                             // a race or co-op run: every PC times the run under the leader's run number
        public static Action<SrEvent> Tap;                          // a co-op member: what happens in my copy of the leader's world goes to the leader

        public static bool SlotActive { get; private set; }
        static string ownPlayerPath, ownLevelsPath, exeDir;
        static string mainHashBefore;
        static bool inited, starting, restartPending;
        static float startingSince, restartSince, nextHook, nextValidity;
        static int newGameScreenId;                                 // the start screen whose new game already started a run
        static readonly List<int> changedScratch = new List<int>();
        static readonly List<bool[]> dataBankMemory = new List<bool[]>();

        public static string SrRoot { get { return SaveGuard.Root == null ? null : Path.Combine(SaveGuard.Root, "speedrun"); } }
        public static string SlotDir { get { return SrRoot == null ? null : Path.Combine(SrRoot, "slot"); } }
        static string MarkerFile { get { return Path.Combine(SrRoot, "running.txt"); } }
        public static string RunsLog { get { return Path.Combine(SrRoot, "runs.log"); } }
        static string SettingsFile { get { return Path.Combine(exeDir ?? ".", "coop-speedrun.txt"); } }

        static void Log(string m) { CoopBehaviour.Log("speedrun: " + m); }

        // ---- start-up ----------------------------------------------------------------------------------------------------------------

        public static void Init(string exeFolder)
        {
            exeDir = exeFolder;
            LoadSettings();
            try
            {
                if (SrRoot != null && File.Exists(MarkerFile))
                {
                    // the game closed in the middle of a run: the paths were only changed in memory, so my own save was never pointed at the slot
                    string line = File.ReadAllText(MarkerFile).Trim();
                    AppendRunsLog(line.Replace("|RUNNING|", "|CRASHED|"));
                    File.Delete(MarkerFile);
                    Log("the last run never ended (the game closed); logged as CRASHED");
                }
            }
            catch (Exception e) { Log("crash marker: " + e.GetType().Name); }
            Hook();
            inited = true;
            LiveSplit.Enabled = LiveSplitOn;
        }

        static readonly SceneProfile.StringDelegate accHook = OnAccomplishment;
        static readonly SceneProfile.OnAddShardDelegate shardHook = OnShard;
        static readonly SceneProfile.OnAddSeedDelegate seedHook = OnSeed;
        static readonly DataBankDatabase.OnDataBankItemScanned bankHook = OnDataBank;

        // Remove first, then add: never subscribed twice, and back in should anything ever clear the game's lists.
        static void Hook()
        {
            SceneProfile.m_OnNewAccomplishment = (SceneProfile.StringDelegate)Delegate.Combine(Delegate.Remove(SceneProfile.m_OnNewAccomplishment, accHook), accHook);
            SceneProfile.m_addShardDelegate = (SceneProfile.OnAddShardDelegate)Delegate.Combine(Delegate.Remove(SceneProfile.m_addShardDelegate, shardHook), shardHook);
            SceneProfile.m_addSeedDelegate = (SceneProfile.OnAddSeedDelegate)Delegate.Combine(Delegate.Remove(SceneProfile.m_addSeedDelegate, seedHook), seedHook);
            DataBankDatabase.m_onDataBankItemScanned = (DataBankDatabase.OnDataBankItemScanned)Delegate.Combine(Delegate.Remove(DataBankDatabase.m_onDataBankItemScanned, bankHook), bankHook);
        }

        // ---- settings ----------------------------------------------------------------------------------------------------------------

        static void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;
                foreach (var raw in File.ReadAllLines(SettingsFile))
                {
                    int eq = raw.IndexOf('='); if (eq <= 0) continue;
                    string k = raw.Substring(0, eq).Trim(), v = raw.Substring(eq + 1).Trim();
                    SrCategory c;
                    if (k == "category" && SrCategories.TryParseKey(v, out c)) Category = c;
                    else if (k == "mode") Mode = v == "race" ? SrMode.Race : v == "coop" ? SrMode.Coop : SrMode.Solo;
                    else if (k == "splits") SplitsShown = v != "0";
                    else if (k == "livesplit") LiveSplitOn = v == "1";
                    else if (k == "ghosts") Ghosts = v != "0";
                    else if (k == "timer") TimerHidden = v == "0";
                }
            }
            catch (Exception e) { Log("settings not read: " + e.GetType().Name); }
        }

        public static void SaveSettings()
        {
            try
            {
                File.WriteAllText(SettingsFile, "category=" + SrCategories.FileKey(Category) + "\r\nmode=" + (Mode == SrMode.Race ? "race" : Mode == SrMode.Coop ? "coop" : "solo")
                    + "\r\nsplits=" + (SplitsShown ? 1 : 0) + "\r\nlivesplit=" + (LiveSplitOn ? 1 : 0) + "\r\nghosts=" + (Ghosts ? 1 : 0) + "\r\ntimer=" + (TimerHidden ? 0 : 1) + "\r\n");
            }
            catch (Exception e) { Log("settings not saved: " + e.GetType().Name); }
        }

        // ---- the save slot -----------------------------------------------------------------------------------------------------------

        static bool AtStart { get { return Singleton<StartScreen>.Exists && !WorldSync.InGameOrLoading(); } }
        public static bool StartScreenIdle { get { return AtStart && !FadeScreen.FadeInProgress && !starting && (CoopMenu.StartMenuShowing() || CoopMenu.PartyMenuShowing()); } }
        public static bool StartingNow { get { return starting; } }
        // EXTENSION: no co-op world reaches a solo or race run
        public static bool Isolated { get { return SlotActive && Mode != SrMode.Coop; } }

        // Both save folders really point into the slot (full paths compared), the only state in which a speedrun may start.
        public static bool SlotPathsActive()
        {
            try
            {
                if (!SlotActive || SlotDir == null || SaveSystem.m_playerSavePath == null || SaveSystem.m_levelFilesPath == null) return false;
                char[] seps = { '\\', '/' };
                string s = Path.GetFullPath(SlotDir).TrimEnd(seps);
                string p = Path.GetFullPath(SaveSystem.m_playerSavePath).TrimEnd(seps), l = Path.GetFullPath(SaveSystem.m_levelFilesPath).TrimEnd(seps);
                return p.Equals(s, StringComparison.OrdinalIgnoreCase) && l.StartsWith(s + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        // Point every save at a fresh slot folder. Only on the start screen, never in guest mode or while a game starts. Null when done, else why not.
        public static string EnterSlot(SrCategory cat)
        {
            if (SlotActive) return null;
            if (SrRoot == null) return "NO SAVE FOLDER";
            if (!AtStart || WorldSync.StartingNow()) return "ONLY ON THE START SCREEN";
            if (WorldSync.GuestMode) return "YOUR OWN SAVE IS NOT BACK YET, TRY AGAIN";
            string slot = SlotDir;
            string p0 = SaveSystem.m_playerSavePath, l0 = SaveSystem.m_levelFilesPath;
            try
            {
                mainHashBefore = MainSaveHash(p0, l0);
                if (Directory.Exists(slot)) Directory.Delete(slot, true);
                Directory.CreateDirectory(Path.Combine(slot, "Levels"));
                string profile = Path.Combine(p0, "PlayerProfile.sav");
                if (File.Exists(profile)) File.Copy(profile, Path.Combine(slot, "PlayerProfile.sav"), true);   // settings come along; Any% loses the unlocks below
                ownPlayerPath = p0; ownLevelsPath = l0;
                RememberDataBank();
                SaveSystem.m_playerSavePath = slot;
                SaveSystem.m_levelFilesPath = Path.Combine(slot, "Levels");
                SaveSystem.LoadPlayerProfile();
                if (cat == SrCategory.Any) ClearSkinUnlocks();
                SlotActive = true;
                Log("save slot ready (" + SrCategories.Label(cat) + "): saves go to " + slot + "; my own save is not touched");
            }
            catch (Exception e)
            {
                SaveSystem.m_playerSavePath = p0; SaveSystem.m_levelFilesPath = l0; ownPlayerPath = null; ownLevelsPath = null;
                try { SaveSystem.LoadPlayerProfile(); } catch (Exception) { }
                Log("save slot not made: " + e.GetType().Name + " " + e.Message);
                return "COULD NOT MAKE THE SPEEDRUN SAVE";
            }
            CoopMenu.RefreshStartMenu();
            return null;
        }

        // Back to my own saves: only on the start screen, never in a game or while one is starting.
        public static bool LeaveSlot(string why)
        {
            if (!SlotActive || ownPlayerPath == null || ownLevelsPath == null) return false;
            if (WorldSync.InGameOrLoading() || starting) return false;
            SaveSystem.m_playerSavePath = ownPlayerPath; SaveSystem.m_levelFilesPath = ownLevelsPath;
            SlotActive = false; HoldSlot = false; LeverArmed = false; ForcedRunId = 0;
            RestoreDataBank();
            try { SaveSystem.LoadPlayerProfile(); } catch (Exception e) { Log("reading my own profile again: " + e.GetType().Name); }
            string after = MainSaveHash(ownPlayerPath, ownLevelsPath);
            LastMainSaveCheck = after == mainHashBefore ? "same" : "CHANGED";
            if (after != mainHashBefore) { Log("WARNING my own save files changed during the speedrun (" + mainHashBefore + " -> " + after + ")"); CoopUI.Toast("WARNING: YOUR OWN SAVE CHANGED DURING THE RUN"); }
            Log("left the save slot (" + why + "); saves are mine again (" + ownPlayerPath + "), my own save files unchanged: " + (after == mainHashBefore));
            CoopUI.Toast("BACK TO YOUR OWN SAVE");
            CoopMenu.RefreshStartMenu();
            return true;
        }

        public static string OwnPlayerPathForTest { get { return ownPlayerPath; } }
        public static string OwnLevelsPathForTest { get { return ownLevelsPath; } }

        static string MainSaveHash(string playerPath, string levelsPath)
        {
            try
            {
                var sha = System.Security.Cryptography.SHA1.Create();
                string s = "";
                foreach (var f in new[] { Path.Combine(playerPath ?? "", "PlayerProfile.sav"), Path.Combine(levelsPath ?? "", Application.loadedLevelName + ".sav") })
                    s += File.Exists(f) ? BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(f))).Replace("-", "").Substring(0, 12) : "none";
                return s;
            }
            catch (Exception e) { return "unreadable " + e.GetType().Name; }
        }

        // The databank (and with it the skins) lives in memory across levels: kept as it was before the slot and put back after it.
        static void RememberDataBank()
        {
            dataBankMemory.Clear();
            try { foreach (var it in AssetSingleton<DataBankDatabase>.Instance.m_dataBankItems) dataBankMemory.Add(new[] { it.m_unlocked, it.m_viewed }); }
            catch (Exception) { dataBankMemory.Clear(); }
        }

        static void RestoreDataBank()
        {
            try
            {
                var items = AssetSingleton<DataBankDatabase>.Instance.m_dataBankItems;
                if (items.Count != dataBankMemory.Count) return;
                for (int i = 0; i < items.Count; i++) { items[i].m_unlocked = dataBankMemory[i][0]; items[i].m_viewed = dataBankMemory[i][1]; }
            }
            catch (Exception) { }
            dataBankMemory.Clear();
        }

        // Any%: a fresh profile's unlocks (the settings stay). Only in memory, and only in the slot.
        static void ClearSkinUnlocks()
        {
            var pd = Singleton<PlayerProfile>.Instance.m_playerData;
            pd.m_unlockedNinjaSkin = false; pd.m_unlockedNativeSkin = false; pd.m_ninjaPromptShown = false; pd.m_nativePromptShown = false;
            foreach (var skin in new[] { SceneProfile.CharacterSkin.Ninja, SceneProfile.CharacterSkin.Native })
            {
                var it = DataBankDatabase.GetItemByIndex(20 + (int)skin);
                if (it != null) it.m_unlocked = false;
            }
        }

        public static bool CloudOff()
        {
            try
            {
                var t = Type.GetType("SteamManager, Assembly-CSharp");
                var sm = t == null ? null : UnityEngine.Object.FindObjectOfType(t) as Component;
                if (sm == null) return false;
                return !(bool)t.GetField("m_cloudSavable").GetValue(sm) && !(bool)t.GetField("m_cloudSaveEnabled").GetValue(sm);
            }
            catch (Exception) { return false; }
        }

        // ---- starting a run ----------------------------------------------------------------------------------------------------------

        // A solo run from the menu: the slot, then the game's own NEW GAME. On any refusal the slot is left again at once.
        public static string StartSolo(SrCategory cat, bool inParty)
        {
            if (inParty) return "LEAVE THE PARTY FOR A SOLO RUN";
            if (Current != null && Current.Live) return "A RUN IS ALREADY GOING";
            Category = cat; Mode = SrMode.Solo;
            string why = EnterSlot(cat);
            if (why != null) return why;
            if (!BeginNewGame(out why)) { LeaveSlot("run not started"); return why; }
            return null;
        }

        // The game's NEW GAME in the slot. The timer starts on this frame. False (and nothing started) when the slot or the cloud is not safe.
        public static bool BeginNewGame(out string why)
        {
            why = null;
            if (!SlotPathsActive()) why = "THE SPEEDRUN SAVE IS NOT READY";
            else if (!CloudOff()) why = "STEAM CLOUD SAVING IS STILL ON";
            else if (!AtStart || starting || WorldSync.StartingNow()) why = "ONLY ON THE START SCREEN";
            else if (FadeScreen.FadeInProgress) why = "WAIT FOR THE SCREEN TO SETTLE";
            if (why != null) { Log("run not started: " + why.ToLower()); return false; }
            long t = Clock();
            StartScreen.StartGameCountdown(true);
            bool debug;
            bool fresh = ReadNewGame(out debug);
            newGameScreenId = Singleton<StartScreen>.Instance.GetInstanceID();
            StartRun(t, fresh && !debug);
            return true;
        }

        static FieldInfo newGameField, debugLoadField;
        static bool ReadNewGame(out bool debugLoad)
        {
            debugLoad = false;
            try
            {
                if (!Singleton<StartScreen>.Exists) return false;
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                if (newGameField == null) newGameField = typeof(StartScreen).GetField("m_newGame", flags);
                if (debugLoadField == null) debugLoadField = typeof(StartScreen).GetField("m_debugLoad", flags);
                var ss = Singleton<StartScreen>.Instance;
                debugLoad = debugLoadField != null && (bool)debugLoadField.GetValue(ss);
                return newGameField != null && (bool)newGameField.GetValue(ss);
            }
            catch (Exception) { return false; }
        }

        static void StartRun(long ticks, bool freshNewGame)
        {
            if (Current != null && Current.Live) End(Current, SrState.Reset, "a new run started");
            Shadows.Clear();
            int seeds = Singleton<SceneProfile>.Exists ? Singleton<SceneProfile>.Instance.m_numberOfSeeds : 8;
            var run = new SrRun(Category, Mode, seeds, Clock);
            run.RunId = ForcedRunId != 0 ? ForcedRunId : (uint)(DateTime.UtcNow.Ticks & 0x7FFFFFFF) | 1u;
            coopBank.Clear();
            run.Timer.StartAt(ticks);
            run.StartFrame = Time.frameCount;
            run.StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            run.NewGameAtStart = freshNewGame;
            if (!freshNewGame) run.MarkInvalid("NOT A FRESH NEW GAME");
            try
            {
                var pd = Singleton<PlayerProfile>.Instance.m_playerData;
                string skin = SkinRule(Category, pd.m_unlockedNinjaSkin, pd.m_unlockedNativeSkin, SceneProfile.CharacterSkin.Standard);
                if (skin != null) run.MarkInvalid(skin);
            }
            catch (Exception) { }
            var rec = SpeedrunRecords.Load(RecordFile(Category, Mode));
            SpeedrunRecords.Fill(rec, run.Engine);
            rec.Attempts++;
            SpeedrunRecords.Save(RecordFile(Category, Mode), rec);
            recordCache.Remove(RecordFile(Category, Mode));
            Current = run;
            starting = true; startingSince = Time.realtimeSinceStartup;
            LeverArmed = false; leverHooked = null; outro = null; finalArmed = false; FinalBy = "";
            try { Directory.CreateDirectory(SrRoot); File.WriteAllText(MarkerFile, RunLine(run, "RUNNING")); } catch (Exception) { }
            Log("run started: " + SrCategories.Label(Category) + " " + SrCategories.ModeLabel(Mode) + " (frame " + run.StartFrame + ", new game " + freshNewGame + ", attempt " + rec.Attempts + ")");
            Fire(run, "start", -1);
        }

        // EXTENSION / test: another category timed alongside from the same start (all four end detectors on the same events)
        public static SrRun AddShadow(SrCategory cat)
        {
            if (Current == null) return null;
            var s = new SrRun(cat, Current.Mode, Current.Engine.SeedTotal, Clock) { Shadow = true };
            s.Timer.StartAt(Current.Timer.StartTicks);
            s.StartFrame = Current.StartFrame; s.NewGameAtStart = Current.NewGameAtStart;
            Shadows.Add(s);
            return s;
        }

        // ---- the game's events -------------------------------------------------------------------------------------------------------

        static bool Listening
        {
            get
            {
                if (Current != null && Current.Live) return true;
                if (Tap != null) return true;
                foreach (var s in Shadows) if (s.Live) return true;
                return false;
            }
        }

        // a co-op run counts the databank entries any player of the party scanned (each world has its own databank)
        static readonly HashSet<int> coopBank = new HashSet<int>();

        static int DataBankCount()
        {
            int n = 0;
            bool coop = Current != null && Current.Live && Current.Mode == SrMode.Coop;
            try { for (int i = 0; i < 20; i++) { var it = DataBankDatabase.GetItemByIndex(i); if ((it != null && it.m_unlocked) || (coop && coopBank.Contains(i))) n++; } } catch (Exception) { }
            return n;
        }

        static int DataBankIndex(string uid)
        {
            try { for (int i = 0; i < 20; i++) { var it = DataBankDatabase.GetItemByIndex(i); if (it != null && it.m_UID == uid) return i; } } catch (Exception) { }
            return -1;
        }

        public static SrEvent Snapshot(SrEvKind kind, string name)
        {
            var e = new SrEvent { Kind = kind, Name = name, Ticks = Clock(), Frame = Time.frameCount, Arg = -1 };
            if (Singleton<SceneProfile>.Exists)
            {
                e.HasState = true;
                e.Crystals = SceneProfile.CrystalsCollected;
                e.Seeds = SceneProfile.SeedCollected;
                e.DataBank = DataBankCount();
            }
            return e;
        }

        // EXTENSION: part 2's co-op host feeds the members' reported events through here too
        public static void Feed(SrEvent e)
        {
            if (Current != null && Current.Live) FeedRun(Current, e);
            for (int i = 0; i < Shadows.Count; i++) if (Shadows[i].Live) FeedRun(Shadows[i], e);
            if (Tap != null) { try { Tap(e); } catch (Exception ex) { Log("co-op report: " + ex.GetType().Name); } }
        }

        // (co-op leader) a member reported something from their copy of my world (SrNode checked it is my run and they play in my world).
        // Splits and ends are taken from whoever is first; the engine never takes a split twice.
        public static void CoopMemberEvent(byte kind, int arg, SrCategory cat, int seeds)
        {
            var run = Current;
            if (run == null || !run.Live || run.Mode != SrMode.Coop) return;
            switch (kind)
            {
                case SrMsg.KindSplit:
                    {
                        var defs = run.Engine.Defs;
                        if (arg >= 0 && arg < defs.Count && defs[arg].Rule == SplitRule.Acc) Feed(Snapshot(SrEvKind.Accomplishment, defs[arg].Name));
                    }
                    break;
                case SrMsg.KindDataBank: if (arg >= 0 && arg < 20 && coopBank.Add(arg)) Feed(Snapshot(SrEvKind.DataBank, null)); break;
                case SrMsg.KindLever: Feed(Snapshot(SrEvKind.LeverEnd, "member")); break;
                case SrMsg.KindSeed: { var e = Snapshot(SrEvKind.Seed, null); if (arg > e.Seeds) e.Seeds = arg; Feed(e); } break;
                case SrMsg.KindFinal: Feed(Snapshot(SrEvKind.FinalFadeEnd, "member")); break;
            }
        }

        static void FeedRun(SrRun run, SrEvent e)
        {
            changedScratch.Clear();
            run.Feed(e, changedScratch);
            for (int i = 0; i < changedScratch.Count; i++)
            {
                int k = changedScratch[i];
                Fire(run, run.Engine.States[k] == SrSplitState.Skipped ? "skip" : "split", k);
            }
            if (run.State == SrState.Finished) Finished(run);
            else if (run.State == SrState.CannotFinish && !run.Shadow && !cannotToastShown) { cannotToastShown = true; CoopUI.Toast("RUN INVALID: LAST SEED SENT BEFORE EVERYTHING ELSE"); }
        }
        static bool cannotToastShown;

        static void OnAccomplishment(string name)
        {
            try
            {
                if (!Listening || name == null) return;
                var e = Snapshot(SrEvKind.Accomplishment, name);
                if (name == "FinalSequenceViewed") ArmFinal(e.Ticks);
                Feed(e);
            }
            catch (Exception ex) { Log("accomplishment hook: " + ex.GetType().Name + " " + ex.Message); }
        }

        static void OnShard() { try { if (Listening) Feed(Snapshot(SrEvKind.Shard, null)); } catch (Exception ex) { Log("crystal hook: " + ex.GetType().Name); } }
        static void OnSeed() { try { if (Listening) Feed(Snapshot(SrEvKind.Seed, null)); } catch (Exception ex) { Log("seed hook: " + ex.GetType().Name); } }
        static void OnDataBank(string uid, string bankName) { try { if (Listening) { var e = Snapshot(SrEvKind.DataBank, uid); e.Arg = DataBankIndex(uid); Feed(e); } } catch (Exception ex) { Log("databank hook: " + ex.GetType().Name); } }

        static int hTeleportedSeed = "TeleportedSeed".GetHashCode(), h1stEnding = "1stEndingViewed".GetHashCode();
        static bool Has(int hash) { return Singleton<SceneProfile>.Exists && SceneProfile.HasAccomplishment(hash); }

        // ---- end detectors -----------------------------------------------------------------------------------------------------------

        // Any%: the computer switch. The game wires the left lever's private UnityEvent to OutroLever.OnLeftLeverPull in the scene; we listen to
        // the same event, so the stop lands on the very frame the lever passes its angle. A per-frame look at m_activated is the fallback.
        static OutroLever outro;
        static TeleporterLever leverHooked;
        static bool leverLast;
        static float nextLeverFind;
        static readonly UnityEngine.Events.UnityAction<bool> leverListener = OnLeverEvent;

        static void ArmLever()
        {
            if (outro == null)
            {
                if (Time.realtimeSinceStartup < nextLeverFind) return;
                nextLeverFind = Time.realtimeSinceStartup + 2f;
                outro = (OutroLever)UnityEngine.Object.FindObjectOfType(typeof(OutroLever));
                if (outro == null) return;
            }
            var lever = outro.m_leftLeverScript;
            if (lever == null || lever == leverHooked) return;
            var f = typeof(TeleporterLever).GetField("m_onEnable", BindingFlags.NonPublic | BindingFlags.Instance);
            var ev = f == null ? null : f.GetValue(lever) as UnityEngine.Events.UnityEvent<bool>;
            if (ev != null) { ev.RemoveListener(leverListener); ev.AddListener(leverListener); }
            leverHooked = lever; leverLast = lever.m_activated; LeverArmed = true;
            Log("switch detector armed (event " + (ev != null) + ", lever down already " + leverLast + ")");
        }

        static void OnLeverEvent(bool on)
        {
            try
            {
                if (!on) { leverLast = false; return; }
                leverLast = true;
                LeverEnd("event");
            }
            catch (Exception ex) { Log("switch hook: " + ex.GetType().Name); }
        }

        static void LeverEnd(string by)
        {
            if (!Listening || !Has(hTeleportedSeed) || Has(h1stEnding)) return;
            var e = Snapshot(SrEvKind.LeverEnd, by);
            Feed(e);
        }

        // All Star Seeds: FinalSequence adds FinalSequenceViewed, fades out over sc_respawn.m_fadeOutTime (game time), then snaps the camera to the
        // computer. The end is the frame of that snap; the stamp plus the fade time is the fallback should the snap never be seen.
        static bool finalArmed; static float finalGameTime; static Vector3 finalTarget; static bool finalTargetKnown;
        static int finalCamera;     // 0 = not looked yet, 1 = seen away from the computer since armed, 2 = already there when armed

        static void ArmFinal(long ticks)
        {
            finalArmed = true; FinalViewedTicks = ticks; finalGameTime = Time.time; finalTargetKnown = false; finalCamera = 0;
            FinalFadeOut = 0.5f;
            try { var r = sc_respawn.Instance; if (r != null) FinalFadeOut = r.m_fadeOutTime; } catch (Exception) { }
            try
            {
                if (outro == null) outro = (OutroLever)UnityEngine.Object.FindObjectOfType(typeof(OutroLever));
                if (outro != null && outro.m_cameraPosition != null) { finalTarget = outro.m_cameraPosition.transform.position; finalTargetKnown = true; }
                Log("final sequence seen at frame " + Time.frameCount + " (fade-out " + FinalFadeOut + " s, computer view known " + finalTargetKnown + ", camera " + (Camera.main != null ? (Camera.main.transform.position - finalTarget).magnitude.ToString("F2") + " m away" : "none") + ")");
            }
            catch (Exception) { }
        }

        static void FinalCheck(bool late)
        {
            if (!finalArmed) return;
            if (!Listening) { finalArmed = false; return; }
            var cam = Camera.main;
            bool there = finalTargetKnown && cam != null && (cam.transform.position - finalTarget).sqrMagnitude < 0.0001f;
            if (late && finalCamera == 0) finalCamera = there ? 2 : 1;
            // the cut: the game resumes FinalSequence after WaitForSeconds(fade-out) in the first frame where game time has passed it, and snaps the
            // camera there before LateUpdate; the end camera turned on in the same call can already sit near the computer, so position alone is no proof
            if (late && Time.time - finalGameTime >= FinalFadeOut)
            {
                finalArmed = false; FinalBy = there ? "camera" : "fade time";
                Log("All Star Seeds end by " + FinalBy + " at frame " + Time.frameCount + ", " + (Time.time - finalGameTime).ToString("F3") + " s game time after the final sequence");
                Feed(Snapshot(SrEvKind.FinalFadeEnd, FinalBy));
            }
            else if (Time.time - finalGameTime > FinalFadeOut + 3f)
            {
                finalArmed = false; FinalBy = "fallback";
                var e = Snapshot(SrEvKind.FinalFadeEnd, "fallback");
                e.Ticks = FinalViewedTicks + SpeedTimer.FromMs((long)(FinalFadeOut * 1000f));
                Feed(e);
            }
        }

        // ---- every frame -------------------------------------------------------------------------------------------------------------

        public static void Tick()
        {
            if (!inited) return;
            float now = Time.realtimeSinceStartup;
            if (now >= nextHook) { nextHook = now + 5f; Hook(); }
            bool game = WorldSync.InGameOrLoading();
            if (starting && (game || now - startingSince > 60f)) starting = false;
            var run = Current;
            if (run != null && game) run.EnteredWorld = true;
            if (SlotActive && !game) WatchNativeNewGame();
            if (run != null && run.Live)
            {
                if (run.EnteredWorld && !game) End(run, SrState.Reset, "left the world");
                else if (!run.EnteredWorld && !game && !starting) End(run, SrState.Reset, "the game never started");
            }
            if (game && Listening)
            {
                ArmLever();
                if (leverHooked != null) { bool a = leverHooked.m_activated; if (a && !leverLast) { leverLast = true; LeverEnd("poll"); } else if (!a) leverLast = false; }
                FinalCheck(false);
                if (now >= nextValidity) { nextValidity = now + 0.5f; ValidityTick(); }
            }
            if (pendingRecord != null) { var r = pendingRecord; pendingRecord = null; Record(r); }
            if (SlotActive && !game && !starting && !HoldSlot && !restartPending && Singleton<StartScreen>.Exists && (Current == null || !Current.Live)) LeaveSlot("back on the start screen");
            if (restartPending)
            {
                if (now - restartSince > 90f) restartPending = false;
                else if (!SlotActive && StartScreenIdle) { restartPending = false; string why = StartSolo(Category, false); if (why != null) CoopUI.Toast("RUN NOT STARTED: " + why); }
                else if (SlotActive && !game && !starting && StartScreenIdle) LeaveSlot("restart");
            }
        }

        public static void LateTick() { if (inited && finalArmed) { try { FinalCheck(true); } catch (Exception e) { Log("fade detector: " + e.GetType().Name); } } }

        // A new game started from the game's own start menu while the slot is in use (START OVER in the slot) is a run too.
        static void WatchNativeNewGame()
        {
            if (!Singleton<StartScreen>.Exists) return;
            int id = Singleton<StartScreen>.Instance.GetInstanceID();
            if (id == newGameScreenId) return;
            bool debug;
            if (!ReadNewGame(out debug)) return;
            newGameScreenId = id;
            if (!SlotPathsActive() || !CloudOff()) return;
            StartRun(Clock(), !debug);
        }

        static void ValidityTick()
        {
            var run = Current;
            if (run == null || !run.Live || !Singleton<SceneProfile>.Exists) return;
            string why = SkinRule(run.Category, false, false, SceneProfile.ActiveSkin);
            if (why != null && run.Valid) { run.MarkInvalid(why); CoopUI.Toast("RUN INVALID: " + why); Log("run invalid: " + why); }
            else if (why != null) run.MarkInvalid(why);
        }

        // ---- validity (pure rules) ---------------------------------------------------------------------------------------------------

        // Any%: no skin unlocked on the save and none worn. The always-there skins (Standard, Desert, Oceanic, WhyAye, Facebook) are not unlocks.
        public static string SkinRule(SrCategory c, bool ninjaUnlocked, bool nativeUnlocked, SceneProfile.CharacterSkin active)
        {
            if (c != SrCategory.Any) return null;
            if (ninjaUnlocked || nativeUnlocked) return "ANY%: A SKIN IS UNLOCKED";
            if (active == SceneProfile.CharacterSkin.Ninja || active == SceneProfile.CharacterSkin.Native) return "ANY%: UNLOCKED SKIN WORN";
            return null;
        }

        public static bool AssistInvalidates(SrMode m) { return m != SrMode.Coop; }

        // A mod helper that changes the run (F4, re-join, a world pull): a solo or race run is no longer valid.
        public static void Assist(string why)
        {
            var run = Current;
            if (run == null || !run.Live || !AssistInvalidates(run.Mode)) return;
            bool was = run.Valid;
            run.MarkInvalid("ASSIST: " + why);
            if (was) { CoopUI.Toast("RUN INVALID: " + why); Log("run invalid: assist " + why); }
        }

        // ---- the end of a run --------------------------------------------------------------------------------------------------------

        static SrRun pendingRecord;

        static void Finished(SrRun run)
        {
            if (run.Shadow) return;
            Log("run finished: " + SrCategories.Label(run.Category) + " " + SpeedTimer.Format(run.Engine.EndMs) + (run.Valid ? "" : " (invalid: " + string.Join(", ", run.Invalid.ToArray()) + ")"));
            AppendRunsLog(RunLine(run, run.Valid ? "FINISHED" : "INVALID"));
            DeleteMarker();
            pendingRecord = run;      // the file is written in Tick, never inside the game's own callback
            Fire(run, "end", -1);
        }

        // Ends a live run (reset, DNF): the timer stops now.
        public static void End(SrRun run, SrState state, string why)
        {
            if (run == null || !run.Live) return;
            run.Timer.StopAt(Clock()); run.State = state; run.EndReason = why ?? "";
            if (run.Shadow) return;
            foreach (var s in Shadows) if (s.Live) { s.Timer.StopAt(Clock()); s.State = state; }
            AppendRunsLog(RunLine(run, state == SrState.Dnf ? "DNF" : state == SrState.Crashed ? "CRASHED" : "RESET"));
            DeleteMarker();
            Log("run ended: " + state + " (" + why + ") at " + SpeedTimer.Format(run.FinalMs));
            Fire(run, "end", -1);
        }

        static void Record(SrRun run)
        {
            try
            {
                string file = RecordFile(run.Category, run.Mode);
                var rec = SpeedrunRecords.Load(file);
                run.NewPb = SpeedrunRecords.Apply(rec, run.Engine, run.Valid, DateTime.Now.ToString("yyyy-MM-dd"));
                SpeedrunRecords.Save(file, rec);
                recordCache.Remove(file);
                CoopUI.Toast((run.NewPb ? "NEW PB " : "RUN FINISHED ") + SpeedTimer.Format(run.Engine.EndMs));
            }
            catch (Exception e) { Log("records not saved: " + e.GetType().Name + " " + e.Message); }
        }

        static void DeleteMarker() { try { if (SrRoot != null && File.Exists(MarkerFile)) File.Delete(MarkerFile); } catch (Exception) { } }

        // date|category|mode|state|VALID or reasons|final ms|ID=ms,...
        public static string RunLine(SrRun run, string state)
        {
            var parts = new List<string>();
            for (int i = 0; i < run.Engine.Count; i++) parts.Add(run.Engine.Defs[i].Id + "=" + (run.Engine.States[i] == SrSplitState.Done ? run.Engine.AtMs[i].ToString() : "-"));
            return run.StartedAt + "|" + SrCategories.FileKey(run.Category) + "|" + SrCategories.ModeLabel(run.Mode).ToLower() + "|" + state + "|"
                + (run.Valid ? "VALID" : string.Join("; ", run.Invalid.ToArray())) + "|" + (state == "RUNNING" ? -1 : run.FinalMs) + "|" + string.Join(",", parts.ToArray());
        }

        static void AppendRunsLog(string line)
        {
            try { if (SrRoot == null) return; Directory.CreateDirectory(SrRoot); File.AppendAllText(RunsLog, line + "\r\n"); }
            catch (Exception e) { Log("runs.log not written: " + e.GetType().Name); }
        }

        static void Fire(SrRun run, string what, int split)
        {
            if (run.Shadow) return;
            if (LiveSplitOn) LiveSplit.OnRun(run, what, split);
            if (Changed != null) { try { Changed(run, what, split); } catch (Exception e) { Log("run listener: " + e.GetType().Name); } }
        }

        // ---- records -----------------------------------------------------------------------------------------------------------------

        public static string RecordFile(SrCategory c, SrMode m)
        {
            return Path.Combine(SrRoot ?? ".", (m == SrMode.Coop ? "records-coop-" : "records-") + SrCategories.FileKey(c) + ".txt");
        }

        static readonly Dictionary<string, SrRecord> recordCache = new Dictionary<string, SrRecord>();
        public static SrRecord RecordFor(SrCategory c, SrMode m)
        {
            string f = RecordFile(c, m);
            SrRecord r;
            if (!recordCache.TryGetValue(f, out r)) { r = SpeedrunRecords.Load(f); recordCache[f] = r; }
            return r;
        }

        // ---- the menu (CoopMenu.cs SpeedrunRows) -------------------------------------------------------------------------------------

        public static void FillMenuState(ref CoopMenu.State s)
        {
            s.SrCategory = SrCategories.Label(Category);
            s.SrMode = SrCategories.ModeLabel(Mode);
            var run = Current;
            s.SrActive = SlotActive;
            s.SrRunning = run != null && run.Live && SlotActive;
            s.SrFinished = run != null && !run.Live && SlotActive;
            s.SrRunState = run == null ? "" : run.State.ToString().ToUpper();
            s.SrTime = run == null ? "" : SpeedTimer.Format(run.FinalMs);
            s.SrValid = run == null || run.Valid;
            s.SrInvalidWhy = run == null || run.Valid ? "" : run.Invalid[0];
            var rec = RecordFor(Category, Mode);
            s.SrPb = rec.PbMs >= 0 ? SpeedTimer.Format(rec.PbMs) : "";
            s.SrSplitsShown = SplitsShown; s.SrLiveSplit = LiveSplitOn; s.SrTimerHidden = TimerHidden;
            var node = SpeedrunNet.Node;
            if (node != null)
            {
                s.SrRaceArmed = node.Armed && node.Race.Ph == RaceFsm.Phase.Armed;
                s.SrCountdown = node.Race.Ph == RaceFsm.Phase.Counting;
                s.SrRaceHost = node.Race.IAmHost && node.Race.Active;
                s.SrRaceReady = node.Race.ReadyCount + 1; s.SrRaceTotal = node.Race.Total;
                if (node.Race.Active && !node.Race.Spectating) s.SrCategory = SrCategories.Label(node.Race.Cat);
            }
        }

        public static void MenuAction(string id, bool inParty, bool partyHost)
        {
            switch (id)
            {
                case "sr_cat": Category = SrCategories.All[((int)Category + 1) % SrCategories.All.Length]; SaveSettings(); break;
                case "sr_mode": Mode = (SrMode)(((int)Mode + 1) % 3); SaveSettings(); break;
                case "sr_splits": SplitsShown = !SplitsShown; SaveSettings(); break;
                case "sr_livesplit": LiveSplitOn = !LiveSplitOn; LiveSplit.Enabled = LiveSplitOn; SaveSettings(); CoopUI.Toast(LiveSplitOn ? "LIVESPLIT ON: START ITS TCP SERVER (PORT 16834)" : "LIVESPLIT OFF"); break;
                case "sr_hide": TimerHidden = !TimerHidden; SaveSettings(); break;
                case "sr_start":
                    {
                        string why;
                        if (Mode == SrMode.Race) why = RaceStarter != null ? RaceStarter(Category) : "RACES ARE NOT READY YET";
                        else if (Mode == SrMode.Coop) why = CoopStarter != null ? CoopStarter(Category) : "CO-OP RUNS ARE NOT READY YET";
                        else why = StartSolo(Category, inParty);
                        if (why != null) CoopUI.Toast("RUN NOT STARTED: " + why); else Status = "";
                    }
                    break;
                case "sr_reset": ResetRun(false); break;
                case "sr_restart": ResetRun(true); break;
                case "sr_end": if (Current != null && Current.Live) { End(Current, SrState.Dnf, "ended from the menu"); CoopUI.Toast("RUN ENDED (DNF)"); } break;
                case "sr_go": if (SpeedrunNet.Node != null) SpeedrunNet.Node.HostGoNow(); break;
                case "sr_cancel": if (SpeedrunNet.Node != null) { SpeedrunNet.Node.HostCancel(SrMsg.CancelByHost); CoopUI.Toast("RACE CANCELLED"); } break;
                case "sr_endrace": if (SpeedrunNet.Node != null) { SpeedrunNet.Node.HostCancel(SrMsg.CancelByHost); CoopUI.Toast("RACE ENDED"); } break;
                case "sr_leave": if (SpeedrunNet.Node != null) SpeedrunNet.Node.LeaveRace(); break;
            }
        }

        // RESET RUN: the run counts as reset, then the game's own Save & Quit takes me to the start screen, where the slot is left.
        public static void ResetRun(bool restart)
        {
            if (Current != null && Current.Live) End(Current, SrState.Reset, restart ? "reset and restart" : "reset from the menu");
            if (restart && Current != null && Current.Mode == SrMode.Solo) { restartPending = true; restartSince = Time.realtimeSinceStartup; }
            if (WorldSync.InGameOrLoading() && !SaveSystem.loadingScene && Singleton<PauseManager>.Exists) PauseManager.ResetLevel();
        }
    }
}
