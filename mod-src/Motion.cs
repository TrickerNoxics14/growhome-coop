using System;
using System.Collections.Generic;
using UnityEngine;

namespace CoopMod
{
    // The motion block of a pose packet (newer senders only): which run of the game sent it, when (the sender's clock), and what the player was doing.
    // It travels in the pose packet's old visibility slot. Old senders always left that slot empty and old readers read it and never use it,
    // so an old PC still reads the pose as before and a new PC reading an old packet simply finds no block.
    public static class MotionWire
    {
        public const byte Magic = 0x4D, Version = 1;
        public const int Size = 12;    // magic, version, session (4), send time in ms (4), flags, teleport counter
        public const byte Grounded = 1, Climbing = 2, Gone = 4, Teleported = 8, InAir = 16, Flying = 32;
        const byte Known = 63;

        public static byte[] Write(uint session, uint sendMs, byte flags, byte tp)
        {
            var b = new byte[Size];
            b[0] = Magic; b[1] = Version;
            Buffer.BlockCopy(BitConverter.GetBytes(session), 0, b, 2, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(sendMs), 0, b, 6, 4);
            b[10] = flags; b[11] = tp;
            return b;
        }

        // 1: a motion block, read into s. 0: no block (empty, not one of ours, or a newer version): the pose still counts.
        // -1: a damaged block (cut off, no session, standing and in the air at once): the whole packet is thrown away.
        public static int Read(byte[] b, PlayerState s)
        {
            if (b == null || b.Length == 0 || b[0] != Magic) return 0;
            if (b.Length < 2) return -1;
            if (b[1] != Version) return 0;
            if (b.Length < Size) return -1;
            uint session = BitConverter.ToUInt32(b, 2);
            if (session == 0) return -1;
            byte f = (byte)(b[10] & Known);   // flags a newer version adds are left alone
            if ((f & Grounded) != 0 && (f & InAir) != 0) return -1;
            s.hasMotion = true; s.session = session; s.sendMs = BitConverter.ToUInt32(b, 6); s.mflags = f; s.tp = b[11];
            return 1;
        }
    }

    // Which pose packets of a player count. A new session (they restarted the game) starts their packet numbers again, so the old
    // number is forgotten instead of dropping every packet of the new run. Only the newest packet moves the inputs; an older one only fills the path.
    public static class PoseOrder
    {
        public static bool Newest(Peer p, PlayerState s, out bool restarted)
        {
            restarted = false;
            if (s.hasMotion && s.session != p.MotionSession)
            {
                restarted = p.MotionSession != 0;
                p.MotionSession = s.session; p.LastRecvSeq = 0;
            }
            if (s.seq <= p.LastRecvSeq) return false;
            p.LastRecvSeq = s.seq;
            return true;
        }
    }

    public struct PoseSample { public double t; public uint seq; public Vector3 pos, vel; public byte flags, tp; }

    // Where a copy should be now: the blended path, and what happened on it.
    public struct PoseOut
    {
        public bool valid, teleport, extrapolating, gone;
        public Vector3 pos, vel;
        public byte flags;
        public float ahead;          // seconds guessed past the newest packet
        public double renderT;       // the sender's clock time shown
    }

    // The packets of one player, sorted by their send time. The copy is shown a little in the past ("now minus delay") and blended between the
    // two packets around that time, so packets that come late, in a bunch or out of order still give a smooth path. The delay adapts to the link
    // (80-250 ms). When no packet is new enough the path is guessed ahead for at most 250 ms and then held: it never runs away from real data.
    // In the air (the last two packets both in the air) the guess keeps their fall going for 250 ms and then slows to standing (at most 400 ms).
    public class PoseBuffer
    {
        public const int Capacity = 32;
        public const float MinDelay = 0.08f, MaxDelay = 0.25f, MaxAhead = 0.25f;
        const float FullFor = 0.1f, SlowDown = 0.3f;   // a guess ahead keeps full speed this long, then slows to standing (0.1 + 0.3 / 2 = MaxAhead)

