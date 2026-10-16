namespace NeonSidekick.ActiveDirectory;

/// <summary>
/// What a row of a card opens (2026-10-15, the AD gaps plan, the user's yes to "should a double-click on a row open that object's card?"): the
/// object's DN, and its name for one use only — <see cref="DirectoryText.Gone"/>'s sentence when the object has gone since. Not
/// <see cref="DirectoryTarget"/>, the server and sign-in a session binds to.
/// </summary>
public sealed record DirectoryRowTarget(string Dn, string Name);

/// <summary>
/// A table's column (2026-10-15, the AD gaps plan): its name (the card's header and the CSV's, <see cref="DirectoryText"/>'s constants), whether
/// the card may cut it to fit the pane (a name or a description; never an account name or a date), and whether it holds dates (the CSV's header
/// names the zone and its cells are dates or nothing).
/// </summary>
public sealed record DirectoryColumn(string Name, bool Flexible = false, bool Date = false);

/// <summary>
/// One cell: its raw text (never fenced: the card cleans it, the CSV scrubs it) or a moment (a date column's), null for no value — the card's
/// <c>–</c>, the CSV's empty cell; <paramref name="Indent"/> cells of indent on the card alone (the OU tree's depth).
/// </summary>
public sealed record DirectoryCell(string? Text, DateTimeOffset? When = null, int Indent = 0)
{
    public static readonly DirectoryCell None = new((string?)null);

    public static implicit operator DirectoryCell(string? text) => new(text);

    public static DirectoryCell At(DateTimeOffset? when) => new(null, when);
}

/// <summary>
/// One row in its two forms (2026-10-15, the AD gaps plan, §6): the model's line, built as the answers' text always was (its cells carrying words:
/// <c>last 2026-10-14 10:00</c>), and the cells for the card and the CSV, where those words are the columns' names; one query builds both from
/// the same entry. <see cref="Dn"/> is the CSV's last column; <see cref="Opens"/> says a double-click on the row opens its card.
/// </summary>
public sealed record DirectoryRow(string ModelLine, IReadOnlyList<DirectoryCell?> Cells, string? Dn = null, string? Name = null, bool Opens = false)
{
    public DirectoryRowTarget? Target => Opens && Dn is { Length: > 0 } dn ? new DirectoryRowTarget(dn, Name ?? AdValues.FirstName(dn)) : null;
}

/// <summary>
/// A table of an answer (2026-10-15, the AD gaps plan): a list (a group's members, a find's results, an OU's contents — what <c>to</c> saves) or a
/// card's link list (a user's groups, its direct reports, a group's or computer's <c>member of</c>: the model reads them as one comma row, the
/// card as rows that open). Every row read is kept (<see cref="Rows"/>, the CSV's); <see cref="Shown"/> is how many the model and the card show.
/// </summary>
public sealed class DirectoryTable(IReadOnlyList<DirectoryColumn> columns, bool list = true)
{
    public IReadOnlyList<DirectoryColumn> Columns { get; } = columns ?? throw new ArgumentNullException(nameof(columns));

    public bool IsList { get; } = list;

    public List<DirectoryRow> Rows { get; } = [];

    /// <summary>The rows the model and the card show; every row by default.</summary>
    public int Shown { get; set; } = int.MaxValue;

    /// <summary>The card's title above the table (<c>never signed in (3)</c>); null for none.</summary>
    public string? Title { get; set; }

    /// <summary>The model's text heads the table with <see cref="Title"/> and a colon, as the stale kinds' "never signed in (3):" always did.</summary>
    public bool TitleInModel { get; set; }

    /// <summary>The CSV's <c>section</c> for this table's rows when an answer has two list tables (the title without its count).</summary>
    public string? Section { get; set; }

    /// <summary>The model's text stops adding this table's rows once it is this close to <see cref="DirectoryQueries.MaxChars"/>.</summary>
    public int CutReserve { get; set; } = 800;

    /// <summary>The model's line where it stopped, told how many rows it left out; null to stop without a word.</summary>
    public Func<int, string>? CutNote { get; set; }

    public IEnumerable<DirectoryRow> ShownRows => Rows.Take(Math.Max(0, Shown));
}

/// <summary>A card's row: <c>label: value</c>, the value in the model's form (fenced); <see cref="Links"/> a link table the card shows instead.</summary>
public sealed record DirectoryField(string Label, string Value, DirectoryRowTarget? Target = null, DirectoryTable? Links = null);

/// <summary>
/// One directory answer, built once and drawn three ways (2026-10-15, the AD gaps plan, §6, the user's ask: "line results up in columns a little
/// cleaner"): <see cref="DirectoryRender.ForModel"/> the tools' text as it always read, <see cref="DirectoryRender.ForCard"/> <c>/ad</c>'s card in
/// columns, <see cref="DirectoryCsv"/> the <c>to</c> save. A head line, key/value rows, tables and notes; or a refusal, which for a name several
/// objects share holds the candidates as a table the card offers to pick from.
/// </summary>
public sealed class DirectoryAnswer
{
    /// <summary>The head line in the model's form (fenced).</summary>
    public string Head { get; set; } = "";

    public List<DirectoryField> Fields { get; } = [];

    public List<DirectoryTable> Tables { get; } = [];

    /// <summary>Lines after the tables (the model's and the card's, dim there).</summary>
    public List<string> Notes { get; } = [];

    /// <summary>The refusal the model reads (<c>Error: …</c>); null for an answer.</summary>
    public string? Error { get; set; }

    /// <summary>With <see cref="Error"/>: the objects a name matches, as a list the card offers (each row opens its own).</summary>
    public DirectoryTable? Candidates { get; set; }

    /// <summary>The card's title for <see cref="Candidates"/> (<c>'john' matches 3 accounts</c>).</summary>
    public string? CandidatesTitle { get; set; }

    /// <summary>What the card shows (a user, a group, an OU…), by DN: the back-trail's entry and a group card's buttons read it again by it.</summary>
    public DirectoryRowTarget? Self { get; set; }

    /// <summary>What kind of card: <c>user</c>, <c>group</c>, <c>computer</c>, <c>ou</c>, <c>domain</c>, <c>find</c>.</summary>
    public string Kind { get; set; } = "find";

    /// <summary>The line naming a <c>to</c> save, after everything (the transcript's note finds it by <see cref="DirectoryText.SavedPrefix"/>).</summary>
    public string? Saved { get; set; }

    /// <summary>The zone the times are in (the CSV names it in its date columns' headers).</summary>
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;

    public DateTimeOffset Now { get; set; }

    /// <summary>Whether the user card shows a lockout (its <c>locked</c> row): <c>/ad</c>'s "Search 4740" reads it.</summary>
    public bool Locked => Fields.Any(f => f.Label == DirectoryText.LockedLabel);

    public bool Failed => Error is not null;

    /// <summary>A cap or the time limit cut what was read: a save of it says it is partial.</summary>
    public bool Partial { get; set; }

    public static DirectoryAnswer Refused(string error) => new() { Error = error };
}
