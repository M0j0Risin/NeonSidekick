namespace NeonSidekick.Skills;

/// <summary>What <c>/skills add</c> was given, as <see cref="SkillSource.TryParse"/> reads it.</summary>
public enum SkillSourceKind
{
    /// <summary>Words to look up on skills.sh (<see cref="SkillHub.SearchAsync"/>).</summary>
    Search,

    /// <summary>A GitHub repository, its archive fetched from codeload.</summary>
    GitHub,

    /// <summary>Any other https URL of a <c>.zip</c>.</summary>
    Zip,
}

/// <summary>
/// Where <c>/skills add</c> takes a skill from (2026-09-26, the user's ask: search and download
/// Agent Skills into the app). Four spellings: words to search skills.sh for (<c>pdf</c>,
/// <c>excel reports</c>); <c>owner/repo</c> — every skill in the repository, picked from a list when
/// there are several; <c>owner/repo/skill</c> — one of them, skills.sh's own id form, so a search
/// result can be typed back; a github.com URL (<c>/tree/&lt;ref&gt;/&lt;path&gt;</c> narrows it to a
/// folder, <c>/blob/&lt;ref&gt;/&lt;path&gt;/SKILL.md</c> to one skill); and an https URL of a <c>.zip</c>.
/// The repository is always downloaded whole — codeload's zip of the ref, one request, no API and
/// no rate limit — and the path only narrows where the archive is searched; a branch name with a
/// <c>/</c> in it cannot be told from the path after it and is not supported (the first segment
/// after <c>tree</c> is the ref). Pure.
/// </summary>
public sealed record SkillSource(SkillSourceKind Kind, string? Query = null, string? Owner = null, string? Repo = null, string Ref = SkillSource.DefaultRef, string? SubPath = null, string? SkillId = null, Uri? ZipUrl = null)
{
    /// <summary>The ref codeload resolves to the default branch.</summary>
    public const string DefaultRef = "HEAD";

    public const string GitHubHost = "github.com";
    public const string CodeloadBase = "https://codeload.github.com/";

    /// <summary>The flags, pinned: <c>--global</c> / <c>--profile</c> move the pane's first cursor, <c>--yes</c> is headless's consent.</summary>
    public const string GlobalFlag = "--global";
    public const string ProfileFlag = "--profile";
    public const string YesFlag = "--yes";

    /// <summary>The archive to download: codeload's zip of the ref for a repository, the URL itself for a zip.</summary>
    public Uri? ArchiveUrl => Kind switch
    {
        SkillSourceKind.GitHub => new Uri(CodeloadBase + Owner + "/" + Repo + "/zip/" + Uri.EscapeDataString(Ref)),
        SkillSourceKind.Zip => ZipUrl,
        _ => null,
    };

    /// <summary><c>owner/repo</c>, or the zip's URL: what the provenance calls the source.</summary>
    public string RepoName => Kind == SkillSourceKind.GitHub ? Owner + "/" + Repo : ZipUrl?.AbsoluteUri ?? "";

    /// <summary>How the preview names it: <c>owner/repo@ref</c> (the ref only when one was given) or the zip's URL.</summary>
    public string Label => Kind switch
    {
        SkillSourceKind.GitHub => RepoName + (Ref == DefaultRef ? "" : "@" + Ref),
        SkillSourceKind.Zip => ZipUrl!.AbsoluteUri,
        _ => Query ?? "",
    };

