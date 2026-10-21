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
            var handle = win.Handle; Snap(win, PW, PH);
            int start = win.Selected;
            win.PressKey(Keys.Down); win.PressKey(Keys.Down);
            Check("GUI-INPUT", win.Selected == 2, "the arrow keys move the selection", start + " -> " + win.Selected);
            win.PressKey(Keys.Down);
            Check("GUI-INPUT", win.Selected == 0, "the selection wraps around", "");
            win.PressKey(Keys.Up);
            Check("GUI-INPUT", win.Selected == 2, "up from the first item goes to the last", "");
            win.Selected = 1; win.PressKey(Keys.Enter);
            Check("GUI-INPUT", win.Current == GameWindow.View.Settings, "Enter opens the selected page", win.Current.ToString());
            win.PressKey(Keys.Escape);
            Check("GUI-INPUT", win.Current == GameWindow.View.Home, "Escape goes back", win.Current.ToString());
            Snap(win, PW, PH);
            var p = win.ToClient(win.ItemCenter(1), new Size(PW, PH));
            Check("GUI-INPUT", win.ItemAt(win.ItemCenter(1)) == 1 && win.ItemAt(new PointF(5, 5)) == -1, "a point on a menu line selects it, a point elsewhere does not", "");

            // a question asked from another thread (as the real flow does) is answered from the window
            int answer = -1;
            var asked = new ManualResetEvent(false);
            var t = new Thread(() => { win.AskForTest("Close the other game?", new[] { "Yes", "No" }, out answer); asked.Set(); });
            t.Start();
            for (int i = 0; i < 100 && win.Current != GameWindow.View.Ask; i++) { Application.DoEvents(); Thread.Sleep(20); }
            Check("GUI-INPUT", win.Current == GameWindow.View.Ask, "a question from the worker thread appears in the window", win.Current.ToString());
            win.PressKey(Keys.N);
            asked.WaitOne(5000);
            Check("GUI-INPUT", answer == 1, "pressing N answers No", "answer " + answer);
            var t2 = new Thread(() => { win.AskForTest("Close the other game?", new[] { "Yes", "No" }, out answer); asked.Set(); });
            asked.Reset(); t2.Start();
            for (int i = 0; i < 100 && win.Current != GameWindow.View.Ask; i++) { Application.DoEvents(); Thread.Sleep(20); }
            win.PressKey(Keys.Y); asked.WaitOne(5000);
            Check("GUI-INPUT", answer == 0, "pressing Y answers Yes", "answer " + answer);
            var t3 = new Thread(() => { win.AskForTest("Which colour?", new[] { "Red", "Blue" }, out answer); asked.Set(); });
            asked.Reset(); t3.Start();
            for (int i = 0; i < 100 && win.Current != GameWindow.View.Ask; i++) { Application.DoEvents(); Thread.Sleep(20); }
            win.PressKey(Keys.Down); win.PressKey(Keys.Enter); asked.WaitOne(5000);
            Check("GUI-INPUT", answer == 1, "choosing the second option with the keys answers 1", "answer " + answer);

            // settings are plain files and toggle both ways
            win.ShowSettings();
            string noUpdate = Path.Combine(installDir, "coop-noupdate.txt");
            int idx = Array.FindIndex(win.ItemTexts(), s => s.StartsWith("Check for updates"));
            win.Selected = idx; win.PressKey(Keys.Enter);
            bool off = File.Exists(noUpdate) && UpdatesDisabled();
            win.PressKey(Keys.Enter);
            bool on = !File.Exists(noUpdate) && !UpdatesDisabled();
            Check("GUI-INPUT", off && on, "the update setting switches checking off and on again", "off " + off + ", on " + on);
        }
    }
}