        readonly List<PoseSample> list = new List<PoseSample>(Capacity + 1);
        public uint Session; bool hasSession;
        public int Resets, Duplicates, TooOld, Taken;
        double offset; bool offsetKnown;      // arrival minus send time: the smallest seen, let up slowly so a drifting clock is followed
        double lastArrival;
        float interval = 1f / 30f, jitter;
        double lag; bool lagKnown;            // the copy shows the sender's clock this far behind my clock (offset plus delay), changed slowly
        double lastRenderT = double.MinValue;
        byte lastTp; bool tpKnown;

        public int Count { get { return list.Count; } }
        public double Offset { get { return offset; } }
        public float Jitter { get { return jitter; } }
        public float Interval { get { return interval; } }
        public float TargetDelay { get { return Mathf.Clamp(2f * interval + jitter, MinDelay, MaxDelay); } }
        public float Delay { get { return lagKnown ? (float)(lag - offset) : TargetDelay; } }
        public PoseSample Newest { get { return list[list.Count - 1]; } }
        public double TimeAt(int i) { return list[i].t; }
        public bool Sorted { get { for (int i = 1; i < list.Count; i++) if (list[i].t < list[i - 1].t) return false; return true; } }

        public void Reset()
        {
            list.Clear(); offsetKnown = false; lagKnown = false; tpKnown = false; jitter = 0f; interval = 1f / 30f;
            lastRenderT = double.MinValue; lastArrival = 0; Resets++;
        }

        // session 0 is an old sender: its packets carry no send time, so the arrival time stands in for it
        public bool Add(uint session, uint seq, double sendT, Vector3 pos, Vector3 vel, byte flags, byte tp, double arrival)
        {
            if (!hasSession || session != Session) { if (hasSession) Reset(); Session = session; hasSession = true; }
            for (int i = 0; i < list.Count; i++) if (list[i].seq == seq) { Duplicates++; return false; }
            if (list.Count >= Capacity && sendT <= list[0].t) { TooOld++; return false; }
            double o = arrival - sendT;
            if (!offsetKnown || o < offset) { offset = o; offsetKnown = true; }
            else offset += Math.Min(o - offset, 0.5) * 0.005;
            float since = lastArrival > 0 ? (float)(arrival - lastArrival) : 0f;
            if (arrival > lastArrival) lastArrival = arrival;
            jitter = Mathf.Max(Mathf.Min((float)(o - offset), MaxDelay), jitter * Mathf.Exp(-Mathf.Max(0f, since) / 3f));
            // the send interval (not counted while they are gone: those come slower on purpose)
            if (list.Count > 0 && sendT > Newest.t && ((flags | Newest.flags) & MotionWire.Gone) == 0) interval += (Mathf.Clamp((float)(sendT - Newest.t), 0.01f, 0.3f) - interval) * 0.1f;
            var s = new PoseSample { t = sendT, seq = seq, pos = pos, vel = vel, flags = flags, tp = tp };
            int at = list.Count;
            while (at > 0 && list[at - 1].t > sendT) at--;
            list.Insert(at, s);
            if (list.Count > Capacity) list.RemoveAt(0);
            if (!lagKnown) { lag = offset + TargetDelay; lagKnown = true; }
            Taken++;
            return true;
        }

        public const float MaxAirAcc = 40f;            // a guessed fall never speeds up faster than this (m/s2)

        // The ground under a copy (a downward ray at the terrain), set by the copy's driver; null in pure checks. A guessed fall stops at it: a far copy
        // is moved without physics, so packets that stop right at a landing would otherwise push it metres into the ground until the next one comes.
        public delegate bool GroundProbe(Vector3 from, out float groundY);
        public GroundProbe Ground;
        public int GroundStops;                         // guesses held up by the ground (for the audit)
        uint groundSeq; bool groundKnown, groundHit; float groundY;
        bool GroundUnder(PoseSample s, out float y)
        {
            y = 0f;
            if (Ground == null) return false;
            if (!groundKnown || groundSeq != s.seq) { groundSeq = s.seq; groundKnown = true; groundHit = Ground(s.pos, out groundY); }   // one ray per newest packet
            y = groundY;
            return groundHit;
        }

