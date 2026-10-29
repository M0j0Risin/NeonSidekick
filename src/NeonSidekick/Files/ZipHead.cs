namespace NeonSidekick.Files;

/// <summary>
/// A zip's first bytes (2026-10-28, review P5-14: the Office reader, the skill installer and the model store each had their own copy of the
/// check): the local-header magic <c>PK\x03\x04</c>, and an empty archive's end record <c>PK\x05\x06</c> where the caller allows one. Pure.
/// </summary>
public static class ZipHead
{
    /// <summary>Whether <paramref name="head"/> starts a zip; <paramref name="allowEmpty"/> takes an empty archive's end record too.</summary>
    public static bool Looks(ReadOnlySpan<byte> head, bool allowEmpty = false) =>
        head.Length >= 4 && head[0] == (byte)'P' && head[1] == (byte)'K'
        && ((head[2] == 3 && head[3] == 4) || (allowEmpty && head[2] == 5 && head[3] == 6));
}
