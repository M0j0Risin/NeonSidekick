using NeonSidekick.UI;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>
/// The embedded model lists' filters (later on 2026-09-29, the user's ask): the buttons on the title row of Settings ›
/// Embedded models, <c>/server</c>'s LLM server pane and the startup picker — <c>8GB</c> and <c>16GB</c> as radio
/// buttons (<c>32GB</c> too until 2026-10-05, when the user took it out: every model in the catalog is under 32 GB, so it
/// thinned nothing) (one at a time; the lit one pressed again goes dark), <c>uncensored</c> on its own. A size keeps the models
/// at most that big by the size their row shows (<see cref="Bytes"/>; the weights alone were a choice, the setting
/// <c>Embedded filter type</c>, from later on 2026-09-29 until 2026-10-02, the user's call), uncensored the <see cref="EmbeddedModel.Uncensored"/>
/// ones; both together, both. Nothing is saved: every visit to a pane starts at <see cref="None"/> (or <see cref="For"/>).
/// The catalog alone (later still on 2026-09-29, the user's ask) carries <c>installed</c> and <c>uninstalled</c> between the
/// sizes and uncensored, a radio pair of their own (<see cref="Installed"/>; <c>/server</c> lists installed models only).
/// <c>sort size</c> (2026-09-30, the user's ask) is last on every list and no filter: pressed, <see cref="Arrange"/> orders the
/// embedded rows smallest first by the same bytes the sizes measure; pressed again, they keep the catalog's order, which is by
/// name. Not saved either. Later that day (the user's ask) it named the order a press gives — <c>sort size</c> in name
/// order, <c>sort name</c> sorted by size; since 2026-10-02 (the user's ask) it names the order shown — <c>sort (name)</c> in
/// the catalog's order, <c>sort (size)</c> sorted by size — and is still never lit: the title carries the state.
/// <c>drafter</c> (later on 2026-09-30, the user's ask) sits between uncensored and sort size on every list, a switch of its
/// own like uncensored: lit, it keeps the models that can draft ahead (<see cref="EmbeddedModel.HasMtp"/>: a drafter file, MTP
/// or DFlash, or a head built into the weights — README's Drafter column, <c>drafter</c> or <c>built in</c>).
/// <para>Uncensored flipped later still on 2026-09-30 (the user's ask): the uncensored builds are left out by default, so a
/// visit starts with the normal ones alone; lit, the list is the uncensored builds alone, dark the normal ones alone — two
/// halves, never both. It moved last, after sort size, on the key X; uninstalled took U, and N went. A pane whose model in
/// use is uncensored starts with it lit (<see cref="For"/>, the user's pick), so that row shows.</para>
/// <para>Two cycling buttons since later on 2026-10-13 (the user's call, <c>/queue</c>'s shape): the sizes are one button,
/// <c>size: any|8GB|16GB</c> on S (<see cref="NextSize"/>), and the catalog's installed and uninstalled one more,
/// <c>show: all|installed|uninstalled</c> on I (<see cref="NextInstalled"/>), each lit while it thins the list; sort moved to O.</para>
/// Pure: the tests drive it.
/// </summary>
public sealed record EmbeddedModelFilter(int? MaxGb, bool Uncensored, bool? Installed = null, bool SortSize = false, bool Drafter = false)
{
    /// <summary>No button lit: every normal model, the uncensored builds left out (later on 2026-09-30).</summary>
    public static readonly EmbeddedModelFilter None = new(null, false);

    /// <summary>
    /// A visit's first filter (later on 2026-09-30, the user's pick): <see cref="None"/>, with uncensored lit when
    /// <paramref name="inUse"/> — the model the pane opens on — is an uncensored build, so its row is shown.
    /// </summary>
    public static EmbeddedModelFilter For(EmbeddedModel? inUse) => inUse is { Uncensored: true } ? None with { Uncensored = true } : None;

    /// <summary>
    /// The catalog's show button (later on 2026-10-13; the installed and uninstalled radio pair from later on 2026-09-29): what it keeps,
    /// <c>show: all</c>, <c>show: installed</c> or <c>show: uninstalled</c>. Pinned.
    /// </summary>
    public static string ShowButton(bool? installed) => "show: " + installed switch
    {
        true => "installed",
        false => "uninstalled",
        _ => "all",
    };

    /// <summary>The show button's key: I (installed and uninstalled were I and U until later on 2026-10-13).</summary>
    public const char ShowKey = 'i';

