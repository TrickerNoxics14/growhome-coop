using System.Collections.Generic;
using System.Text;

namespace CoopMod
{
    // Players are shown by their Steam names. A Steam name is typed by a stranger and the game's font only has plain letters, so every name is cleaned before it is shown:
    // letters, digits and common signs only, no markup, no control or look-alike characters, at most 16 long, and two players never carry the same name.
    public static class PlayerNames
    {
        public const int MaxLength = 16;

        public static string Clean(string raw, string fallback)
        {
            if (raw == null) return fallback;
            var sb = new StringBuilder();
            bool lastSpace = true;
            foreach (char c in raw)
            {
                if (c == ' ' || c == '\t' || c == ' ') { if (!lastSpace) { sb.Append(' '); lastSpace = true; } continue; }
                if (c < 0x21 || c > 0x7E) continue;                 // control characters, emoji, other alphabets: the game's font cannot draw them
                if (c == '<' || c == '>' || c == '\\' || c == '|') continue;   // would look like markup
                sb.Append(c); lastSpace = false;
                if (sb.Length >= MaxLength) break;
            }
            string s = sb.ToString().Trim();
            return s.Length == 0 || s == "unknown" || s == "[unknown]" ? fallback : s;
        }

        // names in rank order; later duplicates get " 2", " 3", ... (the first one keeps its plain name on every PC, because everybody ranks players the same way)
        public static void MakeUnique(string[] names)
        {
            var seen = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names.Length; i++)
            {
                string baseName = names[i]; int n;
                if (!seen.TryGetValue(baseName, out n)) { seen[baseName] = 1; continue; }
                string candidate;
                do { n++; candidate = Shorten(baseName, MaxLength - (" " + n).Length) + " " + n; } while (seen.ContainsKey(candidate));
                seen[baseName] = n; seen[candidate] = 1; names[i] = candidate;
            }
        }

        static string Shorten(string s, int max) { return s.Length <= max ? s : s.Substring(0, max); }
    }
}
