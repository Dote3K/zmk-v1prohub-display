using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

// Turns status changes into short transitions and pushes frames until the screen settles, then sleeps.
// A change that arrives mid-transition continues from whatever is on screen at that moment.
sealed class Animator
{
    const double LayerMs = 260, ModMs = 170, MinFrameMs = 16;
    const float NameShift = 34;

    // a value easing from one point to another over LayerMs, restartable from where it currently is
    sealed class Tween
    {
        public float From, To;
        public double Start = double.MinValue;

        public float At(double now)
        {
            return From + (To - From) * Ease(Progress(now));
        }

        public bool Done(double now)
        {
            return Progress(now) >= 1;
        }

        public void Retarget(float to, double now)
        {
            From = At(now);
            To = to;
            Start = now;
        }

        float Progress(double now)
        {
            return (float)Math.Min(1, Math.Max(0, (now - Start) / LayerMs));
        }
    }

    sealed class NameSprite
    {
        public string Text;
        public Tween Offset = new Tween(), Alpha = new Tween();
    }

    readonly Renderer renderer;
    readonly Action<byte[]> send;
    readonly AutoResetEvent changed = new AutoResetEvent(false);
    readonly object sync = new object();
    readonly Stopwatch clock = Stopwatch.StartNew();

    Status target;
    string shownName;
    int shownLayer, maxLayer;
    readonly List<NameSprite> names = new List<NameSprite>();
    readonly Tween indicator = new Tween();
    readonly float[] mods = new float[4]; // linear presence, eased when drawn
    double lastTick;

    public Animator(Renderer renderer, Action<byte[]> send)
    {
        this.renderer = renderer;
        this.send = send;
        Thread t = new Thread(Loop);
        t.IsBackground = true;
        t.Start();
    }

    public void Show(Status s)
    {
        lock (sync)
            target = s;
        changed.Set();
    }

    void Loop()
    {
        while (true)
        {
            changed.WaitOne();
            lock (sync)
                lastTick = clock.Elapsed.TotalMilliseconds;

            bool moving = true;
            while (moving)
            {
                double started = clock.Elapsed.TotalMilliseconds;
                View v;
                lock (sync)
                    moving = Step(out v);
                send(renderer.Render(v));

                double spent = clock.Elapsed.TotalMilliseconds - started;
                if (moving && spent < MinFrameMs)
                    Thread.Sleep((int)(MinFrameMs - spent));
            }
        }
    }

    // Advances the visual state to now; returns true while something is still in motion.
    bool Step(out View v)
    {
        double now = clock.Elapsed.TotalMilliseconds;
        float dt = (float)((now - lastTick) / ModMs);
        lastTick = now;

        Status s = target;
        string name = View.LayerLabel(s);
        if (name != shownName || s.Layer != shownLayer)
        {
            // everything on screen slides out from where it is; the new name slides in (up for a higher layer)
            float dir = shownName == null || s.Layer >= shownLayer ? 1 : -1;
            NameSprite back = names.FindLast(n => n.Text == name);
            foreach (NameSprite n in names)
            {
                if (n == back)
                    continue;
                n.Offset.Retarget(-dir * NameShift, now);
                n.Alpha.Retarget(0, now);
            }
            if (back != null)
            {
                // the name is still fading out: bring that copy back instead of stacking a second one
                back.Offset.Retarget(0, now);
                back.Alpha.Retarget(1, now);
            }
            else
            {
                NameSprite fresh = new NameSprite { Text = name };
                fresh.Offset.From = dir * NameShift;
                fresh.Offset.Start = now;
                fresh.Alpha.To = 1;
                fresh.Alpha.Start = now;
                names.Add(fresh);
            }

            indicator.Retarget(s.Layer, now);
            maxLayer = Math.Max(maxLayer, s.Layer);
            shownName = name;
            shownLayer = s.Layer;
        }

        bool moving = !indicator.Done(now);
        for (int i = names.Count - 1; i >= 0; i--)
        {
            NameSprite n = names[i];
            bool done = n.Alpha.Done(now) && n.Offset.Done(now);
            if (done && n.Alpha.To == 0)
            {
                names.RemoveAt(i);
                continue;
            }
            moving |= !done;
        }
        v = new View();
        foreach (NameSprite n in names)
            v.Names.Add(new View.Name { Text = n.Text, Offset = n.Offset.At(now), Alpha = n.Alpha.At(now) });

        bool[] want = { s.Ctrl, s.Shift, s.Alt, s.Gui };
        for (int i = 0; i < 4; i++)
        {
            float goal = want[i] ? 1 : 0;
            mods[i] = goal > mods[i] ? Math.Min(goal, mods[i] + dt) : Math.Max(goal, mods[i] - dt);
            moving |= mods[i] != goal;
            v.Mods[i] = Ease(mods[i]);
        }

        v.Indicator = indicator.At(now);
        v.MaxLayer = maxLayer;
        v.BatteryLeft = s.BatteryLeft;
        v.BatteryRight = s.BatteryRight;
        return moving;
    }

    static float Ease(float t)
    {
        float u = 1 - t;
        return 1 - u * u * u; // ease-out cubic
    }
}
