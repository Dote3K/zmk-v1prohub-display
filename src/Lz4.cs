using System;

// LZ4-compressed LIVE frame, same layout as the vendor WebUSB SDK 1.2:
//   header  A5 5A 0B <u32 block bytes> 03 <u32 raw bytes> <u16 4096> 01 00
//   blocks  per 4096 raw bytes: <1 = lz4 / 0 = stored> <u16 raw len> <u16 data len> <data>
// Keeps its buffers between frames, so encoding a frame allocates nothing.
sealed class Lz4
{
    const int BlockSize = 4096;

    readonly int[] table = new int[1 << 12];
    readonly byte[] block = new byte[BlockSize + BlockSize / 255 + 16]; // worst case for one block
    byte[] frame = new byte[16];

    // Encodes raw into the internal buffer and returns it; the frame is the first Length bytes and stays
    // valid until the next call.
    public byte[] Frame(byte[] raw, out int length)
    {
        int max = 16 + raw.Length + (raw.Length / BlockSize + 1) * 5;
        if (frame.Length < max)
            frame = new byte[max];

        int o = 16;
        for (int off = 0; off < raw.Length; off += BlockSize)
        {
            int len = Math.Min(BlockSize, raw.Length - off);
            int packed = Compress(raw, off, len);
            bool useLz4 = packed < len;
            frame[o] = (byte)(useLz4 ? 1 : 0);
            PutU16(o + 1, len);
            PutU16(o + 3, useLz4 ? packed : len);
            if (useLz4)
                Buffer.BlockCopy(block, 0, frame, o + 5, packed);
            else
                Buffer.BlockCopy(raw, off, frame, o + 5, len);
            o += 5 + (useLz4 ? packed : len);
        }

        frame[0] = 0xA5;
        frame[1] = 0x5A;
        frame[2] = 0x0B;
        PutU32(3, o - 16);
        frame[7] = 3;
        PutU32(8, raw.Length);
        PutU16(12, BlockSize);
        frame[14] = 1;
        frame[15] = 0;
        length = o;
        return frame;
    }

    // Plain LZ4 block encoder mirroring the SDK's: greedy 4-byte matches, matches may run to the end,
    // always closed by a literal-only token. Writes into block, returns the byte count.
    int Compress(byte[] src, int start, int len)
    {
        for (int i = 0; i < table.Length; i++)
            table[i] = -1;

        int o = 0, end = start + len, anchor = start, p = start;
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
            block[o++] = (byte)(Math.Min(lit, 15) << 4 | Math.Min(ml, 15));
            if (lit >= 15)
                o = PutLength(o, lit - 15);
            Buffer.BlockCopy(src, anchor, block, o, lit);
            o += lit;
            int dist = p - cand;
            block[o++] = (byte)dist;
            block[o++] = (byte)(dist >> 8);
            if (ml >= 15)
                o = PutLength(o, ml - 15);

            int matchStart = p;
            p += m;
            anchor = p;
            for (int q = matchStart + 1; q < Math.Min(p, end - 3); q++)
                table[(int)((BitConverter.ToUInt32(src, q) * 2654435761u) >> 20)] = q;
        }

        int rest = end - anchor;
        block[o++] = (byte)(Math.Min(rest, 15) << 4);
        if (rest >= 15)
            o = PutLength(o, rest - 15);
        Buffer.BlockCopy(src, anchor, block, o, rest);
        return o + rest;
    }

    int PutLength(int o, int n)
    {
        for (; n >= 255; n -= 255)
            block[o++] = 255;
        block[o++] = (byte)n;
        return o;
    }

    void PutU16(int at, int v)
    {
        frame[at] = (byte)v;
        frame[at + 1] = (byte)(v >> 8);
    }

    void PutU32(int at, int v)
    {
        PutU16(at, v);
        PutU16(at + 2, v >> 16);
    }
}
