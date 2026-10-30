using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// Audit checks for starting and installing (see Audit.cs for how they are run).
static partial class Program
{
    const string VdfSample =
        "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n\t\t\t\t\"apps\"\n\t\t\t\t{\n" +
        "\t\t\t\t\t\"220\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LastPlayed\"\t\t\"1\"\n\t\t\t\t\t}\n" +
        "\t\t\t\t\t\"323320\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LastPlayed\"\t\t\"2\"\n\t\t\t\t\t\t\"cloud\"\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\t\"last_sync_state\"\t\t\"synchronized\"\n\t\t\t\t\t\t}\n\t\t\t\t\t}\n" +
        "\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n";

    static bool Throws<T>(Action a) where T : Exception
    {
        try { a(); return false; }
        catch (T) { return true; }
        catch (Exception) { return false; }
    }

    static string other0(string lf) { return SetLaunchOption(lf, "999", "-x"); }
    static string Label(string f) { try { return Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f))) + "\\" + Path.GetFileName(f); } catch { return f; } }

    static void AuditSteamConfig(string tmp)
    {
        installDir = Path.Combine(tmp, "GrowHomeCoop"); gameDir = Path.Combine(installDir, "game");
        string lf = VdfSample, crlf = VdfSample.Replace("\n", "\r\n");
        string opt = "\"C:\\Users\\Some One\\GrowHomeCoop\\game\\GrowHome.exe\" -screen-width 1280 -popupwindow %command%";

        Check("STEAM-LAUNCH-OPTION", GetLaunchOption(lf, AppId) == "", "a game with no launch option reads as empty", "");
        string set = SetLaunchOption(lf, AppId, opt);
        Check("STEAM-LAUNCH-OPTION", GetLaunchOption(set, AppId) == opt, "the launch option (with quotes, backslashes and spaces) reads back exactly", "");
        Check("STEAM-LAUNCH-OPTION", ClearLaunchOption(set, AppId) == lf, "removing it again gives back the original file byte for byte", "");
        Check("STEAM-LAUNCH-OPTION", ClearLaunchOption(other0(lf), "999") == lf, "an entry we created is removed completely on undo", "");
        Check("STEAM-LAUNCH-OPTION", SetLaunchOption(set, AppId, opt) == set, "setting it twice changes nothing the second time", "");
        string twice = SetLaunchOption(SetLaunchOption(lf, AppId, "-a"), AppId, "-b");
        Check("STEAM-LAUNCH-OPTION", GetLaunchOption(twice, AppId) == "-b" && Regex.Matches(twice, "LaunchOptions").Count == 1, "an existing option is replaced, not duplicated", "");
        string setCrlf = SetLaunchOption(crlf, AppId, opt);
        Check("STEAM-LAUNCH-OPTION", !Regex.IsMatch(setCrlf, "(?<!\r)\n") && ClearLaunchOption(setCrlf, AppId) == crlf, "a file with Windows line endings keeps them", "");
        string other = SetLaunchOption(lf, "999", opt);
        Check("STEAM-LAUNCH-OPTION", GetLaunchOption(other, "999") == opt && GetLaunchOption(other, AppId) == "", "a game Steam has no entry for yet gets one, other games are untouched", "");
        bool allMalformed = true; string which = "";
        var bad = new Dictionary<string, string>
        {
            { "empty", "" }, { "garbage", "this is not a steam file" }, { "unbalanced", lf.Substring(0, lf.Length - 6) },
            { "extra brace", lf + "}" }, { "no apps", "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t}\n}\n" }, { "unclosed quote", "\"UserLocalConfigStore\n{" },
        };
        foreach (var kv in bad) if (!Throws<FormatException>(() => SetLaunchOption(kv.Value, AppId, opt))) { allMalformed = false; which += kv.Key + " "; }
        Check("STEAM-LAUNCH-OPTION", allMalformed, "damaged or unexpected settings files are refused and never written", which);

        // the wanted option text
        string wanted = WantedLaunchOption("");
        Check("STEAM-LAUNCH-OPTION", wanted.StartsWith("\"" + Path.Combine(gameDir, "GrowHome.exe") + "\"") && wanted.EndsWith("%command%") && IsOurLaunchOption(wanted), "the option starts the co-op copy and is recognised as ours", wanted);
        string kept = WantedLaunchOption("-windowed -novid");
        Check("STEAM-LAUNCH-OPTION", kept.Contains("-windowed -novid") && !kept.Contains("-popupwindow") && kept.EndsWith("%command%"), "options the person already had are kept", kept);
        Check("STEAM-LAUNCH-OPTION", !IsOurLaunchOption("-windowed") && !IsOurLaunchOption("") && !IsOurLaunchOption(null), "somebody else's option is not mistaken for ours", "");

        // the real settings files on this PC: read, edit in memory, undo; nothing is written
        int files = 0, good = 0; string detail = "";
        var realFiles = new List<string>(extraVdf);
        string root = SteamRoot();
        if (root != null && Directory.Exists(Path.Combine(root, "userdata")))
            foreach (var dir in Directory.GetDirectories(Path.Combine(root, "userdata"))) realFiles.Add(Path.Combine(dir, "config", "localconfig.vdf"));
        foreach (string f in realFiles)
        {
                if (!File.Exists(f)) continue;
                files++;
                try
                {
                    string text = File.ReadAllText(f);
                    string current = GetLaunchOption(text, AppId);
                    string edited = SetLaunchOption(text, AppId, opt);
                    bool roundTrip = current != "" || ClearLaunchOption(edited, AppId) == text;
                    if (GetLaunchOption(edited, AppId) == opt && roundTrip) good++; else detail += Label(f) + " (had option: \"" + current + "\", reads back: " + (GetLaunchOption(edited, AppId) == opt) + ", undo exact: " + roundTrip + "); ";
                }
                catch (Exception e) { detail += Label(f) + ": " + e.Message + "; "; }
            }
        Check("STEAM-LAUNCH-OPTION", good == files, "this PC's real Steam settings files are understood, edited and restored exactly (in memory only)", files + " files, " + good + " ok " + detail);
    }

    static void AuditBorrower()
    {
        string acf = "\"AppState\"\n{\n\t\"appid\"\t\t\"323320\"\n\t\"LastOwner\"\t\t\"76561198000000001\"\n}\n";
        Check("BORROWER-DETECT", !IsBorrower(acf, "39734273"), "the owner's own account is not a borrower", "");
        Check("BORROWER-DETECT", IsBorrower(acf, "39734274"), "another account on the same Steam library is recognised as a borrower", "");
        Check("BORROWER-DETECT", !IsBorrower(null, "39734273") && !IsBorrower("no owner in here", "39734273") && !IsBorrower(acf, null) && !IsBorrower(acf, "abc"), "missing or odd information is treated as not-a-borrower (start directly first)", "");
        string live = ManifestText();
        Check("BORROWER-DETECT", live != null && LastOwner(live) != null, "this PC's Grow Home install manifest can be read", live == null ? "not found" : "LastOwner " + LastOwner(live));
    }

    static void AuditRunningGames(string tmp)
    {
        string mine = ImagePath(Process.GetCurrentProcess().Id);
        Check("RUNNING-GAME", SamePath(mine, Assembly.GetExecutingAssembly().Location), "a process's real program path can be read", mine);
        Check("RUNNING-GAME", SamePath("C:\\a\\b\\", "c:\\A\\B") && !SamePath("C:\\a", "C:\\b") && !SamePath(null, "C:\\a"), "paths are compared the way Windows does", "");
        if (Process.GetProcessesByName("GrowHome").Length > 0) { Check("RUNNING-GAME", false, "no Grow Home is running during this check", "close the game and run the audit again"); return; }

        string fakeDir = Path.Combine(tmp, "fake-game"), fakeExe = Path.Combine(fakeDir, "GrowHome.exe");
        Process p = null;
        try
        {
            p = StartFake(fakeExe, "-n 40 127.0.0.1");
            gameDir = Path.Combine(tmp, "somewhere-else");
            var regular = RunningGames();
            Check("RUNNING-GAME", regular.Count == 1 && !regular[0].Ours && SamePath(regular[0].Path, fakeExe), "a Grow Home running from another folder is the REGULAR game", regular.Count + " found" + (regular.Count == 1 ? ", ours=" + regular[0].Ours : ""));
            gameDir = fakeDir;
            var coop = RunningGames();
            Check("RUNNING-GAME", coop.Count == 1 && coop[0].Ours, "a Grow Home running from the co-op folder is recognised as the co-op copy", coop.Count + " found" + (coop.Count == 1 ? ", ours=" + coop[0].Ours : ""));
        }
        finally { try { if (p != null && !p.HasExited) p.Kill(); } catch { } Thread.Sleep(300); }
    }

    static void AuditVerify(string tmp)
    {
        if (Process.GetProcessesByName("GrowHome").Length > 0) { Check("LAUNCH-VERIFY", false, "no Grow Home is running during this check", "close the game and run the audit again"); return; }
        string dir = Path.Combine(tmp, "verify-game"); Directory.CreateDirectory(dir);
        gameDir = dir;
        File.WriteAllText(ModLogPath, "something else\nCoopMod loaded; Unity 4.6.1f1; players to connect to: 0\nsteam up\nCoopMod loaded; Unity 4.6.1f1; players to connect to: 0\n");
        Check("LAUNCH-VERIFY", ModLoadedCount() == 2, "the mod's \"loaded\" lines are counted (and nothing else)", ModLoadedCount() + " counted");
        File.Delete(ModLogPath);
        Check("LAUNCH-VERIFY", ModLoadedCount() == 0, "no log means not loaded", "");

        int took;
        // the mod says it loaded while the launcher is watching
        int before = ModLoadedCount();
        var writer = Bg("AuditStart worker", () => { Thread.Sleep(1500); File.AppendAllText(ModLogPath, "CoopMod loaded; Unity 4.6.1f1\n"); });
        writer.Start();
        var sw = Stopwatch.StartNew();
        var loaded = WaitForMod(before, 20, true, out took);
        Check("LAUNCH-VERIFY", loaded == Outcome.Loaded && sw.Elapsed.TotalSeconds < 6, "a mod that says it loaded is reported as loaded, quickly", loaded + " after " + sw.ElapsedMilliseconds + " ms");

        // nothing starts at all
        before = ModLoadedCount();
        Check("LAUNCH-VERIFY", WaitForMod(before, 3, true, out took) == Outcome.NoGame, "when nothing starts it says so", "");

        // the co-op copy runs but never loads the mod
        Process p = null;
        try
        {
            p = StartFake(Path.Combine(dir, "GrowHome.exe"), "-n 40 127.0.0.1");
            before = ModLoadedCount();
            Check("LAUNCH-VERIFY", WaitForMod(before, 3, true, out took) == Outcome.NoMod, "the co-op copy running without the mod is reported as such", "");
        }
        finally { try { if (p != null && !p.HasExited) p.Kill(); } catch { } Thread.Sleep(300); }

        // a regular game comes up instead of the co-op copy
        string other = Path.Combine(tmp, "regular-game"); Process q = null; int saved = wrongGameAfterSeconds;
        try
        {
            q = StartFake(Path.Combine(other, "GrowHome.exe"), "-n 40 127.0.0.1");
            wrongGameAfterSeconds = 1;
            before = ModLoadedCount();
            sw.Restart();
            var wrong = WaitForMod(before, 20, true, out took);
            Check("LAUNCH-VERIFY", wrong == Outcome.WrongGame && sw.Elapsed.TotalSeconds < 8, "the regular game starting instead of the co-op copy is caught and named", wrong + " after " + sw.ElapsedMilliseconds + " ms");
        }
        finally { wrongGameAfterSeconds = saved; try { if (q != null && !q.HasExited) q.Kill(); } catch { } Thread.Sleep(300); }

        // the co-op copy starts and closes again
        Process d = null;
        try
        {
            d = StartFake(Path.Combine(dir, "GrowHome.exe"), "-n 2 127.0.0.1");
            before = ModLoadedCount();
            var died = WaitForMod(before, 12, true, out took);
            Check("LAUNCH-VERIFY", died == Outcome.GameDied, "a game that closes right after starting is reported as such", died.ToString());
        }
        finally { try { if (d != null && !d.HasExited) d.Kill(); } catch { } Thread.Sleep(300); }
    }

    static void AuditInstall(string tmp)
    {
        string src = Path.Combine(tmp, "fake-original"), dst = Path.Combine(tmp, "coop-copy"), dst2 = Path.Combine(tmp, "coop-copy-nolinks");
        Directory.CreateDirectory(Path.Combine(src, "GrowHome_Data", "Managed"));
        var rnd = new Random(5);
        Action<string, int> make = (rel, mb) =>
        {
            string p = Path.Combine(src, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            var buf = new byte[1024 * 1024]; rnd.NextBytes(buf);
            using (var f = File.Create(p)) for (int i = 0; i < mb; i++) f.Write(buf, 0, buf.Length);
        };
        make("GrowHome.exe", 9);                                       // big, but not data: must be a real copy
        make("GrowHome_Data\\level0", 100);                           // big data file: linked
        make("GrowHome_Data\\resources.assets", 12);                  // big data file: linked
        make("GrowHome_Data\\Managed\\Assembly-CSharp.dll", 9);       // big, but the patcher writes it: must be a real copy
        make("GrowHome_Data\\small.txt", 0);
        File.WriteAllText(Path.Combine(src, "GrowHome_Data", "small.txt"), "tiny");
        string srcLevel = Path.Combine(src, "GrowHome_Data", "level0"), srcAsm = Path.Combine(src, "GrowHome_Data", "Managed", "Assembly-CSharp.dll"), srcExe = Path.Combine(src, "GrowHome.exe");
        string hashLevel = Sha256(srcLevel), hashAsm = Sha256(srcAsm), hashExe = Sha256(srcExe);

        installDir = tmp;
        long freeBefore = new DriveInfo(Path.GetPathRoot(tmp)).AvailableFreeSpace;
        var clock = Stopwatch.StartNew();
        CopyGame(src, dst, true);
        long ms = clock.ElapsedMilliseconds;
        long used = freeBefore - new DriveInfo(Path.GetPathRoot(tmp)).AvailableFreeSpace;
        Check("INSTALL-FAST", linkedFiles == 2 && copiedFiles == 3, "big data files are linked, everything else is copied", linkedFiles + " linked, " + copiedFiles + " copied");
        Check("INSTALL-FAST", used < 40L * 1024 * 1024, "linking the 112 MB of data uses almost no disk", (used / 1024) + " KB used");
        Check("INSTALL-FAST", ms < 3000, "setting up a copy with 130 MB of files takes seconds", ms + " ms");
        bool same = Sha256(Path.Combine(dst, "GrowHome_Data", "level0")) == hashLevel && Sha256(Path.Combine(dst, "GrowHome_Data", "Managed", "Assembly-CSharp.dll")) == hashAsm && Sha256(Path.Combine(dst, "GrowHome.exe")) == hashExe;
        Check("INSTALL-FAST", same && File.ReadAllText(Path.Combine(dst, "GrowHome_Data", "small.txt")) == "tiny", "the co-op copy has exactly the original's content", "");

        // what the patcher and the game write must never reach the original
        File.AppendAllText(Path.Combine(dst, "GrowHome_Data", "Managed", "Assembly-CSharp.dll"), "patched");
        File.AppendAllText(Path.Combine(dst, "GrowHome.exe"), "x");
        Check("INSTALL-FAST", Sha256(srcAsm) == hashAsm && Sha256(srcExe) == hashExe, "writing the patched assembly or the exe in the co-op copy leaves the original alone", "");

        // deleting the copy must not touch the original
        DeleteGameCopy(dst);
        Check("INSTALL-FAST", !Directory.Exists(dst) && Sha256(srcLevel) == hashLevel && File.Exists(srcExe), "deleting the co-op copy leaves every original file intact", "");

        // a read-only original that the copy links to keeps its read-only flag when the copy is deleted (a link shares the flags), and a read-only
        // real copy is still deleted
        string roSrc = Path.Combine(tmp, "ro-original.assets"), roCopy = Path.Combine(tmp, "ro-copy"), roLink = Path.Combine(roCopy, "GrowHome_Data", "ro.assets"), roReal = Path.Combine(roCopy, "real.txt");
        File.WriteAllText(roSrc, "original data");
        Directory.CreateDirectory(Path.GetDirectoryName(roLink));
        bool roLinked = CreateHardLink(roLink, roSrc, IntPtr.Zero);
        File.WriteAllText(roReal, "a real copy"); File.SetAttributes(roReal, FileAttributes.ReadOnly);
        File.SetAttributes(roSrc, File.GetAttributes(roSrc) | FileAttributes.ReadOnly);
        string roErr = "";
        try { DeleteGameCopy(roCopy); } catch (Exception e) { roErr = e.Message; }
        bool roKept = File.Exists(roSrc) && (File.GetAttributes(roSrc) & FileAttributes.ReadOnly) != 0 && File.ReadAllText(roSrc) == "original data";
        bool roGone = !Directory.Exists(roCopy) && Directory.GetDirectories(tmp, "ro-copy*").Length == 0;
        try { File.SetAttributes(roSrc, FileAttributes.Normal); File.Delete(roSrc); } catch { }
        Check("INSTALL-FAST", roLinked && roKept && roGone && roErr == "", "deleting the co-op copy leaves a read-only original it links to read-only (the flag is not cleared through the link), and read-only files of the copy are still deleted",
            "linked=" + roLinked + ", original still read-only=" + roKept + ", copy gone=" + roGone + (roErr.Length > 0 ? ", error " + roErr : ""));

        // a drive that cannot link: everything is copied and still works
        CopyGame(src, dst2, false);
        File.AppendAllText(Path.Combine(dst2, "GrowHome_Data", "level0"), "y");
        Check("INSTALL-FAST", linkedFiles == 0 && copiedFiles == 5 && Sha256(srcLevel) == hashLevel, "when linking is not possible everything is copied and the original is still safe", linkedFiles + " linked, " + copiedFiles + " copied");
        DeleteGameCopy(dst2);
    }
}

static partial class Program
{
    // Every account starts the game directly; Steam is only the fallback.
    static void AuditStartMode()
    {
        bool f = forceSteam, d = forceDirect;
        try
        {
            forceSteam = false; forceDirect = false;
            Check("LAUNCH-DIRECT-FIRST", !StartViaSteamFirst(), "with no options the game is started directly (no Steam restart, no Steam setting changed)", "");
            Check("LAUNCH-DIRECT-FIRST", !StartViaSteamFirst() && IsBorrower("\"LastOwner\" \"76561198000000001\"", "39734274"), "...also for an account that only borrows the game", "");
            forceSteam = true; forceDirect = false;
            Check("LAUNCH-DIRECT-FIRST", StartViaSteamFirst(), "starting through Steam first happens only when asked for (--force-steam)", "");
            forceSteam = true; forceDirect = true;
            Check("LAUNCH-DIRECT-FIRST", !StartViaSteamFirst(), "--direct always wins", "");
        }
        finally { forceSteam = f; forceDirect = d; }
    }
}
