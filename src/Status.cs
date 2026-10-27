// Keyboard state as reported by the dongle.
sealed class Status
{
    public int Layer;
    public string LayerName = "";
    public byte Mods;        // HID modifier bitmask: bit0..3 left Ctrl/Shift/Alt/GUI, bit4..7 right
    public int BatteryLeft;  // %, 0 = unknown
    public int BatteryRight;

    public bool Ctrl { get { return (Mods & 0x11) != 0; } }
    public bool Shift { get { return (Mods & 0x22) != 0; } }
    public bool Alt { get { return (Mods & 0x44) != 0; } }
    public bool Gui { get { return (Mods & 0x88) != 0; } }
}
