using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

// Everything about Steam: which account is logged in, whether it only borrows the game (Family Sharing), whether a Grow Home process is the co-op copy or the
// regular game, and the Steam "launch option" that makes Steam's own Play button (and steam://rungameid) start the co-op copy instead of the regular game.
static partial class Program
{
    const string AppId = "323320";

    // ---- Steam's text format (KeyValues) --------------------------------------------------------------------------------------------
    // Only used to FIND the place to change in localconfig.vdf; the change itself is a small text insertion so the rest of the file stays byte for byte as Steam wrote it.

    sealed class VNode
    {
        public string Key, Value;                    // Value == null for a block
        public List<VNode> Kids;                     // null for a plain value
        public int KeyStart, OpenBrace = -1, CloseBrace = -1, ValueStart, ValueEnd;   // positions in the text (ValueStart..ValueEnd is the value between its quotes)
    }

    static List<VNode> ParseVdf(string text)
    {
        int i = 0;
        var root = new List<VNode>();
        ParseBlock(text, ref i, root, false);
        return root;
    }

    static string ReadQuoted(string t, ref int i, out int start, out int end)
    {
        // t[i] is the opening quote
        var sb = new StringBuilder();
        i++; start = i;
        while (i < t.Length && t[i] != '"')
        {
            if (t[i] == '\\' && i + 1 < t.Length)
            {
                char n = t[i + 1];
                sb.Append(n == 'n' ? '\n' : n == 't' ? '\t' : n);   // \\ \" \n \t
                i += 2;
            }
            else { sb.Append(t[i]); i++; }
        }
        if (i >= t.Length) throw new FormatException("a quote is never closed");
        end = i; i++;
        return sb.ToString();
    }

    static void SkipSpace(string t, ref int i)
    {
        while (i < t.Length)
        {
            if (char.IsWhiteSpace(t[i])) i++;
            else if (t[i] == '/' && i + 1 < t.Length && t[i + 1] == '/') { while (i < t.Length && t[i] != '\n') i++; }
            else break;
        }
    }

    static void ParseBlock(string t, ref int i, List<VNode> into, bool inner)
    {
        while (true)
        {
            SkipSpace(t, ref i);
            if (i >= t.Length) { if (inner) throw new FormatException("a block is never closed"); return; }
            if (t[i] == '}') { if (!inner) throw new FormatException("a closing brace without a block"); i++; return; }
            if (t[i] != '"') throw new FormatException("unexpected character at " + i);
            var node = new VNode { KeyStart = i };
            int s, e;
            node.Key = ReadQuoted(t, ref i, out s, out e);
            SkipSpace(t, ref i);
            if (i >= t.Length) throw new FormatException("a key without a value");
            if (t[i] == '{')
            {
                node.OpenBrace = i; i++;
                node.Kids = new List<VNode>();
                ParseBlock(t, ref i, node.Kids, true);
                node.CloseBrace = i - 1;
            }
            else if (t[i] == '"')
            {
                node.Value = ReadQuoted(t, ref i, out node.ValueStart, out node.ValueEnd);
            }
            else throw new FormatException("a key without a value at " + i);
            into.Add(node);
        }
    }

    static VNode Child(List<VNode> list, string key)
    {
        if (list == null) return null;
        foreach (var n in list) if (string.Equals(n.Key, key, StringComparison.OrdinalIgnoreCase)) return n;
        return null;
    }

    static VNode AppsBlock(List<VNode> root)
    {
        var n = Child(root, "UserLocalConfigStore"); n = n == null ? null : Child(n.Kids, "Software");
        n = n == null ? null : Child(n.Kids, "Valve"); n = n == null ? null : Child(n.Kids, "Steam"); n = n == null ? null : Child(n.Kids, "apps");
        return n;
    }

    static string EscapeVdf(string s) { return s.Replace("\\", "\\\\").Replace("\"", "\\\""); }

