using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace CoopMod
{
    // Audit checks for sheep, dodos, glo worms and venus traps in a party (CreatureSync): the messages and the gate, who gets which creature and
    // how often (and never more than the bandwidth cap), the leader keeping creatures near a guest awake and sending them, a guest's creatures
    // following the leader's with their own brains off, a creature that dies on the leader going on the guest (and never by the guest's own game),
    // a guest holding a creature, venus traps, the "flying sheep" achievement, and everything given back afterwards. Fake peers only.
    // Group "creatures" (audit\check-groups.txt).
    public partial class SelfTest
    {
        const ulong CrLeader = 76561199000000201UL, CrGuest = 76561199000000202UL, CrStranger = 76561199000000203UL;

        class CaptureLink : ITransport
        {
            public readonly List<byte[]> Sent = new List<byte[]>(), Reliable = new List<byte[]>();
            public void Send(byte[] data) { Sent.Add(data); }
            public void SendReliable(byte[] data) { Reliable.Add(data); }
            public byte[] Receive() { return null; }
            public byte[] ReceiveReliable() { return null; }
            public string Diagnostics() { return "creature test"; }
            public void Close() { }
        }

        static CreatureSync.Pose CrPose(ushort id, byte life, Vector3 pos, Vector3 vel)
        {
            return new CreatureSync.Pose { Id = id, Life = life, Pos = pos, Euler = new Vector3(0f, 90f, 0f), Vel = vel };
        }

        // ---- CREATURE-WIRE, CREATURE-BUDGET, CREATURE-INTEREST: no world needed ------------------------------------------------------

        void CheckCreaturePure()
        {
            var bad = new List<string>();
            var poses = new List<CreatureSync.Pose> { CrPose(5, 2, new Vector3(10.5f, -3.25f, 999f), new Vector3(1.5f, -0.25f, 3f)), CrPose(6, 0, Vector3.one, Vector3.zero) };
            poses[0] = new CreatureSync.Pose { Id = 5, Life = 2, Flags = CreatureSync.FlagSleep, State = 9, Pos = poses[0].Pos, Euler = new Vector3(10f, 200f, 359f), Vel = poses[0].Vel };
            byte[] m = CreatureSync.BuildPose(77, 1234, poses);
            int ep; ushort sq; var back = new List<CreatureSync.Pose>();
            if (!CreatureSync.ReadPose(m, out ep, out sq, back) || ep != 77 || sq != 1234 || back.Count != 2) bad.Add("pose round trip");
            else
            {
                var b = back[0];
                if (b.Id != 5 || b.Life != 2 || b.Flags != CreatureSync.FlagSleep || b.State != 9 || (b.Pos - poses[0].Pos).magnitude > 0.001f || (b.Vel - poses[0].Vel).magnitude > 0.02f
                    || Mathf.Abs(Mathf.DeltaAngle(b.Euler.y, 200f)) > 0.05f || Mathf.Abs(Mathf.DeltaAngle(b.Euler.z, 359f)) > 0.05f) bad.Add("pose values changed on the way");
            }
            if (m.Length != CreatureSync.PoseHeader + 2 * CreatureSync.EntrySize) bad.Add("pose size " + m.Length);
            var cut = new byte[m.Length - 1]; Buffer.BlockCopy(m, 0, cut, 0, cut.Length);
            if (CreatureSync.ReadPose(cut, out ep, out sq, back)) bad.Add("a cut-off pose was read");
            var pad = new byte[m.Length + 1]; Buffer.BlockCopy(m, 0, pad, 0, m.Length);
            if (CreatureSync.ReadPose(pad, out ep, out sq, back)) bad.Add("a padded pose was read");
            var ver = (byte[])m.Clone(); ver[1] = 9;
            if (CreatureSync.ReadPose(ver, out ep, out sq, back)) bad.Add("a pose of an unknown version was read");
            var many = (byte[])m.Clone(); many[8] = 3;
            if (CreatureSync.ReadPose(many, out ep, out sq, back)) bad.Add("a pose with a wrong count was read");
            var nan = (byte[])m.Clone(); Buffer.BlockCopy(BitConverter.GetBytes(float.NaN), 0, nan, CreatureSync.PoseHeader + 5, 4);
            if (CreatureSync.ReadPose(nan, out ep, out sq, back)) bad.Add("a pose with a broken position was read");
            var zero = (byte[])m.Clone(); zero[CreatureSync.PoseHeader] = 0; zero[CreatureSync.PoseHeader + 1] = 0;
            if (CreatureSync.ReadPose(zero, out ep, out sq, back)) bad.Add("a pose for creature 0 was read");
            var full = new List<CreatureSync.Pose>(); for (int i = 0; i < 40; i++) full.Add(CrPose((ushort)(i + 1), 0, Vector3.zero, Vector3.zero));
            byte[] fm = CreatureSync.BuildPose(1, 1, full);
            if (fm.Length > CreatureSync.MaxPoseBytes || fm.Length > 1200 || !CreatureSync.ReadPose(fm, out ep, out sq, back) || back.Count != CreatureSync.MaxEntries) bad.Add("a full pose packet is " + fm.Length + " bytes / " + back.Count + " creatures");
            if (PlayerState.Read(m) != null || PlayerState.Read(fm) != null) bad.Add("a pose packet reads as a player's pose (an older version would use it)");

            var table = new List<CreatureSync.TableEntry> {
                new CreatureSync.TableEntry { Id = 3, Life = 4, Alive = true, Key = -12345, Name = "SheepRare", Pos = new Vector3(1, 2, 3) },
                new CreatureSync.TableEntry { Id = 9, Life = 0, Alive = false, Key = 7, Name = "GloWorm", Pos = Vector3.zero } };
            byte[] t = CreatureSync.BuildTable(55, table, new List<int> { 42, -1 });
            var tb = new List<CreatureSync.TableEntry>(); var gone = new List<int>();
            if (!CreatureSync.ReadTable(t, out ep, tb, gone) || ep != 55 || tb.Count != 2 || gone.Count != 2 || gone[0] != 42 || tb[0].Name != "SheepRare" || tb[0].Key != -12345 || tb[0].Life != 4 || !tb[0].Alive || tb[1].Alive || tb[1].Name != "GloWorm")
                bad.Add("table round trip");
            var tcut = new byte[t.Length - 2]; Buffer.BlockCopy(t, 0, tcut, 0, tcut.Length);
            if (CreatureSync.ReadTable(tcut, out ep, tb, gone)) bad.Add("a cut-off table was read");
            var tpad = new byte[t.Length + 3]; Buffer.BlockCopy(t, 0, tpad, 0, t.Length);
            if (CreatureSync.ReadTable(tpad, out ep, tb, gone)) bad.Add("a padded table was read");
            var hugeName = new List<CreatureSync.TableEntry> { new CreatureSync.TableEntry { Id = 1, Name = new string('x', 300) } };
            byte[] th = CreatureSync.BuildTable(1, hugeName, new List<int>());
            if (!CreatureSync.ReadTable(th, out ep, tb, gone) || tb[0].Name.Length > 32) bad.Add("a long name is not cut to 32");

            byte[] d = CreatureSync.BuildDeath(55, 3, 4), g = CreatureSync.BuildTrapGone(55, 42);
            if (CreatureSync.OpOf(d) != CreatureSync.OpDeath || CreatureSync.OpOf(g) != CreatureSync.OpTrapGone || CreatureSync.OpOf(t) != CreatureSync.OpTable) bad.Add("message kinds");
            var dpad = new byte[d.Length + 1]; Buffer.BlockCopy(d, 0, dpad, 0, d.Length);
            if (CreatureSync.OpOf(dpad) != 0) bad.Add("a padded death was taken");
            var odd = (byte[])d.Clone(); odd[1] = 77;
            if (CreatureSync.OpOf(odd) != 0) bad.Add("an unknown op was taken");
            if (MessageGate.Classify(d, true) != Route.Creature || MessageGate.Classify(t, true) != Route.Creature || MessageGate.Classify(g, true) != Route.Creature) bad.Add("gate: the leader's creature message is not routed to the creatures");
            if (MessageGate.Classify(d, false) != Route.Ignore || MessageGate.Classify(t, false) != Route.Ignore) bad.Add("gate: a guest's creature message is not refused");
            if (MessageGate.Classify(dpad, true) != Route.Ignore || MessageGate.Classify(new byte[] { CreatureSync.Msg, 1 }, true) != Route.Ignore) bad.Add("gate: a creature message of the wrong size is not refused");
            if (CreatureSync.RoleFor(true, true, true, false, true) != CreatureRole.None || CreatureSync.RoleFor(true, true, true, true, true) != CreatureRole.None) bad.Add("a race still syncs creatures");
            if (CreatureSync.RoleFor(false, true, true, true, false) != CreatureRole.None) bad.Add("a guest not in the leader's world follows its creatures");
            if (CreatureSync.RoleFor(false, false, true, false, false) != CreatureRole.None || CreatureSync.RoleFor(false, true, false, false, false) != CreatureRole.None) bad.Add("creatures synced outside a world or alone");
            if (CreatureSync.WantsCreatures(false, 0u) || !CreatureSync.WantsCreatures(false, 12345u) || !CreatureSync.WantsCreatures(true, 0u)) bad.Add("an older guest (its poses carry no motion session) still gets creature data, or a new one does not");
            if (CreatureSync.RoleFor(false, true, true, false, false) != CreatureRole.Leader || CreatureSync.RoleFor(false, true, true, true, true) != CreatureRole.Guest) bad.Add("roles");
            Check("CREATURE-WIRE", "creature poses, lists, deaths and venus traps survive a round trip; cut-off, padded, broken, unknown and oversized ones are refused; the gate takes them from the leader only; an older version reads a pose as nothing; a race or a guest outside the leader's world syncs no creature",
                bad.Count == 0, bad.Count == 0 ? "pose " + m.Length + " bytes, full packet " + fm.Length + " bytes, list " + t.Length + " bytes" : string.Join("; ", bad.ToArray()));

            // CREATURE-BUDGET: three guests with 200 creatures each within 80 m, 10 s at 60 frames a second
            bad.Clear();
            string detail = "";
            for (int pass = 0; pass < 2; pass++)
            {
                int rate = pass == 0 ? CreatureSync.BytesPerSecond : 2000;
                float T = 10f; int worst = 0, packets = 0, maxEntries = 0, throttled = 0; long total = 0; int unseen = 0;
                for (int v = 0; v < 3; v++)
                {
                    var bucket = new CreatureSync.Bucket(rate, CreatureSync.MaxPoseBytes * 2);
                    var last = new Dictionary<ushort, float>(); var seen = new HashSet<ushort>();
                    var cands = new List<CreatureSync.Cand>(); var pick = new List<ushort>();
                    float next = 0f; long bytes = 0;
                    for (float now = 0f; now < T; now += 1f / 60f)
                    {
                        if (now < next) continue;
                        next = now + CreatureSync.NearInterval;
                        for (int k = 0; k < CreatureSync.MaxPacketsPerTick; k++)      // as SendPoses: more packets a tick while creatures are due and the cap allows
                        {
                            cands.Clear();
                            for (int i = 1; i <= 200; i++) { float l; if (!last.TryGetValue((ushort)i, out l)) l = -999f; cands.Add(new CreatureSync.Cand { Id = (ushort)i, Dist = (i * 7 + v * 13) % 80, Last = l }); }
                            if (CreatureSync.Plan(cands, now, CreatureSync.MaxEntries, pick) == 0) break;
                            var list = new List<CreatureSync.Pose>(); foreach (var id in pick) list.Add(CrPose(id, 0, Vector3.zero, Vector3.zero));
                            byte[] pm = CreatureSync.BuildPose(1, 1, list);
                            if (!bucket.Take(pm.Length, now)) { throttled++; break; }
                            foreach (var id in pick) { last[id] = now; seen.Add(id); }
                            bytes += pm.Length; packets++; worst = Math.Max(worst, pm.Length); maxEntries = Math.Max(maxEntries, pick.Count);
                        }
                    }
                    total += bytes;
                    unseen += 200 - seen.Count;
                    float perSec = bytes / T, allowed = rate + CreatureSync.MaxPoseBytes * 2 / T;
                    if (perSec > allowed) bad.Add((pass == 0 ? "" : "lowered cap: ") + "guest " + v + " got " + perSec.ToString("F0") + " B/s (cap " + allowed.ToString("F0") + ")");
                    if (perSec < rate * 0.95f) bad.Add((pass == 0 ? "" : "lowered cap: ") + "guest " + v + " got only " + perSec.ToString("F0") + " B/s with 200 creatures due (cap " + rate + " not used)");
                }
                if (worst > CreatureSync.MaxPoseBytes || worst > 1200 || maxEntries > CreatureSync.MaxEntries) bad.Add("a packet of " + worst + " bytes / " + maxEntries + " creatures");
                if (pass == 0 && unseen > 0) bad.Add(unseen + " creatures near a guest were never sent in 10 s");
                if (pass == 1 && throttled == 0) bad.Add("the lowered cap never held a packet back");
                detail += (pass == 0 ? "cap " : "; lowered cap ") + rate + " B/s: " + (total / 3 / T).ToString("F0") + " B/s a guest, biggest " + worst + " bytes, " + throttled + " held back";
            }
            // 26 near creatures (more than one packet holds) fit in the cap: every one of them 10 times a second
            {
                var bucket = new CreatureSync.Bucket(CreatureSync.BytesPerSecond, CreatureSync.MaxPoseBytes * 2);
                var last = new Dictionary<ushort, float>(); var sent = new Dictionary<ushort, int>();
                var cands = new List<CreatureSync.Cand>(); var pick = new List<ushort>();
                float next = 0f; int slowest = int.MaxValue, ticks = 0;
                for (float now = 0f; now < 10f; now += 1f / 60f)
                {
                    if (now < next) continue;
                    next = now + CreatureSync.NearInterval; ticks++;
                    for (int k = 0; k < CreatureSync.MaxPacketsPerTick; k++)
                    {
                        cands.Clear();
                        for (int i = 1; i <= 26; i++) { float l; if (!last.TryGetValue((ushort)i, out l)) l = -999f; cands.Add(new CreatureSync.Cand { Id = (ushort)i, Dist = 10f + i, Last = l }); }
                        if (CreatureSync.Plan(cands, now, CreatureSync.MaxEntries, pick) == 0) break;
                        var list = new List<CreatureSync.Pose>(); foreach (var id in pick) list.Add(CrPose(id, 0, Vector3.zero, Vector3.zero));
                        if (!bucket.Take(CreatureSync.BuildPose(1, 1, list).Length, now)) break;
                        foreach (var id in pick) { last[id] = now; int c; sent.TryGetValue(id, out c); sent[id] = c + 1; }
                    }
                }
                for (int i = 1; i <= 26; i++) { int c; sent.TryGetValue((ushort)i, out c); slowest = Math.Min(slowest, c); }
                if (slowest < ticks * 0.97f) bad.Add("with 26 near creatures the slowest was sent " + slowest + " times in " + ticks + " ticks (each tick wanted: the cap allows it)");
                detail += "; 26 near creatures: slowest " + slowest + " of " + ticks + " ticks";
            }
            Check("CREATURE-BUDGET", "with 200 creatures near each of 3 guests, no guest gets more than its bandwidth cap (8 KB/s, also a lowered one) but the cap is really used, no packet is over 24 creatures or 1200 bytes, every creature is sent, and near creatures that fit in the cap (26, more than one packet) all get 10 updates a second",
                bad.Count == 0, bad.Count == 0 ? detail : string.Join("; ", bad.ToArray()));

            // CREATURE-INTEREST: near ones 10 times a second, far ones once a second, nothing beyond 150 m
            bad.Clear();
            var counts = new int[3]; var lastI = new float[] { -999f, -999f, -999f }; float[] dist = { 30f, 120f, 200f };
            var ci = new List<CreatureSync.Cand>(); var pk = new List<ushort>();
            float nx = 0f;
            for (float now = 0f; now < 10f; now += 1f / 60f)
            {
                if (now < nx) continue;
                nx = now + CreatureSync.NearInterval;
                ci.Clear(); for (int i = 0; i < 3; i++) ci.Add(new CreatureSync.Cand { Id = (ushort)(i + 1), Dist = dist[i], Last = lastI[i] });
                CreatureSync.Plan(ci, now, CreatureSync.MaxEntries, pk);
                foreach (var id in pk) { counts[id - 1]++; lastI[id - 1] = now; }
            }
            if (counts[0] < 85 || counts[0] > 101) bad.Add("a creature 30 m away was sent " + counts[0] + " times in 10 s (about 100 wanted)");
            if (counts[1] < 8 || counts[1] > 11) bad.Add("a creature 120 m away was sent " + counts[1] + " times in 10 s (about 10 wanted)");
            if (counts[2] != 0) bad.Add("a creature 200 m away was sent " + counts[2] + " times");
            Check("CREATURE-INTEREST", "a guest gets the creatures near it 10 times a second, those up to 150 m once a second and none further",
                bad.Count == 0, bad.Count == 0 ? counts[0] + " / " + counts[1] + " / " + counts[2] + " sends in 10 s at 30 / 120 / 200 m" : string.Join("; ", bad.ToArray()));
        }

        // ---- in the world ---------------------------------------------------------------------------------------------------------------

        static Transform CrCamera() { return Camera.main != null ? Camera.main.transform : null; }

        static int CrFixedSteps;                 // physics steps seen (a puppet is steered in FixedUpdate)
        void FixedUpdate() { CrFixedSteps++; }

        // A spot a guest holding this creature could be at: 4 m to my camera's side (or another side), on open ground at about the
        // creature's height, with a clear line from the creature (a spot inside a rock or behind a wall can never be reached by walking).
        // Several distances are tried, all beyond SnapDistance (so the held creature is always snapped there, then held), because one
        // fixed distance on a slope or a ledge can miss every direction; a blind spot (over a drop, inside a rock) is never returned.
        static readonly float[] CrSpotDists = { 4f, 3.5f, 5f, 3.2f, 6f };
        static bool CrFreeSpot(CreatureSync.Rec c, Transform cam, out Vector3 spot, out string how)
        {
            Vector3 from = c.Body.position;
            Vector3 right = cam != null ? Vector3.ProjectOnPlane(cam.right, Vector3.up).normalized : Vector3.right;
            if (right.sqrMagnitude < 0.5f) right = Vector3.right;
            Vector3 fwd = Vector3.Cross(right, Vector3.up);
            Vector3[] dirs = { right, -right, fwd, -fwd, (right + fwd).normalized, (right - fwd).normalized, (-right + fwd).normalized, (-right - fwd).normalized };
            float groundHere = CrGround(c, from);
            float baseY = float.IsNaN(groundHere) ? from.y : groundHere;
            float lift = float.IsNaN(groundHere) ? 0f : from.y - groundHere;
            int noGround = 0, steps = 0, blocked = 0;
            foreach (float dist in CrSpotDists)
                for (int i = 0; i < dirs.Length; i++)
                {
                    Vector3 to = from + dirs[i] * dist;
                    float g = CrGround(c, to);
                    if (float.IsNaN(g)) { noGround++; continue; }
                    if (Mathf.Abs(g - baseY) > 1.0f) { steps++; continue; }   // a drop or a step up
                    to.y = g + lift;
                    if (CrBlocked(c, from + Vector3.up * 0.3f, to + Vector3.up * 0.3f)) { blocked++; continue; }
                    how = "direction " + i + " at " + dist.ToString("F1") + " m, ground step " + (g - baseY).ToString("F2") + " m";
                    spot = to; return true;
                }
            how = "no open spot (" + (float.IsNaN(groundHere) ? "no ground under it" : "ground " + lift.ToString("F2") + " m below") + "; " + noGround + " without ground, " + steps + " drops or steps, " + blocked + " blocked)";
            spot = from; return false;
        }

        static bool OwnCollider(CreatureSync.Rec c, Collider col) { return col != null && c.Go != null && col.transform.IsChildOf(c.Go.transform); }

        static float CrGround(CreatureSync.Rec c, Vector3 at)
        {
            float best = float.NaN;
            foreach (var h in Physics.RaycastAll(at + Vector3.up * 3f, Vector3.down, 8f))
                if (!h.collider.isTrigger && !OwnCollider(c, h.collider) && (float.IsNaN(best) || h.point.y > best)) best = h.point.y;
            return best;
        }

        static bool CrBlocked(CreatureSync.Rec c, Vector3 a, Vector3 b)
        {
            foreach (var h in Physics.RaycastAll(a, (b - a).normalized, (b - a).magnitude))
                if (!h.collider.isTrigger && !OwnCollider(c, h.collider)) return true;
            return false;
        }

        IEnumerator CrFrames(float seconds, CreatureRole role, List<CreatureSync.Viewer> viewers, ITransport leaderLink, ulong leader, Action each)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end)
            {
                CreatureSync.Tick(role, viewers, leaderLink, leader);
                if (each != null) each();
                yield return null;
            }
        }

        static int CrEntriesFor(List<byte[]> sent, ushort id, out int biggest, List<CreatureSync.Pose> lastOut)
        {
            int n = 0; biggest = 0; int ep; ushort sq; var l = new List<CreatureSync.Pose>();
            foreach (var m in sent)
            {
                biggest = Math.Max(biggest, m.Length);
                if (!CreatureSync.ReadPose(m, out ep, out sq, l)) continue;
                foreach (var p in l) if (p.Id == id) { n++; if (lastOut != null) { lastOut.Clear(); lastOut.Add(p); } }
            }
            return n;
        }

        static bool CrAwake(CreatureSync.Rec r)
        {
            if (r == null || r.Walk == null) return r != null && r.Body != null && !r.Body.isKinematic;
            return !r.Walk.m_distanceDisabled && r.Body != null && !r.Body.isKinematic;
        }

        IEnumerator TestCreaturesLive()
        {
            CreatureSync.TestMode = true;
            CreatureSync.Release("audit");
            try { Check("CREATURE-CENSUS", "the game's creature, respawn, venus trap and flying sheep fields the sync uses exist", CreatureSync.ReflectionOk, CreatureSync.ReflectionOk ? "" : "a private field or method was not found"); }
            catch (Exception e) { Check("CREATURE-CENSUS", "the check ran", false, Describe(e)); }
            var leaderLink = new CaptureLink(); var guestLink = new CaptureLink(); var botLink = new CaptureLink();
            var cam = CrCamera();
            Vector3 me = cam != null ? cam.position : Vector3.zero;

            // ================= as the LEADER =================
            var viewers = new List<CreatureSync.Viewer>();
            yield return StartCoroutine(CrFrames(1.2f, CreatureRole.Leader, viewers, null, 0UL, null));
            var recs = new List<CreatureSync.Rec>(CreatureSync.Records);
            var names = new Dictionary<string, int>(); int walkers = 0, worms = 0;
            foreach (var r in recs) { int cn; names.TryGetValue(r.Name, out cn); names[r.Name] = cn + 1; if (r.Worm) worms++; else walkers++; }
            var nl = new List<string>(); foreach (var kv in names) nl.Add(kv.Key + " x" + kv.Value);
            Note("creatures: " + recs.Count + " in this level (" + string.Join(", ", nl.ToArray()) + ")");
            CreatureSync.Rec far = null, a = null;
            foreach (var r in recs)
            {
                if (r.Worm || !r.Alive) continue;
                float dd = (r.Body.position - me).magnitude;
                if (dd < 400f && (a == null || dd > (a.Body.position - me).magnitude)) a = r;     // the walker furthest from my camera: the game freezes that one
            }
            if (a == null) { Note("creatures: no sheep or dodo in this level; CREATURE-LEADER, CREATURE-AWAKE, CREATURE-PUPPET, CREATURE-DEATH, CREATURE-CLAIM, CREATURE-RELEASE skipped"); CreatureSync.Release("audit"); CreatureSync.TestMode = false; yield break; }
            foreach (var r in recs) if (r.Alive && r != a && (r.Body.position - a.Body.position).magnitude > CreatureSync.FarRange + 10f) { far = r; break; }
            bool frozenBefore = a.Walk != null && a.Walk.m_distanceDisabled;
            Note("creatures: testing with " + a.Name + " #" + a.Id + " " + (a.Body.position - me).magnitude.ToString("F0") + " m from my camera" + (frozenBefore ? " (frozen by the game)" : ""));

            viewers.Add(new CreatureSync.Viewer { Id = CrGuest, Pos = a.Body.position + Vector3.up, Link = guestLink });
            viewers.Add(new CreatureSync.Viewer { Id = 99, Pos = a.Body.position, Link = botLink, Bot = true });
            bool everFrozen = false; int afterSecond = 0;
            float t0 = Time.realtimeSinceStartup;
            yield return StartCoroutine(CrFrames(2.5f, CreatureRole.Leader, viewers, null, 0UL, delegate {
                viewers[0].Pos = a.Body != null ? a.Body.position + Vector3.up : viewers[0].Pos;
                if (Time.realtimeSinceStartup - t0 > 0.5f) { afterSecond++; if (!CrAwake(a)) everFrozen = true; }
            }));
            try
            {
                int biggest; var lastP = new List<CreatureSync.Pose>();
                int sentA = CrEntriesFor(guestLink.Sent, a.Id, out biggest, lastP);
                int sentFar = far != null ? CrEntriesFor(guestLink.Sent, far.Id, out biggest, null) : 0;
                CrEntriesFor(guestLink.Sent, a.Id, out biggest, null);
                bool tableOk = false; int ep; var tb = new List<CreatureSync.TableEntry>(); var gl = new List<int>();
                foreach (var m in guestLink.Reliable) if (CreatureSync.ReadTable(m, out ep, tb, gl) && ep == WorldSync.MyEpoch) foreach (var e in tb) if (e.Id == a.Id && e.Name == a.Name && e.Alive) tableOk = true;
                var st = CreatureSync.StatsFor(CrGuest);
                Check("CREATURE-LEADER", "as the leader: a guest gets the creature list and the poses of the creatures near it about 10 times a second, never one more than 150 m away, never a packet over 1200 bytes; a bot gets no messages",
                    tableOk && sentA >= 15 && sentFar == 0 && biggest <= CreatureSync.MaxPoseBytes && biggest <= 1200 && botLink.Reliable.Count == 0 && lastP.Count == 1 && (lastP[0].Pos - a.Body.position).magnitude < 3f,
                    "list " + (tableOk ? "has it" : "MISSING it") + ", " + sentA + " poses of " + a.Name + " in 2.5 s, " + sentFar + " of one " + (far != null ? (far.Body.position - a.Body.position).magnitude.ToString("F0") + " m away" : "(none far away)") + ", biggest packet " + biggest + " bytes, " + (st != null ? st.Bytes / 2.5f : 0f).ToString("F0") + " B/s, bot reliable " + botLink.Reliable.Count);
                Check("CREATURE-AWAKE", "as the leader: a creature near a guest is never frozen by my game, even far from my camera",
                    !everFrozen && afterSecond > 10 && CrAwake(a), (frozenBefore ? "frozen before; " : "awake before; ") + (everFrozen ? "frozen while the guest stood next to it" : "awake for " + afterSecond + " frames"));
            }
            catch (Exception e) { Check("CREATURE-LEADER", "the check ran", false, Describe(e)); }

            // flying sheep: a grab by a friend's copy never counts for my achievement
            CreatureSync.Rec fly = null; foreach (var r in recs) if (r.Fly != null && r.Alive) { fly = r; break; }
            if (fly == null) Note("creatures: no creature with the flying sheep achievement here; CREATURE-FLYSHEEP skipped");
            else
            {
                Exception fe = null;
                try { typeof(FlyingSheep).GetField("m_grabCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(fly.Fly, 1); } catch (Exception e) { fe = e; }
                yield return StartCoroutine(CrFrames(0.2f, CreatureRole.Leader, viewers, null, 0UL, null));
                Check("CREATURE-FLYSHEEP", "a sheep grabbed by a friend's copy does not count as held by me for the flying sheep achievement", fe == null && CreatureSync.FlyGrabs(fly.Fly) == 0, fe != null ? Describe(fe) : "grabs counted " + CreatureSync.FlyGrabs(fly.Fly));
            }

            // a guest holds a creature: the leader follows that guest (brain off), and takes it back after letting go
            // where a guest could really stand with it: open ground, nothing in between. The furthest walker first; when it stands where no
            // guest could carry it 3-6 m (a ledge, a steep slope), another walker that has open ground around it.
            CreatureSync.Rec c = a;
            string heldSpot; Vector3 heldAt;
            bool spotOk = CrFreeSpot(c, cam, out heldAt, out heldSpot);
            if (!spotOk)
            {
                string firstWhy = a.Name + " #" + a.Id + ": " + heldSpot;
                foreach (var r in recs)
                {
                    if (r == a || r.Worm || !r.Alive || r.Body == null || (r.Body.position - me).magnitude >= 400f) continue;
                    Vector3 s; string why;
                    if (CrFreeSpot(r, cam, out s, out why)) { c = r; heldAt = s; heldSpot = r.Name + " #" + r.Id + " " + why + " (" + firstWhy + ")"; spotOk = true; break; }
                }
                if (!spotOk) heldSpot = firstWhy + "; no other walker has one";
            }
            if (c != a) Note("creatures: CREATURE-CLAIM uses " + c.Name + " #" + c.Id + ": " + heldSpot);
            int refused0 = CreatureSync.StatRefused;
            var held = new List<CreatureSync.Pose> { CrPose(c.Id, c.Life, heldAt, Vector3.zero) };
            CreatureSync.HandlePose(CrStranger, CreatureSync.BuildPose(WorldSync.MyEpoch, 1, held), false, false, c.Body.position, true);
            bool strangerRefused = CreatureSync.BrainOn(c) && CreatureSync.StatRefused > refused0;
            CreatureSync.HandlePose(CrGuest, CreatureSync.BuildPose(WorldSync.MyEpoch, 2, held), false, true, c.Body.position + Vector3.right * 100f, true);
            bool farRefused = CreatureSync.BrainOn(c);
            bool heldOff = false, followed = false; float minGap = 999f, scale0 = Time.timeScale; int snaps0 = c.Snaps, fixed0 = CrFixedSteps;
            float h0 = Time.realtimeSinceStartup;
            yield return StartCoroutine(CrFrames(1.2f, CreatureRole.Leader, viewers, null, 0UL, delegate {
                if (Time.realtimeSinceStartup - h0 < 0.8f)
                {
                    held[0] = CrPose(c.Id, c.Life, held[0].Pos, Vector3.zero);
                    CreatureSync.HandlePose(CrGuest, CreatureSync.BuildPose(WorldSync.MyEpoch, 3, held), false, true, held[0].Pos, true);
                    if (!CreatureSync.BrainOn(c) && c.Agent != null && c.Agent.Steer) heldOff = true;
                    if (c.Body != null) minGap = Mathf.Min(minGap, (c.Body.position - held[0].Pos).magnitude);
                    if (c.Body != null && (c.Body.position - held[0].Pos).magnitude < 1.5f) followed = true;
                }
            }));
            yield return StartCoroutine(CrFrames(0.8f, CreatureRole.Leader, viewers, null, 0UL, null));
            bool back = CreatureSync.BrainOn(c) && c.ClaimBy == 0;
            int gsBefore = guestLink.Sent.Count;

            // ================= the leader's creature dies =================
            CreatureSync.Rec victim = null; foreach (var r in recs) if (r.Alive && !r.Worm && r != a) { victim = r; break; }
            if (victim == null) victim = a;
            int deathsBefore = CreatureSync.StatDeathsSent; ushort vid = victim.Id; byte vlife = victim.Life;
            guestLink.Reliable.Clear();
            Exception ke = null;
            try { Chunker.TurnToChunks(victim.Go, 0f); } catch (Exception e) { ke = e; }
            yield return StartCoroutine(CrFrames(1.3f, CreatureRole.Leader, viewers, null, 0UL, null));
            bool deathOut = false, listSaysDead = false;
            foreach (var m in guestLink.Reliable)
            {
                if (CreatureSync.OpOf(m) == CreatureSync.OpDeath && BitConverter.ToUInt16(m, 6) == vid && m[8] == vlife) deathOut = true;
                int ep; var tb = new List<CreatureSync.TableEntry>(); var gl = new List<int>();
                if (CreatureSync.ReadTable(m, out ep, tb, gl)) foreach (var e in tb) if (e.Id == vid && !e.Alive) listSaysDead = true;
            }
            int bigD; bool posesStop = CrEntriesFor(guestLink.Sent.GetRange(gsBefore, guestLink.Sent.Count - gsBefore), vid, out bigD, null) <= 2;

            // ================= venus traps on the leader =================
            var trapsHere = new List<VenusTrap>(); foreach (VenusTrap tr in UnityEngine.Object.FindObjectsOfType(typeof(VenusTrap))) if (tr != null && !CreatureSync.TrapDying(tr)) trapsHere.Add(tr);
            VenusTrap trapL = trapsHere.Count > 0 ? trapsHere[0] : null, trapG = trapsHere.Count > 1 ? trapsHere[1] : null;
            bool hadTrapL = trapL != null, hadTrapG = trapG != null;   // (a destroyed trap compares == null afterwards: remember whether there was one)
            bool trapOut = false; int keyL = trapL != null ? CreatureSync.KeyOf(trapL.transform) : 0;
            if (trapL != null)
            {
                guestLink.Reliable.Clear();
                CreatureSync.DestroyTrap(trapL);
                yield return StartCoroutine(CrFrames(1.5f, CreatureRole.Leader, viewers, null, 0UL, null));
                foreach (var m in guestLink.Reliable) if (CreatureSync.OpOf(m) == CreatureSync.OpTrapGone && BitConverter.ToInt32(m, 6) == keyL) trapOut = true;
            }
            Check("CREATURE-CLAIM", "as the leader: a creature a guest holds follows that guest with its own brain off and gets its brain back after they let go; a player not in my world or a guest too far from it is refused",
                spotOk && strangerRefused && farRefused && heldOff && followed && back, (spotOk ? "" : "NO SPOT a guest could hold it at: " + heldSpot + "; ") + "stranger " + (strangerRefused ? "refused" : "TAKEN") + ", far guest " + (farRefused ? "refused" : "TAKEN") + ", held: brain " + (heldOff ? "off" : "ON") + ", " + (followed ? "followed (spot " + heldSpot + ", closest " + minGap.ToString("F2") + " m)" : "NOT followed (spot " + heldSpot + ", closest " + minGap.ToString("F2") + " m, time scale " + scale0 + " -> " + Time.timeScale + ", physics steps " + (CrFixedSteps - fixed0) + ", snaps " + (c.Snaps - snaps0) + ", kinematic " + (c.Body != null && c.Body.isKinematic) + ", last error " + c.LastErr.ToString("F1") + " m)") + "; let go: " + (back ? "brain back" : "STILL OFF"));
            if (CreatureSync.Role != CreatureRole.Leader) Note("creatures: role changed to " + CreatureSync.Role);

            // ================= as a GUEST =================
            CreatureSync.Release("audit");
            yield return null;
            CreatureSync.Tick(CreatureRole.Guest, null, leaderLink, CrLeader);
            int epoch = CreatureSync.ExpectedEpoch;
            // the leader's list: every creature of mine except one (the leader has not got it)
            var locals = new List<GameObject>();
            foreach (BehaviourController ai in UnityEngine.Object.FindObjectsOfType(typeof(BehaviourController))) if (ai != null && !ai.name.StartsWith("Walkster") && ai.GetComponentsInChildren<CreatureWalkScript>(true).Length > 0) locals.Add(ai.gameObject);
            foreach (GloWormBehaviour gw in UnityEngine.Object.FindObjectsOfType(typeof(GloWormBehaviour))) if (gw != null && gw.rigidbody != null) locals.Add(gw.gameObject);
            GameObject ga = null, spare = null; float gaD = float.MaxValue;
            foreach (var go in locals)
            {
                if (go.GetComponent<BehaviourController>() == null) continue;
                float dd = (go.transform.position - me).magnitude;
                if (dd < gaD) { gaD = dd; if (ga != null && spare == null) spare = ga; ga = go; } else if (spare == null) spare = go;
            }
            if (ga == null) { Note("creatures: no sheep or dodo left for the guest checks"); CreatureSync.Release("audit"); CreatureSync.TestMode = false; yield break; }
            var table = new List<CreatureSync.TableEntry>(); ushort nid = 500, gaId = 0;
            foreach (var go in locals)
            {
                if (go == spare) continue;
                var e = new CreatureSync.TableEntry { Id = ++nid, Life = 1, Alive = true, Key = CreatureSync.KeyOf(go.transform), Name = go.name, Pos = go.transform.position };
                if (go == ga) gaId = e.Id;
                table.Add(e);
            }
            CreatureSync.Handle(CrStranger, CreatureSync.BuildTable(epoch, table, new List<int>()));
            bool strangerList = !CreatureSync.GuestActive;
            CreatureSync.Handle(CrLeader, CreatureSync.BuildTable(epoch + 1, table, new List<int>()));
            bool oldEpochList = !CreatureSync.GuestActive;
            CreatureSync.Handle(CrLeader, CreatureSync.BuildTable(epoch, table, new List<int>()));
            var ra = CreatureSync.Find(gaId);
            bool mapped = CreatureSync.GuestActive && ra != null && ra.Go == ga;
            int brainsOn = 0, puppets = 0;
            foreach (var r in CreatureSync.Records) if (r.Go != null) { puppets++; if (CreatureSync.BrainOn(r)) brainsOn++; }
            bool spareOff = spare == null || !spare.activeSelf;

            // the leader walks it 1.5 m/s for 3 s
            Vector3 start = ga.transform.position; var body = ra != null ? ra.Body : null; if (body != null) start = body.position;
            Vector3 dir = cam != null ? Vector3.ProjectOnPlane(cam.right, Vector3.up).normalized : Vector3.right;
            if (dir.sqrMagnitude < 0.5f) dir = Vector3.right;
            float w0 = Time.realtimeSinceStartup, errSum = 0f, errMax = 0f, nextPose = 0f; int errN = 0; ushort seq = 10; Vector3 want = start;
            bool brainCameOn = false;
            yield return StartCoroutine(CrFrames(3.2f, CreatureRole.Guest, null, leaderLink, CrLeader, delegate {
                float t = Time.realtimeSinceStartup - w0;
                if (t >= nextPose && t < 3f)
                {
                    nextPose = t + 0.1f;
                    want = start + dir * (1.5f * t);
                    CreatureSync.HandlePose(CrLeader, CreatureSync.BuildPose(epoch, ++seq, new List<CreatureSync.Pose> { CrPose(gaId, 1, want, dir * 1.5f) }), true, false, Vector3.zero, false);
                }
                if (ra != null && ra.Body != null && t > 1.5f) { var e = want - ra.Body.position; e.y = 0f; errSum += e.magnitude; errN++; errMax = Mathf.Max(errMax, e.magnitude); }
                if (ra != null && CreatureSync.BrainOn(ra)) brainCameOn = true;
            }));
            // a pose from somebody who is not the leader, or of another world, moves nothing
            int refusedG = CreatureSync.StatRefused;
            CreatureSync.HandlePose(CrStranger, CreatureSync.BuildPose(epoch, 999, new List<CreatureSync.Pose> { CrPose(gaId, 1, want + Vector3.up * 50f, Vector3.zero) }), true, false, Vector3.zero, false);
            CreatureSync.HandlePose(CrLeader, CreatureSync.BuildPose(epoch + 1, 1000, new List<CreatureSync.Pose> { CrPose(gaId, 1, want + Vector3.up * 50f, Vector3.zero) }), true, false, Vector3.zero, false);
            bool foreignRefused = CreatureSync.StatRefused >= refusedG + 2 && ra != null && (ra.Target.Pos - want).magnitude < 0.5f;
            float errAvg = errN > 0 ? errSum / errN : 99f;
            Check("CREATURE-PUPPET", "as a guest: my creatures take the leader's numbers (one the leader has not got is switched off), their own brains stay off, and one follows the leader's walk; a list or pose from anybody else or of another world is refused",
                strangerList && oldEpochList && mapped && brainsOn == 0 && !brainCameOn && spareOff && errAvg < 1.5f && foreignRefused,
                (mapped ? "mapped" : "NOT mapped") + ", " + puppets + " puppets, " + brainsOn + " brains on" + (brainCameOn ? " (brain came on)" : "") + ", spare " + (spareOff ? "off" : "STILL ON") + ", error avg " + errAvg.ToString("F2") + " m max " + errMax.ToString("F2") + " m (" + (ra != null ? ra.Snaps : 0) + " snaps)" + (strangerList ? "" : ", a stranger's list was TAKEN") + (oldEpochList ? "" : ", another world's list was TAKEN") + (foreignRefused ? "" : ", a foreign pose was TAKEN"));

            // ================= a creature dies on the leader: it goes here too, and never by my own game =================
            int blocked0 = CreatureSync.StatBlocked;
            Exception ge = null;
            try { Chunker.TurnToChunks(ga, 0f); } catch (Exception e) { ge = e; }
            yield return StartCoroutine(CrFrames(0.15f, CreatureRole.Guest, null, leaderLink, CrLeader, null));
            bool guarded = ge == null && ga != null && ga.activeSelf && ra.Ai != null && CreatureSync.StatBlocked > blocked0;
            var oldAi = ra.Ai;
            CreatureSync.Handle(CrLeader, CreatureSync.BuildDeath(epoch, gaId, 1));
            yield return StartCoroutine(CrFrames(0.2f, CreatureRole.Guest, null, leaderLink, CrLeader, null));
            bool gone = oldAi == null && ra.Hidden && (ra.Go == null || (!ra.Go.activeSelf && !CreatureSync.InQueue(ra.Go)));
            // a late pose of the one that died changes nothing; the replacement's pose shows it
            CreatureSync.HandlePose(CrLeader, CreatureSync.BuildPose(epoch, ++seq, new List<CreatureSync.Pose> { CrPose(gaId, 1, want, Vector3.zero) }), true, false, Vector3.zero, false);
            bool staleIgnored = ra.Hidden;
            Vector3 respawnAt = start + dir * 2f + Vector3.up;
            CreatureSync.HandlePose(CrLeader, CreatureSync.BuildPose(epoch, ++seq, new List<CreatureSync.Pose> { CrPose(gaId, 2, respawnAt, Vector3.zero) }), true, false, Vector3.zero, false);
            yield return StartCoroutine(CrFrames(0.5f, CreatureRole.Guest, null, leaderLink, CrLeader, null));
            bool shown = !ra.Hidden && ra.Go != null && ra.Go.activeInHierarchy && ra.Life == 2 && ra.Body != null && (ra.Body.position - respawnAt).magnitude < CreatureSync.SnapDistance + 1f && !CreatureSync.BrainOn(ra);
            Check("CREATURE-DEATH", "a creature eaten or chunked on the leader is told to everybody (its poses stop, the list says dead) and goes on the guest too, its replacement showing only when the leader's does; the guest's own game never chunks one",
                ke == null && deathOut && listSaysDead && posesStop && guarded && gone && staleIgnored && shown,
                (ke != null ? "leader chunk failed: " + Describe(ke) + "; " : "") + "leader: death " + (deathOut ? "sent" : "NOT sent") + ", list " + (listSaysDead ? "dead" : "NOT dead") + ", poses " + (posesStop ? "stopped" : "GO ON")
                + "; guest: own chunk " + (guarded ? "blocked" : "NOT blocked" + (ge != null ? " " + Describe(ge) : "")) + ", leader's death " + (gone ? "chunked it" : "did NOT") + ", late pose " + (staleIgnored ? "ignored" : "SHOWED it") + ", respawn " + (shown ? "shown" : "NOT shown"));

            // ================= a guest holds one: it moves it and tells the leader =================
            leaderLink.Sent.Clear();
            ra.MineUntil = Time.realtimeSinceStartup + 0.6f;
            bool steerOff = true;
            yield return StartCoroutine(CrFrames(0.5f, CreatureRole.Guest, null, leaderLink, CrLeader, delegate { if (ra.Agent != null && ra.Agent.Steer) steerOff = false; }));
            int biggestC; var lastC = new List<CreatureSync.Pose>();
            int toldLeader = CrEntriesFor(leaderLink.Sent, gaId, out biggestC, lastC);
            bool heldFlag = lastC.Count == 1 && (lastC[0].Flags & CreatureSync.FlagHeld) != 0;
            ra.MineUntil = -1f;

            // ================= venus traps as a guest =================
            bool trapGoneHere = false;
            if (hadTrapG && trapG != null)
            {
                CreatureSync.Handle(CrStranger, CreatureSync.BuildTrapGone(epoch, CreatureSync.KeyOf(trapG.transform)));
                bool strangerTrap = !CreatureSync.TrapDying(trapG);
                CreatureSync.Handle(CrLeader, CreatureSync.BuildTrapGone(epoch, CreatureSync.KeyOf(trapG.transform)));
                yield return StartCoroutine(CrFrames(1.5f, CreatureRole.Guest, null, leaderLink, CrLeader, null));
                trapGoneHere = strangerTrap && trapG == null;
            }
            if (!hadTrapL) Note("creatures: no venus trap in this level; CREATURE-TRAP skipped");
            else if (!hadTrapG) Check("CREATURE-TRAP", "a venus trap the plant destroyed on the leader is told to everybody (only one trap here: the guest side was not tested)", trapOut, trapOut ? "told" : "NOT told");
            else Check("CREATURE-TRAP", "a venus trap the plant destroyed on the leader is told to everybody and goes on the guest through the game's own end of a trap; a stranger's word is ignored", trapOut && trapGoneHere, "leader " + (trapOut ? "told" : "did NOT tell") + "; guest " + (trapGoneHere ? "trap gone" : "trap STILL THERE (or a stranger's word was taken)"));

            // ================= given back (a race, leaving the party) =================
            CreatureSync.Tick(CreatureSync.RoleFor(true, true, true, true, true), null, null, 0UL);
            yield return null; yield return null;
            int stillOff = 0, agents = 0;
            foreach (var go in locals) { if (go == null) continue; var ai = go.GetComponent<BehaviourController>(); if (ai != null && !ai.enabled) stillOff++; var gw = go.GetComponent<GloWormBehaviour>(); if (gw != null && !gw.enabled) stillOff++; if (go.GetComponent<CreatureAgent>() != null) agents++; }
            if (ra.Go != null) { var ai = ra.Go.GetComponent<BehaviourController>(); if (ai != null && !ai.enabled) stillOff++; }
            bool spareBack = spare == null || spare.activeSelf;
            int refusedR = CreatureSync.StatRefused;
            CreatureSync.HandlePose(CrLeader, CreatureSync.BuildPose(epoch, ++seq, new List<CreatureSync.Pose> { CrPose(gaId, 2, want, Vector3.zero) }), true, false, Vector3.zero, false);
            bool raceIgnores = CreatureSync.StatRefused > refusedR && CreatureSync.Records.Count == 0;
            Check("CREATURE-CLAIM-GUEST", "as a guest: a creature I hold is moved by me, not steered to the leader's pose, and its pose goes to the leader marked held",
                steerOff && toldLeader >= 2 && heldFlag, (steerOff ? "not steered" : "STEERED") + ", " + toldLeader + " poses to the leader" + (heldFlag ? " marked held" : " NOT marked held"));
            Check("CREATURE-RELEASE", "in a race (or after leaving the party) every creature has its own brain again, the switched-off ones are back, none of my scripts stays on them, and the leader's poses are ignored",
                stillOff == 0 && agents == 0 && spareBack && raceIgnores, stillOff + " brains still off, " + agents + " creatures with my script, spare " + (spareBack ? "back" : "STILL OFF") + ", poses " + (raceIgnores ? "ignored" : "USED"));
            CreatureSync.Release("audit");
            CreatureSync.TestMode = false;
        }
    }
}
