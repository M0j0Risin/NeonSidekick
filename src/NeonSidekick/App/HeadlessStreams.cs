using System.Text;

namespace NeonSidekick.App;

/// <summary>
/// Headless's own stdin and stderr (2026-11-22, the review's #4 and #121), picked by what each is. A redirected stdin is read as UTF-8 (a BOM
/// still names another encoding), since a redirected <c>Console.In</c> decodes with the console's OEM code page. A console stdin is
/// <c>Console.In</c>: on Windows after <paramref name="wideConsole"/> sets the input encoding to UTF-16, which .NET reads with
/// <c>ReadConsoleW</c>, so typed text arrives whole; a UTF-8 decoder over the console's bytes garbled it. stderr is a UTF-8 writer of its own
/// when it is redirected or the output pin to UTF-8 held, else <c>Console.Error</c>, whose encoding is the console's. Both synchronized.
/// </summary>
internal static class HeadlessStreams
{
    public static TextReader Input(bool redirected, Func<Stream> stdin, Func<TextReader> consoleIn, Action? wideConsole = null)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(consoleIn);
        if (redirected)
        {
            return new StreamReader(stdin(), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        }

        try
        {
            wideConsole?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException or System.Security.SecurityException)
        {
            // The console's own code page, then: no worse than before.
        }

        return consoleIn();
    }

    public static TextWriter Error(bool redirected, bool outputPinned, Func<Stream> stderr, Func<TextWriter> consoleError)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(consoleError);
        return TextWriter.Synchronized(redirected || outputPinned
            ? new StreamWriter(stderr(), new UTF8Encoding(false)) { AutoFlush = true }
            : consoleError());
    }
}
