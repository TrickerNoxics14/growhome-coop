using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace CoopMod
{
    // Audit checks for the session as a whole: the hang guard, Save & Quit in the pause menu (QuitGuard), and guests going back to the start menu
    // when the leader leaves (LeaderWatch). Group "session" (audit\check-groups.txt).
    public partial class SelfTest
    {
        // ---- LEADER-LEFT: the rules, with made-up frames --------------------------------------------------------------------------------

        void CheckLeaderWatch()
        {
            const ulong L = 76561199000000077UL;
            var bad = new List<string>();
            string v; int i; float t;

            // a 10 s blip is no leave; 15 s of silence is
            var w = new LeaderWatch(); v = null;
            for (i = 0; i < 100 && v == null; i++) v = w.Step(true, L, true, "3", 0.1f, false, false);
            if (v != null) bad.Add("kicked after a 10 s blip: " + v);
            w.Step(true, L, true, "3", 0.1f, true, false);
            t = 0f; v = null;
            while (v == null && t < 30f) { v = w.Step(true, L, true, "3", 0.1f, false, false); t += 0.1f; }
            if (v == null || !v.Contains("no signal") || t < 14.7f || t > 15.4f) bad.Add("silence: " + v + " after " + t.ToString("F1") + " s");
            if (w.Step(true, L, true, "3", 20f, false, true) != null) bad.Add("a second verdict for the same stay");

            // my own long frames (a load) are not the leader's silence
            w = new LeaderWatch(); v = null;
            for (i = 0; i < 20 && v == null; i++) v = w.Step(true, L, true, "3", 5f, false, false);
            if (v != null) bad.Add("my long frames counted as silence: " + v);

            // the leader's own word, and the party ending, count at once
            w = new LeaderWatch(); w.Step(true, L, true, "3", 0.02f, true, false);
            v = w.Step(true, L, true, "3", 0.02f, false, true);
            if (v == null || !v.Contains("said it left")) bad.Add("leave message: " + v);
            w = new LeaderWatch(); w.Step(true, L, true, "3", 0.02f, true, false);
            v = w.Step(true, L, false, "", 0.02f, false, false);
            if (v == null || !v.Contains("left the party")) bad.Add("party gone: " + v);

            // the leader back on the start screen (the play note cleared after it was on); a play note never seen is no leave
            w = new LeaderWatch(); w.Step(true, L, true, "4", 0.02f, true, false);
            v = w.Step(true, L, true, "", 0.02f, true, false);
            if (v == null || !v.Contains("start screen")) bad.Add("play cleared: " + v);
            w = new LeaderWatch(); v = null;
            for (i = 0; i < 100 && v == null; i++) v = w.Step(true, L, true, "", 0.05f, true, false);
            if (v != null) bad.Add("kicked 5 s after arriving without the play note (it may still be on its way): " + v);
            // ...but a leader that left while I was loading never puts the note on again: 10 s in its world without it is a leave
            t = 5f;
            while (v == null && t < 30f) { v = w.Step(true, L, true, "", 0.05f, true, false); t += 0.05f; }
            if (v == null || !v.Contains("while I was loading") || t < 9.8f || t > 10.5f) bad.Add("left while I was loading: " + v + " after " + t.ToString("F1") + " s");
            // the note showing up late (within the 10 s) is a normal stay
            w = new LeaderWatch(); v = null;
            for (i = 0; i < 160 && v == null; i++) v = w.Step(true, L, true, i < 150 ? "" : "6", 0.05f, true, false);
            for (i = 0; i < 400 && v == null; i++) v = w.Step(true, L, true, "6", 0.05f, true, false);
            if (v != null) bad.Add("a late play note still counted as a leave: " + v);

            // no guest in a leader's world (a race keeps everybody in their own world, or I left the party myself): never
            w = new LeaderWatch(); v = null;
            for (i = 0; i < 300 && v == null; i++) v = w.Step(false, L, true, "", 1f, false, i == 5);
            if (v != null || w.Leader != 0) bad.Add("not a guest, still watched: " + v);
            // a guest without a party leader (an F7 pull): never
            w = new LeaderWatch(); v = null;
            for (i = 0; i < 300 && v == null; i++) v = w.Step(true, 0UL, false, "", 0.5f, false, false);
            if (v != null) bad.Add("no party, still kicked: " + v);
            // a new stay after leaving the world is watched again
            w = new LeaderWatch(); w.Step(true, L, true, "3", 0.02f, true, false); w.Step(true, L, true, "3", 0.02f, false, true);
            w.Step(false, 0UL, true, "", 0.02f, false, false);
            w.Step(true, L, true, "5", 0.02f, true, false);
            v = w.Step(true, L, true, "5", 0.02f, false, true);
            if (v == null) bad.Add("a second stay was not watched");

            // only the leader may say it: the gate
            var msg = new byte[5]; msg[0] = LeaderWatch.MsgLeft;
            bool gate = MessageGate.Classify(msg, true) == Route.LeaderLeft && MessageGate.Classify(msg, false) == Route.Ignore
                && MessageGate.Classify(new byte[] { LeaderWatch.MsgLeft, 1, 2 }, true) == Route.Ignore && MessageGate.Classify(new byte[] { LeaderWatch.MsgLeft, 1, 2, 3, 4, 5 }, true) == Route.Ignore;
            if (!gate) bad.Add("the gate lets a leave message through from somebody else or at a wrong size");
            Check("LEADER-LEFT", "the leader leaving is noticed at once from its own message, the party ending or it going back to the start screen, after 15 s of silence otherwise (a 10 s blip or my own long load is no leave), once per stay, never in a race or without a party, and only the leader's message counts",
                bad.Count == 0, bad.Count == 0 ? "15 cases ok" : string.Join(" | ", bad.ToArray()));
        }

        // ---- LEADER-BEAT: "heard from the leader" is the leader's own counter moving, not packets arriving ----------------------------------

        void CheckLeaderBeat()
        {
            const ulong L = 76561199000000078UL;
            var bad = new List<string>();
            string v; float t; int i;

            // the wire: [0xC5][session][number]; no session or number 0 is refused; an older version reads it as nothing
            uint s, q;
            var b = LeaderBeat.Write(77u, 5u);
            bool wire = b.Length == LeaderBeat.Size && LeaderBeat.Read(b, out s, out q) && s == 77u && q == 5u
                && !LeaderBeat.Read(LeaderBeat.Write(0u, 5u), out s, out q) && !LeaderBeat.Read(LeaderBeat.Write(7u, 0u), out s, out q)
                && !LeaderBeat.Read(new byte[] { LeaderBeat.Msg, 1, 2, 3 }, out s, out q) && !LeaderBeat.Read(new byte[10], out s, out q) && !LeaderBeat.Read(null, out s, out q)
                && PlayerState.Read(b) == null;
            if (!wire) bad.Add("the beat message round trip or its refusals");

            // only a newer beat moves it; a replayed or late one does not; a restarted leader (new session) counts from its first beat
            var p = new Peer { Id = L };
            bool order = LeaderBeat.Advance(p, 9u, 1u) && LeaderBeat.Advance(p, 9u, 2u) && !LeaderBeat.Advance(p, 9u, 2u) && !LeaderBeat.Advance(p, 9u, 1u)
                && LeaderBeat.Advance(p, 10u, 1u) && !LeaderBeat.Advance(p, 10u, 1u) && !LeaderBeat.Advance(null, 1u, 1u);
            if (!order) bad.Add("beat order");

            // packets keep coming (pings, echoes) but the leader's counter stands: that is silence, a leave after 15 s
            uint beats = 40;
            var w = new LeaderWatch(); v = null; t = 0f;
            while (v == null && t < 30f) { v = w.StepBeats(true, L, true, "3", 0.1f, beats, false); t += 0.1f; }
            if (v == null || !v.Contains("no signal") || t < 14.7f || t > 15.4f) bad.Add("a standing counter: " + v + " after " + t.ToString("F1") + " s");
            // the counter moving once a second for a minute: never a leave
            w = new LeaderWatch(); v = null;
            for (i = 0; i < 600 && v == null; i++) { if (i % 10 == 0) beats++; v = w.StepBeats(true, L, true, "3", 0.1f, beats, false); }
            if (v != null) bad.Add("a counter moving every second still gave: " + v);
            // it moved for a while, then stopped: a leave 15 s after its last move
            w = new LeaderWatch(); v = null;
            for (i = 0; i < 50; i++) { if (i % 10 == 0) beats++; w.StepBeats(true, L, true, "3", 0.1f, beats, false); }
            t = 0f;
            while (v == null && t < 30f) { v = w.StepBeats(true, L, true, "3", 0.1f, beats, false); t += 0.1f; }
            if (v == null || !v.Contains("no signal") || t < 13.5f || t > 14.6f) bad.Add("a counter that stopped: " + v + " " + t.ToString("F1") + " s after the loop");
            // a counter that moves only once, right after I arrived, is no proof of a live leader (the first value I see is not news)
            w = new LeaderWatch(); v = null; t = 0f;
            w.StepBeats(true, L, true, "3", 0.1f, 500u, false);
            while (v == null && t < 30f) { v = w.StepBeats(true, L, true, "3", 0.1f, 500u, false); t += 0.1f; }
            if (v == null || t > 15.4f) bad.Add("the first value seen counted as news: " + v);

            Check("LEADER-BEAT", "\"heard from the leader\" is a counter only the leader advances (its pose numbers and its once-a-second beat [0xC5][session][number]): a standing counter is silence even while packets arrive, a moving one is never a leave, replays and late beats move nothing, a restarted leader counts from its first beat",
                bad.Count == 0, bad.Count == 0 ? "wire, order and 4 watch cases ok" : string.Join(" | ", bad.ToArray()));
        }

        // a made-up player's unreliable packets, fed through the real receive path
        class Feed : ITransport
        {
            public readonly Queue<byte[]> In = new Queue<byte[]>();
            public void Send(byte[] data) { }
            public void SendReliable(byte[] data) { }
            public byte[] Receive() { return In.Count > 0 ? In.Dequeue() : null; }
            public byte[] ReceiveReliable() { return null; }
            public string Diagnostics() { return "self test feed"; }
            public void Close() { }
        }

        // a stand-in player's link that records the leader beats it is sent
        class Recorder : ITransport
        {
            public readonly List<byte[]> Beats = new List<byte[]>();
            public void Send(byte[] data) { if (data != null && data.Length > 0 && data[0] == LeaderBeat.Msg) Beats.Add((byte[])data.Clone()); }
            public void SendReliable(byte[] data) { }
            public byte[] Receive() { return null; }
            public byte[] ReceiveReliable() { return null; }
            public string Diagnostics() { return "self test recorder"; }
            public void Close() { }
        }

        IEnumerator TestLeaderBeatLive()
        {
            var cb = GetComponent<CoopBehaviour>();
            if (cb == null || !WorldSync.InWorld() || WorldSync.GuestMode) { Check("LEADER-BEAT", "my game makes its leader beat while I am in my own world", false, "mod=" + (cb != null) + " in world=" + WorldSync.InWorld() + " guest=" + WorldSync.GuestMode); yield break; }

            // 1. my own game's tick makes a beat once a second while I am in my own world, and the real send loop (NetTick) sends each one to a
            //    real (not bot) player: a stand-in player whose link records what it is sent sits in the peer list for the 3.2 s
            var rec = new Recorder();
            var listener = new Peer { Id = Friend + 78, IsBot = false, Number = 8, Link = rec };
            int made0 = cb.BeatsMade; uint seq0 = cb.LastBeatSeq;
            cb.AddPeerForTest(listener);
            yield return StartCoroutine(WaitReal(3.2f));      // fixed on purpose: counts beats over a known time
            cb.RemovePeerForTest(listener);
            int made = cb.BeatsMade - made0; uint seq1 = cb.LastBeatSeq;
            var sentSeq = new List<uint>(); bool sentRead = true; uint sentSession = 0;
            foreach (var b in rec.Beats) { uint ss, sq; if (LeaderBeat.Read(b, out ss, out sq)) { sentSeq.Add(sq); sentSession = ss; } else sentRead = false; }
            bool consecutive = true; for (int i = 1; i < sentSeq.Count; i++) if (sentSeq[i] != sentSeq[i - 1] + 1) consecutive = false;
            uint firstSent = sentSeq.Count > 0 ? sentSeq[0] : 0u, lastSent = sentSeq.Count > 0 ? sentSeq[sentSeq.Count - 1] : 0u;
            Sender("my leader beat as sent to a real player (the real send loop)", firstSent > 0 ? firstSent - 1 : 0u, lastSent);
            bool rate = made >= 2 && made <= 5 && seq1 - seq0 == (uint)made;
            bool sentOk = sentRead && consecutive && sentSeq.Count >= 2 && sentSeq.Count <= made && sentSeq.Count >= made - 1 && lastSent <= seq1 && lastSent >= seq1 - 1 && firstSent == seq0 + 1 && sentSession == CoopBehaviour.MotionSession;
            rate &= sentOk;

            // 2. the real receive path: pings and their echoes keep a player heard but never move its counter; a newer beat or pose does, a replay does not
            var feed = new Feed();
            var peer = new Peer { Id = Friend + 77, IsBot = true, BotIndex = 9, Number = 9, Link = feed };
            var ping = new byte[5]; ping[0] = 0xC1; Buffer.BlockCopy(BitConverter.GetBytes(Time.realtimeSinceStartup), 0, ping, 1, 4);
            var pong = new byte[5]; pong[0] = 0xC2; Buffer.BlockCopy(BitConverter.GetBytes(Time.realtimeSinceStartup), 0, pong, 1, 4);
            feed.In.Enqueue((byte[])ping.Clone()); feed.In.Enqueue(pong);
            cb.ReceiveStateForTest(peer);
            bool pingsHeard = Time.realtimeSinceStartup - peer.LastPacketTime < 1f; uint afterPings = peer.Beats;
            feed.In.Enqueue(LeaderBeat.Write(4242u, 1u)); cb.ReceiveStateForTest(peer); uint b1 = peer.Beats;
            feed.In.Enqueue(LeaderBeat.Write(4242u, 1u)); feed.In.Enqueue((byte[])ping.Clone()); cb.ReceiveStateForTest(peer); uint b1again = peer.Beats;
            feed.In.Enqueue(LeaderBeat.Write(4242u, 2u)); cb.ReceiveStateForTest(peer); uint b2 = peer.Beats;
            var pose = new PlayerState { seq = 5, pos = new Vector3(0f, 10f, 0f), vel = Vector3.zero, camForward = Vector3.forward, hasMotion = true, session = 4242u, sendMs = 100u, mflags = MotionWire.Grounded };
            feed.In.Enqueue(pose.Write()); feed.In.Enqueue(pose.Write()); cb.ReceiveStateForTest(peer); uint b3 = peer.Beats;
            Sender("a made-up leader (beats and a pose)", 0u, peer.Beats);
            bool recv = pingsHeard && afterPings == 0 && b1 == 1 && b1again == 1 && b2 == 2 && b3 == 3;
            Check("LEADER-BEAT", "my game sends its leader beat about once a second while I am in my own world; in the real receive path pings and echoes never move a player's counter, a newer beat or pose moves it once, a replay does not",
                rate && recv, "beats made " + made + " in 3.2 s (numbers " + seq0 + " -> " + seq1 + "), sent to the real player " + sentSeq.Count + " (numbers " + firstSent + " -> " + lastSent + ", in order=" + consecutive + ", session ok=" + (sentSession == CoopBehaviour.MotionSession) + "); counter after pings " + afterPings + " (heard=" + pingsHeard + "), beat " + b1 + ", replay " + b1again + ", next beat " + b2 + ", pose twice " + b3);
        }

        // ---- LEADER-LEFT: a guest in the real game is sent back to the start menu --------------------------------------------------------

        IEnumerator TestLeaderLeftLive()
        {
            var cb = GetComponent<CoopBehaviour>();
            if (cb == null || !WorldSync.InWorld()) { Check("LEADER-LEFT", "a guest in the world goes back to the start menu when the leader leaves", false, "not in the world"); yield break; }
            string root0 = Path.GetFullPath(SaveGuard.Root);
            if (!WorldSync.GuestMode && Snapshot(root0, Path.Combine(root0, "guest-session")).Count == 0)
                Try("LEADER-LEFT-SAVE", "save my own world first, so the kick has a save to leave alone", delegate { SaveSystem.SaveLevel(); SaveSystem.SavePlayerProfile(); });
            if (!WorldSync.GuestMode) Try("LEADER-LEFT", "enter guest mode", delegate { WorldSync.EnterGuestMode(); });
            if (!WorldSync.GuestMode) { Check("LEADER-LEFT", "a guest in the world goes back to the start menu when the leader leaves", false, "guest mode did not start"); yield break; }
            string root = Path.GetFullPath(SaveGuard.Root), guest = Path.Combine(root, "guest-session");
            var before = Snapshot(root, guest);
            const ulong L = Friend + 50;
            int kicks = cb.Kicks;
            cb.LeaderWatchTestParty = true; cb.LeaderWatchTestPlay = "1"; cb.LeaderWatchTestLeader = L;
            // the leader's leave heard before I watch it (still loading its world) is kept, not dropped
            bool early = true;
            if (cb.LeaderWatchForTest.Leader == 0)
            {
                cb.HeardLeaderLeftForTest(new Peer { Id = L, Link = sink }, WorldSync.LoadedEpoch);
                early = cb.EarlyLeftFromForTest == L;
                cb.ForgetEarlyLeftForTest();
            }
            yield return null; yield return null; yield return null;
            bool latched = cb.LeaderWatchForTest.Leader == L && early;

            // the leader's counter moving keeps me in its world past the silence limit (shortened to 4 s for this): the watch reads the counter, not the clock alone
            uint fakeBeats = 1; uint beatsFirst = fakeBeats;
            var leaderPeer = new Peer { Id = L, IsBot = false, Number = 7, Link = new Feed() };
            leaderPeer.Beats = fakeBeats;
            cb.TestLeaderBeats = null;                       // the watch reads the real player's counter (the production lookup), not a test counter
            cb.AddPeerForTest(leaderPeer);
            float keepLimit = LeaderWatch.SilenceLimit; LeaderWatch.SilenceLimit = 4f;
            float beatEnd = Time.realtimeSinceStartup + 6f;
            while (Time.realtimeSinceStartup < beatEnd && cb.Kicks == kicks)
            {
                fakeBeats++; leaderPeer.Beats = fakeBeats;
                yield return StartCoroutine(WaitReal(0.5f));   // fixed on purpose: a beat every half second
            }
            LeaderWatch.SilenceLimit = keepLimit;
            cb.RemovePeerForTest(leaderPeer);
            bool beatKept = cb.Kicks == kicks && WorldSync.InWorld() && cb.LeaderWatchForTest.Silence < 1f;
            Sender("the test leader's beat counter", beatsFirst, fakeBeats);
            Check("LEADER-BEAT", "a guest whose leader's counter keeps moving (the real player's counter in the peer list) stays in its world past the silence limit (shortened to 4 s for 6 s), and the watch's silence stays near zero",
                beatKept, "kicks " + (cb.Kicks - kicks) + ", silence " + cb.LeaderWatchForTest.Silence.ToString("F1") + " s, counter " + beatsFirst + " -> " + fakeBeats);
            // somebody else saying it, and a 3 s quiet spell: nothing happens
            cb.HeardLeaderLeftForTest(new Peer { Id = L + 1, Link = sink }, WorldSync.LoadedEpoch);
            yield return StartCoroutine(WaitReal(3f));      // fixed on purpose: proves nothing happens
            bool stayed = cb.Kicks == kicks && WorldSync.InWorld();
            // the leader's own message, through the message gate as it comes from the network: the verdict (held here, not carried out)
            var msg = new byte[5]; msg[0] = LeaderWatch.MsgLeft; Buffer.BlockCopy(BitConverter.GetBytes(WorldSync.LoadedEpoch), 0, msg, 1, 4);
            cb.HoldKickForTest = true;
            cb.RouteReliableForTest(new Peer { Id = L, Link = sink }, msg, true);
            yield return StartCoroutine(WaitRealUntil(() => cb.PendingKickForTest != null, 3f));
            string heldWhy = cb.PendingKickForTest;
            bool direct = heldWhy != null && heldWhy.Contains("said it left") && cb.Kicks == kicks;
            cb.CancelKickForTest(); cb.HoldKickForTest = false;
            yield return null; yield return null; yield return null;
            bool relatched = cb.LeaderWatchForTest.Leader == L && cb.Kicks == kicks;
            // the same message, and in the next frame my walkster is switched off (I died): it is kept, not lost when the watch resets, and counts
            // as soon as I am back in the world
            int parked0 = cb.LeftParked;
            cb.RouteReliableForTest(new Peer { Id = L, Link = sink }, msg, true);
            cb.TestOutOfWorld = true;
            yield return null;
            bool parked = cb.LeftParked == parked0 + 1 && cb.EarlyLeftFromForTest == L && cb.LeaderWatchForTest.Leader == 0 && cb.Kicks == kicks;
            cb.TestOutOfWorld = false;
            float t0 = Time.realtimeSinceStartup;
            yield return StartCoroutine(WaitRealUntil(() => cb.Kicks > kicks, 3f));
            float kickedAfter = Time.realtimeSinceStartup - t0;
            yield return StartCoroutine(WaitRealUntil(() => !WorldSync.InGameOrLoading() && Singleton<StartScreen>.Exists, 40f));
            cb.LeaderWatchTestLeader = 0; cb.TestLeaderBeats = null;
            bool atStart = !WorldSync.InGameOrLoading() && Singleton<StartScreen>.Exists;
            var afterKick = Snapshot(root, guest);
            bool own = SameFiles(before, afterKick);
            Check("LEADER-LEFT-SAVE", "after the leader-left kick the guest's own save files are byte-identical (SHA-256 of every file, before vs after), and there was a save to compare",
                before.Count > 0 && own && cb.Kicks == kicks + 1, before.Count + " files before, " + afterKick.Count + " after; digest " + Digest(before) + " -> " + Digest(afterKick) + "; kicked=" + (cb.Kicks - kicks));
            bool said = ReadShared(Here("coopmod-steam.log")).Contains(LeaderWatch.Message);
            Check("LEADER-LEFT", "a guest in the real game is sent back to the start menu by the game's own quit when the leader says it left (\"THE LEADER LEFT THE WORLD\"), also when the message comes just as the guest dies (kept until it is back), not by anybody else's message or a short quiet spell, and its own save stays byte-identical",
                latched && stayed && direct && relatched && parked && cb.Kicks == kicks + 1 && cb.LastKickWhy.Contains("said it left") && atStart && own && said,
                "watched=" + latched + " stayed through the blip=" + stayed + " verdict=" + (heldWhy ?? "none") + " watched again=" + relatched + " kept through a moment out of the world=" + parked + " kicked=" + (cb.Kicks - kicks) + " (" + cb.LastKickWhy + ", after " + kickedAfter.ToString("F1") + " s) start screen=" + atStart + " own save same=" + own + " message=" + said);
        }

        // no static field of the quit guard can hold a plant part (a branch, or a list/array/set of them)
        static bool GuardHoldsNoBranch()
        {
            foreach (var f in typeof(QuitGuard).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                var t = f.FieldType;
                if (typeof(PlantBranch).IsAssignableFrom(t) || typeof(PlantRoot).IsAssignableFrom(t) || t == typeof(GameObject) || t == typeof(UnityEngine.Object)) return false;
                if (t.IsArray && (typeof(PlantBranch).IsAssignableFrom(t.GetElementType()) || t.GetElementType() == typeof(GameObject))) return false;
                if (t.IsGenericType) foreach (var a in t.GetGenericArguments()) if (typeof(PlantBranch).IsAssignableFrom(a) || a == typeof(GameObject)) return false;
            }
            return true;
        }

        static string Digest(Dictionary<string, string> files)
        {
            var keys = new List<string>(files.Keys); keys.Sort(StringComparer.Ordinal);
            var sb = new System.Text.StringBuilder();
            foreach (var k in keys) sb.Append(k).Append('=').Append(files[k]).Append('\n');
            return HashOf(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).Substring(0, 16);
        }

        // ---- HANG-GUARD ------------------------------------------------------------------------------------------------------------------

        class FakeClock { public volatile int T = 100000; public int Now() { return T; } }

        IEnumerator TestHangGuard()
        {
            var main = Watchdog.Main;
            bool optOut = File.Exists(Here("coop-nohangguard.txt"));
            bool sane = main != null && CoopBehaviour.WatchdogFromAwake && main.SawFrame && (optOut || (main.Guarded && main.KillMs >= 60000 && main.KillFirstMs >= main.KillMs && main.ReleaseMs >= 10000 && main.ReleaseMs < main.KillMs))
                && main.HangFile != null && main.HangFile.EndsWith("coop-hang.txt");
            Check("HANG-GUARD", "the game's watchdog starts in Awake (a freeze before the first frame is caught too) and its guard never ends a game before a whole minute without a frame (two before the first frame)", sane,
                main == null ? "no watchdog" : "from Awake=" + CoopBehaviour.WatchdogFromAwake + " guarded=" + main.Guarded + (optOut ? " (switched off by coop-nohangguard.txt)" : "") + " release " + main.ReleaseMs + " ms, end " + main.KillMs + " ms, before the first frame " + main.KillFirstMs + " ms");

            string dir = Path.Combine(Path.GetTempPath(), "coop-hang-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var dogs = new List<Watchdog>();
            try
            {
                // A: never a frame (still loading): the mouse comes back first, the game is ended only at the longer first-frame limit
                var ca = new FakeClock(); int relA = 0, endA = 0;
                var a = new Watchdog(Path.Combine(dir, "a.log"), 200) { Clock = ca.Now, HangFile = Path.Combine(dir, "a-hang.txt") };
                a.Guard(400, 900, 1500, delegate { relA++; }, delegate { endA++; }); a.Start(); dogs.Add(a);
                for (int i = 0; i < 4; i++) { ca.T += 250; yield return StartCoroutine(WaitReal(0.3f)); }
                bool aEarly = relA == 1 && endA == 0;
                for (int i = 0; i < 3; i++) { ca.T += 250; yield return StartCoroutine(WaitReal(0.3f)); }
                bool aEnded = endA == 1 && File.Exists(a.HangFile) && ReadShared(Path.Combine(dir, "a.log")).Contains("never showed a frame");

                // B: frames ran, then a freeze: ended at the normal limit, once, with the note for the launcher
                var cbk = new FakeClock(); int relB = 0, endB = 0;
                var b = new Watchdog(Path.Combine(dir, "b.log"), 200) { Clock = cbk.Now, HangFile = Path.Combine(dir, "b-hang.txt") };
                b.Guard(400, 900, 1500, delegate { relB++; }, delegate { endB++; }); b.Start(); dogs.Add(b);
                for (int i = 0; i < 10; i++) { cbk.T += 16; b.Beat(); yield return null; }
                yield return StartCoroutine(WaitReal(0.3f));
                bool bRunning = relB == 0 && endB == 0;
                for (int i = 0; i < 4; i++) { cbk.T += 250; yield return StartCoroutine(WaitReal(0.3f)); }
                bool bEnded = relB == 1 && endB == 1 && File.Exists(b.HangFile) && ReadShared(Path.Combine(dir, "b.log")).Contains("ending the game");
                for (int i = 0; i < 4; i++) { cbk.T += 250; yield return StartCoroutine(WaitReal(0.3f)); }
                bool bOnce = endB == 1;

                // C: the PC sleeps (the clock jumps by minutes in one go): no freeze, nothing done
                var cc = new FakeClock(); int relC = 0, endC = 0;
                var c = new Watchdog(Path.Combine(dir, "c.log"), 200) { Clock = cc.Now };
                c.Guard(400, 900, 1500, delegate { relC++; }, delegate { endC++; }); c.Start(); dogs.Add(c);
                c.Beat(); yield return StartCoroutine(WaitReal(0.3f));
                cc.T += 300000; yield return StartCoroutine(WaitReal(0.6f));
                bool cSlept = c.SleepsSeen >= 1 && relC == 0 && endC == 0;

                // D: a long stall that recovers by itself (a slow load): the mouse came back, but the note is removed again, so the launcher never calls it frozen
                var cd = new FakeClock(); int relD = 0, endD = 0;
                var d0 = new Watchdog(Path.Combine(dir, "d.log"), 200) { Clock = cd.Now, HangFile = Path.Combine(dir, "d-hang.txt") };
                d0.Guard(400, 900, 1500, delegate { relD++; }, delegate { endD++; }); d0.Start(); dogs.Add(d0);
                for (int i = 0; i < 10; i++) { cd.T += 16; d0.Beat(); yield return null; }
                for (int i = 0; i < 2; i++) { cd.T += 250; yield return StartCoroutine(WaitReal(0.3f)); }
                bool dNoted = relD == 1 && File.Exists(d0.HangFile);
                for (int i = 0; i < 10; i++) { cd.T += 16; d0.Beat(); yield return null; }
                yield return StartCoroutine(WaitReal(0.6f));
                bool dCleared = dNoted && endD == 0 && !File.Exists(d0.HangFile) && d0.NotesCleared == 1;

                Check("HANG-GUARD", "a frozen game gets the mouse back (and a fullscreen one is minimized) first, is ended after the limit and only once, leaves coop-hang.txt for the launcher; a game still loading gets the longer limit; a PC that slept is no freeze; a stall that recovers removes its note again",
                    aEarly && aEnded && bRunning && bEnded && bOnce && cSlept && dCleared,
                    "loading: released=" + aEarly + " ended at the longer limit=" + aEnded + "; frozen: quiet while running=" + bRunning + " released+ended=" + bEnded + " once=" + bOnce + "; slept: ignored=" + cSlept + "; recovered: noted=" + dNoted + " note removed=" + dCleared);
            }
            finally
            {
                foreach (var d in dogs) d.Stop();
            }
            yield return StartCoroutine(WaitReal(0.3f));
            try { Directory.Delete(dir, true); } catch (Exception) { }

            // the screen: the co-op copy runs in a window
            int nw, nh;
            bool r1 = ScreenRules.Windowed(true, false, 1920, 1080, 1920, 1080, out nw, out nh) && nw == 1280 && nh == 720;
            bool r2 = ScreenRules.Windowed(true, false, 1600, 900, 1920, 1080, out nw, out nh) && nw == 1600 && nh == 900;
            bool r3 = !ScreenRules.Windowed(true, true, 1920, 1080, 1920, 1080, out nw, out nh) && !ScreenRules.Windowed(false, false, 1280, 720, 1920, 1080, out nw, out nh);
            bool r4 = ScreenRules.Windowed(true, false, 1280, 720, 1280, 720, out nw, out nh) && nw < 1280 && nh < 720 && nw >= 640;
            bool live = !Screen.fullScreen || File.Exists(Here("coop-fullscreen.txt"));
            Check("HANG-GUARD", "a game that starts fullscreen (its first start, or its own setting) is switched to a window that fits the desktop, unless coop-fullscreen.txt allows fullscreen; this game runs in a window",
                r1 && r2 && r3 && r4 && live, "desktop size=" + r1 + " smaller kept=" + r2 + " allowed/windowed untouched=" + r3 + " small desktop=" + r4 + " this game windowed=" + live + " (" + Screen.width + "x" + Screen.height + ")");
        }

        // ---- PAUSE-ROWS and QUIT-GUARD ----------------------------------------------------------------------------------------------------

        static string PauseOrder(Transform group)
        {
            var l = new List<string>();
            for (int i = 0; i < group.childCount; i++) if (group.GetChild(i).gameObject.activeSelf) l.Add(group.GetChild(i).name);
            return string.Join(",", l.ToArray());
        }

        // every active line inside the panel (1 px slack), as laid out now
        static string PauseFit(Transform group)
        {
            var g = group as RectTransform; if (g == null) return "no panel";
            Canvas.ForceUpdateCanvases();
            var bad = new List<string>();
            var corners = new Vector3[4];
            Rect pr = g.rect;
            for (int i = 0; i < group.childCount; i++)
            {
                var row = group.GetChild(i) as RectTransform;
                if (row == null || !row.gameObject.activeSelf) continue;
                row.GetWorldCorners(corners);
                foreach (var c in corners)
                {
                    Vector3 p = g.InverseTransformPoint(c);
                    if (p.x < pr.xMin - 1f || p.x > pr.xMax + 1f || p.y < pr.yMin - 1f || p.y > pr.yMax + 1f) { bad.Add(row.name + " " + p.y.ToString("F0") + " outside " + pr.yMin.ToString("F0") + ".." + pr.yMax.ToString("F0")); break; }
                }
            }
            return string.Join("; ", bad.ToArray());
        }

        IEnumerator WaitNoBlockReason(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end && QuitGuard.Reasons() != null) yield return null;
        }

        IEnumerator TestPauseRows()
        {
            var gm = Singleton<GUIManager>.Exists ? Singleton<GUIManager>.Instance : null;
            GUIMenu pause = gm == null ? null : CoopMenu.Find(gm, "PauseMenu");
            Transform group = pause == null ? null : pause.transform.Find("AspectKeeper/MenuGroupPanel");
            if (group == null || CoopMenu.Entry == null) { Check("PAUSE-ROWS", "the pause menu and its CO-OP line are there", false, "pause=" + (pause != null) + " co-op line=" + (CoopMenu.Entry != null)); yield break; }
            if (gm.AnyMenuActive) gm.CloseAllMenus();
            yield return StartCoroutine(WaitNoBlockReason(15f));
            yield return StartCoroutine(WaitRealUntil(() => QuitGuard.QuitBlock() == 0, 5f));

            var results = new List<string>(); bool allOk = true;
            foreach (bool withRun in new[] { false, true })
            {
                CoopMenu.SpeedrunPauseForTest = withRun;
                yield return null;
                gm.ActivateMenu("PauseMenu");
                yield return null; yield return null;
                string order = PauseOrder(group);
                string want = "ButtonContinue,ButtonDataBank,ButtonCamera," + CoopMenu.EntryName + (withRun ? "," + CoopMenu.SpeedrunPauseName : "") + ",ButtonControls,ButtonQuit";
                bool orderOk = order == want;
                bool usable = true;
                foreach (string n in want.Split(','))
                {
                    var t = group.Find(n); var bt = t == null ? null : t.GetComponent<Button>();
                    if (bt == null || !bt.gameObject.activeInHierarchy || !bt.interactable) usable = false;
                }
                string fit = PauseFit(group);
                var quit = group.Find("ButtonQuit"); var qb = quit == null ? null : quit.GetComponent<Button>();
                bool wired = qb != null && qb.onClick.GetPersistentEventCount() > 0 && qb.onClick.GetPersistentMethodName(0) == "OnButtonPress" && qb.onClick.GetPersistentTarget(0) == pause.GetComponent<UnityGUIMenu>();
                bool ok = orderOk && usable && fit.Length == 0 && wired && PlayerProfile.CanApplicationQuit;
                allOk &= ok;
                results.Add((withRun ? "in a run: " : "no run: ") + (ok ? "ok" : "rows " + order + " (want " + want + ") usable=" + usable + " fit " + (fit.Length == 0 ? "ok" : fit) + " quit wired=" + wired + " may quit=" + PlayerProfile.CanApplicationQuit) + " row height " + CoopMenu.PauseRowHeight);
                gm.CloseAllMenus();
                yield return null;
            }
            CoopMenu.SpeedrunPauseForTest = false;
            yield return null;
            bool noRunNoLine = !CoopMenu.SpeedrunLineWanted && (Speedrun.Current == null || !Speedrun.Current.Live || !Speedrun.SlotActive);
            Check("PAUSE-ROWS", "the pause menu has Resume, Data Bank, Settings, CO-OP, (SPEEDRUN only while a run is live, right under CO-OP), Controls and Save & Quit last, all usable and inside the panel, and Save & Quit goes to the game's own handler",
                allOk && noRunNoLine, string.Join(" | ", results.ToArray()) + "; SPEEDRUN without a live run=" + CoopMenu.SpeedrunLineWanted);

            yield return StartCoroutine(TestQuitGuard(gm, pause));
        }

        IEnumerator TestQuitGuard(GUIManager gm, GUIMenu pause)
        {
            var qbGo = pause.GetComponent<PauseMenuHandler>() != null ? pause.GetComponent<PauseMenuHandler>().m_QuitButton : null;
            yield return StartCoroutine(WaitNoBlockReason(15f));
            string reasonBefore = QuitGuard.Reasons();
            if (reasonBefore != null) Note("quit guard: something really blocks quitting right now (" + reasonBefore + "); the leak case may wait for it");

            // 1. the leak: the counter is raised with nothing going on (a routine cut off half way); the pause menu shows Save & Quit again
            int repairs = QuitGuard.Repairs;
            PlayerProfile.BlockApplicationQuit();
            gm.ActivateMenu("PauseMenu");
            yield return null;
            bool hiddenByGame = qbGo != null && !qbGo.activeSelf;
            // the test opens the menu without the game's own pause, so something else (the game's menu handling, focus) may close it again before the
            // guard's calm is over: then the repair is made outside the menu, which cannot show the button. That is noted and the menu opened again
            // (the button must show then), instead of being called a failed repair.
            int reopened = 0;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                yield return StartCoroutine(WaitRealUntil(() => (qbGo != null && qbGo.activeSelf && QuitGuard.QuitBlock() == 0) || !QuitGuard.PauseOpen, 3f));
                if (qbGo != null && qbGo.activeSelf && QuitGuard.QuitBlock() == 0) break;
                if (QuitGuard.PauseOpen) break;                    // the 3 s ran out with the menu open: a real failure
                reopened++;
                Note("quit guard test: the pause menu closed by itself (quit block " + QuitGuard.QuitBlock() + ", repairs +" + (QuitGuard.Repairs - repairs) + ", last repair with the menu open=" + QuitGuard.LastRepairOpen + "); opened again");
                gm.ActivateMenu("PauseMenu");
                yield return null;
            }
            bool repaired = qbGo != null && qbGo.activeSelf && QuitGuard.QuitBlock() == 0 && PlayerProfile.CanApplicationQuit && QuitGuard.Repairs > repairs;
            string leakWhy = "button " + (qbGo == null ? "missing" : qbGo.activeSelf ? "on" : "off") + ", quit block " + QuitGuard.QuitBlock() + ", can quit " + PlayerProfile.CanApplicationQuit + ", repairs +" + (QuitGuard.Repairs - repairs) + ", menu open " + QuitGuard.PauseOpen + ", menu closed early " + reopened + " times, last repair with the menu open=" + QuitGuard.LastRepairOpen + ", time scale " + Time.timeScale;
            bool logged = ReadShared(Here("coopmod-steam.log")).Contains("quit guard: nothing blocks quitting now");
            gm.CloseAllMenus(); yield return null;

            // 2. something really blocks: Save & Quit stays hidden and the counter is left alone
            QuitGuard.TestReasons = 1;
            PlayerProfile.BlockApplicationQuit();
            gm.ActivateMenu("PauseMenu");
            yield return StartCoroutine(WaitReal(1.5f));        // fixed on purpose: proves nothing is lowered
            bool kept = qbGo != null && !qbGo.activeSelf && QuitGuard.QuitBlock() == 1;
            QuitGuard.TestReasons = 0;
            PlayerProfile.UnblockApplicationQuit();
            gm.CloseAllMenus(); yield return null;

            // 3. outside the pause menu too (closing the window works again), and the pause block
            PlayerProfile.BlockApplicationQuit(); PlayerProfile.BlockPauseMenu();
            yield return StartCoroutine(WaitRealUntil(() => QuitGuard.QuitBlock() == 0 && QuitGuard.PauseBlock() == 0, 5f));
            bool outside = QuitGuard.QuitBlock() == 0 && QuitGuard.PauseBlock() == 0 && SceneProfile.PauseEnabled;

            // 4. the source: a plant part the mod removes while it grows gives its block back first (done in one frame on a real branch, then put back)
            string source = "no branch";
            PlantBranch trunk = null;
            foreach (PlantBranch br in UnityEngine.Object.FindObjectsOfType(typeof(PlantBranch))) if (br.IsTrunk() && !br.m_Growing) { trunk = br; break; }
            bool sourceOk = false;
            if (trunk != null)
            {
                int q0 = QuitGuard.QuitBlock();
                trunk.m_Growing = true; PlayerProfile.BlockApplicationQuit();                    // as the game does when the trunk starts growing
                int given = QuitGuard.ReleaseBranches(new[] { trunk });
                int after = QuitGuard.QuitBlock();
                trunk.m_Growing = false;
                PlayerProfile.BlockApplicationQuit(); int none = QuitGuard.ReleaseBranches(new[] { trunk }); PlayerProfile.UnblockApplicationQuit();   // not growing: nothing to give back
                sourceOk = given == 1 && after == q0 && none == 0 && QuitGuard.QuitBlock() == q0;
                source = "given back " + given + ", counter " + q0 + " -> " + after + ", not growing gives " + none;
            }
            // 5. a plant part being destroyed this frame (Unity's Destroy waits for the frame's end) no longer counts as blocking, gives its block back only
            //    once, and the guard keeps no reference to it: only ids, for this frame
            yield return null;
            string doomedText = "no branch"; bool doomedOk = false;
            if (trunk != null)
            {
                int q0 = QuitGuard.QuitBlock();
                trunk.m_Growing = true; PlayerProfile.BlockApplicationQuit();               // as the game does when the trunk starts growing
                int holdsBefore = QuitGuard.PlantHolds(); bool reasonBefore5 = QuitGuard.Reasons() != null;
                QuitGuard.Doom(trunk);                                                    // the mod (or the game) is destroying it this frame
                int holdsDoomed = QuitGuard.PlantHolds(); int doomedNow = QuitGuard.DoomedCount;
                int givenAgain = QuitGuard.ReleaseBranches(new[] { trunk });              // a doomed part never gives its block back a second time
                trunk.m_Growing = false; PlayerProfile.UnblockApplicationQuit();             // put back the way it was, in the same frame
                bool noRefs = GuardHoldsNoBranch();
                yield return null;                                                        // the next frame: the set is empty again and the part counts as usual
                bool cleared = QuitGuard.DoomedCount == 0 && !QuitGuard.Doomed(trunk);
                doomedOk = reasonBefore5 && holdsDoomed == holdsBefore - 1 && doomedNow >= 1 && givenAgain == 0 && noRefs && cleared && QuitGuard.QuitBlock() == q0;
                doomedText = "holds " + holdsBefore + " -> " + holdsDoomed + " while doomed, doomed ids " + doomedNow + ", given back again " + givenAgain + ", no branch reference kept=" + noRefs + ", next frame cleared=" + cleared + ", counter " + q0 + " -> " + QuitGuard.QuitBlock();
            }
            Check("QUIT-DOOMED", "a growing plant part being destroyed this frame stops counting as a real block at once, never gives its block back twice, and the guard keeps only its id for that frame (no reference to the part)",
                doomedOk, doomedText);

            Check("QUIT-GUARD", "a Save & Quit block left raised with nothing going on is lowered through the game's own calls (Save & Quit shows again in the pause menu, closing the window works, the pause block too) and logged; a real block (growth, respawn, cutscene, teleport) is never touched; a growing plant part the mod removes gives its block back first",
                hiddenByGame && repaired && logged && kept && outside && sourceOk,
                "leak: hidden by the game=" + hiddenByGame + " back=" + repaired + (repaired ? (reopened > 0 ? " (menu closed early " + reopened + "x, opened again)" : "") : " (" + leakWhy + ")") + " logged=" + logged + "; real block kept=" + kept + "; outside the menu=" + outside + "; removed part: " + source);
        }
    }
}
