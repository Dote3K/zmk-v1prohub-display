using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;

// What the Claude Code screen shows at one moment.
sealed class ClaudeView
{
    public string Header = "CLAUDE CODE"; // top line, e.g. the project name
    public string Pet = "";          // sprite name, e.g. "working-typing"
    public double PetStart;          // clock ms the pet's animation started at, so a new state starts at frame 0
    public string State = "";        // one big word: WORKING, NEEDS YOU, DONE ...
    public string Detail = "";       // e.g. "Bash · npm test"
    public string Sub = "";          // e.g. "2:14 · 9 tools"
    public Color StateColor = Canvas.Text;
    public int Quota5h = -1, Quota7d = -1; // % used, -1 = unknown
    public float Screen = 1;         // 0 off .. 1 fully on, as on the keyboard screen

    public static readonly Color Clay = Color.FromArgb(0xDE, 0x88, 0x6D); // the pets' own body color
    public static readonly Color Red = Color.FromArgb(0xF0, 0x44, 0x38);

    // one of each state, for --pet-demo
    public static ClaudeView[] Demo()
    {
        return new[]
        {
            new ClaudeView { Pet = "loading", State = "THINKING", Detail = "Planning the change", Sub = "0:12", Quota5h = 23, Quota7d = 41 },
            new ClaudeView { Pet = "working-typing", State = "WORKING", Detail = "Bash · npm test", Sub = "2:14 · 9 tools", Quota5h = 24, Quota7d = 41 },
            new ClaudeView { Pet = "detective", State = "SEARCHING", Detail = "Grep · SetBrightness", Sub = "2:31 · 10 tools", Quota5h = 24, Quota7d = 41 },
            new ClaudeView { Pet = "notification", State = "NEEDS YOU", StateColor = Clay, Detail = "Permission · Bash", Sub = "rm -rf node_modules", Quota5h = 25, Quota7d = 42 },
            new ClaudeView { Pet = "celebrating", State = "DONE", Detail = "3m 12s · 14 tools", Sub = "your turn", Quota5h = 26, Quota7d = 42 },
            new ClaudeView { Pet = "error", State = "ERROR", StateColor = Red, Detail = "API overloaded", Sub = "retrying", Quota5h = 74, Quota7d = 58 },
            new ClaudeView { Pet = "sleeping", State = "IDLE", Detail = "no session", Sub = "18 min ago", Quota5h = 92, Quota7d = 63 },
        };
    }
}

// Draws the Claude Code screen: the pet on the left, state and quota on the right.
sealed class ClaudeRenderer : IDisposable
{
    const int W = Canvas.W, H = Canvas.H;
    const int PetArea = 156;               // left column the pet is centered in
    const int Col = PetArea + 6, ColW = W - Col - 10;