    /// <summary>
    /// Reads what followed <c>/skills add</c> (the flags already split off, <see cref="SplitFlags"/>).
    /// False with the error for a blank argument, a URL that is neither GitHub nor a zip, and a
    /// repository spelling with characters GitHub never allows.
    /// </summary>
    public static bool TryParse(string text, out SkillSource? source, out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        source = null;
        error = null;
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            error = SkillInstallText.UsageError;
            return false;
        }

        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            return TryParseUrl(trimmed, out source, out error);
        }

        if (trimmed.Any(char.IsWhiteSpace) || !trimmed.Contains('/', StringComparison.Ordinal))
        {
            source = new SkillSource(SkillSourceKind.Search, Query: trimmed);
            return true;
        }

        // github.com/owner/repo typed without a scheme.
        if (trimmed.StartsWith(GitHubHost + "/", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseUrl("https://" + trimmed, out source, out error);
        }

        string[] parts = trimmed.Split('/');
        if (parts.Length is < 2 or > 3 || !IsSegment(parts[0]) || !IsSegment(StripGit(parts[1])))
        {
            error = SkillInstallText.BadRepoError(trimmed);
            return false;
        }

        string? skillId = null;
        if (parts.Length == 3)
        {
            if (!SkillFrontmatter.IsValidName(parts[2]))
            {
                error = SkillInstallText.BadRepoError(trimmed);
                return false;
            }

            skillId = parts[2];
        }

        source = new SkillSource(SkillSourceKind.GitHub, Owner: parts[0], Repo: StripGit(parts[1]), SkillId: skillId);
        return true;
    }

    private static bool TryParseUrl(string text, out SkillSource? source, out string? error)
    {
        source = null;
        error = null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            error = SkillInstallText.UnsupportedUrlError(text);
            return false;
        }

        if (url.Host.Equals(GitHubHost, StringComparison.OrdinalIgnoreCase) || url.Host.Equals("www." + GitHubHost, StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = url.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
            if (parts.Length < 2 || !IsSegment(parts[0]) || !IsSegment(StripGit(parts[1])))
            {
                error = SkillInstallText.UnsupportedUrlError(text);
                return false;
            }

            string owner = parts[0];
            string repo = StripGit(parts[1]);
            if (parts.Length == 2)
            {
                source = new SkillSource(SkillSourceKind.GitHub, Owner: owner, Repo: repo);
                return true;
            }

            if (parts.Length < 4 || parts[2] is not ("tree" or "blob"))
            {
                error = SkillInstallText.UnsupportedUrlError(text);
                return false;
            }

            var rest = parts.Skip(4).ToList();
            if (parts[2] == "blob")
            {
                // A link to the SKILL.md itself: the folder it sits in.
                if (rest.Count == 0 || !rest[^1].Equals(SkillCatalog.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    error = SkillInstallText.UnsupportedUrlError(text);
                    return false;
                }

                rest.RemoveAt(rest.Count - 1);
            }

            if (rest.Any(segment => segment is "." or ".."))
            {
                error = SkillInstallText.UnsupportedUrlError(text);
                return false;
            }

            source = new SkillSource(SkillSourceKind.GitHub, Owner: owner, Repo: repo, Ref: parts[3], SubPath: rest.Count == 0 ? null : string.Join('/', rest));
            return true;
        }

        if (url.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            source = new SkillSource(SkillSourceKind.Zip, ZipUrl: url);
            return true;
        }

        error = SkillInstallText.UnsupportedUrlError(text);
        return false;
    }

    /// <summary>
    /// The argument less its flags, wherever they stand: the scope asked for (the last of
    /// <c>--global</c> / <c>--profile</c> wins) and whether <c>--yes</c> was given. Other words are kept in order.
    /// </summary>
    public static (string Text, SkillScope? Scope, bool Yes) SplitFlags(string args)
    {
        ArgumentNullException.ThrowIfNull(args);
        SkillScope? scope = null;
        bool yes = false;
        var kept = new List<string>();
        foreach (string word in args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (word.Equals(GlobalFlag, StringComparison.OrdinalIgnoreCase))
            {
                scope = SkillScope.Global;
            }
            else if (word.Equals(ProfileFlag, StringComparison.OrdinalIgnoreCase))
            {
                scope = SkillScope.Profile;
            }
            else if (word.Equals(YesFlag, StringComparison.OrdinalIgnoreCase))
            {
                yes = true;
            }
            else
            {
                kept.Add(word);
            }
        }

        return (string.Join(' ', kept), scope, yes);
    }

    /// <summary>A GitHub owner or repository name: letters, digits, <c>-</c>, <c>_</c> and <c>.</c>, not <c>.</c> or <c>..</c>.</summary>
    internal static bool IsSegment(string text) =>
        text.Length > 0 && text is not ("." or "..") && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static string StripGit(string repo) =>
        repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase) && repo.Length > 4 ? repo[..^4] : repo;
}
