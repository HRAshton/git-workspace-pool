using System.Security.Cryptography;
using System.Text;

namespace GitWorkspacePool.Core.Domain;

/// <summary>A provider-independent repository and short branch name. No PR or API is required.</summary>
public sealed record GitBranchRequest(string CloneUrl, string Branch)
{
    /// <summary>Gets the shared pool repository identity, preserving case-sensitive Git URL paths.</summary>
    public string RepositoryIdentity => GetRepositoryIdentity(CloneUrl);

    /// <summary>Gets a canonical shared-pool identity for a configured Git clone URL.</summary>
    public static string GetRepositoryIdentity(string cloneUrl) => "git:" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeCloneUrl(cloneUrl)))
    ).ToLowerInvariant();

    /// <summary>Gets a case-sensitive identity, also safe as a Windows folder name.</summary>
    public string WorkspaceName => "git-" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeCloneUrl(CloneUrl) + "\0" + Branch))
    ).ToLowerInvariant();

    /// <summary>Gets whether the request contains a password-free HTTPS or SSH URL and short branch name.</summary>
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

    /// <summary>Normalizes Git host/port and trailing slash, preserving case-sensitive repository paths.</summary>
    public static string NormalizeCloneUrl(string value) =>
        TryNormalizeCloneUrl(value, false, out string? normalized) ? normalized
            : throw new ArgumentException("Use an HTTPS or SSH Git clone URL without passwords, query or fragment.");

    private static bool TryNormalizeCloneUrl(string? value, bool allowUsername, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Contains('\\')
            || value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))) return false;
        // scp-style SSH paths are relative to the remote account; keep them distinct
        // from absolute ssh:// paths instead of silently equating different repositories.
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            var scp = System.Text.RegularExpressions.Regex.Match(value,
                @"^(?<user>[A-Za-z0-9_][A-Za-z0-9_.-]*)@(?<host>[A-Za-z0-9][A-Za-z0-9.-]*):(?<path>[^:]+)$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking,
                TimeSpan.FromMilliseconds(100));
            if (!scp.Success || !SafeSshPath(scp.Groups["path"].Value)) return false;
            normalized = scp.Groups["user"].Value + "@" + scp.Groups["host"].Value.ToLowerInvariant()
                + ":" + scp.Groups["path"].Value.TrimEnd('/');
            return true;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("https" or "ssh") || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.Host.Length == 0 || uri.Host.StartsWith('-')
            || (uri.Scheme == "https" && !allowUsername && uri.UserInfo.Length != 0)
            || Uri.UnescapeDataString(uri.UserInfo).Contains(':')
            || uri.UserInfo.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.'))
            || uri.UserInfo.StartsWith('-') || uri.AbsolutePath.Trim('/').Length == 0) return false;
        if (uri.Scheme == "ssh")
        {
            if (!SafeSshPath(Uri.UnescapeDataString(uri.AbsolutePath))) return false;
            // An omitted port may come from SSH config. Inspect the supplied authority
            // rather than Uri's default-port canonicalization so explicit :22 stays distinct.
            string authority = value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/', 2)[0];
            string hostAndPort = authority[(authority.LastIndexOf('@') + 1)..];
            bool explicitPort = hostAndPort.StartsWith('[')
                ? hostAndPort.LastIndexOf(']') + 1 < hostAndPort.Length
                : hostAndPort.Contains(':');
            if (explicitPort)
            {
                string digits = hostAndPort[(hostAndPort.LastIndexOf(':') + 1)..];
                if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) return false;
            }
            string port = explicitPort ? ":" + uri.Port : "";
            normalized = "ssh://" + (uri.UserInfo.Length == 0 ? "" : uri.UserInfo + "@")
                + (uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.IdnHost.Trim('[', ']') + "]" : uri.IdnHost.ToLowerInvariant()) + port + uri.AbsolutePath.TrimEnd('/');
        }
        else normalized = uri.Scheme + "://" + uri.Authority.ToLowerInvariant() + uri.AbsolutePath.TrimEnd('/');
        return true;
    }

    private static bool SafeSshPath(string path) => path.Trim('/').Length > 0
        && !path.StartsWith('-')
        && !path.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)
            || "\"'`$;&|<>?#(){}[]".Contains(character));
}
