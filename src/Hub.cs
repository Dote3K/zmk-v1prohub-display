using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// 佳点 V1P hub screen over WinUSB (interface 0, bulk EP 0x01 out / 0x81 in).
// LIVE frame: A5 5A 0B <u32 LE length> <flags 0 = raw> <RGB565 big-endian, row-major> -> "LIVE,ok"
sealed class Hub : IDisposable
{
    public const int Width = 320;
    public const int Height = 170;
    public const int FrameBytes = Width * Height * 2;

    static readonly Guid InterfaceGuid = new Guid("9D32F82C-1FB2-4486-8501-B6145B5BA336");
    const string DeviceMatch = "vid_0483&pid_66aa";
    const byte EpOut = 0x01;
    const byte EpIn = 0x81;

    readonly SafeFileHandle file;
    readonly IntPtr usb;
    readonly byte[] frame = new byte[8 + FrameBytes];

    Hub(SafeFileHandle file, IntPtr usb)
    {
        this.file = file;
        this.usb = usb;
        frame[0] = 0xA5;
        frame[1] = 0x5A;
        frame[2] = 0x0B;
        BitConverter.GetBytes(FrameBytes).CopyTo(frame, 3);
        SetTimeout(EpOut, 5000);

        // drop stale replies once; doing it per command costs a full read timeout every frame
        SetTimeout(EpIn, 20);
        for (int i = 0; i < 8 && Read() != null; i++) { }
        SetTimeout(EpIn, 400);
    }

    // Returns null when the hub is not plugged in.
    public static Hub Open()
    {
        string path = FindPath();
        if (path == null)
            return null;

        SafeFileHandle file = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (file.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "open hub (is the 佳点 app running?)");

        IntPtr usb;
        if (!WinUsb_Initialize(file, out usb))
        {
            int err = Marshal.GetLastWin32Error();
            file.Dispose();
            throw new Win32Exception(err, "WinUsb_Initialize");
        }
        return new Hub(file, usb);
    }

    public bool SendFrame(byte[] rgb565)
    {
        Buffer.BlockCopy(rgb565, 0, frame, 8, FrameBytes);
        return Command(frame, "LIVE,", 4000) == "LIVE,ok";
    }

    // LZ4 frame: mostly-black screens shrink a lot, so transfers are much shorter than raw ones.
    public bool SendCompressed(byte[] rgb565)
    {
        return Command(Lz4.Frame(rgb565), "LIVE,", 4000) == "LIVE,ok";
    }

    public void ExitLive()
    {
        Command(new byte[] { 0xA5, 0x5A, 0x0B, 0, 0, 0, 0, 0 }, "LIVE,", 2500);
    }

    string Command(byte[] data, string prefix, int timeoutMs)
    {
        uint done;
        if (!WinUsb_WritePipe(usb, EpOut, data, (uint)data.Length, out done, IntPtr.Zero) || done != data.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "write to hub");

        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            string reply = Read();
            if (reply != null && reply.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return reply;
        }
        return null;
    }

    string Read()
    {
        byte[] buf = new byte[64];
        uint got;
        if (!WinUsb_ReadPipe(usb, EpIn, buf, (uint)buf.Length, out got, IntPtr.Zero) || got == 0)
            return null;
        return Encoding.ASCII.GetString(buf, 0, (int)got).TrimEnd('\0').Trim();
    }

    void SetTimeout(byte pipe, uint ms)
    {
        WinUsb_SetPipePolicy(usb, pipe, PIPE_TRANSFER_TIMEOUT, 4, ref ms);
    }

    public void Dispose()
    {
        WinUsb_Free(usb);
        file.Dispose();
    }

    static string FindPath()
    {
        Guid guid = InterfaceGuid;
        IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1))
            return null;
        try
        {
            SP_DEVICE_INTERFACE_DATA data = new SP_DEVICE_INTERFACE_DATA();
            data.cbSize = Marshal.SizeOf(data);
            IntPtr detail = Marshal.AllocHGlobal(1024);
            try
            {
                for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, 1024, IntPtr.Zero, IntPtr.Zero))
                        continue;
                    string path = Marshal.PtrToStringUni(detail + 4);
                    if (path.IndexOf(DeviceMatch, StringComparison.OrdinalIgnoreCase) >= 0)
                        return path;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(detail);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return null;
    }

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    const uint DIGCF_PRESENT = 2, DIGCF_DEVICEINTERFACE = 0x10;
    const uint PIPE_TRANSFER_TIMEOUT = 3;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, IntPtr required, IntPtr info);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("winusb.dll", SetLastError = true)]
    static extern bool WinUsb_Initialize(SafeFileHandle file, out IntPtr usb);
    [DllImport("winusb.dll")]
    static extern bool WinUsb_Free(IntPtr usb);
    [DllImport("winusb.dll", SetLastError = true)]
    static extern bool WinUsb_WritePipe(IntPtr usb, byte pipe, byte[] buf, uint len, out uint done, IntPtr overlapped);
    [DllImport("winusb.dll", SetLastError = true)]
    static extern bool WinUsb_ReadPipe(IntPtr usb, byte pipe, byte[] buf, uint len, out uint done, IntPtr overlapped);
    [DllImport("winusb.dll", SetLastError = true)]
    static extern bool WinUsb_SetPipePolicy(IntPtr usb, byte pipe, uint policy, uint len, ref uint value);
}
