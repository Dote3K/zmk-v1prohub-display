using System;
using System.Collections.Generic;

// The keyboard status screen. Status changes turn into short transitions: names slide, the layer
// indicator glides, modifier icons grow in and out. A change that arrives mid-transition continues from
// whatever is on screen at that moment.
sealed class KeyboardScreen : IScreen
{
    const double LayerMs = 260, ModMs = 170, FrameMs = 16;
    const float NameShift = 34;

    sealed class NameSprite
    {
        public string Text;
        public Tween Offset = new Tween(LayerMs), Alpha = new Tween(LayerMs);
    }

    readonly Renderer renderer;
    readonly object sync = new object();

    Status target = new Status { LayerName = "Offline" }; // until the dongle reports
    string shownName;
    int shownLayer, maxLayer;
    readonly List<NameSprite> names = new List<NameSprite>();
    readonly Tween indicator = new Tween(LayerMs);
    readonly float[] mods = new float[4]; // linear presence, eased when drawn
    double lastTick = double.NaN;

    public KeyboardScreen(Renderer renderer)
    {
        this.renderer = renderer;
    }

    public string Id { get { return "keyboard"; } }
    public string Title { get { return "Keyboard"; } }

    public void Show(Status s)
    {
        lock (sync)
            target = s;
    }

    public bool Urgent(double now)
    {
        return false;
    }

    public byte[] Render(double now, float brightness, out double next)
    {
        View v;
        bool moving;
        lock (sync)
            moving = Step(now, out v);
        v.Screen = brightness;
        next = moving ? FrameMs : double.PositiveInfinity;
        return renderer.Render(v);
    }

    // Advances the visual state to now; returns true while something is still in motion.
    bool Step(double now, out View v)
    {
        // after a long pause (another screen was up) everything simply arrives
        float dt = double.IsNaN(lastTick) ? 1 : (float)((now - lastTick) / ModMs);
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
            v.Mods[i] = Tween.Ease(mods[i]);
        }

        v.Indicator = indicator.At(now);
        v.MaxLayer = maxLayer;
        v.BatteryLeft = s.BatteryLeft;
        v.BatteryRight = s.BatteryRight;
        return moving;
    }
}
