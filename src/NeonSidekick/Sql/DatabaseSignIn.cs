using System.Runtime.ExceptionServices;
using NeonSidekick.Computers;

namespace NeonSidekick.Sql;

/// <summary>
/// The database families' side of <see cref="SignInGuard"/> (2026-10-24, the database review's K1, mechanism 2): every open of the SQL Server,
/// Oracle, MySQL and PostgreSQL tools — a query, a change, the boundary's names read, the wizards' Test — goes through it, so a password a
/// server refused is not sent again until it changes (a held call is refused before anything is sent), and while a password is unproven its
/// sign-ins go one at a time. Nothing of the password is kept: the hold key is a hash of the family, the place, the account and the password,
/// so a new password is tried at once. Each family says which of its driver's failures means a refused sign-in. Those codes (SQL Server's 18456,
/// MySQL's 1045) also cover a disabled login or one not yet given its database, so a hold lapses after <see cref="HoldFor"/> (the review of these
/// fixes, 2026-10-24): one try a quarter hour stays far under any lockout policy, and a fix on the server is seen without a restart.
/// </summary>
public static class DatabaseSignIn
{
    /// <summary>How long a refused password is held before one more try.</summary>
    public static readonly TimeSpan HoldFor = TimeSpan.FromMinutes(15);

    /// <summary>The guard's facts for one family's sign-in at <paramref name="where"/>; no hold without a password (a Windows sign-in).</summary>
    public static SignInCall Call(string family, string where, string? user, string? password, Func<Exception, bool> wrongPassword)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(where);
        ArgumentNullException.ThrowIfNull(wrongPassword);
        string place = family + ":" + where.Trim().ToLowerInvariant();
        return new SignInCall(!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(password) ? SignInGuard.HoldKey(place, user, password) : null, place, wrongPassword, HoldFor);
    }

    /// <summary>
    /// Runs <paramref name="open"/> under the guard: true when the password is held (nothing was sent), false once open; the open's own
    /// exception is thrown again as it was, so each family's catches stay as they are.
    /// </summary>
    public static async Task<bool> OpenAsync(SignInCall call, Func<CancellationToken, Task> open, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(open);
        var run = await SignInGuard.OpenAsync(call, async token =>
        {
            await open(token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        if (run.Stop == SignInStop.Held)
        {
            return true;
        }

        if (run.Failure is { } failure)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        return false;
    }
}
