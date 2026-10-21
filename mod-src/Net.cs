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
            w.Write(f);
            w.Write(skin);
            w.Write(jumpScale);
            w.Write((byte)held.Length);
            w.Write((ushort)vis.Length); w.Write(vis);
            foreach (var h in held) { w.Write(h.id); W(w, h.pos); W(w, h.vel); w.Write(h.rot.x); w.Write(h.rot.y); w.Write(h.rot.z); w.Write(h.rot.w); }
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
                s.held = new Held[n];
                for (int i = 0; i < n; i++) { s.held[i].id = r.ReadInt32(); s.held[i].pos = R(r); s.held[i].vel = R(r); s.held[i].rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
                if (!Sane(s.pos, MaxCoord) || !Sane(s.vel, MaxSpeed) || !Sane(s.camForward, 1000f) || !Sane(s.move.x, 100f) || !Sane(s.move.y, 100f) || !Sane(s.jumpScale, 10f)) return null;
                foreach (var h in s.held)
                    if (!Sane(h.pos, MaxCoord) || !Sane(h.vel, MaxSpeed) || !Sane(h.rot.x, 10f) || !Sane(h.rot.y, 10f) || !Sane(h.rot.z, 10f) || !Sane(h.rot.w, 10f)) return null;
                return s;
            }
            catch (Exception) { return null; }
        }

        static void W(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        static Vector3 R(BinaryReader r) { return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
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
