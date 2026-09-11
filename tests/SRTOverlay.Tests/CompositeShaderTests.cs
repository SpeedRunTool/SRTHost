using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using SRTOverlay.DirectX12;

namespace SRTOverlay.Tests;

/// <summary>
/// The precompiled composite shaders, and the guard that keeps them in step with their source.
/// </summary>
/// <remarks>
/// The shim ships shader bytecode rather than compiling inside a game, so the bytecode lives in a
/// generated file that a person has to remember to regenerate after editing the HLSL. These tests are
/// what turns "forgot to regenerate" into a red test rather than a stale overlay: one checks the bytes
/// are real DXBC, the other checks the SHA-256 the generator recorded still matches the HLSL on disk.
/// </remarks>
public sealed class CompositeShaderTests
{
    [Fact]
    public void VertexShaderIsRealDxbc() => AssertDxbc(CompositeShaders.VertexShader);

    [Fact]
    public void PixelShaderIsRealDxbc() => AssertDxbc(CompositeShaders.PixelShader);

    private static void AssertDxbc(ReadOnlySpan<byte> bytecode)
    {
        // fxc/DXBC containers begin with the four-byte magic "DXBC". A stale or empty generated file
        // fails this before it ever reaches a pipeline-state creation inside a game.
        Assert.True(bytecode.Length > 4, "shader bytecode is empty");
        Assert.Equal((byte)'D', bytecode[0]);
        Assert.Equal((byte)'X', bytecode[1]);
        Assert.Equal((byte)'B', bytecode[2]);
        Assert.Equal((byte)'C', bytecode[3]);
    }

    [Fact]
    public void GeneratedBytecodeMatchesTheHlslItWasBuiltFrom()
    {
        string hlsl = ShaderSourcePath();
        if (!File.Exists(hlsl))
        {
            // The guard only runs where the source tree is present, which is any build-and-test run.
            Assert.Skip($"Composite.hlsl not found next to the source tree ({hlsl}).");
            return;
        }

        // Normalise to LF so a checkout's autocrlf cannot change the hash, matching Compile-Shaders.ps1.
        string normalised = File.ReadAllText(hlsl).Replace("\r\n", "\n");
        string actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)));

        Assert.True(
            string.Equals(actual, CompositeShaders.SourceSha256, StringComparison.OrdinalIgnoreCase),
            "Composite.hlsl has changed since CompositeShaders.g.cs was generated. " +
            "Re-run Shaders/Compile-Shaders.ps1 to regenerate the bytecode.");
    }

    /// <summary>
    /// The HLSL's path, derived from this test file's own compile-time path so it needs no runtime
    /// configuration and points at the same checkout the test was built from.
    /// </summary>
    private static string ShaderSourcePath([CallerFilePath] string thisFile = "")
    {
        string testDirectory = Path.GetDirectoryName(thisFile)!; // tests/SRTOverlay.Tests
        return Path.GetFullPath(Path.Combine(
            testDirectory, "..", "..", "src", "SRTOverlay.DirectX12", "Shaders", "Composite.hlsl"));
    }
}
