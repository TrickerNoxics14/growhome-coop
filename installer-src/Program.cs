using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

// Grow Home Co-op: installer, updater and launcher in one exe.
//   first run  -> makes a separate patched copy of Grow Home (your normal game and saves are never touched)
//   every run  -> checks for a newer co-op mod, downloads it (SHA-256 verified), then starts the game
// Options: --no-update  --reinstall  --install-dir <path>  --skin red|blue  --no-prompt  --game-dir <path>
static partial class Program
{
    const string ExpectedAssemblySha = "8d99d51663061da95016e496116191850b6a5437250cfc38bb8576027b2aa40d";   // Grow Home Steam build 857617
    static string ManifestUrl = "https://raw.githubusercontent.com/TrickerNoxics14/growhome-coop/main/version.json";
    const int BundledVersion = 16;                                  // version of the CoopMod.dll embedded in this exe

    static string installDir, gameDir;
    static bool noPrompt, auditMode;
    static string useGame, auditOut;
    static readonly List<string> extraVdf = new List<string>();

    static string cliSkin, cliGameOverride;
    static bool cliNoUpdate, cliReinstall, consoleMode, autoPlay;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    // no crash may ever pop a Windows dialog on the player's screen: critical-error and fault boxes are off for this process (and the processes it starts)
    const uint SEM_FAILCRITICALERRORS = 0x0001, SEM_NOGPFAULTERRORBOX = 0x0002, SEM_NOOPENFILEERRORBOX = 0x8000;
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint GetErrorMode();
    static bool crashHandlerOn;