    // The launch option Steam has for this app ("" when none). Throws FormatException when the text is not a localconfig.vdf.
    static string GetLaunchOption(string vdf, string appId)
    {
        var apps = AppsBlock(ParseVdf(vdf));
        if (apps == null) throw new FormatException("no apps section");
        var app = Child(apps.Kids, appId);
        var opt = app == null ? null : Child(app.Kids, "LaunchOptions");
        return opt == null || opt.Value == null ? "" : opt.Value;
    }

    // Returns the text with the launch option set (inserted, or its value replaced). Everything else is left exactly as it was.
    static string SetLaunchOption(string vdf, string appId, string options)
    {
        var root = ParseVdf(vdf);
        var apps = AppsBlock(root);
        if (apps == null) throw new FormatException("no apps section");
        string nl = vdf.Contains("\r\n") ? "\r\n" : "\n";
        var app = Child(apps.Kids, appId);
        string result;
        if (app != null && app.Kids != null)
        {
            var opt = Child(app.Kids, "LaunchOptions");
            if (opt != null && opt.Value != null)
                result = vdf.Substring(0, opt.ValueStart) + EscapeVdf(options) + vdf.Substring(opt.ValueEnd);
            else
            {
                string indent = IndentOf(vdf, app.KeyStart) + "\t";
                result = vdf.Insert(app.OpenBrace + 1, nl + indent + "\"LaunchOptions\"\t\t\"" + EscapeVdf(options) + "\"");
            }
        }
        else if (app == null)
        {
            string indent = IndentOf(vdf, apps.KeyStart) + "\t";
            string block = nl + indent + "\"" + appId + "\"" + nl + indent + "{" + nl + indent + "\t\"LaunchOptions\"\t\t\"" + EscapeVdf(options) + "\"" + nl + indent + "}";
            result = vdf.Insert(apps.OpenBrace + 1, block);
        }
        else throw new FormatException("the app entry is not a block");
        if (GetLaunchOption(result, appId) != options) throw new FormatException("the edited text does not read back");   // never write something we cannot read
        return result;
    }

    // Removes the launch option again (only the one line); returns the text unchanged when there is none.
    static string ClearLaunchOption(string vdf, string appId)
    {
        var apps = AppsBlock(ParseVdf(vdf));
        if (apps == null) throw new FormatException("no apps section");
        var app = Child(apps.Kids, appId);
        var opt = app == null ? null : Child(app.Kids, "LaunchOptions");
        if (opt == null) return vdf;
        // when the option is all the game's entry holds (we created it), the whole entry goes: the file is then exactly what it was before
        int removeFrom = app.Kids.Count == 1 ? app.KeyStart : opt.KeyStart;
        int valueEnd = app.Kids.Count == 1 ? app.CloseBrace + 1 : opt.ValueEnd + 1;
        int start = removeFrom;
        while (start > 0 && (vdf[start - 1] == '\t' || vdf[start - 1] == ' ')) start--;
        if (start > 0 && vdf[start - 1] == '\n') start--;
        if (start > 0 && vdf[start - 1] == '\r') start--;
        string result = vdf.Substring(0, start) + vdf.Substring(valueEnd);
        if (GetLaunchOption(result, appId) != "") throw new FormatException("the edited text does not read back");
        return result;
    }

    static string IndentOf(string t, int pos)
    {
        int s = pos;
        while (s > 0 && t[s - 1] != '\n') s--;
        var sb = new StringBuilder();
        for (int k = s; k < pos && (t[k] == '\t' || t[k] == ' '); k++) sb.Append(t[k]);
        return sb.ToString();
    }

    // ---- the launch option ------------------------------------------------------------------------------------------------------------

    static string WantedLaunchOption(string existing)
    {
        string exe = Path.Combine(gameDir, "GrowHome.exe");
        string args = "-screen-width 1280 -screen-height 720 -popupwindow";
        if (!string.IsNullOrWhiteSpace(existing) && !IsOurLaunchOption(existing))
            args = existing.Replace("%command%", "").Trim();      // keep whatever the person already had there
        args = LaunchArgs(args, FullscreenAllowed(), false);      // always a window (a frozen fullscreen game leaves a black screen)
        return "\"" + exe + "\" " + args + " %command%";
    }

