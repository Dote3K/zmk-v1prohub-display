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
}
