using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

// One setting as the settings window shows it. The window draws every field from this description, so
// a new screen or option only describes itself here (or in its IScreen.Fields); the page needs no change.
sealed class Field
{
    public string Key, Label, Help = "";
    public string Kind;                   // toggle, choice, multi, number, text, button, info
    public List<KeyValuePair<string, string>> Choices; // value, label (choice and multi)
    public double Min, Max, Step = 1;     // number
    public string Unit = "";
    public Func<object> Get;              // bool, string, string[] or number; for info the text shown
    public Action<object> Set;            // toggle, choice, multi, number, text
    public Func<string> Run;              // button; returns a message for the window
    public Func<bool> Visible;            // null = always shown
    public string Section = "";           // fields of one section share a card under this heading

    public static Field Toggle(string key, string label, string help, Func<bool> get, Action<bool> set)
    {
        return new Field { Key = key, Label = label, Help = help, Kind = "toggle", Get = () => get(), Set = v => set(Convert.ToBoolean(v)) };
    }

    public static Field Choice(string key, string label, string help, IEnumerable<KeyValuePair<string, string>> choices, Func<string> get, Action<string> set)
    {
        return new Field { Key = key, Label = label, Help = help, Kind = "choice", Choices = choices.ToList(), Get = () => get(), Set = v => set(Convert.ToString(v)) };
    }

    public static Field Multi(string key, string label, string help, IEnumerable<KeyValuePair<string, string>> choices, Func<string[]> get, Action<string[]> set)
    {
        return new Field { Key = key, Label = label, Help = help, Kind = "multi", Choices = choices.ToList(), Get = () => get(),
            Set = v => set(v is IEnumerable && !(v is string) ? ((IEnumerable)v).OfType<string>().ToArray() : new string[0]) };
    }

    public static Field Number(string key, string label, string help, double min, double max, double step, string unit, Func<double> get, Action<double> set)
    {
        return new Field { Key = key, Label = label, Help = help, Kind = "number", Min = min, Max = max, Step = step, Unit = unit,
            Get = () => get(), Set = v => set(Math.Max(min, Math.Min(max, Convert.ToDouble(v)))) };
    }

    public static Field Text(string key, string label, string help, Func<string> get, Action<string> set)
    {
        return new Field { Key = key, Label = label, Help = help, Kind = "text", Get = () => get(), Set = v => set(Convert.ToString(v)) };
    }

    public static Field Button(string key, string label, string help, Func<string> run)
    {
        return new Field { Key = key, Label = label, Help = help, Kind = "button", Run = run };
    }

    public static Field Info(string key, string label, Func<string> text)
    {
        return new Field { Key = key, Label = label, Kind = "info", Get = () => text() };
    }
}

// A page of the settings window. Group puts it in the sidebar: "hub" (the overview), "screens" (one page
// per screen, so a new screen appears there by itself) or "app".
sealed class Page
{
    public string Id, Title, Icon, Group, Summary = "";
    public string Screen;                 // the screen this page is about, if any
    public List<Field> Fields = new List<Field>();

    public Page Add(string section, IEnumerable<Field> fields)
    {
        foreach (Field f in fields)
        {
            if (f.Section.Length == 0)
                f.Section = section;
            Fields.Add(f);
        }
        return this;
    }

    public Page Add(string section, params Field[] fields)
    {
        return Add(section, (IEnumerable<Field>)fields);
    }
}

// The settings everyone shares: the tray menu, the settings window and the screens' own options.
static class Settings
{
    static ScreenManager manager;
    static readonly object editLock = new object();

    public static void Init(ScreenManager m)
    {
        manager = m;
    }

    public static Config Current
    {
        get { return manager != null ? manager.Config : new Config(); }
    }

    // Applies an edit to a copy of the config, puts it in effect and saves it.
    public static void Edit(Action<Config> edit)
    {
        if (manager == null)
            return; // the one-shot test modes run without a manager and do not save anything
        lock (editLock)
        {
            Config c = manager.Config.Copy();
            edit(c);
            manager.Configure(c);
            try
            {
                c.Save();
            }
            catch (IOException) { }               // still applied, just not remembered
            catch (UnauthorizedAccessException) { }
        }
    }

    // A screen's own option from config.json ("options": { "<screen>": { "<key>": value } }).
    public static T Option<T>(string screen, string key, T fallback)
    {
        Dictionary<string, object> o;
        object v;
        if (Current.Options.TryGetValue(screen, out o) && o.TryGetValue(key, out v) && v is T)
            return (T)v;
        return fallback;
    }

    public static void SetOption(string screen, string key, object value)
    {
        Edit(c =>
        {
            Dictionary<string, object> o;
            if (!c.Options.TryGetValue(screen, out o))
                c.Options[screen] = o = new Dictionary<string, object>();
            o[key] = value;
        });
    }

    // Every page, built fresh so values and visibility are current: the overview, one page per screen
    // (its place on the hub, then its own settings), and the app.
    public static List<Page> Pages()
    {
        List<Page> pages = new List<Page> { HubPage() };
        foreach (IScreen s in manager.Screens)
            pages.Add(ScreenPage(s));
        pages.Add(AppPage());
        return pages;
    }

