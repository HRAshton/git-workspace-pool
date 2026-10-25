using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Infrastructure;

/// <summary>
/// Strict GitHub remote URL identity matching for HTTPS and SSH origins.
/// </summary>
public static class GitHubRemoteIdentity
{
    /// <summary>
    /// Checks that a remote URL identifies exactly the requested GitHub repository.
    /// </summary>
    /// <param name="originUrl">Resolved Git origin URL.</param>
    /// <param name="request">Validated PR identity.</param>
    /// <returns>Whether the remote identifies the requested repository.</returns>
    public static bool Matches(string originUrl, PullRequestReference request)
    {
        if (
            request.Provider != "github"
            || !TryParse(originUrl, out string owner, out string repository)
        )
        {
            return false;
        }

        return owner.Equals(request.Owner, StringComparison.OrdinalIgnoreCase)
            && repository.Equals(request.Repository, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParse(string remote, out string owner, out string repository)
    {
        owner = string.Empty;
        repository = string.Empty;
        string path;

        if (remote.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            path = remote["git@github.com:".Length..];
        }
        else
        {
            if (!Uri.TryCreate(remote, UriKind.Absolute, out Uri? uri))
            {
                return false;
            }

            bool https = uri.Scheme == Uri.UriSchemeHttps
                && uri.IsDefaultPort
                && uri.UserInfo.Length == 0;
            bool ssh = uri.Scheme == "ssh"
                && (uri.IsDefaultPort || uri.Port == 22)
                && uri.UserInfo == "git";

            if (
                !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || (!https && !ssh)
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment)
            )
            {
                return false;
            }

            path = uri.AbsolutePath.Trim('/');
        }

        string[] segments = path.TrimEnd('/').Split('/');

        if (segments.Length != 2)
        {
            return false;
        }

        owner = segments[0];
        repository = segments[1];

        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repository = repository[..^4];
        }

        return Valid(owner) && Valid(repository);
    }

    private static bool Valid(string value) =>
        value.Length is > 0 and <= 100
        && value != "."
        && value != ".."
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'
        );
}
