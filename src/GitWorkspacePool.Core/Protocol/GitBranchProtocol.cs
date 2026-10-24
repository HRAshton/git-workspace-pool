using System.Diagnostics.CodeAnalysis;
using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Protocol;

/// <summary>Provider-independent v2 protocol: prworkspace://open/?v=2&amp;repo=...&amp;branch=...</summary>
public static class GitBranchProtocol
{
    /// <summary>Parses only an exact v2 repo/branch payload. This does not grant repository trust.</summary>
    public static bool TryParse(string? raw, [NotNullWhen(true)] out GitBranchRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 8192
            || !Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != "prworkspace" || !uri.Host.Equals("open", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Port != -1 || uri.Fragment.Length != 0)
            return false;
        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        foreach (string part in uri.Query.TrimStart('?').Split('&'))
        {
            string[] entry = part.Split('=', 2);
            if (entry.Length != 2 || entry[0] is not ("v" or "repo" or "branch")
                || !fields.TryAdd(entry[0], Uri.UnescapeDataString(entry[1]))) return false;
        }
        if (fields.Count != 3 || fields.GetValueOrDefault("v") != "2"
            || !fields.TryGetValue("repo", out string? repo) || !fields.TryGetValue("branch", out string? branch))
            return false;
        GitBranchRequest parsed = new(repo, branch);
        if (!parsed.IsValid) return false;
        request = parsed;
        return true;
    }
}
