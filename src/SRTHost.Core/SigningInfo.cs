using System.Security.Cryptography.X509Certificates;

namespace SRTHost.Core;

/// <summary>
/// Reads Authenticode signing information from a file on disk. Ported from the 3.x host, which
/// logged this at startup so a user's log makes clear whether they are running a signed build.
/// </summary>
public static class SigningInfo
{
    /// <summary>
    /// Returns the signing certificate for <paramref name="filePath"/>, or <see langword="null"/>
    /// if the file is unsigned, missing, or otherwise unreadable.
    /// </summary>
    public static X509Certificate2? GetSigningCertificate(string filePath)
    {
        try
        {
            // SYSLIB0057 blanket-obsoletes loading certificate data through constructors and
            // Import, steering callers to X509CertificateLoader. That loader deals in raw
            // certificate blobs and has no equivalent for extracting the Authenticode certificate
            // embedded in a PE file, so CreateFromSignedFile remains the only managed API for this.
            // Suppressed narrowly, at the single call site.
#pragma warning disable SYSLIB0057
            using X509Certificate certificate = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            return X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        }
        catch
        {
            // Unsigned is a normal, expected state for a local developer build.
            return null;
        }
    }

    /// <summary>
    /// A short human-readable description of who signed <paramref name="filePath"/>, suitable for
    /// a log line or an About box.
    /// </summary>
    public static string Describe(string filePath)
    {
        using X509Certificate2? certificate = GetSigningCertificate(filePath);
        if (certificate is null)
            return "Unsigned";

        string subject = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        string issuer = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
        return $"Signed by {subject} (issued by {issuer}, expires {certificate.NotAfter:yyyy-MM-dd})";
    }
}
