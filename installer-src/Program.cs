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
static class Program
{
    const string ExpectedAssemblySha = "8d99d51663061da95016e496116191850b6a5437250cfc38bb8576027b2aa40d";   // Grow Home Steam build 857617
    const string ManifestUrl = "https://raw.githubusercontent.com/TrickerNoxics14/growhome-coop/main/version.json";
    const int BundledVersion = 1;                                   // version of the CoopMod.dll embedded in this exe

    static string installDir, gameDir;
    static bool noPrompt, viaSteam;
    static string useGame;

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
                case "--via-steam": viaSteam = true; break;      // start through Steam (needed when this Steam account only has the game via Family Sharing)
                case "--use-game": useGame = args[++i]; break;   // use an existing patched copy at this path
            }
        }
        gameDir = Path.Combine(installDir, "game");
        if (useGame != null) { gameDir = useGame; installDir = Path.GetDirectoryName(Path.GetFullPath(useGame)); }

        try
        {
            bool installed = File.Exists(Path.Combine(gameDir, "GrowHome.exe")) && File.Exists(Path.Combine(gameDir, "GrowHome_Data", "Managed", "CoopMod.dll"));
            if (!installed || reinstall)
            {
                Install(gameOverride, skin);
                if (noPrompt) return 0;
            }
            if (!noUpdate) CheckForUpdate();
            if (noPrompt) return 0;
            Launch();
            return 0;
        }
        catch (Exception e)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine();
            Console.WriteLine("Something went wrong: " + e.Message);
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("If you ask for help, send the text above.");
            if (!noPrompt) { Console.WriteLine("Press Enter to close."); Console.ReadLine(); }
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
        long free = new DriveInfo(root).AvailableFreeSpace;
        if (free < 1500L * 1024 * 1024) throw new Exception("Not enough free disk space on " + root + " (need about 1.5 GB).");

        if (Directory.Exists(gameDir))
        {
            if (!noPrompt)
            {
                Console.Write("An earlier install exists. Replace it? Your co-op save in it will be lost. Type YES to replace: ");
                if (Console.ReadLine() != "YES") throw new Exception("Cancelled.");
            }
            Directory.Delete(gameDir, true);
        }
        Directory.CreateDirectory(installDir);
        Console.WriteLine("Copying the game to " + gameDir + " (about 850 MB, this takes a minute)...");
        CopyDir(src, gameDir);

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

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
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

    static void CheckForUpdate()
    {
        try
        {
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "GrowHomeCoop";
                wc.Headers[HttpRequestHeader.CacheControl] = "no-cache";
                string json = wc.DownloadString(ManifestUrl + "?t=" + DateTime.UtcNow.Ticks);
                int latest = int.Parse(Field(json, "version") ?? "0");
                string url = Field(json, "dll"), sha = Field(json, "sha256"), notes = Field(json, "notes");
                int have = InstalledVersion();
                if (latest <= have) { Console.WriteLine("Co-op mod is up to date (version " + have + ")."); return; }
                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(sha) || !url.StartsWith("https://")) { Console.WriteLine("Update information looks wrong; skipping the update."); return; }
                Console.WriteLine("Updating the co-op mod to version " + latest + (notes != null ? ": " + notes : "") + " ...");
                byte[] data = wc.DownloadData(url);
                if (!string.Equals(Sha256(data), sha, StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Downloaded file did not match its fingerprint; update skipped for safety."); return; }
                string target = Path.Combine(gameDir, "GrowHome_Data", "Managed", "CoopMod.dll");
                if (Process.GetProcessesByName("GrowHome").Length > 0) { Console.WriteLine("Grow Home is running; the update will apply next time."); return; }
                string backup = target + ".bak";
                File.Copy(target, backup, true);
                File.WriteAllBytes(target, data);
                File.WriteAllText(Path.Combine(gameDir, "coop-version.txt"), latest.ToString());
                Console.WriteLine("Updated.");
            }
        }
        catch (Exception e) { Console.WriteLine("(Could not check for updates: " + e.Message + "). Starting with the version you have."); }
    }

    // ---- launch -------------------------------------------------------------------------------------------------------------------------

    static void Launch()
    {
        if (Process.GetProcessesByName("GrowHome").Length > 0)
        {
            Console.WriteLine("Grow Home is already running. Close it first (only one copy can run at a time).");
            Console.WriteLine("Press Enter to close."); Console.ReadLine(); return;
        }
        if (viaSteam)
        {
            Process.Start(new ProcessStartInfo("steam://rungameid/323320") { UseShellExecute = true });
            Console.WriteLine("Starting Grow Home Co-op through Steam...");
            System.Threading.Thread.Sleep(2500);
            return;
        }
        var psi = new ProcessStartInfo(Path.Combine(gameDir, "GrowHome.exe"), "-screen-width 1280 -screen-height 720 -popupwindow")
        {
            WorkingDirectory = gameDir,
            UseShellExecute = false,
        };
        psi.EnvironmentVariables["SteamAppId"] = "323320";
        psi.EnvironmentVariables["SteamGameId"] = "323320";
        Process.Start(psi);
        Console.WriteLine("Starting Grow Home Co-op... choose CONTINUE, get into the open world, then F2 (make a code) or F3 (type a code).");
        System.Threading.Thread.Sleep(2500);
    }
}
