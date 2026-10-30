using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

// Starting the game. The point of all this is that the person ends up in the CO-OP copy every time, quickly, or gets told in plain words why not:
//   - a regular Grow Home that is already running is noticed (and closed with permission) instead of silently being the game you see
//   - an account that only borrows the game (Family Sharing) is started through Steam, with Steam's launch option set so Steam starts the co-op copy
//   - after starting, the launcher waits for the mod's own "loaded" line and says what it found
static partial class Program
{
    static bool assumeYes, forceSteam, forceDirect, undoSteam, setupSteam, lastGameHung;
    static int wrongGameAfterSeconds = 12;      // how long a regular game may run alone before it is called "the wrong game" (the audit shortens it)
    static string gameArgs = "-screen-width 1280 -screen-height 720 -popupwindow";

    // Everything the launcher tells or asks the person goes through these, so the text window and the game-styled window show the same things.
    static Action<string> sayHook;                       // message line
    static Func<string, string[], int> chooseHook;       // question + options -> index of the chosen option
    static Action<int> progressHook;                     // overall progress, 0..100
    static Action hideHook;                              // the window may go away while a background update check finishes

    static void Say(string s) { if (sayHook != null) sayHook(s); else Console.WriteLine(s); }

    // Every account, including one that only borrows the game, starts the co-op copy directly: no Steam restart, no Steam setting changed. Steam is only used as the fallback when a direct start fails.
    static bool StartViaSteamFirst() { return forceSteam && !forceDirect; }

    static void Progress(int percent) { if (progressHook != null) progressHook(Math.Max(0, Math.Min(100, percent))); }

    static void ShowInstallProgress(int percent)
    {
        if (progressHook != null) progressHook(percent * 60 / 100);        // copying is the first 60 % of an install
        else if (percent == 100) Console.WriteLine("\r  100%"); else Console.Write("\r  " + percent + "%  ");
    }

    // A question with a few answers. Text mode lists them by number; the window shows them as menu items.
    static int Choose(string question, params string[] options)
    {
        if (chooseHook != null) return chooseHook(question, options);
        if (assumeYes) { Console.WriteLine(question + " -> " + options[0]); return 0; }
        Console.WriteLine(question);
        for (int i = 0; i < options.Length; i++) Console.WriteLine("  " + (i + 1) + " = " + options[i] + (i == 0 ? " (default)" : ""));
        Console.Write("> ");
        int n; return int.TryParse(Console.ReadLine(), out n) && n >= 1 && n <= options.Length ? n - 1 : 0;
    }

    static bool Confirm(string question)
    {
        if (chooseHook != null) return chooseHook(question, new[] { "Yes", "No" }) == 0;
        Console.Write(question);
        if (assumeYes) { Console.WriteLine("y"); return true; }
        string a = Console.ReadLine();
        return a == null || a.Trim() == "" || a.Trim().ToLower().StartsWith("y");
    }

    // Closing a running game loses what was not saved: the first (default) answer keeps it, even with --yes.
    static readonly string[] FrozenAnswers = { "Leave it running", "Close it" };
    static bool AskCloseFrozen(string question)
    {
        if (chooseHook != null) return chooseHook(question, FrozenAnswers) == 1;
        Console.Write(question + " [y/N] ");
        if (assumeYes) { Console.WriteLine("n"); return false; }
        string a = Console.ReadLine();
        return a != null && a.Trim().ToLower().StartsWith("y");
    }

    // The answer that changes nothing: what Escape (and N on a two-answer question) gives in the window. It is NOT always the last one:
    // on the frozen-game question the first answer leaves the game running, so Escape or N there must never close it.
    static readonly string[] SafeAnswers = { "Leave it running", "No", "Cancel" };
    static int SafeAnswer(string[] options)
    {
        if (options == null || options.Length == 0) return 0;
        for (int i = 0; i < options.Length; i++) if (Array.IndexOf(SafeAnswers, options[i]) >= 0) return i;
        return options.Length - 1;
    }

    // For questions where the safe answer is no: text mode needs YES typed out.
    static bool ConfirmStrict(string question)
    {
        if (chooseHook != null) return chooseHook(question, new[] { "Replace it", "Cancel" }) == 0;
        Console.Write(question + " Type YES to continue: ");
        return Console.ReadLine() == "YES";
    }

    static string ModLogPath { get { return Path.Combine(gameDir, "coopmod-steam.log"); } }

