using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace CoopMod
{
    // One player's state as sent over the wire.
    public class PlayerState
    {
        public uint seq;
        public Vector3 pos, vel, camForward;
        public Vector2 move;
        public bool jumpDown, jumpUp, leftGrab, rightGrab;
        public byte skin;
        public float jumpScale;
        public struct Held { public int id; public Vector3 pos, vel; public Quaternion rot; }
        public Held[] held = new Held[0];
        public byte[] vis = new byte[0];   // which parts of the sender's BUD are active/enabled (see CoopBehaviour.CollectVisibility)
        // jetpack flame and the flower/leaf in the hand (visual only, see RemoteFx)
        public bool jet;                   // flag bit 16: the jetpack is firing
        public byte fly;                   // 0 nothing, 1 leaf/glider (bit 64), 2 flower/parachute (bit 32)
        public Vector3 flyOffset;          // the flower/leaf's position minus the body position
        public Quaternion flyRot = Quaternion.identity;
        public byte petals, wear;          // petals left on the flower; how worn the leaf is
        // one-off things that happened to the sender (a voice line, a hard landing, a death), flag bit 128. Each one is sent in
        // every packet for half a second (packets can get lost) and the receiver plays it once, by its number (see FxOutbox).
        public FxEvent[] events = new FxEvent[0];
        // the motion block (see MotionWire): sent in the visibility slot, which old readers read and never use
        public bool hasMotion;
        public uint session, sendMs;       // this run of the sender's game; the sender's clock when it was sent (ms)
        public byte mflags, tp;            // MotionWire flags; counts the sender's teleports (a respawn, a teleporter)

        public byte[] Write()
        {
            var ms = new MemoryStream(320);
            var w = new BinaryWriter(ms);
            w.Write((byte)0xC0); w.Write(seq);
            W(w, pos); W(w, vel); W(w, camForward);
            w.Write(move.x); w.Write(move.y);
            byte f = 0;
            if (jumpDown) f |= 1;
            if (jumpUp) f |= 2;
            if (leftGrab) f |= 4;
            if (rightGrab) f |= 8;
            if (jet) f |= 16;
            if (fly == 2) f |= 32;
            if (fly == 1) f |= 64;
            int ne = events == null ? 0 : Math.Min(events.Length, FxEvent.MaxPerPacket);
            if (ne > 0) f |= 128;
            w.Write(f);
            w.Write(skin);
            w.Write(jumpScale);
            w.Write((byte)held.Length);
            byte[] slot = hasMotion ? MotionWire.Write(session, sendMs, mflags, tp) : vis;
            w.Write((ushort)slot.Length); w.Write(slot);
            foreach (var h in held) { w.Write(h.id); W(w, h.pos); W(w, h.vel); w.Write(h.rot.x); w.Write(h.rot.y); w.Write(h.rot.z); w.Write(h.rot.w); }
            // the flower/leaf bits always come with these 30 bytes (an old reader never looks past the held list)
            if (fly == 1 || fly == 2) { W(w, flyOffset); w.Write(flyRot.x); w.Write(flyRot.y); w.Write(flyRot.z); w.Write(flyRot.w); w.Write(petals); w.Write(wear); }
            // the events come last, so an old reader (which stops after the flower/leaf bytes) never sees them
            if (ne > 0)
            {
                w.Write((byte)ne);
                for (int i = events.Length - ne; i < events.Length; i++) events[i].Write(w);
            }
            return ms.ToArray();
        }

        // Everything a pose packet says comes from another player's PC, so it is checked before anything uses it: a short or damaged packet, a number that is not
        // a number, a position far outside the world or a speed no BUD can reach is thrown away (null) instead of moving anybody's character or stopping the network loop.
        const float MaxCoord = 50000f, MaxSpeed = 400f;
        static bool Sane(Vector3 v, float limit) { return Sane(v.x, limit) && Sane(v.y, limit) && Sane(v.z, limit); }
        static bool Sane(float f, float limit) { return !float.IsNaN(f) && !float.IsInfinity(f) && Math.Abs(f) <= limit; }

        public static PlayerState Read(byte[] data)
        {
            if (data == null || data.Length < 52 || data.Length > 20000 || data[0] != 0xC0) return null;
            try
            {
                var r = new BinaryReader(new MemoryStream(data)); r.ReadByte();
                var s = new PlayerState();
                s.seq = r.ReadUInt32();
                s.pos = R(r); s.vel = R(r); s.camForward = R(r);
                s.move = new Vector2(r.ReadSingle(), r.ReadSingle());
                byte f = r.ReadByte();
                s.jumpDown = (f & 1) != 0; s.jumpUp = (f & 2) != 0; s.leftGrab = (f & 4) != 0; s.rightGrab = (f & 8) != 0;
                s.skin = r.ReadByte();
                s.jumpScale = r.ReadSingle();
                int n = r.ReadByte();
                int vl = r.ReadUInt16();
                if (vl > 4096 || r.BaseStream.Position + vl > data.Length) return null;
                s.vis = r.ReadBytes(vl);
                if (MotionWire.Read(s.vis, s) < 0) return null;
                s.held = new Held[n];
                for (int i = 0; i < n; i++) { s.held[i].id = r.ReadInt32(); s.held[i].pos = R(r); s.held[i].vel = R(r); s.held[i].rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
                s.jet = (f & 16) != 0;
                bool flower = (f & 32) != 0, leaf = (f & 64) != 0;
                if (flower && leaf) return null;
                if (flower || leaf)
                {
                    // a set bit without its 30 bytes is a cut-off packet: the read below runs past the end and ends in the catch
                    s.fly = (byte)(leaf ? 1 : 2);
                    s.flyOffset = R(r);
                    s.flyRot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    s.petals = r.ReadByte(); s.wear = r.ReadByte();
                    if (!Sane(s.flyOffset, 10f) || !Sane(s.flyRot.x, 10f) || !Sane(s.flyRot.y, 10f) || !Sane(s.flyRot.z, 10f) || !Sane(s.flyRot.w, 10f) || s.petals > 64) return null;
                }
                if ((f & 128) != 0)
                {
                    // events: a count of 1-4, each one checked. A cut-off list runs past the end (the catch). A bad event is dropped on its own,
                    // an unknown kind (a newer version) ends the list, and bytes behind the list are left for a newer reader: the pose always stays.
                    int ne = r.ReadByte();
                    var list = new System.Collections.Generic.List<FxEvent>();
                    if (ne >= 1 && ne <= FxEvent.MaxPerPacket)
                        for (int i = 0; i < ne; i++)
                        {
                            FxEvent e;
                            int got = FxEvent.Read(r, s.pos, out e);
                            if (got == FxEvent.ReadUnknown) break;
                            if (got == FxEvent.ReadOk) list.Add(e);
                        }
                    s.events = list.ToArray();
                }
                if (!Sane(s.pos, MaxCoord) || !Sane(s.vel, MaxSpeed) || !Sane(s.camForward, 1000f) || !Sane(s.move.x, 100f) || !Sane(s.move.y, 100f) || !Sane(s.jumpScale, 10f)) return null;
                foreach (var h in s.held)
                    if (!Sane(h.pos, MaxCoord) || !Sane(h.vel, MaxSpeed) || !Sane(h.rot.x, 10f) || !Sane(h.rot.y, 10f) || !Sane(h.rot.z, 10f) || !Sane(h.rot.w, 10f)) return null;
                return s;
            }
            catch (Exception) { return null; }
        }

        public static void W(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        public static Vector3 R(BinaryReader r) { return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
        public static bool SaneVec(Vector3 v, float limit) { return Sane(v, limit); }
    }

    // One thing that happened to a player, for the others to show on that player's copy.
    public sealed class FxEvent
    {
        public const byte Voice = 1, Grunt = 2, Lethal = 3, Death = 4;
        public const byte CauseImpact = 0, CauseWater = 1, CauseOther = 2;
        public const int MaxPerPacket = 4, MaxVoiceLen = 40;
        public const float MaxPointDist = 30f;   // a landing spot further than this from the sender's body is not theirs

        public ushort id;                        // counts up per sender, so the receiver plays each one once
        public byte kind;
        public string voice;                     // Voice: the game's sound event, e.g. "Voice.Jump"
        public Vector3 point, normal;            // Grunt/Lethal: where and how they hit the ground
        public byte cause;                       // Death: impact, water or something else

        // Only names like "Voice.Jump": the game's voice bank, letters and digits, nothing that could name another sound.
        public static bool IsVoicePath(string p)
        {
            if (p == null || p.Length < 7 || p.Length > MaxVoiceLen || !p.StartsWith("Voice.")) return false;
            for (int i = 6; i < p.Length; i++)
            {
                char c = p[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')) return false;
            }
            return true;
        }

        public void Write(BinaryWriter w)
        {
            w.Write(id); w.Write(kind);
            if (kind == Voice)
            {
                string v = IsVoicePath(voice) ? voice : "Voice.Jump";
                w.Write((byte)v.Length);
                for (int i = 0; i < v.Length; i++) w.Write((byte)v[i]);
            }
            else if (kind == Grunt || kind == Lethal) { PlayerState.W(w, point); PlayerState.W(w, normal); }
            else w.Write(cause);
        }

        // ReadOk: a good event. ReadBad: a known kind read to its end but with values that are not allowed (only this one is dropped).
        // ReadUnknown: a kind this version does not know, so its length is unknown too (the list stops here). A cut-off event throws.
        public const int ReadOk = 1, ReadBad = 0, ReadUnknown = -1;

        public static int Read(BinaryReader r, Vector3 senderPos, out FxEvent e)
        {
            e = new FxEvent();
            e.id = r.ReadUInt16(); e.kind = r.ReadByte();
            if (e.kind == Voice)
            {
                int n = r.ReadByte();
                byte[] b = r.ReadBytes(n);
                if (b.Length != n) throw new EndOfStreamException();
                if (n < 7 || n > MaxVoiceLen) return ReadBad;
                var c = new char[n];
                for (int i = 0; i < n; i++) c[i] = (char)b[i];
                e.voice = new string(c);
                return IsVoicePath(e.voice) ? ReadOk : ReadBad;
            }
            if (e.kind == Grunt || e.kind == Lethal)
            {
                e.point = PlayerState.R(r); e.normal = PlayerState.R(r);
                if (!PlayerState.SaneVec(e.point, 50000f) || !PlayerState.SaneVec(e.normal, 2f) || !PlayerState.SaneVec(senderPos, 50000f)) return ReadBad;
                float m = e.normal.magnitude;
                return m > 0.5f && m < 1.5f && (e.point - senderPos).magnitude <= MaxPointDist ? ReadOk : ReadBad;
            }
            if (e.kind == Death) { e.cause = r.ReadByte(); return e.cause <= CauseOther ? ReadOk : ReadBad; }
            return ReadUnknown;
        }
    }

    // Transport seam: the loopback test uses UDP on 127.0.0.1; the real build will swap in Steam networking.
    public interface ITransport
    {
        void Send(byte[] data);
        byte[] Receive(); // null when nothing is waiting
        void SendReliable(byte[] data);   // ordered, guaranteed, any size up to ~1 MB (world snapshots)
        byte[] ReceiveReliable();
        string Diagnostics();
        void Close();
    }

    public class UdpLoopbackTransport : ITransport
    {
        readonly UdpClient udp;
        readonly IPEndPoint peer;

        public UdpLoopbackTransport(int localPort, int peerPort)
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, localPort));
            peer = new IPEndPoint(IPAddress.Loopback, peerPort);
        }

        public void Send(byte[] data) { try { udp.Send(data, data.Length, peer); } catch (SocketException) { } }

        public byte[] Receive()
        {
            try
            {
                if (udp.Available <= 0) return null;
                IPEndPoint from = null;
                return udp.Receive(ref from);
            }
            catch (SocketException) { return null; }
        }

        // Loopback test transport has no separate reliable channel; UDP on localhost is good enough for small messages.
        public void SendReliable(byte[] data) { }
        public byte[] ReceiveReliable() { return null; }

        public string Diagnostics() { return "udp loopback"; }

        public void Close() { udp.Close(); }
    }
}
