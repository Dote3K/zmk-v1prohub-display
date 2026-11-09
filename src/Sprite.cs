using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

// An animated pet: a sprite sheet (frames in a grid, left to right, top to bottom) plus the manifest
// tools/clawd-frames writes next to it. Frames are already scaled to their final pixel size.
sealed class Sprite : IDisposable
{
    public readonly string Name;
    public readonly int Width, Height, Frames;
    public readonly double FrameMs;
    public double ArtScale;         // screen pixels per art pixel it was rendered at, 0 if unknown
    readonly int columns;
    readonly Bitmap sheet;

    Sprite(string name, Bitmap sheet, int width, int height, int frames, int columns, double frameMs)
    {
        Name = name;
        this.sheet = sheet;
        Width = width;
        Height = height;
        Frames = frames;
        this.columns = columns;
        FrameMs = frameMs;
    }

    // Where pets are looked up: %APPDATA%\zmk-hub-display\pets (your own), pets\ next to the exe (the
    // release zip), or assets\pets next to bin\ while developing.
    public static string DefaultDir()
    {
        string user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zmk-hub-display", "pets");
        if (Directory.Exists(user))
            return user;
        string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pets"); // the release zip
        if (Directory.Exists(beside))
            return beside;
        string dev = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "assets", "pets"));
        return Directory.Exists(dev) ? dev : user;
    }

    // Returns null when the pet's files are missing or unreadable.
    public static Sprite Load(string dir, string name)
    {
        string png = Path.Combine(dir, name + ".png"), json = Path.Combine(dir, name + ".json");
        if (!File.Exists(png) || !File.Exists(json))
            return null;
        try
        {
            string m = File.ReadAllText(json);
            int w = (int)Number(m, "frameWidth"), h = (int)Number(m, "frameHeight");
            int frames = (int)Number(m, "frames"), columns = (int)Number(m, "columns");
            double frameMs = Number(m, "frameMs");
            if (w <= 0 || h <= 0 || frames <= 0 || columns <= 0 || frameMs <= 0)
                return null;

            // copied into the fastest GDI+ format; this also releases the file, which Bitmap(path) keeps locked
            Bitmap sheet;
            using (Bitmap raw = new Bitmap(png))
            {
                if (raw.Width < columns * w || raw.Height < (frames + columns - 1) / columns * h)
                    return null;
                sheet = new Bitmap(raw.Width, raw.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(sheet))
                    g.DrawImage(raw, new Rectangle(0, 0, raw.Width, raw.Height));
            }
            Match scale = Regex.Match(m, "\"scale\"\\s*:\\s*([0-9.]+)");
            return new Sprite(name, sheet, w, h, frames, columns, frameMs)
            {
                ArtScale = scale.Success ? double.Parse(scale.Groups[1].Value, CultureInfo.InvariantCulture) : 0,
            };
        }
        // (csc of .NET Framework 4 is C# 5: no exception filters)
        catch (IOException) { return null; }
        catch (ArgumentException) { return null; }
        catch (FormatException) { return null; }
        catch (InvalidDataException) { return null; }
        catch (OutOfMemoryException) { return null; } // what GDI+ throws for a corrupt image
    }

    // The manifest is flat and written by our own tool, so one number per key is all that is read.
    static double Number(string json, string key)
    {
        Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?[0-9.]+)");
        if (!m.Success)
            throw new InvalidDataException("manifest has no " + key);
        return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    // frame showing ms into the animation, looping
    public int FrameAt(double ms)
    {
        return (int)((long)(Math.Max(0, ms) / FrameMs) % Frames);
    }

    // ms until FrameAt changes
    public double UntilNextFrame(double ms)
    {
        return FrameMs - Math.Max(0, ms) % FrameMs;
    }

    // unscaled, so every art pixel stays a crisp square
    public void Draw(Graphics g, int frame, int x, int y)
    {
        Draw(g, frame, x, y, 1);
    }

    // scaled with nearest neighbour; a scale that divides the pet's own (e.g. 1/2 of 6x) stays crisp
    public void Draw(Graphics g, int frame, int x, int y, float scale)
    {
        InterpolationMode mode = g.InterpolationMode;
        PixelOffsetMode offset = g.PixelOffsetMode;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(sheet, new Rectangle(x, y, (int)Math.Round(Width * scale), (int)Math.Round(Height * scale)),
            frame % columns * Width, frame / columns * Height, Width, Height, GraphicsUnit.Pixel);
        g.InterpolationMode = mode;
        g.PixelOffsetMode = offset;
    }

    public void Dispose()
    {
        sheet.Dispose();
    }
}
