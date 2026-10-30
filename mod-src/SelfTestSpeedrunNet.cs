using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CoopMod
{
    // The checks of races and co-op runs (SpeedrunNet.cs). Whole races are played inside this one game: several SrNodes, each with a fake set of
    // players (FakeSrLink) and a fake game (FakeSrGame), talk through a fake network that hands every message to the real MessageGate first.
    // Time and the clock are fake too, so countdowns, silence and the timer are exact and quick.
    public partial class SelfTest
    {
        // ---- the fake network, players and game -------------------------------------------------------------------------------------------

        class FakeSrNet
        {
            public readonly Dictionary<ulong, FakeSrLink> Links = new Dictionary<ulong, FakeSrLink>();
            public readonly List<KeyValuePair<ulong, KeyValuePair<ulong, byte[]>>> Queue = new List<KeyValuePair<ulong, KeyValuePair<ulong, byte[]>>>();
            public ulong HostId;
            public float Now; public long Ticks = 1000000;
            public int Sent, GateRefused;
            public readonly HashSet<ulong> Silent = new HashSet<ulong>();   // their messages are lost (gone quiet)

            // every message waiting, through the real gate, to its node
            public void Pump()
            {
                for (int round = 0; round < 10 && Queue.Count > 0; round++)
                {
                    var q = new List<KeyValuePair<ulong, KeyValuePair<ulong, byte[]>>>(Queue); Queue.Clear();
                    foreach (var item in q)
                    {
                        ulong from = item.Key, to = item.Value.Key; byte[] m = item.Value.Value;
                        FakeSrLink link; if (!Links.TryGetValue(to, out link) || link.Node == null) continue;
                        bool fromHost = from == HostId;
                        if (MessageGate.Classify(m, fromHost) != Route.Speedrun) { GateRefused++; continue; }
                        link.Node.Handle(from, m, fromHost);
                    }
                }
            }

            public void Advance(float seconds) { Now += seconds; Ticks += SpeedTimer.FromMs((long)Math.Round(seconds * 1000.0)); }
        }

        class FakeSrLink : ISrLink
        {
            public FakeSrNet Net; public ulong Id; public string Name; public SrNode Node;
            public readonly List<ulong> Party = new List<ulong>();
            public readonly HashSet<ulong> InMyWorld = new HashSet<ulong>();
            public string RaceKey = "";
            public float Rtt;
            public ulong SrMyId { get { return Id; } }
            public string SrMyName { get { return Name; } }
            public bool SrIsPartyHost { get { return Net.HostId == Id; } }
            public ulong SrPartyHostId { get { return Net.HostId; } }
            public List<ulong> SrOthers() { var l = new List<ulong>(); foreach (var id in Party) if (id != Id) l.Add(id); return l; }
            public void SrSend(ulong to, byte[] m) { Net.Sent++; if (!Net.Silent.Contains(Id)) Net.Queue.Add(new KeyValuePair<ulong, KeyValuePair<ulong, byte[]>>(Id, new KeyValuePair<ulong, byte[]>(to, m))); }
            public float SrRttMs(ulong id) { return Rtt; }
            public string SrNameOf(ulong id) { FakeSrLink l; return Net.Links.TryGetValue(id, out l) ? l.Name : "PLAYER"; }
            public void SrSetRaceKey(string v) { RaceKey = v ?? ""; }
            public bool SrInMyWorld(ulong id) { return InMyWorld.Contains(id); }
        }

        class FakeSrGame : ISrGame
        {
            public FakeSrNet Net; public SrNode Node;
            public bool Idle = true, World;
            public string EnterRefusal;
            public SrCategory Cat; public SrMode Mode; public uint Forced;
            public bool SlotHeld, SlotEntered; public int Releases, Begins;
            public float BeganAt = -1f;
            public SrRun TheRun;
            public readonly List<string> Toasts = new List<string>();
            public readonly List<int> CoopEvents = new List<int>();

            public float Now { get { return Net.Now; } }
            public long Clock() { return Net.Ticks; }
            public bool StartIdle { get { return Idle && !SlotEntered && (TheRun == null || !TheRun.Live); } }
            public bool InWorld { get { return World; } }
            public int SeedTotal { get { return 8; } }
            public SrRun Run { get { return TheRun; } }
            public string EnterSlot(SrCategory c, SrMode m, uint runId) { if (EnterRefusal != null) return EnterRefusal; Cat = c; Mode = m; Forced = runId; SlotEntered = true; SlotHeld = true; return null; }
            public void ReleaseSlot() { SlotHeld = false; Releases++; }
            public string BeginRefusal;          // the start keeps failing (a fade, a menu, cloud saving ...)
            public bool BeginNewGame(out string why)
            {
                why = null;
                if (!SlotEntered) { why = "NO SLOT"; return false; }
                if (BeginRefusal != null) { why = BeginRefusal; return false; }
                Begins++; BeganAt = Net.Now; Idle = false; World = true;
                TheRun = new SrRun(Cat, Mode, 8, Clock) { RunId = Forced };
                TheRun.Timer.StartAt(Net.Ticks);
                Node.OnRunChanged(TheRun, "start", -1);
                return true;
            }
            public string StartCoopLeader(SrCategory c, uint runId)
            {
                Cat = c; Mode = SrMode.Coop; Forced = runId; SlotEntered = true;
                string why; BeginNewGame(out why);
                return why;
            }
            public void CoopEvent(byte kind, int arg, SrCategory c, int seeds) { CoopEvents.Add(kind * 1000 + arg); }
            public void EndRun(SrState state, string why) { if (TheRun != null && TheRun.Live) { TheRun.Timer.StopAt(Net.Ticks); TheRun.State = state; Node.OnRunChanged(TheRun, "end", -1); } }
            public void Toast(string m) { Toasts.Add(m); }

            // something happens in my run (the live game's Speedrun.Feed does the same)
            public void Happen(SrEvent e)
            {
                if (TheRun == null || !TheRun.Live) return;
                e.Ticks = Net.Ticks;
                var ch = new List<int>();
                TheRun.Feed(e, ch);
                foreach (int k in ch) Node.OnRunChanged(TheRun, TheRun.Engine.States[k] == SrSplitState.Skipped ? "skip" : "split", k);
                if (!TheRun.Live) Node.OnRunChanged(TheRun, "end", -1);
            }
        }

        static FakeSrNet SrFakeParty(ulong host, params ulong[] ids)
        {
            var net = new FakeSrNet { HostId = host };
            var all = new List<ulong>(ids);
            foreach (var id in all)
            {
                var link = new FakeSrLink { Net = net, Id = id, Name = "P" + id };
                link.Party.AddRange(all);
                var game = new FakeSrGame { Net = net };
                link.Node = new SrNode(link, game);
                game.Node = link.Node;
                link.Node.Race.ReadyTimeout = 2f; link.Node.Race.OfflineAfter = 3f; link.Node.Race.DnfAfter = 6f;
                net.Links[id] = link;
            }
            return net;
        }

        static FakeSrGame G(FakeSrNet net, ulong id) { return (FakeSrGame)net.Links[id].Node.Game; }
        static SrNode N(FakeSrNet net, ulong id) { return net.Links[id].Node; }

        // runs the nodes for a while in steps of one frame
        static void SrRunFor(FakeSrNet net, float seconds, float frame, params ulong[] skip)
        {
            var off = new HashSet<ulong>(skip);
            for (float t = 0f; t < seconds - 1e-4f; t += frame)
            {
                net.Advance(frame);
                foreach (var l in net.Links.Values) if (l.Node != null && !off.Contains(l.Id)) l.Node.Tick();
                net.Pump();
            }
        }

        void CheckSpeedrunNetPure()
        {
            if (On("speedrun,net")) Try("SR-GATE", "speedrun message checks ran", CheckSrGate);
            if (On("speedrun,party")) Try("SR-RACE-FSM", "race state checks ran", CheckSrRaceFsm);
            if (On("speedrun,party")) Try("SR-RACE-LOOP", "whole race checks ran", CheckSrRaceLoop);
            if (On("speedrun,party")) Try("SR-COOP-TIMER", "co-op run checks ran", CheckSrCoopTimer);
        }

        // ---- the gate and the messages ----------------------------------------------------------------------------------------------------

        void CheckSrGate()
        {
            var good = new List<byte[]>
            {
                SrMsg.ArmMsg(77, SrMsg.ModeRace, SrCategory.AllSeeds, 8), SrMsg.ReadyMsg(77, SrMsg.ReadyOk), SrMsg.GoMsg(77, 4950), SrMsg.CancelMsg(77, SrMsg.CancelByHost),
                SrMsg.ProgressMsg(77, 61234, 3, 6, SrState.Running, true, 60000), SrMsg.EventMsg(77, SrMsg.KindDataBank, 19),
            };
            var bad = new List<string>();
            foreach (var m in good)
            {
                bool hostOnly = SrMsg.HostOnly(m[0]);
                if (m.Length != SrMsg.Length(m[0])) bad.Add(m[0] + " has " + m.Length + " bytes");
                if (MessageGate.Classify(m, true) != Route.Speedrun) bad.Add(m[0] + " from the host not routed");
                if (MessageGate.Classify(m, false) != (hostOnly ? Route.Ignore : Route.Speedrun)) bad.Add(m[0] + " from a member: " + MessageGate.Classify(m, false));
                var longer = new byte[m.Length + 1]; Buffer.BlockCopy(m, 0, longer, 0, m.Length);
                var shorter = new byte[m.Length - 1]; Buffer.BlockCopy(m, 0, shorter, 0, shorter.Length);
                if (MessageGate.Classify(longer, true) != Route.Ignore || MessageGate.Classify(shorter, true) != Route.Ignore) bad.Add(m[0] + " at a wrong size is not ignored");
                SrNetMsg p; if (!SrMsg.TryParse(m, out p)) bad.Add(m[0] + " does not read back");
            }
            SrNetMsg r;
            bool round = SrMsg.TryParse(good[4], out r) && r.ElapsedMs == 61234 && r.SplitIdx == 3 && r.SplitCount == 6 && r.Valid && r.LastSplitMs == 60000 && r.RunId == 77
                && SrMsg.TryParse(good[0], out r) && r.Cat == 3 && r.Mode == SrMsg.ModeRace && r.Ver == SrMsg.Version && r.Flags == 8
                && SrMsg.TryParse(good[2], out r) && r.MsToGo == 4950;
            SrCheck("SR-GATE", "speedrun messages (20-25) are routed only at their exact size; ARM, GO and CANCEL only from the leader; every field reads back", bad.Count == 0 && round,
                bad.Count == 0 ? good.Count + " types ok, read back=" + round : string.Join(" | ", bad.ToArray()));

            // out of range: each one refused by the reader
            var hostile = new List<byte[]>();
            var a = SrMsg.ArmMsg(5, SrMsg.ModeRace, SrCategory.Any, 8); a[6] = 9; hostile.Add(a);                         // no such mode
            a = SrMsg.ArmMsg(5, SrMsg.ModeRace, SrCategory.Any, 8); a[7] = 4; hostile.Add(a);                              // no such category
            a = SrMsg.ArmMsg(5, SrMsg.ModeRace, SrCategory.Any, 8); a[8] = 0; hostile.Add(a);                              // no seeds
            a = SrMsg.GoMsg(5, 100); Buffer.BlockCopy(BitConverter.GetBytes(10001), 0, a, 5, 4); hostile.Add(a);          // too far away
            a = SrMsg.GoMsg(5, 100); Buffer.BlockCopy(BitConverter.GetBytes(-1), 0, a, 5, 4); hostile.Add(a);
            a = SrMsg.ReadyMsg(5, 1); a[5] = 3; hostile.Add(a);
            a = SrMsg.ProgressMsg(5, 10, 1, 6, SrState.Running, true, 5); Buffer.BlockCopy(BitConverter.GetBytes(SrMsg.MaxElapsedMs + 1), 0, a, 5, 4); hostile.Add(a);
            a = SrMsg.ProgressMsg(5, 10, 1, 6, SrState.Running, true, 5); a[10] = 17; hostile.Add(a);                      // more than 16 splits
            a = SrMsg.ProgressMsg(5, 10, 1, 6, SrState.Running, true, 5); a[9] = 6; hostile.Add(a);                        // split past the count
            a = SrMsg.ProgressMsg(5, 10, 1, 6, SrState.Running, true, 5); a[11] = 6; hostile.Add(a);                       // no such state
            a = SrMsg.ProgressMsg(5, 10, 1, 6, SrState.Running, true, 5); a[12] = 2; hostile.Add(a);
            a = SrMsg.ProgressMsg(5, 10, 1, 6, SrState.Running, true, 5); Buffer.BlockCopy(BitConverter.GetBytes(-2), 0, a, 13, 4); hostile.Add(a);
            a = SrMsg.EventMsg(5, SrMsg.KindDataBank, 1); a[6] = 20; hostile.Add(a);                                       // databank entry 20
            a = SrMsg.EventMsg(5, SrMsg.KindSplit, 1); a[6] = 16; hostile.Add(a);
            a = SrMsg.EventMsg(5, SrMsg.KindSplit, 1); a[5] = 6; hostile.Add(a);                                           // no such kind
            a = SrMsg.CancelMsg(5, 0); a[1] = 0; a[2] = 0; a[3] = 0; a[4] = 0; hostile.Add(a);                             // run 0
            int refusedCount = 0;
            foreach (var m in hostile) { SrNetMsg x; if (!SrMsg.TryParse(m, out x)) refusedCount++; }
            // the node too: a stale run number, a GO from somebody who is not the leader
            var net = SrFakeParty(1, 1, 2);
            var member = N(net, 2);
            int before = member.Refused;
            member.Handle(1, SrMsg.GoMsg(999, 100), true);                  // no race armed
            member.Handle(3, SrMsg.ArmMsg(9, SrMsg.ModeRace, SrCategory.Any, 8), false);   // not the leader
            bool nodeRefuses = member.Refused - before == 2 && member.Race.Ph == RaceFsm.Phase.Idle && !G(net, 2).SlotEntered;
            SrCheck("SR-GATE", "out-of-range fields are refused (mode, category, seeds, GO time, state, split count/index, times, databank, kind, run 0), a stale run number and a non-leader ARM change nothing",
                refusedCount == hostile.Count && nodeRefuses, refusedCount + "/" + hostile.Count + " refused, node refused=" + nodeRefuses);

            // a flood of PROGRESS: 5 a second per player
            var lim = new SrRateLimit(5, 1f);
            int ok = 0; for (int i = 0; i < 50; i++) if (lim.Allow(42, 10f + i * 0.001f)) ok++;
            bool other = lim.Allow(43, 10.01f);
            bool later = lim.Allow(42, 11.2f);
            // on a node: repeated heartbeats are cut, but a finish that comes in the same second always gets through
            var flood = SrFakeParty(1, 1, 2);
            var fn = N(flood, 2);
            for (int i = 0; i < 8; i++) fn.Handle(1, SrMsg.ProgressMsg(88, 1000 + i, 2, 6, SrState.Running, true, 900), false);
            int cut = fn.Limit.Dropped;
            fn.Handle(1, SrMsg.ProgressMsg(88, 1100, 5, 6, SrState.Finished, true, 1100), false);
            var fr = fn.Race.Find(1);
            bool finishIn = cut == 2 && fn.Limit.Dropped == 2 && fn.NewsLimit.Dropped == 0 && fr != null && fr.State == SrState.Finished;
            SrCheck("SR-GATE", "a PROGRESS flood is cut to 5 a second per player (another player and the next second are not affected); a new split or a finish is never cut with the heartbeats",
                ok == 5 && other && later && finishIn, ok + " of 50 let through, other=" + other + " next second=" + later + "; node cut " + cut + " repeats, finish taken=" + finishIn);

            // an older mod gets these messages as world messages: its world code must ignore them (nothing sent back, no guest mode, no error)
            var cap = new CaptureTransport();
            bool guest = WorldSync.GuestMode; string status = WorldSync.Status;
            bool threw = false;
            try { foreach (var m in good) WorldSync.Handle(cap, m); } catch (Exception) { threw = true; }
            WorldSync.Forget(cap);
            bool old = !threw && cap.Sent.Count == 0 && WorldSync.GuestMode == guest && WorldSync.Status == status;
            bool key = SrMsg.RaceBlocksFollow(SrMsg.RaceKey(5, SrMode.Race, SrCategory.Any)) && !SrMsg.RaceBlocksFollow(SrMsg.RaceKey(5, SrMode.Coop, SrCategory.Any)) && !SrMsg.RaceBlocksFollow("");
            SrCheck("SR-GATE", "backward safe: a v13/v14 mod hands the new types to its world code, which ignores them; a race in the party data stops members following, a co-op run does not",
                old && key, "ignored by old path=" + old + " (sent " + cap.Sent.Count + ", threw " + threw + ") party key=" + key);
        }

        // ---- the race state (pure) --------------------------------------------------------------------------------------------------------

        void CheckSrRaceFsm()
        {
            var f = new RaceFsm { ReadyTimeout = 15f, OfflineAfter = 30f, DnfAfter = 120f };
            f.ArmAsHost(10, SrCategory.Any, 8, 1, "ME", new List<ulong> { 2, 3 }, 0f);
            bool notYet = !f.GoDue(1f);
            f.OnReady(2, 10, SrMsg.ReadyOk, 1f);
            bool stale = !f.OnReady(3, 11, SrMsg.ReadyOk, 1f);
            f.OnReady(3, 10, SrMsg.ReadyOk, 2f);
            bool allReady = f.AllReady && f.GoDue(2f);
            var going = f.StartCountdown(2f);
            bool counting = f.Ph == RaceFsm.Phase.Counting && going.Count == 2 && Math.Abs(f.GoAt - 7f) < 0.001f;
            SrCheck("SR-RACE-FSM", "arm, everybody ready, GO: the countdown starts at once for all racers (a READY for another race is ignored)", notYet && stale && allReady && counting,
                "waits=" + notYet + " stale ignored=" + stale + " all ready=" + allReady + " counting=" + counting);

            var g = new RaceFsm { ReadyTimeout = 15f };
            g.ArmAsHost(20, SrCategory.Any, 8, 1, "ME", new List<ulong> { 2, 3, 4 }, 0f);
            g.OnReady(2, 20, SrMsg.ReadyOk, 1f); g.OnReady(3, 20, SrMsg.ReadyVersion, 1f);
            bool waits = !g.GoDue(14.9f), due = g.GoDue(15f);
            var go2 = g.StartCountdown(15f);
            bool leftOut = go2.Count == 1 && go2[0].Id == 2 && !g.Find(3).InRace && !g.Find(4).InRace && g.Find(1).InRace;
            SrCheck("SR-RACE-FSM", "a racer not ready by the 15 s timeout (or on another mod version) is left out; the ready ones race", waits && due && leftOut, "waits=" + waits + " due=" + due + " left out=" + leftOut);

            var m = new RaceFsm();
            m.ArmAsMember(30, SrCategory.Hundred, 8, 1, 2, "ME", 0f);
            bool goStale = !m.OnGo(31, 5000, 0.5f);
            bool go = m.OnGo(30, 4900, 1f) && Math.Abs(m.GoAt - 5.9f) < 0.001f && m.Mine.InRace;
            bool cancel = m.OnCancel(30, 2f) && !m.Active;
            SrCheck("SR-RACE-FSM", "a member's GO time is the leader's GO minus half the round trip; a GO or PROGRESS for another race is ignored; CANCEL ends it", goStale && go && cancel, "stale GO ignored=" + goStale + " go=" + go + " cancel=" + cancel);

            var h = new RaceFsm { OfflineAfter = 30f, DnfAfter = 120f };
            h.ArmAsHost(40, SrCategory.Any, 8, 1, "ME", new List<ulong> { 2, 3, 4 }, 0f);
            foreach (ulong id in new ulong[] { 2, 3, 4 }) h.OnReady(id, 40, SrMsg.ReadyOk, 0f);
            h.StartCountdown(0f);
            SrNetMsg p;
            SrMsg.TryParse(SrMsg.ProgressMsg(40, 5000, 1, 6, SrState.Running, true, 4000), out p); h.OnProgress(2, p, 6f);
            SrMsg.TryParse(SrMsg.ProgressMsg(41, 5000, 1, 6, SrState.Running, true, 4000), out p); bool staleProg = !h.OnProgress(3, p, 6f);
            SrMsg.TryParse(SrMsg.ProgressMsg(40, 6000, 0, 6, SrState.Running, true, 6000), out p); h.OnProgress(3, p, 6f);
            SrMsg.TryParse(SrMsg.ProgressMsg(40, 9000, 0, 6, SrState.Running, true, 6000), out p); h.OnProgress(4, p, 9f);
            h.Tick(37f);
            bool off = h.Find(2).Offline && h.Find(2).Note == "OFFLINE" && h.Find(2).Running;
            h.Tick(127f);
            bool dnf = h.Find(2).State == SrState.Dnf && h.Find(3).State == SrState.Dnf;
            SrCheck("SR-RACE-FSM", "a racer silent for 30 s shows OFFLINE, after 120 s DNF; PROGRESS for another race is ignored", off && dnf && staleProg, "offline=" + off + " dnf=" + dnf + " stale ignored=" + staleProg);

            var k = new RaceFsm();
            k.ArmAsHost(50, SrCategory.Any, 8, 1, "ME", new List<ulong> { 2, 3, 4, 5 }, 0f);
            foreach (ulong id in new ulong[] { 2, 3, 4, 5 }) k.OnReady(id, 50, SrMsg.ReadyOk, 0f);
            k.StartCountdown(0f);
            Action<ulong, int, int, SrState> say = (id, ms, idx, st) => { SrNetMsg q; SrMsg.TryParse(SrMsg.ProgressMsg(50, ms, idx, 6, st, true, ms), out q); k.OnProgress(id, q, 10f); };
            say(1, 9000, 5, SrState.Finished); say(2, 9000, 5, SrState.Finished); say(3, 8000, 5, SrState.Finished); say(4, 3000, 2, SrState.Reset); say(5, 4000, 3, SrState.Running);
            var ranked = k.Ranked(); var places = RaceFsm.Places(ranked);
            string order = ""; for (int i = 0; i < ranked.Count; i++) order += places[i] + ":" + ranked[i].Id + " ";
            bool notDone = !k.AllDone;
            say(5, 4500, 3, SrState.Dnf);
            bool done = k.AllDone;
            SrCheck("SR-RACE-FSM", "results: finished racers by time (a tie shares the place), then those still going, then reset / DNF; the race is over once everybody is done",
                order.Trim() == "1:3 2:1 2:2 4:5 5:4" && notDone && done, "order " + order + "over early=" + !notDone + " over=" + done);
        }

        // ---- a whole race with fake players --------------------------------------------------------------------------------------------

        void CheckSrRaceLoop()
        {
            const float frame = 1f / 60f;
            var net = SrFakeParty(1, 1, 2, 3, 4, 5);
            // 2 and 3 race, 4 is on an older mod (no node: never answers), 5 is in a game and cannot race
            net.Links[4].Node = null;
            G(net, 5).Idle = false;
            net.Links[1].Rtt = 120f;
            var host = N(net, 1);
            string why = host.HostStartRace(SrCategory.Any);
            net.Pump();
            bool armed = why == null && host.Race.Ph == RaceFsm.Phase.Armed && net.Links[1].RaceKey.Contains("|race|any") && G(net, 1).SlotHeld
                && G(net, 2).SlotEntered && G(net, 3).SlotEntered && !G(net, 5).SlotEntered && host.Race.ReadyCount == 2 && host.Race.Find(5).ReadyState == SrMsg.ReadyRefused;
            SrRunFor(net, 1f, frame);
            bool stillWaiting = host.Race.Ph == RaceFsm.Phase.Armed;      // 4 never answered: the leader waits for the timeout
            SrRunFor(net, 1.2f, frame);
            bool counting = host.Race.Ph == RaceFsm.Phase.Counting && N(net, 2).Race.Ph == RaceFsm.Phase.Counting && N(net, 3).Race.Ph == RaceFsm.Phase.Counting
                && !host.Race.Find(4).InRace && !host.Race.Find(5).InRace && G(net, 5).Releases == 0 && N(net, 5).Race.Ph == RaceFsm.Phase.Idle;
            float hostGo = host.Race.GoAt, memberGo = N(net, 2).Race.GoAt;
            SrCheck("SR-RACE-LOOP", "START RACE arms the party: racers get their own slot and answer READY, a busy player refuses, an older mod never answers and is left out after the ready time",
                armed && stillWaiting && counting, "armed=" + armed + " waited for the old mod=" + stillWaiting + " counting=" + counting + " (" + host.Race.ReadyCount + "/" + host.Race.Total + " ready, refusal: " + N(net, 5).LastRefusal + ")");

            SrRunFor(net, 5.2f, frame);
            bool allBegan = G(net, 1).Begins == 1 && G(net, 2).Begins == 1 && G(net, 3).Begins == 1 && G(net, 5).Begins == 0;
            float e1 = G(net, 1).BeganAt - hostGo, e2 = G(net, 2).BeganAt - memberGo;
            bool onFrame = e1 >= 0f && e1 <= frame + 1e-3f && e2 >= 0f && e2 <= frame + 1e-3f && Math.Abs(memberGo - (hostGo - 0.06f)) < 0.02f;
            bool released = !G(net, 1).SlotHeld && !G(net, 2).SlotHeld && G(net, 1).TheRun.RunId == host.Race.RunId && G(net, 2).TheRun.RunId == host.Race.RunId && G(net, 2).TheRun.Mode == SrMode.Race;
            SrCheck("SR-RACE-LOOP", "every racer starts the game's NEW GAME on the frame its GO time is reached (the member's GO is half a round trip earlier), under the race's run number",
                allBegan && onFrame && released, "began=" + allBegan + " leader " + (e1 * 1000f).ToString("F1") + " ms after GO, member " + (e2 * 1000f).ToString("F1") + " ms, member GO " + ((hostGo - memberGo) * 1000f).ToString("F0") + " ms earlier; slot let go at the start=" + released);

            // racer 2 splits, the leader finishes, racer 3 goes quiet
            G(net, 2).Happen(new SrEvent { Kind = SrEvKind.Accomplishment, Name = "TrunkReachedIsland2" });
            SrRunFor(net, 0.5f, frame);
            var seen = host.Race.Find(2);
            bool strip = seen.Started && seen.SplitIdx == 0 && seen.Done == 1 && seen.LastSplitMs >= 0 && SpeedrunUI.RacerLine(host.Race, seen, 1, net.Now).Contains("ISLAND 2");
            net.Silent.Add(3);
            G(net, 1).Happen(new SrEvent { Kind = SrEvKind.Accomplishment, Name = "TeleportedSeed" });
            G(net, 1).Happen(new SrEvent { Kind = SrEvKind.LeverEnd });
            SrRunFor(net, 0.5f, frame);
            bool hostFin = host.Race.Mine.State == SrState.Finished && N(net, 2).Race.Find(1).State == SrState.Finished;
            SrRunFor(net, 3.2f, frame, 3);
            bool offline = host.Race.Find(3).Offline && host.Race.Find(3).Running;
            G(net, 2).Happen(new SrEvent { Kind = SrEvKind.LeverEnd });
            SrRunFor(net, 3.5f, frame, 3);
            bool dnf = host.Race.Find(3).State == SrState.Dnf;
            bool over = host.Race.Ph == RaceFsm.Phase.Over && N(net, 2).Race.Ph == RaceFsm.Phase.Over && net.Links[1].RaceKey == "" && host.ShowResults(net.Now)
                && !N(net, 5).Race.Active;          // 5 (busy, not racing) only watched the race: the race's end lets it go too
            var ranked = host.Race.Ranked();
            string order = ""; foreach (var r in ranked) order += r.Id + ":" + r.State + " ";
            bool rankOk = ranked.Count == 3 && ranked[0].Id == 1 && ranked[1].Id == 2 && ranked[2].Id == 3 && ranked[0].ElapsedMs <= ranked[1].ElapsedMs;
            SrCheck("SR-RACE-LOOP", "PROGRESS fills the race strip live; a silent racer goes OFFLINE then DNF; once everybody is done the race is over, the party key is cleared and the results are ranked",
                strip && hostFin && offline && dnf && over && rankOk, "strip=" + strip + " leader finished=" + hostFin + " offline=" + offline + " dnf=" + dnf + " over=" + over + " results " + order + "(" + net.GateRefused + " messages stopped at the gate)");

            // the leader cancels an armed race: everybody lets go of their slot
            var net2 = SrFakeParty(1, 1, 2);
            N(net2, 1).HostStartRace(SrCategory.AnyNg); net2.Pump();
            N(net2, 1).HostCancel(SrMsg.CancelByHost); net2.Pump();
            bool cancelled = N(net2, 2).Race.Ph == RaceFsm.Phase.Idle && G(net2, 2).Releases >= 1 && !G(net2, 2).SlotHeld && !G(net2, 1).SlotHeld && net2.Links[1].RaceKey == "" && G(net2, 2).Begins == 0;
            var net3 = SrFakeParty(1, 1, 2);
            N(net3, 2).Handle(1, new byte[] { SrMsg.Arm, 99, 5, 0, 0, 0, SrMsg.ModeRace, 0, 8 }, true); net3.Pump();
            bool version = !G(net3, 2).SlotEntered && N(net3, 2).LastRefusal == "another version";
            SrCheck("SR-RACE-LOOP", "CANCEL RACE lets every racer go back to their own save; an ARM from another mod version is answered with a version mismatch and no slot", cancelled && version, "cancelled=" + cancelled + " version=" + version);

            // a racer whose start keeps failing at GO gets its own save back at once; a racer who leaves after the countdown is out at once on the leader
            var net4 = SrFakeParty(1, 1, 2, 3);
            G(net4, 2).BeginRefusal = "WAIT FOR THE SCREEN TO SETTLE";
            N(net4, 1).HostStartRace(SrCategory.Any); net4.Pump();
            SrRunFor(net4, 0.5f, frame);                     // all ready: the countdown is on
            N(net4, 3).LeaveRace(); net4.Pump();
            bool leftOut = N(net4, 1).Race.Find(3).State == SrState.Dnf && N(net4, 1).Race.Find(3).Note == "LEFT" && !G(net4, 3).SlotHeld;
            SrRunFor(net4, 9f, frame);                       // GO, then 3 s of failed starts
            var two = N(net4, 1).Race.Find(2);
            bool noStart = G(net4, 2).Begins == 0 && !G(net4, 2).SlotHeld && N(net4, 2).Race.Ph == RaceFsm.Phase.Over && two.State == SrState.Dnf && G(net4, 1).Begins == 1;
            SrCheck("SR-RACE-LOOP", "a racer whose start fails for 3 s after GO is DNF and lets go of the speedrun save at once; a racer who leaves after the countdown began is out on the leader at once (no 120 s wait)",
                leftOut && noStart, "left out at once=" + leftOut + " failed start released=" + noStart + " (begins " + G(net4, 2).Begins + ", held " + G(net4, 2).SlotHeld + ", phase " + N(net4, 2).Race.Ph + ", leader sees " + two.State + ")");
        }

        // ---- a co-op run with fake players ---------------------------------------------------------------------------------------------

        void CheckSrCoopTimer()
        {
            const float frame = 1f / 60f;
            var net = SrFakeParty(1, 1, 2);
            var host = N(net, 1); var member = N(net, 2);
            string why = host.HostStartCoop(SrCategory.Hundred);
            net.Pump();
            bool started = why == null && host.CoopHost && member.CoopMember && member.CoopCat == SrCategory.Hundred && !G(net, 2).SlotEntered && net.Links[1].RaceKey.Contains("|coop|");
            SrRunFor(net, 3.3f, frame);
            net.Advance(0.7f);                 // 0.7 s after the leader's last message
            var hostRun = G(net, 1).TheRun;
            long truth = hostRun.ElapsedMs, shown = member.Mirror != null ? member.Mirror.ElapsedMs : -1;
            bool close = member.Mirror != null && Math.Abs(truth - shown) <= 50;
            SrCheck("SR-COOP-TIMER", "a co-op run: the leader's run is the only timer; members (no slot of their own) show it from its PROGRESS, extrapolated to within 50 ms",
                started && close, "started=" + started + " leader " + truth + " ms, member shows " + shown + " ms");

            // the member reports a split from its own copy of the world
            G(net, 2).World = true;
            member.OnLocalEvent(new SrEvent { Kind = SrEvKind.Accomplishment, Name = "TeleportedSeed" });
            net.Pump();
            bool refusedNotInWorld = G(net, 1).CoopEvents.Count == 0 && host.EventsRefused == 1;
            net.Links[1].InMyWorld.Add(2);
            member.OnLocalEvent(new SrEvent { Kind = SrEvKind.DataBank, Arg = 7 });
            member.OnLocalEvent(new SrEvent { Kind = SrEvKind.DataBank, Arg = 7 });      // the same entry again: sent once
            net.Pump();
            bool taken = G(net, 1).CoopEvents.Count == 1 && G(net, 1).CoopEvents[0] == SrMsg.KindDataBank * 1000 + 7;
            // an EVENT outside a co-op run (a race leader) is refused
            var race = SrFakeParty(1, 1, 2);
            N(race, 1).HostStartRace(SrCategory.Any); race.Pump();
            N(race, 1).Handle(2, SrMsg.EventMsg(N(race, 1).Race.RunId, SrMsg.KindSplit, 0), false);
            bool outside = N(race, 1).EventsRefused == 1 && G(race, 1).CoopEvents.Count == 0;
            SrCheck("SR-COOP-TIMER", "members report splits / databank entries to the leader; the leader takes them only from a member in its world, each once, and never outside a co-op run",
                refusedNotInWorld && taken && outside, "refused (not in world)=" + refusedNotInWorld + " taken once=" + taken + " (" + G(net, 1).CoopEvents.Count + ") refused outside co-op=" + outside);

            member.HostLostAfter = 2f;
            SrRunFor(net, 2.5f, frame, 1);
            bool lost = member.HostLost && member.Mirror.Timer.Stopped;
            G(net, 1).Happen(new SrEvent { Kind = SrEvKind.Seed, HasState = true, Crystals = 100f, Seeds = 8, DataBank = 20 });
            SrRunFor(net, 1.5f, frame);
            bool finished = hostRun.State == SrState.Finished && member.Mirror.State == SrState.Finished && member.Mirror.Engine.EndMs == hostRun.Engine.EndMs && !member.HostLost;
            SrCheck("SR-COOP-TIMER", "the leader gone quiet: members freeze the time and show HOST LOST; the leader's finish reaches members with the exact final time",
                lost && finished, "host lost=" + lost + " finished=" + finished + " (" + hostRun.Engine.EndMs + " / " + member.Mirror.Engine.EndMs + ")");

            // CANNOT FINISH keeps timing on members; END RUN (DNF) freezes their copy for good, even with the leader's heartbeats after the end
            var net6 = SrFakeParty(1, 1, 2);
            N(net6, 1).HostStartCoop(SrCategory.Hundred); net6.Pump();
            SrRunFor(net6, 2f, frame);
            G(net6, 1).TheRun.State = SrState.CannotFinish;
            SrRunFor(net6, 2.5f, frame);
            var mir = N(net6, 2).Mirror;
            long hostNow = G(net6, 1).TheRun.ElapsedMs;
            bool cannot = mir != null && mir.State == SrState.CannotFinish && !mir.Timer.Stopped && Math.Abs(mir.ElapsedMs - hostNow) <= 50;
            G(net6, 1).TheRun.State = SrState.Running;
            G(net6, 1).EndRun(SrState.Dnf, "test");
            long final = G(net6, 1).TheRun.FinalMs;
            SrRunFor(net6, 3f, frame);
            long a6 = N(net6, 2).Mirror.FinalMs;
            SrRunFor(net6, 3f, frame);
            long b6 = N(net6, 2).Mirror.FinalMs;
            bool frozen = N(net6, 2).Mirror.State == SrState.Dnf && N(net6, 2).Mirror.Timer.Stopped && a6 == final && b6 == final;
            // the leader's message is half a round trip old when it arrives: members count that in
            var net7 = SrFakeParty(1, 1, 2);
            net7.Links[2].Rtt = 80f;
            N(net7, 1).HostStartCoop(SrCategory.Any); net7.Pump();
            net7.Advance(1f); N(net7, 1).Tick(); net7.Advance(0.04f); net7.Pump();
            long lag = N(net7, 2).Mirror == null ? -1 : G(net7, 1).TheRun.ElapsedMs - N(net7, 2).Mirror.ElapsedMs;
            bool lagOk = N(net7, 2).Mirror != null && Math.Abs(lag) <= 2;
            SrCheck("SR-COOP-TIMER", "members' copy of the leader's timer keeps running through CANNOT FINISH, stays frozen on the final time after END RUN (DNF) and its heartbeats, and allows for the one-way delay (80 ms ping)",
                cannot && frozen && lagOk, "cannot finish running=" + cannot + " (" + (mir == null ? -1 : mir.ElapsedMs) + "/" + hostNow + ") frozen=" + frozen + " (" + final + ": " + a6 + ", " + b6 + ") delay error " + lag + " ms");
        }

        // ---- live: a racer's world takes nothing from anybody ------------------------------------------------------------------------------

        void CheckSrRaceIsolationLive(CoopBehaviour cb)
        {
            var cap = new CaptureTransport();
            var bot = new Peer { Id = 76561199000000777UL, IsBot = true, BotIndex = 9, Link = cap, Number = 9 };
            bool iso = Speedrun.Isolated;
            float crystals = SceneProfile.CrystalsCollected;
            int applied = SharedWorld.StatApplied, rejected = SharedWorld.StatRejected, pickups = SharedWorld.StatPickupsApplied;
            int leaderEpoch = WorldSync.LeaderEpoch;
            string guid = null;
            foreach (SaveInfo si in UnityEngine.Object.FindObjectsOfType(typeof(SaveInfo))) if (!string.IsNullOrEmpty(si.m_uniqueID) && si.GetComponent<JointShard>() != null) { guid = si.m_uniqueID; break; }
            var msgs = new List<byte[]>();
            var ms = new MemoryStream(); var w = new BinaryWriter(ms); w.Write((byte)5); w.Write(guid ?? "nothing"); msgs.Add(ms.ToArray());      // a crystal collected
            msgs.Add(new byte[] { 4, 1, 2, 3, 4, 5, 6, 7, 8 }); msgs.Add(new byte[] { 12, 1, 2, 3, 4, 5, 6, 7, 8 }); msgs.Add(new byte[] { 6, 1, 2, 3 }); msgs.Add(new byte[] { 8, 1, 2, 3 });
            var pick = new byte[14]; pick[0] = SharedWorld.MsgPickup; msgs.Add(pick);
            msgs.Add(new byte[] { 1 });                                                  // a world request: would send my slot world
            var hw = new byte[5]; hw[0] = Authority.MsgHaveWorld; Buffer.BlockCopy(BitConverter.GetBytes(WorldSync.MyEpoch), 0, hw, 1, 4); msgs.Add(hw);   // "I play in your world" with my real world number
            var ep = new byte[5]; ep[0] = Authority.MsgEpoch; Buffer.BlockCopy(BitConverter.GetBytes(leaderEpoch + 7), 0, ep, 1, 4); msgs.Add(ep);
            msgs.Add(new byte[] { 2, 1, 2, 3 }); msgs.Add(new byte[] { WorldTransfer.MsgPart, 1, 2, 3 });   // a snapshot and a world part
            bool threw = false;
            // twice: from a member, and as if the party leader sent it (the real race leader: the gate lets its epoch and snapshots through, only isolation stops them)
            try { foreach (var m in msgs) cb.RouteReliableForTest(bot, m, false); foreach (var m in msgs) cb.RouteReliableForTest(bot, m, true); } catch (Exception) { threw = true; }
            bool haveWorld = bot.HasMyWorld == 0;
            WorldSync.Request(cap); WorldSync.Push(cap);
            WorldSync.Forget(cap);
            bool same = Math.Abs(SceneProfile.CrystalsCollected - crystals) < 0.01f && SharedWorld.StatApplied == applied && SharedWorld.StatRejected == rejected && SharedWorld.StatPickupsApplied == pickups;
            bool ok = iso && !threw && same && haveWorld && cap.Sent.Count == 0 && !WorldSync.GuestMode && Speedrun.SlotPathsActive() && WorldSync.LeaderEpoch == leaderEpoch && !WorldSync.RequestOutstanding;
            SrCheck("SR-RACE-ISOLATION", "in a speedrun slot (solo or race) no plant, crystal, island, teleporter, pickup, world request, snapshot, epoch or have-world message from another player changes anything, and my world is never sent or asked for",
                ok, "isolated=" + iso + " threw=" + threw + " unchanged=" + same + " (crystals " + crystals + "->" + SceneProfile.CrystalsCollected + ", crystal message for " + (guid ?? "none") + ") sent " + cap.Sent.Count + " guest=" + WorldSync.GuestMode + " slot=" + Speedrun.SlotPathsActive() + " epoch same=" + (WorldSync.LeaderEpoch == leaderEpoch) + " have-world ignored=" + haveWorld);
        }
    }
}