    static bool IsOurLaunchOption(string option)
    {
        return !string.IsNullOrEmpty(option) && option.IndexOf("GrowHome.exe", StringComparison.OrdinalIgnoreCase) >= 0 && option.IndexOf("%command%", StringComparison.OrdinalIgnoreCase) >= 0
            && option.IndexOf(Path.GetFileName(installDir) ?? "", StringComparison.OrdinalIgnoreCase) >= 0 && option.TrimStart().StartsWith("\"");
    }

    // ---- accounts and ownership -------------------------------------------------------------------------------------------------------

    static string SteamRoot()
    {
        foreach (var r in SteamRoots()) return r;
        return null;
    }

    static string ActiveAccount()
    {
        try
        {
            object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null);
            if (v is int && (int)v != 0) return ((int)v).ToString();
        }
        catch { }
        return null;
    }

    static bool SteamLoggedIn() { return ActiveAccount() != null && Process.GetProcessesByName("steam").Length > 0; }

    static string LocalConfigPath(string account) { string r = SteamRoot(); return r == null || account == null ? null : Path.Combine(r, "userdata", account, "config", "localconfig.vdf"); }

    static string LastOwner(string acfText)
    {
        var m = Regex.Match(acfText ?? "", "\"LastOwner\"\\s+\"(\\d+)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    // Does this Steam account only borrow the game (Family Sharing)? Such an account can only start it through Steam. Unknown = false.
    static bool IsBorrower(string acfText, string account32)
    {
        string owner = LastOwner(acfText);
        if (owner == null || account32 == null) return false;
        ulong acc; if (!ulong.TryParse(account32, out acc)) return false;
        return owner != (76561197960265728UL + acc).ToString();
    }

    static string ManifestText()
    {
        try
        {
            foreach (var steam in SteamRoots())
            {
                var libs = new List<string> { steam };
                string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf)) foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"")) libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                foreach (var lib in libs)
                {
                    string acf = Path.Combine(lib, "steamapps", "appmanifest_" + AppId + ".acf");
                    if (File.Exists(acf)) return File.ReadAllText(acf);
                }
            }
        }
        catch { }
        return null;
    }

    // ---- which Grow Home is running ---------------------------------------------------------------------------------------------------

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    static string ImagePath(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);       // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024); int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    static bool SamePath(string a, string b)
    {
        if (a == null || b == null) return false;
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    sealed class RunningGame { public int Pid; public string Path; public bool Ours; }

    static List<RunningGame> RunningGames()
    {
        var list = new List<RunningGame>();
        string ours = Path.Combine(gameDir, "GrowHome.exe");
        foreach (var p in Process.GetProcessesByName("GrowHome"))
        {
            string path = null; try { path = ImagePath(p.Id); } catch { }
            list.Add(new RunningGame { Pid = p.Id, Path = path, Ours = SamePath(path, ours) });
        }
        return list;
    }

    // ---- Steam itself -----------------------------------------------------------------------------------------------------------------

    static string SteamExe()
    {
        try { var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe", null) as string; if (v != null && File.Exists(v.Replace('/', '\\'))) return v.Replace('/', '\\'); } catch { }
        string r = SteamRoot();
        return r == null ? null : Path.Combine(r, "steam.exe");
    }

    static bool WaitFor(Func<bool> done, int seconds, Action<int> tick)
    {
        var sw = Stopwatch.StartNew();
        int shown = -1;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (done()) return true;
            int s = (int)sw.Elapsed.TotalSeconds;
            if (tick != null && s / 5 != shown) { shown = s / 5; tick(s); }
            System.Threading.Thread.Sleep(500);
        }
        return done();
    }

    // Steam running and signed in; starts it when needed. False if it did not come up in time.
    static bool EnsureSteamLoggedIn(int seconds)
    {
        if (SteamLoggedIn()) return true;
        if (Process.GetProcessesByName("steam").Length == 0)
        {
            Say("Steam is not running. Starting Steam (this can take a moment)...");
            Log("steam not running, starting it");
            string exe = SteamExe();
            try { if (exe != null) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); else Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true }); }
            catch (Exception e) { Log("could not start steam: " + e.Message); }
        }
        bool ok = WaitFor(SteamLoggedIn, seconds, s => Say("  waiting for Steam to sign in... " + s + " s"));
        if (ok) System.Threading.Thread.Sleep(3000);    // let it settle
        Log(ok ? "steam is signed in" : "steam did not sign in within " + seconds + " s");
        return ok;
    }

    static bool ShutdownSteam()
    {
        string exe = SteamExe();
        if (exe == null) return false;
        Say("Closing Steam...");
        try { Process.Start(new ProcessStartInfo(exe, "-shutdown") { UseShellExecute = false, CreateNoWindow = true }); } catch (Exception e) { Log("steam -shutdown failed: " + e.Message); return false; }
        bool gone = WaitFor(() => Process.GetProcessesByName("steam").Length == 0, 60, s => Say("  waiting for Steam to close... " + s + " s"));
        System.Threading.Thread.Sleep(1500);
        Log(gone ? "steam closed" : "steam did not close");
        return gone;
    }

    // Makes Steam's Play button (and steam://rungameid) start the co-op copy. Needs Steam closed while the file is edited, so Steam is restarted once.
    // Returns true when the launch option is in place (already was, or was just set).
    static bool EnsureLaunchOption(bool undo)
    {
        string account = ActiveAccount();
        string path = LocalConfigPath(account);
        if (path == null || !File.Exists(path)) { Log("no localconfig.vdf for account " + account); Say("Could not find this Steam account's settings file."); return false; }
        string text = File.ReadAllText(path);
        string current; try { current = GetLaunchOption(text, AppId); } catch (FormatException e) { Log("localconfig.vdf not understood: " + e.Message); Say("Steam's settings file looks different than expected; not changing it."); return false; }
        string wanted = WantedLaunchOption(current);
        if (undo ? !IsOurLaunchOption(current) : current == wanted) return true;
        // an option of ours from an older launcher still starts this co-op copy: no Steam restart just to change its window arguments
        if (!undo && IsOurLaunchOption(current) && current.IndexOf("\"" + Path.Combine(gameDir, "GrowHome.exe") + "\"", StringComparison.OrdinalIgnoreCase) >= 0) { Log("kept the older co-op launch option: " + current); return true; }
        if (!undo && !string.IsNullOrEmpty(current) && current.Contains("%command%") && !IsOurLaunchOption(current)) { Say("Grow Home already has a Steam launch option with a wrapper; not replacing it."); Log("foreign wrapper launch option: " + current); return false; }

        Say("");
        if (undo) Say("Removing the co-op launch option from Steam. Steam has to restart once for that.");
        else Say("The game could not be started directly. To fix that, Steam has to restart once so that it starts the CO-OP copy (not the regular game). This is only needed because the direct start failed.");
        if (!assumeYes && !Confirm("Restart Steam now? [Y/n] ")) { Say("Skipped. Without this step Steam would start the regular game. Choose Try again and answer Yes when you are ready."); Log("steam setup skipped by the player"); return false; }
        if (Process.GetProcessesByName("steam").Length > 0 && !ShutdownSteam()) { Say("Steam did not close. Close it yourself (Steam menu > Exit), then run this again."); return false; }

        string backup = path + ".growhome-coop.bak";
        if (!File.Exists(backup)) File.Copy(path, backup);
        text = File.ReadAllText(path);                       // read again: Steam may have written the file while it closed
        string edited;
        try { edited = undo ? ClearLaunchOption(text, AppId) : SetLaunchOption(text, AppId, wanted); }
        catch (FormatException e) { Log("could not edit localconfig.vdf: " + e.Message); Say("Steam's settings file looks different than expected; not changing it."); EnsureSteamLoggedIn(90); return false; }
        string tmp = path + ".growhome-coop.tmp";
        File.WriteAllText(tmp, edited, new UTF8Encoding(false));
        File.Delete(path); File.Move(tmp, path);
        Log((undo ? "removed" : "set") + " the Steam launch option for account " + account);
        Say(undo ? "Done. Steam's Play button starts the regular game again." : "Done. Steam's Play button now starts the co-op copy.");
        EnsureSteamLoggedIn(90);
        return true;
    }
}
