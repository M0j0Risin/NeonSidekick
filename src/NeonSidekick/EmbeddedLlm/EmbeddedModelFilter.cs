using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>Which size the embedded model filters' 8GB / 16GB / 32GB buttons measure (<see cref="EmbeddedFilterTypes"/>).</summary>
public enum EmbeddedFilterType
{
    /// <summary>The size the row shows: the weights, the vision projector and the drafter (<see cref="EmbeddedModelCatalog.TotalBytes"/>).</summary>
    File,

    /// <summary>The weights' GGUF alone (<see cref="EmbeddedModel.Model"/>).</summary>
    Gguf,
}

/// <summary>
/// The setting <c>Embedded filter type</c> (later on 2026-09-29, the user's ask and names): <c>file</c> — the default, "for
/// now" — measures a model by the size its row shows, <c>gguf</c> by its weights alone; the <see cref="App.PerfBarMode"/> shape.
/// <see cref="Resolve"/> is the one place the saved word becomes the enum: a hand-edited value that is neither falls back to
/// <see cref="Default"/> with a warning, once per value.
/// </summary>
public static class EmbeddedFilterTypes
{
    /// <summary>The user's pick. Pinned.</summary>
    public const string Default = "file";

    /// <summary>The types in menu order.</summary>
    public static readonly string[] Names = ["file", "gguf"];

    private const string Category = "EmbeddedLlm";

    /// <summary>Trims and ignores case; false (and <see cref="EmbeddedFilterType.File"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out EmbeddedFilterType type)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "file": type = EmbeddedFilterType.File; return true;
            case "gguf": type = EmbeddedFilterType.Gguf; return true;
            default: type = EmbeddedFilterType.File; return false;
        }
    }

    /// <summary>The menu hint next to a type. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "file" => "the size the row shows: weights, vision projector and drafter",
        "gguf" => "the weights' GGUF alone",
        _ => "",
    };

    // The last unknown value warned about: every pane visit asks, the log hears once per value.
    private static string? _warned;

    /// <summary>The type in force for <paramref name="effective"/>; an unknown saved value warns once and uses <see cref="Default"/>.</summary>
    public static EmbeddedFilterType Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.EmbeddedFilterType, out var type))
        {
            return type;
        }

        if (!string.Equals(Interlocked.Exchange(ref _warned, effective.EmbeddedFilterType), effective.EmbeddedFilterType, StringComparison.Ordinal))
        {
            DiagnosticLog.Warn(Category,
                $"{nameof(AppSettingsData.EmbeddedFilterType)}='{effective.EmbeddedFilterType}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        }

        return EmbeddedFilterType.File;
    }
}

/// <summary>
/// The embedded model lists' filters (later on 2026-09-29, the user's ask): the buttons on the title row of Settings ›
/// Embedded models, <c>/server</c>'s LLM server pane and the startup picker — <c>8GB</c>, <c>16GB</c> and <c>32GB</c> as
/// radio buttons (one at a time; the lit one pressed again goes dark), <c>uncensored</c> on its own. A size keeps the models
/// at most that big (<see cref="EmbeddedFilterTypes"/> says which bytes), uncensored the <see cref="EmbeddedModel.Uncensored"/>
/// ones; both together, both. Nothing is saved: every visit to a pane starts at <see cref="None"/>, every model shown.
/// The catalog alone (later still on 2026-09-29, the user's ask) carries <c>installed</c> and <c>uninstalled</c> between the
/// sizes and uncensored, a radio pair of their own (<see cref="Installed"/>; <c>/server</c> lists installed models only).
/// Pure: the tests drive it.
/// </summary>
public sealed record EmbeddedModelFilter(int? MaxGb, bool Uncensored, bool? Installed = null)
{
    /// <summary>No button lit: every model.</summary>
    public static readonly EmbeddedModelFilter None = new(null, false);

    /// <summary>The installed and uninstalled buttons' titles (the catalog's, later on 2026-09-29). Pinned.</summary>
    public const string InstalledButton = "installed";

