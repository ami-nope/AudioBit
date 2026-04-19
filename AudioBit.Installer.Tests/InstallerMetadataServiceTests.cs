using AudioBit.Installer;
using Xunit;

namespace AudioBit.Installer.Tests;

public sealed class InstallerMetadataServiceTests
{
    [Fact]
    public void ParseLatestReleaseVersion_UsesTagName()
    {
        const string json = """
            {
              "tag_name": "2.10",
              "name": "Release 2.10"
            }
            """;

        var version = InstallerMetadataService.ParseLatestReleaseVersion(json);

        Assert.Equal("2.10.0", version);
    }

    [Fact]
    public void ParseGitHubStars_ReadsRepositoryCount()
    {
        const string json = """
            {
              "stargazers_count": 1384
            }
            """;

        var stars = InstallerMetadataService.ParseGitHubStars(json);

        Assert.Equal(1384, stars);
    }

    [Fact]
    public void ParseWebsiteStats_ReadsAggregateRatingJsonLd()
    {
        const string html = """
            <html>
              <head>
                <script type="application/ld+json">
                {
                  "@context": "https://schema.org",
                  "@type": "SoftwareApplication",
                  "aggregateRating": {
                    "@type": "AggregateRating",
                    "ratingValue": "4.9",
                    "ratingCount": "248",
                    "bestRating": "5"
                  },
                  "stars": "1132"
                }
                </script>
              </head>
            </html>
            """;

        var stats = InstallerMetadataService.ParseWebsiteStats(html);

        Assert.NotNull(stats);
        Assert.Equal(4.9, stats!.RatingValue);
        Assert.Equal(248, stats.RatingCount);
        Assert.Equal(1132, stats.StarsCount);
        Assert.Equal(5d, stats.BestRating);
    }

    [Fact]
    public void ParseWebsiteStats_ReturnsNullForBlockedPage()
    {
        const string html = """
            <html>
              <body>
                <h1>Web Page Blocked</h1>
                <p>Category: high-risk</p>
              </body>
            </html>
            """;

        var stats = InstallerMetadataService.ParseWebsiteStats(html);

        Assert.Null(stats);
    }
}
