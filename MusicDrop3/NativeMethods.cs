using System.Runtime.InteropServices;

namespace MFlacDrop;

internal static class NativeMethods
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
}
