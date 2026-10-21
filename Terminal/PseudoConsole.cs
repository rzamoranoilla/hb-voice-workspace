using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HB.Terminal;

/// <summary>
/// Runs a console program attached to a Windows pseudo console (ConPTY) and exposes
/// its input/output as streams of VT sequences.
/// </summary>
public sealed class PseudoConsole : IDisposable
{
    private IntPtr _handle;
    private IntPtr _attributeList;
    private readonly SafeFileHandle _inputWrite;
    private readonly SafeFileHandle _outputRead;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly SafeProcessHandle _process;
    private bool _disposed;

    /// <summary>Raised on a background thread with decoded output text.</summary>
    public event Action<string>? OutputReceived;

    /// <summary>Raised on a background thread when the hosted process ends.</summary>
    public event Action? Exited;

    public PseudoConsole(string commandLine, string workingDirectory, int cols, int rows)
    {
        if (!CreatePipe(out var inputRead, out _inputWrite, IntPtr.Zero, 0) ||
            !CreatePipe(out _outputRead, out var outputWrite, IntPtr.Zero, 0))
            throw new Win32Exception();

        var size = new COORD { X = (short)cols, Y = (short)rows };
        int hr = CreatePseudoConsole(size, inputRead, outputWrite, 0, out _handle);
        // The pseudo console duplicated its ends of the pipes.
        inputRead.Dispose();
        outputWrite.Dispose();
        if (hr != 0) throw new Win32Exception(hr);

        _process = StartProcess(commandLine, workingDirectory);
        _input = new FileStream(_inputWrite, FileAccess.Write);
        _output = new FileStream(_outputRead, FileAccess.Read);

        new Thread(ReadLoop) { IsBackground = true, Name = "ConPTY reader" }.Start();
        new Thread(WaitForExit) { IsBackground = true, Name = "ConPTY exit watcher" }.Start();
    }

    public void Write(string text)
    {
        if (_disposed) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _input.Write(bytes, 0, bytes.Length);
            _input.Flush();
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public void Resize(int cols, int rows)
    {
        if (_disposed || cols < 1 || rows < 1) return;
        ResizePseudoConsole(_handle, new COORD { X = (short)cols, Y = (short)rows });
    }

    private SafeProcessHandle StartProcess(string commandLine, string workingDirectory)
    {
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        _attributeList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(_attributeList, 1, 0, ref size) ||
            !UpdateProcThreadAttribute(_attributeList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                _handle, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception();

        var startupInfo = new STARTUPINFOEX();
        startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        startupInfo.lpAttributeList = _attributeList;

        if (!CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero,
                workingDirectory, ref startupInfo, out var info))
            throw new Win32Exception();

        CloseHandle(info.hThread);
        return new SafeProcessHandle(info.hProcess, ownsHandle: true);
    }

    private void ReadLoop()
    {
        var buffer = new byte[8192];
        var chars = new char[8192];
        // A stateful decoder keeps multi-byte characters intact across read boundaries.
        var decoder = Encoding.UTF8.GetDecoder();
        try
        {
            int read;
            while ((read = _output.Read(buffer, 0, buffer.Length)) > 0)
            {
                int count = decoder.GetChars(buffer, 0, read, chars, 0);
                if (count > 0) OutputReceived?.Invoke(new string(chars, 0, count));
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private void WaitForExit()
    {
        WaitForSingleObject(_process.DangerousGetHandle(), INFINITE);
        if (!_disposed) Exited?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Closing the pseudo console terminates the attached process and ends the read loop.
        // Do it off-thread: it can wait for pending output to be drained.
        var handle = _handle;
        _handle = IntPtr.Zero;
        Task.Run(() =>
        {
            ClosePseudoConsole(handle);
            _input.Dispose();
            _output.Dispose();
            _process.Dispose();
            if (_attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(_attributeList);
                Marshal.FreeHGlobal(_attributeList);
            }
        });
    }

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint INFINITE = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe,
        IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput,
        uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount,
        int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute,
        IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}
