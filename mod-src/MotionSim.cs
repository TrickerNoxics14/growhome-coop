using System;
using System.Collections.Generic;
using UnityEngine;

namespace CoopMod
{
    // The movement self test without game physics: a scripted true path is sent as real pose packets over a fake link (ping, jitter, loss,
    // gaps, reordering, duplicates, a restart) and played back on the receiving side through the real PoseOrder, PoseBuffer and Follower,
    // 60 frames a second. Each frame is measured against the truth.
    public static class MotionSim
    {
        public class Path
        {
            public string Name;
            public float T;
            public Vector3[] Pos, Vel;
            public byte[] Flags, Tp;
            public float TeleportAt = -1f;
            public int Teleports;
            public int I(double t) { int i = (int)(t * 1000.0); return i < 0 ? 0 : (i >= Pos.Length ? Pos.Length - 1 : i); }
        }

        public class Link
        {
            public string Name;
            public float Ping = 0.02f, Jitter, Loss, Dup;
            public float BurstEvery, BurstLen;      // a loss burst of BurstLen every BurstEvery seconds
            public float GapFrom = -1f, GapTo = -1f; // the sender sends nothing (a hitch on their PC)
            public float RestartAt = -1f;            // the sender restarts its game (new session, numbers and clock from the start)
            public float FrameDt = 1f / 60f;         // my frame time
        }

        public class Result
        {
            public string Name;
            public float ErrAvg, ErrMax, WorstJump, WorstJumpAllowed, LatencyMax, CatchErr;
            public int Frames, Scripted, Unscripted, Frozen, Resets, Restarts;
            public bool Ok;
            public string Why = "";
            public override string ToString()
            {
                return Name + ": err avg " + ErrAvg.ToString("F2") + " max " + ErrMax.ToString("F2") + " m, worst frame step " + WorstJump.ToString("F2") + "/" + WorstJumpAllowed.ToString("F2")
                    + " m, latency " + (LatencyMax * 1000f).ToString("F0") + " ms, snaps " + Scripted + "+" + Unscripted + ", frozen " + Frozen + (Why.Length > 0 ? " [" + Why + "]" : "");
            }
        }

        const float Dt = 0.001f;

        static Path New(string name, float T)
        {
            int n = (int)(T / Dt) + 1;
            return new Path { Name = name, T = T, Pos = new Vector3[n], Vel = new Vector3[n], Flags = new byte[n], Tp = new byte[n] };
        }

        public static List<Path> Paths()
        {
            var list = new List<Path>();
            // a straight run at 8 m/s
            var run = New("run", 7f);
            for (int i = 0; i < run.Pos.Length; i++) { run.Vel[i] = new Vector3(8f, 0f, 0f); run.Pos[i] = new Vector3(8f * i * Dt, 0f, 0f); run.Flags[i] = MotionWire.Grounded; }
            list.Add(run);
            // running in a circle (6 m, 8 m/s)
            var circle = New("run circle", 6f);
            float w = 8f / 6f;
            for (int i = 0; i < circle.Pos.Length; i++)
            {
                float t = i * Dt;
                circle.Pos[i] = new Vector3(6f * Mathf.Cos(w * t), 0f, 6f * Mathf.Sin(w * t));
                circle.Vel[i] = new Vector3(-8f * Mathf.Sin(w * t), 0f, 8f * Mathf.Cos(w * t));
                circle.Flags[i] = MotionWire.Grounded;
            }
            list.Add(circle);
            // start and stop: 1 s running, 1 s standing
            var ss = New("start/stop", 6f);
            { float v = 0f, x = 0f; for (int i = 0; i < ss.Pos.Length; i++) { float t = i * Dt; float want = (t % 2f) < 1f ? 8f : 0f; v = Mathf.MoveTowards(v, want, 30f * Dt); x += v * Dt; ss.Pos[i] = new Vector3(x, 0f, 0f); ss.Vel[i] = new Vector3(v, 0f, 0f); ss.Flags[i] = MotionWire.Grounded; } }
            list.Add(ss);
            // jump arcs while running (10 m/s up, 25 m/s2 down)
            var jump = New("jump arcs", 6f);
            { float y = 0f, vy = 0f, last = -9f; for (int i = 0; i < jump.Pos.Length; i++) { float t = i * Dt; if (y <= 0f && t - last > 1.5f) { vy = 10f; last = t; } if (vy != 0f || y > 0f) { vy -= 25f * Dt; y += vy * Dt; if (y <= 0f) { y = 0f; vy = 0f; } } jump.Pos[i] = new Vector3(6f * t, y, 0f); jump.Vel[i] = new Vector3(6f, vy, 0f); jump.Flags[i] = y > 0f ? MotionWire.InAir : MotionWire.Grounded; } }
            list.Add(jump);
            // a 30 m fall: walks off a ledge, falls (15 m/s2), lands and stands
            var fall = New("30 m fall", 5f);
            { float y = 30f, vy = 0f, x = 0f, vx = 4f; for (int i = 0; i < fall.Pos.Length; i++) { float t = i * Dt; if (t > 1f && y > 0f) { vy -= 15f * Dt; y += vy * Dt; if (y <= 0f) { y = 0f; vy = 0f; vx = 0f; } } x += vx * Dt; fall.Pos[i] = new Vector3(x, y, 0f); fall.Vel[i] = new Vector3(vx, vy, 0f); fall.Flags[i] = (t > 1f && y > 0f) ? MotionWire.InAir : MotionWire.Grounded; } }
            list.Add(fall);
            // climbing straight up (2 m/s) with a little sideways sway
            var climb = New("climb", 5f);
            for (int i = 0; i < climb.Pos.Length; i++) { float t = i * Dt; climb.Pos[i] = new Vector3(0.5f * Mathf.Sin(2f * t), 2f * t, 0f); climb.Vel[i] = new Vector3(Mathf.Cos(2f * t), 2f, 0f); climb.Flags[i] = MotionWire.Climbing; }
            list.Add(climb);
            // runs, dies (gone for 1 s), respawns 45 m away with the teleport mark, runs on
            var rs = New("respawn teleport", 5.5f);
            rs.TeleportAt = 3f; rs.Teleports = 1;
            for (int i = 0; i < rs.Pos.Length; i++)
            {
                float t = i * Dt;
                if (t < 2f) { rs.Pos[i] = new Vector3(6f * t, 0f, 0f); rs.Vel[i] = new Vector3(6f, 0f, 0f); rs.Flags[i] = MotionWire.Grounded; }
                else if (t < 3f) { rs.Pos[i] = new Vector3(12f, 0f, 0f); rs.Vel[i] = Vector3.zero; rs.Flags[i] = MotionWire.Gone; }
                else { rs.Pos[i] = new Vector3(40f, 5f, 20f + 6f * (t - 3f)); rs.Vel[i] = new Vector3(0f, 0f, 6f); rs.Flags[i] = MotionWire.Grounded; rs.Tp[i] = 1; }
            }
            list.Add(rs);
            return list;
        }

