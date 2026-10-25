using System.Text.RegularExpressions;
using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>Linear-time, bounded matching over remote short branch names, never refspecs.</summary>
public static class BaselineBranchPolicy
{
    public const string DefaultPattern = "^master$";

    public static IReadOnlyList<Regex> Compile(IReadOnlyList<string> patterns)
    {
        if (patterns.Count > 16) throw new InvalidOperationException("Configure at most 16 baseline regexes.");
        try
        {
            return patterns.Select(pattern =>
            {
                if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 1024)
                    throw new InvalidOperationException("Baseline regexes must contain 1 to 1024 characters.");
                return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
            }).ToArray();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException("Invalid baseline regex. Use a linear-time expression without backreferences or lookarounds.", exception);
        }
    }

    public static IReadOnlyList<string> Match(IReadOnlyList<string> patterns, IReadOnlyList<string> advertised)
    {
        if (advertised.Count > 10000) throw new InvalidOperationException("Remote branch inventory exceeds 10,000 heads.");
        IReadOnlyList<Regex> regexes = Compile(patterns);
        string[] matches = advertised.Where(branch => GitBranchRequest.IsSafeBranch(branch)
            && regexes.Any(regex => regex.IsMatch(branch))).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).Take(257).ToArray();
        if (matches.Length > 256) throw new InvalidOperationException("Baseline regexes match more than 256 branches; narrow the patterns.");
        return matches;
    }
}