    // How many times the mod has said "CoopMod loaded" (it writes that line when the game starts with the mod).
    static int ModLoadedCount()
    {
        try
        {
            if (!File.Exists(ModLogPath)) return 0;
            using (var f = new FileStream(ModLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var r = new StreamReader(f))
            {
                int n = 0; string line;
                while ((line = r.ReadLine()) != null) if (line.StartsWith("CoopMod loaded")) n++;
                return n;
            }
        }
        catch { return 0; }
    }

    // Starting the game directly has worked on this PC before: a later failure is then no reason to restart Steam (that changes Steam's launch option), so the launcher just tries once more.
    static string DirectOkFile { get { return Path.Combine(installDir, "coop-direct-ok.txt"); } }
    static bool DirectWorkedBefore() { try { return File.Exists(DirectOkFile); } catch { return false; } }
    static void MarkDirectWorks() { try { if (!File.Exists(DirectOkFile)) File.WriteAllText(DirectOkFile, "starting the game directly has worked on this PC"); } catch { } }

    enum Outcome { Loaded, WrongGame, NoGame, GameDied, NoMod }

    // Watches until the mod says it loaded, or it is clear that it will not.
    static Outcome WaitForMod(int before, int seconds, bool direct, out int tookSeconds)
    {
        var sw = Stopwatch.StartNew();
        bool sawOurs = false, sawForeign = false; int shown = -1;
        bool foreignBefore = RunningGames().Any(g => !g.Ours);     // a regular game that was already there is the person's, not a handoff
        tookSeconds = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (ModLoadedCount() > before) { tookSeconds = (int)sw.Elapsed.TotalSeconds; return Outcome.Loaded; }
            var games = RunningGames();
            bool ours = games.Any(g => g.Ours), foreign = games.Any(g => !g.Ours);
            sawOurs |= ours; sawForeign |= foreign;
            // the co-op copy closed and Steam brought up the regular game in its place: closed again before it takes the screen
            if (IsHandoff(sawOurs || direct, ours, foreign, foreignBefore)) { CloseHandoffGames(games); tookSeconds = (int)sw.Elapsed.TotalSeconds; return Outcome.GameDied; }
            // a regular game next to the running co-op copy (Steam started it): closed at once, the co-op copy goes on loading
            if (AlongsideToClose(ours, foreign, foreignBefore)) { Log("a regular Grow Home came up next to the co-op copy"); CloseHandoffGames(games); }
            if (!ours && foreign && !sawOurs && sw.Elapsed.TotalSeconds > wrongGameAfterSeconds) { tookSeconds = (int)sw.Elapsed.TotalSeconds; return Outcome.WrongGame; }
            if (sawOurs && !ours && !foreign && sw.Elapsed.TotalSeconds > 3) { tookSeconds = (int)sw.Elapsed.TotalSeconds; return Outcome.GameDied; }
            int s = (int)sw.Elapsed.TotalSeconds;
            if (s / 5 != shown) { shown = s / 5; if (s > 0) Say("  still loading... " + s + " s"); }
            Thread.Sleep(500);
        }
        tookSeconds = (int)sw.Elapsed.TotalSeconds;
        if (ModLoadedCount() > before) return Outcome.Loaded;
        return sawOurs ? Outcome.NoMod : (sawForeign ? Outcome.WrongGame : Outcome.NoGame);
    }

    static bool StartDirect()
    {
        string exe = Path.Combine(gameDir, "GrowHome.exe");
        if (!GameExeAllowed(exe, gameDir, RegularGameDir())) { Log("refused to start " + exe + ": it is not the co-op copy (the regular game is " + RegularGameDir() + ")"); return false; }
        // first choice: the way Steam starts a game (steam.exe as the parent process), which the game's own Steam check accepts whoever opened the launcher
        string args = LaunchArgs(gameArgs, FullscreenAllowed(), lastGameHung);
        Log("game arguments: " + args);
        int under = StartUnderSteam(Path.Combine(gameDir, "GrowHome.exe"), args, gameDir, new Dictionary<string, string> { { "SteamAppId", AppId }, { "SteamGameId", AppId } });
        if (under != 0) { Log("started with steam.exe as its parent (pid " + under + ")"); return true; }
        Log("could not start with steam.exe as the parent; starting the plain way");
        var psi = new ProcessStartInfo(Path.Combine(gameDir, "GrowHome.exe"), args) { WorkingDirectory = gameDir, UseShellExecute = false };
        psi.EnvironmentVariables["SteamAppId"] = AppId;
        psi.EnvironmentVariables["SteamGameId"] = AppId;
        Process.Start(psi);
        return true;
    }

    static void PrintManualLaunchOption()
    {
        Say("");
        Say("To do it by hand: in Steam, right-click Grow Home > Properties > Launch Options, and paste this one line:");
        Say("  " + WantedLaunchOption(""));
    }

    // The co-op folder is the regular Steam game's own folder (a --use-game typo, or a broken setting): nothing may be written or started there.
    static bool IsRegularGameFolder(string dir)
    {
        string r = RegularGameDir();
        return !string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(r) && SamePath(dir, r);
    }

