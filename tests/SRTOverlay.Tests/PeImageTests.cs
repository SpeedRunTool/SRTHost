using System.Runtime.InteropServices;
using SRTOverlay.Injection;

namespace SRTOverlay.Tests;

/// <summary>
/// Pins the PE reader against real binaries.
/// </summary>
/// <remarks>
/// System DLLs rather than fixtures: the reader exists to answer questions about the shim, which is a
/// real signed PE, and a hand-rolled fixture would only prove the reader agrees with the fixture's
/// author. <c>kernel32.dll</c> is signed, exports thousands of names, and is on every machine that
/// can run these tests at all.
/// </remarks>
public sealed class PeImageTests
{
    private static string System32(string name)
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);

    [Fact]
    public void ReadsMachineTypeAndExportsFromASystemLibrary()
    {
        PeImage.PeInfo kernel32 = PeImage.Read(System32("kernel32.dll"));

        Assert.Equal(
            RuntimeInformation.OSArchitecture == Architecture.X64 ? PeImage.MachineAmd64 : PeImage.MachineI386,
            kernel32.Machine);

        // The two entry points the injector itself resolves. If the reader can find these, it can
        // find the shim's start export.
        Assert.True(kernel32.Exports.ContainsKey("LoadLibraryW"));
        Assert.True(kernel32.Exports.ContainsKey("GetProcAddress"));
        Assert.All(kernel32.Exports.Values, rva => Assert.True(rva > 0));
    }

    [Fact]
    public void ReadsTheAuthenticodeBlobOfASignedFile()
    {
        byte[]? blob = PeImage.ReadSignatureBlob(System32("kernel32.dll"));

        Assert.NotNull(blob);

        // A DER SEQUENCE, which is what a PKCS#7 SignedData starts with. Checking the shape rather
        // than the length keeps this from failing whenever Windows re-signs the file.
        Assert.Equal(0x30, blob[0]);
    }

    [Fact]
    public void ReportsNoSignatureBlobForAnUnsignedFile()
    {
        // The test assembly itself. Nothing in this repository is signed at build time - signing
        // happens in CI - so this is reliably unsigned.
        Assert.Null(PeImage.ReadSignatureBlob(typeof(PeImageTests).Assembly.Location));
    }

    [Fact]
    public void RejectsAFileThatIsNotAPortableExecutable()
    {
        string path = Path.Combine(Path.GetTempPath(), $"srt-overlay-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]);

        try
        {
            Assert.Throws<BadImageFormatException>(() => PeImage.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
