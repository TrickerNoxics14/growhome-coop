using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CoopMod
{
    // Writes to the log when the game stops running frames (a freeze that leaves no error behind), and again when it recovers.
    // It runs on its own thread and is given the log file's path up front: Unity's own calls (Application.dataPath, ...) are not allowed off the main thread.
    // The game's own watchdog (Main) is also a hang guard: a freeze that lasts gives the mouse back (and minimizes a fullscreen game), and a game frozen
    // for a whole minute is ended, so a black fullscreen window never keeps the PC. The test dogs of the audit only write.
    public class Watchdog
    {
        public static Watchdog Main;

        readonly string path;
        readonly int stallMs;
        int beat;
        volatile bool stop, sawFrame;
        public volatile bool FullScreen;               // the main thread says so every frame (Screen.fullScreen is a Unity call)
        public Func<int> Clock = delegate { return Environment.TickCount; };   // the audit uses a clock of its own
        public const int SleepGapMs = 20000;           // this thread was held up this long: the PC slept, nothing froze

        // the hang guard (off unless Guard is called)
        int releaseMs, killMs, killFirstMs;
        Action onRelease, onKill;
        public string HangFile;                        // written when the guard steps in: the launcher starts the next game in a small window
        public int Releases, Kills, SleepsSeen, NotesCleared;   // what the guard did (read by the audit)
        public int ReleaseMs { get { return releaseMs; } }
        public int KillMs { get { return killMs; } }
        public int KillFirstMs { get { return killFirstMs; } }
        public bool Guarded { get { return onKill != null; } }

        public Watchdog(string logPath, int stallAfterMs) { path = logPath; stallMs = stallAfterMs; beat = Environment.TickCount; }

        public void Beat() { beat = Clock(); sawFrame = true; }     // the main thread calls this once per frame
        public bool SawFrame { get { return sawFrame; } }
        public void Stop() { stop = true; }
        public void Start() { beat = Clock(); new Thread(Run) { IsBackground = true, Name = "coop-watchdog" }.Start(); }

        // release: after this long without a frame; kill: after this long once frames have run (killFirst: before the first frame, the level is still loading)
        public void Guard(int releaseAfterMs, int killAfterMs, int killBeforeFirstFrameMs, Action release, Action kill)
        {
            releaseMs = releaseAfterMs; killMs = killAfterMs; killFirstMs = killBeforeFirstFrameMs; onRelease = release; onKill = kill;
        }

        void Write(string s) { try { File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss") + " " + s + "\n"); } catch (Exception) { } }
        void Mark(string s) { if (HangFile == null) return; try { File.WriteAllText(HangFile, DateTime.Now.ToString("s") + " " + s + "\r\n"); } catch (Exception) { } }

        void Run()
        {
            bool stalled = false, released = false, killed = false; int stalledFrom = 0, nextNote = 0, last = Clock();
            while (!stop)
            {
                Thread.Sleep(250);
                int now = Clock();
                if (now - last > SleepGapMs) { SleepsSeen++; beat = now; Write("watchdog: this PC was asleep for about " + ((now - last) / 1000) + " s; not counted as a freeze"); }
                last = now;
                int age = now - beat;
                if (age >= stallMs)
                {
                    if (!stalled)
                    {
                        stalled = true; stalledFrom = beat; nextNote = 10000;
                        Write("watchdog: the game has not run a frame for " + (age / 1000f).ToString("F1") + " s; mod code running: " + Perf.InMod + "; garbage collections so far " + GC.CollectionCount(0) + ", heap " + (GC.GetTotalMemory(false) >> 20) + " MB");
                    }
                    else if (age >= nextNote) { nextNote += 10000; Write("watchdog: still no frame after " + (age / 1000) + " s; mod code running: " + Perf.InMod); }
                }
                else if (stalled)
                {
                    stalled = false;
                    Write("watchdog: frames are running again after about " + ((now - stalledFrom) / 1000f).ToString("F1") + " s");
                    // the game came back by itself: it is not frozen, so the launcher must not call it frozen (or start the next game small)
                    if (released && !killed && HangFile != null) { try { File.Delete(HangFile); NotesCleared++; } catch (Exception) { } }
                    released = false;
                }

                if (onKill == null || killed) continue;
                if (age >= releaseMs && !released)
                {
                    released = true; Releases++;
                    Write("watchdog: no frame for " + (age / 1000) + " s; giving the mouse back" + (FullScreen ? " and minimizing the fullscreen game" : ""));
                    Mark("frozen for " + (age / 1000) + " s");
                    try { onRelease(); } catch (Exception) { }
                }
                int limit = sawFrame ? killMs : killFirstMs;
                if (age >= limit)
                {
                    killed = true; Kills++;
                    Write("watchdog: no frame for " + (age / 1000) + " s" + (sawFrame ? "" : " (the game never showed a frame)") + "; ending the game so the screen comes back. The next start uses a small window.");
                    Mark("ended after " + (age / 1000) + " s without a frame");
                    try { onKill(); } catch (Exception) { }
                }
            }
        }

        // ---- what the game's own watchdog does (Win32 calls are allowed on this thread) ----

        [DllImport("user32.dll")] static extern bool ClipCursor(IntPtr rect);
        [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr p, uint code);
        delegate bool EnumProc(IntPtr h, IntPtr p);

        // the game's own window (Unity's window class, this process)
        public static IntPtr GameWindow()
        {
            IntPtr found = IntPtr.Zero; uint me = GetCurrentProcessId();
            EnumWindows(delegate (IntPtr h, IntPtr p)
            {
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (pid != me || !IsWindowVisible(h)) return true;
                var sb = new StringBuilder(64); GetClassName(h, sb, 64);
                if (sb.ToString() == "UnityWndClass") { found = h; return false; }
                if (found == IntPtr.Zero) found = h;
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public void ReleaseScreen()
        {
            ClipCursor(IntPtr.Zero);
            if (!FullScreen) return;
            IntPtr h = GameWindow();
            if (h != IntPtr.Zero) ShowWindowAsync(h, 6);       // SW_MINIMIZE: the desktop comes back even if the game never draws again
        }

        public static void EndGame() { TerminateProcess(GetCurrentProcess(), 3); }
    }
}
