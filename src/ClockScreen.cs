using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;

// A clock with company: the time and date, the weather, and Clawd doing whatever fits the hour, the
// weather or the day. Three looks over the same data:
//   companion  Clawd on the left, a big clock on the right
//   dashboard  the clock with a column of facts: weather, sun, Claude quota, keyboard batteries
//   sky        a window on the sky: its colour follows the sun, sun or moon on their arc, clouds, rain
//              or snow from the weather, and Clawd standing on the ground under it
sealed class ClockScreen : IScreen
{
    const int W = Canvas.W, H = Canvas.H;
    const string DefaultStyle = "companion";

    // Hanoi, for the sky's sunrise and sunset until a city is set
    const double FallbackLat = 21.0285, FallbackLon = 105.8542;

    readonly Canvas c;
    readonly Graphics g;
    readonly string petDir;
    readonly ClaudeSessions sessions;
    readonly Func<Status> keyboard;
    readonly Dictionary<string, Sprite> pets = new Dictionary<string, Sprite>();
    string lastPet;
    double petSince;

    readonly Font clockBig = new Font("Bahnschrift", 60, FontStyle.Bold, GraphicsUnit.Pixel);
    readonly Font clockMid = new Font("Bahnschrift", 46, FontStyle.Bold, GraphicsUnit.Pixel);
    readonly Font temp = new Font("Bahnschrift", 22, FontStyle.Bold, GraphicsUnit.Pixel);
    readonly Font strong = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
    readonly Font small = new Font("Segoe UI Semibold", 13, GraphicsUnit.Pixel);
    readonly Font tiny = new Font("Segoe UI Semibold", 12, GraphicsUnit.Pixel);
    readonly StringFormat tight = StringFormat.GenericTypographic;
    readonly StringFormat left = new StringFormat(StringFormatFlags.NoWrap) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
    readonly StringFormat right = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
    readonly StringFormat center = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

    public ClockScreen(Canvas canvas, string petDir, ClaudeSessions sessions, Func<Status> keyboard)
    {
        c = canvas;
        g = canvas.G;
        this.petDir = petDir;
        this.sessions = sessions;
        this.keyboard = keyboard;
    }

    public string Id { get { return "clock"; } }
    public string Title { get { return "Clock"; } }
    public bool CanTakeover { get { return false; } }

    public bool Urgent(double now)
    {
        return false;
    }

    static string Style
    {
        get { return Settings.Option("clock", "style", DefaultStyle); }
    }

    static bool Clock24
    {
        get { return Settings.Option("clock", "clock24", true); }
    }

    public string Summary(double now)
    {
        WeatherNow w = Weather.Current;
        return TimeText(DateTime.Now) + (w != null ? " · " + Math.Round(w.Temp) + "° " + w.Condition : "");
    }

    // ---- settings ----

    static List<Place> matches = new List<Place>(); // the last city search, to pick another of them
    static string searchNote = "";

