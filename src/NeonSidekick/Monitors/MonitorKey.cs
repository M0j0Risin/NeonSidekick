namespace NeonSidekick.Monitors;

/// <summary>
/// The key a monitor's input names are saved under (2026-10-23, the monitors plan): from the target's device path
/// <c>\\?\DISPLAY#AUSAA34#5&amp;1ceef1a8&amp;1&amp;UID4355#{e6f07b5f-…}</c>, the model id and the connector's UID, <c>ausaa34-uid4355</c> —
/// the same monitor on the same port whatever the layout (Windows moves GDI names: the spike's XG27UQDMS brought back alone became
/// <c>\\.\DISPLAY1</c>). A cable moved to another port changes the UID, so a lookup falls back to the model id alone when exactly one
/// connected monitor has it (<see cref="Saved"/>). Pure.
/// </summary>
public static class MonitorKey
{
    /// <summary>The key for <paramref name="devicePath"/>, or null when it is not a <c>DISPLAY#model#…</c> path (an indirect or virtual display).</summary>
    public static string? From(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return null;
        }

        string[] parts = devicePath.Split('#');
        if (parts.Length < 3 || !parts[0].EndsWith("DISPLAY", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string model = Clean(parts[1]);
        if (model.Length == 0)
        {
            return null;
        }

        string instance = parts[2];
        int uid = instance.LastIndexOf("UID", StringComparison.OrdinalIgnoreCase);
        string port = uid >= 0 ? Clean(instance[uid..]) : Clean(instance.Split('&')[^1]);
        return port.Length == 0 ? model : model + "-" + port;
    }

    /// <summary>The model id a key starts with (<c>ausaa34</c>).</summary>
    public static string Model(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        int dash = key.IndexOf('-', StringComparison.Ordinal);
        return dash < 0 ? key : key[..dash];
    }

    /// <summary>
    /// The key the saved names for <paramref name="key"/> are under among <paramref name="savedKeys"/>: the same key, else one saved key of
    /// the same model when exactly one monitor in <paramref name="connected"/> has that model (a cable moved), else null.
    /// </summary>
    public static string? Saved(string key, IEnumerable<string> savedKeys, IEnumerable<string?> connected)
    {
        ArgumentNullException.ThrowIfNull(key);
        var saved = savedKeys.Distinct(StringComparer.Ordinal).ToList();
        if (saved.Contains(key, StringComparer.Ordinal))
        {
            return key;
        }

        string model = Model(key);
        if (connected.Count(k => k is not null && string.Equals(Model(k), model, StringComparison.Ordinal)) != 1)
        {
            return null;
        }

        var same = saved.Where(s => string.Equals(Model(s), model, StringComparison.Ordinal)).ToList();
        return same.Count == 1 ? same[0] : null;
    }

    private static string Clean(string text) => new(text.Trim().ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c)).ToArray());
}
