using System;
using System.Collections.Generic;

namespace CoopMod
{
    // The party: the Steam lobby seen as a group of friends who play together, as in other games. Everybody gathers on the start screen (CO-OP PARTY),
    // friends are invited through Steam, members say when they are ready, the leader (the host) presses START GAME and every member still on the start
    // screen follows into the leader's world. The rules are pure so the audit checks them; the Steam side is in Lobby.cs and Bootstrap.cs.
    public static class Party
    {
        public const int MaxShown = 4;           // party members listed in the menu; more are summed up as "+N MORE"
        public const int FriendsPerPage = 4;
        public const int NameLength = 14;        // a name in a menu line

        public class Member { public ulong Id; public string Name; public bool Leader, Me, Ready, Waiting; }
        public class Friend { public ulong Id; public string Name; public bool InGame, Invited; }

        // A member on the start screen follows the leader in when the leader has started a game this member has not seen started yet: a member who went
        // back to the start screen on purpose is not pulled straight back in. Only once let in, never the leader, never twice at the same time.
        public static bool ShouldFollow(bool atStart, bool isLeader, bool admitted, bool following, string play, string seenPlay)
        {
            return atStart && !isLeader && admitted && !following && !string.IsNullOrEmpty(play) && play != seenPlay;
        }

        // The leader's "play" note in the party data: a new round number when the leader starts playing, kept while playing, cleared on the start screen.
        public static string PlayFor(bool playing, bool atStart, string current, int lastRound)
        {
            current = current ?? "";
            if (playing) return current.Length > 0 ? current : (Math.Max(0, lastRound) + 1).ToString();
            return atStart ? "" : current;
        }

        public static int ParseRound(string s) { int v; return int.TryParse(s, out v) && v > 0 && v < 1000000 ? v : 0; }

        public static string Short(string s, int max)
        {
            s = (s ?? "").ToUpperInvariant();
            return s.Length <= max ? s : s.Substring(0, max);
        }

        // One line of the member list, e.g. "TRICKER (YOU)  LEADER", "POINT  READY", "EVE  WAITING" (waiting for the leader to let them in).
        public static string RosterLabel(Member m)
        {
            string name = Short(string.IsNullOrEmpty(m.Name) ? "PLAYER" : m.Name, NameLength);
            string tag = m.Leader ? "LEADER" : m.Waiting ? "WAITING" : m.Ready ? "READY" : "";
            return (m.Me ? name + " (YOU)" : name) + (tag.Length > 0 ? "  " + tag : "");
        }

        // The leader first, everybody else in the order Steam lists them.
        public static List<Member> Ordered(List<Member> members)
        {
            var list = new List<Member>();
            if (members == null) return list;
            foreach (var m in members) if (m.Leader) list.Add(m);
            foreach (var m in members) if (!m.Leader) list.Add(m);
            return list;
        }

        // members other than the leader who said they are ready
        public static int ReadyCount(List<Member> members)
        {
            int n = 0;
            if (members != null) foreach (var m in members) if (!m.Leader && m.Ready && !m.Waiting) n++;
            return n;
        }

        // Friends who are playing Grow Home first, then everybody else, by name.
        public static List<Friend> SortFriends(List<Friend> friends)
        {
            var list = new List<Friend>(friends ?? new List<Friend>());
            var keyed = new List<KeyValuePair<int, Friend>>();
            for (int i = 0; i < list.Count; i++) keyed.Add(new KeyValuePair<int, Friend>(i, list[i]));
            keyed.Sort((a, b) =>
            {
                if (a.Value.InGame != b.Value.InGame) return a.Value.InGame ? -1 : 1;
                int c = string.Compare(a.Value.Name ?? "", b.Value.Name ?? "", StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : a.Key.CompareTo(b.Key);
            });
            list.Clear();
            foreach (var k in keyed) list.Add(k.Value);
            return list;
        }

        public static int Pages(int count) { return Math.Max(1, (count + FriendsPerPage - 1) / FriendsPerPage); }
    }
}
