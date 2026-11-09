using System.Collections.Generic;

// What one frame shows, possibly in the middle of transitions. All values are already eased.
sealed class View
{
    public sealed class Name
    {
        public string Text;
        public float Offset;   // vertical shift in px, 0 = resting position
        public float Alpha;    // 0..1
    }

    public List<Name> Names = new List<Name>();  // layer names on screen, oldest first
    public float Indicator;                      // layer track position, fractional while sliding
    public int MaxLayer;                         // highest layer the track has room for
    public float[] Mods = new float[4];          // presence of Ctrl, Shift, Alt, GUI: 0 hidden .. 1 shown
    public int BatteryLeft, BatteryRight;
    public bool Split = true;                    // two halves, so two batteries; one bar otherwise
    public float Screen = 1;                     // overall brightness: 0 screen off .. 1 fully on

    public static string LayerLabel(Status s)
    {
        return s.LayerName.Length > 0 ? s.LayerName : "Layer " + s.Layer;
    }

    public static View Settled(Status s)
    {
        View v = new View();
        v.Names.Add(new Name { Text = LayerLabel(s), Alpha = 1 });
        v.Indicator = v.MaxLayer = s.Layer;
        v.Mods = new float[] { s.Ctrl ? 1 : 0, s.Shift ? 1 : 0, s.Alt ? 1 : 0, s.Gui ? 1 : 0 };
        v.BatteryLeft = s.BatteryLeft;
        v.BatteryRight = s.BatteryRight;
        v.Screen = s.Active ? 1 : 0;
        return v;
    }
}
