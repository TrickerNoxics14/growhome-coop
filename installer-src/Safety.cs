using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

// Things that keep a start from ending on a black screen or a broken install:
//   - the game always starts in a window (a frozen fullscreen game takes the whole PC)
//   - Steam must really be ready (signed in, settled) before the game starts, or the game's own Steam check hands the start to Steam
//   - when the co-op copy closes and Steam starts the REGULAR game in its place, that game is closed again before it covers the screen
//   - only one launcher runs at a time
//   - the mod is replaced in one step (never half written), and never while the game runs
static partial class Program
{
    // ---- the game window ----------------------------------------------------------------------------------------------------------

    const string HangArgs = "-screen-width 1024 -screen-height 576";

    // The arguments the game gets. fullscreenAllowed: coop-fullscreen.txt next to the launcher. afterHang: the last game froze (the mod left coop-hang.txt),
    // so this one starts in a small ordinary window that is easy to close.
    static string LaunchArgs(string args, bool fullscreenAllowed, bool afterHang)
    {
        string a = (args ?? "").Trim();
        if (afterHang && !fullscreenAllowed) a = HangArgs;
        if (!fullscreenAllowed && a.IndexOf("-screen-fullscreen", StringComparison.OrdinalIgnoreCase) < 0) a = (a + " -screen-fullscreen 0").Trim();
        return a;
    }

    static bool FullscreenAllowed() { try { return File.Exists(Path.Combine(installDir, "coop-fullscreen.txt")) || File.Exists(Path.Combine(gameDir, "coop-fullscreen.txt")); } catch { return false; } }

    // The mod writes coop-hang.txt when it had to step in for a frozen game. Read once, then removed.
    static bool TakeHangNote()
    {
        string f = Path.Combine(gameDir, "coop-hang.txt");
        try
        {
            if (!File.Exists(f)) return false;
            Log("the last game froze: " + File.ReadAllText(f).Trim() + "; this start uses a small window");
            File.Delete(f);
            return true;
        }
        catch (Exception e) { Log("could not read the hang note: " + e.Message); return false; }
    }

    // ---- Steam ready --------------------------------------------------------------------------------------------------------------

