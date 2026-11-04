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
    static Renderer renderer;
    static Animator animator;
    static readonly byte[] lastFrame = new byte[Hub.FrameBytes]; // own copy: the renderer reuses its buffer
    static bool haveFrame;

    static int Main(string[] args)
    {
        renderer = new Renderer("Cygnus");

        if (args.Length == 2 && args[0] == "--png")
        {
            renderer.Render(new Status { Layer = 1, LayerName = "Nav/Num", Mods = 0x0F, BatteryLeft = 87 });
            renderer.SavePng(args[1]);
            return 0;
        }

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

        animator = new Animator(renderer, Push, SetScreen);
        Run();
        GC.KeepAlive(timer);
        GC.KeepAlive(single);
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
                        animator.Show(s);
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

    // Called by the animator: off once the idle fade-out has finished, on as soon as a fade-in starts.
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
