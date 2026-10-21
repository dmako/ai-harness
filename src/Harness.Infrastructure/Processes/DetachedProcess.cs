using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Harness.Core.Graphify;
using Harness.Core.Providers;
using Microsoft.Win32.SafeHandles;

namespace Harness.Infrastructure.Processes;

/// <summary>Explicitly detached watches use files, not parent-owned pipes or kill-on-close jobs.</summary>
internal sealed class DetachedProcess(int id, Task<int> completion) : IGraphifyWatch
{
    public int Id { get; } = id;
    public Task<int> Completion { get; } = completion;

    public static IGraphifyWatch Start(ProviderProcessOptions options, string log)
    {
        var (executable, directory, environment) = ProviderProcessFactory.Prepare(options);
        using var input = File.OpenHandle(OperatingSystem.IsWindows() ? "NUL" : "/dev/null", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = File.OpenHandle(OperatingSystem.IsWindows() ? "NUL" : "/dev/null", FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        using var error = File.OpenHandle(log, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return OperatingSystem.IsWindows()
            ? Windows(executable, options.Arguments, directory, environment, input, output, error)
            : Unix(executable, options.Arguments, directory, environment, input, output, error);
    }

    public void Kill()
    {
        if (Completion.IsCompleted)
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        { _ = UnixProviderProcess.Native.kill(-Id, 9); return; }
        try
        { using var process = Process.GetProcessById(Id); process.Kill(entireProcessTree: true); }
        catch (ArgumentException) { /* Already exited. */ }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025:Do not pass disposable objects into unawaited tasks", Justification = "The successful launch transfers Process ownership to WaitAsync, which disposes it after the detached watch exits.")]
    [SupportedOSPlatform("windows")]
    [SuppressMessage("Blocker Code Smell", "S3869:SafeHandle.DangerousGetHandle should not be called", Justification = "Borrowed handles are held alive through CreateProcess by the synchronous caller's using scopes.")]
    private static DetachedProcess Windows(string executable, IReadOnlyList<string> arguments, string directory, Dictionary<string, string> environment, SafeFileHandle input, SafeFileHandle output, SafeFileHandle error)
    {
        foreach (var handle in new[] { input, output, error })
        {
            Check(WindowsProviderProcess.Native.SetHandleInformation(handle, 1, 1));
        }

        using var handles = new NativeMemory(IntPtr.Size * 3);
        Marshal.Copy(new[] { input.DangerousGetHandle(), output.DangerousGetHandle(), error.DangerousGetHandle() }, 0, handles.Pointer, 3);
        nuint size = 0;
        _ = WindowsProviderProcess.Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        using var attributes = new NativeMemory(checked((int)size));
        Check(WindowsProviderProcess.Native.InitializeProcThreadAttributeList(attributes.Pointer, 1, 0, ref size));
        try
        {
            Check(WindowsProviderProcess.Native.UpdateProcThreadAttribute(attributes.Pointer, 0, 0x20002, handles.Pointer, (nuint)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero));
            var startup = new WindowsProviderProcess.StartupInfoEx
            {
                Startup = new() { Size = Marshal.SizeOf<WindowsProviderProcess.StartupInfoEx>(), Flags = 0x100, Input = input.DangerousGetHandle(), Output = output.DangerousGetHandle(), Error = error.DangerousGetHandle() },
                Attributes = attributes.Pointer,
            };
            using var commandLine = new NativeMemory(string.Join(' ', new[] { executable }.Concat(arguments).Select(WindowsProviderProcess.Quote)), unicode: true);
            using var block = new NativeMemory(string.Join('\0', environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + pair.Value)) + "\0\0", unicode: true);
            // BREAKAWAY_FROM_JOB + EXTENDED_STARTUPINFO + UNICODE_ENVIRONMENT + NEW_PROCESS_GROUP + DETACHED_PROCESS.
            if (!WindowsProviderProcess.Native.CreateProcessW(executable, commandLine.Pointer, IntPtr.Zero, IntPtr.Zero, true, 0x01080608, block.Pointer, directory, ref startup, out var info))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot detach graphify watch; a containing Windows job may forbid process breakaway.");
            }

            using var processHandle = new SafeProcessHandle(info.Process, true);
            using var threadHandle = new SafeWaitHandle(info.Thread, true);
            var process = Process.GetProcessById(info.Id);
            try
            {
                _ = process.SafeHandle;
                return new(info.Id, WaitAsync(process));
            }
            catch { process.Dispose(); throw; }
        }
        finally { WindowsProviderProcess.Native.DeleteProcThreadAttributeList(attributes.Pointer); }
    }

    private static async Task<int> WaitAsync(Process process)
    {
        using (process)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
    }

    [UnsupportedOSPlatform("windows")]
    [SuppressMessage("Blocker Code Smell", "S3869:SafeHandle.DangerousGetHandle should not be called", Justification = "posix_spawn dup2 actions borrow descriptors held alive by the synchronous caller.")]
    private static DetachedProcess Unix(string executable, IReadOnlyList<string> arguments, string directory, Dictionary<string, string> environment, SafeFileHandle input, SafeFileHandle output, SafeFileHandle error)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Detached watches support Windows, Linux and macOS.");
        }

        using var attributes = new NativeMemory(1024);
        using var actions = new NativeMemory(1024);
        Check(UnixProviderProcess.Native.posix_spawnattr_init(attributes.Pointer));
        try
        {
            Check(UnixProviderProcess.Native.posix_spawn_file_actions_init(actions.Pointer));
            try
            {
                Check(UnixProviderProcess.Native.posix_spawnattr_setpgroup(attributes.Pointer, 0));
                Check(UnixProviderProcess.Native.posix_spawnattr_setflags(attributes.Pointer, 2));
                using var cwd = new NativeMemory(directory);
                Check(UnixProviderProcess.Native.posix_spawn_file_actions_addchdir_np(actions.Pointer, cwd.Pointer));
                foreach (var (handle, target) in new[] { (input, 0), (output, 1), (error, 2) })
                {
                    Check(UnixProviderProcess.Native.posix_spawn_file_actions_adddup2(actions.Pointer, handle.DangerousGetHandle().ToInt32(), target));
                }

                using var argv = new UnixProviderProcess.StringVector(new[] { executable }.Concat(arguments));
                using var env = new UnixProviderProcess.StringVector(environment.Select(pair => pair.Key + "=" + pair.Value));
                using var name = new NativeMemory(executable);
                Check(UnixProviderProcess.Native.posix_spawn(out var pid, name.Pointer, actions.Pointer, attributes.Pointer, argv.Pointer, env.Pointer));
                return new(pid, UnixProviderProcess.Reap(pid));
            }
            finally { _ = UnixProviderProcess.Native.posix_spawn_file_actions_destroy(actions.Pointer); }
        }
        finally { _ = UnixProviderProcess.Native.posix_spawnattr_destroy(attributes.Pointer); }
    }

    private static void Check(bool success) { if (!success)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }
    private static void Check(int error) { if (error is not 0)
        {
            throw new Win32Exception(error);
        }
    }
}
