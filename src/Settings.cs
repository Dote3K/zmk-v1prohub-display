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

// A section of the settings window: one for the hub as a whole, one per screen that has settings, one
// for the app.
sealed class Page
{
    public string Id, Title, Icon;
    public List<Field> Fields = new List<Field>();
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

    // Every page, built fresh so values and visibility are current.
    public static List<Page> Pages()
    {
        List<Page> pages = new List<Page> { ScreensPage() };
        foreach (IScreen s in manager.Screens)
        {
            IList<Field> fields = s.Fields();
            if (fields.Count > 0)
                pages.Add(new Page { Id = s.Id, Title = s.Title, Icon = s.Id, Fields = fields.ToList() });
        }
        pages.Add(GeneralPage());
        return pages;
    }

    static Page ScreensPage()
    {
        Func<IEnumerable<IScreen>, List<KeyValuePair<string, string>>> choices =
            list => list.Select(s => new KeyValuePair<string, string>(s.Id, s.Title)).ToList();
        IList<IScreen> all = manager.Screens;
        Page p = new Page { Id = "screens", Title = "Screens", Icon = "screens" };
        p.Fields.Add(Field.Choice("mode", "Mode", "Pin one screen, or rotate through several.",
            new[] { new KeyValuePair<string, string>("pinned", "Pin one screen"), new KeyValuePair<string, string>("rotate", "Rotate") },
            () => Current.Mode, v => Edit(c => c.Mode = v == "rotate" ? "rotate" : "pinned")));
        Field pinned = Field.Choice("pinned", "Pinned screen", "", choices(all), () => Current.Pinned, v => Edit(c => c.Pinned = v));
        pinned.Visible = () => Current.Mode == "pinned";
        p.Fields.Add(pinned);
        Field rotate = Field.Multi("rotate", "Rotate through", "In the order listed.", choices(all), () => Current.Rotate,
            v => { if (v.Length > 0) Edit(c => c.Rotate = all.Select(s => s.Id).Where(v.Contains).ToArray()); });
        rotate.Visible = () => Current.Mode == "rotate";
        p.Fields.Add(rotate);
        Field every = Field.Number("rotateSeconds", "Time per screen", "", 3, 120, 1, "s", () => Current.RotateSeconds,
            v => Edit(c => c.RotateSeconds = (int)Math.Round(v)));
        every.Visible = () => Current.Mode == "rotate";
        p.Fields.Add(every);
        p.Fields.Add(Field.Multi("takeover", "Take over the hub when busy", "These screens come up on their own and keep the panel lit while something needs you, like Claude working or waiting.",
            choices(all.Where(s => s.CanTakeover)), () => Current.Takeover, v => Edit(c => c.Takeover = v)));
        p.Fields.Add(Field.Toggle("layerOverlay", "Show the keyboard on layer change", "A layer change or a held modifier shows the keyboard screen for 2 seconds.",
            () => Current.LayerOverlay, v => Edit(c => c.LayerOverlay = v)));
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

    static Page GeneralPage()
    {
        Page p = new Page { Id = "general", Title = "General", Icon = "general" };
        p.Fields.Add(Field.Toggle("autostart", "Start with Windows", "", () => Autostart, v => Autostart = v));
        p.Fields.Add(Field.Info("config", "Settings file", () => Config.FilePath));
        p.Fields.Add(Field.Button("openConfig", "Open config.json", "", () =>
        {
            Open(Config.FilePath);
            return "";
        }));
        p.Fields.Add(Field.Info("build", "Build", () => File.GetLastWriteTime(Process.GetCurrentProcess().MainModule.FileName).ToString("yyyy-MM-dd HH:mm")));
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
