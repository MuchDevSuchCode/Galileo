using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Galileo.Services;

/// <summary>
/// A Windows pseudo-console (ConPTY) session hosting a shell process. Bytes the shell emits are
/// raised on <see cref="Output"/>; keystrokes are sent with <see cref="Write"/>; the grid is
/// resized with <see cref="Resize"/>. Requires Windows 10 1809+ (ConPTY).
/// </summary>
public sealed class TerminalSession : IDisposable
{
    public event Action<byte[]>? Output;

    private IntPtr _hPC = IntPtr.Zero;
    private IntPtr _inputRead = IntPtr.Zero, _inputWrite = IntPtr.Zero;
    private IntPtr _outputRead = IntPtr.Zero, _outputWrite = IntPtr.Zero;
    private PROCESS_INFORMATION _proc;
    private FileStream? _writeStream, _readStream;
    private Thread? _reader;
    private volatile bool _disposed;

    public void Start(string exe, string? args, string? cwd, short cols, short rows)
    {
        if (!CreatePipe(out _inputRead, out _inputWrite, IntPtr.Zero, 0)) throw new IOException("CreatePipe (input) failed.");
        if (!CreatePipe(out _outputRead, out _outputWrite, IntPtr.Zero, 0)) throw new IOException("CreatePipe (output) failed.");

        var hr = CreatePseudoConsole(new COORD { X = cols, Y = rows }, _inputRead, _outputWrite, 0, out _hPC);
        if (hr != 0) throw new IOException($"CreatePseudoConsole failed (0x{hr:X}).");

        var startup = ConfigureStartupInfo(_hPC);
        try
        {
            var cmd = string.IsNullOrEmpty(args) ? exe : $"{exe} {args}";
            if (!CreateProcess(null, new StringBuilder(cmd), IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero,
                    string.IsNullOrEmpty(cwd) ? null : cwd, ref startup, out _proc))
                throw new IOException("CreateProcess failed: " + Marshal.GetLastWin32Error());
        }
        finally
        {
            if (startup.lpAttributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(startup.lpAttributeList);
                Marshal.FreeHGlobal(startup.lpAttributeList);
            }
        }

        // The child now owns these ends; close our copies so EOF propagates correctly.
        ClosePipe(ref _inputRead);
        ClosePipe(ref _outputWrite);

        _writeStream = new FileStream(new SafeFileHandle(_inputWrite, ownsHandle: false), FileAccess.Write);
        _readStream = new FileStream(new SafeFileHandle(_outputRead, ownsHandle: false), FileAccess.Read);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ConPTY-read" };
        _reader.Start();
    }

    private void ReadLoop()
    {
        var buf = new byte[4096];
        try
        {
            int n;
            while (!_disposed && (n = _readStream!.Read(buf, 0, buf.Length)) > 0)
            {
                var slice = new byte[n];
                Buffer.BlockCopy(buf, 0, slice, 0, n);
                Output?.Invoke(slice);
            }
        }
        catch { /* pipe closed on dispose */ }
    }

    public void Write(byte[] data)
    {
        if (_disposed || _writeStream is null) return;
        try { _writeStream.Write(data, 0, data.Length); _writeStream.Flush(); } catch { }
    }

    public void Resize(short cols, short rows)
    {
        if (_disposed || _hPC == IntPtr.Zero || cols <= 0 || rows <= 0) return;
        try { ResizePseudoConsole(_hPC, new COORD { X = cols, Y = rows }); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_hPC != IntPtr.Zero) ClosePseudoConsole(_hPC); } catch { }
        _hPC = IntPtr.Zero;
        try { _writeStream?.Dispose(); } catch { }
        try { _readStream?.Dispose(); } catch { }
        try
        {
            if (_proc.hProcess != IntPtr.Zero)
            {
                TerminateProcess(_proc.hProcess, 0);
                CloseHandle(_proc.hThread);
                CloseHandle(_proc.hProcess);
            }
        }
        catch { }
        ClosePipe(ref _inputWrite);
        ClosePipe(ref _outputRead);
    }

    private static void ClosePipe(ref IntPtr h) { if (h != IntPtr.Zero) { CloseHandle(h); h = IntPtr.Zero; } }

    private static STARTUPINFOEX ConfigureStartupInfo(IntPtr hPC)
    {
        var si = new STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size); // first call sizes the list
        si.lpAttributeList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(si.lpAttributeList, 1, 0, ref size))
            throw new IOException("InitializeProcThreadAttributeList failed.");
        if (!UpdateProcThreadAttribute(si.lpAttributeList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new IOException("UpdateProcThreadAttribute failed.");
        return si;
    }

    private const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr attrs, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint flags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr ret);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? app, StringBuilder cmd, IntPtr procAttrs, IntPtr threadAttrs,
        bool inherit, int flags, IntPtr env, string? cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr h, uint code);
}
