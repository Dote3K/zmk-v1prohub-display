using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32;

static class Program
{
    // ZMK default USB ids
    const ushort DongleVid = 0x1D50, DonglePid = 0x615E;

    static readonly object hubLock = new object();
    static Canvas canvas;
    static Renderer renderer;
    static ScreenManager manager;
    static WebUi ui;
    static readonly byte[] lastFrame = new byte[Hub.FrameBytes]; // own copy: the renderer reuses its buffer
    static bool haveFrame;

    [STAThread] // the tray menu needs a single-threaded apartment
    static int Main(string[] args)
    {
        // Claude Code integration; first, since --hook and --statusline run on every event and should be quick
        if (args.Length == 1 && args[0] == "--hook")
            return ClaudeHooks.Forward("/hook", false);
        if (args.Length == 1 && args[0] == "--statusline")
            return ClaudeHooks.Forward("/statusline", true);
        if (args.Length == 1 && args[0] == "--install-claude")
            return ClaudeHooks.Install(true);
        if (args.Length == 1 && args[0] == "--uninstall-claude")
            return ClaudeHooks.Install(false);

        canvas = new Canvas();
        renderer = new Renderer(canvas, "Cygnus");

        // Claude screen tryouts on the hub. --pet <state|pet> [seconds]: plays one. --pet-demo [seconds]: every
        // demo state in turn. A name is a demo state (e.g. "working") or any pet in the pets folder.
        if ((args.Length == 2 || args.Length == 3) && args[0] == "--pet")
            return PlayClaude(new[] { DemoView(args[1]) }, args.Length == 3 ? double.Parse(args[2]) : 10);
        if ((args.Length == 1 || args.Length == 2) && args[0] == "--pet-demo")
            return PlayClaude(ClaudeView.Demo(), args.Length == 2 ? double.Parse(args[1]) : 6);

        // shows the status screen, fades it out and switches the backlight off, then wakes it again
        if (args.Length == 1 && args[0] == "--screen-test")
        {
            manager = new ScreenManager(new KeyboardScreen(renderer), new IScreen[0], new Config(), Push, SetScreen);
            manager.KeyboardStatus(new Status { LayerName = "Base" });
            Thread.Sleep(2000);
            manager.KeyboardStatus(new Status { LayerName = "Base", Active = false });
            Thread.Sleep(6000);
            manager.KeyboardStatus(new Status { LayerName = "Base" });
            Thread.Sleep(2000);
            return 0;
        }

        if (args.Length == 1 && args[0] == "--install")
            return Autostart(true);
        if (args.Length == 1 && args[0] == "--uninstall")
            return Autostart(false);

        bool first;
        Mutex single = new Mutex(true, "zmk-hub-display", out first);
        if (!first)
        {
            // launched again while running (or --settings): open the running copy's settings window
            if (args.Length == 0 || (args.Length == 1 && args[0] == "--settings"))
                WebUi.OpenRunning();
            return 0;
        }

        int refresh = args.Length == 2 && args[0] == "--refresh" ? int.Parse(args[1]) : 0;
        Timer timer = refresh > 0 ? new Timer(_ => Push(null), null, refresh * 1000, refresh * 1000) : null;

        // every kind of screen the hub can show; they all draw on the one canvas, from the manager's thread
        ClaudeSessions sessions = new ClaudeSessions();
        KeyboardScreen keyboardScreen = new KeyboardScreen(renderer);
        IScreen[] screens =
        {
            new ClaudeScreen(new ClaudeRenderer(canvas, Sprite.DefaultDir()), sessions),
            new ClockScreen(canvas, Sprite.DefaultDir(), sessions, () => keyboardScreen.Latest),
        };
        manager = new ScreenManager(keyboardScreen, screens, Config.Load(), Push, SetScreen);
        Settings.Init(manager);
        Weather.Start(manager.Wake);
        IDisposable watch = Config.Watch(c =>
        {
            if (c.ToJson() != manager.Config.ToJson()) // our own saves come back here too
                manager.Configure(c);
        });
        ui = new WebUi(manager, NextFrame,() => manager.Lit);
        if (new LocalServer(r => ui.Handle(r) ?? Claude(r, sessions)).Start())
            ui.Publish();
        if (args.Length == 1 && args[0] == "--settings")
            ui.Open();

        // the dongle is read on its own thread; this one runs the tray icon until "Quit" is picked
        Thread keyboard = new Thread(Run);
        keyboard.IsBackground = true;
        keyboard.Start();
        using (new Tray(manager, ui.Open))
            System.Windows.Forms.Application.Run();
        GC.KeepAlive(timer);
        GC.KeepAlive(watch);
        GC.KeepAlive(single);
        return 0;
    }

