using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CoopMod
{
    public class SrSplitRecord { public string Id; public long PbMs = -1, BestSegMs = -1; }

    public class SrRecord
    {
        public long PbMs = -1; public string PbDate = "";
        public int Attempts, Finished;
        public readonly List<SrSplitRecord> Splits = new List<SrSplitRecord>();
        public SrSplitRecord Split(string id) { foreach (var s in Splits) if (s.Id == id) return s; return null; }
    }

    // Personal bests and best segments, one plain key=value file per category (records-<cat>.txt, co-op runs records-coop-<cat>.txt) in
    // coop-saves\speedrun, outside the save slot. Written through a .tmp file with the previous version kept as .bak; a damaged file falls back to it.
    public static class SpeedrunRecords
    {
        public static string Format(SrRecord r)
        {
            var sb = new StringBuilder();
            sb.Append("version=1\r\n");
            sb.Append("pb_ms=").Append(r.PbMs).Append("\r\n");
            sb.Append("pb_date=").Append((r.PbDate ?? "").Replace("\r", "").Replace("\n", "")).Append("\r\n");
            sb.Append("attempts=").Append(r.Attempts).Append("\r\n");
            sb.Append("finished=").Append(r.Finished).Append("\r\n");
            foreach (var s in r.Splits) sb.Append("split=").Append(s.Id).Append('|').Append(s.PbMs).Append('|').Append(s.BestSegMs).Append("\r\n");
            return sb.ToString();
        }

        // null for anything that is not a whole records file
        public static SrRecord Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var lines = text.Replace("\r", "").Split('\n');
            if (lines.Length == 0 || lines[0].Trim() != "version=1") return null;
            var r = new SrRecord();
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) return null;
                string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                long n; int a;
                switch (k)
                {
                    case "pb_ms": if (!long.TryParse(v, out n) || n < -1) return null; r.PbMs = n; break;
                    case "pb_date": r.PbDate = v; break;
                    case "attempts": if (!int.TryParse(v, out a) || a < 0) return null; r.Attempts = a; break;
                    case "finished": if (!int.TryParse(v, out a) || a < 0) return null; r.Finished = a; break;
                    case "split":
                        {
                            var p = v.Split('|');
                            long pb, best;
                            if (p.Length != 3 || p[0].Length == 0 || p[0].Length > 24 || !long.TryParse(p[1], out pb) || !long.TryParse(p[2], out best) || pb < -1 || best < -1) return null;
                            if (r.Splits.Count >= SrCategories.MaxSplits * 2) return null;
                            r.Splits.Add(new SrSplitRecord { Id = p[0], PbMs = pb, BestSegMs = best });
                        }
                        break;
                    default: break;     // a newer version's extra keys are kept out, not a damage
                }
            }
            return r;
        }

        public static SrRecord Load(string file)
        {
            foreach (var f in new[] { file, file + ".bak" })
            {
                try
                {
                    if (!File.Exists(f)) continue;
                    var r = Parse(File.ReadAllText(f));
                    if (r != null) return r;
                    CoopBehaviour.Log("speedrun: " + Path.GetFileName(f) + " is damaged, " + (f == file ? "using its backup" : "starting over"));
                }
                catch (Exception e) { CoopBehaviour.Log("speedrun: records not read " + e.GetType().Name); }
            }
            return new SrRecord();
        }

        public static bool Save(string file, SrRecord r)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));
                string tmp = file + ".tmp";
                File.WriteAllText(tmp, Format(r));
                if (File.Exists(file))
                {
                    if (Parse(File.ReadAllText(file)) != null) File.Copy(file, file + ".bak", true);   // only a good file becomes the backup
                    File.Delete(file);
                }
                File.Move(tmp, file);
                return true;
            }
            catch (Exception e) { CoopBehaviour.Log("speedrun: records not saved " + e.GetType().Name); return false; }
        }

        // The PB times and best segments into a new run's engine, by split id.
        public static void Fill(SrRecord r, SplitEngine e)
        {
            for (int i = 0; i < e.Count; i++)
            {
                var s = r.Split(e.Defs[i].Id);
                e.PbAt[i] = s != null && r.PbMs >= 0 ? s.PbMs : -1;
                e.BestSeg[i] = s != null ? s.BestSegMs : -1;
            }
        }

        // A finished run: a valid one counts, becomes the PB when faster, and improves every best segment it beat. True for a new PB.
        public static bool Apply(SrRecord r, SplitEngine e, bool valid, string date)
        {
            if (!e.Ended || !valid) return false;
            r.Finished++;
            bool pb = r.PbMs < 0 || e.EndMs < r.PbMs;
            for (int i = 0; i < e.Count; i++)
            {
                var s = r.Split(e.Defs[i].Id);
                if (s == null) { s = new SrSplitRecord { Id = e.Defs[i].Id }; r.Splits.Add(s); }
                if (pb) s.PbMs = e.States[i] == SrSplitState.Done ? e.AtMs[i] : -1;
                if (e.CleanSegment(i)) { long seg = e.SegmentMs(i); if (s.BestSegMs < 0 || seg < s.BestSegMs) s.BestSegMs = seg; }
            }
            if (pb) { r.PbMs = e.EndMs; r.PbDate = date ?? ""; }
            return pb;
        }
    }
}
