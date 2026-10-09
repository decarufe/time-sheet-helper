using TimeSheetHelper.Collectors.DevOps.Common;

namespace TimeSheetHelper.Collectors.DevOps.LocalGit;

public sealed record GitCommit(string Sha, DateTime AuthorDateUtc, string? Subject);

/// <summary>Parses <c>git log --format=<see cref="Format"/></c> output: unit-separated fields, record-separated commits.</summary>
public static class GitLogParser
{
    public const string Format = "%H%x1f%aI%x1f%s%x1e";

    public static IEnumerable<GitCommit> Parse(string output)
    {
        foreach (var record in output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = record.Split('\x1f');
            if (fields.Length < 2 || fields[0].Length < 7 || JsonExtensions.ParseUtc(fields[1]) is not { } authored)
            {
                continue;
            }
            yield return new GitCommit(fields[0], authored, fields.Length > 2 ? JsonExtensions.FirstLine(fields[2]) : null);
        }
    }
}
