using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

// Audit checks for Safety.cs: the window arguments, Steam being ready, the handoff guard, the single launcher, the window never covering the game,
// and the mod being replaced in one step (see Audit.cs for how they are run).
static partial class Program
{
    static void AuditSafety(string tmp)
    {
        installDir = Path.Combine(tmp, "safety"); gameDir = Path.Combine(installDir, "game");
        Directory.CreateDirectory(Path.Combine(gameDir, "GrowHome_Data", "Managed"));

        // ---- always a window
        string def = LaunchArgs("-screen-width 1280 -screen-height 720 -popupwindow", false, false);
        bool w1 = def == "-screen-width 1280 -screen-height 720 -popupwindow -screen-fullscreen 0";
        bool w2 = LaunchArgs("-screen-width 800 -screen-height 450", false, false).EndsWith("-screen-fullscreen 0");
        bool w3 = LaunchArgs("-screen-fullscreen 1", true, false) == "-screen-fullscreen 1" && LaunchArgs("-x", true, true) == "-x";
        string hung = LaunchArgs("-screen-width 1920 -screen-height 1080 -popupwindow", false, true);
        bool w4 = hung.Contains("-screen-width 1024") && !hung.Contains("-popupwindow") && hung.Contains("-screen-fullscreen 0");
        bool w5 = Regexes(LaunchArgs("-screen-fullscreen 0", false, false), "-screen-fullscreen") == 1;
        string opt = WantedLaunchOption(""), kept = WantedLaunchOption("-windowed -novid");
        bool w6 = opt.Contains("-screen-fullscreen 0") && opt.EndsWith("%command%") && kept.Contains("-windowed -novid -screen-fullscreen 0");
        File.WriteAllText(Path.Combine(gameDir, "coop-hang.txt"), "2026-01-01T00:00:00 frozen");
        bool w7 = TakeHangNote() && !File.Exists(Path.Combine(gameDir, "coop-hang.txt")) && !TakeHangNote();
        Check("LAUNCH-WINDOWED", w1 && w2 && w3 && w4 && w5 && w6 && w7, "the game always starts in a window (also with own arguments and in Steam's launch option), fullscreen only with coop-fullscreen.txt, a small ordinary window after a freeze (the mod's note is read once)",
            def + " | after a freeze: " + hung + " | " + w1 + w2 + w3 + w4 + w5 + w6 + w7);

        // ---- Steam really ready
        int[] steam = { 100 };
        bool s1 = SteamReadyFrom("123", 100, steam, 3, 5) && SteamReadyFrom("123", 0, steam, 1, 5);
        bool s2 = !SteamReadyFrom(null, 100, steam, 3, 500) && !SteamReadyFrom("123", 100, new int[0], 3, 500);
        bool s3 = !SteamReadyFrom("123", 999, steam, 3, 500);                 // the pid Steam wrote is a Steam that closed
        bool s4 = !SteamReadyFrom("123", 100, steam, 0, 10) && SteamReadyFrom("123", 100, steam, 0, 120);
        bool s5 = SteadySeconds(5) >= 8 && SteadySeconds(3600) <= 3;
        Check("STEAM-READY", s1 && s2 && s3 && s4 && s5, "a game starts only when Steam is signed in, its own pid is a running Steam, its web helper is up (or it ran a minute), and a freshly started Steam stayed ready for a while",
            "" + s1 + s2 + s3 + s4 + s5 + "; this PC now: ready=" + SteamReady() + ", pid " + ActiveSteamPid());

        // ---- the regular game that Steam starts in place of a co-op copy that closed
        bool h1 = IsHandoff(true, false, true, false) && !IsHandoff(true, true, true, false) && !IsHandoff(false, false, true, false) && !IsHandoff(true, false, true, true) && !IsHandoff(true, false, false, false);
        bool h2 = false; string hd = "";
        if (Process.GetProcessesByName("GrowHome").Length > 0) hd = "a Grow Home is running: live part skipped";
        else
        {
            Process q = null;
            try
            {
                string regular = Path.Combine(tmp, "handoff-regular", "GrowHome.exe");
                var starter = Bg("AuditSafety worker", () => { Thread.Sleep(1200); q = StartFake(regular, "-n 40 127.0.0.1"); });
                starter.Start();
                int took; var sw = Stopwatch.StartNew();
                var outcome = WaitForMod(ModLoadedCount(), 20, true, out took);
                starter.Join();
                Thread.Sleep(500);
                h2 = outcome == Outcome.GameDied && q != null && q.HasExited && sw.Elapsed.TotalSeconds < 8;
                hd = outcome + " after " + sw.ElapsedMilliseconds + " ms, regular game closed=" + (q != null && q.HasExited);
            }
            finally { try { if (q != null && !q.HasExited) q.Kill(); } catch { } Thread.Sleep(300); }
        }
        Check("LAUNCH-HANDOFF", h1 && h2, "when a direct start ends and Steam brings up the REGULAR game instead, that game is closed again within seconds (a regular game that was already running is never touched)", hd);

        // ---- one launcher at a time
        string name = @"Local\GrowHomeCoopAudit-" + Guid.NewGuid().ToString("N");
        var first = TakeLock(name);
        Mutex second = null, third = null;
        var t = Bg("AuditSafety worker", () => { second = TakeLock(name); }); t.Start(); t.Join();
        if (first != null) { first.ReleaseMutex(); first.Close(); }
        var t2 = Bg("AuditSafety worker", () => { third = TakeLock(name); if (third != null) { third.ReleaseMutex(); third.Close(); } }); t2.Start(); t2.Join();
        Check("SINGLE-INSTANCE", first != null && second == null && third != null && LauncherMutexName.StartsWith(@"Local\"), "a second launcher finds the first one's lock and stops; once the first is gone a new one starts", "first=" + (first != null) + " second=" + (second != null) + " after=" + (third != null));

        // ---- the window never comes up over the game
        bool f1 = WindowAfter(true, false, false, true) == WindowMove.Close && WindowAfter(true, false, false, false) == WindowMove.Close;
        bool f2 = WindowAfter(true, false, true, true) == WindowMove.Nothing && WindowAfter(false, false, true, true) == WindowMove.Nothing;
        bool f3 = WindowAfter(false, false, false, true) == WindowMove.ShowQuiet && WindowAfter(false, false, false, false) == WindowMove.Show;
        bool f4 = WindowAfter(false, true, false, true) == WindowMove.Show;
        Check("NO-FOCUS-STEAL", f1 && f2 && f3 && f4, "a hidden launcher that finished closes without showing itself; a problem shows it without taking the focus from a running game; only a question comes forward", "" + f1 + f2 + f3 + f4);

        // ---- a running game is never called frozen on a guess, and closing it is never the default
        var all = new List<bool>(); for (int i = 0; i < HungSamples; i++) all.Add(false);
        var blip = new List<bool>(all); blip[HungSamples - 1] = true;
        bool k1 = HungVerdict(all) && !HungVerdict(blip) && !HungVerdict(new List<bool> { false, false, false }) && !HungVerdict(null) && HungSamples >= 10;
        bool k2 = false;
        string note = Path.Combine(gameDir, "coop-hang.txt");
        if (Process.GetProcessesByName("GrowHome").Length == 0)
        {
            File.WriteAllText(note, "2026-01-01T00:00:00 frozen for 15 s");      // a note left behind, no co-op game frozen
            k2 = !OurGameHung();
            try { File.Delete(note); } catch { }
        }
        else k2 = true;
        var hook = chooseHook; bool yes = assumeYes; string[] offered = null;
        chooseHook = (q, o) => { offered = o; return 0; };
        bool k3 = !AskCloseFrozen("audit");
        chooseHook = hook;
        bool k4 = offered != null && offered.Length == 2 && offered[1].StartsWith("Close");
        Check("HANG-NO-FALSE-KILL", k1 && k2 && k3 && k4, "the launcher calls the co-op game frozen only after ten seconds of it not responding (a hang note alone or a short load is not enough), and the first answer leaves the game running",
            "" + k1 + k2 + k3 + k4);
        assumeYes = yes;

        // the start's "already running" step: a frozen co-op copy that was closed goes straight on to the start (no question about a REGULAR game
        // that is not there); one left running stops; a real regular game is named as such
        {
            var keepChoose = chooseHook; var keepSay = sayHook;
            var asked = new List<string>(); var said = new System.Text.StringBuilder(); bool closed = false; int frozenAnswer = 1;
            int r1, r2, r3; bool regularNamed1, regularNamed3;
            try
            {
                chooseHook = (q, o) => { asked.Add(q); return o == FrozenAnswers ? frozenAnswer : 1; };
                sayHook = s => said.AppendLine(s);
                var oursOnly = new List<RunningGame> { new RunningGame { Pid = 0, Path = Path.Combine(gameDir, "GrowHome.exe"), Ours = true } };
                frozenAnswer = 1; r1 = DealWithRunning(oursOnly, () => true, () => { closed = true; return true; });
                regularNamed1 = said.ToString().Contains("REGULAR");
                bool once = asked.Count == 1 && asked[0].StartsWith("Close it and start it again");
                frozenAnswer = 0; closed = false; asked.Clear(); said.Length = 0;
                r2 = DealWithRunning(oursOnly, () => true, () => { closed = true; return true; });
                r2 = r2 == 1 && !closed ? 1 : -99;
                asked.Clear(); said.Length = 0;
                var regularOnly = new List<RunningGame> { new RunningGame { Pid = 0, Path = @"C:\Steam\steamapps\common\GrowHome\GrowHome.exe", Ours = false } };
                r3 = DealWithRunning(regularOnly, () => false, () => true);    // "No": nothing started, nothing closed
                regularNamed3 = said.ToString().Contains("REGULAR") && asked.Count == 1;
                Check("HANG-NO-FALSE-KILL", r1 == -1 && once && !regularNamed1 && r2 == 1 && r3 == 1 && regularNamed3,
                    "after the frozen co-op copy is closed the start goes on with no question about a regular game that is not running; leaving it running stops the start; a real regular game is named as one",
                    "closed frozen copy -> " + r1 + " (questions " + asked.Count + ", regular named=" + regularNamed1 + "); left running -> " + r2 + "; regular game, No -> " + r3 + " (named=" + regularNamed3 + ")");
            }
            finally { chooseHook = keepChoose; sayHook = keepSay; }
        }

        // ---- the mod is replaced in one step, never while the game runs
        byte[] v1 = Bytes("mod one"), v2 = Bytes("mod two, a bit longer");
        File.WriteAllBytes(TargetDll, v1); File.WriteAllText(Path.Combine(gameDir, "coop-version.txt"), "1");
        bool a1, a2, a3, a4, a5 = true; string ad = "";
        try
        {
            swapFault = s => { if (s == "written") throw new IOException("audit: power cut after writing"); };
            a1 = !Swap(v2, 2) && SameBytes(TargetDll, v1) && InstalledVersion() == 1 && !File.Exists(TargetDll + ".tmp");
            swapFault = s => { if (s == "dll") throw new IOException("audit: failure between the dll and the version file"); };
            a2 = !Swap(v2, 2) && SameBytes(TargetDll, v1) && InstalledVersion() == 1;
            swapFault = null;
            a3 = Swap(v2, 2) && SameBytes(TargetDll, v2) && SameBytes(TargetDll + ".bak", v1) && InstalledVersion() == 2 && !File.Exists(TargetDll + ".tmp");
        }
        finally { swapFault = null; }
        if (Process.GetProcessesByName("GrowHome").Length > 0) { a4 = false; ad = "a Grow Home is running: the running-game part could not be checked"; }
        else
        {
            Process p = null;
            try
            {
                p = StartFake(Path.Combine(gameDir, "GrowHome.exe"), "-n 40 127.0.0.1");
                byte[] v3 = Bytes("mod three");
                a4 = !Swap(v3, 3) && SameBytes(TargetDll, v2) && InstalledVersion() == 2;
                File.WriteAllBytes(StagedDll, v3); File.WriteAllText(StagedInfo, "3\n" + Sha256(v3));
                ApplyStaged();
                a5 = File.Exists(StagedDll) && File.Exists(StagedInfo) && SameBytes(TargetDll, v2);
                string why = ""; try { Install(Path.Combine(tmp, "no-such-game"), "red"); } catch (Exception e) { why = e.Message; }
                bool refused = why.Contains("co-op game is running");
                a5 &= refused && File.Exists(TargetDll);
            }
            finally { try { if (p != null && !p.HasExited) p.Kill(); } catch { } Thread.Sleep(500); }
            ApplyStaged();
            a5 &= SameBytes(TargetDll, Bytes("mod three")) && InstalledVersion() == 3 && !File.Exists(StagedDll);
        }
        Check("UPDATE-ATOMIC", a1 && a2 && a3 && a4 && a5, "a mod swap that breaks half way leaves the old mod working; a good one keeps .bak; nothing is replaced or reinstalled while the co-op game runs, and a staged update waits for the next start",
            "" + a1 + a2 + a3 + a4 + a5 + " " + ad);

        // ---- deleting the co-op copy: all or nothing
        string copy = Path.Combine(tmp, "copy-in-use");
        Directory.CreateDirectory(Path.Combine(copy, "sub"));
        File.WriteAllText(Path.Combine(copy, "a.txt"), "a"); File.WriteAllText(Path.Combine(copy, "sub", "b.txt"), "b");
        bool d1;
        using (new FileStream(Path.Combine(copy, "sub", "b.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
            d1 = Throws<Exception>(() => DeleteGameCopy(copy)) && File.Exists(Path.Combine(copy, "a.txt")) && File.Exists(Path.Combine(copy, "sub", "b.txt"));
        DeleteGameCopy(copy);
        bool d2 = !Directory.Exists(copy) && Directory.GetDirectories(tmp, "copy-in-use*").Length == 0;
        Check("INSTALL-IN-USE", d1 && d2, "a co-op copy that is still in use is not deleted at all (not half), and is deleted fully once free", "" + d1 + d2);
    }

    static int Regexes(string s, string word) { return System.Text.RegularExpressions.Regex.Matches(s, System.Text.RegularExpressions.Regex.Escape(word)).Count; }
}
