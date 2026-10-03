using NeonSidekick.UI;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>
/// The embedded model lists' filters (later on 2026-09-29, the user's ask): the buttons on the title row of Settings ›
/// Embedded models, <c>/server</c>'s LLM server pane and the startup picker — <c>8GB</c>, <c>16GB</c> and <c>32GB</c> as
/// radio buttons (one at a time; the lit one pressed again goes dark), <c>uncensored</c> on its own. A size keeps the models
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

    /// <summary>The installed and uninstalled buttons' titles (the catalog's, later on 2026-09-29; a glyph to the left of each since 2026-10-03, the user's ask, as of every filter button but the sizes). Pinned.</summary>
    public const string InstalledButton = "✓ installed";

    public const string UninstalledButton = "⤓ uninstalled";

    /// <summary>Their keys: I and U (N for not installed until later on 2026-09-30, when uncensored gave U up).</summary>
    public const char InstalledKey = 'i';

    public const char UninstalledKey = 'u';

    /// <summary>The size buttons' gigabytes, in button order. Pinned.</summary>
    public static readonly int[] Sizes = [8, 16, 32];

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

    /// <summary>The sort size button's key.</summary>
    public const char SortSizeKey = 's';

    /// <summary>The drafter button's title (later on 2026-09-30). Pinned.</summary>
    public const string DrafterButton = "⇉ drafter";

    /// <summary>The drafter button's key.</summary>
    public const char DrafterKey = 'd';

    /// <summary>The drafter button's index: after the sizes, and after installed and uninstalled <paramref name="withInstalled"/>.</summary>
    public static int DrafterIndex(bool withInstalled = false) => Sizes.Length + (withInstalled ? 2 : 0);

    /// <summary>The sort size button's index: after drafter.</summary>
    public static int SortSizeIndex(bool withInstalled = false) => DrafterIndex(withInstalled) + 1;

    /// <summary>
    /// The filters' part of <c>/server</c>'s hint row. Pinned. Shortened later on 2026-09-30 (the user's ask): the sizes are
    /// on their buttons, so <c>1 / 2 / 3 = GB</c>, and sort size is <c>S = sort</c>; uncensored, last on X, is <c>X = unc</c>.
    /// </summary>
    public const string Keys = "1 / 2 / 3 = GB · D = drafter · S = sort · X = unc";

    /// <summary>The filters' part of the catalog's hint row, with installed and uninstalled (later on 2026-09-29; <c>I / U = inst / uninst</c> later on 2026-09-30). Pinned.</summary>
    public const string CatalogKeys = "1 / 2 / 3 = GB · I / U = inst / uninst · D = drafter · S = sort · X = unc";

    /// <summary>A size button's title: <c>8GB</c>. Pinned.</summary>
    public static string SizeButton(int gb) => gb.ToString(System.Globalization.CultureInfo.InvariantCulture) + "GB";

    /// <summary>Whether any filter button is lit (sort size thins nothing, so it is not one; dark uncensored thins, but is no button lit).</summary>
    public bool Active => MaxGb is not null || Uncensored || Installed is not null || Drafter;

    /// <summary>
    /// The buttons, the lit ones <see cref="MenuButton.On"/>: the sizes on the keys 1, 2 and 3, then — <paramref name="withInstalled"/>,
    /// the catalog's — installed on I and uninstalled on U, then drafter on D, sort size on S, and uncensored on X last
    /// (later on 2026-09-30). <see cref="Press"/> reads the same layout.
    /// </summary>
    public IReadOnlyList<MenuButton> Buttons(bool withInstalled = false)
    {
        var buttons = new List<MenuButton>(Sizes.Length + 5);
        for (int i = 0; i < Sizes.Length; i++)
        {
            buttons.Add(new MenuButton(SizeButton(Sizes[i]), (char)('1' + i), MaxGb == Sizes[i]));
        }

        if (withInstalled)
        {
            buttons.Add(new MenuButton(InstalledButton, InstalledKey, Installed == true));
            buttons.Add(new MenuButton(UninstalledButton, UninstalledKey, Installed == false));
        }

        buttons.Add(new MenuButton(DrafterButton, DrafterKey, Drafter));
        buttons.Add(new MenuButton(SortSize ? SortSizeButton : SortNameButton, SortSizeKey));   // the order shown, never lit
        buttons.Add(new MenuButton(UncensoredButton, UncensoredKey, Uncensored));
        return buttons;
    }

    /// <summary>
    /// The filter after the button at <paramref name="index"/> of <see cref="Buttons"/> is pressed: a size lights alone, or goes
    /// dark when it was the lit one; installed and uninstalled the same between themselves; drafter, sort size and uncensored flip. Each
    /// group leaves the others be; any other index changes nothing.
    /// </summary>
    public EmbeddedModelFilter Press(int index, bool withInstalled = false)
    {
        if (index >= 0 && index < Sizes.Length)
        {
            return this with { MaxGb = MaxGb == Sizes[index] ? null : Sizes[index] };
        }

        if (withInstalled && (index == Sizes.Length || index == Sizes.Length + 1))
        {
            bool wanted = index == Sizes.Length;
            return this with { Installed = Installed == wanted ? null : wanted };
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
