using System.Globalization;
using Microsoft.Data.Sqlite;
using NeonSidekick.Diagnostics;
using NeonSidekick.Sessions;

namespace NeonSidekick.Shopping;

/// <summary>A store the list can be walked in (2026-10-11): its name, the folder under <c>reference</c> its documents live in, and notes.</summary>
public sealed record ShopStore(long Id, string Name, string Folder, string Notes);

/// <summary>A store's department (2026-10-11): Produce, Dairy &amp; Eggs, Pharmacy.</summary>
public sealed record ShopSection(long Id, long StoreId, string Name);

/// <summary>
/// A place in a store an item can be found (2026-10-11): an aisle (<c>Aisle 2</c>) or a named zone (<c>Dairy</c>, <c>Pharmacy Wall</c>),
/// in the section it belongs to (null for none) and its place on the walk through the store (null: after the numbered ones, by label).
/// </summary>
public sealed record ShopLocation(long Id, long StoreId, string Label, long? SectionId, string? SectionName, int? Walk);

/// <summary>
/// One layout fact of a store (2026-10-11; the sample database's <c>aisle_items</c>, generalised): a term — a product, a brand or a
/// category (<see cref="ShoppingKinds"/>) — and where it is: a location, or a section alone. <see cref="Source"/> says how it was learnt
/// (<see cref="ShoppingSources"/>): a guess is said to be one whenever it places an item.
/// </summary>
public sealed record ShopPlacement(long Id, long StoreId, string Term, string Kind, long? LocationId, long? SectionId, string Source, string Note);

/// <summary>A line bought before (2026-10-11, the recently bought): its last name and quantity, how many times it was bought and when last.</summary>
public sealed record ShopBought(long Id, string Name, int Quantity, int Times, DateTimeOffset Last);

/// <summary>A line of the list (2026-10-11): store-independent, placed per store when it is shown (<see cref="ShoppingPlacer"/>).</summary>
public sealed record ShopItem(long Id, string Name, int Quantity, string Note, bool Checked, DateTimeOffset Created);

/// <summary>
/// One reference document of a store as it was last read (2026-10-11): its path in the store's reference folder, the size and time it
/// had then (a change reads it again), the pages read of how many, and what stopped the read short (empty when nothing did).
/// </summary>
public sealed record ShopRefDoc(long Id, long StoreId, string File, long Size, long Modified, int Pages, int PageCount, string Detail);

/// <summary>A page of a reference document (2026-10-11): its number, its text, and the picture of it (a full path; null for none).</summary>
public sealed record ShopRefPage(long Id, long DocId, string File, int Page, string Text, string? Image);

/// <summary>The words of <c>placements.kind</c> (2026-10-11): what a term names. A brand outranks the rest (<see cref="ShoppingPlacer"/>).</summary>
public static class ShoppingKinds
{
    public const string Item = "item";
    public const string Brand = "brand";
    public const string Category = "category";

    public static readonly IReadOnlyList<string> All = [Item, Brand, Category];

