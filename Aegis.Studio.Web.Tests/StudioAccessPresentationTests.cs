using Aegis.Studio.Web.Authentication;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class StudioAccessPresentationTests
{
    [Fact]
    public void PendingShellAllowsOnlyItsStaticReferencesAndIdentityEndpoints()
    {
        string directory = Path.Combine(Path.GetTempPath(), "studio-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "index.html"), """
                <script src="/studio-access.js?v=1"></script>
                <link href="/css/components/studio-page.css?v=1" rel="stylesheet">
                <img src="/studio-combined-logo.svg">
                <a href="/api/jobs">Jobs</a><script src="/api/admin/data.js"></script>
                <script src="//external.example/script.js"></script>
                <a href="/private.html">Private</a>
                """);
            string[] paths = StudioAccessPresentation.PendingPaths(directory);
            Assert.Contains("/", paths);
            Assert.Contains("/index.html", paths);
            Assert.Contains("/studio-access.js", paths);
            Assert.Contains("/css/components/studio-page.css", paths);
            Assert.Contains("/studio-combined-logo.svg", paths);
            Assert.Contains("/api/auth/me", paths);
            Assert.Contains("/api/auth/logout", paths);
            Assert.DoesNotContain("/private.html", paths);
            Assert.DoesNotContain(paths, path => path.StartsWith("//"));
            Assert.Equal(new[] { "/api/auth/logout", "/api/auth/me" },
                paths.Where(path => path.StartsWith("/api/")).OrderBy(path => path).ToArray());
        }
        finally { Directory.Delete(directory, true); }
    }
}
