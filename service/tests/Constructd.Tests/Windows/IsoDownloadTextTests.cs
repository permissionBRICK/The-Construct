using System.Globalization;
using Constructd.Windows.Iso;

namespace Constructd.Tests.Windows;

/// <summary>The ISO download's progress line reads the same on every host (F34): invariant numbers, GiB above 1024 MiB.</summary>
public sealed class IsoDownloadTextTests
{
    [Fact]
    public void A_german_host_prints_the_same_line_as_any_other()
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var line = HttpIsoDownloader.ProgressLine("download", 2_272_000_000, 4_080_000_000, 11.2 * 1024 * 1024, 8, 0);
            Assert.Equal("host ISO download: 2.1 / 3.8 GiB (55.7%) | 11.2 MiB/s | 8 streams | 0 retries | ETA 154s", line);
            Assert.Equal("512 MiB", HttpIsoDownloader.Size(512L << 20));
            Assert.Equal("1.5 GiB", HttpIsoDownloader.Size(3L << 29));
            Assert.Equal("host ISO download: 300 / 900 MiB (33.3%) | 0.0 MiB/s | 2 streams | 1 retries",
                HttpIsoDownloader.ProgressLine("download", 300L << 20, 900L << 20, 0, 2, 1));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }
}
