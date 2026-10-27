using System;
using System.Diagnostics;
using System.Threading;

// Turns status changes into short transitions and pushes frames until the screen settles, then sleeps.
sealed class Animator
{
    const double LayerMs = 220, ModMs = 150, MinFrameMs = 16;

    readonly Renderer renderer;
    readonly Action<byte[]> send;
    readonly AutoResetEvent changed = new AutoResetEvent(false);
    readonly object sync = new object();
    readonly Stopwatch clock = Stopwatch.StartNew();

    Status target;
    string nameFrom = "", nameTo = "";
    int layerFrom, layerTo;
    double layerStart = double.MinValue;
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
        if (s.Layer != layerTo || name != nameTo)
        {
            nameFrom = nameTo;
            layerFrom = layerTo;
            nameTo = name;
            layerTo = s.Layer;
            layerStart = now;
        }
        float lt = (float)Math.Min(1, (now - layerStart) / LayerMs);

        bool[] want = { s.Ctrl, s.Shift, s.Alt, s.Gui };
        bool modsMoving = false;
        float[] shown = new float[4];
        for (int i = 0; i < 4; i++)
        {
            float goal = want[i] ? 1 : 0;
            mods[i] = goal > mods[i] ? Math.Min(goal, mods[i] + dt) : Math.Max(goal, mods[i] - dt);
            modsMoving |= mods[i] != goal;
            shown[i] = Ease(mods[i]);
        }

        v = new View
        {
            NameFrom = nameFrom,
            NameTo = nameTo,
            LayerFrom = layerFrom,
            LayerTo = layerTo,
            LayerT = Ease(lt),
            Mods = shown,
            BatteryLeft = s.BatteryLeft,
            BatteryRight = s.BatteryRight,
        };
        return lt < 1 || modsMoving;
    }

    static float Ease(float t)
    {
        float u = 1 - t;
        return 1 - u * u * u; // ease-out cubic
    }
}
