using System;
using System.Threading;

static class Program
{
    static int Main(string[] args)
    {
        Status demo = new Status { Layer = 1, LayerName = "Nav/Num", Mods = 0x02, BatteryLeft = 87, BatteryRight = 0 };

        using (Renderer renderer = new Renderer("Cygnus"))
        {
            if (args.Length == 2 && args[0] == "--png")
            {
                renderer.Render(demo);
                renderer.SavePng(args[1]);
                return 0;
            }
        }

        using (Hub hub = Hub.Open())
        using (Renderer renderer = new Renderer("Cygnus"))
        {
            if (hub == null)
            {
                Console.Error.WriteLine("hub not found");
                return 1;
            }

            Console.WriteLine(hub.SendFrame(renderer.Render(demo)) ? "LIVE,ok" : "frame rejected");
            Thread.Sleep(8000);
            hub.ExitLive();
        }
        return 0;
    }
}
