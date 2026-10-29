using System;
using System.Collections.Generic;
using System.IO;

namespace CoopMod
{
    // The leader's world as several packed pieces. Steam sends one reliable message of about 1 MB at most and a grown world is bigger,
    // so it is packed (Lz) and cut into parts. Every number in a part comes from another PC and is checked before anything is made from it.
    public static class WorldTransfer
    {
        public const byte MsgPart = 9;
        public const int PartSize = 256 * 1024;
        public const int MaxParts = 128;               // 32 MB packed at most
        const int HeaderSize = 1 + 4 + 2 + 2 + 4 + 4 + 4 + 4;

        // epoch: which of the leader's worlds this is (a guest's changes count only while it plays in the world the leader plays now)
        public static List<byte[]> Split(byte[] world, int transfer, int epoch = 0)
        {
            byte[] packed = Lz.Pack(world);
            int parts = Math.Max(1, (packed.Length + PartSize - 1) / PartSize);
            if (parts > MaxParts) throw new InvalidOperationException("the world is too big to send (" + world.Length + " bytes)");
            uint sum = Lz.Checksum(world);
            var list = new List<byte[]>();
            for (int i = 0; i < parts; i++)
            {
                int at = i * PartSize, n = Math.Min(PartSize, packed.Length - at);
                var ms = new MemoryStream(); var w = new BinaryWriter(ms);
                w.Write(MsgPart); w.Write(transfer); w.Write((ushort)i); w.Write((ushort)parts); w.Write(world.Length); w.Write(sum); w.Write(packed.Length); w.Write(epoch);
                w.Write(packed, at, n);
                list.Add(ms.ToArray());
            }
            return list;
        }

        // Puts the parts of one sender's world back together. A part of a newer transfer starts over; anything that does not add up is refused.
        public class Assembly
        {
            int transfer = int.MinValue, parts, rawLen, packedLen, partEpoch; uint sum;
            byte[][] got; int have;
            public float LastPartAt = -999f;
            public int Have { get { return have; } }
            public int Parts { get { return parts; } }
            public int Epoch;   // of the world that came in last

            // the whole world when this part completes it, null otherwise (FormatException for a damaged part)
            public byte[] Add(byte[] m, float now)
            {
                if (m == null || m.Length < HeaderSize || m[0] != MsgPart) throw new FormatException("not a world part");
                int t = BitConverter.ToInt32(m, 1), i = BitConverter.ToUInt16(m, 5), n = BitConverter.ToUInt16(m, 7);
                int raw = BitConverter.ToInt32(m, 9); uint s = BitConverter.ToUInt32(m, 13); int packed = BitConverter.ToInt32(m, 17); int epoch = BitConverter.ToInt32(m, 21);
                int len = m.Length - HeaderSize;
                if (n < 1 || n > MaxParts || i >= n) throw new FormatException("bad part number");
                if (raw < 0 || raw > Lz.MaxRaw || packed < 0 || packed > n * PartSize || packed < (n - 1) * PartSize) throw new FormatException("bad world size");
                int expect = i < n - 1 ? PartSize : packed - (n - 1) * PartSize;
                if (len != expect) throw new FormatException("bad part size");
                if (t != transfer || n != parts || raw != rawLen || packed != packedLen || s != sum || epoch != partEpoch)
                {
                    partEpoch = epoch; transfer = t; parts = n; rawLen = raw; packedLen = packed; sum = s; got = new byte[n][]; have = 0;
                }
                LastPartAt = now;
                if (got[i] == null) { got[i] = new byte[len]; Buffer.BlockCopy(m, HeaderSize, got[i], 0, len); have++; }
                if (have < parts) return null;
                var all = new byte[packedLen];
                for (int k = 0; k < parts; k++) Buffer.BlockCopy(got[k], 0, all, k * PartSize, got[k].Length);
                got = null; have = 0; transfer = int.MinValue;
                byte[] world = Lz.Unpack(all, rawLen);
                if (Lz.Checksum(world) != sum) throw new FormatException("the world is damaged (checksum)");
                Epoch = partEpoch;
                return world;
            }
        }
    }

    // Only the leader's save is real. A guest's world changes count only once that guest plays in the leader's world,
    // and a guest sends none of its own until then (a guest's blank world must never overwrite the leader's plant).
    public static class Authority
    {
        public const byte MsgHaveWorld = 10;   // guest -> leader: [10][epoch] I play in your world number epoch
        public const byte MsgEpoch = 11;       // leader -> everybody: [11][epoch] the world I play now

        // I am the leader of a party: a change from this player is used only if they have my world
        public static bool MayApply(bool roomUsed, bool iAmHost, bool senderHasMyWorld) { return !(roomUsed && iAmHost) || senderHasMyWorld; }

        // I am a guest playing the world the leader plays now: the one I loaded carries the number the leader says it plays
        public static bool InLeaderWorld(bool guestMode, int loaded, int leaderSays) { return guestMode && loaded != 0 && loaded == leaderSays; }

        // I am a guest: I tell the others about my world only once it is the leader's
        public static bool MayShare(bool iAmGuest, bool haveHostWorld) { return !iAmGuest || haveHostWorld; }
    }
}
