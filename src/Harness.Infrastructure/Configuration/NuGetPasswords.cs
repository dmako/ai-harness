using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

namespace Harness.Infrastructure.Configuration;

/// <summary>
/// The encrypted Password of nuget.config's packageSourceCredentials: NuGet encrypts it with DPAPI for the current
/// user (entropy "NuGet"), so it can be read on Windows only, by that user.
/// </summary>
internal static class NuGetPasswords
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NuGet");

    /// <summary>The password, or why it cannot be read.</summary>
    internal static (string Password, string Problem) Decrypt(string encrypted)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ("", "its Password is encrypted, which only Windows can read; use ClearTextPassword");
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(encrypted);
        }
        catch (FormatException)
        {
            return ("", "its encrypted Password is not valid");
        }

        return Unprotect(data) is { } plain
            ? (Encoding.UTF8.GetString(plain), "")
            : ("", "its encrypted Password cannot be decrypted by this user");
    }

    private static byte[]? Unprotect(byte[] data)
    {
        var input = new Native.DataBlob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        var entropy = new Native.DataBlob { Size = Entropy.Length, Data = Marshal.AllocHGlobal(Entropy.Length) };
        var output = default(Native.DataBlob);
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            Marshal.Copy(Entropy, 0, entropy.Data, Entropy.Length);
            if (
                !Native.CryptUnprotectData(
                    ref input,
                    IntPtr.Zero,
                    ref entropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    Native.UiForbidden,
                    ref output
                )
            )
            {
                return null;
            }

            var plain = new byte[output.Size];
            Marshal.Copy(output.Data, plain, 0, output.Size);
            return plain;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            Marshal.FreeHGlobal(entropy.Data);
            if (output.Data != IntPtr.Zero)
            {
                Native.LocalFree(output.Data);
            }
        }
    }

    [SuppressMessage(
        "Interoperability",
        "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute to generate P/Invoke marshalling code at compile time",
        Justification = "The repository disallows unsafe code; this bounded Windows ABI declaration uses runtime marshalling."
    )]
    private static class Native
    {
        internal const int UiForbidden = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        internal struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("crypt32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CryptUnprotectData(
            ref DataBlob dataIn,
            IntPtr description,
            ref DataBlob entropy,
            IntPtr reserved,
            IntPtr prompt,
            int flags,
            ref DataBlob dataOut
        );

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        internal static extern IntPtr LocalFree(IntPtr memory);
    }
}
