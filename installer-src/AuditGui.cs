using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

// Audit checks for the game-styled window (see Audit.cs). The window is drawn into pictures, so nothing needs to be shown on screen.
static partial class Program
{
    static Bitmap Snap(GameWindow w, int width, int height)
    {
        var bmp = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bmp)) w.Draw(g, new Size(width, height));
        return bmp;
    }

    static double Brightness(Bitmap b, int x, int y) { var c = b.GetPixel(Math.Min(b.Width - 1, Math.Max(0, x)), Math.Min(b.Height - 1, Math.Max(0, y))); return 0.299 * c.R + 0.587 * c.G + 0.114 * c.B; }

    // how many pixels in the area are close to a colour (the pink selection, the white frame, ...)
    static int CountNear(Bitmap b, Rectangle area, Color target, int tolerance)
    {
        int n = 0;
        for (int y = area.Top; y < area.Bottom; y += 2)
            for (int x = area.Left; x < area.Right; x += 2)
            {
                var c = b.GetPixel(x, y);
                if (Math.Abs(c.R - target.R) < tolerance && Math.Abs(c.G - target.G) < tolerance && Math.Abs(c.B - target.B) < tolerance) n++;
            }
        return n;
    }

    // The home page's "Check for updates" button: it checks now (also when automatic checks are switched off), stages a newer mod for the next start,
    // says what it found, and says so when the server cannot be reached. Runs the real code against a throw-away update server and a test page.
    static void AuditUpdateButton(string tmp)
    {
        string realGame = gameDir, realInstall = installDir, realManifest = ManifestUrl, realPrivate = auditPrivateKey, realKey = UpdatePublicKeyXml;
        installDir = Path.Combine(tmp, "button-install"); gameDir = Path.Combine(installDir, "game");
        Directory.CreateDirectory(Path.GetDirectoryName(TargetDll));
        byte[] v1 = Bytes("mod version one"), v2 = Bytes("mod version two, brand new");
        File.WriteAllBytes(TargetDll, v1);
        File.WriteAllText(Path.Combine(gameDir, "coop-version.txt"), "1");
        var server = new MiniServer();
        var win = new GameWindow(true);
        try
        {
            var handle = win.Handle;   // the page needs a handle, so the check's answer reaches it
            Check("UPDATE-BUTTON", handle != IntPtr.Zero, "the test page can receive the check's answer", "");
            using (var k = new System.Security.Cryptography.RSACryptoServiceProvider(2048)) { auditPrivateKey = k.ToXmlString(true); UpdatePublicKeyXml = k.ToXmlString(false); }
            ManifestUrl = server.Url("/version.json");
            win.ShowHome();
            int at = Array.FindIndex(win.ItemTexts(), s => s.StartsWith("Check for updates"));
            Check("UPDATE-BUTTON", at >= 0, "the home page has a Check for updates button", string.Join(" | ", win.ItemTexts()));
            if (at < 0) return;

            // 1. the server has the installed version: up to date, nothing staged
            server.Serve("/version.json", Manifest(1, server.Url("/mod1.dll"), Sha256(v1)));
            PressCheck(win, at);
            Check("UPDATE-BUTTON", LastCheck == "current" && win.ResultLine.Contains("UP TO DATE") && !File.Exists(StagedDll), "a check with nothing newer says the mod is up to date and stages nothing", LastCheck + ": " + win.ResultLine);
            win.PressKey(Keys.Escape);
            Check("UPDATE-BUTTON", win.Current == GameWindow.View.Home, "Escape returns from the check to the home page", win.Current.ToString());

            // 2. a newer version: downloaded and staged for the next start; the installed mod is not touched yet
            server.Serve("/mod2.dll", v2); server.Serve("/version.json", Manifest(2, server.Url("/mod2.dll"), Sha256(v2)));
            PressCheck(win, at);
            Check("UPDATE-BUTTON", LastCheck == "staged" && File.Exists(StagedDll) && File.Exists(StagedInfo) && SameBytes(TargetDll, v1) && InstalledVersion() == 1, "a newer version is downloaded and staged, the installed mod stays until the next start", LastCheck + ", staged " + File.Exists(StagedDll) + ", installed version " + InstalledVersion());
            Check("UPDATE-BUTTON", win.ResultLine.Contains("DOWNLOADED"), "the page says a newer version is downloaded", win.ResultLine);
            win.PressKey(Keys.Escape);
            File.Delete(StagedDll); File.Delete(StagedInfo);

            // 3. automatic checks switched off in Settings: the button still checks
            File.WriteAllText(Path.Combine(installDir, "coop-noupdate.txt"), "off");
            PressCheck(win, at);
            Check("UPDATE-BUTTON", LastCheck == "staged" && File.Exists(StagedDll), "the button checks even when automatic checks are switched off", LastCheck);
            win.PressKey(Keys.Escape);
            File.Delete(StagedDll); File.Delete(StagedInfo);
            File.Delete(Path.Combine(installDir, "coop-noupdate.txt"));

            // 4. the server cannot be reached: the page says so and nothing changes
            var dead = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); dead.Start();
            int deadPort = ((System.Net.IPEndPoint)dead.LocalEndpoint).Port; dead.Stop();
            ManifestUrl = "http://127.0.0.1:" + deadPort + "/version.json";
            PressCheck(win, at);
            Check("UPDATE-BUTTON", LastCheck == "failed" && win.ResultLine.Contains("COULD NOT CHECK") && !File.Exists(StagedDll) && SameBytes(TargetDll, v1), "an unreachable server is reported and changes nothing", LastCheck + ": " + win.ResultLine);
            win.PressKey(Keys.Escape);
        }
        finally
        {
            server.Stop();
            win.Dispose();
            gameDir = realGame; installDir = realInstall; ManifestUrl = realManifest; auditPrivateKey = realPrivate; UpdatePublicKeyXml = realKey;
        }
    }

    // Once starting the game directly has worked, a later failure retries instead of falling back to Steam (and asking to restart it).
    static void AuditDirectMarker(string tmp)
    {
        string realInstall = installDir;
        installDir = Path.Combine(tmp, "direct-marker"); Directory.CreateDirectory(installDir);
        try
        {
            bool before = DirectWorkedBefore(); MarkDirectWorks(); bool after = DirectWorkedBefore();
            Check("LAUNCH-DIRECT-FIRST", !before && after, "once starting the game directly has worked, a later failure retries instead of falling back to Steam", "before " + before + ", after " + after);
        }
        finally { installDir = realInstall; }
    }

    // A program the launcher starts "under Steam" really has steam.exe as its parent (this is what keeps the game's own Steam check from sending the launch
