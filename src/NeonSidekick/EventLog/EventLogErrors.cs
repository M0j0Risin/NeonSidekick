using System.Globalization;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.EventLog;

/// <summary>What a failed read was doing, for its sentence: the computer's name, the log, and for a remote one the host and the account.</summary>
public sealed record EventLogErrorContext(string Computer, string? Log = null, bool Remote = false, string? Host = null, string? Account = null, bool File = false);

/// <summary>
/// A Win32 code from the event-log reader as the sentence the model and the user read (2026-10-15, the event-log plan) — from the
/// code, never the message text, which Windows words in its own UI language: 5 (and how to be let in), no such log or file, a query
/// the log refused, an Analytic or Debug log, the term budget, an unreachable host (the firewall rule), a bad sign-in, an account
/// refused (<c>UncAccess</c>'s set), a timeout or a cancel. A refused query is logged at Warn. Pure but for that line.
/// </summary>
public static class EventLogErrors
{
    /// <summary>The sentence for <paramref name="code"/> in <paramref name="context"/>; <paramref name="detail"/> (the raw message) closes the fallback.</summary>
    public static string Sentence(int code, EventLogErrorContext context, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        string log = context.Log ?? "the";
        switch (code)
        {
            case 5:
                return context.Remote
                    ? EventLogText.RemoteAccessDenied(context.Log, context.Host ?? context.Computer, context.Account ?? EventLogText.YouAccount)
                    : EventLogText.AccessDenied(context.Log, context.Computer);
            case 2 or 3 when context.File:
                return EventLogText.FileMissing(context.Log ?? "");
            case 2 or 3 or 15000 or 15007:
                return EventLogText.NoSuchLog(context.Log ?? "", context.Computer);
            case 15001 or 15016 or 15017 or 15018 or 15019 or 15020 or 15021:
                DiagnosticLog.Warn(EventLogText.Category, EventLogText.RefusedQueryLogLine(code, log));
                return EventLogText.QueryRefused;
            case 15022 or 50:
                return EventLogText.NotSearchable(context.Log ?? "");
            case 15026:
                return EventLogText.TooManyTerms(EventLogXPath.MaxTerms + 1, EventLogXPath.MaxTerms);
            case 1722 or 1753 or 1727 or 53 or 1702 or 1706:
                return EventLogText.Unreachable(context.Host ?? context.Computer);
            case 1326 or 86:
                return EventLogText.BadSignIn(context.Host ?? context.Computer, context.Account ?? EventLogText.YouAccount);
            case 1311:
                return EventLogText.NoLogonServer(context.Host ?? context.Computer);
            case 1385 or 1327 or 1330 or 1331 or 1907 or 1909:
                return EventLogText.AccountRefused(context.Host ?? context.Computer, context.Account ?? EventLogText.YouAccount, LogText.Excerpt(detail ?? ""));
            case 1734:
                return EventLogText.EventTooLarge;
            case 1223:
                return EventLogText.Canceled;
            case 1460 or 258:
                return EventLogText.TimedOut(context.Computer);
            default:
                return EventLogText.Failed(context.Computer, code.ToString(CultureInfo.InvariantCulture), LogText.Excerpt(detail ?? ""));
        }
    }
}
