using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhotosPlus.Services;

/// <summary>Thin wrappers over Win32 shell operations used by the image context menu.</summary>
public static class ShellOps
{
    // ---- Set as desktop background ----

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

    public static bool SetWallpaper(string path)
    {
        const int SPI_SETDESKWALLPAPER = 0x0014;
        const int SPIF_UPDATEINIFILE = 0x01;
        const int SPIF_SENDCHANGE = 0x02;
        return SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE) != 0;
    }

    // ---- Windows file Properties dialog ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    public static void ShowProperties(IntPtr hwnd, string path)
    {
        const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
        const int SW_SHOW = 5;
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_INVOKEIDLIST,
            hwnd = hwnd,
            lpVerb = "properties",
            lpFile = path,
            nShow = SW_SHOW
        };
        ShellExecuteEx(ref info);
    }

    // ---- Shell verbs via the default handler ----

    /// <summary>Invokes a shell verb ("openas" = Open with…, "print", "edit"…) on a file.</summary>
    public static void InvokeVerb(string path, string verb)
    {
        Process.Start(new ProcessStartInfo { FileName = path, Verb = verb, UseShellExecute = true });
    }
}
