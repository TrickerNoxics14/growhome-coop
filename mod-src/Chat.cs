using System;
using System.Collections.Generic;
using System.Text;

namespace CoopMod
{
    // In-game text chat. A message is [7][length][letters]: plain text, at most 100 characters. Who said it is never taken from the message: the game knows which player's
    // connection it arrived on, so nobody can speak in somebody else's name. Everything that arrives is cleaned and rate-limited before it is shown.
    public static class Chat
    {
        public const byte Msg = 7;
        public const int MaxText = 100, MaxBytes = 2 + MaxText, KeepLines = 40;
        const int BurstMessages = 5; const float BurstSeconds = 6f;

        public class Line { public string Name, Text; public float At; public bool Mine; }
        public static readonly List<Line> Lines = new List<Line>();
        static readonly Dictionary<ulong, List<float>> recent = new Dictionary<ulong, List<float>>();

        // Only what the game's font can draw; no markup, no control characters; spaces squeezed; at most 100 characters.
        public static string Clean(string raw)
        {
            if (raw == null) return "";
            var sb = new StringBuilder(); bool lastSpace = true;
            foreach (char c in raw)
            {
                if (c == ' ' || c == '\t' || c == ' ') { if (!lastSpace) { sb.Append(' '); lastSpace = true; } continue; }
                if (c < 0x21 || c > 0x7E || c == '<' || c == '>' || c == '\\') continue;
                sb.Append(c); lastSpace = false;
                if (sb.Length >= MaxText) break;
            }
            return sb.ToString().Trim();
        }

        public static byte[] Encode(string text)
        {
            string t = Clean(text);
            var b = new byte[2 + t.Length];
            b[0] = Msg; b[1] = (byte)t.Length;
            Encoding.ASCII.GetBytes(t, 0, t.Length, b, 2);
            return b;
        }

        public static bool TryDecode(byte[] m, out string text)
        {
            text = null;
            if (m == null || m.Length < 3 || m.Length > MaxBytes || m[0] != Msg) return false;
            int len = m[1];
            if (len < 1 || len > MaxText || m.Length != 2 + len) return false;
            text = Clean(Encoding.ASCII.GetString(m, 2, len));
            return text.Length > 0;
        }

        // at most 5 messages from one player in 6 seconds; the rest are dropped
        public static bool Allow(ulong sender, float now)
        {
            List<float> times;
            if (!recent.TryGetValue(sender, out times)) recent[sender] = times = new List<float>();
            times.RemoveAll(t => now - t > BurstSeconds || t > now);
            if (times.Count >= BurstMessages) return false;
            times.Add(now);
            return true;
        }

        public static void Add(string name, string text, float now, bool mine)
        {
            Lines.Add(new Line { Name = name, Text = text, At = now, Mine = mine });
            while (Lines.Count > KeepLines) Lines.RemoveAt(0);
        }
    }
}
