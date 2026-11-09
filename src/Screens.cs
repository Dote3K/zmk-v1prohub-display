using System;

// One kind of hub screen. The ScreenManager decides which one is up; a screen only draws itself and
// says whether it has something that should take the hub over. A new kind of screen implements this
// and is added to the list in Program.
interface IScreen
{
    string Id { get; }       // name in config.json: "keyboard", "claude"
    string Title { get; }    // name in the tray menu

    // has something that should take the hub over (when the config allows it) and keep the panel lit
    bool Urgent(double now);

    // A frame at the given brightness (0 off .. 1 full). next: ms until it wants another frame, or
    // PositiveInfinity when only an outside change (ScreenManager.Wake) can alter it.
    byte[] Render(double now, float brightness, out double next);
}

// Claude Code sessions: the busiest one while something is going on, an idle page with the quota
// otherwise (seen when the screen is pinned or in the rotation).
sealed class ClaudeScreen : IScreen
{
    readonly ClaudeRenderer renderer;
    readonly ClaudeSessions sessions;

    public ClaudeScreen(ClaudeRenderer renderer, ClaudeSessions sessions)
    {
        this.renderer = renderer;
        this.sessions = sessions;
    }

    public string Id { get { return "claude"; } }
    public string Title { get { return "Claude Code"; } }

    public bool Urgent(double now)
    {
        return sessions.Current(now) != null;
    }

    public byte[] Render(double now, float brightness, out double next)
    {
        ClaudeView v = sessions.Current(now) ?? sessions.Idle(now);
        v.Screen = brightness;
        double ms = now - v.PetStart;
        byte[] frame = renderer.Render(v, ms);
        // the pet's next frame, and at least once a second for the clocks in the text
        next = Math.Min(1000, renderer.UntilNextFrame(v.Pet, ms));
        return frame;
    }
}
