using System.Runtime.InteropServices;

namespace CpuX;

internal static class Program
{
    [DllImport("ole32.dll")]
    private static extern int CoInitializeSecurity(IntPtr descriptor, int count, IntPtr services, IntPtr reserved1, uint authentication, uint impersonation, IntPtr authList, uint capabilities, IntPtr reserved3);

    [STAThread]
    public static void Main()
    {
        // Establish process-wide impersonation before WPF initializes any COM components.
        // Required by local WMI providers, even when a service proxy blanket is set.
        var hr = CoInitializeSecurity(IntPtr.Zero, -1, IntPtr.Zero, IntPtr.Zero, 0, 3, IntPtr.Zero, 0, IntPtr.Zero);
        if (hr < 0 && (uint)hr != 0x80010119) Marshal.ThrowExceptionForHR(hr); // RPC_E_TOO_LATE
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
