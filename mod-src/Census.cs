using System.Collections.Generic;
using UnityEngine;

namespace CoopMod
{
    // F5: write a summary of what the world contains, so we know which objects need syncing.
    public static class Census
    {
        static string Clean(string n)
        {
            n = n.Replace("(Clone)", "").Trim();
            int i = n.Length;
            while (i > 0 && (char.IsDigit(n[i - 1]) || n[i - 1] == ' ' || n[i - 1] == '_')) i--;
            return i > 0 ? n.Substring(0, i) : n;
        }

        public static void Run()
        {
            var counts = new Dictionary<string, int>();
            var dynamicCounts = new Dictionary<string, int>();
            var runtime = new Dictionary<string, int>();
            int total = 0;
            foreach (SaveInfo si in Object.FindObjectsOfType(typeof(SaveInfo)))
            {
                total++;
                string n = Clean(si.name);
                int c; counts.TryGetValue(n, out c); counts[n] = c + 1;
                var rb = si.rigidbody;
                if (rb != null && !rb.isKinematic) { dynamicCounts.TryGetValue(n, out c); dynamicCounts[n] = c + 1; }
                if (si.m_runtimeSpawned) { runtime.TryGetValue(n, out c); runtime[n] = c + 1; }
            }
            CoopBehaviour.Log("census: " + total + " SaveInfo objects, " + counts.Count + " kinds");
            var keys = new List<string>(counts.Keys);
            keys.Sort((a, b) => counts[b].CompareTo(counts[a]));
            foreach (var k in keys)
            {
                int d; dynamicCounts.TryGetValue(k, out d);
                int r; runtime.TryGetValue(k, out r);
                CoopBehaviour.Log("census:   " + k + " x" + counts[k] + (d > 0 ? " (physics " + d + ")" : "") + (r > 0 ? " (runtime-spawned " + r + ")" : ""));
            }
            var branches = Object.FindObjectsOfType(typeof(PlantBranch));
            float totalLen = 0f; int elems = 0;
            foreach (PlantBranch b in branches) { totalLen += b.m_Length; elems += b.m_Elements.Count; }
            CoopBehaviour.Log("census: PlantBranch x" + branches.Length + " totalLength=" + totalLen.ToString("F1") + " elements=" + elems + "; PlantRoot x" + Object.FindObjectsOfType(typeof(PlantRoot)).Length);
            CoopBehaviour.Log("census: end");
        }
    }
}