    // 1. of the start: a Grow Home that is already running. Returns -1 to go on with the start, else the exit code.
    // Only the copy that really runs is named: a frozen co-op copy that was closed is not followed by a question about a "regular" game that is not there.
    static int DealWithRunning(List<RunningGame> running, Func<bool> hung, Func<bool> closeOurs)
    {
        if (running == null || running.Count == 0) return -1;
        var regular = running.Where(g => !g.Ours).ToList();
        if (regular.Count == 0)
        {
            if (!hung()) { Say("The co-op copy of Grow Home is already running."); Log("co-op copy already running"); return 0; }
            Say("The co-op copy of Grow Home is running but not responding (a frozen game can leave the screen black).");
            Log("co-op copy runs but does not respond; " + DumpHungGames() + " hang dumps in " + LogsDir);
            if (!AskCloseFrozen("Close it and start it again?")) { Say("Nothing changed."); return 1; }
            if (!closeOurs()) { Say("The frozen game did not close. Press Ctrl+Alt+Del, open Task Manager and end GrowHome."); return 1; }
        }
        else
        {
            Say("The REGULAR Grow Home is already running (probably started from Steam). Only one copy can run at a time, and that is not the co-op one.");
            Log("regular game running: " + string.Join(", ", regular.Select(g => g.Pid + " " + g.Path).ToArray()));
            if (!Confirm("Close it and start the co-op copy? [Y/n] ")) { Say("Nothing started."); return 1; }
            foreach (var g in regular) { try { Process.GetProcessById(g.Pid).Kill(); } catch { } }
        }
        WaitFor(() => RunningGames().Count == 0, 20, null);
        Thread.Sleep(1000);
        return -1;
    }

    static Func<SteamProbe> steamProbeSource = ProbeSteam;     // the audit plays a stale Steam through the real start
    static Func<bool> steamRestartSource = RestartSteam;

