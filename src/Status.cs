using System.Text;

// Keyboard state as reported by the dongle (cygnus-dongle src/status.c, protocol v1).
sealed class Status
{
    public const byte ReportId = 0xC5;
    const byte Version = 1;

    public int Layer;
    public string LayerName = "";
    public byte Mods;        // HID modifier bitmask: bit0..3 left Ctrl/Shift/Alt/GUI, bit4..7 right
    public int BatteryLeft;  // %, 0 = unknown
    public int BatteryRight;

    public bool Ctrl { get { return (Mods & 0x11) != 0; } }
    public bool Shift { get { return (Mods & 0x22) != 0; } }
    public bool Alt { get { return (Mods & 0x44) != 0; } }
    public bool Gui { get { return (Mods & 0x88) != 0; } }

    // Returns null for anything that is not a v1 status report.
    public static Status Parse(byte[] r)
    {
        if (r.Length < 32 || r[0] != ReportId || r[1] != Version)
            return null;
        int end = 8;
        while (end < 32 && r[end] != 0)
            end++;
        return new Status
        {
            Layer = r[2],
            Mods = r[3],
            BatteryLeft = r[4],
            BatteryRight = r[5],
            LayerName = Encoding.ASCII.GetString(r, 8, end - 8),
        };
    }
}