    /// <summary>The kind <paramref name="word"/> names (any case, trimmed); null for any other word.</summary>
    public static string? Parse(string? word) => All.FirstOrDefault(k => string.Equals(k, word?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>The words of <c>placements.source</c> (2026-10-11): how a layout fact was learnt.</summary>
public static class ShoppingSources
{
    /// <summary>The user said so, or the model passed on what the user said.</summary>
    public const string User = "user";

    /// <summary>Read in one of the store's reference documents.</summary>
    public const string Reference = "reference";

    /// <summary>The model's best guess: shown as one until the user confirms or corrects it.</summary>
    public const string Guess = "guess";

    public static readonly IReadOnlyList<string> All = [User, Reference, Guess];
}

/// <summary>
/// <c>&lt;profile&gt;\db\shopping\shopping.db</c> (2026-10-11, the user's ask: the shopping list a feature of its own, in place of a
/// hand-built SQLite file the generic SQLite tools and a skill drove): one list of items, and any number of stores, each with its
/// sections, locations (aisles and named zones, in the order the store is walked) and placements — the layout facts that put an item
/// at a location. An item is store-independent; where it is at a store is worked out when the list is shown
/// (<see cref="ShoppingPlacer"/>), so a fact learnt later moves every line it covers with no row to rewrite. The active store is a
/// <c>meta</c> row, so it lives with the list in the profile and needs no setting.
/// <para>The shape is <see cref="SessionStore"/>'s and <see cref="Skills.SkillRecordStore"/>'s: one connection opened lazily under one
/// lock, WAL, foreign keys on, a <c>meta</c> schema number a file must match or the store stays off (one Warning), every statement's
/// failure logged and swallowed. Pure SQL: what the tools say is <see cref="ShoppingList"/>'s and <see cref="ShoppingText"/>'s.</para>
/// </summary>
public sealed class ShoppingStore : IDisposable
{
    public const string FileName = "shopping.db";

    /// <summary>The folder under the profile that holds <see cref="FileName"/>, its WAL siblings and the stores' <see cref="ReferenceFolderName"/>. Pinned.</summary>
    public static readonly string FolderName = Path.Combine("db", "shopping");

    /// <summary>The folder beside the file holding one folder per store (<see cref="ShopStore.Folder"/>) of reference documents. Pinned.</summary>
    public const string ReferenceFolderName = "reference";

    /// <summary>The log category of the store and the shopping tools' audit lines.</summary>
    public const string Category = "Shopping";

    /// <summary>The store's schema number in <c>meta</c>; a file at any other is refused (<see cref="SchemaMismatchWarning"/>).</summary>
    public const int SchemaVersion = 1;

    /// <summary>The <c>meta</c> key holding the active store's id.</summary>
    private const string ActiveStoreKey = "active_store";

    /// <summary>Logged (Warn) when the file's stored schema is not <see cref="SchemaVersion"/>: the list is off, the file untouched. Pinned.</summary>
    public static string SchemaMismatchWarning(long stored) =>
        $"{FileName} is at schema {stored.ToString(CultureInfo.InvariantCulture)}, this version reads {SchemaVersion.ToString(CultureInfo.InvariantCulture)}; the shopping list is off until it is moved away.";

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly TimeProvider _time;
    private SqliteConnection? _connection;
    private bool _failed;
    private bool _disposed;

    /// <param name="profileDirectory">The profile's folder; the file is <see cref="FileName"/> in its <see cref="FolderName"/>, made at the first open.</param>
    public ShoppingStore(string profileDirectory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        Folder = Path.Combine(Path.GetFullPath(profileDirectory), FolderName);
        _filePath = Path.Combine(Folder, FileName);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The folder holding the file: <c>&lt;profile&gt;\db\shopping</c>.</summary>
    public string Folder { get; }

    public string FilePath => _filePath;

    /// <summary>The folder holding every store's reference folder: <c>&lt;profile&gt;\db\shopping\reference</c>.</summary>
    public string ReferenceRoot => Path.Combine(Folder, ReferenceFolderName);

    /// <summary>The reference folder of <paramref name="store"/>.</summary>
    public string ReferenceFolder(ShopStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Path.Combine(ReferenceRoot, store.Folder);
    }

    /// <summary>False once the file failed to open or carries another schema: every read is empty and every write ignored from then on.</summary>
    public bool Available
    {
        get
        {
            lock (_gate)
            {
                return Open() is not null;
            }
        }
    }

    // ---- stores ----

    /// <summary>Every store, by name.</summary>
    public IReadOnlyList<ShopStore> Stores() =>
        Query("SELECT id, name, folder, notes FROM stores ORDER BY name COLLATE NOCASE", null, ReadStore, "read the stores");

    /// <summary>The store named <paramref name="name"/> (or whose folder it is), any case; null for none.</summary>
    public ShopStore? FindStore(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string wanted = name.Trim();
        return Query("SELECT id, name, folder, notes FROM stores WHERE name = $name COLLATE NOCASE OR folder = $name COLLATE NOCASE ORDER BY id LIMIT 1",
            c => c.Parameters.AddWithValue("$name", wanted), ReadStore, "find a store").FirstOrDefault();
    }

    /// <summary>The active store: the one <see cref="SetActiveStore"/> chose while it still exists, else the first made; null with none.</summary>
    public ShopStore? ActiveStore()
    {
        var stores = Query("SELECT s.id, s.name, s.folder, s.notes FROM stores s LEFT JOIN meta m ON m.key = $key AND m.value = s.id ORDER BY m.value IS NULL, s.id LIMIT 1",
            c => c.Parameters.AddWithValue("$key", ActiveStoreKey), ReadStore, "read the active store");
        return stores.FirstOrDefault();
    }

    /// <summary>Makes <paramref name="storeId"/> the active store. False when the write failed.</summary>
    public bool SetActiveStore(long storeId) =>
        Execute("INSERT INTO meta(key, value) VALUES ($key, $id) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            c => { c.Parameters.AddWithValue("$key", ActiveStoreKey); c.Parameters.AddWithValue("$id", storeId); }, "set the active store") is not null;

    /// <summary>
    /// A new store named <paramref name="name"/>, its reference folder <paramref name="folder"/> (made unique with a number when taken);
    /// null when a store of that name exists or the write failed.
    /// </summary>
    public ShopStore? AddStore(string name, string folder, string notes = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (FindStore(name) is not null)
        {
            return null;
        }

        string unique = folder;
        for (int n = 2; Query("SELECT id FROM stores WHERE folder = $f COLLATE NOCASE", c => c.Parameters.AddWithValue("$f", unique), r => r.GetInt64(0), "check a folder").Count > 0; n++)
        {
            unique = folder + "-" + n.ToString(CultureInfo.InvariantCulture);
        }

        long? id = Insert("INSERT INTO stores(name, folder, notes, created_at) VALUES ($name, $folder, $notes, $at)",
            c =>
            {
                c.Parameters.AddWithValue("$name", name.Trim());
                c.Parameters.AddWithValue("$folder", unique);
                c.Parameters.AddWithValue("$notes", notes);
                c.Parameters.AddWithValue("$at", Now());
            }, "add a store");
        return id is { } made ? new ShopStore(made, name.Trim(), unique, notes) : null;
    }

    /// <summary>
    /// Renames a store and, with <paramref name="folder"/>, moves its folder's name too (2026-10-11, the user's ask: the folder follows the
    /// name): the store's row and every picture path of its indexed pages that lies under its old <c>reference</c> or <c>cache</c> folder,
    /// in one transaction (a full path from before is stored relative after). The folders on disk are the caller's to move. False when
    /// another store has the name or the folder, or the write failed (nothing changed then).
    /// </summary>
    public bool RenameStore(long storeId, string name, string? folder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return false;
            }

            using var transaction = connection.BeginTransaction();
            try
            {
                string? oldFolder = null;
                using (var read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText = "SELECT folder FROM stores WHERE id = $id";
                    read.Parameters.AddWithValue("$id", storeId);
                    oldFolder = read.ExecuteScalar() as string;
                }

                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE stores SET name = $name, folder = coalesce($folder, folder) WHERE id = $id " +
                        "AND NOT EXISTS (SELECT 1 FROM stores WHERE (name = $name COLLATE NOCASE OR folder = $folder COLLATE NOCASE) AND id <> $id)";
                    update.Parameters.AddWithValue("$id", storeId);
                    update.Parameters.AddWithValue("$name", name.Trim());
                    update.Parameters.AddWithValue("$folder", (object?)folder ?? DBNull.Value);
                    if (oldFolder is null || update.ExecuteNonQuery() == 0)
                    {
                        transaction.Rollback();
                        return false;
                    }
                }

                if (folder is not null && !string.Equals(folder, oldFolder, StringComparison.Ordinal))
                {
                    var moved = new List<(long Id, string Image)>();
                    using (var pages = connection.CreateCommand())
                    {
                        pages.Transaction = transaction;
                        pages.CommandText = "SELECT p.id, p.image FROM ref_pages p JOIN ref_docs d ON d.id = p.doc_id WHERE d.store_id = $id AND p.image IS NOT NULL";
                        pages.Parameters.AddWithValue("$id", storeId);
                        using var reader = pages.ExecuteReader();
                        while (reader.Read())
                        {
                            moved.Add((reader.GetInt64(0), MovedImage(reader.GetString(1), oldFolder, folder)));
                        }
                    }

                    foreach (var (id, image) in moved)
                    {
                        using var row = connection.CreateCommand();
                        row.Transaction = transaction;
                        row.CommandText = "UPDATE ref_pages SET image = $image WHERE id = $id";
                        row.Parameters.AddWithValue("$id", id);
                        row.Parameters.AddWithValue("$image", image);
                        row.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
                return true;
            }
            catch (SqliteException ex)
            {
                transaction.Rollback();
                Fail("rename a store", ex);
                return false;
            }
        }
    }

    /// <summary>
    /// A page's picture path once its store's folder <paramref name="from"/> is <paramref name="to"/>: relative to the store file's folder
    /// (<c>cache\h-e-b\guide.pdf-p2.png</c>), the store's folder swapped where the path lies under its <c>reference</c> or <c>cache</c>
    /// folder; any other path relative as it is, or full when it lies outside. Pure but for the folder it is relative to.
    /// </summary>
    public string MovedImage(string image, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(image);
        string relative = Path.GetRelativePath(Folder, FullImage(image));
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length > 2 && (parts[0].Equals(ReferenceFolderName, StringComparison.OrdinalIgnoreCase) || parts[0].Equals(ShoppingReference.CacheFolderName, StringComparison.OrdinalIgnoreCase))
            && parts[1].Equals(from, StringComparison.OrdinalIgnoreCase))
        {
            parts[1] = to;
            return string.Join(Path.DirectorySeparatorChar, parts);
        }

        return relative;
    }

    /// <summary>A stored picture path made full: one relative to the store file's folder (since 2026-10-11) joined to it, a full one (earlier) as it is.</summary>
    public string FullImage(string image) => Path.IsPathRooted(image) ? image : Path.GetFullPath(Path.Combine(Folder, image));

    /// <summary>A picture path to store: relative to the store file's folder when it lies under it, so a profile moved or a store renamed keeps it.</summary>
    public string StoredImage(string full)
    {
        string relative = Path.GetRelativePath(Folder, full);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? full : relative;
    }
    /// <summary>Removes a store with its sections, locations and placements. False when nothing was removed.</summary>
    public bool RemoveStore(long storeId) =>
        Execute("DELETE FROM stores WHERE id = $id", c => c.Parameters.AddWithValue("$id", storeId), "remove a store") is > 0;

    // ---- sections and locations ----

    /// <summary>A store's sections, by name.</summary>
    public IReadOnlyList<ShopSection> Sections(long storeId) =>
        Query("SELECT id, store_id, name FROM sections WHERE store_id = $s ORDER BY name COLLATE NOCASE",
            c => c.Parameters.AddWithValue("$s", storeId), r => new ShopSection(r.GetInt64(0), r.GetInt64(1), r.GetString(2)), "read the sections");

    /// <summary>The section of a store named <paramref name="name"/> (any case), made when there is none; null when the write failed.</summary>
    public ShopSection? EnsureSection(long storeId, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string trimmed = name.Trim();
        Execute("INSERT OR IGNORE INTO sections(store_id, name) VALUES ($s, $n)",
            c => { c.Parameters.AddWithValue("$s", storeId); c.Parameters.AddWithValue("$n", trimmed); }, "add a section");
        return Sections(storeId).FirstOrDefault(s => string.Equals(s.Name, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Renames a section. False when the store has another of that name or nothing changed.</summary>
    public bool RenameSection(long sectionId, string name) =>
        Execute("UPDATE sections SET name = $n WHERE id = $id AND NOT EXISTS (SELECT 1 FROM sections o WHERE o.store_id = sections.store_id AND o.name = $n COLLATE NOCASE AND o.id <> $id)",
            c => { c.Parameters.AddWithValue("$id", sectionId); c.Parameters.AddWithValue("$n", name.Trim()); }, "rename a section") is > 0;

    /// <summary>Removes a section; its locations and placements keep their places with no section.</summary>
    public bool RemoveSection(long sectionId) =>
        Execute("DELETE FROM sections WHERE id = $id", c => c.Parameters.AddWithValue("$id", sectionId), "remove a section") is > 0;

    /// <summary>A store's locations with their sections' names, in walk order (<see cref="ShoppingPlacer.WalkOrder"/>).</summary>
    public IReadOnlyList<ShopLocation> Locations(long storeId) =>
        ShoppingPlacer.WalkOrder(Query(
            "SELECT l.id, l.store_id, l.label, l.section_id, s.name, l.walk FROM locations l LEFT JOIN sections s ON s.id = l.section_id WHERE l.store_id = $s",
            c => c.Parameters.AddWithValue("$s", storeId),
            r => new ShopLocation(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetInt32(5)),
            "read the locations")).ToList();

    /// <summary>
    /// The location of a store labelled <paramref name="label"/> (any case), made when there is none; its section and walk set when
    /// given (a null leaves an existing one's as it is). Null when the write failed.
    /// </summary>
    public ShopLocation? EnsureLocation(long storeId, string label, long? sectionId = null, int? walk = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        string trimmed = label.Trim();
        Execute("INSERT INTO locations(store_id, label, section_id, walk) VALUES ($s, $l, $sec, $w) " +
                "ON CONFLICT(store_id, label) DO UPDATE SET section_id = coalesce(excluded.section_id, section_id), walk = coalesce(excluded.walk, walk)",
            c =>
            {
                c.Parameters.AddWithValue("$s", storeId);
                c.Parameters.AddWithValue("$l", trimmed);
                c.Parameters.AddWithValue("$sec", (object?)sectionId ?? DBNull.Value);
                c.Parameters.AddWithValue("$w", (object?)walk ?? DBNull.Value);
            }, "add a location");
        return Locations(storeId).FirstOrDefault(l => string.Equals(l.Label, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Renames a location. False when the store has another of that label or nothing changed.</summary>
    public bool RenameLocation(long locationId, string label) =>
        Execute("UPDATE locations SET label = $l WHERE id = $id AND NOT EXISTS (SELECT 1 FROM locations o WHERE o.store_id = locations.store_id AND o.label = $l COLLATE NOCASE AND o.id <> $id)",
            c => { c.Parameters.AddWithValue("$id", locationId); c.Parameters.AddWithValue("$l", label.Trim()); }, "rename a location") is > 0;

    /// <summary>Puts a location in a section (null: none). False when nothing changed.</summary>
    public bool SetLocationSection(long locationId, long? sectionId) =>
        Execute("UPDATE locations SET section_id = $s WHERE id = $id",
            c => { c.Parameters.AddWithValue("$id", locationId); c.Parameters.AddWithValue("$s", (object?)sectionId ?? DBNull.Value); }, "set a location's section") is > 0;

    /// <summary>Sets a location's place on the walk (null: back to its label's natural order).</summary>
    public bool SetWalk(long locationId, int? walk) =>
        Execute("UPDATE locations SET walk = $w WHERE id = $id",
            c => { c.Parameters.AddWithValue("$id", locationId); c.Parameters.AddWithValue("$w", (object?)walk ?? DBNull.Value); }, "set a location's walk") is > 0;

    /// <summary>Removes a location with the placements at it.</summary>
    public bool RemoveLocation(long locationId) =>
        Execute("DELETE FROM locations WHERE id = $id", c => c.Parameters.AddWithValue("$id", locationId), "remove a location") is > 0;

    // ---- placements ----

    /// <summary>A store's placements, by term.</summary>
    public IReadOnlyList<ShopPlacement> Placements(long storeId) =>
        Query("SELECT id, store_id, term, kind, location_id, section_id, source, note FROM placements WHERE store_id = $s ORDER BY term COLLATE NOCASE",
            c => c.Parameters.AddWithValue("$s", storeId),
            r => new ShopPlacement(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetInt64(5), r.GetString(6), r.GetString(7)),
            "read the placements");

    /// <summary>
    /// Records where <paramref name="term"/> is at a store: a new placement, or the one already held for the term (any case) moved and
    /// relabelled. At least one of <paramref name="locationId"/> and <paramref name="sectionId"/> is set. False when the write failed.
    /// </summary>
    public bool Place(long storeId, string term, string kind, long? locationId, long? sectionId, string source, string note = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        if (locationId is null && sectionId is null)
        {
            throw new ArgumentException("A placement needs a location or a section.", nameof(locationId));
        }

        return Execute(
            "INSERT INTO placements(store_id, term, kind, location_id, section_id, source, note, updated_at) VALUES ($s, $t, $k, $l, $sec, $src, $n, $at) " +
            "ON CONFLICT(store_id, term) DO UPDATE SET kind = excluded.kind, location_id = excluded.location_id, section_id = excluded.section_id, source = excluded.source, note = excluded.note, updated_at = excluded.updated_at",
            c =>
            {
                c.Parameters.AddWithValue("$s", storeId);
                c.Parameters.AddWithValue("$t", term.Trim());
                c.Parameters.AddWithValue("$k", kind);
                c.Parameters.AddWithValue("$l", (object?)locationId ?? DBNull.Value);
                c.Parameters.AddWithValue("$sec", (object?)sectionId ?? DBNull.Value);
                c.Parameters.AddWithValue("$src", source);
                c.Parameters.AddWithValue("$n", note);
                c.Parameters.AddWithValue("$at", Now());
            }, "record a placement") is not null;
    }

    /// <summary>Changes what a placement's term names (2026-10-11, the layout editor). False when nothing changed.</summary>
    public bool SetKind(long placementId, string kind) =>
        Execute("UPDATE placements SET kind = $k, updated_at = $at WHERE id = $id",
            c => { c.Parameters.AddWithValue("$id", placementId); c.Parameters.AddWithValue("$k", kind); c.Parameters.AddWithValue("$at", Now()); }, "change a placement's kind") is > 0;

    /// <summary>Changes how a placement was learnt (2026-10-11: a guess kept becomes the user's). False when nothing changed.</summary>
    public bool SetSource(long placementId, string source) =>
        Execute("UPDATE placements SET source = $s, updated_at = $at WHERE id = $id",
            c => { c.Parameters.AddWithValue("$id", placementId); c.Parameters.AddWithValue("$s", source); c.Parameters.AddWithValue("$at", Now()); }, "change a placement's source") is > 0;

    /// <summary>Forgets a placement by its id (2026-10-11, the layout editor). False when there was none.</summary>
    public bool RemovePlacement(long placementId) =>
        Execute("DELETE FROM placements WHERE id = $id", c => c.Parameters.AddWithValue("$id", placementId), "forget a placement") is > 0;

    /// <summary>Forgets where <paramref name="term"/> is at a store (any case). False when there was no such placement.</summary>
    public bool Unplace(long storeId, string term) =>
        Execute("DELETE FROM placements WHERE store_id = $s AND term = $t COLLATE NOCASE",
            c => { c.Parameters.AddWithValue("$s", storeId); c.Parameters.AddWithValue("$t", term.Trim()); }, "forget a placement") is > 0;

    // ---- items ----

    /// <summary>The list, oldest first.</summary>
    public IReadOnlyList<ShopItem> Items() =>
        Query("SELECT id, name, quantity, note, checked, created_at FROM items ORDER BY id", null,
            r => new ShopItem(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetInt64(4) != 0, Parse(r.GetString(5))),
            "read the list");

    /// <summary>
    /// The lines still to get, the unticked ones (later on 2026-10-12, the home bar's 🛒 chip: the pane caption's "items to get"); null
    /// when the store is off or the read failed.
    /// </summary>
    public int? OpenCount() =>
        Query("SELECT COUNT(*) FROM items WHERE checked = 0", null, r => r.GetInt64(0), "count the list") is [var count] ? (int)count : null;

    /// <summary>The store's lock, for a test to hold as a long write would (<see cref="TryOpenCount"/>).</summary>
    internal object Gate => _gate;

    /// <summary>
    /// <see cref="OpenCount"/> without waiting longer than <paramref name="wait"/> for the store's lock (2026-10-13, the code review: the
    /// home bar's first count is read on the drawing thread): false, <paramref name="count"/> null, when a write held it that long.
    /// </summary>
    public bool TryOpenCount(TimeSpan wait, out int? count)
    {
        count = null;
        if (!Monitor.TryEnter(_gate, wait))
        {
            return false;
        }

        try
        {
            count = OpenCount();   // the lock is re-entered there
            return true;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    /// <summary>A new line; null when the write failed.</summary>
    public ShopItem? AddItem(string name, int quantity, string note = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var now = _time.GetUtcNow();
        long? id = Insert("INSERT INTO items(name, quantity, note, checked, created_at, updated_at) VALUES ($n, $q, $note, 0, $at, $at)",
            c =>
            {
                c.Parameters.AddWithValue("$n", name.Trim());
                c.Parameters.AddWithValue("$q", Math.Max(1, quantity));
                c.Parameters.AddWithValue("$note", note);
                c.Parameters.AddWithValue("$at", SessionStore.Stamp(now));
            }, "add to the list");
        return id is { } made ? new ShopItem(made, name.Trim(), Math.Max(1, quantity), note, false, now) : null;
    }

    /// <summary>Sets a line's quantity (at least 1), unticking it. False when nothing changed.</summary>
    public bool SetQuantity(long itemId, int quantity) =>
        Execute("UPDATE items SET quantity = $q, checked = 0, checked_at = NULL, updated_at = $at WHERE id = $id",
            c => { c.Parameters.AddWithValue("$id", itemId); c.Parameters.AddWithValue("$q", Math.Max(1, quantity)); c.Parameters.AddWithValue("$at", Now()); }, "change a quantity") is > 0;

    /// <summary>Ticks or unticks a line. False when nothing changed.</summary>
    public bool SetChecked(long itemId, bool on) =>
        Execute("UPDATE items SET checked = $c, checked_at = $when, updated_at = $at WHERE id = $id",
            c =>
            {
                c.Parameters.AddWithValue("$id", itemId);
                c.Parameters.AddWithValue("$c", on ? 1 : 0);
                c.Parameters.AddWithValue("$when", on ? Now() : DBNull.Value);
                c.Parameters.AddWithValue("$at", Now());
            }, "tick a line") is > 0;

    /// <summary>Ticks (or unticks) every line not already so (2026-10-11, the pane's tick all): how many changed, null when the write failed.</summary>
    public int? SetAllChecked(bool on) =>
        Execute("UPDATE items SET checked = $c, checked_at = $when, updated_at = $at WHERE checked <> $c",
            c =>
            {
                c.Parameters.AddWithValue("$c", on ? 1 : 0);
                c.Parameters.AddWithValue("$when", on ? Now() : DBNull.Value);
                c.Parameters.AddWithValue("$at", Now());
            }, "tick every line");

    /// <summary>Renames a line. False when nothing changed.</summary>
    public bool RenameItem(long itemId, string name) =>
        Execute("UPDATE items SET name = $n, updated_at = $at WHERE id = $id",
            c => { c.Parameters.AddWithValue("$id", itemId); c.Parameters.AddWithValue("$n", name.Trim()); c.Parameters.AddWithValue("$at", Now()); }, "rename a line") is > 0;

    /// <summary>
    /// Removes a line, and records <paramref name="bought"/> as bought (<see cref="RecordBought"/>'s row) in the same transaction
    /// (2026-10-11 review: recorded first and apart, a delete that then failed counted the line bought all the same, and again at every
    /// retry). False when nothing was removed (nothing recorded then).
    /// </summary>
    public bool RemoveItem(long itemId, ShopItem? bought = null) =>
        InTransaction("remove a line", (connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM items WHERE id = $id";
            command.Parameters.AddWithValue("$id", itemId);
            if (command.ExecuteNonQuery() == 0)
            {
                return null;
            }

            if (bought is not null)
            {
                RecordBoughtIn(connection, transaction, bought.Name, bought.Quantity);
            }

            return 1;
        }) is > 0;

    /// <summary>
    /// Removes the ticked lines (every line with <paramref name="all"/>); how many went, null when the write failed. With
    /// <paramref name="recordBought"/> the ticked lines are recorded as bought (<see cref="RecordBought"/>'s rows) in the same transaction.
    /// </summary>
    public int? Clear(bool all, bool recordBought = false) =>
        InTransaction("clear the list", (connection, transaction) =>
        {
            if (recordBought)
            {
                var ticked = new List<(string Name, int Quantity)>();
                using (var read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText = "SELECT name, quantity FROM items WHERE checked <> 0 ORDER BY id";
                    using var reader = read.ExecuteReader();
                    while (reader.Read())
                    {
                        ticked.Add((reader.GetString(0), reader.GetInt32(1)));
                    }
                }

                foreach (var (name, quantity) in ticked)
                {
                    RecordBoughtIn(connection, transaction, name, quantity);
                }
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = all ? "DELETE FROM items" : "DELETE FROM items WHERE checked <> 0";
            return command.ExecuteNonQuery();
        });

    // ---- reference documents (2026-10-11) ----

    /// <summary>A store's reference documents as last read, by path.</summary>
    public IReadOnlyList<ShopRefDoc> RefDocs(long storeId) =>
        Query("SELECT id, store_id, file, size, modified, pages, page_count, detail FROM ref_docs WHERE store_id = $s ORDER BY file COLLATE NOCASE",
            c => c.Parameters.AddWithValue("$s", storeId),
            r => new ShopRefDoc(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetInt32(5), r.GetInt32(6), r.GetString(7)),
            "read the reference documents");

    /// <summary>
    /// A reference document's read recorded: its row (made or replaced, any case of its path) and its pages, the old pages gone, in one
    /// transaction. False when the write failed (nothing changed then).
    /// </summary>
    public bool SaveRefDoc(long storeId, string file, long size, long modified, int pageCount, string detail, IReadOnlyList<(int Page, string Text, string? Image)> pages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNull(pages);
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return false;
            }

            using var transaction = connection.BeginTransaction();
            try
            {
                using (var drop = connection.CreateCommand())
                {
                    drop.Transaction = transaction;
                    drop.CommandText = "DELETE FROM ref_docs WHERE store_id = $s AND file = $f COLLATE NOCASE";
                    drop.Parameters.AddWithValue("$s", storeId);
                    drop.Parameters.AddWithValue("$f", file);
                    drop.ExecuteNonQuery();
                }

                long docId;
                using (var doc = connection.CreateCommand())
                {
                    doc.Transaction = transaction;
                    doc.CommandText = "INSERT INTO ref_docs(store_id, file, size, modified, pages, page_count, detail, indexed_at) VALUES ($s, $f, $size, $m, $p, $n, $d, $at); SELECT last_insert_rowid();";
                    doc.Parameters.AddWithValue("$s", storeId);
                    doc.Parameters.AddWithValue("$f", file);
                    doc.Parameters.AddWithValue("$size", size);
                    doc.Parameters.AddWithValue("$m", modified);
                    doc.Parameters.AddWithValue("$p", pages.Count);
                    doc.Parameters.AddWithValue("$n", pageCount);
                    doc.Parameters.AddWithValue("$d", detail);
                    doc.Parameters.AddWithValue("$at", Now());
                    docId = (long)doc.ExecuteScalar()!;
                }

                foreach (var (page, text, image) in pages)
                {
                    using var row = connection.CreateCommand();
                    row.Transaction = transaction;
                    row.CommandText = "INSERT INTO ref_pages(doc_id, page, text, image) VALUES ($doc, $page, $text, $image)";
                    row.Parameters.AddWithValue("$doc", docId);
                    row.Parameters.AddWithValue("$page", page);
                    row.Parameters.AddWithValue("$text", text);
                    row.Parameters.AddWithValue("$image", (object?)image ?? DBNull.Value);
                    row.ExecuteNonQuery();
                }

                transaction.Commit();
                return true;
            }
            catch (SqliteException ex)
            {
                transaction.Rollback();
                Fail("record a reference document", ex);
                return false;
            }
        }
    }

    /// <summary>Forgets a reference document and its pages (the file is gone).</summary>
    public bool RemoveRefDoc(long docId) =>
        Execute("DELETE FROM ref_docs WHERE id = $id", c => c.Parameters.AddWithValue("$id", docId), "forget a reference document") is > 0;

    /// <summary>The pages of a store's documents that <paramref name="ftsQuery"/> (an FTS5 query) matches, the best first, at most <paramref name="limit"/>.</summary>
    public IReadOnlyList<ShopRefPage> SearchRef(long storeId, string ftsQuery, int limit)
    {
        ArgumentNullException.ThrowIfNull(ftsQuery);
        if (ftsQuery.Length == 0)
        {
            return [];
        }

        return Query(
            "SELECT p.id, p.doc_id, d.file, p.page, p.text, p.image FROM ref_fts JOIN ref_pages p ON p.id = ref_fts.rowid JOIN ref_docs d ON d.id = p.doc_id " +
            "WHERE ref_fts MATCH $q AND d.store_id = $s ORDER BY bm25(ref_fts) LIMIT $n",
            c => { c.Parameters.AddWithValue("$q", ftsQuery); c.Parameters.AddWithValue("$s", storeId); c.Parameters.AddWithValue("$n", limit); },
            ReadRefPage, "search the reference documents");
    }

    /// <summary>A page of a store's document by its path (any case) and number; null for none.</summary>
    public ShopRefPage? RefPage(long storeId, string file, int page) =>
        Query("SELECT p.id, p.doc_id, d.file, p.page, p.text, p.image FROM ref_pages p JOIN ref_docs d ON d.id = p.doc_id WHERE d.store_id = $s AND d.file = $f COLLATE NOCASE AND p.page = $p",
            c => { c.Parameters.AddWithValue("$s", storeId); c.Parameters.AddWithValue("$f", file); c.Parameters.AddWithValue("$p", page); },
            ReadRefPage, "read a reference page").FirstOrDefault();

    /// <summary>A page read, its picture's path made full (<see cref="FullImage"/>).</summary>
    private ShopRefPage ReadRefPage(SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.IsDBNull(5) ? null : FullImage(r.GetString(5)));

    // ---- bought (2026-10-11, the recently bought) ----

    /// <summary>
    /// <paramref name="items"/> recorded as bought: each by its folded name (<see cref="ShoppingPlacer.Key"/>), its count of times up by
    /// one, its latest name, quantity and time kept. False when the write failed.
    /// </summary>
    public bool RecordBought(IEnumerable<ShopItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return false;
            }

            using var transaction = connection.BeginTransaction();
            try
            {
                foreach (var item in items)
                {
                    RecordBoughtIn(connection, transaction, item.Name, item.Quantity);
                }

                transaction.Commit();
                return true;
            }
            catch (SqliteException ex)
            {
                transaction.Rollback();
                Fail("record what was bought", ex);
                return false;
            }
        }
    }

    /// <summary>One line recorded as bought inside <paramref name="transaction"/>: <see cref="RecordBought"/>'s row.</summary>
    private void RecordBoughtIn(SqliteConnection connection, SqliteTransaction transaction, string name, int quantity)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO bought(key, name, quantity, times, last_at) VALUES ($k, $n, $q, 1, $at) " +
            "ON CONFLICT(key) DO UPDATE SET name = excluded.name, quantity = excluded.quantity, times = times + 1, last_at = excluded.last_at";
        command.Parameters.AddWithValue("$k", ShoppingPlacer.Key(name));
        command.Parameters.AddWithValue("$n", name);
        command.Parameters.AddWithValue("$q", quantity);
        command.Parameters.AddWithValue("$at", Now());
        command.ExecuteNonQuery();
    }

    /// <summary>What was bought before, the most often first, then the most recent.</summary>
    public IReadOnlyList<ShopBought> Bought() =>
        Query("SELECT id, name, quantity, times, last_at FROM bought ORDER BY times DESC, last_at DESC", null,
            r => new ShopBought(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), Parse(r.GetString(4))), "read what was bought");

    /// <summary>Forgets a line bought before. False when there was none.</summary>
    public bool ForgetBought(long id) =>
        Execute("DELETE FROM bought WHERE id = $id", c => c.Parameters.AddWithValue("$id", id), "forget a bought line") is > 0;

    // ---- plumbing ----

    private static ShopStore ReadStore(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3));

    private string Now() => SessionStore.Stamp(_time.GetUtcNow());

    private static DateTimeOffset Parse(string stamp) =>
        DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var moment) ? moment : DateTimeOffset.UnixEpoch;

    private IReadOnlyList<T> Query<T>(string sql, Action<SqliteCommand>? bind, Func<SqliteDataReader, T> map, string what)
    {
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return [];
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                bind?.Invoke(command);
                using var reader = command.ExecuteReader();
                var rows = new List<T>();
                while (reader.Read())
                {
                    rows.Add(map(reader));
                }

                return rows;
            }
            catch (SqliteException ex)
            {
                Fail(what, ex);
                return [];
            }
        }
    }

    /// <summary>The rows the statement changed; null when the file is off or the statement failed (logged).</summary>
    private int? Execute(string sql, Action<SqliteCommand>? bind, string what)
    {
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return null;
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                bind?.Invoke(command);
                return command.ExecuteNonQuery();
            }
            catch (SqliteException ex)
            {
                Fail(what, ex);
                return null;
            }
        }
    }