// to the regular game when the launcher was opened from Explorer).
    static void AuditStartUnderSteam(string tmp)
    {
        var steam = System.Diagnostics.Process.GetProcessesByName("steam").FirstOrDefault();
        if (steam == null) { Check("LAUNCH-PARENT", false, "a program started under Steam has steam.exe as its parent", "Steam is not running"); return; }
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        int pid = StartUnderSteam(cmd, "/c ping -n 4 127.0.0.1 >nul", Environment.SystemDirectory, new System.Collections.Generic.Dictionary<string, string> { { "SteamAppId", "323320" } });
        int parent = pid == 0 ? 0 : ParentPidOf(pid);
        bool sees = false;
        try { var p = System.Diagnostics.Process.GetProcessById(pid); sees = !p.HasExited; } catch { }
        Check("LAUNCH-PARENT", pid != 0 && parent == steam.Id, "a program started under Steam has steam.exe as its parent", "pid " + pid + ", parent " + parent + ", steam " + steam.Id);
        try { System.Diagnostics.Process.GetProcessById(pid).Kill(); } catch { }
    }

    // Presses the button on the given home-page line and waits (a few seconds at most) until the check has answered and the page shows it.
    static void PressCheck(GameWindow win, int index)
    {
        win.Selected = index;
        win.PressKey(Keys.Enter);
        for (int i = 0; i < 1500 && win.CheckRunning; i++) { Application.DoEvents(); Thread.Sleep(20); }
        for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(10); }
    }

    // Draws the pages the way a person would see them and keeps the pictures next to the audit report so they can be looked at.
    static void AuditGui(string tmp, string pictureDir)
    {
        installDir = Path.Combine(tmp, "gui-install"); gameDir = Path.Combine(installDir, "game");
        Directory.CreateDirectory(gameDir);
        const int PW = 1280, PH = 720;

        // fonts: read from the real Grow Home on this PC, and a fallback that never throws
        string orig = FindGame();
        Look.LoadFonts(orig == null ? new string[0] : new[] { Path.Combine(orig, "GrowHome_Data", "sharedassets0.assets") }, Path.Combine(tmp, "font-cache"));
        Check("GUI-FONT", Look.FromGame && Look.Block != null && Look.Block.Name == "Block FIXED", "the game's menu font is read from this PC's own Grow Home", Look.Block == null ? "none" : Look.Block.Name + (Look.Small != null ? " + " + Look.Small.Name : ""));
        Check("GUI-FONT", File.Exists(Path.Combine(tmp, "font-cache", "block.ttf")), "the font is kept so the next start does not search again", "");
        Look.LoadFonts(new string[0], Path.Combine(tmp, "no-fonts-here"));
        Check("GUI-FONT", !Look.FromGame && Look.Block != null, "without a Grow Home to read it from, a plain font is used and nothing breaks", Look.Block == null ? "" : Look.Block.Name);
        Look.LoadFonts(orig == null ? new string[0] : new[] { Path.Combine(orig, "GrowHome_Data", "sharedassets0.assets") }, Path.Combine(tmp, "font-cache"));

        using (var win = new GameWindow(true))
        {
            var handle = win.Handle;
            Bitmap home = Snap(win, PW, PH);
            Directory.CreateDirectory(pictureDir);
            home.Save(Path.Combine(pictureDir, "window-home.png"), ImageFormat.Png);

            // the picture is a real picture: sky is darker at the top than at the bottom, text and panel exist, the selection is pink
            Check("GUI-RENDER", Brightness(home, 640, 5) + 15 < Brightness(home, 640, 700) && Brightness(home, 640, 400) > 60, "the home page is drawn with the game's blue sky", "top " + (int)Brightness(home, 640, 5) + " bottom " + (int)Brightness(home, 640, 700));
            Check("GUI-RENDER", CountNear(home, new Rectangle(55, 240, 560, 140), Look.Pink, 45) > 40, "the selected menu item is pink", CountNear(home, new Rectangle(55, 240, 560, 140), Look.Pink, 45) + " pink pixels");
            Check("GUI-RENDER", CountNear(home, new Rectangle(735, 96, 450, 460), Color.White, 25) > 150, "the status panel has its white frame", CountNear(home, new Rectangle(735, 96, 450, 460), Color.White, 25) + " white pixels");
            Check("GUI-RENDER", CountNear(home, new Rectangle(900, 560, 290, 70), Look.Red, 45) > 40, "the five progress boxes are drawn", "");
            Check("GUI-RENDER", CountNear(home, new Rectangle(60, 100, 600, 100), Color.White, 60) + CountNear(home, new Rectangle(60, 100, 600, 100), Look.Text, 30) > 200, "the title is drawn", "");

            // everything fits: no menu line runs into the status panel
            Bitmap probe = new Bitmap(10, 10);
            float widest; using (var g = Graphics.FromImage(probe)) widest = win.MaxItemWidth(g);
            Check("GUI-LAYOUT", widest < 640, "the longest menu line fits in the left column", ((int)widest) + " px of 640");

            // pages
            win.ShowSettings();
            Bitmap settings = Snap(win, PW, PH); settings.Save(Path.Combine(pictureDir, "window-settings.png"), ImageFormat.Png);
            string[] st = win.ItemTexts();
            Check("GUI-RENDER", st.Length >= 3 && st.Any(t => t.StartsWith("Skip intro")) && st.Any(t => t.StartsWith("Check for updates")) && st.Last() == "Back", "the settings page lists its options with their values", string.Join(" | ", st));
            float settingsWidest; using (var g2 = Graphics.FromImage(probe)) settingsWidest = win.MaxItemWidth(g2);
            Check("GUI-LAYOUT", settingsWidest < 1100, "the longest settings line fits across the page", ((int)settingsWidest) + " px of 1100");
            win.ShowHome();

            win.TestSetStatus(new[] { "GROW HOME|FOUND", "CO-OP COPY|INSTALLED  V2", "STEAM|SIGNED IN", "ACCOUNT|OWNS THE GAME", "UPDATES|ON, IN THE BACKGROUND", "INTRO SCREENS|SKIPPED" });
            Bitmap homeStatus = Snap(win, PW, PH); homeStatus.Save(Path.Combine(pictureDir, "window-home-status.png"), ImageFormat.Png);

            win.TestRun("Installing");
            win.TestAddLine("Installing Grow Home Co-op");
            win.TestAddLine("Found Grow Home: C:\\Program Files (x86)\\Steam\\steamapps\\common\\GrowHome  (only read, never changed)");
            win.TestAddLine("Setting up the co-op copy in C:\\Users\\Some Very Long Name\\GrowHomeCoop\\game (most files are linked, not copied: this takes seconds)...");
            win.TestAddLine("Patched the co-op copy.");
            win.TestSetProgress(45);
            Thread.Sleep(900);
            Bitmap run = Snap(win, PW, PH); run.Save(Path.Combine(pictureDir, "window-run.png"), ImageFormat.Png);
            Check("GUI-RENDER", run.GetPixel(918, 575).R - run.GetPixel(1132, 575).R > 25 && run.GetPixel(918, 575).R > home.GetPixel(918, 575).R + 25, "progress lights up the boxes it has passed", "box 10%: red " + run.GetPixel(918, 575).R + ", box 100%: red " + run.GetPixel(1132, 575).R);
            Check("GUI-RENDER", CountNear(run, new Rectangle(745, 110, 430, 430), Look.Text, 30) > 300, "messages appear in the status panel", "");

            // long text is wrapped inside the panel, never cut off or running over it
            using (var g = Graphics.FromImage(probe))
            {
                var lines = GameWindow.Wrap(g, "C:\\Users\\Some Very Long Name With Spaces\\GrowHomeCoop\\game\\GrowHome_Data\\Managed\\Assembly-CSharp.dll was not found anywhere on this computer at all", Look.Tiny(19), 405);
                bool fits = lines.All(l => g.MeasureString(l, Look.Tiny(19), 100000, StringFormat.GenericTypographic).Width <= 410);
                Check("GUI-LAYOUT", fits && lines.Count >= 3, "long messages and paths are wrapped to fit the status panel", lines.Count + " lines");
                string joined = string.Concat(lines).Replace(" ", "");
                Check("GUI-LAYOUT", joined == "C:\\Users\\SomeVeryLongNameWithSpaces\\GrowHomeCoop\\game\\GrowHome_Data\\Managed\\Assembly-CSharp.dllwasnotfoundanywhereonthiscomputeratall", "wrapping loses no text", "");
            }

            // a question
            win.ShowAsk("The REGULAR Grow Home is already running. Close it and start the co-op copy?", new[] { "Yes", "No" }, i => { });
            Bitmap ask = Snap(win, PW, PH); ask.Save(Path.Combine(pictureDir, "window-question.png"), ImageFormat.Png);
            Check("GUI-RENDER", win.ItemTexts().SequenceEqual(new[] { "Yes", "No" }), "a question shows its answers as menu items", string.Join(" | ", win.ItemTexts()));
        }

        // sizes: the window can be any size and still draws
        using (var win = new GameWindow(true))
        {
            var handle = win.Handle;
            bool allFine = true;
            foreach (var size in new[] { new Size(640, 400), new Size(1152, 648), new Size(1920, 1080), new Size(900, 900) })
            {
                var b = Snap(win, size.Width, size.Height);
                if (b.Width != size.Width || Brightness(b, size.Width / 2, size.Height / 2) < 30) allFine = false;
            }
            Check("GUI-LAYOUT", allFine, "the window draws at small, normal, full-HD and square sizes", "");
        }

        // input: keyboard and mouse, the way a person uses the game's own menus
        using (var win = new GameWindow(true))
        {
            var handle = win.Handle; Snap(win, PW, PH); win.AskGraceMs = 0;
            int start = win.Selected;
            win.PressKey(Keys.Down); win.PressKey(Keys.Down);
            Check("GUI-INPUT", win.Selected == 2, "the arrow keys move the selection", start + " -> " + win.Selected);
            int itemCount = win.ItemTexts().Length;   // the home page has four lines now (Check for updates was added): count them, do not assume
            for (int i = 2; i < itemCount - 1; i++) win.PressKey(Keys.Down);
            Check("GUI-INPUT", win.Selected == itemCount - 1, "the arrow keys reach the last item", itemCount + " items");
            win.PressKey(Keys.Down);
            Check("GUI-INPUT", win.Selected == 0, "the selection wraps around", "");
            win.PressKey(Keys.Up);
            Check("GUI-INPUT", win.Selected == itemCount - 1, "up from the first item goes to the last", "");
            win.Selected = 1; win.PressKey(Keys.Enter);
            Check("GUI-INPUT", win.Current == GameWindow.View.Settings, "Enter opens the selected page", win.Current.ToString());
            win.PressKey(Keys.Escape);
            Check("GUI-INPUT", win.Current == GameWindow.View.Home, "Escape goes back", win.Current.ToString());
            Snap(win, PW, PH);
            var p = win.ToClient(win.ItemCenter(1), new Size(PW, PH));
            Check("GUI-INPUT", win.ItemAt(win.ItemCenter(1)) == 1 && win.ItemAt(new PointF(5, 5)) == -1, "a point on a menu line selects it, a point elsewhere does not", "");

            // a question ignores keys and clicks in its first moments: a key still held down or a double click must not answer it before it can be read
            win.AskGraceMs = 900;
            int graceAnswer = -1; var graceDone = new ManualResetEvent(false);
            var tg = Bg("AuditGui worker", () => { win.AskForTest("Restart Steam now?", new[] { "Yes", "No" }, out graceAnswer); graceDone.Set(); });
            tg.Start();
            for (int i = 0; i < 100 && win.Current != GameWindow.View.Ask; i++) { Application.DoEvents(); Thread.Sleep(20); }
            win.PressKey(Keys.N); win.PressKey(Keys.Enter); win.PressKey(Keys.Space); win.Activate(); win.PressKey(Keys.Escape);
            bool unanswered = !graceDone.WaitOne(250) && win.Current == GameWindow.View.Ask;
            Check("GUI-INPUT", unanswered, "a question ignores N, Enter, Space, a click and Escape in its first moments", unanswered ? "" : "it was answered unseen: " + graceAnswer);
            for (int i = 0; i < 60; i++) { Application.DoEvents(); Thread.Sleep(20); }
            win.PressKey(Keys.Y); graceDone.WaitOne(5000);
            Check("GUI-INPUT", graceAnswer == 0, "after that moment the question can be answered", "answer " + graceAnswer);
            win.AskGraceMs = 0;

            // a mouse resting where an answer appears must not choose it: Enter still gives the default (Yes), until the mouse really moves
            win.AskGraceMs = 0;
            int restAnswer = -1; var restDone = new ManualResetEvent(false);
            var tr = Bg("AuditGui worker", () => { win.AskForTest("Restart Steam now?", new[] { "Yes", "No" }, out restAnswer); restDone.Set(); });
            tr.Start();
            for (int i = 0; i < 100 && win.Current != GameWindow.View.Ask; i++) { Application.DoEvents(); Thread.Sleep(20); }
            Snap(win, PW, PH);
            var noSpot = win.ItemCenter(1);
            win.Hover(noSpot, win.AskMouse);
            int resting = win.Selected;
            win.Hover(noSpot, new Point(win.AskMouse.X + 60, win.AskMouse.Y + 40));
            int moved = win.Selected;
            Check("GUI-INPUT", resting == 0 && moved == 1, "a resting mouse chooses nothing on a question; moving it onto an answer does", "resting -> " + resting + ", moved -> " + moved);
            win.Selected = 0; win.PressKey(Keys.Enter); restDone.WaitOne(5000);
            Check("GUI-INPUT", restAnswer == 0, "Enter on a fresh question means Yes", "answer " + restAnswer);

            // a question asked from another thread (as the real flow does) is answered from the window
            int answer = -1;
            var asked = new ManualResetEvent(false);
            var t = Bg("AuditGui worker", () => { win.AskForTest("Close the other game?", new[] { "Yes", "No" }, out answer); asked.Set(); });
            t.Start();
            for (int i = 0; i < 100 && win.Current != GameWindow.View.Ask; i++) { Application.DoEvents(); Thread.Sleep(20); }
            Check("GUI-INPUT", win.Current == GameWindow.View.Ask, "a question from the worker thread appears in the window", win.Current.ToString());
            win.PressKey(Keys.N);
            asked.WaitOne(5000);
            Check("GUI-INPUT", answer == 1, "pressing N answers No", "answer " + answer);
            var t2 = Bg("AuditGui worker", () => { win.AskForTest("Close the other game?", new[] { "Yes", "No" }, out answer); asked.Set(); });
            asked.Reset(); t2.Start();
            for (int i = 0; i < 100 && win.Current != GameWindow.View.Ask; i++) { Application.DoEvents(); Thread.Sleep(20); }
            win.PressKey(Keys.Y); asked.WaitOne(5000);
            Check("GUI-INPUT", answer == 0, "pressing Y answers Yes", "answer " + answer);
            var t3 = Bg("AuditGui worker", () => { win.AskForTest("Which colour?", new[] { "Red", "Blue" }, out answer); asked.Set(); });
            asked.Reset(); t3.Start();
            for (int i = 0; i < 100 && win.Current != GameWindow.View.Ask; i++) { Application.DoEvents(); Thread.Sleep(20); }
            win.PressKey(Keys.Down); win.PressKey(Keys.Enter); asked.WaitOne(5000);
            Check("GUI-INPUT", answer == 1, "choosing the second option with the keys answers 1", "answer " + answer);

            // the frozen-game question through the real AskCloseFrozen and this window: Escape, N and Enter on the fresh question leave the game
            // running (its safe answer is the FIRST one), only Y or choosing "Close it" closes it
            var keepHook = chooseHook;
            var frozen = new List<string>();
            try
            {
                chooseHook = (q, o) => { int a; win.AskForTest(q, o, out a); return a; };
                foreach (var how in new[] { "Escape", "N", "Enter", "Y", "Down+Enter" })
                {
                    bool closes = false; var fdone = new ManualResetEvent(false);
                    var tf = Bg("AuditGui worker", () => { closes = AskCloseFrozen("Close the frozen game?"); fdone.Set(); });
                    tf.Start();
                    for (int i = 0; i < 100 && win.Current != GameWindow.View.Ask; i++) { Application.DoEvents(); Thread.Sleep(20); }
                    bool startsSafe = win.Selected == 0 && win.ItemTexts().Length == 2 && win.ItemTexts()[0] == "Leave it running";
                    switch (how)
                    {
                        case "Escape": win.PressKey(Keys.Escape); break;
                        case "N": win.PressKey(Keys.N); break;
                        case "Enter": win.PressKey(Keys.Enter); break;
                        case "Y": win.PressKey(Keys.Y); break;
                        default: win.PressKey(Keys.Down); win.PressKey(Keys.Enter); break;
                    }
                    bool answered = fdone.WaitOne(5000);
                    frozen.Add(how + "=" + (answered ? (closes ? "close" : "keep") : "no answer") + (startsSafe ? "" : " (did not start on Leave it running)"));
                }
            }
            finally { chooseHook = keepHook; }
            bool frozenOk = frozen.SequenceEqual(new[] { "Escape=keep", "N=keep", "Enter=keep", "Y=close", "Down+Enter=close" });
            int safeYesNo = SafeAnswer(new[] { "Yes", "No" }), safeReplace = SafeAnswer(new[] { "Replace it", "Cancel" }), safeStale = SafeAnswer(new[] { StaleRestart, StaleCancel }), safeFrozen = SafeAnswer(FrozenAnswers);
            bool safeRule = safeYesNo == 1 && safeReplace == 1 && safeStale == 1 && safeFrozen == 0;
            Check("HANG-NO-FALSE-KILL", frozenOk && safeRule, "in the window, Escape, N and Enter on the frozen-game question leave the game running; only Y or choosing \"Close it\" closes it (Escape and N give each question's answer that changes nothing)",
                string.Join(", ", frozen.ToArray()) + "; safe answers Yes/No=" + safeYesNo + " Replace/Cancel=" + safeReplace + " Restart/Cancel=" + safeStale + " frozen=" + safeFrozen);

            // settings are plain files and toggle both ways
            win.ShowSettings();
            string noUpdate = Path.Combine(installDir, "coop-noupdate.txt");
            int idx = Array.FindIndex(win.ItemTexts(), s => s.StartsWith("Check for updates"));
            win.Selected = idx; win.PressKey(Keys.Enter);
            bool off = File.Exists(noUpdate) && UpdatesDisabled();
            win.PressKey(Keys.Enter);
            bool on = !File.Exists(noUpdate) && !UpdatesDisabled();
            Check("GUI-INPUT", off && on, "the update setting switches checking off and on again", "off " + off + ", on " + on);

            // the other settings are files too: each switches both ways, and the room setting cycles public, listed-you-accept, private
            string[] names = { "On-screen display", "Name tags", "Chat log", "Share world" };
            string[] files = { "coop-ui-hidden.txt", "coop-noname.txt", "coop-nochat.txt", "coop-noshare.txt" };
            bool toggles = true;
            for (int k = 0; k < names.Length; k++)
            {
                string f = Path.Combine(gameDir, files[k]);
                win.ShowSettings();
                win.Selected = Array.FindIndex(win.ItemTexts(), s => s.StartsWith(names[k]));
                win.PressKey(Keys.Enter);
                bool hidden = File.Exists(f);
                win.PressKey(Keys.Enter);
                toggles &= hidden && !File.Exists(f);
            }
            win.ShowSettings();
            win.Selected = Array.FindIndex(win.ItemTexts(), s => s.StartsWith("Room for F2"));
            string roomFile = Path.Combine(gameDir, CoopSettings.RoomFile);
            win.PressKey(Keys.Enter); string r1 = File.Exists(roomFile) ? File.ReadAllText(roomFile).Trim() : "";
            win.PressKey(Keys.Enter); string r2 = File.Exists(roomFile) ? File.ReadAllText(roomFile).Trim() : "";
            win.PressKey(Keys.Enter); string r3 = File.Exists(roomFile) ? File.ReadAllText(roomFile).Trim() : "";
            bool cycle = r1 == "ask" && r2 == "private" && r3 == "public";
            Check("SETTINGS", toggles && cycle, "the other settings are plain files that switch both ways, and the room setting cycles public, listed-you-accept, private", "toggles " + toggles + ", room " + r1 + " " + r2 + " " + r3);
        }
    }
}
