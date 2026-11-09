using System;
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
    static Animator animator;
    static Display display;
    static Status lastStatus; // the dongle's latest report, null until it has sent one
    static readonly byte[] lastFrame = new byte[Hub.FrameBytes]; // own copy: the renderer reuses its buffer
    static bool haveFrame;

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

        if (args.Length == 2 && args[0] == "--png")
        {
            renderer.Render(new Status { Layer = 1, LayerName = "Nav/Num", Mods = 0x0F, BatteryLeft = 87 });
            canvas.SavePng(args[1]);
            return 0;
        }

        // Claude screen tryouts. --pet-png <state|pet> <out.png> [ms]: one frame to a file, exit code 2 if
        // the pet is missing. --pet <state|pet> [seconds]: plays it on the hub. --pet-demo [seconds]: every
        // demo state in turn. A name is a demo state (e.g. "working") or any pet in the pets folder.
        if ((args.Length == 3 || args.Length == 4) && args[0] == "--pet-png")
        {
            using (ClaudeScreen screen = new ClaudeScreen(canvas, Sprite.DefaultDir()))
            {
                ClaudeView v = DemoView(args[1]);
                screen.Render(v, args.Length == 4 ? double.Parse(args[3]) : 0);
                canvas.SavePng(args[2]);
                return screen.Pet(v.Pet) != null ? 0 : 2;
            }
        }
        if ((args.Length == 2 || args.Length == 3) && args[0] == "--pet")
            return PlayClaude(new[] { DemoView(args[1]) }, args.Length == 3 ? double.Parse(args[2]) : 10);
        if ((args.Length == 1 || args.Length == 2) && args[0] == "--pet-demo")
            return PlayClaude(ClaudeView.Demo(), args.Length == 2 ? double.Parse(args[1]) : 6);

        // shows the status screen, fades it out and switches the backlight off, then wakes it again
        if (args.Length == 1 && args[0] == "--screen-test")
        {
            animator = new Animator(renderer, Push, SetScreen);
            animator.Show(new Status { LayerName = "Base" });
            Thread.Sleep(2000);
            animator.Show(new Status { LayerName = "Base", Active = false });
            Thread.Sleep(6000);
            animator.Show(new Status { LayerName = "Base" });
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
            return 0;

        int refresh = args.Length == 2 && args[0] == "--refresh" ? int.Parse(args[1]) : 0;
        Timer timer = refresh > 0 ? new Timer(_ => Push(null), null, refresh * 1000, refresh * 1000) : null;

        // Claude screen: its own canvas, since it renders on its own thread next to the animator
        ClaudeSessions sessions = new ClaudeSessions();
        display = new Display(new ClaudeScreen(new Canvas(), Sprite.DefaultDir()), sessions, Push, SetClaudeScreen,
            () => animator.Show(lastStatus ?? new Status { Active = false }));
        animator = new Animator(renderer, display.KeyboardFrame, SetScreen);
        new ClaudeServer((path, body) =>
        {
            string reply = null;
            if (path == "/hook")
                sessions.Hook(body, display.Now);
            else
                reply = sessions.StatusLine(body);
            display.ClaudeChanged();
            return reply;
        }).Start();

        Run();
        GC.KeepAlive(timer);
        GC.KeepAlive(single);
        return 0;
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
        using (ClaudeScreen screen = new ClaudeScreen(canvas, Sprite.DefaultDir()))
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
                    {
                        display.KeyboardStatus(s);
                        lastStatus = s;
                        animator.Show(s);
                    }
                }
            }
            catch (IOException) { }
            catch (Win32Exception) { }
            finally
            {
                dongle.Dispose();
            }
        }
    }

    // Whether the screen should be lit, and whether the hub's backlight is off right now. The backlight
    // state starts unknown (treated as off), so the first push turns it on even if a previous run left it off.
    static bool screenOn = true, backlightOff = true;

    // The keyboard and Claude each say whether they want the panel lit; it is lit while either does.
    static bool keyboardWantsOn = true, claudeWantsOn;

    // Called by the animator: off once the idle fade-out has finished, on as soon as a fade-in starts.
    static void SetScreen(bool on)
    {
        lock (hubLock)
        {
            keyboardWantsOn = on;
            ApplyScreen();
        }
    }

    // Called by the display: on while the Claude screen is up.
    static void SetClaudeScreen(bool on)
    {
        lock (hubLock)
        {
            claudeWantsOn = on;
            ApplyScreen();
        }
    }

    static void ApplyScreen()
    {
        bool on = keyboardWantsOn || claudeWantsOn;
        if (screenOn == on)
            return;
        screenOn = on;
        if (!on)
            Push(null);
    }

    // Brightness 0 only sets the hub's screen-off flag and keeps the configured level, so turning the
    // screen back on reads that level (set in the 佳点 app) and sends it again.
    static void SyncBacklight()
    {
        if (backlightOff == !screenOn)
            return;
        int level = screenOn ? hub.Brightness() : 0;
        if (screenOn && level <= 0)
            level = 255;
        if (hub.SetBrightness(level))
            backlightOff = !screenOn;
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
