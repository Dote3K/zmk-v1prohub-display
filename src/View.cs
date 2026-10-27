// What one frame shows, possibly in the middle of a transition. Fractions are already eased.
sealed class View
{
    public string NameFrom = "", NameTo = "";
    public int LayerFrom, LayerTo;
    public float LayerT = 1;              // 0 = showing LayerFrom, 1 = showing LayerTo
    public float[] Mods = new float[4];   // presence of Ctrl, Shift, Alt, GUI: 0 hidden .. 1 shown
    public int BatteryLeft, BatteryRight;

    public static string LayerLabel(Status s)
    {
        return s.LayerName.Length > 0 ? s.LayerName : "Layer " + s.Layer;
    }

    public static View Settled(Status s)
    {
        View v = new View();
        v.NameFrom = v.NameTo = LayerLabel(s);
        v.LayerFrom = v.LayerTo = s.Layer;
        v.Mods = new float[] { s.Ctrl ? 1 : 0, s.Shift ? 1 : 0, s.Alt ? 1 : 0, s.Gui ? 1 : 0 };
        v.BatteryLeft = s.BatteryLeft;
        v.BatteryRight = s.BatteryRight;
        return v;
    }
}
