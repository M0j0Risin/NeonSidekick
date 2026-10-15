using NeonSidekick.Files;

namespace NeonSidekick.Viewer;

/// <summary>
/// The pictures' deletes (later on 2026-10-15, <c>File deletes to Recycle bin</c>): the viewer's and the thumbnail browser's double-Del and the
/// picture menu's Delete, on Windows and on a Mac, through <see cref="FileDeletes.Remove"/>. <see cref="ToRecycleBin"/> and <see cref="Bin"/> are
/// set by <c>Program</c> (the setting read at each delete, the system's bin); unset, every delete is direct, as before. A failure throws as
/// <see cref="File.Delete"/> did, so each window's catch stays.
/// </summary>
public static class PictureDeletes
{
    /// <summary>Whether a delete goes to the bin now: the effective <c>File deletes to Recycle bin</c>.</summary>
    public static Func<bool>? ToRecycleBin { get; set; }

    /// <summary>The system's bin, <see cref="RecycleBin.Platform"/>.</summary>
    public static IRecycleBin? Bin { get; set; }

    /// <summary>Deletes the picture at <paramref name="path"/>: to the bin while the setting is on and there is one, else directly.</summary>
    public static DeleteWay Delete(string path) =>
        FileDeletes.Remove(path, isDirectory: false, isLink: false, ToRecycleBin?.Invoke() == true ? Bin : null);

    /// <summary>What a log line adds after "Deleted path": where it went.</summary>
    public static string LogTail(DeleteWay way) => way switch
    {
        DeleteWay.Recycled => " (to the " + FileText.RecycleBinName + ")",
        DeleteWay.DeletedNoBin => " (no " + FileText.RecycleBinName + " on that drive)",
        _ => "",
    };
}
