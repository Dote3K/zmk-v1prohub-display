using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

// The icon in the notification area: choose what the hub shows without editing config.json. Every
// change is applied right away and saved to config.json.
sealed class Tray : IDisposable
{
    static readonly int[] RotateChoices = { 5, 10, 15, 30, 60 };

    readonly ScreenManager manager;
    readonly NotifyIcon icon;
    readonly ContextMenuStrip menu = new ContextMenuStrip();

    readonly Action openSettings;

    public Tray(ScreenManager manager, Action openSettings)
    {
        this.manager = manager;
        this.openSettings = openSettings;
        menu.Items.Add("..."); // an empty strip refuses to open; the real items are built on opening
        menu.Opening += (s, e) => Build();
        icon = new NotifyIcon { Icon = MakeIcon(), Text = "ZMK hub display", ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (s, e) => openSettings();
    }

    // rebuilt on every opening, so it always matches the config (also after a hand edit)
    void Build()
    {
        Config c = manager.Config;
        IList<IScreen> screens = manager.Screens;
        menu.Items.Clear();

        ToolStripMenuItem settings = Item("Settings…", false, openSettings);
        settings.Font = new Font(settings.Font, FontStyle.Bold); // the double-click action
        menu.Items.Add(settings);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Hub screen") { Enabled = false });
        foreach (IScreen s in screens)
        {
            string id = s.Id;
            menu.Items.Add(Item("Pin: " + s.Title, c.Mode == "pinned" && c.Pinned == id, () => Change(x =>
            {
                x.Mode = "pinned";
                x.Pinned = id;
            })));
        }
        menu.Items.Add(Item("Rotate", c.Mode == "rotate", () => Change(x => x.Mode = "rotate")));

        ToolStripMenuItem through = new ToolStripMenuItem("Rotate through");
        foreach (IScreen s in screens)
        {
            string id = s.Id;
            through.DropDownItems.Add(Item(s.Title, c.Rotate.Contains(id), () => Change(x =>
            {
                bool on = !x.Rotate.Contains(id);
                // kept in menu order; the last one cannot be taken out
                string[] next = screens.Select(t => t.Id).Where(t => t == id ? on : x.Rotate.Contains(t)).ToArray();
                if (next.Length > 0)
                    x.Rotate = next;
            })));
        }
        menu.Items.Add(through);

        ToolStripMenuItem every = new ToolStripMenuItem("Time per screen");
        foreach (int sec in RotateChoices)
        {
            int n = sec;
            every.DropDownItems.Add(Item(n + " seconds", c.RotateSeconds == n, () => Change(x => x.RotateSeconds = n)));
        }
        menu.Items.Add(every);

        menu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem takeover = new ToolStripMenuItem("Take over the hub when busy");
        foreach (IScreen s in screens.Where(t => t.Id != "keyboard")) // the keyboard is never urgent
        {
            string id = s.Id;
            takeover.DropDownItems.Add(Item(s.Title, c.Takeover.Contains(id), () => Change(x =>
                x.Takeover = x.Takeover.Contains(id) ? x.Takeover.Where(t => t != id).ToArray() : x.Takeover.Concat(new[] { id }).ToArray())));
        }
        menu.Items.Add(takeover);
        menu.Items.Add(Item("Show layer changes", c.LayerOverlay, () => Change(x => x.LayerOverlay = !x.LayerOverlay)));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Open config.json", false, OpenConfig));
        menu.Items.Add(Item("Quit", false, () =>
        {
            icon.Visible = false;
            Application.Exit();
        }));
    }

    static ToolStripMenuItem Item(string text, bool check, Action click)
    {
        ToolStripMenuItem item = new ToolStripMenuItem(text) { Checked = check };
        item.Click += (s, e) => click();
        return item;
    }

    static void Change(Action<Config> edit)
    {
        Settings.Edit(edit);
    }

    static void OpenConfig()
    {
        Settings.Open(Config.FilePath);
    }

    // a tiny pixel Clawd on a dark tile
    static Icon MakeIcon()
    {
        string[] art =
        {
            "..########..",
            "..#.####.#..",
            "############",
            "############",
            "..########..",
            "..#.#..#.#..",
            "..#.#..#.#..",
        };
        using (Bitmap bmp = new Bitmap(32, 32))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (SolidBrush tile = new SolidBrush(Color.FromArgb(0x1A, 0x1A, 0x1A)))
                    g.FillRectangle(tile, 0, 3, 32, 26);
                using (SolidBrush clay = new SolidBrush(ClaudeView.Clay))
                    for (int y = 0; y < art.Length; y++)
                        for (int x = 0; x < art[y].Length; x++)
                            if (art[y][x] == '#')
                                g.FillRectangle(clay, 4 + x * 2, 9 + y * 2, 2, 2);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        menu.Dispose();
    }
}
