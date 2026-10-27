using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Raw HID interface of the ZMK dongle (zmk-raw-hid: usage page 0xFF60, usage 0x61, 32-byte reports).
sealed class RawHid : IDisposable
{
    const ushort UsagePage = 0xFF60, Usage = 0x61;

    readonly SafeFileHandle file;
    readonly FileStream stream;
    readonly int inLen, outLen;

    RawHid(SafeFileHandle file, int inLen, int outLen)
    {
        this.file = file;
        this.inLen = inLen;
        this.outLen = outLen;
        stream = new FileStream(file, FileAccess.ReadWrite, inLen, false);
    }

    // Returns null when no matching device is plugged in.
    public static RawHid Open(ushort vid, ushort pid)
    {
        Guid guid;
        HidD_GetHidGuid(out guid);
        IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1))
            return null;
        IntPtr detail = Marshal.AllocHGlobal(1024);
        try
        {
            SP_DEVICE_INTERFACE_DATA data = new SP_DEVICE_INTERFACE_DATA();
            data.cbSize = Marshal.SizeOf(data);
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, 1024, IntPtr.Zero, IntPtr.Zero))
                    continue;
                RawHid hid = TryOpen(Marshal.PtrToStringUni(detail + 4), vid, pid);
                if (hid != null)
                    return hid;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
            SetupDiDestroyDeviceInfoList(set);
        }
        return null;
    }

    static RawHid TryOpen(string path, ushort vid, ushort pid)
    {
        SafeFileHandle h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
            return null;

        HIDD_ATTRIBUTES attr = new HIDD_ATTRIBUTES();
        attr.Size = Marshal.SizeOf(attr);
        IntPtr pre;
        HIDP_CAPS caps;
        if (HidD_GetAttributes(h, ref attr) && attr.VendorID == vid && attr.ProductID == pid
            && HidD_GetPreparsedData(h, out pre))
        {
            bool ok = HidP_GetCaps(pre, out caps) == HIDP_STATUS_SUCCESS;
            HidD_FreePreparsedData(pre);
            if (ok && caps.UsagePage == UsagePage && caps.Usage == Usage)
                return new RawHid(h, caps.InputReportByteLength, caps.OutputReportByteLength);
        }
        h.Dispose();
        return null;
    }

    // Blocks until the next input report arrives. Returns the payload without the report id byte.
    public byte[] Read()
    {
        byte[] buf = new byte[inLen];
        int n = stream.Read(buf, 0, inLen);
        if (n <= 1)
            throw new IOException("raw hid closed");
        byte[] payload = new byte[n - 1];
        Buffer.BlockCopy(buf, 1, payload, 0, n - 1);
        return payload;
    }

    public void Write(params byte[] payload)
    {
        byte[] buf = new byte[outLen];
        Buffer.BlockCopy(payload, 0, buf, 1, Math.Min(payload.Length, outLen - 1));
        if (!HidD_SetOutputReport(file, buf, buf.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "raw hid write");
    }

    public void Dispose()
    {
        stream.Dispose();
    }

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    const uint DIGCF_PRESENT = 2, DIGCF_DEVICEINTERFACE = 0x10;
    const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")]
    static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attr);
    [DllImport("hid.dll")]
    static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")]
    static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")]
    static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buf, int len);
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
}
