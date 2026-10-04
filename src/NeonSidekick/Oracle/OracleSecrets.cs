using NeonSidekick.Sql;

namespace NeonSidekick.Oracle;

/// <summary>
/// The password a connection signs in with (2026-09-30), <see cref="SqlSecrets"/>' part for Oracle, whose every connection
/// takes one: under <c>passwordStore: credman</c> the Windows Credential Manager entry
/// <see cref="OracleConnectionConfig.CredentialTarget"/>; under <c>file</c> (the default) the <c>password</c> of
/// <c>oracle.json</c> decrypted — or, while it is still plain text (a file the app could not rewrite), as it stands. A
/// missing or undecryptable password is the sentence that names the fix, never a throw. The work is
/// <see cref="OracleConfigFile.Family"/>'s (the 2026-10-04 review: the three password families were copies).
/// </summary>
public static class OracleSecrets
{
    public static CredentialResult Resolve(OracleNamedConnection connection) => OracleConfigFile.Family.Resolve(connection);

    /// <summary>
    /// Saves <paramref name="password"/> for <paramref name="connection"/> in its store (the Oracle tab's masked prompt and
    /// the wizard): under <c>credman</c> the Credential Manager entry, written with the connection's user; under <c>file</c>
    /// DPAPI-encrypted into the <c>oracle.json</c> the connection came from. The status line's sentence either way.
    /// </summary>
    public static (bool Saved, string Notice) Save(OracleNamedConnection connection, string password) => OracleConfigFile.Family.Save(connection, password);
}
