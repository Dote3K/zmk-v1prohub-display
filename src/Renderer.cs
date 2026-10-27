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

    // black and white everywhere; only a low battery bar turns orange or red
    static readonly Color Background = Color.Black;
    static readonly Color Accent = Color.White;
    static readonly Color Text = Color.White;
    static readonly Color Dim = Color.FromArgb(0x70, 0x70, 0x70);
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
        // no grid fitting: lets text sit at fractional positions so it glides instead of stepping
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
    }

    public byte[] Render(Status s)
    {
        return Render(View.Settled(s));
    }

    public byte[] Render(View v)
    {
        g.Clear(Background);

        g.DrawString(keyboard.ToUpperInvariant(), small, Brush(Dim), new RectangleF(10, 2, 200, 22), left);

        // layer names, each at its own offset and opacity, clipped to the name area
        RectangleF nameArea = new RectangleF(0, 24, W, 62);
        g.SetClip(nameArea);
        foreach (View.Name n in v.Names)
            if (n.Alpha > 0.01f)
                DrawName(n.Text, nameArea, n.Offset, (int)(255 * n.Alpha));
        g.ResetClip();

        // layer track with a sliding indicator
        int count = Math.Max(3, v.MaxLayer + 1), dot = 14, gap = 6;
        int x0 = (W - (count * dot + (count - 1) * gap)) / 2;
        for (int i = 0; i < count; i++)
            Fill(Track, x0 + i * (dot + gap), 88, dot, 5, 2);
        Fill(Accent, x0 + v.Indicator * (dot + gap), 88, dot, 5, 2);

        // held modifiers: each takes room in proportion to its presence, so the row reflows smoothly
        float total = 0;
        foreach (float m in v.Mods)
            total += m;
        float mx = W / 2f - total * ModGap / 2f;
        for (int i = 0; i < 4; i++)
        {
            float m = v.Mods[i];
            if (m > 0.01f)
                DrawMod(i, mx + m * ModGap / 2f, 112, 0.6f + 0.4f * m, (int)(255 * m));
            mx += m * ModGap;
        }

        Battery(16, v.BatteryLeft);
        Battery(W - 16 - BatteryWidth, v.BatteryRight);

        return ToRgb565();
    }

    const float ModGap = 44;

    void DrawName(string name, RectangleF area, float dy, int alpha)
    {
        name = name.ToUpperInvariant();
        area.Offset(0, dy);
        g.DrawString(name, FitFont(name), Brush(Color.FromArgb(alpha, Text)), area, center);
    }

    // 0 Ctrl (caret), 1 Shift (arrow), 2 Alt (option), 3 GUI (Windows logo); ~22px at scale 1
    void DrawMod(int kind, float cx, float cy, float scale, int alpha)
    {
        g.TranslateTransform(cx, cy);
        g.ScaleTransform(scale, scale);
        cx = cy = 0;
        Color color = Color.FromArgb(alpha, Accent);
        using (Pen pen = new Pen(color, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            switch (kind)
            {
                case 0:
                    g.DrawLines(pen, new[] { new PointF(cx - 9, cy + 4), new PointF(cx, cy - 6), new PointF(cx + 9, cy + 4) });
                    break;
                case 1:
                    g.DrawPolygon(pen, new[] {
                        new PointF(cx, cy - 11), new PointF(cx + 11, cy + 1), new PointF(cx + 5, cy + 1), new PointF(cx + 5, cy + 10),
                        new PointF(cx - 5, cy + 10), new PointF(cx - 5, cy + 1), new PointF(cx - 11, cy + 1) });
                    break;
                case 2:
                    g.DrawLines(pen, new[] { new PointF(cx - 11, cy - 7), new PointF(cx - 4, cy - 7), new PointF(cx + 4, cy + 8), new PointF(cx + 11, cy + 8) });
                    g.DrawLine(pen, cx + 3, cy - 7, cx + 11, cy - 7);
                    break;
                default:
                    SolidBrush b = Brush(color);
                    g.FillRectangle(b, cx - 10, cy - 10, 9, 9);
                    g.FillRectangle(b, cx + 1, cy - 10, 9, 9);
                    g.FillRectangle(b, cx - 10, cy + 1, 9, 9);
                    g.FillRectangle(b, cx + 1, cy + 1, 9, 9);
                    break;
            }
        }
        g.ResetTransform();
    }

    Font FitFont(string text)
    {
        // shrink long layer names so they stay on one line
        return g.MeasureString(text, big).Width <= W - 16 ? big : medium;
    }

    const int BatteryWidth = 130;

    // thin bar with the percentage centered above it
    void Battery(int x, int pct)
    {
        Color col = pct >= 50 ? Color.White : pct >= 20 ? Color.FromArgb(0xFF, 0x8A, 0x1F) : Color.FromArgb(0xF0, 0x44, 0x38);
        g.DrawString(pct > 0 ? pct.ToString() : "--", small, Brush(pct > 0 ? Text : Dim), new RectangleF(x, 132, BatteryWidth, 18), center);
        Fill(Track, x, 154, BatteryWidth, 4, 2);
        if (pct > 0)
            Fill(col, x, 154, Math.Max(4, BatteryWidth * pct / 100), 4, 2);
    }

    void Fill(Color c, float x, float y, float w, float h, float r)
    {
        using (GraphicsPath p = new GraphicsPath())
        {
            float d = 2 * r;
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
