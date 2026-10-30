using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace CoopMod
{
    // Movement of other players' copies (group "movement"): the motion block on the wire, the packet buffer, a restarted sender,
    // the scripted paths over a bad fake link, and live copies near and far from my camera.
    public partial class SelfTest
    {
        // The motion block of a pose packet: read back exactly, an old reader sees the old layout, never accepted cut off or damaged.
        void CheckMotionWire()
        {
            var pos = new Vector3(10, 4, -20);
            var held = new[] { new PlayerState.Held { id = 3, pos = pos, rot = Quaternion.identity } };
            var m = new PlayerState { seq = 31, pos = pos, vel = new Vector3(1, 2, 3), camForward = Vector3.forward, skin = 2, fly = 2, petals = 3, flyRot = Quaternion.identity, held = held,
                events = new[] { new FxEvent { id = 5, kind = FxEvent.Death, cause = FxEvent.CauseImpact } },
                hasMotion = true, session = 0xDEADBEEF, sendMs = 123456789u, mflags = (byte)(MotionWire.InAir | MotionWire.Climbing | MotionWire.Teleported), tp = 200 };
            byte[] bytes = m.Write();
            var b = PlayerState.Read(bytes);
            bool same = b != null && b.hasMotion && b.session == 0xDEADBEEF && b.sendMs == 123456789u && b.mflags == m.mflags && b.tp == 200
                && b.fly == 2 && b.petals == 3 && b.held.Length == 1 && b.events.Length == 1 && b.skin == 2 && (b.vel - m.vel).magnitude < 0.0001f;
            Check("MOVE-WIRE", "a pose packet with a motion block (and a flower, a held object and an event) reads back exactly", same, b == null ? "refused" : "session=" + b.session.ToString("X") + " ms=" + b.sendMs + " flags=" + b.mflags + " tp=" + b.tp);

            // an old reader: the block sits in the visibility slot (2-byte length, then the bytes), which it reads and never uses
            var plain = new PlayerState { seq = 31, pos = pos, vel = m.vel, camForward = Vector3.forward, skin = 2, fly = 2, petals = 3, flyRot = Quaternion.identity, held = held, events = m.events }.Write();
            int slotAt = 1 + 4 + 36 + 8 + 1 + 1 + 4 + 1;
            bool layout = bytes.Length == plain.Length + MotionWire.Size && BitConverter.ToUInt16(bytes, slotAt) == MotionWire.Size && BitConverter.ToUInt16(plain, slotAt) == 0;
            for (int i = 0; layout && i < plain.Length; i++)
            {
                int j = i < slotAt + 2 ? i : i + MotionWire.Size;
                if (i == slotAt || i == slotAt + 1) continue;
                if (bytes[j] != plain[i]) layout = false;
            }
            var pb = PlayerState.Read(plain);
            Check("MOVE-WIRE", "the block only fills the old visibility slot (an old reader reads the same pose), and a packet without it reads with none",
                layout && pb != null && !pb.hasMotion && pb.events.Length == 1, "layout=" + layout + ", plain read=" + (pb != null) + (pb != null ? " motion=" + pb.hasMotion : ""));

            int accepted = 0, escaped = 0;
            for (int cut = 0; cut < bytes.Length; cut++)
            {
                var c = new byte[cut]; Buffer.BlockCopy(bytes, 0, c, 0, cut);
                try { if (PlayerState.Read(c) != null) accepted++; } catch (Exception) { escaped++; }
            }
            Check("MOVE-WIRE", "every cut-off packet with a motion block is refused", accepted == 0 && escaped == 0, accepted + " accepted, " + escaped + " threw");

            // damaged blocks: built by hand into the slot
            Func<byte[], PlayerState> withSlot = slot => { var s = new PlayerState { seq = 32, pos = pos, camForward = Vector3.forward, vis = slot }; return PlayerState.Read(s.Write()); };
            var good = MotionWire.Write(77u, 1000u, MotionWire.Grounded, 1);
            var hostile = new Dictionary<string, byte[]>();
            var shortB = new byte[6]; Buffer.BlockCopy(good, 0, shortB, 0, 6); hostile["a block cut short"] = shortB;
            hostile["just the marker"] = new byte[] { MotionWire.Magic };
            var noSession = (byte[])good.Clone(); for (int i = 2; i < 6; i++) noSession[i] = 0; hostile["no session"] = noSession;
            var both = (byte[])good.Clone(); both[10] = (byte)(MotionWire.Grounded | MotionWire.InAir); hostile["standing and in the air at once"] = both;
            string leaked = "";
            foreach (var kv in hostile) { try { if (withSlot(kv.Value) != null) leaked += kv.Key + "; "; } catch (Exception) { leaked += kv.Key + " (threw); "; } }
            Check("MOVE-WIRE", "damaged motion blocks are refused with their packet", leaked.Length == 0, leaked.Length == 0 ? hostile.Count + " kinds tried" : "accepted: " + leaked);

            var newer = (byte[])good.Clone(); newer[1] = 9;
            var longer = new byte[MotionWire.Size + 5]; Buffer.BlockCopy(good, 0, longer, 0, MotionWire.Size);
            var unknownFlag = (byte[])good.Clone(); unknownFlag[10] |= 128;
            var other = new byte[] { 1, 2, 3 };
            var nb = withSlot(newer); var lb = withSlot(longer); var ub = withSlot(unknownFlag); var ob = withSlot(other);
            Check("MOVE-WIRE", "a newer block, a longer block, an unknown flag or other bytes in the slot keep the pose (what this version knows is used)",
                nb != null && !nb.hasMotion && lb != null && lb.hasMotion && lb.session == 77u && ub != null && ub.hasMotion && ub.mflags == MotionWire.Grounded && ob != null && !ob.hasMotion,
                "newer=" + (nb == null ? "refused" : "motion " + nb.hasMotion) + ", longer=" + (lb == null ? "refused" : "ok") + ", flag=" + (ub == null ? "refused" : ub.mflags.ToString()) + ", other=" + (ob == null ? "refused" : "ok"));

            // the sender side: a dead player's packets say gone; a respawn or teleporter counts as a teleport, running and falling do not
            var last = new PlayerState { seq = 40, pos = pos, camForward = Vector3.forward, skin = 1 };
            var gone = CoopBehaviour.PoseWhileGone(last, 41, new FxEvent[0], true);
            var goneBack = gone != null ? PlayerState.Read(gone.Write()) : null;
            bool quiet = CoopBehaviour.PoseWhileGone(last, 42, new FxEvent[0], false) == null;
            Check("MOVE-WIRE", "while my walkster is gone (in a world) my pose still goes out, marked gone and standing still",
                goneBack != null && goneBack.hasMotion && (goneBack.mflags & MotionWire.Gone) != 0 && goneBack.vel == Vector3.zero && goneBack.session == CoopBehaviour.MotionSession && quiet,
                goneBack == null ? "nothing sent" : "flags=" + goneBack.mflags + " session ok=" + (goneBack.session == CoopBehaviour.MotionSession) + ", without heartbeat nothing=" + quiet);
            bool run = !CoopBehaviour.TeleportedSince(Vector3.zero, new Vector3(0.3f, 0, 0), new Vector3(8, 0, 0), 0.033f, true, false);
            bool fall = !CoopBehaviour.TeleportedSince(Vector3.zero, new Vector3(0, -1.2f, 0), new Vector3(0, -35f, 0), 0.033f, true, false);
            bool hitch = !CoopBehaviour.TeleportedSince(Vector3.zero, new Vector3(8f, 0, 0), new Vector3(8, 0, 0), 1f, true, false);
            bool tele = CoopBehaviour.TeleportedSince(Vector3.zero, new Vector3(40f, 5f, 0), new Vector3(0, 0, 0), 0.033f, true, false);
            bool back = CoopBehaviour.TeleportedSince(Vector3.zero, Vector3.zero, Vector3.zero, 0.033f, true, true);
            bool first = !CoopBehaviour.TeleportedSince(Vector3.zero, new Vector3(500f, 0, 0), Vector3.zero, 0.033f, false, false);
            Check("MOVE-WIRE", "the sender marks a teleport for a respawn or a 40 m jump, not for running, a fast fall, a 1 s hitch or its first packet",
                run && fall && hitch && tele && back && first, "run=" + run + " fall=" + fall + " hitch=" + hitch + " teleport=" + tele + " back from gone=" + back + " first=" + first);
        }

        static PoseBuffer FilledBuffer(int n, double offset, float ping)
        {
            var b = new PoseBuffer();
            for (int i = 0; i < n; i++) { double t = i / 30.0; b.Add(5u, (uint)(i + 1), t, new Vector3(8f * (float)t, 0, 0), new Vector3(8, 0, 0), MotionWire.Grounded, 0, t + offset + ping); }
            return b;
        }

        // The packet buffer alone, without the game: ordering, repeats, old packets, the guess-ahead cap, a restart, the clock and the delay.
        void CheckMotionBuffer()
        {
            var b = new PoseBuffer();
            var order = new[] { 3, 1, 5, 2, 4, 8, 6, 7, 10, 9 };
            foreach (int i in order) b.Add(5u, (uint)i, i / 30.0, new Vector3(i, 0, 0), Vector3.right * 30f, MotionWire.Grounded, 0, 100.0 + i / 30.0);
            bool sorted = b.Count == 10 && b.Sorted && Math.Abs(b.TimeAt(0) - 1 / 30.0) < 1e-6 && Math.Abs(b.TimeAt(9) - 10 / 30.0) < 1e-6;
            Check("MOVE-BUFFER", "packets that arrive out of order are kept in send order", sorted, "count=" + b.Count + " sorted=" + b.Sorted);
            int dup0 = b.Duplicates;
            bool dupRefused = !b.Add(5u, 4u, 4 / 30.0, new Vector3(99, 0, 0), Vector3.zero, MotionWire.Grounded, 0, 101.0);
            Check("MOVE-BUFFER", "a repeated packet is dropped", dupRefused && b.Duplicates == dup0 + 1 && b.Count == 10, "dropped=" + dupRefused + " count=" + b.Count);

            var full = FilledBuffer(PoseBuffer.Capacity + 8, 50.0, 0.05f);
            bool oldRefused = !full.Add(5u, 9999u, 0.0, Vector3.zero, Vector3.zero, MotionWire.Grounded, 0, 60.0);
            Check("MOVE-BUFFER", "the buffer keeps at most " + PoseBuffer.Capacity + " packets and refuses one older than all of them", full.Count == PoseBuffer.Capacity && oldRefused && full.TooOld == 1, "count=" + full.Count + " old refused=" + oldRefused);

            // the clock: their clock is 50 s behind mine, packets take 80 ms (some up to 60 ms longer)
            var rnd = new System.Random(3);
            var c = new PoseBuffer();
            for (int i = 0; i < 90; i++) { double t = i / 30.0; c.Add(6u, (uint)(i + 1), t, Vector3.zero, Vector3.zero, MotionWire.Grounded, 0, t + 50.0 + 0.08 + (i % 4 == 0 ? 0.0 : rnd.NextDouble() * 0.06)); }
            Check("MOVE-BUFFER", "the clock difference is found from the fastest packets (50.08 s), within 10 ms", Math.Abs(c.Offset - 50.08) < 0.01, "found " + c.Offset.ToString("F4") + " s");
            Check("MOVE-BUFFER", "the delay grows with the jitter (up to 60 ms of jitter: more than 100 ms, at most 250 ms)", c.TargetDelay > 0.1f && c.TargetDelay <= PoseBuffer.MaxDelay, "delay " + (c.TargetDelay * 1000f).ToString("F0") + " ms, jitter " + (c.Jitter * 1000f).ToString("F0") + " ms");
            var p = FilledBuffer(90, 50.0, 0.03f);
            Check("MOVE-BUFFER", "on a steady link the delay is the smallest (80 ms)", Mathf.Abs(p.TargetDelay - PoseBuffer.MinDelay) < 0.005f, "delay " + (p.TargetDelay * 1000f).ToString("F0") + " ms");

            // the guess ahead: the last packet says 8 m/s, then nothing for 2 s
            var g = FilledBuffer(30, 50.0, 0.03f);
            var newest = g.Newest;
            double now = newest.t + 50.03;
            PoseOut o = new PoseOut();
            float worstAhead = 0f;
            for (int i = 0; i < 120; i++) { now += 1.0 / 60.0; o = g.Sample(now, 1f / 60f); float a = Vector3.Dot(o.pos - newest.pos, Vector3.right); if (a > worstAhead) worstAhead = a; }
            Check("MOVE-BUFFER", "with no news the path is guessed ahead at most 250 ms of their speed, then held still",
                worstAhead <= 8f * PoseBuffer.MaxAhead + 0.01f && o.vel.magnitude < 0.01f && o.extrapolating, "ahead " + worstAhead.ToString("F2") + " m (at most " + (8f * PoseBuffer.MaxAhead).ToString("F2") + "), speed at the end " + o.vel.magnitude.ToString("F2"));

            // the guess ahead in the air: a fall (15 m/s2) goes on falling as the last packets show, then slows to standing within 400 ms
            var fb = new PoseBuffer();
            for (int i = 0; i < 30; i++) { double t = i / 30.0; float vy = -15f * (float)t; fb.Add(5u, (uint)(i + 1), t, new Vector3(4f * (float)t, 30f - 7.5f * (float)(t * t), 0), new Vector3(4f, vy, 0), MotionWire.InAir, 0, t + 50.03); }
            var fNew = fb.Newest;
            double fnow = fNew.t + 50.03; float fallErr = 0f, fallAhead = 0f; PoseOut fo = new PoseOut();
            for (int i = 0; i < 120; i++)
            {
                fnow += 1.0 / 60.0; fo = fb.Sample(fnow, 1f / 60f);
                float e = (float)(fo.renderT - fNew.t);
                if (e > 0f && e <= PoseBuffer.MaxAhead) { var truth = fNew.pos + fNew.vel * e + new Vector3(0, -7.5f * e * e, 0); fallErr = Mathf.Max(fallErr, (fo.pos - truth).magnitude); }
                fallAhead = Mathf.Max(fallAhead, fo.ahead);
            }
            Check("MOVE-BUFFER", "in the air the guess ahead keeps falling as the last packets show (within 5 cm for 250 ms), then slows to standing: at most 400 ms ahead",
                fallErr < 0.05f && fallAhead <= PoseBuffer.MaxAhead + 0.15f + 0.001f && fo.vel.magnitude < 0.01f && fo.extrapolating, "error " + fallErr.ToString("F3") + " m, ahead at most " + (fallAhead * 1000f).ToString("F0") + " ms, speed at the end " + fo.vel.magnitude.ToString("F2"));

            // packets stop right as a fast fall lands (20 m/s, speeding up at the 40 m/s2 cap): the guess ahead stops at the ground under the copy
            // (a far copy has no physics to stop it), and only there: over a deep drop the fall goes on as before
            float noGround = LandingGuessLowest(null), deepDrop, onGround; int stops;
            float lastY = 100f + 18.6667f * (29f / 30f) - 20f * (29f / 30f) * (29f / 30f);
            deepDrop = LandingGuessLowest((Vector3 from, out float y) => { y = from.y - 50f; return true; }, out stops);
            bool deepSame = Mathf.Abs(deepDrop - noGround) < 0.001f && stops == 0;
            onGround = LandingGuessLowest((Vector3 from, out float y) => { y = from.y - 2f; return true; }, out stops);
            Check("MOVE-BUFFER", "a guessed fall stops at the ground under the copy when packets stop at a landing (never metres into it), and over a deep drop it falls on as before",
                lastY - noGround > 5f && onGround >= lastY - 1.5f - 0.001f && stops > 0 && deepSame,
                "lowest guess below the last packet: no ground known " + (lastY - noGround).ToString("F1") + " m, ground 2 m below " + (lastY - onGround).ToString("F2") + " m (stopped " + stops + "x), ground 50 m below " + (lastY - deepDrop).ToString("F1") + " m");

            // between packets the path is blended: halfway between two packets of a straight run is halfway
            var h = new PoseBuffer();
            h.Add(7u, 1u, 0.0, Vector3.zero, new Vector3(6, 0, 0), MotionWire.Grounded, 0, 10.0);
            h.Add(7u, 2u, 1.0 / 30.0, new Vector3(0.2f, 0, 0), new Vector3(6, 0, 0), MotionWire.Grounded, 0, 10.0 + 1.0 / 30.0);
            var mid = h.Sample(10.0 + 1.0 / 60.0 + h.Delay, 0f);
            Check("MOVE-BUFFER", "between two packets the copy is shown on the blended path", Mathf.Abs(mid.pos.x - 0.1f) < 0.01f && Mathf.Abs(mid.vel.x - 6f) < 0.1f, "x=" + mid.pos.x.ToString("F3") + " speed=" + mid.vel.x.ToString("F2"));

            // a teleport mark is shown once, when the path reaches it
            var tp = new PoseBuffer(); int marks = 0;
            for (int i = 0; i < 30; i++) tp.Add(8u, (uint)(i + 1), i / 30.0, i < 15 ? Vector3.zero : new Vector3(50, 0, 0), Vector3.zero, MotionWire.Grounded, (byte)(i < 15 ? 3 : 4), i / 30.0);
            for (int i = 0; i < 90; i++) if (tp.Sample(i / 60.0, 1f / 60f).teleport) marks++;
            Check("MOVE-BUFFER", "a teleport mark makes exactly one jump", marks == 1, marks + " jumps");
        }

        // A fall that speeds up at 40 m/s2 and is at 20 m/s in the last packet, then no packets for 2 s: the lowest height the guess shows.
        static float LandingGuessLowest(PoseBuffer.GroundProbe ground) { int s; return LandingGuessLowest(ground, out s); }
        static float LandingGuessLowest(PoseBuffer.GroundProbe ground, out int stops)
        {
            var b = new PoseBuffer(); b.Ground = ground;
            for (int i = 0; i < 30; i++)
            {
                double t = i / 30.0; float ft = (float)t;
                b.Add(6u, (uint)(i + 1), t, new Vector3(3f * ft, 100f + 18.6667f * ft - 20f * ft * ft, 0f), new Vector3(3f, 18.6667f - 40f * ft, 0f), MotionWire.InAir, 0, t + 50.03);
            }
            double now = b.Newest.t + 50.03; float lowest = float.MaxValue;
            for (int i = 0; i < 120; i++) { now += 1.0 / 60.0; var o = b.Sample(now, 1f / 60f); if (o.pos.y < lowest) lowest = o.pos.y; }
            stops = b.GroundStops;
            return lowest;
        }

        // A sender that restarts its game counts its packets from 1 again: the new run is taken at once, its first packet included.
        void CheckMotionSession()
        {
            var peer = new Peer { Id = 4243UL, Number = 9 };
            Func<uint, uint, PlayerState> pk = (session, seq) => PlayerState.Read(new PlayerState { seq = seq, pos = Vector3.zero, camForward = Vector3.forward, hasMotion = true, session = session, sendMs = seq * 33u, mflags = MotionWire.Grounded }.Write());
            bool r;
            bool a = PoseOrder.Newest(peer, pk(11u, 500u), out r) && !r;
            bool late = !PoseOrder.Newest(peer, pk(11u, 499u), out r);
            bool restartTaken = PoseOrder.Newest(peer, pk(12u, 1u), out r) && r;
            bool next = PoseOrder.Newest(peer, pk(12u, 2u), out r) && !r;
            Check("MOVE-SESSION", "a restarted sender (new session, numbers from 1) is taken at once; within a run an older packet is not the newest",
                a && late && restartTaken && next && peer.LastRecvSeq == 2u, "first=" + a + " late dropped=" + late + " restart taken=" + restartTaken + " next=" + next + " last=" + peer.LastRecvSeq);
            var b = FilledBuffer(20, 5.0, 0.02f);
            int resets = b.Resets;
            b.Add(9u, 1u, 0.5, Vector3.one, Vector3.zero, MotionWire.Grounded, 0, 100.0);
            Check("MOVE-SESSION", "a new session empties the path buffer and starts the clock again", b.Count == 1 && b.Resets == resets + 1 && Math.Abs(b.Offset - 99.5) < 0.001, "count=" + b.Count + " offset=" + b.Offset.ToString("F2"));
            var old = new Peer { Id = 4244UL, Number = 10 };
            bool oldA = PoseOrder.Newest(old, PlayerState.Read(new PlayerState { seq = 70, camForward = Vector3.forward }.Write()), out r);
            bool oldB = !PoseOrder.Newest(old, PlayerState.Read(new PlayerState { seq = 69, camForward = Vector3.forward }.Write()), out r);
            Check("MOVE-SESSION", "packets of an old version (no session) are still ordered by their number", oldA && oldB && old.MotionSession == 0u, "first=" + oldA + " older dropped=" + oldB);
        }

        // The scripted paths over the fake links (see MotionSim), measured frame by frame against the truth.
        void CheckMotionSim()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var results = MotionSim.RunAll();
            sw.Stop();
            foreach (var r in results)
                Check("MOVE-SIM", "a copy follows \"" + r.Name + "\" smoothly (error avg < 0.3 m, worst < 1.5 m, no frame jump, no stand-still, teleport only when marked)", r.Ok, r.ToString());
            Check("MOVE-SIM", "all " + results.Count + " scripted runs were played", results.Count >= 40, results.Count + " runs in " + sw.ElapsedMilliseconds + " ms");
        }

        // Live: a copy far behind my camera (the game used to freeze it there and the mod jumped it 14 m at a time) keeps running smoothly,
        // and a copy in front of me follows its path with one steering pull.
        IEnumerator TestMovementLive()
        {
            var host = UnityEngine.Object.FindObjectOfType(typeof(CoopBehaviour)) as CoopBehaviour;
            var me = GameObject.Find("Walkster");
            var meW = me != null ? me.GetComponentInChildren<BipedWalkScript>() : null;
            var cam = Camera.main;
            if (host == null || meW == null || cam == null)
            {
                string why = "mod=" + (host != null) + " walkster=" + (meW != null) + " camera=" + (cam != null);
                Check("MOVE-FAR", "the mod, my walkster and the camera exist to test with", false, why);
                Check("MOVE-NEAR", "the mod, my walkster and the camera exist to test with", false, why);
                yield break;
            }
            Vector3 fwd = cam.transform.forward; fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 0.01f ? fwd.normalized : Vector3.forward;
            Vector3 side = Vector3.Cross(Vector3.up, fwd).normalized;
            yield return StartCoroutine(RunCopy(host, "MOVE-FAR", meW.body.position - fwd * 40f, side, 6f, 4f, true));
            yield return StartCoroutine(RunCopy(host, "MOVE-NEAR", meW.body.position + fwd * 4f + Vector3.up * 0.3f, side, 2f, 2.5f, false));
        }

        IEnumerator RunCopy(CoopBehaviour host, string id, Vector3 start, Vector3 dir, float speed, float seconds, bool far)
        {
            var peer = new Peer { Id = far ? 7790UL : 7791UL, Number = far ? 5 : 6 };
            host.TestSpawnCopy(peer, start);
            if (peer.Clone == null || peer.Driver == null) { Check(id, "a copy can be made for the test", false, "no copy"); yield break; }
            var d = peer.Driver; d.useKeyboard = false;
            var cw = peer.Clone.GetComponentInChildren<BipedWalkScript>();
            uint session = far ? 5150u : 5151u, seq = 0;
            float t0 = Time.realtimeSinceStartup, nextPkt = 0f;
            int frames = 0, frozen = 0, farFrames = 0, measured = 0;
            int snaps0 = d.Snaps, wakes0 = d.Unfreezes;
            float errSum = 0f, errMax = 0f;
            Vector3 bodyStart = cw.body.position;
            Vector3 lastBody = bodyStart; int stillFrames = 0;
            string error = "";
            while (true)
            {
                float t = Time.realtimeSinceStartup - t0;
                if (t > seconds) break;
                while (nextPkt <= t)
                {
                    var s = new PlayerState { seq = ++seq, pos = start + dir * speed * nextPkt, vel = dir * speed, camForward = dir, hasMotion = true, session = session, sendMs = (uint)(nextPkt * 1000f), mflags = MotionWire.Grounded };
                    var back = PlayerState.Read(s.Write());
                    bool r;
                    if (back != null && PoseOrder.Newest(peer, back, out r)) d.Take(back, Vector3.zero);
                    nextPkt += 1f / 30f;
                }
                yield return null;
                if (peer.Clone == null || cw == null) { error = "the copy went away"; break; }
                frames++;
                if (cw.body.isKinematic || cw.m_distanceDisabled) frozen++;
                if (d.Far) farFrames++;
                if ((cw.body.position - lastBody).magnitude < 0.2f * speed * Time.unscaledDeltaTime && t > 0.5f) stillFrames++;
                lastBody = cw.body.position;
                if (t > 0.7f)
                {
                    float shownT = Time.realtimeSinceStartup - t0 - d.Buffer.Delay;
                    Vector3 truth = start + dir * speed * shownT;
                    Vector3 e = truth - cw.body.position; e.y = 0f;
                    errSum += e.magnitude; measured++; if (e.magnitude > errMax) errMax = e.magnitude;
                }
            }
            float moved = cw != null ? Vector3.Dot(cw.body.position - bodyStart, dir) : 0f;
            float expected = speed * (seconds - d.Buffer.Delay);
            float avg = measured > 0 ? errSum / measured : 99f;
            Sender(id + " scripted sender", 0u, peer.LastRecvSeq);           // the poses really flowed (a sender stuck at 0 makes the run hollow)
            string detail = error + "moved " + moved.ToString("F1") + " of " + expected.ToString("F1") + " m, frames " + frames + " (far " + farFrames + ", frozen " + frozen + ", still " + stillFrames + "), woken " + (d.Unfreezes - wakes0)
                + ", snaps " + (d.Snaps - snaps0) + (d.Snaps > snaps0 ? " (" + d.LastSnap + ")" : "") + ", error avg " + avg.ToString("F2") + " max " + errMax.ToString("F2") + " m, delay " + (d.Buffer.Delay * 1000f).ToString("F0") + " ms";
            if (far)
                Check(id, "a copy 40 m behind my camera keeps running along its path: never frozen, never still, no jumps",
                    error.Length == 0 && frames > 0 && frozen == 0 && farFrames >= frames * 0.8f && stillFrames <= 2 && moved > expected * 0.85f && d.Snaps == snaps0 && avg < 0.6f && errMax < 1.5f, detail);
            else
                Check(id, "a copy in front of me follows its path with the one steering pull: never frozen, no jumps, close to the path",
                    error.Length == 0 && frames > 0 && frozen == 0 && farFrames <= frames * 0.2f && d.Snaps == snaps0 && avg < 1.0f && errMax < 3.0f, detail);
            if (peer.Clone != null) Destroy(peer.Clone);
            peer.ClearClone();
            yield return null;
        }
    }
}
