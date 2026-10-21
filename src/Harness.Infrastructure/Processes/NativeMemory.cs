using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Harness.Infrastructure.Processes;

internal sealed class NativeMemory : SafeHandleZeroOrMinusOneIsInvalid
{
    internal NativeMemory(int size) : base(true)
    {
        SetHandle(Marshal.AllocHGlobal(size));
        Marshal.Copy(new byte[size], 0, handle, size);
    }

    internal NativeMemory(string value, bool unicode = false) : base(true)
    {
        if (unicode)
        {
            SetHandle(Marshal.StringToHGlobalUni(value));
        }
        else
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
            SetHandle(Marshal.AllocHGlobal(bytes.Length));
            Marshal.Copy(bytes, 0, handle, bytes.Length);
        }
    }

    internal IntPtr Pointer => handle;

    protected override bool ReleaseHandle()
    {
        Marshal.FreeHGlobal(handle);
        return true;
    }
}
