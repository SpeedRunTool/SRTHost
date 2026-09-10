using System.Globalization;
using System.Text;

namespace SRTOverlay.Core;

/// <summary>
/// The shim's log file. One writer, opened once, shared for reading.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the shim runs inside a process nothing else can attach a debugger to without
/// stopping the game, and because a fault here is a fault in someone's play session. It is the only
/// diagnostic channel the injected side has until the pipe to the runner is up.
/// </para>
/// <para>
/// <b>Nothing on the render path may call this.</b> It allocates, formats and takes a lock, and the
/// per-frame budget is under a millisecond on the game's own thread. Startup, teardown, hook
/// installation and error paths only.
/// </para>
/// </remarks>
public static class OverlayLog
{
    private static readonly Lock Gate = new();
    private static StreamWriter? writer;

    /// <summary>Whether a log file is open. False is normal - an empty log path disables it.</summary>
    public static bool IsOpen => Volatile.Read(ref writer) is not null;

    /// <summary>
    /// Open <paramref name="path"/> for writing, replacing any previous file.
    /// </summary>
    /// <remarks>
    /// Shared for read and write so the log can be tailed live while the game runs, and so a second
    /// injection into the same process does not fail on a lock it cannot see. Failure to open is
    /// swallowed: an overlay that refuses to start because it could not write a log is worse than
    /// one that runs silently.
    /// </remarks>
    public static void Open(string path)
    {
        lock (Gate)
        {
            Close();

            try
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true,
                };
            }
            catch (Exception)
            {
                // Nowhere to report it to; the log is the reporting channel.
                writer = null;
            }
        }
    }

    /// <summary>Write one line, timestamped and stamped with the calling thread.</summary>
    public static void Write(string message)
    {
        lock (Gate)
        {
            if (writer is null)
                return;

            try
            {
                writer.Write(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                writer.Write(" [t");
                writer.Write(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture));
                writer.Write("] ");
                writer.WriteLine(message);
            }
            catch (Exception)
            {
                // Same reasoning as Open: never let logging be the thing that breaks the game.
            }
        }
    }

    /// <summary>Write an exception with its type, message and stack.</summary>
    /// <remarks>
    /// Called from the catch blocks that guard every unmanaged boundary. Those blocks exist because
    /// an escaping managed exception is a <c>FailFast</c>, and a <c>FailFast</c> here takes the game
    /// down with it - so this is the last thing that runs before the shim decides to do nothing.
    /// </remarks>
    public static void WriteException(string context, Exception exception)
        => Write($"{context}: {exception.GetType().FullName}: {exception.Message}{Environment.NewLine}{exception.StackTrace}");

    /// <summary>Flush and close. Safe to call when nothing is open.</summary>
    public static void Close()
    {
        lock (Gate)
        {
            try
            {
                writer?.Flush();
                writer?.Dispose();
            }
            catch (Exception)
            {
                // Teardown; there is nothing left to tell.
            }
            finally
            {
                writer = null;
            }
        }
    }
}
