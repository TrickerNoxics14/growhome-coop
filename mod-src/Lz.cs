using System;
using System.IO;

namespace CoopMod
{
    // Small byte compressor in plain C#. The game's Mono has no working GZipStream (its native zlib helper is missing), so plant data is packed with this.
    // Same block layout as LZ4: [token: literal count (4 bits) | match length - 4 (4 bits)] [extra literal count] [literals] [offset, 2 bytes] [extra match length] ...
    // The last sequence has literals only.
    public static class Lz
    {
        const int HashBits = 14, MinMatch = 4, MaxOffset = 65535;
        public const int MaxRaw = 16 * 1024 * 1024;     // refuse to build anything bigger from a claimed size

        // FNV-1a over the unpacked data: every plant update carries it, so a damaged one is thrown out before it can touch the game
        public static uint Checksum(byte[] d) { uint h = 2166136261u; for (int i = 0; i < d.Length; i++) { h ^= d[i]; h *= 16777619u; } return h; }

        public static byte[] Pack(byte[] src)
        {
            int n = src.Length;
            var dst = new byte[n + n / 255 + 64];
            var table = new int[1 << HashBits];          // position + 1; 0 = empty
            int d = 0, anchor = 0, i = 0, limit = n - MinMatch;
            while (i < limit)
            {
                uint v = (uint)(src[i] | (src[i + 1] << 8) | (src[i + 2] << 16) | (src[i + 3] << 24));
                int h = (int)((v * 2654435761u) >> (32 - HashBits));
                int cand = table[h] - 1;
                table[h] = i + 1;
                if (cand >= 0 && i - cand <= MaxOffset && src[cand] == src[i] && src[cand + 1] == src[i + 1] && src[cand + 2] == src[i + 2] && src[cand + 3] == src[i + 3])
                {
                    int len = MinMatch;
                    while (i + len < n && src[cand + len] == src[i + len]) len++;
                    d = Emit(dst, d, src, anchor, i - anchor, len - MinMatch, i - cand, true);
                    i += len; anchor = i;
                }
                else i++;
            }
            d = Emit(dst, d, src, anchor, n - anchor, 0, 0, false);
            var res = new byte[d];
            Buffer.BlockCopy(dst, 0, res, 0, d);
            return res;
        }

        static int Emit(byte[] dst, int d, byte[] src, int litStart, int lit, int matchExtra, int offset, bool hasMatch)
        {
            int tokenPos = d++;
            int tl = lit >= 15 ? 15 : lit;
            int tm = hasMatch ? (matchExtra >= 15 ? 15 : matchExtra) : 0;
            dst[tokenPos] = (byte)((tl << 4) | tm);
            if (lit >= 15) d = Extra(dst, d, lit - 15);
            Buffer.BlockCopy(src, litStart, dst, d, lit); d += lit;
            if (hasMatch)
            {
                dst[d++] = (byte)(offset & 255); dst[d++] = (byte)(offset >> 8);
                if (matchExtra >= 15) d = Extra(dst, d, matchExtra - 15);
            }
            return d;
        }

        static int Extra(byte[] dst, int d, int rest)
        {
            while (rest >= 255) { dst[d++] = 255; rest -= 255; }
            dst[d++] = (byte)rest;
            return d;
        }

        // The input comes from other players: every length and offset is checked.
        public static byte[] Unpack(byte[] src, int rawLen)
        {
            if (rawLen < 0 || rawLen > MaxRaw) throw new FormatException("bad size");
            var dst = new byte[rawLen];
            int s = 0, d = 0;
            while (s < src.Length)
            {
                int token = src[s++];
                int lit = token >> 4;
                if (lit == 15) { int b; do { if (s >= src.Length) throw new FormatException("cut off"); b = src[s++]; lit += b; } while (b == 255); }
                if (lit < 0 || lit > rawLen - d || lit > src.Length - s) throw new FormatException("bad literal run");
                Buffer.BlockCopy(src, s, dst, d, lit); s += lit; d += lit;
                if (d >= rawLen) break;
                if (s + 2 > src.Length) throw new FormatException("cut off");
                int offset = src[s] | (src[s + 1] << 8); s += 2;
                int ml = token & 15;
                if (ml == 15) { int b; do { if (s >= src.Length) throw new FormatException("cut off"); b = src[s++]; ml += b; } while (b == 255); }
                ml += MinMatch;
                if (offset == 0 || offset > d || ml < 0 || ml > rawLen - d) throw new FormatException("bad match");
                int from = d - offset;
                for (int k = 0; k < ml; k++) dst[d++] = dst[from++];
            }
            if (d != rawLen) throw new FormatException("short data");
            return dst;
        }
    }
}
