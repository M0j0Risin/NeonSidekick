using System.Collections;
using System.Data.Common;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// A <see cref="DbDataReader"/> over rows of objects with declared type names (2026-10-29, the Office writing plan): drives each family's typed
/// export reader with its provider's own value types, no server needed. A cell holding an <see cref="Exception"/> throws it from
/// <see cref="GetValue"/> (an unreadable value); <see cref="ProviderValues"/> answers <see cref="GetProviderSpecificValue"/> for such a cell.
/// <see cref="Reads"/> counts the rows read, so a test can see what was read when.
/// </summary>
public sealed class FakeDataReader : DbDataReader
{
    private readonly string[] _names;
    private readonly string[] _types;
    private readonly IReadOnlyList<object?[]> _rows;
    private int _at = -1;

    public FakeDataReader(string[] names, string[] types, params object?[][] rows)
    {
        _names = names;
        _types = types;
        _rows = rows;
    }

    /// <summary>The provider's own value per (row, column), for a cell whose <see cref="GetValue"/> throws.</summary>
    public Dictionary<(int Row, int Column), object> ProviderValues { get; } = [];

    /// <summary>The rows <see cref="Read"/> has moved to.</summary>
    public int Reads { get; private set; }

    public override int FieldCount => _names.Length;

    public override bool HasRows => _rows.Count > 0;

    public override bool IsClosed => false;

    public override int RecordsAffected => -1;

    public override int Depth => 0;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        if (_at + 1 >= _rows.Count)
        {
            return false;
        }

        _at++;
        Reads++;
        return true;
    }

    public override bool NextResult() => false;

    public override string GetName(int ordinal) => _names[ordinal];

    public override string GetDataTypeName(int ordinal) => _types[ordinal];

    public override Type GetFieldType(int ordinal) => _rows.Select(r => r[ordinal]).FirstOrDefault(v => v is not null and not Exception)?.GetType() ?? typeof(object);

    public override int GetOrdinal(string name) => Array.IndexOf(_names, name);

    public override bool IsDBNull(int ordinal) => _rows[_at][ordinal] is null or DBNull;

    public override object GetValue(int ordinal) => _rows[_at][ordinal] switch
    {
        Exception ex => throw ex,
        null => DBNull.Value,
        var value => value,
    };

    public override object GetProviderSpecificValue(int ordinal) =>
        ProviderValues.TryGetValue((_at, ordinal), out var value) ? value : GetValue(ordinal);

    public override int GetValues(object[] values)
    {
        int n = Math.Min(values.Length, FieldCount);
        for (int i = 0; i < n; i++)
        {
            values[i] = GetValue(i);
        }

        return n;
    }

    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);

    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

    public override char GetChar(int ordinal) => (char)GetValue(ordinal);

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);

    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);

    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);

    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);

    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);

    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);

    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);

    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);

    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
}