    // Claude Code's hooks POST their event to /hook and the status line its JSON to /statusline. Only
    // JSON POSTs without an Origin are taken: a web page cannot send those to localhost without a CORS
    // preflight, which is refused. Hooks get an empty 200, which Claude Code reads as "no output", so
    // nothing here can approve, deny or block anything.
    static Response Claude(Request r, ClaudeSessions sessions)
    {
        if (r.Path != "/hook" && r.Path != "/statusline")
            return null;
        if (r.Method != "POST" || r.Header("Origin") != null ||
            !(r.Header("Content-Type") ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            return Response.Error("403 Forbidden");
        Dictionary<string, object> body = r.Json();
        if (body == null)
            return Response.Error("400 Bad Request");
        string reply = null;
        if (r.Path == "/hook")
            sessions.Hook(body, manager.Now);
        else
            reply = sessions.StatusLine(body);
        manager.Wake();
        return Response.Text(reply);
    }

    static long frameVersion; // counts frames handed to Push

    // A copy of the newest frame once there is one newer than seen, or after timeoutMs either way;
    // null before the first frame.
    static byte[] NextFrame(ref long seen, int timeoutMs)
    {
        lock (hubLock)
        {
            if (frameVersion == seen)
                Monitor.Wait(hubLock, timeoutMs);
            seen = frameVersion;
            return haveFrame ? (byte[])lastFrame.Clone() : null;
        }
    }

    // A demo state by its word ("working", "needs you") or pet ("detective"); otherwise the named pet
    // with its name as the state.
    static ClaudeView DemoView(string name)
    {
        foreach (ClaudeView v in ClaudeView.Demo())
            if (string.Equals(v.State, name.Replace('-', ' '), StringComparison.OrdinalIgnoreCase) || v.Pet == name)
                return v;
        return new ClaudeView { Pet = name, State = name.Replace("working-", "").Replace('-', ' '), Quota5h = 23, Quota7d = 41 };
    }

    // Plays each view for the given seconds, a frame whenever the pet's animation moves on.
    static int PlayClaude(ClaudeView[] views, double seconds)
    {
        using (ClaudeRenderer screen = new ClaudeRenderer(canvas, Sprite.DefaultDir()))
        {
            Stopwatch clock = Stopwatch.StartNew();
            foreach (ClaudeView v in views)
            {
                Sprite pet = screen.Pet(v.Pet);
                double start = clock.Elapsed.TotalMilliseconds;
                double ms;
                while ((ms = clock.Elapsed.TotalMilliseconds - start) < seconds * 1000)
                {
                    Push(screen.Render(v, ms));
                    double wait = pet != null ? pet.UntilNextFrame(clock.Elapsed.TotalMilliseconds - start) : 250;
                    Thread.Sleep((int)Math.Ceiling(Math.Max(1, wait)));
                }
            }
        }
        CloseHub(null);
        return 0;
    }

    // Per-user autostart via HKCU Run (no admin needed). Install also starts it now,
    // uninstall stops running copies.
    static int Autostart(bool install)
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        string exe = Process.GetCurrentProcess().MainModule.FileName;
        using (RegistryKey run = Registry.CurrentUser.OpenSubKey(key, true))
        {
            if (install)
            {
                run.SetValue("zmk-hub-display", "\"" + exe + "\"");
                Process.Start(exe);
            }
            else
            {
                run.DeleteValue("zmk-hub-display", false);
                foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
                    if (p.Id != Process.GetCurrentProcess().Id)
                        p.Kill();
            }
        }
        return 0;
    }

    static void Run()
    {
        // registered before the first lookup so an arrival in between is not missed
        AutoResetEvent hidArrived = new AutoResetEvent(false);
        DeviceWatch.OnHidArrival(hidArrived);

        while (true)
        {
            RawHid dongle = RawHid.Open(DongleVid, DonglePid);
            if (dongle == null)
            {
                hidArrived.WaitOne();
                Thread.Sleep(500); // let all HID interfaces of the new device show up
                continue;
            }
            try
            {
                dongle.Write(Status.ReportId);
                while (true)
                {
                    Status s = Status.Parse(dongle.Read());
                    if (s != null)
                        manager.KeyboardStatus(s);
                }
            }
            catch (IOException) { }
            catch (Win32Exception) { }
            finally
            {
                dongle.Dispose();
                manager.KeyboardGone();
            }
        }
    }

    // Whether the screen should be lit, and whether the hub's backlight is off right now. The backlight
    // state starts unknown (treated as off), so the first push turns it on even if a previous run left it off.
    static bool screenOn = true, backlightOff = true;

    // Called by the screen manager: off once the fade-out has finished, on as soon as a fade-in starts.
    static void SetScreen(bool on)
    {
        lock (hubLock)
        {
            if (screenOn == on)
                return;
            screenOn = on;
            if (!on)
                Push(null);
        }
    }

    static int hubLevel = -1;  // the level set in the 佳点 app, read before this app sends its own
    static int sentLevel = -1; // the level last sent while lit

    // Brightness 0 only sets the hub's screen-off flag and keeps the configured level, so turning the
    // screen back on sends a level again: the custom one from the settings, or the one the 佳点 app set.
    // A new custom level is sent with the next frame.
    static void SyncBacklight()
    {
        if (!screenOn)
        {
            if (!backlightOff && hub.SetBrightness(0))
            {
                backlightOff = true;
                sentLevel = -1;
            }
            return;
        }
        if (hubLevel < 0)
        {
            hubLevel = hub.Brightness();
            if (hubLevel <= 0)
                hubLevel = 255;
        }
        int custom = manager != null ? manager.Config.Brightness : -1;
        int level = custom >= 0 ? Math.Max(1, custom * 255 / 100) : hubLevel;
        if (!backlightOff && level == sentLevel)
            return;
        if (hub.SetBrightness(level))
        {
            backlightOff = false;
            sentLevel = level;
        }
    }

    // Sends a new frame, or re-sends the last one when frame is null. The hub stays open through an
    // animation and is released after a second of quiet so the 佳点 app can still open it.
    // While the screen is off no frames go out; only the backlight is switched off.
    static void Push(byte[] frame)
    {
        lock (hubLock)
        {
            if (frame != null)
            {
                Buffer.BlockCopy(frame, 0, lastFrame, 0, Hub.FrameBytes);
                haveFrame = true;
                frameVersion++;
                Monitor.PulseAll(hubLock); // the settings window's live picture
            }
            if (!haveFrame || (!screenOn && backlightOff))
                return;
            try
            {
                if (hub == null)
                    hub = Hub.Open();
                if (hub != null)
                {
                    SyncBacklight();
                    if (screenOn && !hub.SendCompressed(lastFrame))
                        CloseHub(null);
                }
            }
            catch (Win32Exception)
            {
                CloseHub(null);
            }
            releaseHub.Change(1000, Timeout.Infinite);
        }
    }

    static Hub hub;
    static readonly Timer releaseHub = new Timer(CloseHub);

    static void CloseHub(object state)
    {
        lock (hubLock)
        {
            if (hub != null)
                hub.Dispose();
            hub = null;
        }
    }
}
