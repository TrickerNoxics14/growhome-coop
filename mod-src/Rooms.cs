using System.Collections.Generic;
using System.Text;

namespace CoopMod
{
    // Who may be in a room. A PUBLIC ("open") room is listed in the room list and anybody may join. A PRIVATE room is found only by its code, and nobody who joins
    // is connected to until the host has accepted them; the host's decision is kept in the room's data as two short lists of Steam ids. Everything read from the
    // room's data was written by a stranger's PC, so it is parsed defensively.
    public static class RoomRules
    {
        public const string Open = "open", Private = "private";
        public const int MaxIds = 16;

        // anything that is not exactly "open" is private (the safe side)
        public static string Mode(string raw) { return raw == Open ? Open : Private; }

        public static List<ulong> ParseIds(string csv)
        {
            var ids = new List<ulong>();
            if (string.IsNullOrEmpty(csv) || csv.Length > 400) return ids;
            foreach (string part in csv.Split(','))
            {
                ulong v;
                if (part.Length > 0 && part.Length <= 20 && ulong.TryParse(part, out v) && v != 0 && !ids.Contains(v)) ids.Add(v);
                if (ids.Count >= MaxIds) break;
            }
            return ids;
        }

        public static string AddId(string csv, ulong id)
        {
            var ids = ParseIds(csv);
            if (id != 0 && !ids.Contains(id) && ids.Count < MaxIds) ids.Add(id);
            var sb = new StringBuilder();
            foreach (ulong v in ids) { if (sb.Length > 0) sb.Append(','); sb.Append(v); }
            return sb.ToString();
        }

        // May I connect to this room member? Refused players never; the host always; in an open room everybody; in a private room only those the host accepted.
        public static bool Allowed(string mode, ulong owner, ulong who, string approved, string denied)
        {
            if (who == 0) return false;
            if (ParseIds(denied).Contains(who)) return false;
            if (who == owner) return true;
            if (Mode(mode) == Open) return true;
            return ParseIds(approved).Contains(who);
        }

        // Steam gives a room to another member when its owner leaves; that member must never be taken for the host (they could accept themselves into a private room)
        public static bool HostChanged(ulong hostWhenIJoined, ulong ownerNow) { return hostWhenIJoined != 0 && ownerNow != hostWhenIJoined; }

        public static string RoomName(string raw) { return PlayerNames.Clean(raw, "Room"); }
    }

    public class Room { public ulong Id; public string Name; public int Players, Max; public bool Ask; }   // Ask: the host must accept you
}
