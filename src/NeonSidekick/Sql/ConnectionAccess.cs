namespace NeonSidekick.Sql;

/// <summary>
/// The <c>access</c> key of a connection in <c>sql.json</c>, <c>oracle.json</c>, <c>mysql.json</c> and <c>postgres.json</c>
/// (2026-10-05, the user's two-key call, <c>unc.json</c>'s words): <c>read</c>, the default, or <c>readwrite</c> — which lets the
/// family's <c>_execute</c> tool change that connection's databases while the family's mode is <c>read-write</c> too. Either key off
/// and the connection only reads. Pure.
/// </summary>
public static class ConnectionAccess
{
    /// <summary>The <c>access</c> word for reading only (the default).</summary>
    public const string ReadAccess = "read";

    /// <summary>The <c>access</c> word for reading and changing, under the family's mode <c>read-write</c>.</summary>
    public const string ReadWriteAccess = "readwrite";

    /// <summary>Whether <paramref name="access"/> is <c>readwrite</c>.</summary>
    public static bool IsReadWrite(string? access) => string.Equals(access?.Trim(), ReadWriteAccess, StringComparison.OrdinalIgnoreCase);

    /// <summary>The problem with an <c>access</c> word, or null: empty, <c>read</c> and <c>readwrite</c> are fine. Pinned.</summary>
    public static string? Problem(string? access)
    {
        string word = access?.Trim() ?? "";
        return word.Length == 0 || word.Equals(ReadAccess, StringComparison.OrdinalIgnoreCase) || word.Equals(ReadWriteAccess, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"\"access\" is '{word}'; it must be read or readwrite";
    }

    /// <summary>The value the wizards store: null for <c>read</c> (the default, left out of the file), else <c>readwrite</c>.</summary>
    public static string? Stored(bool readWrite) => readWrite ? ReadWriteAccess : null;
}
