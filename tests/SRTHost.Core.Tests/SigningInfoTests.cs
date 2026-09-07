using SRTHost.Core;

namespace SRTHost.Core.Tests;

public class SigningInfoTests
{
    [Fact]
    public void GetSigningCertificate_ReturnsNull_ForMissingFile()
    {
        Assert.Null(SigningInfo.GetSigningCertificate(Path.Combine(Path.GetTempPath(), "srt-does-not-exist.exe")));
    }

    [Fact]
    public void Describe_ReportsUnsigned_ForMissingFile()
    {
        Assert.Equal("Unsigned", SigningInfo.Describe(Path.Combine(Path.GetTempPath(), "srt-does-not-exist.exe")));
    }
}
