using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace GitWorkspacePool.Core.Domain;

/// <summary>
/// A validated pull request reference from an accepted web provider.
/// </summary>
public sealed record PullRequestReference(string Provider, string Owner, string Repository, int Number)
{
    /// <summary>
    /// Gets the repository key used for local matching.
    /// </summary>
    public string RepositoryKey => $"{Owner}/{Repository}";

    /// <summary>
    /// Parses a GitHub pull request URL, including its files or commits page.
    /// </summary>
    /// <param name="value">The untrusted URL supplied by the browser.</param>
    /// <param name="reference">The parsed pull request, if accepted.</param>
    /// <returns>Whether the URL identifies a supported pull request.</returns>
    public static bool TryParse(string? value, [NotNullWhen(true)] out PullRequestReference? reference)
    {
        reference = null;

        if (
            string.IsNullOrWhiteSpace(value)
            || value.Length > 4096
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0
        )
        {
            return false;
        }

        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (
            segments.Length < 4
            || segments[2] != "pull"
            || !IsSafeSegment(segments[0])
            || !IsSafeSegment(segments[1])
            || !int.TryParse(
                segments[3],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int number
            )
            || number <= 0
        )
        {
            return false;
        }

        reference = new PullRequestReference("github", segments[0], segments[1], number);
        return true;
    }

    private static bool IsSafeSegment(string segment)
    {
        return segment.Length is > 0 and <= 100
            && segment != "."
            && segment != ".."
            && segment.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'
            );
    }
}
