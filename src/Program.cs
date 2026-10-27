using System;
using System.ComponentModel;
using System.IO;
using System.Threading;

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

        int refresh = args.Length == 2 && args[0] == "--refresh" ? int.Parse(args[1]) : 0;
        Timer timer = refresh > 0 ? new Timer(_ => Push(null), null, refresh * 1000, refresh * 1000) : null;

        Run();
        GC.KeepAlive(timer);
        return 0;
    }

    static void Run()
    {
        while (true)
        {
            RawHid dongle = RawHid.Open(DongleVid, DonglePid);
            if (dongle == null)
            {
                Thread.Sleep(3000);
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
            Thread.Sleep(1000);
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