    /// <summary>What the show button keeps after <paramref name="installed"/>: all, installed, uninstalled, all again. Pure.</summary>
    public static bool? NextInstalled(bool? installed) => installed switch
    {
        null => true,
        true => false,
        false => null,
    };

    /// <summary>The sizes' gigabytes, in the size button's order after <c>any</c> (32 went on 2026-10-05: every catalog model is under it). Pinned.</summary>
    public static readonly int[] Sizes = [8, 16];

    /// <summary>The size button (later on 2026-10-13; one button per size from later on 2026-09-29): <c>size: any</c>, <c>size: 8GB</c>, <c>size: 16GB</c>. Pinned.</summary>
    public static string SizeTitle(int? maxGb) => "size: " + (maxGb is { } gb ? SizeButton(gb) : "any");

    /// <summary>The size button's key: S (the sizes were 1 and 2, and S sort, until later on 2026-10-13).</summary>
    public const char SizeKey = 's';

    /// <summary>The size after <paramref name="maxGb"/>: any, then each of <see cref="Sizes"/>, then any again; a size not among them goes to any. Pure.</summary>
    public static int? NextSize(int? maxGb)
    {
        if (maxGb is not { } gb)
        {
            return Sizes[0];
        }

        int at = Array.IndexOf(Sizes, gb);
        return at < 0 || at + 1 >= Sizes.Length ? null : Sizes[at + 1];
    }

    /// <summary>The uncensored button's title. Pinned.</summary>
    public const string UncensoredButton = "◌ uncensored";

    /// <summary>The uncensored button's key: X (U until later on 2026-09-30).</summary>
    public const char UncensoredKey = 'x';

    /// <summary>The uncensored button's index: after sort size, last (later on 2026-09-30).</summary>
    public static int UncensoredIndex(bool withInstalled = false) => SortSizeIndex(withInstalled) + 1;

    /// <summary>
    /// The sort button's title while sorted by size: the order shown (2026-10-02, the user's ask; <c>sort size</c>, then the
    /// name-order title, until then). Pinned.
    /// </summary>
    public const string SortSizeButton = "⇅ sort (size)";

    /// <summary>The sort button's title in the catalog's (name) order: the order shown (2026-10-02, the user's ask). Pinned.</summary>
    public const string SortNameButton = "⇅ sort (name)";

    /// <summary>The sort button's key: O (S until later on 2026-10-13, when the size button took it).</summary>
    public const char SortSizeKey = 'o';

    /// <summary>The drafter button's title (later on 2026-09-30). Pinned.</summary>
    public const string DrafterButton = "⇉ drafter";

    /// <summary>The drafter button's key.</summary>
    public const char DrafterKey = 'd';

    /// <summary>The size button's index: first.</summary>
    public const int SizeIndex = 0;

    /// <summary>The catalog's show button's index: after the size.</summary>
    public const int ShowIndex = 1;

    /// <summary>The drafter button's index: after the size, and after the show button <paramref name="withInstalled"/>.</summary>
    public static int DrafterIndex(bool withInstalled = false) => withInstalled ? ShowIndex + 1 : SizeIndex + 1;

    /// <summary>The sort size button's index: after drafter.</summary>
    public static int SortSizeIndex(bool withInstalled = false) => DrafterIndex(withInstalled) + 1;

    /// <summary>
    /// The filters' part of <c>/server</c>'s hint row. Pinned. Shortened later on 2026-09-30 (the user's ask): <c>1 / 2 = GB</c> for the
    /// size buttons, <c>s = sort</c>, <c>x = unc</c>; since later on 2026-10-13 the one size button is <c>s = size</c> and sort <c>o = sort</c>.
    /// </summary>
    public const string Keys = "s = size · d = drafter · o = sort · x = unc";

    /// <summary>The filters' part of the catalog's hint row, with the show button (later on 2026-09-29 as <c>i / u = inst / uninst</c>; <c>i = show</c> since later on 2026-10-13). Pinned.</summary>
    public const string CatalogKeys = "s = size · i = show · d = drafter · o = sort · x = unc";

    /// <summary>A size button's title: <c>8GB</c>. Pinned.</summary>
    public static string SizeButton(int gb) => gb.ToString(System.Globalization.CultureInfo.InvariantCulture) + "GB";

    /// <summary>Whether any filter button is lit (sort size thins nothing, so it is not one; dark uncensored thins, but is no button lit).</summary>
    public bool Active => MaxGb is not null || Uncensored || Installed is not null || Drafter;

