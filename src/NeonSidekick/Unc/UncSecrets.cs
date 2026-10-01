using NeonSidekick.Sql;

namespace NeonSidekick.Unc;

/// <summary>
/// The password a <c>runas</c> share signs in with (2026-09-30), <see cref="SqlSecrets"/>' part for the UNC tools: under
/// <c>passwordStore: credman</c> the Windows Credential Manager entry <see cref="UncShareConfig.CredentialTarget"/>; under
/// <c>file</c> (the default) the <c>password</c> of <c>unc.json</c> decrypted — or, while it is still plain text (a file the app
/// could not rewrite), as it stands. A <c>windows</c> share needs none. A missing or undecryptable password is the sentence that
/// names the fix, never a throw.
/// </summary>
public static class UncSecrets
{
    public static CredentialResult Resolve(UncNamedShare share)
    {
        ArgumentNullException.ThrowIfNull(share);
        var config = share.Config;
        if (!config.NeedsPassword)
        {
            return CredentialResult.Ok("");
        }

        if (config.InCredentialManager)
        {
            string target = config.CredentialTarget(share.Name);
            var read = WindowsCredentials.ReadGeneric(target);
            return read.NotFound ? CredentialResult.Failed(UncText.NoCredential(target)) : read;
        }

        string password = config.Password ?? "";
        if (password.Length == 0)
        {
            return CredentialResult.Failed(UncText.NoPassword(share.Name));
        }

        if (!WindowsCredentials.IsProtected(password))
        {
            return CredentialResult.Ok(password);
        }

        // WindowsCredentials words its failure for sql.json; the fix it names is on the UNC tab here.
        var decrypted = WindowsCredentials.Unprotect(password);
        return decrypted.Error is { } error ? CredentialResult.Failed(error.Replace("the SQL tab", "the UNC tab", StringComparison.Ordinal)) : decrypted;
    }

    /// <summary>
    /// Saves <paramref name="password"/> for <paramref name="share"/> in its store (the UNC tab's masked prompt and the wizard):
    /// under <c>credman</c> the Credential Manager entry, written with the share's user; under <c>file</c> DPAPI-encrypted into the
    /// <c>unc.json</c> the share came from. The status line's sentence either way.
    /// </summary>
    public static (bool Saved, string Notice) Save(UncNamedShare share, string password)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(password);
        var config = share.Config;
        if (config.InCredentialManager)
        {
            string target = config.CredentialTarget(share.Name);
            var written = WindowsCredentials.WriteGeneric(target, config.User?.Trim() ?? "", password);
            return written.Error is { } refused ? (false, SqlText.PasswordSaveFailed(share.Name, refused)) : (true, SqlText.PasswordSavedToCredman(share.Name, target));
        }

        var encrypted = WindowsCredentials.Protect(password);
        if (encrypted.Error is { } failed)
        {
            return (false, SqlText.PasswordSaveFailed(share.Name, failed));
        }

        return UncConfigFile.WritePassword(share.Source, share.Name, encrypted.Value!) is { } error
            ? (false, SqlText.PasswordSaveFailed(share.Name, error))
            : (true, SqlText.PasswordSavedToFile(share.Name, share.Source));
    }
}