    /// <summary>
    /// <paramref name="work"/> in one transaction: committed with what it returns, rolled back when it returns null or a statement fails
    /// (logged); null when the file is off.
    /// </summary>
    private int? InTransaction(string what, Func<SqliteConnection, SqliteTransaction, int?> work)
    {
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return null;
            }

            using var transaction = connection.BeginTransaction();
            try
            {
                if (work(connection, transaction) is not { } done)
                {
                    transaction.Rollback();
                    return null;
                }

                transaction.Commit();
                return done;
            }
            catch (SqliteException ex)
            {
                transaction.Rollback();
                Fail(what, ex);
                return null;
            }
        }
    }

    /// <summary>The new row's id; null when the file is off or the statement failed (logged).</summary>
    private long? Insert(string sql, Action<SqliteCommand> bind, string what)
    {
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return null;
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql + "; SELECT last_insert_rowid();";
                bind(command);
                return command.ExecuteScalar() is long id ? id : null;
            }
            catch (SqliteException ex)
            {
                Fail(what, ex);
                return null;
            }
        }
    }

    private SqliteConnection? Open()
    {
        if (_connection is not null)
        {
            return _connection;
        }

        if (_failed || _disposed)
        {
            return null;
        }

        SqliteConnection? connection = null;
        try
        {
            Directory.CreateDirectory(Folder);
            connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _filePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
            connection.Open();
            using (var pragmas = connection.CreateCommand())
            {
                // foreign_keys: a store's sections, locations and placements go with it.
                pragmas.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;";
                pragmas.ExecuteNonQuery();
            }

            // The version is read before Schema runs, so a file this code does not know is refused untouched (SessionStore's rule).
            using (var version = connection.CreateCommand())
            {
                version.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'meta'";
                bool hasMeta = version.ExecuteScalar() is long tables && tables > 0;
                version.CommandText = "SELECT value FROM meta WHERE key = 'schema'";
                if (hasMeta && version.ExecuteScalar() is long stored && stored != SchemaVersion)
                {
                    connection.Dispose();
                    _failed = true;
                    DiagnosticLog.Warn(Category, SchemaMismatchWarning(stored));
                    return null;
                }
            }

            using (var schema = connection.CreateCommand())
            {
                schema.CommandText = Schema;
                schema.ExecuteNonQuery();
            }

            _connection = connection;
            return connection;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            connection?.Dispose();
            _failed = true;
            DiagnosticLog.Warn(Category, $"Could not open {FileName}; the shopping list is off: {ex.Message}", ex);
            return null;
        }
    }

    private static void Fail(string what, SqliteException ex) =>
        DiagnosticLog.Error(Category, $"Could not {what} in {FileName}: {ex.Message}", ex);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _connection?.Dispose();
            _connection = null;
        }
    }

    /// <summary>
    /// The schema, every statement <c>IF NOT EXISTS</c>. Names and labels compare without case, so <c>Aisle 2</c> and <c>aisle 2</c> are one.
    /// A placement's location goes with it (a fact about a place that is gone says nothing); a section's removal leaves its locations and
    /// placements without one. <c>meta</c>'s value is an integer: the schema number, the active store's id.
    /// </summary>
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value INTEGER NOT NULL);
        INSERT OR IGNORE INTO meta(key, value) VALUES ('schema', 1);
        CREATE TABLE IF NOT EXISTS stores(
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE,
            folder TEXT NOT NULL UNIQUE COLLATE NOCASE,
            notes TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS sections(
            id INTEGER PRIMARY KEY,
            store_id INTEGER NOT NULL REFERENCES stores(id) ON DELETE CASCADE,
            name TEXT NOT NULL COLLATE NOCASE,
            UNIQUE(store_id, name));
        CREATE TABLE IF NOT EXISTS locations(
            id INTEGER PRIMARY KEY,
            store_id INTEGER NOT NULL REFERENCES stores(id) ON DELETE CASCADE,
            label TEXT NOT NULL COLLATE NOCASE,
            section_id INTEGER REFERENCES sections(id) ON DELETE SET NULL,
            walk INTEGER,
            UNIQUE(store_id, label));
        CREATE TABLE IF NOT EXISTS placements(
            id INTEGER PRIMARY KEY,
            store_id INTEGER NOT NULL REFERENCES stores(id) ON DELETE CASCADE,
            term TEXT NOT NULL COLLATE NOCASE,
            kind TEXT NOT NULL,
            location_id INTEGER REFERENCES locations(id) ON DELETE CASCADE,
            section_id INTEGER REFERENCES sections(id) ON DELETE SET NULL,
            source TEXT NOT NULL,
            note TEXT NOT NULL DEFAULT '',
            updated_at TEXT NOT NULL,
            UNIQUE(store_id, term));
        CREATE INDEX IF NOT EXISTS placements_location ON placements(location_id);
        CREATE TABLE IF NOT EXISTS items(
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            quantity INTEGER NOT NULL DEFAULT 1 CHECK (quantity > 0),
            note TEXT NOT NULL DEFAULT '',
            checked INTEGER NOT NULL DEFAULT 0,
            checked_at TEXT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS bought(
            id INTEGER PRIMARY KEY,
            key TEXT NOT NULL UNIQUE,
            name TEXT NOT NULL,
            quantity INTEGER NOT NULL,
            times INTEGER NOT NULL,
            last_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS ref_docs(
            id INTEGER PRIMARY KEY,
            store_id INTEGER NOT NULL REFERENCES stores(id) ON DELETE CASCADE,
            file TEXT NOT NULL COLLATE NOCASE,
            size INTEGER NOT NULL,
            modified INTEGER NOT NULL,
            pages INTEGER NOT NULL,
            page_count INTEGER NOT NULL,
            detail TEXT NOT NULL DEFAULT '',
            indexed_at TEXT NOT NULL,
            UNIQUE(store_id, file));
        CREATE TABLE IF NOT EXISTS ref_pages(
            id INTEGER PRIMARY KEY,
            doc_id INTEGER NOT NULL REFERENCES ref_docs(id) ON DELETE CASCADE,
            page INTEGER NOT NULL,
            text TEXT NOT NULL,
            image TEXT);
        CREATE INDEX IF NOT EXISTS ref_pages_doc ON ref_pages(doc_id, page);
        CREATE VIRTUAL TABLE IF NOT EXISTS ref_fts USING fts5(text, content='ref_pages', content_rowid='id', tokenize='unicode61');
        CREATE TRIGGER IF NOT EXISTS ref_pages_ai AFTER INSERT ON ref_pages BEGIN
            INSERT INTO ref_fts(rowid, text) VALUES (new.id, new.text);
        END;
        CREATE TRIGGER IF NOT EXISTS ref_pages_ad AFTER DELETE ON ref_pages BEGIN
            INSERT INTO ref_fts(ref_fts, rowid, text) VALUES ('delete', old.id, old.text);
        END;
        CREATE TRIGGER IF NOT EXISTS ref_pages_au AFTER UPDATE ON ref_pages BEGIN
            INSERT INTO ref_fts(ref_fts, rowid, text) VALUES ('delete', old.id, old.text);
            INSERT INTO ref_fts(rowid, text) VALUES (new.id, new.text);
        END;
        """;
}
