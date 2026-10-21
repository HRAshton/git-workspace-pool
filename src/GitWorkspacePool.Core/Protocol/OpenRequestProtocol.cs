using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Protocol;

public static class OpenRequestProtocol
{
    public static bool TryExtract(string? raw, out string requestUrl)
    {
        requestUrl = string.Empty;

        if (
            string.IsNullOrWhiteSpace(raw)
            || raw.Length > 8192
            || !Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri)
            || !uri.Scheme.Equals("prworkspace", StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("open", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath != "/"
            || uri.UserInfo.Length != 0
            || !string.IsNullOrEmpty(uri.Fragment)
        )
        {
            return false;
        }

        string? encodedUrl = null;
        string? version = null;

        foreach (string part in uri.Query.TrimStart('?').Split('&'))
        {
            string[] entry = part.Split('=', 2);

            if (entry.Length != 2)
            {
                return false;
            }

            if (entry[0] == "url" && encodedUrl is null)
            {
                encodedUrl = Uri.UnescapeDataString(entry[1]);
            }
            else if (entry[0] == "v" && version is null)
            {
                version = entry[1];
            }
            else
            {
                return false;
            }
        }

        if (version != "1" || !PullRequestReference.TryParse(encodedUrl, out _))
        {
            return false;
        }

        requestUrl = encodedUrl!;
        return true;
    }
}
