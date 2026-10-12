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
        ShoppingReference? reference = null,
        TimeProvider? time = null)
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
                if (ShoppingCommand.Items(line.Text).Count == 0)
                {
                    return ["Error: " + ShoppingText.NoItems + ": /shopping add milk, 2 eggs"];   // the screen opens the add page instead
                }

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

            case ShoppingVerb.Rename:
            {
                // One line, or several of one product written alike (the first stands for them); several products refused.
                var (old, to) = PickRename(line, text => list.Find(text) is { Count: > 0 } found ? found.Any(i => ShoppingPlacer.Key(i.Name) == ShoppingPlacer.Key(text)) : null);
                var found = list.Find(old);
                return [found.Count == 0 ? ShoppingText.NotOnList(old)
                    : found.Select(i => ShoppingPlacer.Key(i.Name)).Distinct(StringComparer.Ordinal).Count() > 1 ? ShoppingText.Ambiguous(old, found)
                    : list.RenameItem(found[0], to)];
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

                return [list.RemoveStore(gone)];
            }

            case ShoppingVerb.RenameStore:
            {
                var (old, to) = PickRename(line, text => store.FindStore(text) is not null ? true : null);
                return [store.FindStore(old) is { } renamed ? list.RenameStore(renamed, to) : ShoppingText.NoSuchStore(old, store.Stores())];
            }

            case ShoppingVerb.Export:
            {
                // The store named, else the active one (2026-10-11); compact unless verbose.
                var shop = list.StoreFor(line.Text, out string? missing);
                if (missing is not null)
                {
                    return [missing];
                }

                var view = list.View(shop);
                int open = view.Groups.Sum(g => g.Items.Count);
                if (open == 0)
                {
                    return [ShoppingText.NothingToBuy];
                }

                // A file per export, stamped with the local time (2026-10-11); one the same second is written over.
                string name = ShoppingText.ExportFileName((time ?? TimeProvider.System).GetLocalNow());
                var written = files.WriteText(name, ShoppingText.Markdown(view, line.Verbose), overwrite: true);
                return [written.Outcome == FileOutcome.Ok
                    ? ShoppingText.Exported(Path.Combine(files.Root, written.Relative), open)
                    : FileText.Error(written.Outcome, name, "write", written.Detail)];
            }

            case ShoppingVerb.Print:
            {
                var shop = list.StoreFor(line.Text, out string? missing);
                if (missing is not null)
                {
                    return [missing];
                }

                var view = list.View(shop);
                if (view.Groups.Sum(g => g.Items.Count) == 0)
                {
                    return [ShoppingText.NothingToBuy];
                }

                if (print is null)
                {
                    return ["Error: " + ShoppingText.NoPrinting];
                }

                return (await print(ShoppingText.Markdown(view, line.Verbose), ShoppingText.Title(view.Store), cancellationToken).ConfigureAwait(false)).Split('\n');
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

    /// <summary>
    /// Which of a rename's splits (<see cref="ShoppingCommand.RenameSplits"/>) is meant: the first whose old name <paramref name="names"/>
    /// exactly (true), else the first it finds at all (false), else the parsed one (null: none found). So <c>soup to ready to eat soup</c>
    /// renames "soup", and <c>ready to eat soup to soup</c> renames "ready to eat soup" (2026-10-11 review).
    /// </summary>
    public static (string Old, string New) PickRename(ShoppingCommandLine line, Func<string, bool?> names)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(names);
        var splits = ShoppingCommand.RenameSplits(line.Text + " to " + line.To);
        var found = splits.Select(s => (Split: s, Exact: names(s.Old))).Where(x => x.Exact is not null).ToList();
        return found.FirstOrDefault(x => x.Exact == true).Split is { Old: not null } exact ? exact
            : found.Count > 0 ? found[0].Split
            : (line.Text, line.To);
    }
}