    static Page HubPage()
    {
        Config c = Current;
        string[] rotating = c.Rotate.Where(id => manager.Screens.Any(s => s.Id == id)).ToArray();
        Page p = new Page
        {
            Id = "hub", Title = "Overview", Icon = "hub", Group = "hub",
            Summary = c.Mode == "rotate" ? "Rotating " + rotating.Length + " screens every " + c.RotateSeconds + " s" : "Pinned: " + TitleOf(c.Pinned),
        };
        Field every = Field.Number("rotateSeconds", "Time per screen", "", 3, 120, 1, "s", () => Current.RotateSeconds,
            v => Edit(x => x.RotateSeconds = (int)Math.Round(v)));
        every.Visible = () => Current.Mode == "rotate";
        p.Add("Display",
            Field.Choice("mode", "When nothing needs the hub", "Pin one screen, or take turns. Pick the screens on their own pages.",
                new[] { new KeyValuePair<string, string>("pinned", "Pin one"), new KeyValuePair<string, string>("rotate", "Rotate") },
                () => Current.Mode, v => Edit(x => x.Mode = v == "rotate" ? "rotate" : "pinned")),
            every,
            Field.Toggle("customBrightness", "Custom brightness", "Off: the level set in the 佳点 app.",
                () => Current.Brightness >= 0, v => Edit(x => x.Brightness = v ? 70 : -1)),
            Bright());
        p.Add("Interruptions",
            Field.Toggle("layerOverlay", "Show the keyboard on layer change", "A layer change or a held modifier brings the keyboard screen up for 2 seconds.",
                () => Current.LayerOverlay, v => Edit(x => x.LayerOverlay = v)));
        return p;
    }

    static Field Bright()
    {
        Field f = Field.Number("brightness", "Brightness", "", 5, 100, 5, "%", () => Current.Brightness,
            v => Edit(x => x.Brightness = (int)Math.Round(v)));
        f.Visible = () => Current.Brightness >= 0;
        return f;
    }

    static string TitleOf(string id)
    {
        IScreen s = manager.Screens.FirstOrDefault(x => x.Id == id);
        return s != null ? s.Title : id;
    }

    // what a screen does on the hub, the same for every screen, then the screen's own settings
    static Page ScreenPage(IScreen s)
    {
        string id = s.Id;
        Page p = new Page { Id = id, Title = s.Title, Icon = id, Group = "screens", Screen = id, Summary = s.Summary(manager.Now) };
        List<Field> role = new List<Field>();
        Field pin = Field.Toggle("pinned", "Pinned", "Shown whenever nothing else needs the hub.", () => Current.Pinned == id, v =>
        {
            if (v)
                Edit(x => x.Pinned = id);
            else if (Current.Pinned == id && id != "keyboard")
                Edit(x => x.Pinned = "keyboard"); // something has to be pinned; the keyboard is the fallback
        });
        pin.Visible = () => Current.Mode == "pinned";
        role.Add(pin);
        Field turn = Field.Toggle("rotation", "In the rotation", "Takes its turn with the other screens.", () => Current.Rotate.Contains(id), v =>
            Edit(x =>
            {
                string[] next = manager.Screens.Select(t => t.Id).Where(t => t == id ? v : x.Rotate.Contains(t)).ToArray();
                if (next.Length > 0) // the last one stays
                    x.Rotate = next;
            }));
        turn.Visible = () => Current.Mode == "rotate";
        role.Add(turn);
        if (s.CanTakeover)
            role.Add(Field.Toggle("takeover", "Take over when busy", "Comes up on its own and keeps the panel lit while something needs you.",
                () => Current.Takeover.Contains(id),
                v => Edit(x => x.Takeover = v ? x.Takeover.Concat(new[] { id }).Distinct().ToArray() : x.Takeover.Where(t => t != id).ToArray())));
        p.Add("On the hub", role);
        p.Add("Settings", s.Fields());
        return p;
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "zmk-hub-display";

    public static bool Autostart
    {
        get
        {
            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKey))
                return run != null && run.GetValue(RunName) != null;
        }
        set
        {
            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (value)
                    run.SetValue(RunName, "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
                else
                    run.DeleteValue(RunName, false);
            }
        }
    }

    static Page AppPage()
    {
        Page p = new Page { Id = "app", Title = "App", Icon = "app", Group = "app", Summary = "Startup and files" };
        p.Add("Startup", Field.Toggle("autostart", "Start with Windows", "Runs in the tray when you sign in.", () => Autostart, v => Autostart = v));
        p.Add("Files",
            Field.Info("config", "Settings file", () => Config.FilePath),
            Field.Button("openConfig", "Open config.json", "Every setting here lives in this file; edits there apply right away.", () =>
            {
                Open(Config.FilePath);
                return "";
            }),
            Field.Info("build", "Build", () => File.GetLastWriteTime(Process.GetCurrentProcess().MainModule.FileName).ToString("yyyy-MM-dd HH:mm")));
        return p;
    }

    // a file or folder in whatever Windows opens it with; notepad for a file type nothing claims
    public static void Open(string path)
    {
        try
        {
            Process.Start(path);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Process.Start("notepad.exe", "\"" + path + "\"");
        }
    }
}
