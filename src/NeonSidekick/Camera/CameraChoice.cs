namespace NeonSidekick.Camera;

/// <summary>
/// How <c>Camera device</c> names a camera (2026-10-27, the review: of two cameras with one name, <c>/camera use 2</c> saved the name and the
/// next open took the first). A camera is saved by its name, which holds when the USB port changes; one of two with one name is saved as
/// <c>name · id:…</c>, its id picking it out. An id that no longer lists (another port) falls back to the name, then to the first camera.
/// </summary>
public static class CameraChoice
{
    /// <summary>What separates a twin's name from its id in the saved value.</summary>
    public const string IdMark = " · id:";

    /// <summary>Whether another camera in <paramref name="devices"/> has <paramref name="device"/>'s name (any case).</summary>
    public static bool HasTwin(IReadOnlyList<CameraDevice> devices, CameraDevice device)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(device);
        return devices.Count(d => string.Equals(d.Name, device.Name, StringComparison.OrdinalIgnoreCase)) > 1;
    }

    /// <summary>The value <c>Camera device</c> saves for <paramref name="device"/>: its name, or of twins its name and id.</summary>
    public static string Saved(IReadOnlyList<CameraDevice> devices, CameraDevice device) =>
        HasTwin(devices, device) ? device.Name + IdMark + device.Id : device.Name;

    /// <summary>A saved value's name and id (null for a plain name).</summary>
    public static (string Name, string? Id) Read(string? saved)
    {
        string text = (saved ?? "").Trim();
        int mark = text.LastIndexOf(IdMark, StringComparison.Ordinal);
        return mark < 0 ? (text, null) : (text[..mark], text[(mark + IdMark.Length)..]);
    }

    /// <summary>The name a saved value shows as: the name, marked as the one chosen of two when the value carries an id.</summary>
    public static string Label(string? saved)
    {
        var (name, id) = Read(saved);
        return id is null ? name : CameraText.TwinLabel(name);
    }

    /// <summary>The camera <paramref name="saved"/> picks out: by its id, else by its name (any case); null for none (or nothing saved).</summary>
    public static CameraDevice? Find(IReadOnlyList<CameraDevice> devices, string? saved)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var (name, id) = Read(saved);
        if (name.Length == 0 && id is null)
        {
            return null;
        }

        return (id is null ? null : devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal)))
            ?? devices.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
