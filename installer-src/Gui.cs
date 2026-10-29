using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// The window: it looks like the game's own menus (blue sky, pixel font, pink ">>>" selection, dotted settings rows, a "STATUS REPORT" panel).
// It does exactly what the text mode does: the same flow runs on a worker thread and its messages, questions and progress come here through the hooks in Start.cs.
// The pixel font is read from the person's own Grow Home when the window opens (it is not part of this exe); without it a plain system font is used.
static partial class Program
{
    static int GuiMain()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Look.LoadFonts(null);
        var form = new GameWindow();
        Application.Run(form);
        return form.ExitCode;
    }

    // ---- colours and fonts --------------------------------------------------------------------------------------------------------------

    static class Look
    {
        public static readonly Color Text = Color.FromArgb(208, 242, 255), Cyan = Color.FromArgb(120, 226, 255), Pink = Color.FromArgb(255, 164, 214), Red = Color.FromArgb(255, 108, 124), Dim = Color.FromArgb(160, 214, 246);
        public static FontFamily Block, Small;
        public static bool FromGame;
        static readonly Dictionary<string, Font> cache = new Dictionary<string, Font>();
        static readonly List<IntPtr> keepAlive = new List<IntPtr>();

        public static Font Big(float px) { return Make(Block, px); }
        public static Font Tiny(float px) { return Make(Small ?? Block, px); }

        static Font Make(FontFamily f, float px)
        {
            string key = f.Name + px;
            Font font;
            if (!cache.TryGetValue(key, out font)) cache[key] = font = new Font(f, px, FromGame ? FontStyle.Regular : FontStyle.Bold, GraphicsUnit.Pixel);
            return font;
        }

        // Looks for the game's menu font ("Block FIXED") and its small header font, in the person's own Grow Home. cacheDir keeps the cut-out fonts so the next start is instant.
        public static void LoadFonts(string[] assetFiles, string cacheDir = null)
        {
            cache.Clear();
            Block = Small = null; FromGame = false;
            try
            {
                cacheDir = cacheDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GrowHomeCoop", "fonts");
                byte[] block = ReadIf(Path.Combine(cacheDir, "block.ttf")), small = ReadIf(Path.Combine(cacheDir, "small.ttf"));
                if (block == null)
                {
                    var files = new List<string>();
                    if (assetFiles != null) files.AddRange(assetFiles);
                    else
                    {
                        string orig = null; try { orig = FindGame(); } catch { }
                        if (orig != null) files.Add(Path.Combine(orig, "GrowHome_Data", "sharedassets0.assets"));
                        if (gameDir != null) files.Add(Path.Combine(gameDir, "GrowHome_Data", "sharedassets0.assets"));
                    }
                    foreach (var f in files)
                    {
                        if (!File.Exists(f)) continue;
                        byte[] b, s; FindFonts(File.ReadAllBytes(f), out b, out s);
                        if (b != null) { block = b; small = s; break; }
                    }
                    if (block != null) { try { Directory.CreateDirectory(cacheDir); File.WriteAllBytes(Path.Combine(cacheDir, "block.ttf"), block); if (small != null) File.WriteAllBytes(Path.Combine(cacheDir, "small.ttf"), small); } catch { } }
                }
                if (block != null)
                {
                    var pfc = new PrivateFontCollection();
                    Add(pfc, block); if (small != null) Add(pfc, small);
                    Block = pfc.Families.FirstOrDefault(x => x.Name.StartsWith("Block")) ?? pfc.Families[0];
                    Small = pfc.Families.FirstOrDefault(x => x.Name.StartsWith("Homespun"));
                    FromGame = true;
                }
            }
            catch (Exception e) { try { Log("fonts: " + e.Message); } catch { } Block = null; FromGame = false; }
            if (Block == null) { try { Block = new FontFamily("Consolas"); } catch { Block = FontFamily.GenericMonospace; } }
        }

        static byte[] ReadIf(string p) { try { return File.Exists(p) ? File.ReadAllBytes(p) : null; } catch { return null; } }

        static void Add(PrivateFontCollection pfc, byte[] data)
        {
            IntPtr mem = Marshal.AllocCoTaskMem(data.Length);
            Marshal.Copy(data, 0, mem, data.Length);
            pfc.AddMemoryFont(mem, data.Length);
            keepAlive.Add(mem);                                  // the font must stay in memory for as long as the program runs
        }

        static uint U32(byte[] b, int i) { return (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]); }
        static int U16(byte[] b, int i) { return b[i] << 8 | b[i + 1]; }

        // Every TrueType font stored in the data, by its family name. Returns the menu font and the small font when they are there.
        public static void FindFonts(byte[] d, out byte[] block, out byte[] small)
        {
            block = small = null;
            for (int i = 0; i + 12 < d.Length; i++)
            {
                if (d[i] != 0 || d[i + 1] != 1 || d[i + 2] != 0 || d[i + 3] != 0) continue;
                int n = U16(d, i + 4);
                if (n < 8 || n > 30 || i + 12 + n * 16 > d.Length) continue;
                bool ok = true, glyf = false, head = false, cmap = false; long end = 0; int nameOff = -1, nameLen = 0;
                for (int t = 0; t < n && ok; t++)
                {
                    int r = i + 12 + t * 16;
                    string tag = Encoding.ASCII.GetString(d, r, 4);
                    foreach (char c in tag) if (c < 32 || c > 126) ok = false;
                    long off = U32(d, r + 8), len = U32(d, r + 12);
                    if (off < 12 || off + len > d.Length - i || len > 4000000) ok = false;
                    if (tag == "glyf") glyf = true; if (tag == "head") head = true; if (tag == "cmap") cmap = true;
                    if (tag == "name") { nameOff = (int)off; nameLen = (int)len; }
                    end = Math.Max(end, off + len);
                }
                if (!ok || !glyf || !head || !cmap || nameOff < 0 || end > 300000) continue;
                string family = FamilyName(d, i + nameOff, nameLen);
                if (family == null) continue;
                var font = new byte[end]; Array.Copy(d, i, font, 0, end);
                if (family == "Block FIXED" && block == null) block = font;
                else if (family.StartsWith("Homespun") && small == null) small = font;
                if (block != null && small != null) return;
                i += (int)end - 1;
            }
        }

        static string FamilyName(byte[] d, int at, int len)
        {
            try
            {
                int count = U16(d, at + 2), strings = at + U16(d, at + 4);
                for (int k = 0; k < count; k++)
                {
                    int r = at + 6 + k * 12;
                    if (U16(d, r + 6) != 1) continue;                         // name id 1 = family
                    int plat = U16(d, r), length = U16(d, r + 8), off = U16(d, r + 10);
                    return plat == 3 || plat == 0 ? Encoding.BigEndianUnicode.GetString(d, strings + off, length) : Encoding.ASCII.GetString(d, strings + off, length);
                }
            }
            catch { }
            return null;
        }
    }

    // ---- the window ---------------------------------------------------------------------------------------------------------------------

    sealed class MenuItem
    {
        public string Text; public Func<string> Value; public Action Click; public RectangleF Hit;
    }

    sealed class LogLine { public string Text; public DateTime Born = DateTime.UtcNow; }

    sealed class GameWindow : Form
    {
        public enum View { Home, Settings, Run, Ask }

        const float W = 1280f, H = 720f;
        public View Current = View.Home;
        readonly List<MenuItem> items = new List<MenuItem>();
        public int Selected;
        readonly List<LogLine> log = new List<LogLine>();
        readonly List<string> status = new List<string>();
        public int ProgressNow;
        string askText = ""; string[] askOptions = new string[0]; Action<int> askDone; View askFrom;
        string runTitle = "Starting"; string resultLine = ""; bool running, failed;
        bool updateCheck;                                // the run page is the update check's: its Back returns home once the check has answered
        DateTime closeAt = DateTime.MaxValue;
        public int ExitCode;
        public int AskGraceMs = 900;           // a question ignores keys and clicks for this long, so a key still held down or a double click cannot answer it unseen
        public Point AskMouse { get { return askMouse; } }   // the audit's resting-mouse check measures from here, not from the real cursor (which a person may move at any time)
        DateTime askShownAt = DateTime.MinValue; Point askMouse;      // where the mouse sat when the question appeared
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 33 };
        readonly bool testing;

        public GameWindow(bool forTest = false)
        {
            testing = forTest;
            Text = "Grow Home Co-op";
            DoubleBuffered = true;
            ClientSize = new Size(1152, 648);
            MinimumSize = new Size(640, 400);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(40, 90, 200);
            KeyPreview = true;
            if (!forTest)
            {
                sayHook = s => Post(() => AddLine(s));
                chooseHook = (q, o) => Ask(q, o);
                progressHook = p => Post(() => { if (p > ProgressNow) ProgressNow = p; });
                hideHook = () => Post(() => Hide());
                timer.Tick += (s, e) => { if (DateTime.UtcNow > closeAt) { closeAt = DateTime.MaxValue; Close(); } Invalidate(); };
                timer.Start();
                Shown += (s, e) => { RefreshStatus(); if (autoPlay) BeginFlow(); };
            }
            ShowHome();
        }

        public bool IsInstalled { get { return File.Exists(Path.Combine(gameDir, "GrowHome.exe")) && File.Exists(Path.Combine(gameDir, "GrowHome_Data", "Managed", "CoopMod.dll")); } }

        void Post(Action a) { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { } }

        // ---- pages ------------------------------------------------------------------------------------------------------------------

        public void ShowHome()
        {
            Current = View.Home; items.Clear(); Selected = 0;
            items.Add(new MenuItem { Text = IsInstalled ? "Play" : "Install + Play", Click = BeginFlow });
            items.Add(new MenuItem { Text = "Settings", Click = ShowSettings });
            items.Add(new MenuItem { Text = "Check for updates", Click = BeginUpdateCheck });
            items.Add(new MenuItem { Text = "Quit", Click = () => Close() });
            Invalidate();
        }

        static string YesNo(bool yes) { return yes ? "[Yes] / No" : "Yes / [No]"; }

        public void ShowSettings()
        {
            Current = View.Settings; items.Clear(); Selected = 0;

            string introFile = Path.Combine(gameDir, "coop-intro.txt");
            string noUpdate = Path.Combine(installDir, "coop-noupdate.txt");
            items.Add(new MenuItem { Text = "Skip intro screens", Value = () => YesNo(!File.Exists(introFile)), Click = () => Toggle(introFile, "show the intro screens") });
            items.Add(new MenuItem { Text = "Check for updates", Value = () => YesNo(!File.Exists(noUpdate)), Click = () => Toggle(noUpdate, "never check") });
            string hudFile = Path.Combine(gameDir, "coop-ui-hidden.txt");
            string noNames = Path.Combine(gameDir, "coop-noname.txt");
            string noChat = Path.Combine(gameDir, "coop-nochat.txt");
            string noShare = Path.Combine(gameDir, "coop-noshare.txt");
            items.Add(new MenuItem { Text = "On-screen display", Value = () => YesNo(!File.Exists(hudFile)), Click = () => Toggle(hudFile, "the mod's on-screen display is hidden (F1 shows it again)") });
            items.Add(new MenuItem { Text = "Name tags", Value = () => YesNo(!File.Exists(noNames)), Click = () => Toggle(noNames, "name tags above players hidden") });
            items.Add(new MenuItem { Text = "Chat log", Value = () => YesNo(!File.Exists(noChat)), Click = () => Toggle(noChat, "chat log hidden (T still opens the chat box)") });
            items.Add(new MenuItem { Text = "Share world", Value = () => YesNo(!File.Exists(noShare)), Click = () => Toggle(noShare, "my plants, islands and crystals are not shared with friends") });
            items.Add(new MenuItem { Text = "Room for F2", Value = () => "<  " + CoopSettings.RoomLabel(CoopSettings.ReadRoom(gameDir)) + "  >", Click = () => CycleRoom() });
            items.Add(new MenuItem { Text = "Back", Click = () => { ShowHome(); RefreshStatus(); } });
            Invalidate();
        }

        // These settings are plain files next to the program: a file means "the unusual choice".
        void CycleRoom()
        {
            try { File.WriteAllText(Path.Combine(gameDir, CoopSettings.RoomFile), CoopSettings.NextRoom(CoopSettings.ReadRoom(gameDir))); } catch { }
            Invalidate();
        }

        void Toggle(string file, string note)
        {
            try { if (File.Exists(file)) File.Delete(file); else { Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, note); } } catch { }
            Invalidate();
        }

        void RefreshStatus()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var s = new List<string>();
                try
                {
                    string orig = null; try { orig = FindGame(); } catch { }
                    s.Add("GROW HOME|" + (orig != null ? "FOUND" : "NOT FOUND"));
                    s.Add("CO-OP COPY|" + (IsInstalled ? "INSTALLED  V" + InstalledVersion() : "NOT INSTALLED"));
                    s.Add("STEAM|" + (SteamLoggedIn() ? "SIGNED IN" : (Process.GetProcessesByName("steam").Length > 0 ? "STARTING" : "NOT RUNNING")));
                    string acct = ActiveAccount();
                    s.Add("ACCOUNT|" + (acct == null ? "-" : (IsBorrower(ManifestText(), acct) ? "BORROWS THE GAME" : "OWNS THE GAME")));
                    s.Add("UPDATES|" + (UpdatesDisabled() ? "OFF" : "ON, IN THE BACKGROUND"));
                    s.Add("INTRO SCREENS|" + (File.Exists(Path.Combine(gameDir, "coop-intro.txt")) ? "SHOWN" : "SKIPPED"));
                }
                catch { }
                Post(() => { status.Clear(); status.AddRange(s); Invalidate(); });
            });
        }

        // ---- the flow (runs on a worker thread) --------------------------------------------------------------------------------------

        public void BeginFlow()
        {
            if (running) return;
            running = true; failed = false; resultLine = ""; log.Clear(); ProgressNow = 0;
            runTitle = IsInstalled ? "Starting" : "Installing"; updateCheck = false;
            Current = View.Run; items.Clear(); Selected = 0;
            var worker = new Thread(() =>
            {
                int code = 1;
                try { code = RunFlow(); } catch (Exception e) { Post(() => AddLine("Something went wrong: " + e.Message)); }
                Post(() => FlowDone(code));
            }) { IsBackground = true };
            worker.Start();
            Invalidate();
        }

        // The home page's "Check for updates" button: it checks now, also when automatic checks are switched off in Settings, because the player asked for it.
        // A newer mod is downloaded and used at the next start, as the automatic check does; the installed mod is never changed while the game runs.
        public void BeginUpdateCheck()
        {
            if (running) return;
            running = true; failed = false; resultLine = ""; log.Clear(); ProgressNow = 0; updateCheck = true;
            runTitle = "Checking for updates";
            Current = View.Run; items.Clear(); Selected = 0;
            var worker = new Thread(() =>
            {
                try { CheckForUpdate(false, true, true); }
                catch (Exception e) { LastCheck = "failed"; Post(() => AddLine("Could not check: " + e.Message)); }
                Post(() => UpdateCheckDone());
            }) { IsBackground = true };
            worker.Start();
            Invalidate();
        }

        void UpdateCheckDone()
        {
            running = false;
            switch (LastCheck)
            {
                case "current": resultLine = "THE CO-OP MOD IS UP TO DATE (VERSION " + InstalledVersion() + ")."; break;
                case "staged": resultLine = "A NEWER VERSION IS DOWNLOADED. IT IS USED THE NEXT TIME YOU START THE GAME."; break;
                case "installed": resultLine = "A NEWER VERSION IS INSTALLED."; break;
                default: failed = true; resultLine = "COULD NOT CHECK FOR UPDATES. NOTHING WAS CHANGED."; break;
            }
            items.Clear(); Selected = 0;
            items.Add(new MenuItem { Text = "Back", Click = () => { ShowHome(); RefreshStatus(); } });
            Invalidate();
        }

        public bool CheckRunning { get { return running; } }
        public string ResultLine { get { return resultLine; } }

        void FlowDone(int code)
        {
            running = false; ExitCode = code;
            if (code == 0)
            {
                resultLine = "CO-OP MOD LOADED. HAVE FUN!";
                ProgressNow = 100;
                if (!testing && !Visible) Show(); closeAt = DateTime.UtcNow.AddSeconds(2.5);
            }
            else
            {
                failed = true; resultLine = "SOMETHING NEEDS ATTENTION";
                if (!testing && !Visible) Show(); items.Clear(); Selected = 0;
                items.Add(new MenuItem { Text = "Try again", Click = BeginFlow });
                items.Add(new MenuItem { Text = "Back", Click = () => { ShowHome(); RefreshStatus(); } });
                items.Add(new MenuItem { Text = "Quit", Click = () => Close() });
            }
            Invalidate();
        }

        void AddLine(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            if (log.Count > 0 && s.StartsWith("  still ") && log[log.Count - 1].Text.StartsWith("  still ")) log.RemoveAt(log.Count - 1);   // a counting line replaces itself
            log.Add(new LogLine { Text = s.Trim() });
            if (log.Count > 200) log.RemoveAt(0);
            Invalidate();
        }

        int Ask(string question, string[] options)
        {
            int result = options.Length - 1;
            var done = new ManualResetEvent(false);
            Post(() => ShowAsk(question, options, i => { result = i; done.Set(); }));
            done.WaitOne();
            try { Log("asked: " + question.Trim() + "  ->  " + options[Math.Max(0, Math.Min(options.Length - 1, result))]); } catch { }
            return result;
        }

        public void ShowAsk(string question, string[] options, Action<int> answered)
        {
            askFrom = Current == View.Ask ? askFrom : Current;
            Current = View.Ask; askText = question; askOptions = options; askDone = answered; askShownAt = DateTime.UtcNow; askMouse = Cursor.Position;
            items.Clear(); Selected = 0;
            for (int i = 0; i < options.Length; i++) { int k = i; items.Add(new MenuItem { Text = options[i], Click = () => Answer(k) }); }
            if (!testing && !Visible) Show();
            Invalidate();
        }

        void Answer(int index)
        {
            Current = askFrom; items.Clear(); Selected = 0;
            var cb = askDone; askDone = null;
            if (askFrom == View.Home) ShowHome();
            if (cb != null) cb(index);
            Invalidate();
        }

        // ---- input ------------------------------------------------------------------------------------------------------------------

        bool AskIsSettling() { return Current == View.Ask && (DateTime.UtcNow - askShownAt).TotalMilliseconds < AskGraceMs; }

        public void Activate() { if (AskIsSettling()) return; if (Selected >= 0 && Selected < items.Count && items[Selected].Click != null) items[Selected].Click(); }

        public void Back()
        {
            if (Current == View.Settings) { ShowHome(); RefreshStatus(); }
            else if (Current == View.Ask) { if (!AskIsSettling()) Answer(askOptions.Length - 1); }
            else if (Current == View.Home) Close();
            else if (Current == View.Run && !running && (failed || updateCheck)) { ShowHome(); RefreshStatus(); }
        }

        public void Move(int d) { if (items.Count > 0) { Selected = (Selected + d + items.Count) % items.Count; Invalidate(); } }

        public void PressKey(Keys k)
        {
            switch (k)
            {
                case Keys.Up: case Keys.W: Move(-1); break;
                case Keys.Down: case Keys.S: Move(1); break;
                case Keys.Left: case Keys.Right: if (Current == View.Settings && Selected < items.Count && items[Selected].Value != null) Activate(); break;
                case Keys.Enter: case Keys.Space: Activate(); break;
                case Keys.Escape: Back(); break;
                case Keys.Y: if (Current == View.Ask && askOptions.Length == 2 && !AskIsSettling()) Answer(0); break;
                case Keys.N: if (Current == View.Ask && askOptions.Length == 2 && !AskIsSettling()) Answer(1); break;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e) { PressKey(e.KeyCode); e.Handled = true; base.OnKeyDown(e); }

        PointF ToDesign(Point p, Size size)
        {
            float s = Math.Min(size.Width / W, size.Height / H);
            return new PointF((p.X - (size.Width - W * s) / 2f) / s, (p.Y - (size.Height - H * s) / 2f) / s);
        }

        public int ItemAt(PointF design) { for (int i = 0; i < items.Count; i++) if (items[i].Hit.Contains(design)) return i; return -1; }

        // Hovering selects a line, like the game's menus. But on a question the mouse usually happens to be resting right where an answer appears:
        // until it has really moved, where it sits chooses nothing (otherwise Enter answers whatever is under a resting mouse).
        public void Hover(PointF design, Point screen)
        {
            if (Current == View.Ask && Math.Abs(screen.X - askMouse.X) + Math.Abs(screen.Y - askMouse.Y) < 8) return;
            int i = ItemAt(design);
            if (i >= 0 && i != Selected) { Selected = i; Invalidate(); }
        }

        protected override void OnMouseMove(MouseEventArgs e) { Hover(ToDesign(e.Location, ClientSize), Cursor.Position); base.OnMouseMove(e); }
        protected override void OnMouseClick(MouseEventArgs e) { int i = ItemAt(ToDesign(e.Location, ClientSize)); if (i >= 0) { Selected = i; Activate(); } base.OnMouseClick(e); }

        // ---- drawing ----------------------------------------------------------------------------------------------------------------

        static readonly StringFormat Fmt = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces };

        static float Width(Graphics g, string t, Font f) { return g.MeasureString(t, f, 100000, Fmt).Width; }

        static void Glow(Graphics g, string t, Font f, Color c, float x, float y, Color glow)
        {
            using (var gb = new SolidBrush(Color.FromArgb(58, glow)))
                foreach (var o in new[] { new PointF(-2, 0), new PointF(2, 0), new PointF(0, -2), new PointF(0, 2), new PointF(-1.5f, -1.5f), new PointF(1.5f, 1.5f) })
                    g.DrawString(t, f, gb, x + o.X, y + o.Y, Fmt);
            using (var b = new SolidBrush(c)) g.DrawString(t, f, b, x, y, Fmt);
        }

        static void Dashes(Graphics g, float x1, float x2, float y, Color c)
        {
            using (var b = new SolidBrush(c)) for (float x = x1; x < x2 - 6; x += 11) g.FillRectangle(b, x, y, 7, 2.5f);
        }

        static void Dots(Graphics g, float x1, float x2, float y, Color c)
        {
            using (var b = new SolidBrush(c)) for (float x = x1; x < x2; x += 6) g.FillRectangle(b, x, y, 2.4f, 2.4f);
        }

        // greedy word wrap; a word that is wider than the line (a long path) is broken by letters
        public static List<string> Wrap(Graphics g, string text, Font f, float width)
        {
            var lines = new List<string>();
            foreach (var para in (text ?? "").Split('\n'))
            {
                string line = "";
                foreach (var word in para.Split(' '))
                {
                    string w = word;
                    while (Width(g, w, f) > width && w.Length > 1)
                    {
                        int cut = w.Length - 1; while (cut > 1 && Width(g, w.Substring(0, cut), f) > width) cut--;
                        if (line.Length > 0) { lines.Add(line); line = ""; }
                        lines.Add(w.Substring(0, cut)); w = w.Substring(cut);
                    }
                    string trial = line.Length == 0 ? w : line + " " + w;
                    if (Width(g, trial, f) <= width || line.Length == 0) line = trial; else { lines.Add(line); line = w; }
                }
                lines.Add(line);
            }
            return lines;
        }

        // A low-poly rock: an outer ring and a jittered inner ring joined by triangles, plus a faceted top, each face its own shade (lit from the upper left).
        static void Blob(Graphics g, float cx, float cy, float rx, float ry, int seed, Color baseColor)
        {
            var rnd = new Random(seed); int n = 11; var p = new PointF[n]; var q = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                double a = Math.PI * 2 * i / n, r = 0.72 + 0.28 * rnd.NextDouble();
                p[i] = new PointF(cx + (float)(Math.Cos(a) * rx * r), cy + (float)(Math.Sin(a) * ry * r * (Math.Sin(a) < 0 ? 1.1 : 0.6)));
                float jx = (float)(rnd.NextDouble() - 0.5) * rx * 0.25f, jy = (float)(rnd.NextDouble() - 0.5) * ry * 0.2f;
                q[i] = new PointF(cx + (p[i].X - cx) * 0.55f + jx - rx * 0.05f, cy + (p[i].Y - cy) * 0.55f - ry * 0.18f + jy);
            }
            Func<double, float> lit = a => 0.74f + 0.34f * (float)(0.5 - 0.5 * Math.Cos(a + 2.3)) + 0.16f * (float)rnd.NextDouble();
            Action<PointF[], float> face = (pts, light) =>
            {
                var c = Color.FromArgb(Math.Min(255, (int)(baseColor.R * light)), Math.Min(255, (int)(baseColor.G * light)), Math.Min(255, (int)(baseColor.B * light * 1.04f)));
                using (var b = new SolidBrush(c)) g.FillPolygon(b, pts);
            };
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n; double a = Math.PI * 2 * (i + 0.5) / n;
                face(new[] { p[i], p[j], q[j] }, lit(a));
                face(new[] { p[i], q[j], q[i] }, lit(a + 0.4));
            }
            var top = new PointF(cx - rx * 0.1f, cy - ry * 0.62f);
            for (int i = 0; i < n; i++) face(new[] { top, q[i], q[(i + 1) % n] }, lit(Math.PI * 2 * (i + 0.5) / n + 0.8) + 0.06f);
        }

        public void Draw(Graphics g, Size size)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            // sky
            using (var sky = new LinearGradientBrush(new Rectangle(0, 0, size.Width, size.Height + 1), Color.FromArgb(34, 70, 188), Color.FromArgb(86, 176, 255), 90f)) g.FillRectangle(sky, 0, 0, size.Width, size.Height);
            float s = Math.Min(size.Width / W, size.Height / H);
            float ox = (size.Width - W * s) / 2f, oy = (size.Height - H * s) / 2f;
            var saved = g.Save();
            g.TranslateTransform(ox, oy);
            g.ScaleTransform(s, s);
            Scenery(g);
            g.Restore(saved);
            // vignette: dark corners like the game's menus (under the text, so the text stays bright)
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(-size.Width * 0.30f, -size.Height * 0.40f, size.Width * 1.60f, size.Height * 1.80f);
                using (var v = new PathGradientBrush(path) { CenterColor = Color.FromArgb(0, 0, 0, 0), SurroundColors = new[] { Color.FromArgb(150, 4, 18, 90) } })
                    g.FillRectangle(v, 0, 0, size.Width, size.Height);
            }
            saved = g.Save();
            g.TranslateTransform(ox, oy);
            g.ScaleTransform(s, s);
            DrawPage(g);
            g.Restore(saved);
        }

        void Scenery(Graphics g)
        {
            // far floating islands
            using (var b = new SolidBrush(Color.FromArgb(34, 90, 120, 220)))
            {
                g.FillPolygon(b, new[] { new PointF(900, 120), new PointF(1010, 110), new PointF(1040, 175), new PointF(970, 250), new PointF(930, 190) });
                g.FillPolygon(b, new[] { new PointF(1090, 250), new PointF(1160, 240), new PointF(1180, 290), new PointF(1120, 330) });
                g.FillPolygon(b, new[] { new PointF(500, 30), new PointF(640, 20), new PointF(600, 120) });
            }
            // water and sand
            using (var w = new LinearGradientBrush(new RectangleF(0, 560, W, 200), Color.FromArgb(0, 120, 200, 255), Color.FromArgb(150, 130, 210, 255), 90f)) g.FillRectangle(w, 0, 560, W, 200);
            using (var sand = new SolidBrush(Color.FromArgb(70, 214, 214, 150))) g.FillRectangle(sand, 0, 608, W, 20);
            // rocks
            Blob(g, 860, 470, 250, 250, 3, Color.FromArgb(88, 100, 196));
            Blob(g, 705, 600, 215, 120, 8, Color.FromArgb(96, 104, 200));
            Blob(g, 450, 700, 190, 70, 5, Color.FromArgb(104, 112, 205));
            Blob(g, 1070, 640, 150, 70, 11, Color.FromArgb(86, 98, 190));
            using (var green = new SolidBrush(Color.FromArgb(70, 150, 150)))
            {
                g.FillPolygon(green, new[] { new PointF(852, 205), new PointF(862, 175), new PointF(872, 205) });
                g.FillPolygon(green, new[] { new PointF(660, 400), new PointF(672, 372), new PointF(684, 400) });
            }
        }

        void DrawPage(Graphics g)
        {
            bool wide = Current == View.Settings || Current == View.Ask;
            // header
            Glow(g, "@ B.OTANICAL U.TILITY D.ROID", Look.Tiny(25), Look.Text, 65, 34, Look.Cyan);
            Dashes(g, 65, wide ? 1215 : 625, 68, Look.Text);
            Glow(g, "//MISSION REPORT : CO-OP EXPEDITION", Look.Tiny(20), Look.Text, 65, 76, Look.Cyan);
            // footer
            Glow(g, "[oo]", Look.Tiny(16), Look.Text, 70, 628, Look.Cyan);
            Glow(g, "/|nn|\\", Look.Tiny(16), Look.Text, 66, 646, Look.Cyan);
            Glow(g, " d b  CO-OP v" + BundledVersion, Look.Tiny(15), Look.Text, 66, 668, Look.Cyan);

            if (!wide) DrawStatusPanel(g);

            string title = Current == View.Home ? "Grow Home Co-op" : Current == View.Settings ? "Settings" : Current == View.Ask ? "Question" : runTitle;
            Glow(g, title, Look.Big(Current == View.Home ? 54 : 40), Look.Text, 65, Current == View.Home ? 120 : 140, Look.Cyan);

            float y = Current == View.Home ? 250 : 225;
            if (Current == View.Ask)
            {
                var f = Look.Big(26); y = 215;
                foreach (var line in Wrap(g, askText, f, 1080)) { Glow(g, line, f, Look.Text, 65, y, Look.Cyan); y += 36; }
                y += 40;
            }
            if (Current == View.Run && !failed && !(resultLine.Length > 0))
            {

                bool pulse = (DateTime.UtcNow.Millisecond / 500) % 2 == 0;
                Glow(g, ">>>Please wait", Look.Big(34), pulse ? Look.Pink : Color.FromArgb(255, 205, 232), 65, y, Look.Red);
                y += 64;
            }
            if (resultLine.Length > 0) { Glow(g, resultLine, Look.Big(26), failed ? Look.Pink : Look.Text, 65, y, failed ? Look.Red : Look.Cyan); y += 64; }

            float rowW = wide ? 1150 : 560;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                bool on = i == Selected;
                var font = Look.Big(36);
                string label = (on ? ">>>" : "") + it.Text;
                float x = 65;
                Glow(g, label, font, on ? Look.Pink : Look.Text, x, y, on ? Look.Red : Look.Cyan);
                float lw = Width(g, label, font);
                if (it.Value != null)
                {
                    string value = it.Value();
                    var vf = Look.Big(26);
                    float vw = Width(g, value, vf);
                    Dots(g, x + lw + 14, 65 + rowW - vw - 18, y + 28, on ? Look.Pink : Look.Text);
                    Glow(g, value, vf, on ? Look.Pink : Look.Text, 65 + rowW - vw, y + 6, on ? Look.Red : Look.Cyan);
                }
                float step = Current == View.Settings ? Math.Min(50f, 400f / items.Count) : 50f;   // the settings page has more rows now: they tighten to fit above the footer
                it.Hit = new RectangleF(55, y - 4, rowW + 20, step - 2);
                y += step;
            }
        }

        void DrawStatusPanel(Graphics g)
        {
            Glow(g, "STATUS REPORT", Look.Tiny(25), Look.Text, 735, 34, Look.Cyan);
            Dashes(g, 735, 1180, 68, Look.Text);
            var frame = new RectangleF(735, 100, 445, 445);
            using (var fill = new SolidBrush(Color.FromArgb(46, 60, 130, 255))) g.FillRectangle(fill, frame);
            using (var grid = new Pen(Color.FromArgb(48, 255, 255, 255), 1.4f))
            {
                for (float gx = frame.Left + 64; gx < frame.Right; gx += 64) g.DrawLine(grid, gx, frame.Top, gx, frame.Bottom);
                for (float gy = frame.Top + 64; gy < frame.Bottom; gy += 64) g.DrawLine(grid, frame.Left, gy, frame.Right, gy);
            }
            using (var border = new Pen(Color.FromArgb(240, 255, 255, 255), 4f)) g.DrawRectangle(border, frame.X, frame.Y, frame.Width, frame.Height);
            Dots(g, frame.Left - 4, frame.Right + 12, frame.Bottom + 2, Color.White);
            using (var sq = new SolidBrush(Color.White)) { g.FillRectangle(sq, frame.Left + 140, frame.Bottom - 12, 12, 12); g.FillRectangle(sq, frame.Right + 4, frame.Bottom - 12, 4, 12); }

            if (Current == View.Home)
            {
                var kf = Look.Tiny(19); float y = frame.Top + 22;
                foreach (var row in status)
                {
                    var parts = row.Split('|');
                    Glow(g, parts[0], kf, Look.Dim, frame.Left + 20, y, Look.Cyan);
                    string v = parts.Length > 1 ? parts[1] : "";
                    bool bad = v.StartsWith("NOT ");
                    Glow(g, v, kf, bad ? Look.Pink : Look.Text, frame.Left + 20, y + 24, bad ? Look.Red : Look.Cyan);
                    y += 62;
                }
                if (status.Count == 0) Glow(g, "READING...", kf, Look.Dim, frame.Left + 20, y, Look.Cyan);
            }
            else
            {
                var lf = Look.Tiny(19); float lh = 26, maxY = frame.Bottom - 18;
                var wrapped = new List<KeyValuePair<string, bool>>();
                for (int i = 0; i < log.Count; i++)
                {
                    string text = log[i].Text;
                    bool newest = i == log.Count - 1;
                    if (newest) { int shown = (int)((DateTime.UtcNow - log[i].Born).TotalMilliseconds / 14); if (shown < text.Length) text = text.Substring(0, Math.Max(0, shown)); }
                    foreach (var l in Wrap(g, text, lf, frame.Width - 40)) wrapped.Add(new KeyValuePair<string, bool>(l, newest));
                }
                int fit = (int)((maxY - (frame.Top + 16)) / lh);
                int from = Math.Max(0, wrapped.Count - fit);
                float y = frame.Top + 16;
                for (int i = from; i < wrapped.Count; i++) { Glow(g, wrapped[i].Key.ToUpperInvariant(), lf, wrapped[i].Value ? Look.Pink : Look.Text, frame.Left + 20, y, wrapped[i].Value ? Look.Red : Look.Cyan); y += lh; }
            }

            // progress as the five red boxes of the game's status screen
            int[] marks = { 10, 20, 40, 60, 100 };
            using (var small = new Pen(Look.Red, 3f))
            {
                float bx = 905;
                Glow(g, "CO-OP", Look.Tiny(17), Look.Text, 736, 574, Look.Cyan);
                Glow(g, ProgressNow + "%", Look.Big(24), Look.Text, 736, 594, Look.Cyan);
                foreach (int m in marks)
                {
                    bool lit = ProgressNow >= m;
                    var r = new RectangleF(bx, 568, 52, 52);
                    if (lit) using (var f = new SolidBrush(Color.FromArgb(120, Look.Red))) g.FillRectangle(f, r);
                    g.DrawRectangle(small, r.X, r.Y, r.Width, r.Height);
                    string t = m + "%"; var tf = Look.Tiny(16);
                    Glow(g, t, tf, lit ? Color.White : Look.Red, bx + (52 - Width(g, t, tf)) / 2, 586, Look.Red);
                    bx += 55;
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e) { Draw(e.Graphics, ClientSize); }
        protected override void OnPaintBackground(PaintEventArgs e) { }

        // ---- for the audit ------------------------------------------------------------------------------------------------------------

        public string[] ItemTexts() { return items.Select(i => i.Text + (i.Value != null ? " " + i.Value() : "")).ToArray(); }
        public PointF ItemCenter(int i) { var r = items[i].Hit; return new PointF(r.X + r.Width / 2, r.Y + r.Height / 2); }
        public Point ToClient(PointF design, Size size) { float s = Math.Min(size.Width / W, size.Height / H); return new Point((int)(design.X * s + (size.Width - W * s) / 2f), (int)(design.Y * s + (size.Height - H * s) / 2f)); }
        public void AskForTest(string q, string[] o, out int answer) { answer = Ask(q, o); }
        public void TestAddLine(string s) { AddLine(s); }
        public void TestSetStatus(IEnumerable<string> rows) { status.Clear(); status.AddRange(rows); }
        public void TestSetProgress(int p) { ProgressNow = p; }
        public void TestRun(string title) { Current = View.Run; runTitle = title; items.Clear(); running = true; }
        public float MaxItemWidth(Graphics g) { float m = 0; foreach (var it in items) { float w = Width(g, ">>>" + it.Text, Look.Big(36)); if (it.Value != null) w += Width(g, it.Value(), Look.Big(26)) + 40; m = Math.Max(m, w); } return m; }
    }
}
