using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Shopping;

/// <summary>
/// What a <c>/shopping</c> line does (2026-10-11), shared by the screen and headless: the answer's lines, a refusal led by
/// <c>Error: </c>. The pane is the screen's own (<see cref="ShoppingVerb.Pane"/> here prints the list, as without a pane). The two
/// acts that lose more than a line — the whole list cleared, a store removed — wait for <paramref name="confirm"/>; null (headless)
/// takes the typed line as the act, as headless <c>/youtube list --clear</c> does. <c>print</c> goes through <paramref name="print"/>
/// (the Markdown and the title; null where nothing prints).
/// </summary>
public static class ShoppingCommandRunner
{
    public static async Task<IReadOnlyList<string>> RunAsync(
        ShoppingList list,
        ShoppingCommandLine line,
        WorkingDirectory files,
        Func<string, CancellationToken, Task<bool>>? confirm,
        Func<string, string, CancellationToken, Task<string>>? print,
        CancellationToken cancellationToken,
        ShoppingReference? reference = null)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(files);
        if (line.Error is { } error)
        {
            return ["Error: " + error];
        }

        if (!list.Store.Available)
        {
            return [ShoppingText.Unavailable];
        }

        var store = list.Store;
        switch (line.Verb)
        {
            case ShoppingVerb.Pane:
            case ShoppingVerb.List:
            {
                var shop = list.StoreFor(line.Text, out string? missing);
                return missing is not null ? [missing] : ShoppingText.List(list.View(shop)).Split('\n');
            }

            case ShoppingVerb.Add:
            {
                var shop = list.StoreFor(null, out _);
                var added = new List<ShopAdded>();
                foreach (string item in ShoppingCommand.Items(line.Text))
                {
                    var (name, quantity) = ShoppingList.ParseLine(item);
                    if (list.Add(name, quantity, shop) is not { } a)
                    {
                        return ["Error: " + ShoppingText.WriteFailed];
                    }

                    added.Add(a);
                }

                // The hint is the model's; the user places a line by telling the model, or it stays unplaced.
                return ShoppingText.Added(added, shop).Split('\n').Where(l => l != ShoppingText.UnplacedHint).ToList();
            }

            case ShoppingVerb.Check:
            case ShoppingVerb.Uncheck:
            {
                bool on = line.Verb == ShoppingVerb.Check;
                return ShoppingCheckTool.Apply(list, [line.Text], item => list.Check(item, on) ? ShoppingText.Checked(item, on) : null).Split('\n');
            }

            case ShoppingVerb.Remove:
                return ShoppingCheckTool.Apply(list, [line.Text], item => list.Remove(item) ? ShoppingText.Removed(item) : null).Split('\n');

            case ShoppingVerb.Clear:
                return [list.Clear(all: false) is { } cleared ? ShoppingText.Cleared(cleared, all: false) : "Error: " + ShoppingText.WriteFailed];

            case ShoppingVerb.ClearAll:
            {
                int count = store.Items().Count;
                if (count == 0)
                {
                    return [ShoppingText.Cleared(0, all: true)];
                }

                if (confirm is not null && !await confirm(ShoppingText.ClearAllQuestion(count), cancellationToken).ConfigureAwait(false))
                {
                    return [App.ChatScreen.KeptNotice];
                }

                return [list.Clear(all: true) is { } emptied ? ShoppingText.Cleared(emptied, all: true) : "Error: " + ShoppingText.WriteFailed];
            }

            case ShoppingVerb.Stores:
            {
                var active = store.ActiveStore();
                var lines = new List<string> { ShoppingText.Stores(store.Stores(), active) };
                if (active is not null)
                {
                    lines.AddRange(ShoppingText.Layout(active, true, store.Sections(active.Id), store.Locations(active.Id), store.Placements(active.Id).Count, store.ReferenceFolder(active), list.ReferenceFiles(active)).Split('\n'));
                }

                return lines;
            }

            case ShoppingVerb.UseStore:
                return [store.FindStore(line.Text) is { } used ? list.UseStore(used) : ShoppingText.NoSuchStore(line.Text, store.Stores())];

            case ShoppingVerb.AddStore:
                return [list.AddStore(line.Text)];

            case ShoppingVerb.RemoveStore:
            {
                if (store.FindStore(line.Text) is not { } gone)
                {
                    return [ShoppingText.NoSuchStore(line.Text, store.Stores())];
                }

                if (confirm is not null && !await confirm(ShoppingText.RemoveStorePrompt(gone, store.Placements(gone.Id).Count), cancellationToken).ConfigureAwait(false))
                {
                    return [App.ChatScreen.KeptNotice];
                }

                if (!store.RemoveStore(gone.Id))
                {
                    return ["Error: " + ShoppingText.WriteFailed];
                }

                DiagnosticLog.Info(ShoppingStore.Category, ShoppingText.AuditStoreRemoved(gone));
                if (reference is not null)
                {
                    // Its pages' pictures are the index's, gone with it; the documents themselves stay.
                    WorkingDirectory.DeleteDirectoryQuietly(reference.CacheFolder(gone));
                }

                return [ShoppingText.StoreRemoved(gone)];
            }

            case ShoppingVerb.Export:
            {
                var view = list.View(list.StoreFor(null, out _));
                int open = view.Groups.Sum(g => g.Items.Count);
                if (open == 0)
                {
                    return [ShoppingText.NothingToBuy];
                }

                var written = files.WriteText(ShoppingText.ExportFileName, ShoppingText.Markdown(view), overwrite: true);
                return [written.Outcome == FileOutcome.Ok
                    ? ShoppingText.Exported(Path.Combine(files.Root, written.Relative), open)
                    : FileText.Error(written.Outcome, ShoppingText.ExportFileName, "write", written.Detail)];
            }

            case ShoppingVerb.Print:
            {
                var view = list.View(list.StoreFor(null, out _));
                if (view.Groups.Sum(g => g.Items.Count) == 0)
                {
                    return [ShoppingText.NothingToBuy];
                }

                if (print is null)
                {
                    return ["Error: " + ShoppingText.NoPrinting];
                }

                return (await print(ShoppingText.Markdown(view), ShoppingText.Title(view.Store), cancellationToken).ConfigureAwait(false)).Split('\n');
            }

            case ShoppingVerb.Reindex:
            {
                var shop = list.StoreFor(line.Text, out string? missing);
                if (missing is not null)
                {
                    return [missing];
                }

                if (shop is null)
                {
                    return [ShoppingText.NoStore];
                }

                if (reference is null)
                {
                    return ["Error: " + ShoppingText.NoBrowserForPdf];
                }

                return ShoppingText.ReferenceRead(shop, await reference.RefreshAsync(shop, force: true, cancellationToken).ConfigureAwait(false)).Split('\n');
            }

            default:
                return ["Error: " + ShoppingText.CommandUsage];
        }
    }
}