    public const string UninstalledButton = "uninstalled";

    /// <summary>Their keys: I, and N for not installed.</summary>
    public const char InstalledKey = 'i';

    public const char UninstalledKey = 'n';

    /// <summary>The size buttons' gigabytes, in button order. Pinned.</summary>
    public static readonly int[] Sizes = [8, 16, 32];

    /// <summary>The uncensored button's title. Pinned.</summary>
    public const string UncensoredButton = "uncensored";

    /// <summary>The uncensored button's key.</summary>
    public const char UncensoredKey = 'u';

    /// <summary>The uncensored button's index: after the sizes, and after installed and uninstalled <paramref name="withInstalled"/>.</summary>
    public static int UncensoredIndex(bool withInstalled = false) => Sizes.Length + (withInstalled ? 2 : 0);

    /// <summary>The filters' part of <c>/server</c>'s hint row. Pinned.</summary>
    public const string Keys = "1 / 2 / 3 = 8 / 16 / 32 GB · U = uncensored";

    /// <summary>The filters' part of the catalog's hint row, with installed and uninstalled (later on 2026-09-29). Pinned.</summary>
    public const string CatalogKeys = "1 / 2 / 3 = 8 / 16 / 32 GB · I / N = installed / uninstalled · U = uncensored";

    /// <summary>A size button's title: <c>8GB</c>. Pinned.</summary>
    public static string SizeButton(int gb) => gb.ToString(System.Globalization.CultureInfo.InvariantCulture) + "GB";

    /// <summary>Whether any button is lit.</summary>
    public bool Active => MaxGb is not null || Uncensored || Installed is not null;

    /// <summary>
    /// The buttons, the lit ones <see cref="MenuButton.On"/>: the sizes on the keys 1, 2 and 3, then — <paramref name="withInstalled"/>,
    /// the catalog's — installed on I and uninstalled on N, then uncensored on U. <see cref="Press"/> reads the same layout.
    /// </summary>
    public IReadOnlyList<MenuButton> Buttons(bool withInstalled = false)
    {
        var buttons = new List<MenuButton>(Sizes.Length + 3);
        for (int i = 0; i < Sizes.Length; i++)
        {
            buttons.Add(new MenuButton(SizeButton(Sizes[i]), (char)('1' + i), MaxGb == Sizes[i]));
        }

        if (withInstalled)
        {
            buttons.Add(new MenuButton(InstalledButton, InstalledKey, Installed == true));
            buttons.Add(new MenuButton(UninstalledButton, UninstalledKey, Installed == false));
        }

        buttons.Add(new MenuButton(UncensoredButton, UncensoredKey, Uncensored));
        return buttons;
    }

    /// <summary>
    /// The filter after the button at <paramref name="index"/> of <see cref="Buttons"/> is pressed: a size lights alone, or goes
    /// dark when it was the lit one; installed and uninstalled the same between themselves; uncensored flips. Each group leaves
    /// the others be; any other index changes nothing.
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

        return index == UncensoredIndex(withInstalled) ? this with { Uncensored = !Uncensored } : this;
    }

    /// <summary>
    /// Whether <paramref name="model"/> passes. The size is compared as a row shows it — gigabytes of 10⁹ to one decimal, as
    /// <c>ModelStore.SizeLabel</c> writes them — so a row that reads "8 GB" passes 8GB. <paramref name="installed"/> is the
    /// model's state on disk (a paused download is not installed); <c>/server</c>'s rows are all installed.
    /// </summary>
    public bool Matches(EmbeddedModel model, EmbeddedFilterType type, bool installed = true)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (Uncensored && !model.Uncensored)
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

        long bytes = type == EmbeddedFilterType.Gguf ? model.Model.Bytes : EmbeddedModelCatalog.TotalBytes(model);
        return Math.Round(bytes / 1_000_000_000.0, 1, MidpointRounding.AwayFromZero) <= max;
    }
}
