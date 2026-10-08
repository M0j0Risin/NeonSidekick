using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Shortcuts;

/// <summary>
/// <c>/shortcut</c>'s writer on Windows (2026-10-07): the user's desktop folder as the shell knows it
/// (<see cref="Environment.SpecialFolder.DesktopDirectory"/>, a OneDrive-moved desktop included), the running exe, and
/// <see cref="ShortcutNative"/>'s <c>ShellLink</c>, each call on a short STA thread of its own with COM started and stopped around
/// it (the <c>PictureWindowDrag.Probe</c> shape), so the screen's thread never has its apartment chosen for it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsShortcutWriter : IShortcutWriter
{
    public ShortcutKind Kind => ShortcutKind.Lnk;

    public string DesktopFolder => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public string? Executable => Environment.ProcessPath is { Length: > 0 } path ? path : null;

    public void Write(ShortcutSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        OnComThread(() =>
        {
            ShortcutNative.Write(spec);
            return true;
        });
    }

    /// <summary>A <c>.lnk</c>'s target, arguments and Start-in folder as the shell reads them back (the smoke's and the tests' proof).</summary>
    public static (string Target, string Arguments, string WorkingDirectory) Read(string linkPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);
        return OnComThread(() => ShortcutNative.Read(linkPath));
    }

    private static T OnComThread<T>(Func<T> work)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            int co = ShortcutNative.CoInitializeEx(0, ShortcutNative.CoinitApartmentThreaded);
            try
            {
                if (co < 0 && co != ShortcutNative.RpcEChangedMode)
                {
                    throw new IOException($"CoInitializeEx failed (0x{co:X8})");
                }

                result = work();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                if (co >= 0)
                {
                    ShortcutNative.CoUninitialize();
                }
            }
        })
        { IsBackground = true, Name = "Desktop shortcut" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }
}
