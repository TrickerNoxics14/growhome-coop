using System.Diagnostics;

namespace CoopMod
{
    // Measures how long the mod's own per-frame work takes, to tell "hitch caused by the mod" from "hitch caused by the game or the PC".
    public static class Perf
    {
        static readonly Stopwatch sw = new Stopwatch();
        static double frameMs, heavyMs; static string heavy = "";
        public static double LastFrameMs, LastHeavyMs; public static string LastHeavy = "";

        public static void NewFrame()
        {
            LastFrameMs = frameMs; LastHeavyMs = heavyMs; LastHeavy = heavy;
            frameMs = 0; heavyMs = 0; heavy = "";
        }

        public static void Begin() { sw.Reset(); sw.Start(); }

        public static void End(string name)
        {
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds;
            frameMs += ms;
            if (ms > heavyMs) { heavyMs = ms; heavy = name; }
            if (ms > 5) CoopBehaviour.Log("slow: " + name + " took " + ms.ToString("F1") + " ms");
        }
    }
}