    static void QuietCrashes()
    {
        try { SetErrorMode(GetErrorMode() | SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX); } catch { }
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            // last line of defence: log it, record it (audit), and leave with an exit code instead of the Windows "has stopped working" dialog
            string what = e.ExceptionObject is Exception ? e.ExceptionObject.ToString() : Convert.ToString(e.ExceptionObject);
            try { Log("unhandled: " + what); } catch { }
            if (auditMode)
            {
                try { Check("THREAD-SAFE", false, "the launcher audit ran without an unhandled exception", (what ?? "").Split((char)10)[0].Trim()); WriteAuditOut(); } catch { }
                Environment.Exit(4);
            }
            Environment.Exit(1);
        };
        // the window's own thread: WinForms would catch an exception there (paint, timer, a posted action) and show its "Unhandled exception has
        // occurred in your application" dialog, which neither the handler above nor the error mode stops. Set before any window exists.
        try
        {
            System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException, false);
            System.Windows.Forms.Application.ThreadException += OnUiException;
            uiHandlerOn = true;
        }
        catch (Exception e) { try { Log("could not set the window's crash handler: " + e.Message); } catch { } }
        crashHandlerOn = true;
    }

    static bool uiHandlerOn; static int uiFaults; static volatile bool uiFaultTest;
    static void OnUiException(object sender, System.Threading.ThreadExceptionEventArgs e)
    {
        lock (auditLines) uiFaults++;
        try { Log("unhandled on the window's thread: " + e.Exception); } catch { }
        if (uiFaultTest) return;                 // the audit raised it on purpose: counted, nothing else
        if (auditMode)
        {
            try { Check("THREAD-SAFE", false, "the launcher audit ran without an unhandled exception on the window's thread", (Convert.ToString(e.Exception) ?? "").Split((char)10)[0].Trim()); WriteAuditOut(); } catch { }
            Environment.Exit(4);
        }
        Environment.Exit(1);
    }

    static int Main(string[] args)
    {
        QuietCrashes();
        AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;
        try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }

        installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "GrowHomeCoop");
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--no-update": cliNoUpdate = true; break;
                case "--reinstall": cliReinstall = true; break;
                case "--no-prompt": noPrompt = true; break;
                case "--install-dir": installDir = args[++i]; break;
                case "--game-dir": cliGameOverride = args[++i]; break;
                case "--skin": cliSkin = args[++i]; break;
                case "--via-steam": break;                       // old option, ignored: every account starts the game directly (the audit showed borrowing accounts can)
                case "--force-steam": forceSteam = true; break;  // start through Steam first (only for people who want Steam to own the start)
                case "--direct": forceDirect = true; break;      // never start through Steam
                case "--yes": assumeYes = true; break;           // answer every question with yes and do not wait for a key at the end (text mode)
                case "--console": consoleMode = true; break;     // text window instead of the game-styled window
                case "--auto-play": autoPlay = true; break;      // the window starts the game by itself
                case "--game-args": gameArgs = args[++i]; break; // arguments for the game window (default: 1280x720 borderless)
                case "--setup-steam": setupSteam = true; break;  // make Steam's Play button start the co-op copy
                case "--undo-steam-setup": undoSteam = true; break;   // put Steam's launch option back to what it was
                case "--use-game": useGame = args[++i]; break;   // use an existing patched copy at this path
                case "--audit": auditMode = true; break;         // run the self-checks of the updater, patcher and window (see audit/run-audit.ps1), then stop
                case "--audit-out": auditOut = args[++i]; break;
                case "--audit-vdf": extraVdf.Add(args[++i]); break;   // also check this Steam settings file (e.g. a copy from another PC)
            }
        }
        gameDir = Path.Combine(installDir, "game");
        if (useGame != null) { gameDir = useGame; installDir = Path.GetDirectoryName(Path.GetFullPath(useGame)); }
        // --use-game pointed at the regular Steam game: refused before anything (install, mod, steam_appid.txt, launcher.log) is written there
        if (useGame != null && !auditMode && IsRegularGameFolder(Path.GetFullPath(useGame))) { AttachConsole(-1); Console.WriteLine("--use-game " + useGame + " is the regular Grow Home's own folder. Nothing was changed or started."); return 2; }

        bool window = !consoleMode && !assumeYes && !noPrompt && !auditMode && !setupSteam && !undoSteam;
        if (!window) { AttachConsole(-1); try { Console.Title = "Grow Home Co-op"; } catch { } }     // text modes print into the console they were started from
        if (auditMode) return Audit();
        launcherLock = TakeLock(LauncherMutexName);
        if (launcherLock == null)
        {
            PointToOtherLauncher();
            if (!window) Console.WriteLine("Grow Home Co-op is already open.");
            return 5;
        }
        if (window) return GuiMain();

        int code = RunFlow();
        if (code != 0 && !assumeYes) { Console.WriteLine(); Console.WriteLine("Press Enter to close."); Console.ReadLine(); }
        Environment.Exit(code);
        return code;
    }

    // The whole job: install when needed, then start the game. The console and the window both run this; the window gives Say/Confirm/Progress its own hooks.
    static int RunFlow()
    {
        try
        {
            if (NeedsInstall())
            {
                Install(cliGameOverride, cliSkin);
                if (noPrompt) return 0;
            }
            if (noPrompt) { if (!cliNoUpdate) CheckForUpdate(true); return 0; }   // tests: update right away and stop
            if (undoSteam) { EnsureSteamLoggedIn(90); return EnsureLaunchOption(true) ? 0 : 1; }
            if (setupSteam) { EnsureSteamLoggedIn(90); return EnsureLaunchOption(false) ? 0 : 1; }
            return Start(cliNoUpdate);
        }
        catch (Exception e)
        {
            Say("");
            Say("Something went wrong: " + e.Message);
            Say("If you ask for help, send launcher.log from the GrowHomeCoop folder.");
            Log("failed: " + e);
            return 1;
        }
    }

    // The co-op copy is there: its exe and the patched assembly (the patcher keeps the original next to it as .orig). A missing CoopMod.dll alone
    // is NOT "not installed": the start puts the bundled one back (EnforceModPin), where a reinstall would delete the copy and the co-op save in it.
    static bool CopyInstalled(string dir)
    {
        string managed = Path.Combine(dir, "GrowHome_Data", "Managed");
        if (!File.Exists(Path.Combine(dir, "GrowHome.exe")) || !File.Exists(Path.Combine(managed, "Assembly-CSharp.dll"))) return false;
        if (File.Exists(Path.Combine(managed, "CoopMod.dll"))) return true;
        return File.Exists(Path.Combine(managed, "Assembly-CSharp.dll.orig")) && BundledMod() != null;
    }

    static bool NeedsInstall() { return !CopyInstalled(gameDir) || cliReinstall; }

    // ---- embedded files -----------------------------------------------------------------------------------------------------------------

    static Assembly ResolveEmbedded(object sender, ResolveEventArgs e)
    {
        string name = new AssemblyName(e.Name).Name + ".dll";
        byte[] data = ReadResource(name);
        return data == null ? null : Assembly.Load(data);
    }

    static byte[] ReadResource(string name)
    {
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            if (s == null) return null;
            var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
        }
    }

    // ---- finding things -----------------------------------------------------------------------------------------------------------------

    static IEnumerable<string> SteamRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
        };
        try { var p = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; if (p != null) list.Add(p.Replace('/', '\\')); } catch { }
        foreach (var p in list) if (Directory.Exists(p) && seen.Add(p)) yield return p;
    }

    static string FindGame()
    {
        foreach (var steam in SteamRoots())
        {
            var libs = new List<string> { steam };
            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            foreach (var lib in libs)
            {
                string g = Path.Combine(lib, "steamapps", "common", "GrowHome");
                if (File.Exists(Path.Combine(g, "GrowHome.exe"))) return g;
            }
        }
        return null;
    }

    static string MySteamId()
    {
        try
        {
            object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null);
            if (v is int && (int)v != 0) return (76561197960265728UL + (ulong)(int)v).ToString();
        }
        catch { }
        return null;
    }

    static string Sha256(string path)
    {
        using (var f = File.OpenRead(path)) return string.Concat(SHA256.Create().ComputeHash(f).Select(b => b.ToString("x2")).ToArray());
    }

    static string Sha256(byte[] data) { return string.Concat(SHA256.Create().ComputeHash(data).Select(b => b.ToString("x2")).ToArray()); }

    // ---- install ------------------------------------------------------------------------------------------------------------------------

    static void Install(string gameOverride, string skin)
    {
        if (IsRegularGameFolder(gameDir) || (gameOverride != null && SamePath(gameOverride, gameDir))) throw new Exception("The co-op folder " + gameDir + " is the regular Grow Home's own folder. Nothing was changed.");
        if (Directory.Exists(gameDir) && OurGameRunning()) throw new Exception("The co-op game is running. Close it first, then install again.");
        Say("Installing Grow Home Co-op");
        string src = gameOverride ?? FindGame();
        if (src == null) throw new Exception("Could not find Grow Home. Install it on Steam first, then run this again.");
        Say("Found Grow Home: " + src + "  (only read, never changed)");

        string asm = Path.Combine(src, "GrowHome_Data", "Managed", "Assembly-CSharp.dll");
        string sha = Sha256(asm);
        if (sha != ExpectedAssemblySha)
            throw new Exception("Your Grow Home version differs from the one this mod supports (Steam build 857617).\nIn Steam: right-click Grow Home > Properties > Installed Files > Verify integrity, then try again.\nYour file hash: " + sha);

        string root = Path.GetPathRoot(Path.GetFullPath(installDir));
        bool sameDrive = string.Equals(root, Path.GetPathRoot(Path.GetFullPath(src)), StringComparison.OrdinalIgnoreCase);
        long free = new DriveInfo(root).AvailableFreeSpace;
        long need = sameDrive ? 250L * 1024 * 1024 : 1500L * 1024 * 1024;       // linked files need no space of their own
        if (free < need) throw new Exception("Not enough free disk space on " + root + " (need about " + (need / (1024 * 1024)) + " MB).");

        if (Directory.Exists(gameDir))
        {
            if (!noPrompt && !assumeYes && !ConfirmStrict("An earlier install exists. Replace it? Your co-op save in it will be lost.")) throw new Exception("Cancelled.");
            DeleteGameCopy(gameDir);
        }
        Directory.CreateDirectory(installDir);
        Say(sameDrive ? "Setting up the co-op copy in " + gameDir + " (most files are linked, not copied: this takes seconds)..." : "Copying the game to " + gameDir + " (about 850 MB, this takes a minute)...");
        CopyGame(src, gameDir, sameDrive);

        string managed = Path.Combine(gameDir, "GrowHome_Data", "Managed");
        File.WriteAllBytes(Path.Combine(managed, "CoopMod.dll"), ReadResource("CoopMod.dll"));
        File.WriteAllText(Path.Combine(gameDir, "coop-version.txt"), BundledVersion.ToString());
        Patch(managed);
        File.WriteAllText(Path.Combine(gameDir, "steam_appid.txt"), "323320");

        string me = MySteamId();
        if (me != null) File.WriteAllText(Path.Combine(installDir, "MY-STEAM-ID.txt"), me);

        if (skin == null && !noPrompt) skin = Choose("Which BUD colour do you want?", "Red", "Blue") == 1 ? "blue" : "red";
        if (skin != null) File.WriteAllText(Path.Combine(gameDir, "coop-skin.txt"), skin.ToLower() == "blue" ? "Oceanic" : "Standard");

        string self = Assembly.GetExecutingAssembly().Location, keep = Path.Combine(installDir, "GrowHomeCoop.exe");
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) File.Copy(self, keep, true);   // the shortcut points at this permanent copy
        if (!noPrompt) MakeShortcut();
        Say("");
        Say("Installed. A \"Grow Home Co-op\" shortcut is on your desktop. Use it (or this exe) to play.");
        Say("HOW TO PLAY TOGETHER: everyone starts the game, picks CONTINUE and gets into the open world.");
        Say("ONE person presses F2 (a code appears), everyone else presses F3 and types the code.");
    }

    // The big read-only data files of the game are linked instead of copied when the co-op copy is on the same drive: the install takes seconds instead of
    // minutes and needs almost no disk space. Anything the game or the patcher writes (the exe, the Managed folder with the patched assembly, logs, saves) is always a real copy.
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string newFile, string existingFile, IntPtr reserved);

    const long LinkMinSize = 8L * 1024 * 1024;
    static bool linksWork = true;                               // switched off at the first failure (other drive, FAT, ...)
    static int linkedFiles, copiedFiles; static long linkedBytes, copiedBytes;

    static bool MayLink(string relativePath, long size)
    {
        if (size < LinkMinSize) return false;
        string p = relativePath.Replace('/', '\\');
        return p.StartsWith("GrowHome_Data\\", StringComparison.OrdinalIgnoreCase) && !p.StartsWith("GrowHome_Data\\Managed\\", StringComparison.OrdinalIgnoreCase);
    }

    static long TreeSize(string dir)
    {
        long n = 0;
        foreach (var f in Directory.GetFiles(dir)) n += new FileInfo(f).Length;
        foreach (var d in Directory.GetDirectories(dir)) n += TreeSize(d);
        return n;
    }

    static void CopyGame(string from, string to, bool allowLinks)
    {
        linksWork = allowLinks; linkedFiles = copiedFiles = 0; linkedBytes = copiedBytes = 0;
        long total = Math.Max(1, TreeSize(from)), done = 0; int shown = -1;
        var clock = Stopwatch.StartNew();
        CopyTree(from, to, from, total, ref done, ref shown);
        ShowInstallProgress(100);
        Log("installed in " + (int)clock.Elapsed.TotalSeconds + " s: " + linkedFiles + " files linked (" + linkedBytes / (1024 * 1024) + " MB), " + copiedFiles + " copied (" + copiedBytes / (1024 * 1024) + " MB)");
    }

    static void CopyTree(string from, string to, string root, long total, ref long done, ref int shown)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
        {
            string dest = Path.Combine(to, Path.GetFileName(f));
            long size = new FileInfo(f).Length;
            bool linked = false;
            if (linksWork && MayLink(f.Substring(root.Length).TrimStart('\\', '/'), size))
            {
                linked = CreateHardLink(dest, f, IntPtr.Zero);
                if (!linked) { Log("hard link failed (error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + "): copying instead"); linksWork = false; }
            }
            if (linked) { linkedFiles++; linkedBytes += size; } else { File.Copy(f, dest, true); copiedFiles++; copiedBytes += size; }
            done += size;
            int pct = (int)(done * 100 / total);
            if (pct / 5 != shown) { shown = pct / 5; ShowInstallProgress(pct); }
        }
        foreach (var d in Directory.GetDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)), root, total, ref done, ref shown);
    }

    // Deleting the co-op copy must never touch the original: linked files only lose this extra name. A read-only file is deleted through one handle
    // that puts its flags back before it closes (a linked file shares its flags with the original, and the original is left exactly as it is).
    static void DeleteGameCopy(string dir)
    {
        // renamed first: a file the game (or anything else) still holds stops it here, before a single file is gone
        string doomed = dir.TrimEnd(Path.DirectorySeparatorChar, '/') + ".old-" + DateTime.Now.Ticks;
        try { Directory.Move(dir, doomed); }
        catch (IOException e) { throw new Exception("The co-op copy is in use (is the game still running?). Nothing was deleted. " + e.Message); }
        catch (UnauthorizedAccessException e) { throw new Exception("The co-op copy is in use (is the game still running?). Nothing was deleted. " + e.Message); }
        dir = doomed;
        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { File.Delete(f); }
            catch (UnauthorizedAccessException) { DeleteReadOnly(f); }
        }
        Directory.Delete(dir, true);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct FileBasicInfo { public long Creation, LastAccess, LastWrite, Change; public uint Attributes; }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle h, int infoClass, out FileBasicInfo info, int size);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, int infoClass, ref FileBasicInfo info, int size);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, int infoClass, ref byte deleteFile, int size);

    // A read-only file in the co-op copy. Its flags live on the file itself, which a linked file shares with the original. Through one handle:
    // read-only off, marked for deletion, flags put back exactly, then closed - this name goes, and the original keeps exactly the flags it had.
    static void DeleteReadOnly(string f)
    {
        string why = null;
        using (var h = CreateFileW(f, 0x00010000 | 0x0100 | 0x0080, 7, IntPtr.Zero, 3, 0, IntPtr.Zero))     // DELETE | FILE_WRITE_ATTRIBUTES | FILE_READ_ATTRIBUTES, share all, OPEN_EXISTING
        {
            int size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(FileBasicInfo));
            FileBasicInfo had;
            if (h.IsInvalid) why = "open";
            else if (!GetFileInformationByHandleEx(h, 0, out had, size)) why = "read flags";
            else
            {
                var off = new FileBasicInfo { Attributes = (had.Attributes & ~(uint)FileAttributes.ReadOnly) == 0 ? (uint)FileAttributes.Normal : had.Attributes & ~(uint)FileAttributes.ReadOnly };   // 0 times: left as they are
                var back = new FileBasicInfo { Attributes = had.Attributes };
                byte del = 1;
                if (!SetFileInformationByHandle(h, 0, ref off, size)) why = "clear read-only";
                else
                {
                    bool marked = SetFileInformationByHandle(h, 4, ref del, 1);                       // FileDispositionInfo: gone when the handle closes
                    if (!SetFileInformationByHandle(h, 0, ref back, size)) Log("could not put the flags back on " + f + " (error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ")");
                    if (!marked) why = "mark for deletion";
                }
            }
            if (why != null) why += " failed (error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ")";
        }
        if (why != null) throw new IOException("Could not delete the read-only file " + f + ": " + why + ". Nothing of the original was changed.");
    }

    // Inserts a call to CoopMod.Bootstrap.Init() at the start of SteamManager.Awake (the original dll is kept as .orig).
    static void Patch(string managedDir)
    {
        string path = Path.Combine(managedDir, "Assembly-CSharp.dll");
        string orig = path + ".orig";
        if (!File.Exists(orig)) File.Copy(path, orig);
        PatchWithCecil(managedDir, path, orig);
        Say("Patched the co-op copy.");
    }

    // Reads the original assembly, adds the call, and writes the result to 'path' (the audit writes it somewhere else to keep the install untouched).
    static void PatchWithCecil(string managedDir, string path, string orig)
    {
        var resolver = new Mono.Cecil.DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managedDir);
        var asm = Mono.Cecil.AssemblyDefinition.ReadAssembly(orig, new Mono.Cecil.ReaderParameters { AssemblyResolver = resolver });
        var mod = Mono.Cecil.AssemblyDefinition.ReadAssembly(Path.Combine(managedDir, "CoopMod.dll"));
        var init = asm.MainModule.ImportReference(mod.MainModule.GetType("CoopMod.Bootstrap").Methods.First(m => m.Name == "Init"));
        var awake = asm.MainModule.GetType("SteamManager").Methods.First(m => m.Name == "Awake");
        var il = awake.Body.GetILProcessor();
        il.InsertBefore(awake.Body.Instructions[0], il.Create(Mono.Cecil.Cil.OpCodes.Call, init));
        asm.Write(path);
    }

    static void MakeShortcut()
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(t);
            dynamic sc = shell.CreateShortcut(Path.Combine(desktop, "Grow Home Co-op.lnk"));
            sc.TargetPath = Path.Combine(installDir, "GrowHomeCoop.exe");
            sc.WorkingDirectory = installDir;
            sc.IconLocation = Path.Combine(gameDir, "GrowHome.exe") + ",0";
            sc.Description = "Grow Home Co-op (modded copy)";
            sc.Save();
        }
        catch (Exception e) { Say("(Could not make a desktop shortcut: " + e.Message + ")"); }
    }

    // ---- update -------------------------------------------------------------------------------------------------------------------------

    static int InstalledVersion()
    {
        int v; string f = Path.Combine(gameDir, "coop-version.txt");
        return File.Exists(f) && int.TryParse(File.ReadAllText(f).Trim(), out v) ? v : 0;
    }

    static string Field(string json, string name)
    {
        var m = Regex.Match(json, "\"" + name + "\"\\s*:\\s*(\"([^\"]*)\"|(\\d+))");
        return m.Success ? (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value) : null;
    }

    // Downloads are small and only from the allowed hosts, also after any redirect.
    static string Get(string url, int timeoutMs)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Timeout = timeoutMs; req.ReadWriteTimeout = timeoutMs; req.UserAgent = "GrowHomeCoop"; req.MaximumAutomaticRedirections = 3;
        req.Headers[HttpRequestHeader.CacheControl] = "no-cache";
        using (var resp = req.GetResponse())
        {
            if (!UrlAllowed(resp.ResponseUri.ToString())) throw new WebException("the answer came from a place that is not allowed");
            using (var s = resp.GetResponseStream()) return Encoding.UTF8.GetString(ReadLimited(s, 64 * 1024));
        }
    }

    static byte[] GetBytes(string url, int timeoutMs)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Timeout = timeoutMs; req.ReadWriteTimeout = timeoutMs; req.UserAgent = "GrowHomeCoop"; req.MaximumAutomaticRedirections = 3;
        using (var resp = req.GetResponse())
        {
            if (!UrlAllowed(resp.ResponseUri.ToString())) throw new WebException("the download came from a place that is not allowed");
            using (var s = resp.GetResponseStream()) return ReadLimited(s, 8 * 1024 * 1024);
        }
    }

    static byte[] ReadLimited(Stream s, int max)
    {
        var ms = new MemoryStream(); var buf = new byte[16384]; int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); if (ms.Length > max) throw new InvalidDataException("the answer is far bigger than expected"); }
        return ms.ToArray();
    }

    static string StagedDll { get { return Path.Combine(gameDir, "GrowHome_Data", "Managed", "CoopMod.dll.new"); } }
    static string StagedInfo { get { return Path.Combine(gameDir, "coop-staged.txt"); } }
    static string TargetDll { get { return Path.Combine(gameDir, "GrowHome_Data", "Managed", "CoopMod.dll"); } }


    // A mod update downloaded during an earlier start is put in place now, before the game runs (no network needed, so this is instant).
    static void ApplyStaged()
    {
        try
        {
            if (!File.Exists(StagedDll) || !File.Exists(StagedInfo)) return;
            var parts = File.ReadAllText(StagedInfo).Split(new[] { '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int version = int.Parse(parts[0]); string sha = parts[1];
            byte[] data = File.ReadAllBytes(StagedDll);
            bool good = version > InstalledVersion() && string.Equals(Sha256(data), sha, StringComparison.OrdinalIgnoreCase);
            // a game that runs keeps the mod it loaded: the update waits for the next start instead of being thrown away
            if (good && Process.GetProcessesByName("GrowHome").Length > 0) { Log("a game runs; staged update " + version + " is kept for the next start"); return; }
            if (good)
            {
                if (!Swap(data, version)) { Log("staged update " + version + " kept for the next start"); return; }
                Say("Co-op mod updated to version " + version + ".");
                Log("applied staged update " + version);
            }
            File.Delete(StagedDll); File.Delete(StagedInfo);
        }
        catch (Exception e) { Log("staged update failed: " + e.Message); }
    }

    // Looks for a newer mod. Short timeouts so a slow or blocked network (school Wi-Fi) never holds the game up.
    // applyNow = true (tests): install straight away. false (normal start): download and keep it for the next start.
    static string LastCheck = "";   // what the last check found, read by the launcher's button: "current", "staged", "installed", "failed" or "off"
    static bool UpdatesDisabled() { return File.Exists(Path.Combine(installDir, "coop-noupdate.txt")); }   // put this file next to the exe to never check

    // Returns the version that was downloaded and staged (or installed), 0 when nothing was. background = on a slow network it may take its time (nobody is waiting).
    static int CheckForUpdate(bool applyNow, bool background = false, bool byHand = false)
    {
        if (UpdatesDisabled() && !byHand) { LastCheck = "off"; Log("update checks are switched off (coop-noupdate.txt)"); return 0; }
        LastCheck = "failed";   // every path below that does not succeed keeps this; the ones that succeed set their own value
        int manifestMs = background ? 10000 : 4000, downloadMs = background ? 90000 : 15000;
        try
        {
            string json = Get(ManifestUrl + "?t=" + DateTime.UtcNow.Ticks, manifestMs);
            int latest = int.Parse(Field(json, "version") ?? "0");
            string url = Field(json, "dll"), sha = Field(json, "sha256"), notes = Field(json, "notes");
            int have = InstalledVersion();
            if (latest <= have) { LastCheck = "current"; Say("Co-op mod is up to date (version " + have + ")."); Log("up to date " + have); return 0; }
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(sha) || !UrlAllowed(url)) { Say("The update server sent something this launcher cannot use; nothing was changed."); Log("update info looks wrong"); return 0; }
            if (!VerifyManifest(latest, sha, url, Field(json, "signature"))) { Say("This update is not signed by the author; skipped for safety."); Log("update has no valid signature: refused"); return 0; }
            Say("New co-op mod version " + latest + (notes != null ? " (" + notes + ")" : "") + ": downloading...");
            byte[] data = GetBytes(url, downloadMs);
            if (!string.Equals(Sha256(data), sha, StringComparison.OrdinalIgnoreCase)) { Say("Downloaded file did not match its fingerprint; skipped for safety."); Log("update fingerprint mismatch"); return 0; }
            if (applyNow && Process.GetProcessesByName("GrowHome").Length == 0 && Swap(data, latest)) { LastCheck = "installed"; Say("Updated."); Log("updated to " + latest); return latest; }
            File.WriteAllBytes(StagedDll, data);
            File.WriteAllText(StagedInfo, latest + "\n" + sha);
            LastCheck = "staged"; Say("Update downloaded. It will be used the next time you start the game.");
            Log("staged update " + latest);
            return latest;
        }
        catch (Exception e) { Say("(No update check this time: " + e.Message + ")"); Log("update check failed: " + e.Message); }
        return 0;
    }

    static void Log(string m)
    {
        try { File.AppendAllText(Path.Combine(installDir, "launcher.log"), DateTime.Now.ToString("s") + " " + m + "\r\n"); } catch { }
    }

}