    static int ActiveSteamPid()
    {
        try { object v = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "pid", null); return v is int ? (int)v : 0; }
        catch { return 0; }
    }

    // Steam is ready for a game: signed in, steam.exe running, the pid Steam wrote (when it wrote one) is a running steam.exe (else it is left over from
    // a Steam that closed), and its web helper is up or Steam has run for a minute.
    static bool SteamReadyFrom(string account, int activePid, int[] steamPids, int webHelpers, double steamAgeSeconds)
    {
        if (account == null || steamPids == null || steamPids.Length == 0) return false;
        if (activePid != 0 && !steamPids.Contains(activePid)) return false;
        return webHelpers > 0 || steamAgeSeconds >= 60;
    }

    static double SteamAgeSeconds()
    {
        double age = 1e9;
        foreach (var p in Process.GetProcessesByName("steam")) { try { age = Math.Min(age, (DateTime.Now - p.StartTime).TotalSeconds); } catch { } }
        return age;
    }

    static bool SteamReady()
    {
        int[] pids = Process.GetProcessesByName("steam").Select(p => p.Id).ToArray();
        return SteamReadyFrom(ActiveAccount(), ActiveSteamPid(), pids, Process.GetProcessesByName("steamwebhelper").Length, SteamAgeSeconds());
    }

    // A Steam that just started signs in, then restarts parts of itself: it has to stay ready for a while before a game may start.
    static int SteadySeconds(double steamAgeSeconds) { return steamAgeSeconds < 90 ? 10 : 2; }

    static bool WaitSteamSteady(int seconds)
    {
        int need = SteadySeconds(SteamAgeSeconds());
        var sw = Stopwatch.StartNew(); var steady = Stopwatch.StartNew(); int shown = -1;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (!SteamReady()) steady.Restart();
            else if (steady.Elapsed.TotalSeconds >= need) { Log("steam is ready (steady " + need + " s)"); return true; }
            int s = (int)sw.Elapsed.TotalSeconds;
            if (s / 5 != shown && s > 0) { shown = s / 5; Say("  waiting for Steam to be ready... " + s + " s"); }
            Thread.Sleep(500);
        }
        Log("steam was not steady within " + seconds + " s");
        return false;
    }

    // ---- Steam started the regular game in place of the co-op copy ------------------------------------------------------------------

    // The co-op copy was seen and is gone, and a regular game runs: Steam took over the start. Only then is the regular game ours to close.
    static bool IsHandoff(bool sawOurs, bool oursNow, bool foreignNow, bool foreignBefore) { return sawOurs && !oursNow && foreignNow && !foreignBefore; }

    static int CloseHandoffGames(IEnumerable<RunningGame> games)
    {
        int n = 0;
        foreach (var g in games.Where(x => !x.Ours))
        {
            try { Process.GetProcessById(g.Pid).Kill(); n++; Log("Steam started the regular game (" + g.Path + ") after the co-op copy closed; closed it"); } catch (Exception e) { Log("could not close the regular game: " + e.Message); }
        }
        return n;
    }

    // After a failed start: for a while, a regular game that Steam brings up in its place is closed again (it would cover the screen and is not co-op).
    static void WatchForHandoff(int seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            var games = RunningGames();
            if (games.Any(g => g.Ours)) return;
            if (CloseHandoffGames(games) > 0) { WaitFor(() => RunningGames().Count == 0, 10, null); return; }
            Thread.Sleep(500);
        }
    }

    // A new start only when no Grow Home runs at all (two games fight over Steam and the screen).
    static bool NoGameRunning(int waitSeconds) { return WaitFor(() => RunningGames().Count == 0, waitSeconds, null); }

    // ---- a frozen co-op copy --------------------------------------------------------------------------------------------------------

    // Not responding for HungSamples checks in a row, a second apart. The mod's hang note alone is no proof: a long load (or a stall that
    // recovered) is not a frozen game, and a running co-op game is never closed on a guess.
    const int HungSamples = 10;

    static bool HungVerdict(IList<bool> responding)
    {
        if (responding == null || responding.Count < HungSamples) return false;
        for (int i = 0; i < responding.Count; i++) if (responding[i]) return false;
        return true;
    }

    static bool OurGameHung()
    {
        var seen = new List<bool>();
        for (int i = 0; i < HungSamples; i++)
        {
            var ours = RunningGames().Where(g => g.Ours).ToList();
            if (ours.Count == 0) return false;
            bool responding = true;
            foreach (var g in ours) { try { var p = Process.GetProcessById(g.Pid); p.Refresh(); if (p.MainWindowHandle != IntPtr.Zero && !p.Responding) responding = false; } catch { } }
            seen.Add(responding);
            if (responding) return false;
            if (i < HungSamples - 1) Thread.Sleep(1000);
        }
        return HungVerdict(seen);
    }

    static bool CloseOurGame()
    {
        foreach (var g in RunningGames().Where(x => x.Ours)) { try { Process.GetProcessById(g.Pid).Kill(); } catch { } }
        return NoGameRunning(15);
    }

    // ---- one launcher at a time -----------------------------------------------------------------------------------------------------

    const string LauncherMutexName = @"Local\GrowHomeCoopLauncher";
    static Mutex launcherLock;

    // Returns the lock, or null when another launcher already holds it.
    static Mutex TakeLock(string name)
    {
        try
        {
            bool created; var m = new Mutex(true, name, out created);
            if (created) return m;
            try { if (m.WaitOne(0)) return m; } catch (AbandonedMutexException) { return m; }    // a launcher that crashed left it behind
            m.Close(); return null;
        }
        catch (Exception e) { Log("could not take the launcher lock: " + e.Message); return null; }
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

    // A second launcher: the first one's window comes forward only when it is visible and no game would be covered.
    static void PointToOtherLauncher()
    {
        Log("another launcher is already running; this one stops");
        if (RunningGames().Count > 0) return;
        try
        {
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName))
                if (p.Id != me.Id && p.MainWindowHandle != IntPtr.Zero && IsWindowVisible(p.MainWindowHandle)) { SetForegroundWindow(p.MainWindowHandle); break; }
        }
        catch { }
    }

    // ---- the launcher window over the game -------------------------------------------------------------------------------------------

    enum WindowMove { Nothing, Close, Show, ShowQuiet }

    // What the window does when the flow ends. Never comes up over a running game on success; a problem is shown without taking the focus
    // from a game that runs; a question has to be seen, so it comes forward.
    static WindowMove WindowAfter(bool success, bool question, bool visible, bool gameRunning)
    {
        if (question) return visible ? WindowMove.Nothing : WindowMove.Show;
        if (success) return visible ? WindowMove.Nothing : WindowMove.Close;
        if (visible) return WindowMove.Nothing;
        return gameRunning ? WindowMove.ShowQuiet : WindowMove.Show;
    }

    // ---- replacing the mod ------------------------------------------------------------------------------------------------------------

    static Action<string> swapFault;      // the audit breaks a swap at a named step

    static void Step(string name) { if (swapFault != null) swapFault(name); }

    static bool OurGameRunning() { return RunningGames().Any(g => g.Ours); }

    // Writes next to the target, checks what was written, then swaps it in with one rename (the old one kept as .bak). The target is either fully old or fully new.
    static void WriteAtomic(string target, byte[] data, string backup)
    {
        string tmp = target + ".tmp";
        try
        {
            using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) { f.Write(data, 0, data.Length); f.Flush(true); }
            Step("written");
            if (Sha256(File.ReadAllBytes(tmp)) != Sha256(data)) throw new IOException("the new file did not read back the same");
            Step("verified");
            if (File.Exists(target)) File.Replace(tmp, target, backup, true);
            else File.Move(tmp, target);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }

    // Puts a new mod in place. False (nothing changed) while the co-op copy runs or when any step fails.
    static bool Swap(byte[] data, int version)
    {
        if (OurGameRunning()) { Log("the co-op game runs; the mod is not replaced now"); return false; }
        try
        {
            WriteAtomic(TargetDll, data, TargetDll + ".bak");
            Step("dll");
            WriteAtomic(Path.Combine(gameDir, "coop-version.txt"), System.Text.Encoding.ASCII.GetBytes(version.ToString()), null);
            // the fingerprint of this verified update: the start-up check (EnforceModPin) accepts a newer mod only with it
            if (version > BundledVersion) WritePin(Sha256(data)); else { try { if (File.Exists(PinFile)) File.Delete(PinFile); } catch { } }
            return true;
        }
        catch (Exception e)
        {
            Log("replacing the mod failed: " + e.Message);
            // the dll may already be new while the version file is old: put the old dll back so both agree
            try { if (File.Exists(TargetDll + ".bak") && Sha256(File.ReadAllBytes(TargetDll)) == Sha256(data) && InstalledVersion() != version) File.Copy(TargetDll + ".bak", TargetDll, true); } catch { }
            return false;
        }
    }
}
