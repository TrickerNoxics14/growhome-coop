using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

// Hardening from the coop-anything playbook (2026-10-30):
//   - steam_appid.txt is in the co-op copy before every start (without it the game's Steam check hands the start to Steam, which starts the REGULAR game)
//   - the launcher only ever starts the co-op copy's GrowHome.exe, and a regular game that comes up next to it is closed again
//   - a game the launcher calls frozen leaves a minidump in GrowHomeCoop\logs first (the last 2 are kept)
//   - the mod in the game folder is the one this launcher carries (or a verified update): an old or changed CoopMod.dll is replaced, not used
//   - a Steam that runs but never registered itself for games (ActiveProcess pid 0 or a dead pid) is named, and the game is not started then
static partial class Program
{
    // ---- 1. steam_appid.txt -------------------------------------------------------------------------------------------------------

    static string AppIdFile(string dir) { return Path.Combine(dir, "steam_appid.txt"); }

    // Makes sure the co-op copy has steam_appid.txt with 323320. Returns what it found: "ok", "created", "repaired (...)", or "not written: ..." (then the start stops).
    static string EnsureAppIdFile(string dir)
    {
        string f = AppIdFile(dir);
        try
        {
            string had = File.Exists(f) ? File.ReadAllText(f) : null;
            if (had != null && had.Trim() == AppId) return "ok";
            File.WriteAllText(f, AppId);
            if (File.ReadAllText(f).Trim() != AppId) return "not written: it does not read back";
            return had == null ? "created" : "repaired (it said \"" + (had.Length > 20 ? had.Substring(0, 20) + "..." : had.Trim()) + "\")";
        }
        catch (Exception e) { return "not written: " + e.Message; }
    }

    static bool AppIdOk(string dir) { try { return File.Exists(AppIdFile(dir)) && File.ReadAllText(AppIdFile(dir)).Trim() == AppId; } catch { return false; } }

    // ---- 2. only the co-op copy ------------------------------------------------------------------------------------------------------

    // The only program the launcher starts: GrowHome.exe in the co-op folder, and never one in the regular game's folder (when the co-op folder is the regular one, nothing).
    static bool GameExeAllowed(string exe, string coopDir, string regularDir)
    {
        if (string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(coopDir)) return false;
        if (!SamePath(exe, Path.Combine(coopDir, "GrowHome.exe"))) return false;
        if (!string.IsNullOrEmpty(regularDir) && SamePath(coopDir, regularDir)) return false;
        return true;
    }

    static string regularDirCache; static bool regularDirKnown;
    static string RegularGameDir() { if (!regularDirKnown) { try { regularDirCache = FindGame(); } catch { regularDirCache = null; } regularDirKnown = true; } return regularDirCache; }

    // A regular game that came up NEXT TO the running co-op copy (Steam started it) is closed at once: the two would fight over Steam and the screen.
    static bool AlongsideToClose(bool oursNow, bool foreignNow, bool foreignBefore) { return oursNow && foreignNow && !foreignBefore; }

    // ---- 3. a minidump of a frozen game ------------------------------------------------------------------------------------------------

    [DllImport("dbghelp.dll", SetLastError = true)]
    static extern bool MiniDumpWriteDump(IntPtr process, int pid, Microsoft.Win32.SafeHandles.SafeFileHandle file, int type, IntPtr exception, IntPtr userStream, IntPtr callback);

    const int DumpsKept = 2;
    const int DumpType = 0x00000000 | 0x00001000 | 0x00000020;   // MiniDumpNormal (every thread's stack) | WithThreadInfo | WithUnloadedModules: small, enough to see where the game hangs
    static string LogsDir { get { return Path.Combine(installDir, "logs"); } }

