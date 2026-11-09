using System;
using System.Diagnostics;
using System.Threading;

// Decides what the hub shows. The keyboard screen is the default; while a Claude Code session has
// something going on (see ClaudeSessions.Current) the Claude screen takes over and keeps the panel lit
// even when the keyboard idles. A layer or modifier change still brings the keyboard screen up for a
// moment, then Claude comes back.
sealed class Display
{
    const double KeyboardHoldMs = 2000;

    readonly ClaudeScreen screen;
    readonly ClaudeSessions sessions;
    readonly Action<byte[]> push;
    readonly Action<bool> claudePower;   // Claude wants the backlight on
    readonly Action showKeyboard;        // re-render the keyboard screen once
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly AutoResetEvent wake = new AutoResetEvent(false);
    readonly object sync = new object();

    bool claudeActive;                   // a session has something to show
    bool claudeShown;                    // the Claude screen is on the hub right now
    double keyboardUntil = double.NegativeInfinity;
    Status lastStatus;

    public Display(ClaudeScreen screen, ClaudeSessions sessions, Action<byte[]> push, Action<bool> claudePower, Action showKeyboard)
    {
        this.screen = screen;
        this.sessions = sessions;
        this.push = push;
        this.claudePower = claudePower;
        this.showKeyboard = showKeyboard;
        Thread t = new Thread(Loop);
        t.IsBackground = true;
        t.Start();
    }

    public double Now
    {
        get { return clock.Elapsed.TotalMilliseconds; }
    }

    // A session changed: look again right away.
    public void ClaudeChanged()
    {
        wake.Set();
    }

    // Every report from the dongle, before the animator sees it. A visible change (layer, modifiers)
    // gives the keyboard screen the hub for a moment; activity alone does not.
    public void KeyboardStatus(Status s)
    {
        lock (sync)
        {
            Status prev = lastStatus;
            lastStatus = s;
            if (prev != null && (prev.Layer != s.Layer || prev.LayerName != s.LayerName || prev.Mods != s.Mods))
            {
                keyboardUntil = Now + KeyboardHoldMs;
                wake.Set();
            }
        }
    }

    // Frames from the keyboard animator go out only while the keyboard screen owns the hub.
    public void KeyboardFrame(byte[] frame)
    {
        lock (sync)
            if (!claudeActive || Now < keyboardUntil)
                push(frame);
    }

    void Loop()
    {
        while (true)
        {
            double now = Now;
            ClaudeView v = sessions.Current(now);
            bool show;
            lock (sync)
            {
                claudeActive = v != null;
                show = claudeActive && now >= keyboardUntil;
            }

            int wait;
            if (show)
            {
                if (!claudeShown)
                {
                    claudeShown = true;
                    claudePower(true);
                }
                push(screen.Render(v, now - v.PetStart));
                // next pet frame, and at least once a second for the clocks in the text
                Sprite pet = screen.Pet(v.Pet);
                double next = pet != null ? Math.Min(1000, pet.UntilNextFrame(Now - v.PetStart)) : 1000;
                wait = (int)Math.Ceiling(Math.Max(1, next));
            }
            else
            {
                if (claudeShown)
                {
                    claudeShown = false;
                    claudePower(false);
                    showKeyboard();
                }
                // while Claude waits behind the keyboard screen, come back when the hold ends
                wait = claudeActive ? (int)Math.Ceiling(Math.Max(1, keyboardUntil - now)) : 1000;
            }
            wake.WaitOne(wait);
        }
    }
}
