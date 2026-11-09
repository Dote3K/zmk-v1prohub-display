using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

// The hub-sized drawing surface every screen paints on, and its conversion to the hub's RGB565.
sealed class Canvas : IDisposable
{
    public const int W = Hub.Width, H = Hub.Height;

    public static readonly Color Background = Color.Black;
    public static readonly Color Text = Color.White;
    public static readonly Color Dim = Color.FromArgb(0x70, 0x70, 0x70);
    public static readonly Color Track = Color.FromArgb(0x26, 0x26, 0x26);

    readonly Bitmap bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb);
    public readonly Graphics G;

    public Canvas()
    {
        G = Graphics.FromImage(bmp);
        G.SmoothingMode = SmoothingMode.AntiAlias;
        // no grid fitting: lets text sit at fractional positions so it glides instead of stepping
        G.TextRenderingHint = TextRenderingHint.AntiAlias;
    }

    public void Clear()
    {
        G.Clear(Background);
    }

    // fading to or from off: darken everything drawn so far (screen 0 = black .. 1 = untouched)
    public void Fade(float screen)
    {
        if (screen < 1)
            G.FillRectangle(Brush(Color.FromArgb((int)(255 * (1 - screen)), Background)), 0, 0, W, H);
    }

    // rounded rectangle
    public void Fill(Color c, float x, float y, float w, float h, float r)
    {
        using (GraphicsPath p = new GraphicsPath())
        {
            float d = 2 * r;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            G.FillPath(Brush(c), p);
        }
    }

    // cached so a long-running process does not leak GDI handles
    readonly Dictionary<int, SolidBrush> brushes = new Dictionary<int, SolidBrush>();

    public SolidBrush Brush(Color c)
    {
        SolidBrush b;
        if (!brushes.TryGetValue(c.ToArgb(), out b))
            brushes[c.ToArgb()] = b = new SolidBrush(c);
        return b;
    }

    readonly byte[] pixels = new byte[Hub.FrameBytes];
    byte[] scan;

    // The returned buffer is reused by the next call.
    public byte[] ToRgb565()
    {
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        int stride = data.Stride;
        try
        {
            if (scan == null)
                scan = new byte[stride * H];
            Marshal.Copy(data.Scan0, scan, 0, stride * H);
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        int i = 0;
        for (int y = 0; y < H; y++)
            for (int s = y * stride, end = s + W * 3; s < end; s += 3)
            {
                int b = scan[s], gr = scan[s + 1], r = scan[s + 2];
                pixels[i++] = (byte)((r & 0xF8) | (gr >> 5));
                pixels[i++] = (byte)(((gr & 0x1C) << 3) | (b >> 3));
            }
        return pixels;
    }

    public void SavePng(string path)
    {
        bmp.Save(path, ImageFormat.Png);
    }

    public void Dispose()
    {
        foreach (SolidBrush b in brushes.Values)
            b.Dispose();
        G.Dispose();
        bmp.Dispose();
    }
}
