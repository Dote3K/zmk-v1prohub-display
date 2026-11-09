using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

// Which screens the hub shows and when, kept in %APPDATA%\zmk-hub-display\config.json. Screens are
// named by IScreen.Id ("keyboard", "claude"). The file can be edited by hand; the app picks changes up.
sealed class Config
{
    public string Mode = "pinned";                       // "pinned": one screen; "rotate": cycle through Rotate
    public string Pinned = "keyboard";
    public string[] Rotate = { "keyboard", "claude" };
    public int RotateSeconds = 15;
    public string[] Takeover = { "claude" };             // may take the hub over while they have something urgent
    public bool LayerOverlay = true;                     // a layer or modifier change shows the keyboard for a moment

    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zmk-hub-display");
    public static readonly string FilePath = Path.Combine(Dir, "config.json");

    public Config Copy()
    {
        Config c = (Config)MemberwiseClone();
        c.Rotate = (string[])Rotate.Clone();
        c.Takeover = (string[])Takeover.Clone();
        return c;
    }

    public string ToJson()
    {
        return Json.Write(new Dictionary<string, object>
        {
            { "mode", Mode },
            { "pinned", Pinned },
            { "rotate", Rotate },
            { "rotateSeconds", RotateSeconds },
            { "takeover", Takeover },
            { "layerOverlay", LayerOverlay },
        }) + "\n";
    }

    // The saved config; a missing file is created with the defaults so there is something to edit.
    // Anything unreadable or out of range falls back to its default.
    public static Config Load()
    {
        Config c = new Config();
        try
        {
            if (!File.Exists(FilePath))
            {
                c.Save();
                return c;
            }
            Dictionary<string, object> d = Json.Parse(File.ReadAllText(FilePath, Encoding.UTF8));
            if (d == null)
                return c;
            object o;
            if (d.TryGetValue("mode", out o) && (o as string == "pinned" || o as string == "rotate"))
                c.Mode = (string)o;
            if (d.TryGetValue("pinned", out o) && o is string)
                c.Pinned = (string)o;
            if (d.TryGetValue("rotate", out o) && o is IList)
                c.Rotate = ((IList)o).OfType<string>().ToArray();
            if (d.TryGetValue("rotateSeconds", out o) && o is int)
                c.RotateSeconds = Math.Max(3, Math.Min(3600, (int)o));
            if (d.TryGetValue("takeover", out o) && o is IList)
                c.Takeover = ((IList)o).OfType<string>().ToArray();
            if (d.TryGetValue("layerOverlay", out o) && o is bool)
                c.LayerOverlay = (bool)o;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return c;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, ToJson(), new UTF8Encoding(false));
        if (File.Exists(FilePath))
            File.Replace(tmp, FilePath, null);
        else
            File.Move(tmp, FilePath);
    }

    // Calls changed with the new config whenever the file is edited (by hand or by the tray menu).
    // Editors save in bursts, so it waits for a quiet moment before reading.
    public static IDisposable Watch(Action<Config> changed)
    {
        Directory.CreateDirectory(Dir);
        FileSystemWatcher w = new FileSystemWatcher(Dir, "config.json");
        Timer settle = new Timer(_ => changed(Load()));
        FileSystemEventHandler poke = (s, e) => settle.Change(300, Timeout.Infinite);
        w.Changed += poke;
        w.Created += poke;
        w.Renamed += (s, e) => settle.Change(300, Timeout.Infinite);
        w.EnableRaisingEvents = true;
        return w;
    }
}
