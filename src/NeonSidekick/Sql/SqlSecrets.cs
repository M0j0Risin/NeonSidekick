namespace NeonSidekick.Sql;

/// <summary>
/// The password a connection signs in with (later on 2026-09-23, the user's call: every SQL password kept safe, the
/// store chosen per connection): nothing for <c>windows</c>; under <c>passwordStore: credman</c> the Windows
/// Credential Manager entry <see cref="SqlConnectionConfig.CredentialTarget"/>; under <c>file</c> (the default) the
/// <c>password</c> of <c>sql.json</c> decrypted — or, while it is still plain text (a file the app could not rewrite),
/// as it stands. A missing or undecryptable password is the sentence that names the fix, never a throw. The work is
/// <see cref="SqlConfigFile.Family"/>'s since 2026-10-24 (the database review's Q11: this class was a copy of
/// <see cref="ConnectionFamily{TConfig, TNamed}"/>'s, and its missing-credential wording had drifted from the other families').
/// </summary>
public static class SqlSecrets
{
    public static CredentialResult Resolve(SqlNamedConnection connection) => SqlConfigFile.Family.Resolve(connection);

    /// <summary>
    /// Saves <paramref name="password"/> for <paramref name="connection"/> in its store (the SQL tab's masked prompt,
    /// later on 2026-09-23): under <c>credman</c> the Credential Manager entry, written with the connection's account;
    /// under <c>file</c> DPAPI-encrypted into the <c>sql.json</c> the connection came from. The status line's sentence either way.
    /// </summary>
    public static (bool Saved, string Notice) Save(SqlNamedConnection connection, string password) => SqlConfigFile.Family.Save(connection, password);
}
