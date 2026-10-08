using System.Runtime.Versioning;
using System.Text;
using NeonSidekick.Diagnostics;
using NeonSidekick.Viewer;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Shortcuts;

/// <summary>
/// <c>/shortcut</c>'s writer on a Mac (2026-10-08, the user's ask: Windows' shortcut as a <c>.command</c> file): the desktop
/// (<see cref="Environment.SpecialFolder.DesktopDirectory"/>, <c>~/Desktop</c>), the running exe, and the file written as
/// <see cref="DesktopShortcut.CommandScript"/> says, mode 0755 by <see cref="File.SetUnixFileMode(string, UnixFileMode)"/>. One already
/// there is deleted first, so the old file's custom icon and its Open-with binding never outlive a new pick. Then two touches through
/// NSWorkspace, in-process (no <c>chmod</c>, <c>xattr</c>, <c>SetFile</c> or <c>osascript</c>: no process-start site): the app's
/// icon (<c>setIcon:forFile:options:</c>, from the embedded <c>app.ico</c>, which ImageIO reads) and the terminal it opens in, the
/// one <c>/terminal</c> opens (<see cref="MacTerminal.App"/>; the user's pick the same day), bound to this file alone by
/// <c>setDefaultApplicationAtURL:toOpenFileAtURL:completionHandler:</c> (macOS 12+, a nil handler: it settles asynchronously), Terminal
/// bound too so a system-wide "Change All" never decides it. Either touch failing leaves a working script, logged. NSWorkspace and
/// NSImage are used off the main thread inside an autorelease pool of the call's own, as the clipboard's NSPasteboard is.
/// Excluded from coverage with the AppKit layer; the smoke's <c>shortcut:command</c> proves it on the published exe.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacShortcutWriter : IShortcutWriter
{
    /// <summary>The embedded icon's logical name (the csproj's <c>EmbeddedResource</c>).</summary>
    public const string IconResource = "app.ico";

    private const ulong NoIconOptions = 0;   // NSWorkspaceIconCreationOptions: none

    public ShortcutKind Kind => ShortcutKind.Command;

    public string DesktopFolder => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public string? Executable => Environment.ProcessPath is { Length: > 0 } path ? path : null;

    public void Write(ShortcutSpec spec) => WriteFile(spec, MacTerminal.App());

    /// <summary>
    /// <paramref name="spec"/>'s <c>.command</c> file written for <paramref name="terminal"/>: whether the icon was set, and the
    /// terminal's bundle path it was bound to (null when the app was not found). Throws when the file itself could not be written.
    /// </summary>
    public static (bool IconSet, string? Terminal) WriteFile(ShortcutSpec spec, TerminalApp terminal)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (File.Exists(spec.LinkPath))
        {
            File.Delete(spec.LinkPath);
        }

        File.WriteAllText(spec.LinkPath, DesktopShortcut.CommandScript(spec), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.SetUnixFileMode(spec.LinkPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        bool iconSet = false;
        string? bound = null;
        nint pool = objc_autoreleasePoolPush();
        try
        {
            nint workspace = Send(Class("NSWorkspace"), Sel("sharedWorkspace"));
            iconSet = SetIcon(workspace, spec.LinkPath);
            bound = Bind(workspace, spec.LinkPath, terminal);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Warn(ShortcutText.Category, $"{spec.LinkPath}: {ex.Message}");
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }

        if (!iconSet)
        {
            DiagnosticLog.Warn(ShortcutText.Category, ShortcutText.IconNotSet(spec.LinkPath));
        }

        DiagnosticLog.Info(ShortcutText.Category, ShortcutText.OpensIn(spec.LinkPath, bound is null ? null : Path.GetFileNameWithoutExtension(bound)));
        return (iconSet, bound);
    }

    /// <summary>The app Finder opens <paramref name="path"/> with as a double-click would (its bundle's path), or null (the smoke's proof).</summary>
    public static string? OpenerOf(string path)
    {
        nint pool = objc_autoreleasePoolPush();
        try
        {
            nint workspace = Send(Class("NSWorkspace"), Sel("sharedWorkspace"));
            nint app = Send(workspace, Sel("URLForApplicationToOpenURL:"), FileUrl(path));
            return app == 0 ? null : FromNSString(Send(app, Sel("path")));
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>The embedded icon as an NSImage that ImageIO could read (the caller's pool owns it), or 0.</summary>
    public static unsafe nint LoadIcon()
    {
        using Stream? stream = typeof(MacShortcutWriter).Assembly.GetManifestResourceStream(IconResource);
        if (stream is null)
        {
            return 0;
        }

        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        nint data;
        fixed (byte* p = bytes)
        {
            data = Send(Class("NSData"), Sel("dataWithBytes:length:"), (nint)p, bytes.Length);   // copied
        }

        nint image = Send(Send(Class("NSImage"), Sel("alloc")), Sel("initWithData:"), data);
        if (image == 0)
        {
            return 0;
        }

        Send(image, Sel("autorelease"));
        return SendBool(image, Sel("isValid")) != 0 ? image : 0;
    }

    private static bool SetIcon(nint workspace, string path)
    {
        nint image = LoadIcon();
        return image != 0 && (byte)Send(workspace, Sel("setIcon:forFile:options:"), image, NSString(path), (nint)NoIconOptions) != 0;
    }

    /// <summary>The file bound to <paramref name="terminal"/> (by its path, else its bundle id); the app's path, or null when it was not found.</summary>
    private static string? Bind(nint workspace, string path, TerminalApp terminal)
    {
        nint app = terminal.BundlePath is { } bundle && Directory.Exists(bundle)
            ? Send(Class("NSURL"), Sel("fileURLWithPath:"), NSString(bundle))
            : Send(workspace, Sel("URLForApplicationWithBundleIdentifier:"), NSString(terminal.BundleId));
        if (app == 0)
        {
            return null;
        }

        SendVoid(workspace, Sel("setDefaultApplicationAtURL:toOpenFileAtURL:completionHandler:"), app, FileUrl(path), 0);
        return FromNSString(Send(app, Sel("path")));
    }

    private static nint FileUrl(string path) => Send(Class("NSURL"), Sel("fileURLWithPath:"), NSString(path));
}
