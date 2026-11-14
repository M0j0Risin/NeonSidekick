using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>
/// <c>/settings export</c> and <c>/settings import</c> as one flow for the screen and headless (2026-11-13, the settings-transfer review's #58 and
/// #70: the flush, the clock, the path, the pool and the cancel were written twice and had drifted, and the parser sat on <see cref="ChatScreen"/>
/// for the Web UI's and headless's use). The front ends keep only the rendering.
/// </summary>
public static class SettingsTransferCommand
{
    /// <summary>The verb of a <c>/settings</c> line when it is <c>export</c> or <c>import</c>, with the rest of the line (the path); null otherwise. Pure.</summary>
    public static (string Verb, string Path)? Parse(string args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string trimmed = args.Trim();
        string first = SlashCommands.FirstWord(trimmed);
        if (string.Equals(first, SettingsTransferText.ExportWord, StringComparison.OrdinalIgnoreCase)
            || string.Equals(first, SettingsTransferText.ImportWord, StringComparison.OrdinalIgnoreCase))
        {
            return (first.ToLowerInvariant(), trimmed[first.Length..].Trim());
        }

        return null;
    }

    /// <summary>
    /// Runs one: an export (the pending settings flushed first, named by <paramref name="time"/>'s local time), a bare import's list of the zips in
    /// <paramref name="downloads"/>, or an import. The work runs on the pool under <paramref name="wrap"/> (the screen's spinner; headless runs it
    /// as it is). A canceled export answers null (nothing to say, as <c>/sessions export</c>); a canceled import says so. The import's work always
    /// starts and its second phase always finishes: only its first phase hears the token. A bad path is an outcome, never a throw.
    /// </summary>
    public static async Task<SettingsTransferOutcome?> RunAsync(AppSettings settings, string verb, string path, string downloads, TimeProvider time,
        ImportEnvironment environment, Func<string, Func<Task<SettingsTransferOutcome>>, Task<SettingsTransferOutcome>> wrap, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(wrap);
        string home = settings.StorageDirectory;
        if (verb == SettingsTransferText.ExportWord)
        {
            await settings.FlushAsync().ConfigureAwait(false);   // a pending debounced change goes in the zip
            var now = time.GetLocalNow();
            try
            {
                return await wrap(SettingsTransferText.ExportingSpinner,
                    () => Task.Run(() => SettingsArchive.ExportToFile(home, path, downloads, environment.AppVersion, now, cancellationToken), cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;   // the temp zip deleted; a cancel answers nothing
            }
        }

        if (path.Length == 0)
        {
            return new SettingsTransferOutcome(await Task.Run(() => SettingsArchive.ListZips(downloads), CancellationToken.None).ConfigureAwait(false), null);
        }

        if (SettingsArchive.ImportPath(path, downloads) is not { } zip)
        {
            return SettingsTransferOutcome.Failed(SettingsTransferText.BadPath(path));
        }

        try
        {
            return await wrap(SettingsTransferText.ImportingSpinner,
                () => Task.Run(() => SettingsArchive.ImportFromFile(home, zip, environment, cancellationToken), CancellationToken.None)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SettingsTransferOutcome([new(SettingsTransferText.Cancelled)], null);
        }
    }
}
