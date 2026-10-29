using System;
using System.IO;
using System.Threading;

namespace CoopMod
{
    // Writes to the log when the game stops running frames (a freeze that leaves no error behind), and again when it recovers.
    // It runs on its own thread and is given the log file's path up front: Unity's own calls (Application.dataPath, ...) are not allowed off the main thread.
    public class Watchdog
    {
        public static Watchdog Main;

        readonly string path;
        readonly int stallMs;
        int beat;
        volatile bool stop;

        public Watchdog(string logPath, int stallAfterMs) { path = logPath; stallMs = stallAfterMs; beat = Environment.TickCount; }

        public void Beat() { beat = Environment.TickCount; }     // the main thread calls this once per frame
        public void Stop() { stop = true; }
        public void Start() { new Thread(Run) { IsBackground = true, Name = "coop-watchdog" }.Start(); }

        void Write(string s) { try { File.AppendAllText(path, s + "\n"); } catch (Exception) { } }

        void Run()
        {
            bool stalled = false; int stalledFrom = 0, nextNote = 0;
            while (!stop)
            {
                Thread.Sleep(250);
                int now = Environment.TickCount, age = now - beat;
                if (age >= stallMs)
                {
                    if (!stalled)
                    {
                        stalled = true; stalledFrom = beat; nextNote = 10000;
                        Write("watchdog: the game has not run a frame for " + (age / 1000f).ToString("F1") + " s; mod code running: " + Perf.InMod + "; garbage collections so far " + GC.CollectionCount(0) + ", heap " + (GC.GetTotalMemory(false) >> 20) + " MB");
                    }
                    else if (age >= nextNote) { nextNote += 10000; Write("watchdog: still no frame after " + (age / 1000) + " s; mod code running: " + Perf.InMod); }
                }
                else if (stalled) { stalled = false; Write("watchdog: frames are running again after about " + ((now - stalledFrom) / 1000f).ToString("F1") + " s"); }
            }
        }
    }
}
