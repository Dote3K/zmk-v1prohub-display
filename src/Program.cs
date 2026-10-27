using System;
using System.Threading;

static class Program
{
    static int Main(string[] args)
    {
        using (Hub hub = Hub.Open())
        {
            if (hub == null)
            {
                Console.Error.WriteLine("hub not found");
                return 1;
            }

            // test pattern: red -> blue horizontal gradient
            byte[] px = new byte[Hub.FrameBytes];
            for (int y = 0; y < Hub.Height; y++)
                for (int x = 0; x < Hub.Width; x++)
                {
                    int r = 255 - x * 255 / (Hub.Width - 1), b = x * 255 / (Hub.Width - 1), g = y * 255 / (Hub.Height - 1);
                    int i = (y * Hub.Width + x) * 2;
                    px[i] = (byte)((r & 0xF8) | (g >> 5));
                    px[i + 1] = (byte)(((g & 0x1C) << 3) | (b >> 3));
                }

            Console.WriteLine(hub.SendFrame(px) ? "LIVE,ok" : "frame rejected");
            Thread.Sleep(5000);
            hub.ExitLive();
        }
        return 0;
    }
}