    // Writes a minidump of the process into the logs folder and keeps only the newest two. Returns the file, or null when it could not be written.
    static string WriteHangDump(int pid)
    {
        string path = null;
        IntPtr h = IntPtr.Zero;
        try
        {
            Directory.CreateDirectory(LogsDir);
            h = OpenProcess(0x0400 | 0x0010, false, pid);       // PROCESS_QUERY_INFORMATION | PROCESS_VM_READ
            if (h == IntPtr.Zero) { Log("hang dump: cannot open process " + pid + " (error " + Marshal.GetLastWin32Error() + ")"); return null; }
            path = Path.Combine(LogsDir, "hang-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + pid + ".dmp");
            bool ok;
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                ok = MiniDumpWriteDump(h, pid, fs.SafeFileHandle, DumpType, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (!ok) { Log("hang dump of " + pid + " not written (error " + Marshal.GetLastWin32Error() + ")"); try { File.Delete(path); } catch { } return null; }
            Log("hang dump written: " + path + " (" + new FileInfo(path).Length / 1024 + " KB)");
            PruneDumps(DumpsKept);
            return path;
        }
        catch (Exception e) { Log("hang dump of " + pid + " not written: " + e.Message); try { if (path != null && File.Exists(path)) File.Delete(path); } catch { } return null; }
        finally { if (h != IntPtr.Zero) CloseHandle(h); }
    }

    static void PruneDumps(int keep)
    {
        try
        {
            foreach (var f in Directory.GetFiles(LogsDir, "hang-*.dmp").OrderByDescending(x => Path.GetFileName(x), StringComparer.Ordinal).Skip(keep))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }

    // The launcher has decided the co-op game is frozen: every co-op process gets a dump before anything closes it. Returns how many were written.
    static int DumpHungGames()
    {
        int n = 0;
        foreach (var g in RunningGames().Where(x => x.Ours)) if (WriteHangDump(g.Pid) != null) n++;
        return n;
    }

    // ---- 4. the mod is pinned ------------------------------------------------------------------------------------------------------

    static string PinFile { get { return Path.Combine(gameDir, "coop-mod-sha256.txt"); } }      // the fingerprint of a verified update the launcher put in (only for a mod newer than the one it carries)
    static string bundledShaCache;
    static Func<byte[]> bundledModSource = () => ReadResource("CoopMod.dll");             // the audit swaps in a stand-in
    static byte[] BundledMod() { return bundledModSource(); }
    static string BundledModSha() { if (bundledShaCache == null) { var d = BundledMod(); bundledShaCache = d == null ? "" : Sha256(d); } return bundledShaCache; }

    enum PinVerdict { Ok, NewerPinned, NewerFirstSeen, Replace }

    // installed: the mod in the game folder ("" when missing). The mod this launcher carries is always right; a newer version is right only with the
    // fingerprint the launcher wrote when it put that verified update in (a newer one from before this check existed is taken once and pinned then);
    // anything else (an old build, a changed file, a pin that does not match) is replaced.
    static PinVerdict ModPinVerdict(string installedSha, int installedVersion, string bundledSha, int bundledVersion, string pinnedSha)
    {
        if (string.IsNullOrEmpty(installedSha) || string.IsNullOrEmpty(bundledSha)) return PinVerdict.Replace;
        if (string.Equals(installedSha, bundledSha, StringComparison.OrdinalIgnoreCase)) return PinVerdict.Ok;
        if (installedVersion > bundledVersion)
        {
            if (string.IsNullOrEmpty(pinnedSha)) return PinVerdict.NewerFirstSeen;
            return string.Equals(installedSha, pinnedSha.Trim(), StringComparison.OrdinalIgnoreCase) ? PinVerdict.NewerPinned : PinVerdict.Replace;
        }
        return PinVerdict.Replace;
    }

    static void WritePin(string sha) { try { WriteAtomic(PinFile, Encoding.ASCII.GetBytes(sha), null); } catch (Exception e) { Log("could not write the mod fingerprint: " + e.Message); } }

    // Before every start. True when the mod in place is the expected one (it may have just been put back); false: do not start.
    static bool EnforceModPin()
    {
        byte[] bundled = BundledMod();
        if (bundled == null) { Log("mod pin: this launcher carries no mod (development build): not checked"); return true; }
        string installed = File.Exists(TargetDll) ? Sha256(TargetDll) : "";
        string pinned = null; try { if (File.Exists(PinFile)) pinned = File.ReadAllText(PinFile).Trim(); } catch { }
        int have = InstalledVersion();
        var verdict = ModPinVerdict(installed, have, BundledModSha(), BundledVersion, pinned);
        switch (verdict)
        {
            case PinVerdict.Ok:
                if (have != BundledVersion) { WriteAtomic(Path.Combine(gameDir, "coop-version.txt"), Encoding.ASCII.GetBytes(BundledVersion.ToString()), null); Log("mod pin: the mod is this launcher's version " + BundledVersion + " (the version file said " + have + ")"); }
                else Log("mod pin: ok, the mod is this launcher's version " + BundledVersion + " (" + installed.Substring(0, 12) + ")");
                return true;
            case PinVerdict.NewerPinned:
                Log("mod pin: ok, the pinned update version " + have + " (" + installed.Substring(0, 12) + ")");
                return true;
            case PinVerdict.NewerFirstSeen:
                WritePin(installed);
                Log("mod pin: kept the newer mod version " + have + " from an earlier update and pinned its fingerprint " + installed.Substring(0, 12));
                return true;
            default:
                if (OurGameRunning()) { Log("mod pin: the mod in the game folder is not the expected one, but the co-op game runs: not replaced now"); Say("The co-op mod in the game folder is not the expected one. Close the game, then start again."); return false; }
                try
                {
                    WriteAtomic(TargetDll, bundled, File.Exists(TargetDll) ? TargetDll + ".bak" : null);
                    WriteAtomic(Path.Combine(gameDir, "coop-version.txt"), Encoding.ASCII.GetBytes(BundledVersion.ToString()), null);
                    try { if (File.Exists(PinFile)) File.Delete(PinFile); } catch { }
                }
                catch (Exception e) { Log("mod pin: could not put the expected mod in place: " + e.Message); Say("The co-op mod in the game folder is not the expected one and could not be replaced (" + e.Message + ")."); return false; }
                Log("mod pin: the mod in the game folder (version " + have + ", " + (installed.Length > 0 ? installed.Substring(0, 12) : "missing") + ") was not the expected one; replaced with this launcher's version " + BundledVersion + " (" + BundledModSha().Substring(0, 12) + ")");
                Say("The co-op mod in the game folder was old or changed; it was replaced with version " + BundledVersion + ".");
                return true;
        }
    }

    // ---- 5. a Steam that runs but never registered itself ------------------------------------------------------------------------------

    enum SteamState { Ready, NotRunning, Starting, Stale }
    const double StaleGraceSeconds = 60;     // a Steam younger than this is still starting (a self-update or "verifying installation" can take a minute or more)
    const int StaleConfirmSeconds = 5;       // stale on every probe for this long before the player is asked
    const int StaleWaitSeconds = 90;         // the gate waits this long for a Steam that is still starting (the same budget as the sign-in wait)

    // steamPids: the running steam.exe processes. activePid: HKCU\Software\Valve\Steam\ActiveProcess\pid. clientUp: Steam's client is really up
    // (its web helper runs AND it is signed in: connection log "Logged On" or an active account). Steam running while that pid is 0 or not a running
    // steam.exe is a Steam the games cannot find (their SteamAPI_Init fails) - but only a Steam whose client is up and that is past its start counts
    // as stale: one that is updating, verifying, on its login screen or asking to go offline has not written its pid yet and is still starting.
    static SteamState SteamStateFrom(int[] steamPids, int activePid, double steamAgeSeconds, bool clientUp)
    {
        if (steamPids == null || steamPids.Length == 0) return SteamState.NotRunning;
        if (activePid != 0 && steamPids.Contains(activePid)) return SteamState.Ready;
        return steamAgeSeconds >= StaleGraceSeconds && clientUp ? SteamState.Stale : SteamState.Starting;
    }

    static bool SteamClientUp(int webHelpers, string connection, int activeUser) { return webHelpers > 0 && (connection == "Logged On" || activeUser != 0); }

    // The state on the last line of Steam's connection log ("Logged On", "Logged Off", "Connecting", ...), or null.
    static string LastConnectionState(string logText)
    {
        if (string.IsNullOrEmpty(logText)) return null;
        string found = null;
        foreach (Match m in Regex.Matches(logText, @"^\[[^\]\r\n]+\] \[([A-Za-z][A-Za-z ]*?),", RegexOptions.Multiline)) found = m.Groups[1].Value;
        return found;
    }

    static string ConnectionLogTail()
    {
        try
        {
            string r = SteamRoot(); if (r == null) return null;
            string f = Path.Combine(r, "logs", "connection_log.txt");
            if (!File.Exists(f)) return null;
            using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, s.Length - 32768); s.Seek(start, SeekOrigin.Begin);
                var buf = new byte[s.Length - start]; int got = 0, n;
                while (got < buf.Length && (n = s.Read(buf, got, buf.Length - got)) > 0) got += n;
                return Encoding.UTF8.GetString(buf, 0, got);
            }
        }
        catch { return null; }
    }

    sealed class SteamProbe
    {
        public int[] Pids; public int ActivePid; public double Age; public string Connection; public int WebHelpers; public int ActiveUser;
        public SteamState State { get { return SteamStateFrom(Pids, ActivePid, Age, SteamClientUp(WebHelpers, Connection, ActiveUser)); } }
    }

    static int ActiveSteamUser()
    {
        try { object v = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null); return v is int ? (int)v : 0; }
        catch { return 0; }
    }

