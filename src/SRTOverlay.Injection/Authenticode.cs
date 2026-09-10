using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace SRTOverlay.Injection;

/// <summary>
/// Verifies that the shim about to be loaded into a game is the one this project signed.
/// </summary>
/// <remarks>
/// <para>
/// This is the honest answer to "what stops someone dropping their own DLL in that folder", and
/// section 11 of the 5.0 plan makes it a requirement rather than a nicety. It costs one
/// <c>WinVerifyTrust</c> call on a path the injector was about to hand to <c>LoadLibraryW</c>.
/// </para>
/// <para>
/// It is not a security boundary against a local attacker who already runs as the user - such an
/// attacker can inject whatever they like without going through this code at all. What it does buy
/// is that the SRT overlay never becomes the mechanism by which some other binary gets into a game
/// process, which is the thing that would cost the ecosystem its reputation.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class Authenticode
{
    /// <summary>The result of checking one file.</summary>
    /// <param name="Trusted">Whether the chain verified.</param>
    /// <param name="Status">The <c>WinVerifyTrust</c> status, zero on success.</param>
    /// <param name="Subject">The signer's subject name, or empty when there is no signature.</param>
    public readonly record struct Result(bool Trusted, int Status, string Subject)
    {
        /// <summary>A one-line description for a log.</summary>
        public string Describe() => Trusted
            ? $"trusted, signed by {Subject}"
            : $"NOT trusted (WinVerifyTrust 0x{Status:X8}){(Subject.Length == 0 ? string.Empty : $", subject {Subject}")}";
    }

    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_SAFER_FLAG = 0x100;

    /// <summary>Verify a file's Authenticode signature and report who signed it.</summary>
    public static Result Verify(string path)
    {
        int status = VerifyTrust(path);
        string subject = status == 0 ? ReadSubject(path) : string.Empty;
        return new Result(status == 0, status, subject);
    }

    /// <summary>
    /// Verify, and additionally require the signer's subject to contain <paramref name="expectedSubject"/>.
    /// </summary>
    /// <remarks>
    /// A substring match on the subject rather than a thumbprint pin, because Trusted Signing rotates
    /// certificates frequently by design - a pinned thumbprint would fail on the first rotation and
    /// teach whoever hit it to turn the check off, which is worse than the weaker match.
    /// </remarks>
    public static Result Verify(string path, string expectedSubject)
    {
        Result result = Verify(path);
        if (!result.Trusted || result.Subject.Contains(expectedSubject, StringComparison.OrdinalIgnoreCase))
            return result;

        return result with { Trusted = false };
    }

    private static int VerifyTrust(string path)
    {
        WinTrustFileInfo file = new()
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = Marshal.StringToHGlobalUni(path),
            hFile = 0,
            pgKnownSubject = 0,
        };

        nint fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, fileInfo, fDeleteOld: false);

            WinTrustData data = new()
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = fileInfo,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_SAFER_FLAG,
            };

            Guid action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            int status = WinVerifyTrust(-1, ref action, ref data);

            // The state handle is allocated by the VERIFY call and leaks unless the same structure
            // comes back through with STATEACTION_CLOSE. Missing this is the classic WinVerifyTrust
            // bug, and it leaks a handle per check in a process that runs for a whole play session.
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            _ = WinVerifyTrust(-1, ref action, ref data);

            return status;
        }
        finally
        {
            Marshal.FreeHGlobal(file.pcwszFilePath);
            Marshal.FreeHGlobal(fileInfo);
        }
    }

    /// <summary>The subject of the certificate that signed <paramref name="path"/>, or empty.</summary>
    /// <remarks>
    /// Decodes the PE's embedded PKCS#7 rather than calling <c>X509Certificate.CreateFromSignedFile</c>,
    /// which is obsolete under SYSLIB0057 and has no direct replacement - <c>X509CertificateLoader</c>
    /// loads certificate blobs and does not know about PE files. <see cref="PeImage"/> is already
    /// parsing this file for its exports, so getting the blob costs one data directory lookup.
    /// <para>
    /// This only reports who signed it. Whether that signature is any good is <c>WinVerifyTrust</c>'s
    /// answer, and this is never consulted unless that call already succeeded.
    /// </para>
    /// </remarks>
    private static string ReadSubject(string path)
    {
        try
        {
            byte[]? blob = PeImage.ReadSignatureBlob(path);
            if (blob is null)
                return string.Empty;

            SignedCms signature = new();
            signature.Decode(blob);

            // SignerInfos[0] is the signature itself; any others are countersignatures and
            // timestamps, which say when rather than who.
            X509Certificate2? signer = signature.SignerInfos.Count > 0 ? signature.SignerInfos[0].Certificate : null;
            return signer?.Subject ?? string.Empty;
        }
        catch (Exception)
        {
            // An unsigned or oddly-signed file; the trust status already said so.
            return string.Empty;
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WinVerifyTrust(nint window, ref Guid action, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        internal uint cbStruct;
        internal nint pcwszFilePath;
        internal nint hFile;
        internal nint pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        internal uint cbStruct;
        internal nint pPolicyCallbackData;
        internal nint pSIPClientData;
        internal uint dwUIChoice;
        internal uint fdwRevocationChecks;
        internal uint dwUnionChoice;
        internal nint pFile;
        internal uint dwStateAction;
        internal nint hWVTStateData;
        internal nint pwszURLReference;
        internal uint dwProvFlags;
        internal uint dwUIContext;
        internal nint pSignatureSettings;
    }
}
