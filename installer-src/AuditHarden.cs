using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

// Audit checks for Harden.cs (see Audit.cs for how they are run). Everything runs in a throw-away folder with fake inputs; the real registry,
// the real Steam and the real game folder are only read.
static partial class Program
{
    static void AuditHarden(string tmp)
    {
        string saveInstall = installDir, saveGame = gameDir;
        try
        {
            installDir = Path.Combine(tmp, "harden"); gameDir = Path.Combine(installDir, "game");
            Directory.CreateDirectory(Path.Combine(gameDir, "GrowHome_Data", "Managed"));
            AuditAppId();
            AuditOnlyCoop(tmp);
            AuditHangDump(tmp);
            AuditModPin();
            AuditSteamStale();
            AuditStartGates();
        }
        finally { installDir = saveInstall; gameDir = saveGame; }
    }

    // ---- STEAM-APPID
    static void AuditAppId()
    {
        string f = AppIdFile(gameDir);
        if (File.Exists(f)) File.Delete(f);
        string a = EnsureAppIdFile(gameDir);
        bool created = a == "created" && File.ReadAllText(f) == AppId;
        File.WriteAllText(f, "220\r\n");
        string b = EnsureAppIdFile(gameDir);
        bool repaired = b.StartsWith("repaired") && File.ReadAllText(f).Trim() == AppId;
        File.WriteAllText(f, AppId + "\r\n");
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc); File.SetLastWriteTimeUtc(f, stamp);
        string c = EnsureAppIdFile(gameDir);
        bool untouched = c == "ok" && File.GetLastWriteTimeUtc(f) == stamp && AppIdOk(gameDir);
        File.Delete(f);
        bool missingNotOk = !AppIdOk(gameDir);
        // a folder that cannot be written: the start stops instead of going on without the file
        string gone = Path.Combine(gameDir, "no-such-folder");
        string d = EnsureAppIdFile(gone);
        bool refused = d.StartsWith("not written") && !AppIdOk(gone);
        Check("STEAM-APPID", created && repaired && untouched && missingNotOk && refused, "before every start the co-op copy gets steam_appid.txt with 323320 (created when missing, repaired when wrong, left alone when right); when it cannot be written the game is not started",
            "missing: " + a + "; wrong: " + b + "; right: " + c + " (untouched=" + untouched + "); unwritable: " + d);
    }

    // ---- LAUNCH-ONLY-COOP
    static void AuditOnlyCoop(string tmp)
    {
        string coop = Path.Combine(tmp, "only-coop", "GrowHomeCoop", "game"), regular = Path.Combine(tmp, "only-coop", "steamapps", "common", "GrowHome");
        bool r1 = GameExeAllowed(Path.Combine(coop, "GrowHome.exe"), coop, regular);
        bool r2 = !GameExeAllowed(Path.Combine(regular, "GrowHome.exe"), coop, regular);
        bool r3 = !GameExeAllowed(Path.Combine(regular, "GrowHome.exe"), regular, regular);          // a co-op folder that IS the regular game's
        bool r4 = !GameExeAllowed(Path.Combine(coop, "other.exe"), coop, regular) && !GameExeAllowed(null, coop, regular) && !GameExeAllowed(Path.Combine(coop, "GrowHome.exe"), null, regular);
        bool r5 = GameExeAllowed(Path.Combine(coop.ToUpperInvariant(), "growhome.EXE"), coop, null);
        bool a1 = AlongsideToClose(true, true, false) && !AlongsideToClose(true, true, true) && !AlongsideToClose(false, true, false) && !AlongsideToClose(true, false, false);
        // the real start refuses a co-op folder that is the regular game's: nothing is started at all
        string keep = gameDir; bool k = regularDirKnown; string kc = regularDirCache;
        bool refusedStart;
        try { gameDir = regular; regularDirKnown = true; regularDirCache = regular; refusedStart = !StartDirect(); }
        finally { gameDir = keep; regularDirKnown = k; regularDirCache = kc; }
        // the whole start and the install refuse a co-op folder that is the regular game's before writing a single file there (steam_appid.txt, the mod, launcher.log)
        bool untouchedRegular = false; string ur = "";
        {
            string keepInstall = installDir; var keepSay = sayHook; var keepChoose = chooseHook;
            string fakeExe = Path.Combine(regular, "GrowHome.exe");
            Directory.CreateDirectory(Path.Combine(regular, "GrowHome_Data", "Managed"));
            File.WriteAllText(fakeExe, "the regular game");
            Func<string[]> listing = () => Directory.GetFileSystemEntries(Path.GetDirectoryName(regular), "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var before = listing();
            int sr = 0; bool installRefused = false;
            try
            {
                gameDir = regular; installDir = Path.GetDirectoryName(regular); regularDirKnown = true; regularDirCache = regular;
                sayHook = s => { }; chooseHook = (q, o) => 0;
                sr = Start(true);
                installRefused = Throws<Exception>(() => Install(null, "red"));
            }
            finally { gameDir = keep; installDir = keepInstall; regularDirKnown = k; regularDirCache = kc; sayHook = keepSay; chooseHook = keepChoose; }
            var after = listing();
            untouchedRegular = sr == 2 && installRefused && before.SequenceEqual(after) && File.ReadAllText(fakeExe) == "the regular game";
            ur = "start -> " + sr + ", install refused=" + installRefused + ", files " + before.Length + " -> " + after.Length + (before.SequenceEqual(after) ? "" : " (" + string.Join(", ", after.Except(before).Select(Path.GetFileName).ToArray()) + " written)");
        }

        bool live = false; string ld = "";
        if (Process.GetProcessesByName("GrowHome").Length > 0) ld = "a Grow Home is running: the live part could not run";
        else
        {
            Process ours = null, other = null;
            string saved = gameDir;
            try
            {
                gameDir = Path.Combine(tmp, "alongside-coop");
                ours = StartFake(Path.Combine(gameDir, "GrowHome.exe"), "-n 30 127.0.0.1");
                var starter = Bg("AuditHarden worker", () => { Thread.Sleep(1000); other = StartFake(Path.Combine(tmp, "alongside-regular", "GrowHome.exe"), "-n 30 127.0.0.1"); });
                starter.Start();
                int took; var sw = Stopwatch.StartNew();
                var outcome = WaitForMod(ModLoadedCount(), 4, true, out took);
                starter.Join(); Thread.Sleep(300);
                live = other != null && other.HasExited && ours != null && !ours.HasExited && outcome == Outcome.NoMod;
                ld = "outcome " + outcome + " after " + sw.ElapsedMilliseconds + " ms; regular closed=" + (other != null && other.HasExited) + ", co-op copy still running=" + (ours != null && !ours.HasExited);
            }
            finally
            {
                gameDir = saved;
                foreach (var p in new[] { ours, other }) try { if (p != null && !p.HasExited) p.Kill(); } catch { }
                Thread.Sleep(300);
            }
        }
        Check("LAUNCH-ONLY-COOP", r1 && r2 && r3 && r4 && r5 && a1 && refusedStart && untouchedRegular && live, "the launcher only ever starts GrowHome.exe in the co-op folder (never the regular game's, never a co-op folder that is the regular one, where the start and the install also write nothing at all), and a regular game that comes up next to the running co-op copy is closed at once",
            "paths " + r1 + r2 + r3 + r4 + r5 + ", rule " + a1 + ", start refused for the regular folder=" + refusedStart + "; regular folder untouched: " + ur + "; live: " + ld);
    }

    // ---- HANG-MINIDUMP
    static void AuditHangDump(string tmp)
    {
        bool ok = false; string detail = "";
        if (Process.GetProcessesByName("GrowHome").Length > 0) { Check("HANG-MINIDUMP", false, "no Grow Home is running during this check", "close the game and run the audit again"); return; }
        Process p = null;
        try
        {
            p = StartFake(Path.Combine(gameDir, "GrowHome.exe"), "-n 30 127.0.0.1");     // a "co-op game" that we pretend is frozen
            var written = new List<string>();
            for (int i = 0; i < 3; i++) { string w = WriteHangDump(p.Id); if (w != null) written.Add(w); Thread.Sleep(30); }
            bool header = written.Count == 3 && written.All(w => !File.Exists(w) || HasMdmp(w));
            var left = Directory.Exists(LogsDir) ? Directory.GetFiles(LogsDir, "hang-*.dmp").OrderBy(x => x, StringComparer.Ordinal).ToList() : new List<string>();
            bool kept2 = left.Count == 2 && SamePath(left[0], written[1]) && SamePath(left[1], written[2]) && left.All(HasMdmp) && left.All(x => new FileInfo(x).Length > 1024);
            // the launcher's own path: every running co-op process gets a dump before anything closes it
            int n = DumpHungGames();
            int after = Directory.GetFiles(LogsDir, "hang-*.dmp").Length;
            bool viaHung = n == 1 && after == 2 && !p.HasExited;
            // a process that is gone: no dump, no half file
            p.Kill(); p.WaitForExit(5000); int deadPid = p.Id;
            bool dead = WriteHangDump(deadPid) == null && Directory.GetFiles(LogsDir, "hang-*.dmp").Length == 2 && Directory.GetFiles(LogsDir, "*-" + deadPid + ".dmp").Length <= 2;
            bool dirOk = SamePath(LogsDir, Path.Combine(installDir, "logs"));
            ok = header && kept2 && viaHung && dead && dirOk;
            detail = written.Count + " dumps written (MDMP header=" + header + "), kept " + left.Count + " newest=" + kept2 + (left.Count > 0 ? " (" + new FileInfo(left[left.Count - 1]).Length / 1024 + " KB)" : "") + "; frozen-game path dumped " + n + "; a closed process gives none=" + dead + "; folder " + LogsDir;
        }
        finally { try { if (p != null && !p.HasExited) p.Kill(); } catch { } Thread.Sleep(300); }
        Check("HANG-MINIDUMP", ok, "when the launcher calls the co-op game frozen it first writes a minidump of it into GrowHomeCoop\\logs (a real MiniDumpWriteDump file), and only the last 2 dumps are kept", detail);
    }

    static bool HasMdmp(string f)
    {
        try { using (var s = File.OpenRead(f)) { var b = new byte[4]; return s.Read(b, 0, 4) == 4 && Encoding.ASCII.GetString(b) == "MDMP"; } } catch { return false; }
    }

    // ---- MOD-PIN
    static void AuditModPin()
    {
        var keepSource = bundledModSource; string keepSha = bundledShaCache;
        byte[] carried = Bytes("the mod this launcher carries, version 15"), old = Bytes("an old build of the mod"), newer = Bytes("a verified update, version 16"), evil = Bytes("a changed mod with a high version number");
        var bad = new List<string>();
        try
        {
            bundledModSource = () => carried; bundledShaCache = null;
            string vf = Path.Combine(gameDir, "coop-version.txt");
            Action<byte[], string> install = (dll, ver) => { File.WriteAllBytes(TargetDll, dll); File.WriteAllText(vf, ver); };

            // the pure rules
            string cs = Sha256(carried), os = Sha256(old), ns = Sha256(newer);
            if (ModPinVerdict(cs, 15, cs, 15, null) != PinVerdict.Ok || ModPinVerdict(cs, 3, cs, 15, null) != PinVerdict.Ok) bad.Add("the carried mod is not accepted");
            if (ModPinVerdict(os, 15, cs, 15, null) != PinVerdict.Replace || ModPinVerdict(os, 14, cs, 15, null) != PinVerdict.Replace || ModPinVerdict("", 15, cs, 15, null) != PinVerdict.Replace) bad.Add("an old or missing mod is not replaced");
            if (ModPinVerdict(ns, 16, cs, 15, ns) != PinVerdict.NewerPinned || ModPinVerdict(ns, 16, cs, 15, os) != PinVerdict.Replace || ModPinVerdict(ns, 16, cs, 15, null) != PinVerdict.NewerFirstSeen) bad.Add("newer-version rules");

            // the real start-up step on a game folder
            install(old, "15");
            bool r1 = EnforceModPin() && SameBytes(TargetDll, carried) && SameBytes(TargetDll + ".bak", old) && InstalledVersion() == BundledVersion;
            if (!r1) bad.Add("an old build with the same version number was not replaced");
            File.Delete(TargetDll + ".bak");
            install(carried, "15"); var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc); File.SetLastWriteTimeUtc(TargetDll, stamp);
            bool r2 = EnforceModPin() && File.GetLastWriteTimeUtc(TargetDll) == stamp && !File.Exists(TargetDll + ".bak");
            if (!r2) bad.Add("the right mod was rewritten");
            // a verified update (through the real swap) is pinned and kept; changing it afterwards gets it replaced
            install(carried, "15");
            bool swapped = Swap(newer, BundledVersion + 1);
            bool r3 = swapped && File.Exists(PinFile) && File.ReadAllText(PinFile).Trim() == ns && EnforceModPin() && SameBytes(TargetDll, newer);
            if (!r3) bad.Add("a verified update was not kept");
            File.WriteAllBytes(TargetDll, evil);
            bool r4 = EnforceModPin() && SameBytes(TargetDll, carried) && InstalledVersion() == BundledVersion && !File.Exists(PinFile);
            if (!r4) bad.Add("a changed mod with a newer version number was used");
            // a newer mod from an update before pins existed: kept once and pinned, then held to that fingerprint
            install(newer, (BundledVersion + 1).ToString()); if (File.Exists(PinFile)) File.Delete(PinFile);
            bool r5 = EnforceModPin() && SameBytes(TargetDll, newer) && File.Exists(PinFile) && File.ReadAllText(PinFile).Trim() == ns;
            File.WriteAllBytes(TargetDll, evil);
            r5 &= EnforceModPin() && SameBytes(TargetDll, carried);
            if (!r5) bad.Add("trust-once of an earlier update");
            // a missing mod is put back by the start, not by a reinstall: the flow (RunFlow and the window's Play button) counts the copy as installed,
            // so the co-op save in it survives
            string managedDir = Path.GetDirectoryName(TargetDll), saves = Path.Combine(gameDir, "coop-saves");
            if (!File.Exists(Path.Combine(gameDir, "GrowHome.exe"))) File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), Path.Combine(gameDir, "GrowHome.exe")); File.WriteAllText(Path.Combine(managedDir, "Assembly-CSharp.dll"), "patched"); File.WriteAllText(Path.Combine(managedDir, "Assembly-CSharp.dll.orig"), "original");
            Directory.CreateDirectory(saves); File.WriteAllText(Path.Combine(saves, "save.dat"), "my co-op save");
            if (File.Exists(TargetDll)) File.Delete(TargetDll);
            bool keepReinstall = cliReinstall; cliReinstall = false;
            bool flowInstalls = NeedsInstall(); cliReinstall = keepReinstall;
            bool windowInstalls; using (var w = new GameWindow(true)) windowInstalls = !w.IsInstalled;
            bool r6 = !flowInstalls && !windowInstalls && EnforceModPin() && SameBytes(TargetDll, carried) && File.ReadAllText(Path.Combine(saves, "save.dat")) == "my co-op save";
            if (!r6) bad.Add("a missing mod was not put back by the start (reinstall chosen: flow=" + flowInstalls + ", window=" + windowInstalls + ")");
            File.Delete(Path.Combine(managedDir, "Assembly-CSharp.dll.orig"));
            if (File.Exists(TargetDll)) File.Delete(TargetDll);
            bool unpatched = NeedsInstall();         // never patched (no .orig) and no mod: that is a real reinstall
            if (!unpatched) bad.Add("a copy that was never patched counts as installed");
            File.WriteAllBytes(TargetDll, carried);
            // while the co-op game runs nothing is replaced and the start stops
            install(old, "15");
            if (Process.GetProcessesByName("GrowHome").Length > 0) bad.Add("a Grow Home is running: the running-game part could not be checked");
            else
            {
                Process p = null;
                try
                {
                    p = StartFake(Path.Combine(gameDir, "GrowHome.exe"), "-n 30 127.0.0.1");
                    bool r7 = !EnforceModPin() && SameBytes(TargetDll, old);
                    if (!r7) bad.Add("the mod was replaced under a running game");
                }
                finally { try { if (p != null && !p.HasExited) p.Kill(); } catch { } Thread.Sleep(300); }
            }
        }
        finally { bundledModSource = keepSource; bundledShaCache = keepSha; }
        // the launcher really carries a mod and its fingerprint is what it pins
        byte[] real = ReadResource("CoopMod.dll");
        bool carries = real != null && real.Length > 100000 && BundledModSha() == Sha256(real);
        if (!carries) bad.Add("this launcher carries no mod");
        Check("MOD-PIN", bad.Count == 0, "before every start the mod in the game folder must be the one this launcher carries (or a verified update it pinned): an old build, a changed file or a missing one is replaced (old one kept as .bak; a missing one by the start, never by a reinstall that would delete the co-op save), never while the co-op game runs",
            bad.Count == 0 ? "carried mod " + BundledModSha().Substring(0, 12) + " version " + BundledVersion + "; 8 cases ok (a missing mod is put back by the start, the co-op save kept)" : string.Join("; ", bad.ToArray()));
    }

    // ---- STEAM-STALE
    static void AuditSteamStale()
    {
        int[] steam = { 100, 101 };
        bool s1 = SteamStateFrom(steam, 100, 600, true) == SteamState.Ready && SteamStateFrom(steam, 101, 1, false) == SteamState.Ready;
        bool s2 = SteamStateFrom(steam, 0, 600, true) == SteamState.Stale;                 // the case found on 2026-10-30: Steam up and Logged On, ActiveProcess pid 0
        bool s3 = SteamStateFrom(steam, 999, 600, true) == SteamState.Stale;               // the pid of a Steam that closed
        bool s4 = SteamStateFrom(steam, 0, 5, true) == SteamState.Starting && SteamStateFrom(new int[0], 0, 1e9, true) == SteamState.NotRunning && SteamStateFrom(null, 100, 1e9, true) == SteamState.NotRunning;
        // a Steam that is still starting is never stale: updating or verifying (no web helper yet), on its login screen, a minute-long start,
        // and the pid a crashed Steam left behind while the new one starts
        bool s6 = SteamStateFrom(steam, 0, 40, false) == SteamState.Starting && SteamStateFrom(steam, 0, 600, false) == SteamState.Starting
            && SteamStateFrom(steam, 999, 30, false) == SteamState.Starting && SteamStateFrom(steam, 999, 45, true) == SteamState.Starting && StaleGraceSeconds >= 60;
        bool s7 = !SteamClientUp(0, "Logged On", 5) && SteamClientUp(1, "Logged On", 0) && SteamClientUp(1, null, 123) && !SteamClientUp(1, "Connecting", 0) && !SteamClientUp(1, null, 0);
        const string log = "[2026-10-30 19:46:11] [Logged Off, 0, 0] [U:1:1] Log session ended\r\n[2026-10-30 19:46:20] [Logging On, 4, 7] [U:1:1] RecvMsgClientLogOnResponse() : 'OK'\r\n[2026-10-30 19:46:20] [Logged On, 4, 7] [U:1:1] processing complete\r\n";
        bool s5 = LastConnectionState(log) == "Logged On" && LastConnectionState(log + "[2026-10-30 20:00:00] [Connecting, 0, 0] x\n") == "Connecting" && LastConnectionState("") == null && LastConnectionState("no steam log here") == null;

        // the gate before the start, with a fake Steam on a fake clock: the question, the restart button, no start while stale, and waiting (never
        // a restart) for a Steam that is still starting
        var hook = chooseHook; var sayKeep = sayHook; var sleepKeep = staleGateSleep;
        var said = new StringBuilder();
        string[] offered = null; int answer = 1, restarts = 0; bool nowReady = false; double t = 0;
        Func<SteamProbe> stale = () => new SteamProbe { Pids = steam, ActivePid = nowReady ? 100 : 0, Age = 600 + t, Connection = "Logged On", WebHelpers = 3, ActiveUser = 5 };
        Func<bool> restart = () => { restarts++; nowReady = true; return true; };
        bool g1, g2, g3, g4, g5, g6, g7, g8, g9; double tStale;
        try
        {
            staleGateSleep = ms => t += ms / 1000.0;
            chooseHook = (q, o) => { offered = o; return answer; };
            sayHook = s => said.AppendLine(s);
            answer = 1; nowReady = false; restarts = 0; t = 0;
            g1 = !SteamStaleGate(stale, restart) && restarts == 0 && offered != null && offered[0] == StaleRestart && offered[1] == StaleCancel;     // Cancel: nothing starts
            tStale = t;
            g1 &= tStale >= StaleConfirmSeconds && tStale < StaleConfirmSeconds + 2;                                                   // asked only after it stayed stale
            string text = said.ToString();
            bool plain = text.Contains("Fully exit Steam") && text.Contains("Exit") && text.Contains("says you are signed in");
            answer = 0; nowReady = false; restarts = 0; t = 0;
            g2 = SteamStaleGate(stale, restart) && restarts == 1;                                                    // Restart: Steam restarted once, then ready
            answer = 0; nowReady = false; restarts = 0; t = 0;
            g3 = !SteamStaleGate(stale, () => { restarts++; return true; }) && restarts == 1;                          // restarted but still stale: still no start
            offered = null; t = 0;
            g4 = SteamStaleGate(() => new SteamProbe { Pids = steam, ActivePid = 100, Age = 600 }, restart) && offered == null && t == 0;   // a ready Steam: no question, no wait
            g5 = SteamStaleGate(() => new SteamProbe { Pids = new int[0], ActivePid = 0, Age = 1e9 }, restart) && offered == null;  // not running: the usual start of Steam takes over
            // updating for 40 s (no web helper, the old "Logged On" still last in its log, a saved account), registers itself at 45 s: waited for, no question, no restart
            offered = null; restarts = 0; t = 0;
            g6 = SteamStaleGate(() => new SteamProbe { Pids = steam, ActivePid = t >= 45 ? 100 : 0, Age = 30 + t, Connection = "Logged On", WebHelpers = t < 40 ? 0 : 1, ActiveUser = 5 }, restart) && offered == null && restarts == 0 && t >= 45 && t < 47;
            // a crash left its pid behind, the new Steam is still starting for minutes: never called stale, the gate stops waiting after its budget
            offered = null; restarts = 0; t = 0;
            g7 = SteamStaleGate(() => new SteamProbe { Pids = steam, ActivePid = 999, Age = 10 + t, Connection = "Logged On", WebHelpers = 0, ActiveUser = 5 }, restart) && offered == null && restarts == 0 && t >= StaleWaitSeconds && t < StaleWaitSeconds + 2;
            // not signed in yet (login screen): no wait here, the sign-in wait takes it, as before
            offered = null; t = 0;
            g8 = SteamStaleGate(() => new SteamProbe { Pids = steam, ActivePid = 0, Age = 300, Connection = null, WebHelpers = 2, ActiveUser = 0 }, restart) && offered == null && t == 0;
            // stale for 2 s, then it registers: a blip is no stale Steam
            offered = null; t = 0;
            g9 = SteamStaleGate(() => new SteamProbe { Pids = steam, ActivePid = t >= 2 ? 101 : 0, Age = 600, Connection = "Logged On", WebHelpers = 2, ActiveUser = 5 }, restart) && offered == null;
            g1 &= plain;
        }
        finally { chooseHook = hook; sayHook = sayKeep; staleGateSleep = sleepKeep; }
        string[] plan = SteamRestartPlan(@"C:\Steam\steam.exe");
        bool p1 = plan.Length == 2 && plan[0].EndsWith("-shutdown") && !plan[1].Contains("-shutdown");
        var now = ProbeSteam();
        Check("STEAM-STALE", s1 && s2 && s3 && s4 && s5 && s6 && s7 && g1 && g2 && g3 && g4 && g5 && g6 && g7 && g8 && g9 && p1, "a Steam whose client is up and signed in (\"Logged On\") while its ActiveProcess pid stays 0 or not a running steam.exe is named in plain words with a Restart Steam button (steam.exe -shutdown, then start) and the game never starts in that state; a Steam that is still starting (updating, verifying, login screen, a crash's old pid) is waited for, never offered a restart",
            "rules " + s1 + s2 + s3 + s4 + s5 + " starting " + s6 + " client " + s7 + ", gate cancel/restart/still-stale/ready/not-running " + g1 + g2 + g3 + g4 + g5 + " updating/old-pid/not-signed-in/blip " + g6 + g7 + g8 + g9 + ", plan " + p1 + "; this PC now: " + now.State + " (pid " + now.ActivePid + ", web helpers " + now.WebHelpers + ", log " + (now.Connection ?? "?") + ")");
    }

    // ---- START-GATES: the real Start() runs steam_appid.txt, then the mod pin, then the Steam gate, all before anything is launched
    static void AuditStartGates()
    {
        var bad = new List<string>();
        if (Process.GetProcessesByName("GrowHome").Length > 0) { Check("START-GATES", false, "no Grow Home is running during this check", "close the game and run the audit again"); return; }
        string keepInstall = installDir, keepGame = gameDir;
        var keepSource = bundledModSource; string keepSha = bundledShaCache;
        var keepProbe = steamProbeSource; var keepRestart = steamRestartSource; var keepSleep = staleGateSleep; var keepChoose = chooseHook; var keepSay = sayHook;
        bool keepDirect = forceDirect, keepSteam = forceSteam;
        try
        {
            installDir = Path.Combine(Path.GetDirectoryName(installDir), "start-gates"); gameDir = Path.Combine(installDir, "game");
            Directory.CreateDirectory(Path.GetDirectoryName(TargetDll));
            byte[] carried = Bytes("the mod this launcher carries (start gates)"), old = Bytes("an old mod (start gates)");
            bundledModSource = () => carried; bundledShaCache = null;
            File.WriteAllBytes(TargetDll, old); File.WriteAllText(Path.Combine(gameDir, "coop-version.txt"), BundledVersion.ToString());
            int probes = 0, restarts = 0;
            steamProbeSource = () => { probes++; return new SteamProbe { Pids = new[] { 100 }, ActivePid = 0, Age = 600, Connection = "Logged On", WebHelpers = 2, ActiveUser = 5 }; };
            steamRestartSource = () => { restarts++; return false; };
            staleGateSleep = ms => { };
            var asked = new List<string>(); chooseHook = (q, o) => { asked.Add(q); return o.Length - 1; };    // the last answer: Cancel
            sayHook = s => { };
            forceDirect = true; forceSteam = false;
            string logFile = Path.Combine(installDir, "launcher.log");

            // 1. steam_appid.txt cannot be written (a folder of that name is in the way): the start stops at once, before the mod pin and Steam
            Directory.CreateDirectory(AppIdFile(gameDir));
            int c1 = Start(true);
            bool stop1 = c1 == 1 && probes == 0 && SameBytes(TargetDll, old);
            if (!stop1) bad.Add("unwritable steam_appid.txt: result " + c1 + ", steam probed " + probes + ", mod touched=" + !SameBytes(TargetDll, old));
            Directory.Delete(AppIdFile(gameDir));

            // 2. a stale Steam, Cancel: the start stops after steam_appid.txt and the mod pin ran, nothing is launched
            if (File.Exists(logFile)) File.Delete(logFile);
            int c2 = Start(true);
            string log = File.Exists(logFile) ? File.ReadAllText(logFile) : "";
            int iApp = log.IndexOf("steam_appid.txt: "), iPin = log.IndexOf("mod pin: "), iGate = log.IndexOf("steam is running but not registered");
            bool launched = log.Contains("game arguments") || log.Contains("started directly") || log.Contains("started through steam") || Process.GetProcessesByName("GrowHome").Length > 0;
            bool stop2 = c2 == 1 && probes > 0 && restarts == 0 && AppIdOk(gameDir) && SameBytes(TargetDll, carried) && iApp >= 0 && iPin > iApp && iGate > iPin && !launched && asked.Count == 1;
            if (!stop2) bad.Add("stale Steam: result " + c2 + ", probes " + probes + ", appid ok=" + AppIdOk(gameDir) + ", mod pinned=" + SameBytes(TargetDll, carried) + ", log order appid " + iApp + " / pin " + iPin + " / gate " + iGate + ", launched=" + launched + ", questions " + asked.Count);
        }
        catch (Exception e) { bad.Add(e.GetType().Name + ": " + e.Message); }
        finally
        {
            installDir = keepInstall; gameDir = keepGame; bundledModSource = keepSource; bundledShaCache = keepSha;
            steamProbeSource = keepProbe; steamRestartSource = keepRestart; staleGateSleep = keepSleep; chooseHook = keepChoose; sayHook = keepSay;
            forceDirect = keepDirect; forceSteam = keepSteam;
        }
        Check("START-GATES", bad.Count == 0, "the real start checks steam_appid.txt, then the mod pin, then a stale Steam, before anything is launched: an unwritable steam_appid.txt or a stale Steam stops it with nothing started",
            bad.Count == 0 ? "unwritable steam_appid.txt stops before the mod pin and Steam; a stale Steam stops after both, nothing launched" : string.Join("; ", bad.ToArray()));
    }
}
