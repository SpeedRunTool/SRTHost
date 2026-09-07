using System.Diagnostics;

namespace SRTHost.PluginRunner.Tests;

/// <summary>
/// The runner's command line, exercised against the real executable.
/// </summary>
/// <remarks>
/// Only the router ever writes this command line, so the value of these is not that a user might get
/// it wrong - it is that a mismatch between the two executables after an in-place update should stop
/// the runner immediately and say why, rather than leaving a process alive that connected to nothing.
/// </remarks>
public class RunnerCommandLineTests
{
    private const int ExitInvalidArguments = 2;
    private const int ExitNoRouter = 3;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RefusesToStartWithoutAPipe()
    {
        (int exitCode, string _, string standardError) = await RunAsync([], Token);

        Assert.Equal(ExitInvalidArguments, exitCode);
        Assert.Contains("--pipe is required", standardError, StringComparison.Ordinal);
        Assert.Contains("--log-level", standardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesAnArgumentItDoesNotKnow()
    {
        (int exitCode, string _, string standardError) = await RunAsync(["--pipe", "x", "--nonsense", "y"], Token);

        Assert.Equal(ExitInvalidArguments, exitCode);
        Assert.Contains("--nonsense", standardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesAnUnparseableLogLevel()
    {
        (int exitCode, string _, string standardError) = await RunAsync(["--pipe=x", "--log-level=chatty"], Token);

        Assert.Equal(ExitInvalidArguments, exitCode);
        Assert.Contains("chatty", standardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A runner whose router never appears exits rather than waiting forever, and says so with a
    /// distinct code - the supervisor treats "nobody was listening" differently from "the plugin
    /// failed".
    /// </summary>
    [Fact]
    public async Task GivesUpWhenNoRouterIsListening()
    {
        (int exitCode, string _, string _) = await RunAsync(
            ["--pipe", "SRTHost.deadbeef.Nothing.Listening.Here", "--connect-timeout", "1"],
            Token);

        Assert.Equal(ExitNoRouter, exitCode);
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(
        string[] arguments,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(RunnerLocator.ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{RunnerLocator.ExecutablePath}'.");

        // Both streams are read before waiting: a process that fills a redirected pipe buffer blocks
        // on the write, and waiting for exit first would deadlock against it.
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).WaitAsync(Timeout, cancellationToken);

        return (process.ExitCode, await standardOutput, await standardError);
    }
}
