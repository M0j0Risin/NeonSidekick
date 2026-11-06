namespace NeonSidekick.Updates;

/// <summary>
/// The public keys a release must be signed under (2026-11-04, self-update): the daily key and an offline backup, so a lost key doesn't
/// strand anyone. Their private halves live in the user's own key files, outside the repository and the app's home, and <c>ReleaseSign
/// --new-key</c> prints what goes here. Tests make a throwaway key at run time and pass it to <see cref="UpdateSignature"/>.
///
/// <para>Public, so <c>tools/ReleaseSign.cs</c> checks a signature against the app's own list before publishing: a wrong key file stops at a
/// draft and can't publish a release every copy refuses. The <c>update:</c> smoke probe fails unless this holds <see cref="Required"/> keys that
/// parse as P-256, so a release can't ship with none or a key of another curve and strand its users. No check can tell a real key from a test one: <c>tools/ReleaseSign.cs</c> checks the private keys it signs with against the tag's list.</para>
/// </summary>
public static class UpdateKeys
{
    /// <summary>How many keys a shipped build must embed: the daily one and the backup.</summary>
    public const int Required = 2;

    /// <summary>The embedded keys, each in <see cref="UpdateSignature.PublicKeyText"/>'s form. A lost or stolen key's handling is in <c>BUILD.md</c>'s "Making a release".</summary>
    public static IReadOnlyList<string> Embedded { get; } =
    [
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEVgZroEJW6q47srvwrO6xWmP2hBmyyAJjz9ODdPjLeJP6XSD6l0oyiV8djeIy75k5ewH652h5nLxz5WWcx3GqgA==",
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEOKf6SR8OTO5+JqBC4BwyJ3zG4MqSqhrh9+6W+49YCF0BilNEXgNc4aBamj7AOzITc4Z76eZtYspU8hWmfL+W+A==",
    ];
}
