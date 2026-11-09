// Which screens the hub shows and when. Screens are named by IScreen.Id.
sealed class Config
{
    public string Mode = "pinned";                       // "pinned": one screen; "rotate": cycle through Rotate
    public string Pinned = "keyboard";
    public string[] Rotate = { "keyboard", "claude" };
    public int RotateSeconds = 15;
    public string[] Takeover = { "claude" };             // may take the hub over while they have something urgent
    public bool LayerOverlay = true;                     // a layer or modifier change shows the keyboard for a moment
}
