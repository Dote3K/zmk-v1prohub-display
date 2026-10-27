using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

// Signals an event whenever a HID interface appears, so the app can sleep until something is plugged
// in instead of polling. Uses CM_Register_Notification, which needs no window.
static class DeviceWatch
{
    static readonly Guid HidInterface = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");

    static CmNotifyCallback callback; // kept alive for the lifetime of the process
    static IntPtr handle;

    public static void OnHidArrival(EventWaitHandle signal)
    {
        // CM_NOTIFY_FILTER: cbSize, Flags, FilterType, Reserved, then a 400-byte union
        const int size = 16 + 400;
        IntPtr filter = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i += 4)
                Marshal.WriteInt32(filter, i, 0);
            Marshal.WriteInt32(filter, 0, size);
            Marshal.StructureToPtr(HidInterface, filter + 16, false);

            callback = (notify, context, action, data, dataSize) =>
            {
                if (action == CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL)
                    signal.Set();
                return 0;
            };
            int cr = CM_Register_Notification(filter, IntPtr.Zero, callback, out handle);
            if (cr != 0)
                throw new Win32Exception("CM_Register_Notification failed: " + cr);
        }
        finally
        {
            Marshal.FreeHGlobal(filter);
        }
    }

    const int CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL = 0;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate int CmNotifyCallback(IntPtr notify, IntPtr context, int action, IntPtr data, int dataSize);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Register_Notification(IntPtr filter, IntPtr context, CmNotifyCallback callback, out IntPtr notify);
}