        public static List<Link> Links()
        {
            return new List<Link>
            {
                new Link { Name = "perfect", Ping = 0.02f },
                new Link { Name = "150 ms +-50 jitter", Ping = 0.15f, Jitter = 0.05f },
                new Link { Name = "5% loss", Ping = 0.05f, Jitter = 0.01f, Loss = 0.05f },
                new Link { Name = "300 ms loss bursts", Ping = 0.05f, BurstEvery = 1.5f, BurstLen = 0.3f },
                new Link { Name = "reorder + duplicates", Ping = 0.06f, Jitter = 0.04f, Dup = 0.1f },
                new Link { Name = "30 fps, 100 ms +-30 jitter", Ping = 0.1f, Jitter = 0.03f, FrameDt = 1f / 30f },
            };
        }

        struct Packet { public double at; public byte[] data; }

        // One path over one link. The receiver's clock is 1000 s ahead of the sender's (they never agree).
        public static Result Run(Path path, Link link, int seed)
        {
            var rnd = new System.Random(seed);
            var res = new Result { Name = path.Name + " / " + link.Name };
            var queue = new List<Packet>();
            const double RecvAhead = 1000.0;
            uint sessionA = 1111u, sessionB = 2222u, seq = 0;
            double next = 0;
            bool newRun = false;
            float maxOneWay = link.Ping + link.Jitter + 0.04f;
            // the sender: 30 Hz, 5 Hz while gone
            while (next <= path.T)
            {
                double t = next;
                int i = path.I(t);
                bool gone = (path.Flags[i] & MotionWire.Gone) != 0;
                next += gone ? 0.2 : 1.0 / 30.0;
                if (link.GapFrom >= 0f && t >= link.GapFrom && t < link.GapTo) continue;
                bool second = link.RestartAt >= 0f && t >= link.RestartAt;
                if (second && !newRun) { seq = 0; newRun = true; }   // a new run counts from 1 again
                double senderClock = second ? t - link.RestartAt + 7.0 : t;
                var s = new PlayerState
                {
                    seq = ++seq, pos = path.Pos[i], vel = path.Vel[i], camForward = Vector3.forward,
                    hasMotion = true, session = second ? sessionB : sessionA, sendMs = (uint)(senderClock * 1000.0), mflags = path.Flags[i], tp = path.Tp[i],
                };
                byte[] bytes = s.Write();
                if (link.Loss > 0f && rnd.NextDouble() < link.Loss) continue;
                if (link.BurstEvery > 0f && (t % link.BurstEvery) > link.BurstEvery - link.BurstLen) continue;
                double oneWay = link.Ping + (link.Jitter > 0f ? (rnd.NextDouble() * 2.0 - 1.0) * link.Jitter : 0.0);
                queue.Add(new Packet { at = t + oneWay, data = bytes });
                if (link.Dup > 0f && rnd.NextDouble() < link.Dup) queue.Add(new Packet { at = t + oneWay + rnd.NextDouble() * 0.03, data = bytes });
            }
            queue.Sort((a, b) => a.at.CompareTo(b.at));

            var peer = new Peer { Id = 99UL, Number = 2 };
            var buf = new PoseBuffer();
            var f = new Follower();
            int qi = 0;
            float dt = link.FrameDt;
            double errSum = 0; int errN = 0;
            Vector3 prev = Vector3.zero; bool havePrev = false;
            float worstMargin = float.MinValue;
            for (double now = 0; now <= path.T; now += dt)
            {
                while (qi < queue.Count && queue[qi].at <= now)
                {
                    var s = PlayerState.Read(queue[qi].data); qi++;
                    if (s == null) { res.Why += "a packet did not read back; "; continue; }
                    bool restarted;
                    PoseOrder.Newest(peer, s, out restarted);
                    if (restarted) { buf.Reset(); res.Restarts++; }
                    buf.Add(s.session, s.seq, s.sendMs / 1000.0, s.pos, s.vel, s.mflags, s.tp, now + RecvAhead);
                }
                var o = buf.Sample(now + RecvAhead, dt);
                if (!o.valid) continue;
                bool snapped = f.Step(o, dt);
                if (snapped) { if (o.teleport) res.Scripted++; else res.Unscripted++; }
                double truthT = buf.Session == sessionB ? o.renderT - 7.0 + link.RestartAt : o.renderT;
                int ti = path.I(truthT);
                Vector3 truth = path.Pos[ti];
                float speed = path.Vel[ti].magnitude;
                float step = havePrev ? (f.Pos - prev).magnitude : 0f;
                prev = f.Pos; havePrev = true;
                if (now < 0.5) continue;                                     // the first packets: the link is still being measured
                bool nearTp = path.TeleportAt >= 0f && truthT > path.TeleportAt - 0.05 && truthT < path.TeleportAt + 0.3;
                bool inGap = link.GapFrom >= 0f && truthT > link.GapFrom - 0.3 && truthT < link.GapTo + 1.5;
                bool nearRestart = link.RestartAt >= 0f && now > link.RestartAt - 0.1 && now < link.RestartAt + 0.6;
                bool gone = (path.Flags[ti] & MotionWire.Gone) != 0 || (path.Flags[path.I(truthT + 0.1)] & MotionWire.Gone) != 0;   // (dying stops them at once)
                res.Frames++;
                float err = (f.Pos - truth).magnitude;
                if (!nearTp && !inGap && !nearRestart) { errSum += err; errN++; if (err > res.ErrMax) res.ErrMax = err; }
                if (link.GapFrom >= 0f && truthT > link.GapTo + 1.5 && err > res.CatchErr) res.CatchErr = err;
                float allowed = inGap ? 1.0f : 2f * Mathf.Max(speed, o.vel.magnitude) * dt + 0.1f;
                if (!(snapped && o.teleport) && step - allowed > worstMargin) { worstMargin = step - allowed; res.WorstJump = step; res.WorstJumpAllowed = allowed; }
                if (!inGap && !gone && !nearTp && speed > 2f && step < 0.25f * speed * dt) { res.Frozen++; if (res.Frozen == 1) res.Why += "first still frame at " + truthT.ToString("F2") + " s (step " + step.ToString("F3") + " m); "; }
                float latency = (float)(now - truthT);
                if (!inGap && !nearRestart && latency > res.LatencyMax) res.LatencyMax = latency;
                if (latency > maxOneWay + PoseBuffer.MaxDelay + 0.06f && !inGap && !nearRestart) res.Why = "shown " + (latency * 1000f).ToString("F0") + " ms late; ";
            }
            res.ErrAvg = errN > 0 ? (float)(errSum / errN) : 99f;
            res.Resets = buf.Resets;
            if (res.ErrAvg >= 0.3f) res.Why += "average error; ";
            if (res.ErrMax >= 1.5f) res.Why += "worst error; ";
            if (res.WorstJump > res.WorstJumpAllowed) res.Why += "a frame jumped; ";
            if (res.Unscripted > 0) res.Why += "jumped to the path without a teleport; ";
            if (res.Scripted != path.Teleports) res.Why += res.Scripted + " teleports for " + path.Teleports + " scripted; ";
            if (res.Frozen > 0) res.Why += "stood still while they moved; ";
            if (link.GapFrom >= 0f && res.CatchErr >= 0.5f) res.Why += "did not catch up after the gap; ";
            if (link.RestartAt >= 0f && res.Restarts != 1) res.Why += res.Restarts + " restarts seen; ";
            res.Ok = res.Why.Length == 0;
            return res;
        }

        // Every path over every link, plus a 2 s gap and a restart on the runs.
        public static List<Result> RunAll()
        {
            var results = new List<Result>();
            var paths = Paths();
            int seed = 7;
            foreach (var p in paths) foreach (var l in Links()) results.Add(Run(p, l, seed++));
            foreach (var p in paths)
            {
                if (p.Name != "run" && p.Name != "run circle") continue;
                results.Add(Run(p, new Link { Name = "2 s gap", Ping = 0.05f, Jitter = 0.01f, GapFrom = 2f, GapTo = 4f }, seed++));
                results.Add(Run(p, new Link { Name = "restart", Ping = 0.05f, Jitter = 0.01f, RestartAt = 3f }, seed++));
            }
            return results;
        }
    }
}
