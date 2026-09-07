namespace SRTHost.Ipc.Tests;

public class PlaceholderTests
{
    // The real suite - frame round-trip, fragmented reads, oversize rejection, malformed headers,
    // throughput - lands with the frame codec in Phase 2. This exists so the test job is wired up
    // and green from Phase 0 rather than being bolted on later.
    [Fact]
    public void TestProjectIsWiredUp() => Assert.True(true);
}
