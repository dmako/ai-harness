using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Harness.Core.Providers;
using Microsoft.Win32.SafeHandles;

namespace Harness.Infrastructure.Processes;

[UnsupportedOSPlatform("windows")]
internal sealed class UnixProviderProcess : IProviderProcess
{
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly FileStream _error;
    private int _disposed;

    private UnixProviderProcess(int id, FileStream input, FileStream output, FileStream error)
    {
        Id = id;
        _input = input;
        _output = output;
        _error = error;
        Completion = Reap(Id);
    }

    internal static Task<int> Reap(int pid) => Task.Factory.StartNew(() =>
    {
        int status;
        while (Native.waitpid(pid, out status, 0) < 0)
        {
            if (Marshal.GetLastPInvokeError() is not 4)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return (status & 0x7f) is 0 ? status >> 8 : 128 + (status & 0x7f);
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public int Id { get; }
    public Stream Input => _input;
    public Stream Output => _output;
    public Stream Error => _error;
    public Task<int> Completion { get; }

    internal static UnixProviderProcess Start(string executable, IReadOnlyList<string> arguments, string directory, Dictionary<string, string> environment)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Provider process containment supports Linux, macOS and Windows.");
        // Opaque storage exceeds glibc's 336-byte attr/80-byte actions and Darwin's pointer-sized types.
        using var attributes = new NativeMemory(1024);
        using var actions = new NativeMemory(1024);
        Check(Native.posix_spawnattr_init(attributes.Pointer));
        try
        {
            Check(Native.posix_spawn_file_actions_init(actions.Pointer));
            try
            {
                Check(Native.posix_spawnattr_setpgroup(attributes.Pointer, 0));
                Check(Native.posix_spawnattr_setflags(attributes.Pointer, 2)); // POSIX_SPAWN_SETPGROUP
                using var workingDirectory = new NativeMemory(directory);
                Check(Native.posix_spawn_file_actions_addchdir_np(actions.Pointer, workingDirectory.Pointer));
                using var input = Pipe();
                using var output = Pipe();
                using var error = Pipe();
                foreach (var (descriptor, target) in new[] { (input.Read, 0), (output.Write, 1), (error.Write, 2) })
                    Check(Native.posix_spawn_file_actions_adddup2(actions.Pointer, descriptor, target));
                using var argv = new StringVector(new[] { executable }.Concat(arguments));
                using var env = new StringVector(environment.Select(pair => pair.Key + "=" + pair.Value));
                using var executableName = new NativeMemory(executable);
                Check(Native.posix_spawn(out var pid, executableName.Pointer, actions.Pointer, attributes.Pointer, argv.Pointer, env.Pointer));
                try
                {
                    return new(pid, input.TakeWrite(), output.TakeRead(), error.TakeRead());
                }
                catch
                {
                    _ = Native.kill(-pid, 9);
                    _ = Native.waitpid(pid, out _, 0);
                    throw;
                }
            }
            finally { _ = Native.posix_spawn_file_actions_destroy(actions.Pointer); }
        }
        finally { _ = Native.posix_spawnattr_destroy(attributes.Pointer); }
    }

    public void KillTree() => _ = Native.kill(-Id, 9);

    [SuppressMessage("Minor Code Smell", "S6966:Awaitable method should be used", Justification = "Close redirected pipes synchronously to unblock outstanding I/O before awaiting process shutdown.")]
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) is not 0)
            return;
        try
        {
            _input.Dispose();
            await Completion.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = Native.kill(-Id, 15);
        }
        finally
        {
            KillTree();
            try
            { await Completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            finally { _output.Dispose(); _error.Dispose(); }
        }
    }

    private static PipePair Pipe()
    {
        var descriptors = new int[2];
        if (Native.pipe(descriptors) is not 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        var pair = new PipePair(descriptors[0], descriptors[1]);
        try
        {
            if (Native.fcntl(pair.Read, 2, 1) < 0 || Native.fcntl(pair.Write, 2, 1) < 0) // FD_CLOEXEC
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return pair;
        }
        catch { pair.Dispose(); throw; }
    }

    private static void Check(int error)
    {
        if (error is not 0)
            throw new Win32Exception(error);
    }

    private sealed class PipePair(int read, int write) : IDisposable
    {
        private SafeFileHandle? _read = new((IntPtr)read, true);
        private SafeFileHandle? _write = new((IntPtr)write, true);
        public int Read => read;
        public int Write => write;
        internal FileStream TakeRead() { var stream = new FileStream(_read!, FileAccess.Read); _read = null; return stream; }
        internal FileStream TakeWrite() { var stream = new FileStream(_write!, FileAccess.Write); _write = null; return stream; }
        public void Dispose() { _read?.Dispose(); _write?.Dispose(); }
    }

    internal sealed class StringVector : IDisposable
    {
        private readonly List<NativeMemory> _strings = [];
        private readonly NativeMemory _vector;
        internal StringVector(IEnumerable<string> strings)
        {
            try
            {
                foreach (var value in strings)
                    _strings.Add(new(value));
                _vector = new((_strings.Count + 1) * IntPtr.Size);
                Marshal.Copy(_strings.Select(value => value.Pointer).ToArray(), 0, _vector.Pointer, _strings.Count);
            }
            catch
            {
                foreach (var value in _strings)
                    value.Dispose();
                throw;
            }
        }
        internal IntPtr Pointer => _vector.Pointer;
        public void Dispose() { _vector.Dispose(); foreach (var value in _strings) value.Dispose(); }
    }

    internal static class Native
    {
        private const string LibC = "libc";
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)][DllImport(LibC)] internal static extern int posix_spawnattr_init(IntPtr attributes);
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)][DllImport(LibC)] internal static extern int posix_spawnattr_destroy(IntPtr attributes);
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)][DllImport(LibC)] internal static extern int posix_spawnattr_setpgroup(IntPtr attributes, int group);
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)][DllImport(LibC)] internal static extern int posix_spawnattr_setflags(IntPtr attributes, short flags);
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)][DllImport(LibC)] internal static extern int posix_spawn_file_actions_init(IntPtr actions);
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)][DllImport(LibC)] internal static extern int posix_spawn_file_actions_destroy(IntPtr actions);
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)][DllImport(LibC)] internal static extern int posix_spawn_file_actions_adddup2(IntPtr actions, int descriptor, int target);
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)][DllImport(LibC)] internal static extern int posix_spawn_file_actions_addchdir_np(IntPtr actions, IntPtr directory);
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)][DllImport(LibC)] internal static extern int posix_spawn(out int pid, IntPtr executable, IntPtr actions, IntPtr attributes, IntPtr argv, IntPtr environment);
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)][DllImport(LibC, SetLastError = true)] internal static extern int pipe([Out] int[] descriptors);
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)][DllImport(LibC, SetLastError = true)] internal static extern int fcntl(int descriptor, int command, int value);
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)][DllImport(LibC, SetLastError = true)] internal static extern int waitpid(int pid, out int status, int options);
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)][DllImport(LibC, SetLastError = true)] internal static extern int kill(int pid, int signal);
    }
}
