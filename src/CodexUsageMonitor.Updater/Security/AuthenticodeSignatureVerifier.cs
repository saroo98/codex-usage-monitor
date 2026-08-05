using CodexUsageMonitor.Updater.Install;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CodexUsageMonitor.Updater.Security;

[SupportedOSPlatform("windows")]
public sealed class AuthenticodeSignatureVerifier : IExecutableSignatureVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public Task<ExecutableSignatureResult> VerifyAsync(
        string filePath,
        IReadOnlySet<string> allowedPublisherThumbprints,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(allowedPublisherThumbprints);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(filePath);
        try
        {
            UpdatePathSecurity.EnsureRegularFile(fullPath, "The signed executable path is unsafe.");
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException)
        {
            return Task.FromResult(new ExecutableSignatureResult(false, null, null, "signature.file_missing_or_unsafe"));
        }

        if (allowedPublisherThumbprints.Count == 0)
        {
            return Task.FromResult(new ExecutableSignatureResult(false, null, null, "signature.publisher_pins_missing"));
        }

        var nativeResult = VerifyTrust(fullPath);
        if (nativeResult != 0)
        {
            return Task.FromResult(new ExecutableSignatureResult(false, null, null, $"signature.wintrust_{nativeResult:x8}"));
        }

        try
        {
#pragma warning disable SYSLIB0057 // No X509CertificateLoader API extracts the signer certificate from a signed PE file.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(fullPath));
#pragma warning restore SYSLIB0057
            var sha1 = certificate.GetCertHashString(HashAlgorithmName.SHA1);
            var sha256 = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            var matchedThumbprint = allowedPublisherThumbprints.FirstOrDefault(candidate =>
                string.Equals(candidate, sha1, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate, sha256, StringComparison.OrdinalIgnoreCase));

            return Task.FromResult(matchedThumbprint is not null
                ? new ExecutableSignatureResult(
                    true,
                    certificate.Subject,
                    matchedThumbprint.ToUpperInvariant(),
                    null)
                : new ExecutableSignatureResult(
                    false,
                    certificate.Subject,
                    sha256,
                    "signature.publisher_not_pinned"));
        }
        catch (CryptographicException)
        {
            return Task.FromResult(new ExecutableSignatureResult(false, null, null, "signature.certificate_unreadable"));
        }
    }

    private static int VerifyTrust(string filePath)
    {
        using var fileInfo = new WinTrustFileInfo(filePath);
        using var trustData = new WinTrustData(fileInfo.Pointer);
        var action = GenericVerifyV2;
        return NativeMethods.WinVerifyTrust(nint.Zero, ref action, trustData.Pointer);
    }

    private sealed class WinTrustFileInfo : IDisposable
    {
        private readonly nint _pathPointer;

        internal WinTrustFileInfo(string filePath)
        {
            _pathPointer = Marshal.StringToCoTaskMemUni(filePath);
            var value = new NativeMethods.WinTrustFileInfo
            {
                StructureSize = (uint)Marshal.SizeOf<NativeMethods.WinTrustFileInfo>(),
                FilePath = _pathPointer,
                FileHandle = nint.Zero,
                KnownSubject = nint.Zero,
            };
            Pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<NativeMethods.WinTrustFileInfo>());
            Marshal.StructureToPtr(value, Pointer, false);
        }

        internal nint Pointer { get; }

        public void Dispose()
        {
            if (Pointer != nint.Zero)
            {
                Marshal.DestroyStructure<NativeMethods.WinTrustFileInfo>(Pointer);
                Marshal.FreeCoTaskMem(Pointer);
            }

            if (_pathPointer != nint.Zero)
            {
                Marshal.FreeCoTaskMem(_pathPointer);
            }
        }
    }

    private sealed class WinTrustData : IDisposable
    {
        internal WinTrustData(nint fileInfoPointer)
        {
            var value = new NativeMethods.WinTrustData
            {
                StructureSize = (uint)Marshal.SizeOf<NativeMethods.WinTrustData>(),
                PolicyCallbackData = nint.Zero,
                SipClientData = nint.Zero,
                UiChoice = NativeMethods.WtdUiNone,
                RevocationChecks = NativeMethods.WtdRevokeWholeChain,
                UnionChoice = NativeMethods.WtdChoiceFile,
                UnionData = fileInfoPointer,
                StateAction = NativeMethods.WtdStateActionIgnore,
                StateData = nint.Zero,
                UrlReference = nint.Zero,
                ProviderFlags = NativeMethods.WtdRevocationCheckChainExcludeRoot,
                UiContext = 0,
                SignatureSettings = nint.Zero,
            };
            Pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<NativeMethods.WinTrustData>());
            Marshal.StructureToPtr(value, Pointer, false);
        }

        internal nint Pointer { get; }

        public void Dispose()
        {
            if (Pointer != nint.Zero)
            {
                Marshal.DestroyStructure<NativeMethods.WinTrustData>(Pointer);
                Marshal.FreeCoTaskMem(Pointer);
            }
        }
    }

    private static class NativeMethods
    {
        internal const uint WtdUiNone = 2;
        internal const uint WtdRevokeWholeChain = 1;
        internal const uint WtdChoiceFile = 1;
        internal const uint WtdStateActionIgnore = 0;
        internal const uint WtdRevocationCheckChainExcludeRoot = 0x00000080;

        [StructLayout(LayoutKind.Sequential)]
        internal struct WinTrustFileInfo
        {
            internal uint StructureSize;
            internal nint FilePath;
            internal nint FileHandle;
            internal nint KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WinTrustData
        {
            internal uint StructureSize;
            internal nint PolicyCallbackData;
            internal nint SipClientData;
            internal uint UiChoice;
            internal uint RevocationChecks;
            internal uint UnionChoice;
            internal nint UnionData;
            internal uint StateAction;
            internal nint StateData;
            internal nint UrlReference;
            internal uint ProviderFlags;
            internal uint UiContext;
            internal nint SignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
        internal static extern int WinVerifyTrust(nint window, ref Guid actionId, nint trustData);
    }
}