    static int Start(bool noUpdate)
    {
        // never in the regular game's folder: not one file is written there (launcher.log included), and nothing is started
        if (IsRegularGameFolder(gameDir)) { Say("The co-op folder " + gameDir + " is the regular Grow Home's own folder. Nothing was changed or started. Run this without --use-game."); return 2; }
        var total = Stopwatch.StartNew();
        Log("start; installed mod version " + InstalledVersion() + "; args: " + string.Join(" ", Environment.GetCommandLineArgs().Skip(1).ToArray()));
        ApplyStaged();
        Say("Grow Home Co-op (mod version " + InstalledVersion() + ")");
        Progress(10);

        // 1. a Grow Home that is already running decides what you will see, so deal with it first
        int dealt = DealWithRunning(RunningGames(), OurGameHung, CloseOurGame);
        if (dealt >= 0) return dealt;

        Progress(20);
        // the co-op copy itself: steam_appid.txt in place, and the mod is the one this launcher carries (or a verified update)
        string appid = EnsureAppIdFile(gameDir);
        Log("steam_appid.txt: " + appid);
        if (!AppIdOk(gameDir)) { Say("Could not write steam_appid.txt into " + gameDir + " (" + appid + "). Without it Steam would start the regular game."); return 1; }
        if (!EnforceModPin()) return 1;

        // 2. how to start: an account that only borrows the game can only start it through Steam
        string account = ActiveAccount();
        bool borrower = IsBorrower(ManifestText(), account);
        bool viaSteamNow = StartViaSteamFirst();
        Log("steam account " + account + "; borrower=" + borrower + "; start through steam=" + viaSteamNow);
        Log("environment: SteamAppId=" + Environment.GetEnvironmentVariable("SteamAppId") + ", SteamGameId=" + Environment.GetEnvironmentVariable("SteamGameId") + ", SteamClientLaunch=" + Environment.GetEnvironmentVariable("SteamClientLaunch") + ", SteamOverlayGameId=" + Environment.GetEnvironmentVariable("SteamOverlayGameId") + "; game folder " + gameDir + "; steam_appid.txt " + File.Exists(Path.Combine(gameDir, "steam_appid.txt")));

        // 3. Steam has to be running and signed in either way (the game talks to it)
        if (!SteamStaleGate(steamProbeSource, steamRestartSource)) return 1;     // a Steam that runs but never registered itself for games is named first; the game never starts then
        if (!EnsureSteamLoggedIn(90)) { Say("Steam is not signed in. Open Steam, sign in, then run this again."); return 1; }
        // a Steam that is still settling makes the game's own Steam check hand the start to Steam (which starts the regular game)
        if (!WaitSteamSteady(60)) Say("Steam is still busy; starting anyway.");
        lastGameHung = TakeHangNote();
        if (lastGameHung) Say("The last game froze. This time it starts in a small window.");

        // Looks for a newer mod while the game loads. The game never waits for it. On a slow network (school) the check may take a minute or more:
        // the launcher then stays alive with its window hidden until it is done, and tells the game, which shows a message.
        int updatedTo = 0;
        Progress(40);
        var updater = noUpdate || UpdatesDisabled() ? null : Bg("Start worker", () => { updatedTo = CheckForUpdate(false, true); });
        if (updater != null) updater.Start();

        int code = LaunchAndVerify(viaSteamNow, borrower, true);
        if (updater != null)
        {
            if (code == 0 && updater.IsAlive) { Say("Checking for a mod update in the background. This window closes by itself."); if (hideHook != null) hideHook(); else HideConsole(); updater.Join(150000); }
            else updater.Join(4000);
            if (updatedTo > 0) WriteUpdateReady(updatedTo);
        }
        Log("done in " + (int)total.Elapsed.TotalSeconds + " s, result " + code);
        return code;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    static void HideConsole() { try { IntPtr h = GetConsoleWindow(); if (h != IntPtr.Zero) ShowWindow(h, 0); } catch { } }

    // The game looks for this file now and then and shows "MOD UPDATE DOWNLOADED" once.
    static void WriteUpdateReady(int version)
    {
        try { File.WriteAllText(Path.Combine(gameDir, "coop-update-ready.txt"), version.ToString()); Log("told the game about update " + version); } catch (Exception e) { Log("could not write the update note: " + e.Message); }
    }

    static int LaunchAndVerify(bool throughSteam, bool borrower, bool mayFallBack)
    {
        int before = ModLoadedCount();
        // never a second game next to one that is still closing (two games fight over Steam and the screen)
        if (RunningGames().Count > 0 && !NoGameRunning(15)) { Say("Another Grow Home is still running. Close it, then try again."); Log("not started: a game still runs"); return 2; }
        if (throughSteam)
        {
            if (!EnsureLaunchOption(false)) { PrintManualLaunchOption(); return 1; }
            Say("Starting Grow Home Co-op through Steam...");
            Process.Start(new ProcessStartInfo("steam://rungameid/" + AppId) { UseShellExecute = true });
        }
        else
        {
            Say("Starting Grow Home Co-op...");
            if (!StartDirect()) { Say("The co-op copy is not where it should be (or it is the regular game's folder). Run this once with  --reinstall."); return 2; }
        }
        Log("started " + (throughSteam ? "through steam" : "directly"));
        Progress(60);

        int took;
        var outcome = WaitForMod(before, throughSteam ? 120 : 60, !throughSteam, out took);
        Log("outcome " + outcome + " after " + took + " s");
        switch (outcome)
        {
            case Outcome.Loaded:
                Progress(100);
                Say("");
                if (!throughSteam) MarkDirectWorks();
                Say("Co-op mod loaded (" + took + " s). In the game: CONTINUE, get into the open world, then F2 (make a code) or F3 (type a code).");
                Thread.Sleep(1500);
                return 0;

            case Outcome.GameDied:
                if (!throughSteam) WatchForHandoff(10);
                if (!throughSteam && mayFallBack)
                {
                    if (DirectWorkedBefore()) { Say("The game closed right after starting. Trying once more (no Steam restart needed)..."); WaitFor(() => RunningGames().Count == 0, 15, null); Thread.Sleep(3000); return LaunchAndVerify(false, borrower, false); }
                    Say("The game closed right after starting. I will try through Steam instead.");
                    return LaunchAndVerify(true, true, false);
                }
                Say("The game closed right after starting. If a friend or your other PC is playing from the same Steam library, Steam Family Sharing allows only one person at a time.");
                return 2;

            case Outcome.WrongGame:
                Say("");
                Say("The REGULAR Grow Home started, not the co-op copy.");
                Say(throughSteam ? "Steam did not use the co-op launch option. Close Steam completely (Steam menu > Exit) and run this again." : "Close the regular game and run this again.");
                return 4;

            case Outcome.NoGame:
                if (!throughSteam) WatchForHandoff(10);
                Say("");
                Say("Nothing started within " + took + " seconds.");
                if (!throughSteam && mayFallBack && DirectWorkedBefore()) { Say("Trying once more..."); Thread.Sleep(3000); return LaunchAndVerify(false, borrower, false); }
                if (!throughSteam && mayFallBack) { Say("Trying through Steam..."); return LaunchAndVerify(true, borrower, false); }
                Say("If another computer is playing from the same Steam library right now, Steam Family Sharing blocks the game here. Otherwise check that Steam is signed in.");
                return 2;

            default:   // NoMod
                Say("");
                if (OurGameHung())
                {
                    Say("The co-op copy started but stopped responding.");
                    Log("co-op copy stopped responding; " + DumpHungGames() + " hang dumps in " + LogsDir);
                    if (AskCloseFrozen("Close the frozen game?")) CloseOurGame();
                    return 3;
                }
                Say("The co-op copy started but the mod did not load. Run this once with  --reinstall  to rebuild the co-op copy.");
                return 3;
        }
    }
}
