using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

// Draws the Prospector-style status screen and converts it to the hub's RGB565.
sealed class Renderer : IDisposable
{
    const int W = Hub.Width, H = Hub.Height;

    // black and white everywhere; only the battery bars carry color
    static readonly Color Background = Color.Black;
    static readonly Color Accent = Color.White;
    static readonly Color Text = Color.White;
    static readonly Color Dim = Color.FromArgb(0x70, 0x70, 0x70);
    static readonly Color Pill = Color.FromArgb(0x1A, 0x1A, 0x1A);
    static readonly Color Track = Color.FromArgb(0x26, 0x26, 0x26);

    readonly Bitmap bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb);
    readonly Graphics g;
    readonly Font small = new Font("Segoe UI Semibold", 13, GraphicsUnit.Pixel);
    readonly Font pill = new Font("Segoe UI Semibold", 11, GraphicsUnit.Pixel);
    readonly Font big = new Font("Bahnschrift", 50, FontStyle.Bold, GraphicsUnit.Pixel);
    readonly Font medium = new Font("Bahnschrift", 34, FontStyle.Bold, GraphicsUnit.Pixel);
    readonly StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    readonly StringFormat left = new StringFormat { LineAlignment = StringAlignment.Center };
    readonly string keyboard;

    public Renderer(string keyboardName)
    {
        keyboard = keyboardName;
        g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    public byte[] Render(Status s)
    {
        g.Clear(Background);

        g.DrawString(keyboard.ToUpperInvariant(), small, Brush(Dim), new RectangleF(10, 2, 200, 22), left);

        string name = s.LayerName.Length > 0 ? s.LayerName : "Layer " + s.Layer;
        name = name.ToUpperInvariant();
        g.DrawString(name, FitFont(name), Brushes.White, new RectangleF(0, 24, W, 62), center);

        int count = Math.Max(3, s.Layer + 1), dot = 14, gap = 6;
        int x0 = (W - (count * dot + (count - 1) * gap)) / 2;
        for (int i = 0; i < count; i++)
            Fill(i == s.Layer ? Accent : Track, x0 + i * (dot + gap), 88, dot, 5, 2);

        string[] names = { "CTRL", "SHIFT", "ALT", "GUI" };
        bool[] on = { s.Ctrl, s.Shift, s.Alt, s.Gui };
        int pw = 62, pg = 8, px0 = (W - (4 * pw + 3 * pg)) / 2;
        for (int i = 0; i < 4; i++)
        {
            int x = px0 + i * (pw + pg);
            Fill(on[i] ? Accent : Pill, x, 102, pw, 20, 10);
            g.DrawString(names[i], pill, on[i] ? Brushes.Black : Brush(Dim), new RectangleF(x, 102, pw, 20), center);
        }

        Battery(12, "L", s.BatteryLeft);
        Battery(166, "R", s.BatteryRight);

        return ToRgb565();
    }

    Font FitFont(string text)
    {
        // shrink long layer names so they stay on one line
        return g.MeasureString(text, big).Width <= W - 16 ? big : medium;
    }

    void Battery(int x, string label, int pct)
    {
        Color col = pct >= 50 ? Color.FromArgb(0x32, 0xD5, 0x83) : pct >= 20 ? Color.FromArgb(0xFD, 0xB0, 0x22) : Color.FromArgb(0xF9, 0x70, 0x66);
        g.DrawString(label, pill, Brush(Dim), new RectangleF(x, 132, 18, 26), left);
        Fill(Track, x + 18, 139, 90, 12, 6);
        if (pct > 0)
            Fill(col, x + 18, 139, Math.Max(12, 90 * pct / 100), 12, 6);
        g.DrawString(pct > 0 ? pct + "%" : "--", small, Brush(Text), new RectangleF(x + 110, 132, 44, 26), left);
    }

    void Fill(Color c, int x, int y, int w, int h, int r)
    {
        using (GraphicsPath p = new GraphicsPath())
        {
            int d = 2 * r;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            g.FillPath(Brush(c), p);
        }
    }

    // cached so a long-running process does not leak GDI handles
    readonly Dictionary<int, SolidBrush> brushes = new Dictionary<int, SolidBrush>();

    SolidBrush Brush(Color c)
    {
        SolidBrush b;
        if (!brushes.TryGetValue(c.ToArgb(), out b))
            brushes[c.ToArgb()] = b = new SolidBrush(c);
        return b;
    }

    byte[] ToRgb565()
    {
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        byte[] row = new byte[data.Stride];
        byte[] px = new byte[Hub.FrameBytes];
        try
        {
            for (int y = 0; y < H; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                for (int x = 0; x < W; x++)
                {
                    int b = row[x * 3], gr = row[x * 3 + 1], r = row[x * 3 + 2];
                    int i = (y * W + x) * 2;
                    px[i] = (byte)((r & 0xF8) | (gr >> 5));
                    px[i + 1] = (byte)(((gr & 0x1C) << 3) | (b >> 3));
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return px;
    }

    public void SavePng(string path)
    {
        bmp.Save(path, ImageFormat.Png);
    }

    public void Dispose()
    {
        g.Dispose();
        bmp.Dispose();
    }
}
