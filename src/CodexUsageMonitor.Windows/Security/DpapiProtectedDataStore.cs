using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CodexUsageMonitor.Core.Security;

namespace CodexUsageMonitor.Windows.Security;

public sealed class DpapiProtectedDataStore : IProtectedDataStore
{
    private const uint CryptProtectUiForbidden = 0x00000001;
    private const int MaximumBlobBytes = 1024 * 1024;

    public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> purpose)
    {
        if (plaintext.Length > MaximumBlobBytes || purpose.Length > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(plaintext));
        }

        return Transform(plaintext, purpose, protect: true);
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedData, ReadOnlySpan<byte> purpose)
    {
        if (protectedData.Length is <= 0 or > MaximumBlobBytes || purpose.Length > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(protectedData));
        }

        return Transform(protectedData, purpose, protect: false);
    }

    private static byte[] Transform(ReadOnlySpan<byte> input, ReadOnlySpan<byte> entropy, bool protect)
    {
        var inputPointer = nint.Zero;
        var entropyPointer = nint.Zero;
        var description = nint.Zero;
        NativeMethods.DataBlob output = default;
        byte[]? inputCopy = null;
        byte[]? entropyCopy = null;
        try
        {
            inputPointer = Marshal.AllocHGlobal(input.Length);
            inputCopy = input.ToArray();
            Marshal.Copy(inputCopy, 0, inputPointer, inputCopy.Length);
            var inputBlob = new NativeMethods.DataBlob(input.Length, inputPointer);
            var entropyBlob = default(NativeMethods.DataBlob);
            if (!entropy.IsEmpty)
            {
                entropyPointer = Marshal.AllocHGlobal(entropy.Length);
                entropyCopy = entropy.ToArray();
                Marshal.Copy(entropyCopy, 0, entropyPointer, entropyCopy.Length);
                entropyBlob = new NativeMethods.DataBlob(entropy.Length, entropyPointer);
            }

            bool succeeded;
            if (protect)
            {
                succeeded = NativeMethods.CryptProtectData(
                    ref inputBlob,
                    "Codex Usage Monitor protected data",
                    ref entropyBlob,
                    nint.Zero,
                    nint.Zero,
                    CryptProtectUiForbidden,
                    out output);
            }
            else
            {
                succeeded = NativeMethods.CryptUnprotectData(
                    ref inputBlob,
                    out description,
                    ref entropyBlob,
                    nint.Zero,
                    nint.Zero,
                    CryptProtectUiForbidden,
                    out output);
            }

            if (!succeeded)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (output.Size is < 0 or > MaximumBlobBytes || (output.Size > 0 && output.Data == nint.Zero))
            {
                throw new CryptographicException("DPAPI returned an invalid output buffer.");
            }

            var result = new byte[output.Size];
            if (output.Size > 0)
            {
                Marshal.Copy(output.Data, result, 0, output.Size);
            }

            return result;
        }
        finally
        {
            if (inputCopy is not null)
            {
                CryptographicOperations.ZeroMemory(inputCopy);
            }

            if (entropyCopy is not null)
            {
                CryptographicOperations.ZeroMemory(entropyCopy);
            }

            ZeroAndFree(inputPointer, input.Length);
            ZeroAndFree(entropyPointer, entropy.Length);
            if (description != nint.Zero)
            {
                NativeMethods.LocalFree(description);
            }

            if (output.Data != nint.Zero)
            {
                ZeroUnmanaged(output.Data, Math.Clamp(output.Size, 0, MaximumBlobBytes));
                NativeMethods.LocalFree(output.Data);
            }
        }
    }

    private static unsafe void ZeroUnmanaged(nint pointer, int length)
    {
        if (pointer == nint.Zero || length <= 0)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(new Span<byte>(pointer.ToPointer(), length));
    }

    private static void ZeroAndFree(nint pointer, int length)
    {
        if (pointer == nint.Zero)
        {
            return;
        }

        ZeroUnmanaged(pointer, length);
        Marshal.FreeHGlobal(pointer);
    }

    private static class NativeMethods
    {
        [DllImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CryptProtectData(
            ref DataBlob dataIn,
            string? description,
            ref DataBlob optionalEntropy,
            nint reserved,
            nint promptStruct,
            uint flags,
            out DataBlob dataOut);

        [DllImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CryptUnprotectData(
            ref DataBlob dataIn,
            out nint description,
            ref DataBlob optionalEntropy,
            nint reserved,
            nint promptStruct,
            uint flags,
            out DataBlob dataOut);

        [DllImport("kernel32.dll")]
        internal static extern nint LocalFree(nint memory);

        [StructLayout(LayoutKind.Sequential)]
        internal struct DataBlob(int size, nint data)
        {
            internal int Size = size;
            internal nint Data = data;
        }
    }
}