    public IList<Field> Fields()
    {
        Func<string, string, KeyValuePair<string, string>> kv = (k, v) => new KeyValuePair<string, string>(k, v);
        List<Field> f = new List<Field>
        {
            Field.Choice("style", "Style", "Companion: Clawd and a big clock. Dashboard: the clock and a column of facts. Sky: the sky outside, with Clawd under it.",
                new[] { kv("companion", "Companion"), kv("dashboard", "Dashboard"), kv("sky", "Sky") },
                () => Style, v => Settings.SetOption(Id, "style", v)),
            Field.Toggle("clock24", "24-hour clock", "", () => Clock24, v => Settings.SetOption(Id, "clock24", v)),
            Field.Text("birthday", "Birthday", "Day and month as DD-MM, e.g. 04-12. Clawd celebrates that day.",
                () => Settings.Option(Id, "birthday", ""), v => Settings.SetOption(Id, "birthday", v.Trim())),
        };
        foreach (Field x in f)
            x.Section = "Clock";

        Place place = Weather.Location();
        List<Field> w = new List<Field>
        {
            Field.Toggle("weather", "Show the weather", "From Open-Meteo, every 30 minutes. Only the city's coordinates are sent.",
                () => Settings.Option(Id, "weather", true), v => { Settings.SetOption(Id, "weather", v); Weather.Refresh(); }),
            Field.Text("city", "City", searchNote.Length > 0 ? searchNote : "Type a city and press Enter.",
                () => place != null ? place.Name : "", SearchCity),
        };
        if (matches.Count > 1)
        {
            List<KeyValuePair<string, string>> choices = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < matches.Count; i++)
                choices.Add(kv(i.ToString(), matches[i].Label));
            string chosen = place == null ? "" : matches.FindIndex(m => m.Name == place.Name && Math.Abs(m.Lat - place.Lat) < 0.001).ToString();
            w.Add(Field.Choice("match", "Which one", "", choices, () => chosen, v =>
            {
                int i;
                if (int.TryParse(v, out i) && i >= 0 && i < matches.Count)
                    Weather.SetLocation(matches[i]);
            }));
        }
        w.Add(Field.Info("now", "Now", Weather.Describe));
        foreach (Field x in w)
            x.Section = "Weather";
        f.AddRange(w);
        return f;
    }

    static void SearchCity(string name)
    {
        if (name.Trim().Length == 0)
            return;
        try
        {
            matches = Weather.Search(name);
            searchNote = matches.Count == 0 ? "No place called \"" + name.Trim() + "\" was found." : "";
            if (matches.Count > 0)
                Weather.SetLocation(matches[0]);
        }
        catch (Exception e) // offline or the geocoder is down
        {
            searchNote = "Could not look the city up: " + e.Message;
        }
    }

    // ---- drawing ----

    public byte[] Render(double now, float brightness, out double next)
    {
        return Draw(Style, DateTime.Now, Weather.Current, now, brightness, out next);
    }

    // Also used for previews with a made-up time and weather (--clock-png).
    public byte[] Draw(string style, DateTime t, WeatherNow w, double ms, float brightness, out double next)
    {
        c.Clear();
        next = (60 - t.Second) * 1000 - t.Millisecond; // nothing but the pet changes before the next minute
        if (brightness <= 0.004f)
            return c.ToRgb565();

        string petName = PetFor(t, w);
        if (petName != lastPet)
        {
            lastPet = petName;
            petSince = ms;
        }
        Sprite pet = Pet(petName) ?? Pet("happy");
        double petMs = ms - petSince;

        TextRenderingHint hint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        if (style == "dashboard")
            Dashboard(t, w, pet, petMs);
        else if (style == "sky")
        {
            Sky(t, w, pet, petMs, ms);
            next = Math.Min(next, 100); // stars, clouds, rain: ten frames a second is smooth enough
        }
        else
            Companion(t, w, pet, petMs);
        g.TextRenderingHint = hint;

        if (pet != null)
            next = Math.Min(next, pet.UntilNextFrame(petMs));
        c.Fade(brightness);
        return c.ToRgb565();
    }

    // What Clawd is up to: a birthday or holiday first, then the weather, then the time of day.
    string PetFor(DateTime t, WeatherNow w)
    {
        string dm = t.ToString("dd-MM");
        if (dm == Settings.Option(Id, "birthday", "")) return "birthday";
        if (dm == "01-01") return "new-year";
        if (dm == "24-12" || dm == "25-12") return "christmas";
        if (dm == "31-10") return "halloween";
        if (dm == "14-02") return "valentine";
        if (w != null)
        {
            if (w.Wet) return "umbrella";
            if (w.Kind == "snow") return "snow";
            if (w.Temp <= 12) return "winter";
            if (w.Temp >= 34) return "summer";
        }
        int m = t.Hour * 60 + t.Minute;
        if (m < 5 * 60) return "sleeping";
        if (m < 9 * 60) return "coffee";
        if (m < 12 * 60) return "working-typing";
        if (m < 13 * 60 + 30) return "eating";
        if (m < 18 * 60) return "working-typing";
        if (m < 21 * 60) return "gaming";
        if (m < 23 * 60) return "reading";
        return "yawning";
    }

    Sprite Pet(string name)
    {
        Sprite s;
        if (!pets.TryGetValue(name, out s))
            pets[name] = s = Sprite.Load(petDir, name);
        return s;
    }

    // a scale that puts every art pixel on exactly `px` screen pixels, so a smaller pet stays crisp
    static float ArtScale(Sprite s, int px)
    {
        return s.ArtScale > 0 ? (float)(px / s.ArtScale) : 0.5f;
    }

    string TimeText(DateTime t)
    {
        return Clock24 ? t.ToString("HH:mm") : t.ToString("h:mm");
    }

    // the time, drawn in three pieces so the colon sits a little apart; returns its width
    float DrawTime(DateTime t, Font font, float x, float y, Color color, bool alignCenter)
    {
        string text = TimeText(t);
        int colon = text.IndexOf(':');
        string a = text.Substring(0, colon), b = text.Substring(colon + 1);
        float wa = g.MeasureString(a, font, 1000, tight).Width, wc = g.MeasureString(":", font, 1000, tight).Width,
            wb = g.MeasureString(b, font, 1000, tight).Width, gap = font.Size * 0.04f;
        float width = wa + wc + wb + gap * 2;
        if (alignCenter)
            x -= width / 2;
        g.DrawString(a, font, c.Brush(color), x, y, tight);
        g.DrawString(":", font, c.Brush(color), x + wa + gap, y, tight);
        g.DrawString(b, font, c.Brush(color), x + wa + wc + gap * 2, y, tight);
        if (!Clock24)
            g.DrawString(t.ToString("tt", CultureInfo.InvariantCulture), tiny, c.Brush(Canvas.Dim), x + width + 4, y + font.Size * 0.15f, tight);
        return width;
    }

    // ---- companion ----

    void Companion(DateTime t, WeatherNow w, Sprite pet, double petMs)
    {
        const int area = 150, col = 160;
        if (pet != null)
            pet.Draw(g, pet.FrameAt(petMs), Math.Max(0, (area - pet.Width) / 2), Math.Max(0, (H - pet.Height) / 2));

        DrawTime(t, clockBig, col, 14, Canvas.Text, false);
        g.DrawString(t.ToString("dddd", CultureInfo.InvariantCulture).ToUpperInvariant(), small, c.Brush(Canvas.Dim), new RectangleF(col + 2, 84, W - col - 8, 18), left);
        g.DrawString(t.ToString("d MMMM", CultureInfo.InvariantCulture), strong, c.Brush(Canvas.Text), new RectangleF(col + 2, 102, W - col - 8, 20), left);

        if (w != null)
        {
            WeatherIcon(w, col + 2, 134, 24);
            string deg = Math.Round(w.Temp) + "°";
            float tw = g.MeasureString(deg, temp).Width; // measured as drawn, padding included
            g.DrawString(deg, temp, c.Brush(Canvas.Text), new RectangleF(col + 30, 132, tw, 28), left);
            g.DrawString(w.Condition, tiny, c.Brush(Canvas.Dim), new RectangleF(col + 30 + tw, 134, W - col - 34 - tw, 24), left);
        }
        else
        {
            DateTime rise, set;
            if (SunTimes(t, out rise, out set))
                g.DrawString("Sun " + rise.ToString("HH:mm") + " – " + set.ToString("HH:mm"), tiny, c.Brush(Canvas.Dim),
                    new RectangleF(col + 2, 134, W - col - 8, 24), left);
        }
    }

    // ---- dashboard ----

    void Dashboard(DateTime t, WeatherNow w, Sprite pet, double petMs)
    {
        const int split = 156, x = 170, colW = W - x - 10;
        DrawTime(t, clockMid, 12, 10, Canvas.Text, false);
        g.DrawString(t.ToString("dddd", CultureInfo.InvariantCulture).ToUpperInvariant(), tiny, c.Brush(Canvas.Dim), new RectangleF(14, 64, split - 20, 16), left);
        g.DrawString(t.ToString("d MMMM", CultureInfo.InvariantCulture), strong, c.Brush(Canvas.Text), new RectangleF(14, 80, split - 20, 20), left);
        if (pet != null)
        {
            float s = ArtScale(pet, 3);
            pet.Draw(g, pet.FrameAt(petMs), 10, (int)(H - 6 - pet.Height * s), s);
        }
        g.FillRectangle(c.Brush(Canvas.Track), split, 12, 1, H - 24);

        float y = 10;
        if (w != null)
        {
            WeatherIcon(w, x, y + 2, 26);
            g.DrawString(Math.Round(w.Temp) + "°", temp, c.Brush(Canvas.Text), new RectangleF(x + 34, y, 60, 30), left);
            g.DrawString(w.Condition, tiny, c.Brush(Canvas.Dim), new RectangleF(x + 82, y, colW - 82, 30), left);
            y += 32;
            string range = (double.IsNaN(w.High) ? "" : "H " + Math.Round(w.High) + "°  L " + Math.Round(w.Low) + "°") +
                (w.RainChance >= 0 ? "   Rain " + w.RainChance + "%" : "");
            g.DrawString(range, tiny, c.Brush(Canvas.Text), new RectangleF(x, y, colW, 18), left);
            y += 22;
        }
        DateTime rise, set;
        if (SunTimes(t, out rise, out set))
        {
            Row(x, y, colW, "Sun", rise.ToString("HH:mm") + " – " + set.ToString("HH:mm"));
            y += 22;
        }

        int q5, q7;
        sessions.Quota(out q5, out q7);
        if (q5 >= 0 || q7 >= 0)
        {
            Bar(x, y, colW / 2 - 6, "5H", q5, true);
            Bar(x + colW / 2 + 6, y, colW / 2 - 6, "7D", q7, true);
            y += 30;
        }
        Status k = keyboard();
        if (k != null && y < H - 28)
        {
            if (KeyboardScreen.IsSplit)
            {
                Bar(x, y, colW / 2 - 6, "L", k.BatteryLeft, false);
                Bar(x + colW / 2 + 6, y, colW / 2 - 6, "R", k.BatteryRight, false);
            }
            else
                Bar(x, y, colW, "Battery", k.BatteryLeft, false);
        }
    }

    void Row(float x, float y, float w, string label, string value)
    {
        g.DrawString(label, tiny, c.Brush(Canvas.Dim), new RectangleF(x, y, w, 18), left);
        g.DrawString(value, tiny, c.Brush(Canvas.Text), new RectangleF(x, y, w, 18), right);
    }

    // label and number above a thin bar; quota warms up as it fills, a battery as it empties
    void Bar(float x, float y, float w, string label, int pct, bool quota)
    {
        Row(x, y, w, label, pct >= 0 && (quota || pct > 0) ? pct + "%" : "--");
        c.Fill(Canvas.Track, x, y + 19, w, 4, 2);
        if (pct <= 0)
            return;
        Color col = quota
            ? (pct >= 90 ? ClaudeView.Red : pct >= 70 ? Color.FromArgb(0xFF, 0x8A, 0x1F) : Canvas.Text)
            : (pct >= 50 ? Canvas.Text : pct >= 20 ? Color.FromArgb(0xFF, 0x8A, 0x1F) : ClaudeView.Red);
        c.Fill(col, x, y + 19, Math.Max(4, w * Math.Min(pct, 100) / 100f), 4, 2);
    }

    bool SunTimes(DateTime t, out DateTime rise, out DateTime set)
    {
        Place p = Weather.Location();
        return Astro.SunTimes(t, p != null ? p.Lat : FallbackLat, p != null ? p.Lon : FallbackLon, out rise, out set);
    }

    // ---- sky ----

    static readonly Color NightTop = Color.FromArgb(0x07, 0x0B, 0x1E), NightLow = Color.FromArgb(0x1A, 0x22, 0x44);
    static readonly Color DayTop = Color.FromArgb(0x2D, 0x6C, 0xC8), DayLow = Color.FromArgb(0x8E, 0xC5, 0xF0);
    static readonly Color GlowTop = Color.FromArgb(0x3A, 0x35, 0x6E), GlowLow = Color.FromArgb(0xF2, 0x9A, 0x5E);
    static readonly Color GreyTop = Color.FromArgb(0x4A, 0x52, 0x5E), GreyLow = Color.FromArgb(0x8D, 0x95, 0x9F);
    const int Ground = 146;

    void Sky(DateTime t, WeatherNow w, Sprite pet, double petMs, double ms)
    {
        DateTime rise, set;
        bool hasSun = SunTimes(t, out rise, out set);
        // light: 0 night .. 1 day, glow: how much dawn or dusk colour (peaks at sunrise and sunset)
        const double edge = 45; // minutes of twilight on each side
        double toRise = (t - rise).TotalMinutes, toSet = (set - t).TotalMinutes;
        double light = hasSun ? Clamp(Math.Min(toRise, toSet) / edge * 0.5 + 0.5) : (t.Hour >= 6 && t.Hour < 18 ? 1 : 0);
        double glow = hasSun ? Math.Max(0, 1 - Math.Min(Math.Abs(toRise), Math.Abs(toSet)) / edge) : 0;
        double grey = w == null ? 0 : w.Kind == "cloudy" || w.Kind == "fog" ? 0.7 : w.Wet || w.Kind == "snow" ? 0.85 : 0;

        Color top = Mix(Mix(Mix(NightTop, DayTop, light), GlowTop, glow * 0.7), Dim(GreyTop, 0.25 + 0.75 * light), grey);
        Color low = Mix(Mix(Mix(NightLow, DayLow, light), GlowLow, glow), Dim(GreyLow, 0.25 + 0.75 * light), grey);
        using (LinearGradientBrush sky = new LinearGradientBrush(new Rectangle(0, 0, W, Ground + 1), top, low, LinearGradientMode.Vertical))
            g.FillRectangle(sky, 0, 0, W, Ground);

        // stars, when it is dark and clear enough to see them
        double starAlpha = (1 - light) * (1 - grey);
        if (starAlpha > 0.05)
        {
            Random r = new Random(7);
            for (int i = 0; i < 36; i++)
            {
                int sx = r.Next(W), sy = r.Next(Ground - 30);
                double twinkle = 0.55 + 0.45 * Math.Sin(ms / 700.0 + i * 1.7);
                g.FillRectangle(c.Brush(Color.FromArgb((int)(255 * starAlpha * twinkle), 255, 255, 240)), sx, sy, r.Next(3) == 0 ? 2 : 1, 1);
            }
        }

        // the sun crosses from sunrise to sunset; the moon takes the night
        if (hasSun)
        {
            bool day = t >= rise && t <= set;
            double p;
            if (day)
                p = (t - rise).TotalMinutes / (set - rise).TotalMinutes;
            else
            {
                DateTime from = t > set ? set : set.AddDays(-1), to = t > set ? rise.AddDays(1) : rise;
                p = (t - from).TotalMinutes / (to - from).TotalMinutes;
            }
            float bx = (float)(18 + p * (W - 36)), by = (float)(Ground - 14 - Math.Sin(p * Math.PI) * 104);
            if (day)
                Sun(bx, by, grey);
            else
                Moon(bx, by, Astro.MoonPhase(t), grey);
        }

        // clouds drift across; more of them the greyer it is
        int clouds = w == null ? 1 : w.Kind == "clear" ? 0 : w.Kind == "partly" ? 2 : 4;
        Color cloud = Mix(Color.FromArgb(0x4B, 0x52, 0x66), Color.FromArgb(0xF4, 0xF6, 0xFA), light * (1 - grey * 0.5));
        for (int i = 0; i < clouds; i++)
        {
            double speed = 0.004 + i * 0.0015;
            float cx = (float)((ms * speed + i * 97) % (W + 80)) - 40, cy = 20 + (i * 37) % 70;
            Cloud(cx, cy, 1 + (i % 2) * 0.4f, cloud);
        }

        // rain or snow
        if (w != null && (w.Wet || w.Kind == "snow"))
        {
            Random r = new Random(11);
            bool snow = w.Kind == "snow";
            int count = snow ? 40 : w.Kind == "drizzle" ? 30 : 60;
            for (int i = 0; i < count; i++)
            {
                double speed = snow ? 0.02 + r.NextDouble() * 0.015 : 0.18 + r.NextDouble() * 0.08;
                float px = r.Next(W + 40), py = (float)((r.NextDouble() * Ground + ms * speed) % Ground);
                if (snow)
                {
                    px += (float)(Math.Sin(ms / 900.0 + i) * 4);
                    g.FillRectangle(c.Brush(Color.FromArgb(230, 255, 255, 255)), px % W, py, 2, 2);
                }
                else
                {
                    float sx = (px - py * 0.25f) % W;
                    using (Pen drop = new Pen(Color.FromArgb(170, 0x9C, 0xC8, 0xF2), 1))
                        g.DrawLine(drop, sx, py, sx - 2, py + 7);
                }
            }
        }

        // the ground, with Clawd on it
        g.FillRectangle(c.Brush(Mix(Color.FromArgb(0x0C, 0x0E, 0x12), Color.FromArgb(0x1F, 0x2A, 0x1E), light)), 0, Ground, W, H - Ground);
        g.FillRectangle(c.Brush(Color.FromArgb(40, 255, 255, 255)), 0, Ground, W, 1);
        if (pet != null)
        {
            float s = ArtScale(pet, 3);
            pet.Draw(g, pet.FrameAt(petMs), 14, (int)(Ground + 8 - pet.Height * s), s);
        }

        // the time over the sky, with a soft shadow so it reads on any colour
        Color shade = Color.FromArgb(110, 0, 0, 0);
        DrawTime(t, clockBig, W / 2f + 2, 16, shade, true);
        DrawTime(t, clockBig, W / 2f, 14, Canvas.Text, true);
        string date = t.ToString("dddd, d MMMM", CultureInfo.InvariantCulture);
        g.DrawString(date, strong, c.Brush(shade), new RectangleF(1, 86, W, 20), center);
        g.DrawString(date, strong, c.Brush(Canvas.Text), new RectangleF(0, 85, W, 20), center);
        if (w != null)
            g.DrawString(Math.Round(w.Temp) + "°  " + w.Condition, small, c.Brush(Canvas.Text), new RectangleF(110, Ground + 2, W - 120, H - Ground - 2), right);
    }

    void Sun(float x, float y, double grey)
    {
        int a = (int)(255 * (1 - grey * 0.6));
        g.FillEllipse(c.Brush(Color.FromArgb(a / 5, 0xFF, 0xD3, 0x6B)), x - 18, y - 18, 36, 36);
        g.FillEllipse(c.Brush(Color.FromArgb(a, 0xFF, 0xD3, 0x6B)), x - 10, y - 10, 20, 20);
    }

    // a lit disc with the dark part of the phase laid over it
    void Moon(float x, float y, double phase, double grey)
    {
        int a = (int)(255 * (1 - grey * 0.6));
        const float r = 9;
        double lit = 1 - Math.Abs(phase - 0.5) * 2; // 0 new .. 1 full
        if (lit < 0.08)
            return; // a new moon is not to be seen
        g.FillEllipse(c.Brush(Color.FromArgb(a, 0xEE, 0xEB, 0xDD)), x - r, y - r, 2 * r, 2 * r);
        if (lit > 0.97)
            return;
        float shift = (float)(lit * 2 * r) * (phase < 0.5 ? 1 : -1);
        GraphicsState state = g.Save();
        using (GraphicsPath disc = new GraphicsPath())
        {
            disc.AddEllipse(x - r, y - r, 2 * r, 2 * r);
            g.SetClip(disc);
            g.FillEllipse(c.Brush(Color.FromArgb(230, 0x10, 0x14, 0x26)), x - r - shift, y - r, 2 * r, 2 * r);
        }
        g.Restore(state);
    }

    void Cloud(float x, float y, float s, Color col)
    {
        SolidBrush b = c.Brush(Color.FromArgb(220, col));
        g.FillEllipse(b, x, y + 6 * s, 22 * s, 14 * s);
        g.FillEllipse(b, x + 10 * s, y, 22 * s, 20 * s);
        g.FillEllipse(b, x + 24 * s, y + 5 * s, 22 * s, 15 * s);
        g.FillRectangle(b, x + 8 * s, y + 12 * s, 30 * s, 8 * s);
    }

    // ---- weather icons, drawn rather than stored so they scale with the layout ----

    void WeatherIcon(WeatherNow w, float x, float y, float size)
    {
        float s = size / 24f;
        string kind = w.Kind;
        Color sunCol = Color.FromArgb(0xFF, 0xC9, 0x4A), cloudCol = Color.FromArgb(0xD5, 0xD9, 0xDF);
        if (kind == "clear" || kind == "partly")
        {
            if (w.IsDay)
            {
                using (Pen ray = new Pen(sunCol, 2 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4;
                        g.DrawLine(ray, x + 12 * s + (float)Math.Cos(a) * 8 * s, y + 12 * s + (float)Math.Sin(a) * 8 * s,
                            x + 12 * s + (float)Math.Cos(a) * 11 * s, y + 12 * s + (float)Math.Sin(a) * 11 * s);
                    }
                g.FillEllipse(c.Brush(sunCol), x + 7 * s, y + 7 * s, 10 * s, 10 * s);
            }
            else
            {
                g.FillEllipse(c.Brush(Color.FromArgb(0xEE, 0xEB, 0xDD)), x + 5 * s, y + 4 * s, 14 * s, 14 * s);
                g.FillEllipse(c.Brush(Canvas.Background), x + 10 * s, y + 1 * s, 13 * s, 13 * s);
            }
            if (kind == "clear")
                return;
            Cloud(x + 4 * s, y + 10 * s, 0.45f * s, cloudCol);
            return;
        }
        if (kind == "fog")
        {
            using (Pen line = new Pen(cloudCol, 2 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                for (int i = 0; i < 3; i++)
                    g.DrawLine(line, x + (2 + i * 2) * s, y + (7 + i * 5) * s, x + (22 - i * 2) * s, y + (7 + i * 5) * s);
            return;
        }
        Cloud(x, y + 2 * s, 0.5f * s, cloudCol);
        if (kind == "rain" || kind == "drizzle")
            using (Pen drop = new Pen(Color.FromArgb(0x6F, 0xB2, 0xF0), 2 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                for (int i = 0; i < (kind == "rain" ? 3 : 2); i++)
                    g.DrawLine(drop, x + (7 + i * 5) * s, y + 17 * s, x + (5 + i * 5) * s, y + 22 * s);
        else if (kind == "snow")
            for (int i = 0; i < 3; i++)
                g.FillEllipse(c.Brush(Color.White), x + (6 + i * 5) * s, y + (17 + (i % 2) * 3) * s, 3 * s, 3 * s);
        else if (kind == "storm")
            g.FillPolygon(c.Brush(sunCol), new[] { new PointF(x + 12 * s, y + 13 * s), new PointF(x + 8 * s, y + 20 * s), new PointF(x + 12 * s, y + 20 * s),
                new PointF(x + 10 * s, y + 25 * s), new PointF(x + 16 * s, y + 17 * s), new PointF(x + 12 * s, y + 17 * s) });
    }

    // ---- colour helpers ----

    static double Clamp(double v)
    {
        return Math.Max(0, Math.Min(1, v));
    }

    static Color Mix(Color a, Color b, double t)
    {
        t = Clamp(t);
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    static Color Dim(Color col, double k)
    {
        return Color.FromArgb((int)(col.R * k), (int)(col.G * k), (int)(col.B * k));
    }
}
