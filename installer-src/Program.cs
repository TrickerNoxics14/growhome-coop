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
    const int BundledVersion = 2;                                   // version of the CoopMod.dll embedded in this exe

    static string installDir, gameDir;
    static bool noPrompt, auditMode;
    static string useGame, auditOut;
    static readonly List<string> extraVdf = new List<string>();

    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;
        try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
        Console.Title = "Grow Home Co-op";

        string skin = null, gameOverride = null;
        bool noUpdate = false, reinstall = false;
        installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "GrowHomeCoop");
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--no-update": noUpdate = true; break;
                case "--reinstall": reinstall = true; break;
                case "--no-prompt": noPrompt = true; break;
                case "--install-dir": installDir = args[++i]; break;
                case "--game-dir": gameOverride = args[++i]; break;
                case "--skin": skin = args[++i]; break;
                case "--via-steam": forceSteam = true; break;    // always start through Steam (the launcher picks this by itself for accounts that only borrow the game)
                case "--direct": forceDirect = true; break;      // never start through Steam
                case "--yes": assumeYes = true; break;           // answer every question with yes and do not wait for a key at the end
                case "--game-args": gameArgs = args[++i]; break; // arguments for the game window (default: 1280x720 borderless)
                case "--setup-steam": setupSteam = true; break;  // make Steam's Play button start the co-op copy
                case "--undo-steam-setup": undoSteam = true; break;   // put Steam's launch option back to what it was
                case "--use-game": useGame = args[++i]; break;   // use an existing patched copy at this path
                case "--audit": auditMode = true; break;         // run the self-checks of the updater and patcher (see audit/run-audit.ps1), then stop
                case "--audit-out": auditOut = args[++i]; break;
                case "--audit-vdf": extraVdf.Add(args[++i]); break;   // also check this Steam settings file (e.g. a copy from another PC)
            }
        }
        gameDir = Path.Combine(installDir, "game");
        if (useGame != null) { gameDir = useGame; installDir = Path.GetDirectoryName(Path.GetFullPath(useGame)); }
        if (auditMode) return Audit();

        try
        {
            bool installed = File.Exists(Path.Combine(gameDir, "GrowHome.exe")) && File.Exists(Path.Combine(gameDir, "GrowHome_Data", "Managed", "CoopMod.dll"));
            if (!installed || reinstall)
            {
                Install(gameOverride, skin);
                if (noPrompt) return 0;
            }
            if (noPrompt) { if (!noUpdate) CheckForUpdate(true); return 0; }   // tests: update right away and stop
            if (undoSteam) { EnsureSteamLoggedIn(90); return EnsureLaunchOption(true) ? 0 : 1; }
            if (setupSteam) { EnsureSteamLoggedIn(90); return EnsureLaunchOption(false) ? 0 : 1; }
            int code = Start(noUpdate);
            if (code != 0 && !assumeYes) { Console.WriteLine(); Console.WriteLine("Press Enter to close."); Console.ReadLine(); }
            Environment.Exit(code);
            return code;
        }
        catch (Exception e)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine();
            Console.WriteLine("Something went wrong: " + e.Message);
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("If you ask for help, send the text above.");
            if (!noPrompt && !assumeYes) { Console.WriteLine("Press Enter to close."); Console.ReadLine(); }
            return 1;
        }
    }

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
        Console.WriteLine("=== Grow Home Co-op installer ===");
        string src = gameOverride ?? FindGame();
        if (src == null) throw new Exception("Could not find Grow Home. Install it on Steam first, then run this again.");
        Console.WriteLine("Found Grow Home: " + src + "  (only read, never changed)");

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
            if (!noPrompt && !assumeYes)
            {
                Console.Write("An earlier install exists. Replace it? Your co-op save in it will be lost. Type YES to replace: ");
                if (Console.ReadLine() != "YES") throw new Exception("Cancelled.");
            }
            DeleteGameCopy(gameDir);
        }
        Directory.CreateDirectory(installDir);
        Console.WriteLine(sameDrive ? "Setting up the co-op copy in " + gameDir + " (most files are linked, not copied: this takes seconds)..." : "Copying the game to " + gameDir + " (about 850 MB, this takes a minute)...");
        CopyGame(src, gameDir, sameDrive);

        string managed = Path.Combine(gameDir, "GrowHome_Data", "Managed");
        File.WriteAllBytes(Path.Combine(managed, "CoopMod.dll"), ReadResource("CoopMod.dll"));
        File.WriteAllText(Path.Combine(gameDir, "coop-version.txt"), BundledVersion.ToString());
        Patch(managed);
        File.WriteAllText(Path.Combine(gameDir, "steam_appid.txt"), "323320");

        string me = MySteamId();
        if (me != null) File.WriteAllText(Path.Combine(installDir, "MY-STEAM-ID.txt"), me);

        if (skin == null && !noPrompt)
        {
            Console.WriteLine();
            Console.Write("Which BUD colour do you want? 1 = red (default), 2 = blue: ");
            skin = Console.ReadLine() == "2" ? "blue" : "red";
        }
        if (skin != null) File.WriteAllText(Path.Combine(gameDir, "coop-skin.txt"), skin.ToLower() == "blue" ? "Oceanic" : "Standard");

        string self = Assembly.GetExecutingAssembly().Location, keep = Path.Combine(installDir, "GrowHomeCoop.exe");
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) File.Copy(self, keep, true);   // the shortcut points at this permanent copy
        if (!noPrompt) MakeShortcut();
        Console.WriteLine();
        Console.WriteLine("Installed. A \"Grow Home Co-op\" shortcut is on your desktop. Use it (or this exe) to play.");
        Console.WriteLine();
        Console.WriteLine("HOW TO PLAY TOGETHER: everyone starts the game, picks CONTINUE and gets into the open world.");
        Console.WriteLine("ONE person presses F2 (a code appears), everyone else presses F3 and types the code.");
        Console.WriteLine();
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
        Console.WriteLine("\r  100%");
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
            if (pct / 5 != shown) { shown = pct / 5; Console.Write("\r  " + pct + "%  "); }
        }
        foreach (var d in Directory.GetDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)), root, total, ref done, ref shown);
    }

    // Deleting the co-op copy must never touch the original: linked files only lose this extra name. Read-only flags are cleared on the co-op copy only
    // for real copies (a linked file shares its flags with the original, and the original is left exactly as it is).
    static void DeleteGameCopy(string dir)
    {
        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { File.Delete(f); }
            catch (UnauthorizedAccessException) { var a = File.GetAttributes(f); File.SetAttributes(f, a & ~FileAttributes.ReadOnly); File.Delete(f); }
        }
        Directory.Delete(dir, true);
    }

    // Inserts a call to CoopMod.Bootstrap.Init() at the start of SteamManager.Awake (the original dll is kept as .orig).
    static void Patch(string managedDir)
    {
        string path = Path.Combine(managedDir, "Assembly-CSharp.dll");
        string orig = path + ".orig";
        if (!File.Exists(orig)) File.Copy(path, orig);
        PatchWithCecil(managedDir, path, orig);
        Console.WriteLine("Patched the co-op copy.");
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
        catch (Exception e) { Console.WriteLine("(Could not make a desktop shortcut: " + e.Message + ")"); }
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

    static string Get(string url, int timeoutMs)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Timeout = timeoutMs; req.ReadWriteTimeout = timeoutMs; req.UserAgent = "GrowHomeCoop";
        req.Headers[HttpRequestHeader.CacheControl] = "no-cache";
        using (var resp = req.GetResponse())
        using (var rd = new StreamReader(resp.GetResponseStream())) return rd.ReadToEnd();
    }

    static byte[] GetBytes(string url, int timeoutMs)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Timeout = timeoutMs; req.ReadWriteTimeout = timeoutMs; req.UserAgent = "GrowHomeCoop";
        using (var resp = req.GetResponse())
        using (var s = resp.GetResponseStream())
        {
            var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
        }
    }

    // Updates only come over https. (The audit also allows its own throw-away server on this PC.)
    static bool UrlAllowed(string url)
    {
        return url.StartsWith("https://") || (auditMode && url.StartsWith("http://127.0.0.1:"));
    }

    static string StagedDll { get { return Path.Combine(gameDir, "GrowHome_Data", "Managed", "CoopMod.dll.new"); } }
    static string StagedInfo { get { return Path.Combine(gameDir, "coop-staged.txt"); } }
    static string TargetDll { get { return Path.Combine(gameDir, "GrowHome_Data", "Managed", "CoopMod.dll"); } }

    static void Swap(byte[] data, int version)
    {
        File.Copy(TargetDll, TargetDll + ".bak", true);
        File.WriteAllBytes(TargetDll, data);
        File.WriteAllText(Path.Combine(gameDir, "coop-version.txt"), version.ToString());
    }

    // A mod update downloaded during an earlier start is put in place now, before the game runs (no network needed, so this is instant).
    static void ApplyStaged()
    {
        try
        {
            if (!File.Exists(StagedDll) || !File.Exists(StagedInfo)) return;
            var parts = File.ReadAllText(StagedInfo).Split(new[] { '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int version = int.Parse(parts[0]); string sha = parts[1];
            byte[] data = File.ReadAllBytes(StagedDll);
            if (version > InstalledVersion() && string.Equals(Sha256(data), sha, StringComparison.OrdinalIgnoreCase) && Process.GetProcessesByName("GrowHome").Length == 0)
            {
                Swap(data, version);
                Console.WriteLine("Co-op mod updated to version " + version + ".");
                Log("applied staged update " + version);
            }
            File.Delete(StagedDll); File.Delete(StagedInfo);
        }
        catch (Exception e) { Log("staged update failed: " + e.Message); }
    }

    // Looks for a newer mod. Short timeouts so a slow or blocked network (school Wi-Fi) never holds the game up.
    // applyNow = true (tests): install straight away. false (normal start): download and keep it for the next start.
    static bool UpdatesDisabled() { return File.Exists(Path.Combine(installDir, "coop-noupdate.txt")); }   // put this file next to the exe to never check

    // Returns the version that was downloaded and staged (or installed), 0 when nothing was. background = on a slow network it may take its time (nobody is waiting).
    static int CheckForUpdate(bool applyNow, bool background = false)
    {
        if (UpdatesDisabled()) { Log("update checks are switched off (coop-noupdate.txt)"); return 0; }
        int manifestMs = background ? 10000 : 4000, downloadMs = background ? 90000 : 15000;
        try
        {
            string json = Get(ManifestUrl + "?t=" + DateTime.UtcNow.Ticks, manifestMs);
            int latest = int.Parse(Field(json, "version") ?? "0");
            string url = Field(json, "dll"), sha = Field(json, "sha256"), notes = Field(json, "notes");
            int have = InstalledVersion();
            if (latest <= have) { Console.WriteLine("Co-op mod is up to date (version " + have + ")."); Log("up to date " + have); return 0; }
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(sha) || !UrlAllowed(url)) { Log("update info looks wrong"); return 0; }
            Console.WriteLine("New co-op mod version " + latest + (notes != null ? " (" + notes + ")" : "") + ": downloading...");
            byte[] data = GetBytes(url, downloadMs);
            if (!string.Equals(Sha256(data), sha, StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Downloaded file did not match its fingerprint; skipped for safety."); Log("update fingerprint mismatch"); return 0; }
            if (applyNow && Process.GetProcessesByName("GrowHome").Length == 0) { Swap(data, latest); Console.WriteLine("Updated."); Log("updated to " + latest); return latest; }
            File.WriteAllBytes(StagedDll, data);
            File.WriteAllText(StagedInfo, latest + "\n" + sha);
            Console.WriteLine("Update downloaded. It will be used the next time you start the game.");
            Log("staged update " + latest);
            return latest;
        }
        catch (Exception e) { Console.WriteLine("(No update check this time: " + e.Message + ")"); Log("update check failed: " + e.Message); }
        return 0;
    }

    static void Log(string m)
    {
        try { File.AppendAllText(Path.Combine(installDir, "launcher.log"), DateTime.Now.ToString("s") + " " + m + "\r\n"); } catch { }
    }

}
