using System.Diagnostics.CodeAnalysis;
using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Protocol;

/// <summary>Provider-independent protocol: prworkspace://open/?repo=...&amp;branch=...</summary>
public static class GitBranchProtocol
{
    /// <summary>Parses only an exact repo/branch payload. This does not grant repository trust.</summary>
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
            if (entry.Length != 2 || entry[0] is not ("repo" or "branch")
                || !fields.TryAdd(entry[0], Uri.UnescapeDataString(entry[1]))) return false;
        }
        if (fields.Count != 2
            || !fields.TryGetValue("repo", out string? repo) || !fields.TryGetValue("branch", out string? branch))
            return false;
        GitBranchRequest parsed = new(repo, branch);
        if (!parsed.IsValid) return false;
        request = parsed;
        return true;
    }
}
