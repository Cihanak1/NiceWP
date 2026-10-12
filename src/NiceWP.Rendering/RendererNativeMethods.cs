using System.Runtime.InteropServices;

namespace NiceWP.Rendering;

internal static class RendererNativeMethods
{
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint hWnd);
}
