using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

// Decides which screen is on the hub, fades the panel on and off, and pushes the frames. In order:
//  1. layer overlay: a layer or modifier change shows the keyboard screen for a moment
//  2. takeover: a screen listed in Config.Takeover that has something urgent (Claude working or waiting)
//  3. the mode: one pinned screen, or a rotation through several
// The panel is lit while the keyboard is in use or the screen on the hub is urgent, and fades out
// otherwise. Switching screens dips through black.
sealed class ScreenManager
{
    const double OnMs = 200, OffMs = 900, SwitchMs = 280, OverlayMs = 2000, FrameMs = 16, RecheckMs = 1000;

    readonly KeyboardScreen keyboard;
    readonly List<IScreen> screens = new List<IScreen>();
    readonly Action<byte[]> push;
    readonly Action<bool> power;          // backlight: on before a fade-in, off once faded out
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly AutoResetEvent wake = new AutoResetEvent(false);
    readonly object sync = new object();

    Config config;
    Status lastStatus;
    bool keyboardActive;                  // dongle says the keyboard is in use
    double overlayUntil = double.NegativeInfinity, rotateAt;
    int rotateIndex;

    // drawing state, touched only by the loop thread
    readonly Tween light = new Tween(0, OnMs);
    IScreen shown, leaving;
    double switchAt = double.NegativeInfinity;
    bool powered;

    public ScreenManager(KeyboardScreen keyboard, IEnumerable<IScreen> others, Config config, Action<byte[]> push, Action<bool> power)
    {
        this.keyboard = keyboard;
        screens.Add(keyboard);
        screens.AddRange(others);
        this.config = config;
        rotateAt = config.RotateSeconds * 1000.0;
        this.push = push;
        this.power = power;
        Thread t = new Thread(Loop);
        t.IsBackground = true;
        t.Start();
    }

    public double Now
    {
        get { return clock.Elapsed.TotalMilliseconds; }
    }

    public IList<IScreen> Screens
    {
        get { return screens.AsReadOnly(); }
    }

    public Config Config
    {
        get { return config; }
    }

    // A new config takes effect right away; a rotation starts over at its first screen.
    public void Configure(Config c)
    {
        lock (sync)
        {
            config = c;
            rotateIndex = 0;
            rotateAt = Now + c.RotateSeconds * 1000.0;
        }
        wake.Set();
    }

    // Something a screen shows has changed.
    public void Wake()
    {
        wake.Set();
    }

    // Every report from the dongle. A visible change (layer, modifiers) brings the keyboard screen up
    // for a moment; activity alone only keeps the panel lit.
    public void KeyboardStatus(Status s)
    {
        keyboard.Show(s);
        lock (sync)
        {
            if (lastStatus != null && (lastStatus.Layer != s.Layer || lastStatus.LayerName != s.LayerName || lastStatus.Mods != s.Mods))
                overlayUntil = Now + OverlayMs;
            lastStatus = s;
            keyboardActive = s.Active;
        }
        wake.Set();
    }

    // The dongle went away: the keyboard no longer keeps the panel lit.
    public void KeyboardGone()
    {
        lock (sync)
            keyboardActive = false;
        wake.Set();
    }

    IScreen Find(string id)
    {
        foreach (IScreen s in screens)
            if (s.Id == id)
                return s;
        return null;
    }

    // Which screen should be up now, and whether it is urgent (which keeps the panel lit).
    IScreen Pick(double now, out bool urgent)
    {
        Config c = config;
        urgent = false;
        if (c.LayerOverlay && now < overlayUntil)
            return keyboard;
        foreach (string id in c.Takeover)
        {
            IScreen s = Find(id);
            if (s != null && s.Urgent(now))
            {
                urgent = true;
                return s;
            }
        }

        List<IScreen> list = new List<IScreen>();
        foreach (string id in c.Mode == "rotate" ? c.Rotate : new[] { c.Pinned })
        {
            IScreen s = Find(id);
            if (s != null)
                list.Add(s);
        }
        if (list.Count == 0)
            list.Add(keyboard);
        if (list.Count > 1 && now >= rotateAt)
        {
            rotateIndex++;
            rotateAt = now + c.RotateSeconds * 1000.0;
        }
        IScreen pick = list[rotateIndex % list.Count];
        urgent = pick.Urgent(now); // e.g. pinned Claude while it works keeps the panel lit
        return pick;
    }

    void Loop()
    {
        while (true)
        {
            double now = Now;
            IScreen pick;
            bool urgent, active, rotating;
            double overlayEnd, rotateEnd;
            lock (sync)
            {
                pick = Pick(now, out urgent);
                active = keyboardActive;
                rotating = config.Mode == "rotate" && config.Rotate.Length > 1;
                overlayEnd = overlayUntil;
                rotateEnd = rotateAt;
            }

            bool lit = active || urgent;
            if (lit != (light.To == 1))
            {
                light.Retarget(lit ? 1 : 0, now); // From is taken at the old duration, so no jump
                light.Ms = lit ? OnMs : OffMs;
            }
            if (lit && !powered)
            {
                powered = true;
                power(true);
            }

            if (pick != shown)
            {
                // dip through black, unless the panel is dark anyway
                if (shown != null && light.At(now) > 0.01f)
                {
                    leaving = shown;
                    switchAt = now;
                }
                shown = pick;
            }

            double wait = RecheckMs; // urgency changes with time (e.g. DONE expiring)
            if (powered)
            {
                float level = light.At(now);
                IScreen draw = shown;
                float t = (float)((now - switchAt) / SwitchMs);
                bool switching = leaving != null && t < 1;
                if (switching)
                {
                    if (t < 0.5f)
                    {
                        draw = leaving;
                        level *= 1 - 2 * t;
                    }
                    else
                        level *= 2 * t - 1;
                }
                else
                    leaving = null;

                double next;
                push(draw.Render(now, level, out next));
                if (!lit && light.Done(now))
                {
                    powered = false;
                    power(false);
                }
                else
                {
                    wait = Math.Min(wait, next);
                    if (switching || !light.Done(now))
                        wait = Math.Min(wait, FrameMs);
                }
            }
            if (now < overlayEnd)
                wait = Math.Min(wait, overlayEnd - now);
            if (rotating)
                wait = Math.Min(wait, rotateEnd - now);

            // a frame that took long eats into the wait, so motion keeps its pace
            wait -= Now - now;
            wake.WaitOne((int)Math.Ceiling(Math.Max(1, wait)));
        }
    }
}
