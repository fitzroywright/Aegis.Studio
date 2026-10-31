using System.Text.RegularExpressions;

namespace Aegis.Studio.Web.Authentication;

/// <summary>Pending accounts may load the shared presentation, but no business APIs.</summary>
public static class StudioAccessPresentation
{
    public static string[] PendingPaths(string webRootPath)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase)
        {
            "/", "/index.html", "/login.html", "/login.js", "/api/auth/me", "/api/auth/logout",
            "/thinking-machines-letterhead.png", "/thinking-machines-about-once.gif"
        };
        // Follow only local static references in the canonical shell; never allow an API path.
        string shell = File.ReadAllText(Path.Combine(webRootPath, "index.html"));
        foreach (Match match in Regex.Matches(shell, "(?:src|href)=\"(/[^\"?#]+)(?:[?][^\"]*)?\""))
        {
            string path = match.Groups[1].Value;
            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("//", StringComparison.Ordinal)) continue;
            if (Path.GetExtension(path).ToLowerInvariant() is ".js" or ".css" or ".svg" or ".png" or ".gif") paths.Add(path);
        }
        return paths.ToArray();
    }
}
