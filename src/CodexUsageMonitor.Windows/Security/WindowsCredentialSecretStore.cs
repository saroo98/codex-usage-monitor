using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CodexUsageMonitor.Core.Security;

namespace CodexUsageMonitor.Windows.Security;

public sealed class WindowsCredentialSecretStore : ISecretStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int MaximumSecretBytes = 2560;
    private const string Prefix = "CodexUsageMonitor/";

    public Task SetAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = Target(key);
        if (secret.Length is <= 0 or > MaximumSecretBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(secret));
        }

        var copy = secret.ToArray();
        var pointer = nint.Zero;
        try
        {
            pointer = Marshal.AllocHGlobal(copy.Length);
            Marshal.Copy(copy, 0, pointer, copy.Length);
            var credential = new NativeMethods.Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = checked((uint)copy.Length),
                CredentialBlob = pointer,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!NativeMethods.CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return Task.CompletedTask;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
            if (pointer != nint.Zero)
            {
                unsafe
                {
                    CryptographicOperations.ZeroMemory(new Span<byte>(pointer.ToPointer(), secret.Length));
                }

                Marshal.FreeHGlobal(pointer);
            }
        }
    }

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = Target(key);
        if (!NativeMethods.CredRead(target, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            return error == 1168
                ? Task.FromResult<byte[]?>(null)
                : throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.Credential>(pointer);
            if (credential.CredentialBlobSize > MaximumSecretBytes ||
                (credential.CredentialBlobSize > 0 && credential.CredentialBlob == nint.Zero))
            {
                throw new InvalidDataException("Credential Manager returned an invalid secret buffer.");
            }

            var result = new byte[checked((int)credential.CredentialBlobSize)];
            if (result.Length > 0)
            {
                Marshal.Copy(credential.CredentialBlob, result, 0, result.Length);
            }

            return Task.FromResult<byte[]?>(result);
        }
        finally
        {
            NativeMethods.CredFree(pointer);
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!NativeMethods.CredDelete(Target(key), CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1168)
            {
                throw new Win32Exception(error);
            }
        }

        return Task.CompletedTask;
    }

    private static string Target(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var normalized = key.Trim();
        if (normalized.Length > 192 || normalized.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        return Prefix + normalized;
    }

    private static class NativeMethods
    {
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredWrite(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredRead(string target, uint type, uint flags, out nint credentialPointer);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll")]
        internal static extern void CredFree(nint buffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct Credential
        {
            internal uint Flags;
            internal uint Type;
            internal string TargetName;
            internal string? Comment;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            internal uint CredentialBlobSize;
            internal nint CredentialBlob;
            internal uint Persist;
            internal uint AttributeCount;
            internal nint Attributes;
            internal string? TargetAlias;
            internal string UserName;
        }
    }
}
