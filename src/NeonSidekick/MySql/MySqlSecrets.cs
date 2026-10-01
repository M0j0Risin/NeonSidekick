using NeonSidekick.Sql;

namespace NeonSidekick.MySql;

/// <summary>
/// The password a connection signs in with (2026-09-30), <see cref="SqlSecrets"/>' part for MySQL, whose every connection
/// takes one: under <c>passwordStore: credman</c> the Windows Credential Manager entry
/// <see cref="MySqlConnectionConfig.CredentialTarget"/>; under <c>file</c> (the default) the <c>password</c> of
/// <c>mysql.json</c> decrypted — or, while it is still plain text (a file the app could not rewrite), as it stands. A
/// missing or undecryptable password is the sentence that names the fix, never a throw.
/// </summary>
public static class MySqlSecrets
{
    public static CredentialResult Resolve(MySqlNamedConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var config = connection.Config;
        if (config.InCredentialManager)
        {
            string target = config.CredentialTarget(connection.Name);
            var read = WindowsCredentials.ReadGeneric(target);
            return read.NotFound ? CredentialResult.Failed(MySqlText.NoCredential(target)) : read;
        }

        string password = config.Password ?? "";
        if (password.Length == 0)
        {
            return CredentialResult.Failed(MySqlText.NoPassword(connection.Name));
        }

        if (!WindowsCredentials.IsProtected(password))
        {
            return CredentialResult.Ok(password);
        }

        // WindowsCredentials words its failure for sql.json; the fix it names is on the MySQL tab here.
        var decrypted = WindowsCredentials.Unprotect(password);
        return decrypted.Error is { } error ? CredentialResult.Failed(error.Replace("the SQL tab", "the MySQL tab", StringComparison.Ordinal)) : decrypted;
    }

    /// <summary>
    /// Saves <paramref name="password"/> for <paramref name="connection"/> in its store (the MySQL tab's masked prompt and
    /// the wizard): under <c>credman</c> the Credential Manager entry, written with the connection's user; under <c>file</c>
    /// DPAPI-encrypted into the <c>mysql.json</c> the connection came from. The status line's sentence either way.
    /// </summary>
    public static (bool Saved, string Notice) Save(MySqlNamedConnection connection, string password)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(password);
        var config = connection.Config;
        if (config.InCredentialManager)
        {
            string target = config.CredentialTarget(connection.Name);
            var written = WindowsCredentials.WriteGeneric(target, config.User?.Trim() ?? "", password);
            return written.Error is { } refused ? (false, SqlText.PasswordSaveFailed(connection.Name, refused)) : (true, SqlText.PasswordSavedToCredman(connection.Name, target));
        }

        var encrypted = WindowsCredentials.Protect(password);
        if (encrypted.Error is { } failed)
        {
            return (false, SqlText.PasswordSaveFailed(connection.Name, failed));
        }

        return MySqlConfigFile.WritePassword(connection.Source, connection.Name, encrypted.Value!) is { } error
            ? (false, SqlText.PasswordSaveFailed(connection.Name, error))
            : (true, SqlText.PasswordSavedToFile(connection.Name, connection.Source));
    }
}