        // the newest packet and the one before it were both in the air, close together: their speed change is the fall to guess ahead with
        bool InAirAcc(int idx, out Vector3 acc)
        {
            acc = Vector3.zero;
            if (idx < 1) return false;
            var b = list[idx]; var a = list[idx - 1];
            if ((b.flags & MotionWire.InAir) == 0 || (a.flags & MotionWire.InAir) == 0 || a.tp != b.tp) return false;
            float span = (float)(b.t - a.t);
            if (span < 0.005f || span > 0.15f) return false;
            acc = Vector3.ClampMagnitude((b.vel - a.vel) / span, MaxAirAcc);
            return true;
        }

        // The pose to show at my clock 'now'; dt is the time since the last call (the delay slides by at most a little each second).
        public PoseOut Sample(double now, float dt)
        {
            var o = new PoseOut();
            if (list.Count == 0) return o;
            double want = offset + TargetDelay;
            if (Math.Abs(want - lag) > 1.0) lag = want;   // their clock jumped: start over rather than crawl
            else if (dt > 0f)
            {
                double step = (want > lag ? 0.25 : 0.05) * Math.Min(dt, 0.25f);
                lag = want > lag ? Math.Min(want, lag + step) : Math.Max(want, lag - step);
            }
            double rt = now - lag;
            if (rt < lastRenderT) rt = lastRenderT;       // never back in time
            lastRenderT = rt;
            o.valid = true; o.renderT = rt;
            int idx = list.Count - 1;
            while (idx >= 0 && list[idx].t > rt) idx--;
            PoseSample shown;
            if (idx < 0) { shown = list[0]; o.pos = shown.pos; o.vel = Vector3.zero; }
            else if (idx == list.Count - 1)
            {
                shown = list[idx];
                float e = (float)(rt - shown.t);
                Vector3 acc;
                if ((shown.flags & MotionWire.Gone) != 0) { o.pos = shown.pos; o.vel = Vector3.zero; }
                else if (InAirAcc(idx, out acc))
                {
                    // in the air nobody stops at once: the fall (or jump arc) goes on as the last two packets show for MaxAhead, then slows to
                    // standing like a guess on the ground (at most MaxAhead + SlowDown / 2 = 400 ms ahead)
                    float g, k;
                    if (e < MaxAhead) { g = e; k = 1f; }
                    else if (e < MaxAhead + SlowDown) { float x = e - MaxAhead; g = MaxAhead + x - x * x / (2f * SlowDown); k = 1f - x / SlowDown; }
                    else { g = MaxAhead + SlowDown * 0.5f; k = 0f; }
                    o.pos = shown.pos + shown.vel * g + acc * (0.5f * g * g);
                    o.vel = (shown.vel + acc * g) * k;
                    float gy;
                    if (o.pos.y < shown.pos.y && GroundUnder(shown, out gy))
                    {
                        // never below the ground under the last packet: at most as close to it as that packet was (and never closer than 0.5 m above it)
                        float floor = gy + Mathf.Min(0.5f, Mathf.Max(0f, shown.pos.y - gy));
                        if (o.pos.y < floor) { o.pos.y = floor; if (o.vel.y < 0f) o.vel.y = 0f; GroundStops++; }
                    }
                    o.ahead = g; o.extrapolating = e > 0.001f;
                }
                else
                {
                    // full speed for the first 100 ms, then slowing to standing: never more than MaxAhead (250 ms) of their speed ahead
                    float g, k;
                    if (e < FullFor) { g = e; k = 1f; }
                    else if (e < FullFor + SlowDown) { float x = e - FullFor; g = FullFor + x - x * x / (2f * SlowDown); k = 1f - x / SlowDown; }
                    else { g = FullFor + SlowDown * 0.5f; k = 0f; }
                    o.pos = shown.pos + shown.vel * g;
                    o.vel = shown.vel * k;
                    o.ahead = g; o.extrapolating = e > 0.001f;
                }
            }
            else
            {
                var a = list[idx]; var b = list[idx + 1];
                shown = a;
                float span = (float)(b.t - a.t);
                float u = span > 0f ? Mathf.Clamp01((float)((rt - a.t) / span)) : 1f;
                if (a.tp != b.tp || (a.flags & MotionWire.Gone) != 0) { o.pos = a.pos; o.vel = Vector3.zero; }   // gone, or a teleport comes at b: hold until then
                else if (span <= 0.35f)
                {
                    float u2 = u * u, u3 = u2 * u;
                    o.pos = a.pos * (2f * u3 - 3f * u2 + 1f) + a.vel * (span * (u3 - 2f * u2 + u)) + b.pos * (3f * u2 - 2f * u3) + b.vel * (span * (u3 - u2));
                    o.vel = (a.pos * (6f * u2 - 6f * u) + b.pos * (6f * u - 6f * u2)) / span + a.vel * (3f * u2 - 4f * u + 1f) + b.vel * (3f * u2 - 2f * u);
                }
                else { o.pos = Vector3.Lerp(a.pos, b.pos, u); o.vel = (b.pos - a.pos) / span; }   // a long gap: a straight line, no curve guessed into it
                if (u >= 1f) shown = b;
            }
            o.flags = shown.flags;
            o.gone = (shown.flags & MotionWire.Gone) != 0;
            if (tpKnown && shown.tp != lastTp) o.teleport = true;
            lastTp = shown.tp; tpKnown = true;
            return o;
        }
    }

