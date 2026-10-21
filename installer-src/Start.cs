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
    static bool assumeYes, forceSteam, forceDirect, undoSteam, setupSteam;
    static int wrongGameAfterSeconds = 12;      // how long a regular game may run alone before it is called "the wrong game" (the audit shortens it)
    static string gameArgs = "-screen-width 1280 -screen-height 720 -popupwindow";

    // Everything the launcher tells or asks the person goes through these, so the text window and the game-styled window show the same things.
    static Action<string> sayHook;                       // message line
    static Func<string, string[], int> chooseHook;       // question + options -> index of the chosen option
    static Action<int> progressHook;                     // overall progress, 0..100
    static Action hideHook;                              // the window may go away while a background update check finishes

    static void Say(string s) { if (sayHook != null) sayHook(s); else Console.WriteLine(s); }

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

    enum Outcome { Loaded, WrongGame, NoGame, GameDied, NoMod }

    // Watches until the mod says it loaded, or it is clear that it will not.
    static Outcome WaitForMod(int before, int seconds, bool direct, out int tookSeconds)
    {
        var sw = Stopwatch.StartNew();
        bool sawOurs = false, sawForeign = false; int shown = -1;
        tookSeconds = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (ModLoadedCount() > before) { tookSeconds = (int)sw.Elapsed.TotalSeconds; return Outcome.Loaded; }
            var games = RunningGames();
            bool ours = games.Any(g => g.Ours), foreign = games.Any(g => !g.Ours);
            sawOurs |= ours; sawForeign |= foreign;
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

    static void StartDirect()
    {
        var psi = new ProcessStartInfo(Path.Combine(gameDir, "GrowHome.exe"), gameArgs) { WorkingDirectory = gameDir, UseShellExecute = false };
        psi.EnvironmentVariables["SteamAppId"] = AppId;
        psi.EnvironmentVariables["SteamGameId"] = AppId;
        Process.Start(psi);
    }

    static void PrintManualLaunchOption()
    {
        Say("");
        Say("To do it by hand: in Steam, right-click Grow Home > Properties > Launch Options, and paste this one line:");
        Say("  " + WantedLaunchOption(""));
    }

    static int Start(bool noUpdate)
    {
        var total = Stopwatch.StartNew();
        Log("start; installed mod version " + InstalledVersion() + "; args: " + string.Join(" ", Environment.GetCommandLineArgs().Skip(1).ToArray()));
        ApplyStaged();
        Say("Grow Home Co-op (mod version " + InstalledVersion() + ")");
        Progress(10);

        // 1. a Grow Home that is already running decides what you will see, so deal with it first
        var running = RunningGames();
        if (running.Count > 0)
        {
            var regular = running.Where(g => !g.Ours).ToList();
            if (regular.Count == 0) { Say("The co-op copy of Grow Home is already running."); Log("co-op copy already running"); return 0; }
            Say("The REGULAR Grow Home is already running (probably started from Steam). Only one copy can run at a time, and that is not the co-op one.");
            Log("regular game running: " + string.Join(", ", regular.Select(g => g.Pid + " " + g.Path).ToArray()));
            if (!Confirm("Close it and start the co-op copy? [Y/n] ")) { Say("Nothing started."); return 1; }
            foreach (var g in regular) { try { Process.GetProcessById(g.Pid).Kill(); } catch { } }
            WaitFor(() => RunningGames().Count == 0, 20, null);
            Thread.Sleep(1000);
        }

        Progress(20);
        // 2. how to start: an account that only borrows the game can only start it through Steam
        string account = ActiveAccount();
        bool borrower = IsBorrower(ManifestText(), account);
        bool viaSteamNow = forceSteam || (borrower && !forceDirect);
        Log("steam account " + account + "; borrower=" + borrower + "; start through steam=" + viaSteamNow);

        // 3. Steam has to be running and signed in either way (the game talks to it)
        if (!EnsureSteamLoggedIn(90)) { Say("Steam is not signed in. Open Steam, sign in, then run this again."); return 1; }

        // Looks for a newer mod while the game loads. The game never waits for it. On a slow network (school) the check may take a minute or more:
        // the launcher then stays alive with its window hidden until it is done, and tells the game, which shows a message.
        int updatedTo = 0;
        Progress(40);
        var updater = noUpdate || UpdatesDisabled() ? null : new Thread(() => { updatedTo = CheckForUpdate(false, true); }) { IsBackground = true };
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
        if (throughSteam)
        {
            if (!EnsureLaunchOption(false)) { PrintManualLaunchOption(); return 1; }
            Say("Starting Grow Home Co-op through Steam...");
            Process.Start(new ProcessStartInfo("steam://rungameid/" + AppId) { UseShellExecute = true });
        }
        else
        {
            Say("Starting Grow Home Co-op...");
            StartDirect();
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
                Say("Co-op mod loaded (" + took + " s). In the game: CONTINUE, get into the open world, then F2 (make a code) or F3 (type a code).");
                Thread.Sleep(1500);
                return 0;

            case Outcome.GameDied:
                if (!throughSteam && mayFallBack)
                {
                    Say("The game closed right after starting. This Steam account may only borrow the game, so I will start it through Steam instead.");
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
                Say("");
                Say("Nothing started within " + took + " seconds.");
                if (!throughSteam && mayFallBack) { Say("Trying through Steam..."); return LaunchAndVerify(true, borrower, false); }
                Say("If another computer is playing from the same Steam library right now, Steam Family Sharing blocks the game here. Otherwise check that Steam is signed in.");
                return 2;

            default:   // NoMod
                Say("");
                Say("The co-op copy started but the mod did not load. Run this once with  --reinstall  to rebuild the co-op copy.");
                return 3;
        }
    }
}