    readonly Canvas c;
    readonly Graphics g;
    readonly string petDir;
    readonly Dictionary<string, Sprite> pets = new Dictionary<string, Sprite>(); // null = missing, not retried
    readonly Font small = new Font("Segoe UI Semibold", 13, GraphicsUnit.Pixel);
    readonly Font tiny = new Font("Segoe UI Semibold", 12, GraphicsUnit.Pixel);
    // white text at these sizes needs full bold strokes to read sharp on the panel; gray text stays semibold
    readonly Font strong = new Font("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
    readonly Font strongTiny = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
    readonly Font[] stateFonts =
    {
        new Font("Bahnschrift", 30, FontStyle.Bold, GraphicsUnit.Pixel),
        new Font("Bahnschrift", 24, FontStyle.Bold, GraphicsUnit.Pixel),
        new Font("Bahnschrift", 19, FontStyle.Bold, GraphicsUnit.Pixel),
    };
    readonly StringFormat line = new StringFormat(StringFormatFlags.NoWrap)
    {
        LineAlignment = StringAlignment.Center,
        Trimming = StringTrimming.EllipsisCharacter,
    };
    readonly StringFormat right = new StringFormat(StringFormatFlags.NoWrap)
    {
        Alignment = StringAlignment.Far,
        LineAlignment = StringAlignment.Center,
    };

    public ClaudeRenderer(Canvas canvas, string petDir)
    {
        c = canvas;
        g = canvas.G;
        this.petDir = petDir;
    }

    // drawn when a state's own pet has not been rendered into the pets folder
    const string FallbackPet = "loading";

    // ms until the pet drawn for this name shows its next frame
    public double UntilNextFrame(string pet, double ms)
    {
        Sprite s = Pet(pet) ?? Pet(FallbackPet);
        return s != null ? s.UntilNextFrame(ms) : 1000;
    }

    public Sprite Pet(string name)
    {
        Sprite s;
        if (!pets.TryGetValue(name, out s))
            pets[name] = s = Sprite.Load(petDir, name);
        return s;
    }

    // ms is the time into the pet's animation.
    public byte[] Render(ClaudeView v, double ms)
    {
        c.Clear();
        if (v.Screen <= 0.004f)
            return c.ToRgb565();

        Sprite pet = Pet(v.Pet) ?? Pet(FallbackPet);
        if (pet != null)
            pet.Draw(g, pet.FrameAt(ms), Math.Max(0, (PetArea - pet.Width) / 2), Math.Max(0, (H - pet.Height) / 2));

        // Text here stands still, so it snaps to the pixel grid: small sizes come out sharp instead of
        // smeared across two rows. (The keyboard screen keeps free positioning for its gliding names.)
        TextRenderingHint hint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.DrawString(v.Header.ToUpperInvariant(), small, c.Brush(Canvas.Dim), new RectangleF(Col, 2, ColW, 22), line);

        string state = v.State.ToUpperInvariant();
        g.DrawString(state, StateFont(state), c.Brush(v.StateColor), new RectangleF(Col - 2, 26, ColW + 4, 40), line);
        g.DrawString(v.Detail, strong, c.Brush(Canvas.Text), new RectangleF(Col, 70, ColW, 20), line);
        g.DrawString(v.Sub, tiny, c.Brush(Canvas.Dim), new RectangleF(Col, 90, ColW, 18), line);

        int half = (ColW - 10) / 2;
        Quota(Col, half, "5H", v.Quota5h);
        Quota(Col + half + 10, half, "7D", v.Quota7d);
        g.TextRenderingHint = hint;

        c.Fade(v.Screen);
        return c.ToRgb565();
    }

    // the largest font the word fits in, so "NEEDS YOU" still fits beside the pet
    Font StateFont(string text)
    {
        foreach (Font f in stateFonts)
            if (g.MeasureString(text, f).Width <= ColW + 4)
                return f;
        return stateFonts[stateFonts.Length - 1];
    }

    // label and percentage above a thin bar; the bar warms up as the limit gets close
    void Quota(int x, int w, string label, int pct)
    {
        g.DrawString(label, tiny, c.Brush(Canvas.Dim), new RectangleF(x, 134, w, 16), line);
        g.DrawString(pct >= 0 ? pct.ToString() : "--", pct >= 0 ? strongTiny : tiny, c.Brush(pct >= 0 ? Canvas.Text : Canvas.Dim), new RectangleF(x, 134, w, 16), right);
        c.Fill(Canvas.Track, x, 154, w, 4, 2);
        if (pct > 0)
        {
            Color col = pct >= 90 ? ClaudeView.Red : pct >= 70 ? Color.FromArgb(0xFF, 0x8A, 0x1F) : Canvas.Text;
            c.Fill(col, x, 154, Math.Max(4, w * Math.Min(pct, 100) / 100), 4, 2);
        }
    }

    public void Dispose()
    {
        foreach (Sprite s in pets.Values)
            if (s != null)
                s.Dispose();
        pets.Clear();
    }
}
