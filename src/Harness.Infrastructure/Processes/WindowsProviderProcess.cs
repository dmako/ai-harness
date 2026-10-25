using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Harness.Core.Providers;
using Microsoft.Win32.SafeHandles;

namespace Harness.Infrastructure.Processes;

[SupportedOSPlatform("windows")]
internal sealed class WindowsProviderProcess : IProviderProcess
{
    private readonly Process _process;
    private readonly SafeFileHandle _job;
    private readonly AnonymousPipeServerStream _input;
    private readonly AnonymousPipeServerStream _output;
    private readonly AnonymousPipeServerStream _error;
    private int _disposed;

    private WindowsProviderProcess(
        Process process,
        SafeFileHandle job,
        AnonymousPipeServerStream input,
        AnonymousPipeServerStream output,
        AnonymousPipeServerStream error
    )
    {
        _process = process;
        _job = job;
        _input = input;
        _output = output;
        _error = error;
        Id = process.Id;
        Completion = WaitAsync();
    }

    public int Id { get; }
    public Stream Input => _input;
    public Stream Output => _output;
    public Stream Error => _error;
    public Task<int> Completion { get; }

    [SuppressMessage(
        "Blocker Code Smell",
        "S3869:SafeHandle.DangerousGetHandle should not be called",
        Justification = "STARTUPINFOEX handle arrays require raw handles; all owners remain alive through CreateProcess and are disposed only afterward."
    )]
    internal static WindowsProviderProcess Start(
        string executable,
        IReadOnlyList<string> arguments,
        string directory,
        Dictionary<string, string> environment,
        bool allowDetachedChildren = false
    )
    {
        var job = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            job.Dispose();
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        AnonymousPipeServerStream? input = null;
        AnonymousPipeServerStream? output = null;
        AnonymousPipeServerStream? error = null;
        try
        {
            // JOBOBJECT_EXTENDED_LIMIT_INFORMATION is 144 bytes on all supported 64-bit Windows RIDs.
            using var limits = new NativeMemory(144);
            Marshal.WriteInt32(limits.Pointer, 16, 0x2000 | (allowDetachedChildren ? 0x800 : 0)); // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            Check(Native.SetInformationJobObject(job, 9, limits.Pointer, 144));
            input = new(PipeDirection.Out, HandleInheritability.Inheritable);
            output = new(PipeDirection.In, HandleInheritability.Inheritable);
            error = new(PipeDirection.In, HandleInheritability.Inheritable);
            using var handles = new NativeMemory(IntPtr.Size * 3);
            Marshal.Copy(
                new[]
                {
                    input.ClientSafePipeHandle.DangerousGetHandle(),
                    output.ClientSafePipeHandle.DangerousGetHandle(),
                    error.ClientSafePipeHandle.DangerousGetHandle(),
                },
                0,
                handles.Pointer,
                3
            );
            using var jobs = new NativeMemory(IntPtr.Size);
            Marshal.WriteIntPtr(jobs.Pointer, job.DangerousGetHandle());
            nuint size = 0;
            _ = Native.InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            using var attributes = new NativeMemory(checked((int)size));
            Check(Native.InitializeProcThreadAttributeList(attributes.Pointer, 2, 0, ref size));
            try
            {
                Check(
                    Native.UpdateProcThreadAttribute(
                        attributes.Pointer,
                        0,
                        0x20002,
                        handles.Pointer,
                        (nuint)(3 * IntPtr.Size),
                        IntPtr.Zero,
                        IntPtr.Zero
                    )
                );
                // Atomic job assignment prevents a fast-exiting launcher from orphaning descendants before assignment.
                Check(
                    Native.UpdateProcThreadAttribute(
                        attributes.Pointer,
                        0,
                        0x2000D,
                        jobs.Pointer,
                        (nuint)IntPtr.Size,
                        IntPtr.Zero,
                        IntPtr.Zero
                    )
                );
                var startup = new StartupInfoEx
                {
                    Startup = new()
                    {
                        Size = Marshal.SizeOf<StartupInfoEx>(),
                        Flags = 0x100,
                        Input = input.ClientSafePipeHandle.DangerousGetHandle(),
                        Output = output.ClientSafePipeHandle.DangerousGetHandle(),
                        Error = error.ClientSafePipeHandle.DangerousGetHandle(),
                    },
                    Attributes = attributes.Pointer,
                };
                using var commandLine = new NativeMemory(
                    string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote)),
                    unicode: true
                );
                using var block = new NativeMemory(
                    string.Join(
                        '\0',
                        environment
                            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                            .Select(pair => pair.Key + "=" + pair.Value)
                    ) + "\0\0",
                    unicode: true
                );
                Check(
                    Native.CreateProcessW(
                        executable,
                        commandLine.Pointer,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        true,
                        0x08080400,
                        block.Pointer,
                        directory,
                        ref startup,
                        out var info
                    )
                );
                using var processHandle = new SafeProcessHandle(info.Process, true);
                using var threadHandle = new SafeWaitHandle(info.Thread, true);
                var process = Process.GetProcessById(info.Id);
                try
                {
                    _ = process.SafeHandle; // Capture a handle before releasing CreateProcess's handle.
                    input.DisposeLocalCopyOfClientHandle();
                    output.DisposeLocalCopyOfClientHandle();
                    error.DisposeLocalCopyOfClientHandle();
                    return new(process, job, input, output, error);
                }
                catch
                {
                    process.Dispose();
                    throw;
                }
            }
            finally
            {
                Native.DeleteProcThreadAttributeList(attributes.Pointer);
            }
        }
        catch
        {
            job.Dispose();
            input?.Dispose();
            output?.Dispose();
            error?.Dispose();
            throw;
        }
    }

    public void KillTree() => _job.Dispose();

    [SuppressMessage(
        "Minor Code Smell",
        "S6966:Awaitable method should be used",
        Justification = "Close redirected pipes synchronously to unblock outstanding I/O before awaiting process shutdown."
    )]
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) is not 0)
        {
            return;
        }

        try
        {
            _input.Dispose();
            await Completion.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        { /* The job closes below, even if the server ignores stdin EOF. */
        }
        finally
        {
            KillTree();
            try
            {
                await Completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            finally
            {
                _output.Dispose();
                _error.Dispose();
                _process.Dispose();
            }
        }
    }

    private async Task<int> WaitAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        return _process.ExitCode;
    }

    private static void Check(bool success)
    {
        if (!success)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    internal static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character is '\\')
            {
                slashes++;
                continue;
            }
            result.Append('\\', character is '"' ? (slashes * 2) + 1 : slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved,
            Desktop,
            Title;
        public int X,
            Y,
            XSize,
            YSize,
            XChars,
            YChars,
            Fill,
            Flags;
        public short Show,
            ReservedSize;
        public IntPtr ReservedData,
            Input,
            Output,
            Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx
    {
        public StartupInfo Startup;
        public IntPtr Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInfo
    {
        public IntPtr Process,
            Thread;
        public int Id,
            ThreadId;
    }

    [SuppressMessage(
        "Interoperability",
        "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute to generate P/Invoke marshalling code at compile time",
        Justification = "The repository disallows unsafe code; these bounded Windows ABI declarations use runtime marshalling."
    )]
    internal static class Native
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        internal static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            SafeFileHandle job,
            int informationClass,
            IntPtr information,
            int length
        );

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(
            IntPtr list,
            int count,
            int flags,
            ref nuint size
        );

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(
            IntPtr list,
            int flags,
            nuint attribute,
            IntPtr value,
            nuint size,
            IntPtr previous,
            IntPtr returnedSize
        );

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll")]
        internal static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetHandleInformation(SafeFileHandle handle, int mask, int flags);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessW(
            string application,
            IntPtr commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inherit,
            int flags,
            IntPtr environment,
            string directory,
            ref StartupInfoEx startup,
            out ProcessInfo info
        );
    }
}