    static SteamProbe ProbeSteam()
    {
        return new SteamProbe { Pids = Process.GetProcessesByName("steam").Select(p => p.Id).ToArray(), ActivePid = ActiveSteamPid(), Age = SteamAgeSeconds(), Connection = LastConnectionState(ConnectionLogTail()),
            WebHelpers = Process.GetProcessesByName("steamwebhelper").Length, ActiveUser = ActiveSteamUser() };
    }

    const string StaleRestart = "Restart Steam for me", StaleCancel = "Cancel";
    static Action<int> staleGateSleep = ms => Thread.Sleep(ms);       // the audit runs the gate's waiting on a fake clock

    // Before the game starts: a stale Steam is named in plain words, with a button that restarts Steam. The game never starts while Steam is stale.
    // A Steam that is still starting is waited for (up to StaleWaitSeconds), never offered a restart; stale has to hold for StaleConfirmSeconds in a row.
    static bool SteamStaleGate(Func<SteamProbe> probe, Func<bool> restartSteam)
    {
        SteamProbe p; SteamState st; int waited = 0, staleFor = 0; bool told = false;
        while (true)
        {
            p = probe(); st = p.State;
            if (st == SteamState.Ready || st == SteamState.NotRunning) { Log("steam gate: " + st + (waited > 0 ? " after " + waited + " s" : "")); return true; }
            if (st == SteamState.Stale) { if (staleFor >= StaleConfirmSeconds) break; }
            else
            {
                staleFor = 0;
                if (p.ActiveUser == 0 && p.Connection != "Logged On") { if (waited > 0) Log("steam gate: steam is not signed in yet after " + waited + " s"); return true; }   // not signed in (login screen, update before sign-in): the sign-in wait takes it, as before
                if (waited >= StaleWaitSeconds) { Log("steam gate: steam is still starting after " + waited + " s (pid " + p.ActivePid + ", web helpers " + p.WebHelpers + ", log " + (p.Connection ?? "?") + "); the sign-in wait takes it from here"); return true; }
            }
            if (!told) { told = true; Say("Waiting for Steam to finish starting..."); }
            staleGateSleep(1000); waited++; if (st == SteamState.Stale) staleFor++;
        }
        Log("steam is running but not registered for games (ActiveProcess pid " + p.ActivePid + ", steam.exe " + string.Join(",", p.Pids.Select(x => x.ToString()).ToArray()) + ", web helpers " + p.WebHelpers + ", connection log: " + (p.Connection ?? "?") + ", age " + (int)p.Age + " s, stale for " + staleFor + " s)");
        Say("");
        Say("Steam is running" + (p.Connection == "Logged On" ? " and says you are signed in" : "") + ", but it did not register itself for games, so Grow Home cannot talk to it.");
        Say("Fully exit Steam (right-click the Steam icon next to the clock > Exit), open Steam again, then start Grow Home Co-op again.");
        int c = Choose("Steam has to be restarted before the game can start.", StaleRestart, StaleCancel);
        if (c != 0) { Say("Nothing started."); Log("stale steam: the player chose not to restart it"); return false; }
        if (!restartSteam()) { Say("Steam did not restart by itself. Exit it from its tray icon, open it again, then try again."); Log("stale steam: restart did not finish"); return false; }
        p = probe();
        st = p.State;
        if (st != SteamState.Ready) { Say("Steam is still not ready for games. Exit it from its tray icon, open it again, then try again."); Log("stale steam: still " + st + " after the restart"); return false; }
        Log("stale steam: restarted, ready now");
        return true;
    }

    // What the restart button does, in order: ask the running Steam to shut down (never killed), then start it again.
    static string[] SteamRestartPlan(string steamExe) { return new[] { "\"" + steamExe + "\" -shutdown", "\"" + steamExe + "\"" }; }

    static bool RestartSteam()
    {
        string exe = SteamExe();
        if (exe == null || !File.Exists(exe)) { Log("restart steam: steam.exe not found"); return false; }
        Log("restart steam: " + string.Join(" then ", SteamRestartPlan(exe)));
        if (!ShutdownSteam()) return false;
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); } catch (Exception e) { Log("restart steam: could not start it: " + e.Message); return false; }
        Say("Starting Steam again...");
        bool ok = WaitFor(() => SteamLoggedIn() && SteamStateFrom(Process.GetProcessesByName("steam").Select(q => q.Id).ToArray(), ActiveSteamPid(), 1e9, true) == SteamState.Ready, 120, s => Say("  waiting for Steam to sign in... " + s + " s"));
        if (ok) Thread.Sleep(3000);
        return ok;
    }
}