    /// <summary>
    /// The buttons, the lit ones <see cref="MenuButton.On"/>: the size on S, then — <paramref name="withInstalled"/>, the catalog's — the
    /// show button on I, each lit while it thins the list (later on 2026-10-13), then drafter on D, sort on O, and uncensored on X last
    /// (later on 2026-09-30). <see cref="Press"/> reads the same layout.
    /// </summary>
    public IReadOnlyList<MenuButton> Buttons(bool withInstalled = false)
    {
        var buttons = new List<MenuButton>(5)
        {
            new(SizeTitle(MaxGb), SizeKey, MaxGb is not null),
        };

        if (withInstalled)
        {
            buttons.Add(new MenuButton(ShowButton(Installed), ShowKey, Installed is not null));
        }

        buttons.Add(new MenuButton(DrafterButton, DrafterKey, Drafter));
        buttons.Add(new MenuButton(SortSize ? SortSizeButton : SortNameButton, SortSizeKey));   // the order shown, never lit
        buttons.Add(new MenuButton(UncensoredButton, UncensoredKey, Uncensored));
        return buttons;
    }

    /// <summary>
    /// The filter after the button at <paramref name="index"/> of <see cref="Buttons"/> is pressed: the size steps to the next
    /// (<see cref="NextSize"/>), the show button likewise (<see cref="NextInstalled"/>); drafter, sort size and uncensored flip. Each
    /// leaves the others be; any other index changes nothing.
    /// </summary>
    public EmbeddedModelFilter Press(int index, bool withInstalled = false)
    {
        if (index == SizeIndex)
        {
            return this with { MaxGb = NextSize(MaxGb) };
        }

        if (withInstalled && index == ShowIndex)
        {
            return this with { Installed = NextInstalled(Installed) };
        }

        if (index == DrafterIndex(withInstalled))
        {
            return this with { Drafter = !Drafter };
        }

        if (index == SortSizeIndex(withInstalled))
        {
            return this with { SortSize = !SortSize };
        }

        return index == UncensoredIndex(withInstalled) ? this with { Uncensored = !Uncensored } : this;
    }

    /// <summary>
    /// Whether <paramref name="model"/> passes. The size is compared as a row shows it — gigabytes of 10⁹ to one decimal, as
    /// <c>ModelStore.SizeLabel</c> writes them — so a row that reads "8 GB" passes 8GB. <paramref name="installed"/> is the
    /// model's state on disk (a paused download is not installed); <c>/server</c>'s rows are all installed. Uncensored picks a
    /// half (later on 2026-09-30): lit, the uncensored builds alone; dark, the others alone.
    /// </summary>
    public bool Matches(EmbeddedModel model, bool installed = true)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (Uncensored != model.Uncensored)
        {
            return false;
        }

        if (Drafter && !model.HasMtp)
        {
            return false;
        }

        if (Installed is { } wanted && wanted != installed)
        {
            return false;
        }

        if (MaxGb is not { } max)
        {
            return true;
        }

        return Math.Round(Bytes(model) / 1_000_000_000.0, 1, MidpointRounding.AwayFromZero) <= max;
    }

    /// <summary>
    /// The bytes the sizes and sort size measure <paramref name="model"/> by: the size its row shows — the weights, the vision
    /// projector and the drafter (<see cref="EmbeddedModelCatalog.TotalBytes"/>; the only way since 2026-10-02).
    /// </summary>
    public static long Bytes(EmbeddedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return EmbeddedModelCatalog.TotalBytes(model);
    }

    /// <summary>
    /// The rows in the order they are shown (2026-09-30): <paramref name="shown"/> as it is while sort size is dark; lit, the
    /// entries <paramref name="modelAt"/> names a model for are re-ordered among their own slots, smallest first by
    /// <see cref="Bytes"/> and the list's order between equals, and every other entry (a server on the network, the Claude
    /// API) keeps its place.
    /// </summary>
    public List<int> Arrange(List<int> shown, Func<int, EmbeddedModel?> modelAt)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(modelAt);
        if (!SortSize)
        {
            return shown;
        }

        var slots = new List<int>();
        var models = new List<(int Entry, long Bytes)>();
        for (int position = 0; position < shown.Count; position++)
        {
            if (modelAt(shown[position]) is { } model)
            {
                slots.Add(position);
                models.Add((shown[position], Bytes(model)));
            }
        }

        // OrderBy is stable: equal sizes keep the list's order.
        var sorted = models.OrderBy(m => m.Bytes).ToList();
        var arranged = shown.ToList();
        for (int i = 0; i < slots.Count; i++)
        {
            arranged[slots[i]] = sorted[i].Entry;
        }

        return arranged;
    }
}
