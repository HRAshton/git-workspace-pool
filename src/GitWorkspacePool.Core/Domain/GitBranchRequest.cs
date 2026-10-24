using System.Security.Cryptography;
using System.Text;

namespace GitWorkspacePool.Core.Domain;

/// <summary>A provider-independent repository and short branch name. No PR or API is required.</summary>
public sealed record GitBranchRequest(string CloneUrl, string Branch)
{
    /// <summary>Gets a case-sensitive identity, also safe as a Windows folder name.</summary>
    public string WorkspaceName => "git-" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeCloneUrl(CloneUrl) + "\0" + Branch))
    ).ToLowerInvariant();

    /// <summary>Gets whether the request contains a credential-free HTTPS URL and short branch name.</summary>
    public bool IsValid => TryNormalizeCloneUrl(CloneUrl, false, out _)
        && IsSafeBranch(Branch);

    /// <summary>Checks the branch syntax before passing it to Git check-ref-format.</summary>
    public static bool IsSafeBranch(string? value) => value is { Length: > 0 and <= 1024 }
        && value != "@" && !value.StartsWith('/') && !value.EndsWith('/')
        && !value.Contains("..", StringComparison.Ordinal)
        && !value.Contains("//", StringComparison.Ordinal)
        && !value.Contains("@{", StringComparison.Ordinal)
        && !value.Any(c => c <= ' ' || c == 127 || "~^:?*[\\".Contains(c))
        && value.Split('/').All(part => !part.StartsWith('.') && !part.EndsWith('.')
            && !part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));

    /// <summary>Checks an origin URL against a credential-free registered clone URL.</summary>
    public static bool OriginMatches(string origin, string registered) =>
        TryNormalizeCloneUrl(origin, true, out string? actual)
        && TryNormalizeCloneUrl(registered, false, out string? expected)
        && actual.Equals(expected, StringComparison.Ordinal);

    /// <summary>Normalizes HTTPS host/port and trailing slash, preserving case-sensitive repository paths.</summary>
    public static string NormalizeCloneUrl(string value) =>
        TryNormalizeCloneUrl(value, false, out string? normalized) ? normalized
            : throw new ArgumentException("Use an HTTPS Git clone URL without credentials, query or fragment.");

    private static bool TryNormalizeCloneUrl(string? value, bool allowUsername, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Contains('\\')
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || (!allowUsername && uri.UserInfo.Length != 0)
            || Uri.UnescapeDataString(uri.UserInfo).Contains(':')
            || uri.AbsolutePath.Trim('/').Length == 0)
        {
            return false;
        }
        normalized = uri.Scheme + "://" + uri.Authority.ToLowerInvariant() + uri.AbsolutePath.TrimEnd('/');
        return true;
    }
}
