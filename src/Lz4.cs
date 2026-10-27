using System;
using System.IO;

// LZ4-compressed LIVE frame, same layout as the vendor WebUSB SDK 1.2:
//   header  A5 5A 0B <u32 block bytes> 03 <u32 raw bytes> <u16 4096> 01 00
//   blocks  per 4096 raw bytes: <1 = lz4 / 0 = stored> <u16 raw len> <u16 data len> <data>
static class Lz4
{
    const int BlockSize = 4096;

    public static byte[] Frame(byte[] raw)
    {
        MemoryStream blocks = new MemoryStream(raw.Length / 4);
        for (int off = 0; off < raw.Length; off += BlockSize)
        {
            int len = Math.Min(BlockSize, raw.Length - off);
            byte[] packed = Compress(raw, off, len);
            bool useLz4 = packed.Length < len;
            blocks.WriteByte((byte)(useLz4 ? 1 : 0));
            WriteU16(blocks, len);
            WriteU16(blocks, useLz4 ? packed.Length : len);
            if (useLz4)
                blocks.Write(packed, 0, packed.Length);
            else
                blocks.Write(raw, off, len);
        }

        byte[] frame = new byte[16 + blocks.Length];
        frame[0] = 0xA5;
        frame[1] = 0x5A;
        frame[2] = 0x0B;
        BitConverter.GetBytes((uint)blocks.Length).CopyTo(frame, 3);
        frame[7] = 3;
        BitConverter.GetBytes((uint)raw.Length).CopyTo(frame, 8);
        BitConverter.GetBytes((ushort)BlockSize).CopyTo(frame, 12);
        frame[14] = 1;
        blocks.Position = 0;
        blocks.Read(frame, 16, (int)blocks.Length);
        return frame;
    }

    // Plain LZ4 block encoder mirroring the SDK's: greedy 4-byte matches, matches may run to the end,
    // always closed by a literal-only token.
    static byte[] Compress(byte[] src, int start, int len)
    {
        MemoryStream o = new MemoryStream(len);
        int[] table = new int[1 << 12];
        for (int i = 0; i < table.Length; i++)
            table[i] = -1;

        int end = start + len, anchor = start, p = start;
        while (p + 4 <= end)
        {
            uint v = BitConverter.ToUInt32(src, p);
            int h = (int)((v * 2654435761u) >> 20);
            int cand = table[h];
            table[h] = p;
            if (cand < 0 || BitConverter.ToUInt32(src, cand) != v)
            {
                p++;
                continue;
            }

            int m = 4;
            while (p + m < end && src[cand + m] == src[p + m])
                m++;

            int lit = p - anchor, ml = m - 4;
            o.WriteByte((byte)(Math.Min(lit, 15) << 4 | Math.Min(ml, 15)));
            if (lit >= 15)
                WriteLength(o, lit - 15);
            o.Write(src, anchor, lit);
            int dist = p - cand;
            o.WriteByte((byte)dist);
            o.WriteByte((byte)(dist >> 8));
            if (ml >= 15)
                WriteLength(o, ml - 15);

            int matchStart = p;
            p += m;
            anchor = p;
            for (int q = matchStart + 1; q < Math.Min(p, end - 3); q++)
                table[(int)((BitConverter.ToUInt32(src, q) * 2654435761u) >> 20)] = q;
        }

        int rest = end - anchor;
        o.WriteByte((byte)(Math.Min(rest, 15) << 4));
        if (rest >= 15)
            WriteLength(o, rest - 15);
        o.Write(src, anchor, rest);
        return o.ToArray();
    }

    static void WriteLength(Stream o, int n)
    {
        for (; n >= 255; n -= 255)
            o.WriteByte(255);
        o.WriteByte((byte)n);
    }

    static void WriteU16(Stream o, int v)
    {
        o.WriteByte((byte)v);
        o.WriteByte((byte)(v >> 8));
    }
}