    // When a copy jumps straight to the path instead of moving along it: on the sender's teleport mark, or when it is far off (more than
    // 8 m, or a second of their speed) for half a second in a row.
    public class SnapRule
    {
        public const float MinDist = 8f, Persist = 0.5f;
        float bigFor;
        public bool Step(float err, float speed, bool teleport, float dt)
        {
            if (teleport) { bigFor = 0f; return true; }
            if (err > Mathf.Max(MinDist, speed)) bigFor += dt; else bigFor = 0f;
            if (bigFor < Persist) return false;
            bigFor = 0f;
            return true;
        }
    }

    // A copy moved straight along the path (far away or off screen: no physics to fight). The path's own speed is fed forward and the
    // remaining error shrinks smoothly; how fast it may close grows with the error, so a copy that fell behind catches up without a jump.
    public class Follower
    {
        public const float Gain = 8f;
        public Vector3 Pos, Vel;
        public bool Init;
        public int Snaps;
        public readonly SnapRule Rule = new SnapRule();

        // the speed that closes err along with the path's own speed
        public static Vector3 Want(Vector3 err, Vector3 pathVel, float gain)
        {
            float e = err.magnitude;
            float cap = 4f + pathVel.magnitude + Mathf.Max(0f, e - 3f) * 3f;
            return pathVel + Vector3.ClampMagnitude(err * gain, cap);
        }

        public void Reset(Vector3 pos, Vector3 vel) { Pos = pos; Vel = vel; Init = true; }

        // true when it jumped to the path this step
        public bool Step(PoseOut o, float dt)
        {
            if (!o.valid) return false;
            if (!Init) { Reset(o.pos, o.vel); return false; }
            Vector3 q = Pos + o.vel * dt;
            Vector3 err = o.pos - q;
            if (Rule.Step((o.pos - Pos).magnitude, o.vel.magnitude, o.teleport, dt)) { Reset(o.pos, o.vel); Snaps++; return true; }
            float e = err.magnitude;
            float cap = 4f + o.vel.magnitude + Mathf.Max(0f, e - 3f) * 3f;
            Vector3 corr = Vector3.ClampMagnitude(err * (1f - Mathf.Exp(-Gain * dt)), cap * dt);
            Vector3 next = q + corr;
            Vel = dt > 0f ? (next - Pos) / dt : o.vel;
            Pos = next;
            return false;
        }
    }
}
