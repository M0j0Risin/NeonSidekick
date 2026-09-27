using System.IO.Compression;
using System.Text;

namespace NeonSidekick.Tests.Fakes;

/// <summary>Zips built in memory for the <c>/skills add</c> tests: codeload's shape (every entry under one root folder, the commit as the comment) or a plain one.</summary>
public static class SkillZip
{
    public const string Commit = "33375500bcea98d610eb30ce10ac4e59b89c390d";

    /// <summary>A <c>SKILL.md</c> for <paramref name="name"/>.</summary>
    public static string SkillMd(string name, string description = "Does a thing. Use when asked.", string body = "# Steps\n\nDo it.", string extra = "") =>
        $"---\nname: {name}\ndescription: {description}\n{extra}---\n\n{body}\n";

    /// <summary>A zip of <paramref name="files"/> (paths with forward slashes) under <paramref name="root"/> (<c>""</c> for none), with <paramref name="comment"/>.</summary>
    public static byte[] Build(IEnumerable<(string Path, string Content)> files, string root = "repo-main/", string? comment = Commit, IEnumerable<string>? links = null)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                var entry = zip.CreateEntry(root + path);
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }

            foreach (string link in links ?? [])
            {
                var entry = zip.CreateEntry(root + link);
                entry.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
                using var stream = entry.Open();
                stream.Write("../../etc/passwd"u8);
            }

            if (comment is not null)
            {
                zip.Comment = comment;
            }
        }

        return buffer.ToArray();
    }

    /// <summary>A repository with the Anthropic layout: <c>skills/&lt;name&gt;/SKILL.md</c> for each name, and a README.</summary>
    public static byte[] Repo(params string[] names) =>
        Build(names.SelectMany(n => new[] { ($"skills/{n}/SKILL.md", SkillMd(n)), ($"skills/{n}/scripts/run.py", "print('hi')\n") })
            .Append(("README.md", "# Skills\n")));
}
