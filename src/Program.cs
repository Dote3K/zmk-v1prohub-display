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
    static byte[] lastFrame;

    static int Main(string[] args)
    {
        renderer = new Renderer("Cygnus");

        if (args.Length == 2 && args[0] == "--png")
        {
            renderer.Render(new Status { Layer = 1, LayerName = "Nav/Num", Mods = 0x02, BatteryLeft = 87 });
            renderer.SavePng(args[1]);
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
                        Push(renderer.Render(s));
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

    // Sends a new frame, or re-sends the last one when frame is null. The hub is only held while
    // sending so the 佳点 app can still open it.
    static void Push(byte[] frame)
    {
        lock (hubLock)
        {
            if (frame != null)
                lastFrame = frame;
            if (lastFrame == null)
                return;
            try
            {
                using (Hub hub = Hub.Open())
                    if (hub != null)
                        hub.SendFrame(lastFrame);
            }
            catch (Win32Exception) { }
        }
    }
}
