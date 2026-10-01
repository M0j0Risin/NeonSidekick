using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Unc;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>unc_shares(check?)</c> (2026-09-30): the named shares of <c>unc.json</c> — where each points, who it is reached as, read-only
/// or read-write, what it holds, the default marked; never a password. Touches no share unless <c>check</c> asks: then every
/// share's root is listed under its own account, at once, each bounded by <see cref="UncAccess.ReachTimeout"/>.
/// </summary>
public sealed class UncSharesTool : UncTool
{
    public const string ToolName = "unc_shares";
    public const string CheckArgument = "check";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "check": { "type": "boolean", "description": "true to try every share now and say whether it answers (slower; a share that does not answer takes up to 10 s). Default false." }
          }
        }
        """);

    public UncSharesTool(UncAccess unc, Func<AppSettingsData> effective) : base(unc, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the network shares and outside folders the user has set up for the unc_ tools: each one's name, path, the account it is reached as, whether it is read-only or read-write, and what it holds, the default marked. " +
        "Pass a name as \"share\" to the other unc_ tools, or a full path under one. check true tries each share now.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> DescribeAsync(bool check, CancellationToken cancellationToken)
    {
        var catalog = Unc.Catalog();
        var effective = Effective;
        if (!check || catalog.Shares.Count == 0)
        {
            return UncText.Shares(catalog, effective.UncDefaultShare, effective.UncWrites);
        }

        var probes = catalog.Shares.Select(async share =>
        {
            var result = await Unc.RunAsync(share, write: false, files => files.List("", Files.WorkingDirectory.MaxListLimit).Entries.Count, cancellationToken).ConfigureAwait(false);
            return (share.Name, Reach: result.Error ?? UncText.Reached(result.Value));
        });
        var reach = (await Task.WhenAll(probes).ConfigureAwait(false)).ToDictionary(r => r.Name, r => r.Reach, StringComparer.OrdinalIgnoreCase);
        return UncText.Shares(catalog, effective.UncDefaultShare, effective.UncWrites, reach);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadBoolean(arguments, CheckArgument, out var check, out var raw))
        {
            return Files.FileText.BadBoolean(CheckArgument, raw);
        }

        return await DescribeAsync(check ?? false, cancellationToken).ConfigureAwait(false);
    }
}
