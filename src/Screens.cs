using System;
using System.Collections.Generic;
using System.IO;

// One kind of hub screen. The ScreenManager decides which one is up; a screen only draws itself and
// says whether it has something that should take the hub over. A new kind of screen implements this
// and is added to the list in Program.
interface IScreen
{
    string Id { get; }       // name in config.json: "keyboard", "claude"
    string Title { get; }    // name in the tray menu

    // has something that should take the hub over (when the config allows it) and keep the panel lit
    bool Urgent(double now);
    bool CanTakeover { get; }   // whether Urgent can ever be true; offered under "Take over the hub" if so

    // this screen's own settings, shown on its page of the settings window; empty for none
    IList<Field> Fields();

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

    public bool CanTakeover { get { return true; } }

    public IList<Field> Fields()
    {
        bool hooks, statusLine;
        ClaudeHooks.Installed(out hooks, out statusLine);
        string pets = Sprite.DefaultDir();
        return new List<Field>
        {
            Field.Info("hooks", "Claude Code connection",
                () => hooks ? (statusLine ? "Hooks and status line installed" : "Hooks installed; the status line is your own, so there is no quota") : "Not installed"),
            Field.Button("install", hooks ? "Remove from Claude Code" : "Install into Claude Code",
                "Adds or removes the hooks and the status line in " + ClaudeHooks.SettingsFile() + ". The previous file is backed up next to it.",
                () =>
                {
                    string message;
                    ClaudeHooks.Apply(!hooks, out message);
                    return message;
                }),
            Field.Info("quota", "Quota", sessions.QuotaInfo),
            Field.Info("pets", "Pets folder", () => pets + (Directory.Exists(pets) ? "  ·  " + Directory.GetFiles(pets, "*.json").Length + " pets" : "  ·  none yet")),
            Field.Button("openPets", "Open pets folder", "Pets are made with tools/clawd-frames.", () =>
            {
                Directory.CreateDirectory(pets);
                Settings.Open(pets);
                return "";
            }),
        };
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
