using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Snappo.Updates;

internal static class ReleaseChecker
{
    internal const string ReleasesPage = "https://github.com/1lyst4r/Snappo/releases";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    internal static Version ParseVersion(string? tag)
    {
        if (tag?.StartsWith("v", StringComparison.OrdinalIgnoreCase) == true) tag = tag[1..];
        if (!Version.TryParse(tag, out var version)) throw new InvalidDataException("The release tag isn't a version number.");
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }

    internal static async Task<(Version Version, string Tag)> GetLatestAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/1lyst4r/Snappo/releases/latest");
        request.Headers.UserAgent.ParseAdd("Snappo-Update-Checker/1.0");
        using var response = await Client.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        string tag = release.RootElement.GetProperty("tag_name").GetString() ?? "";
        return (ParseVersion(tag), tag);
    }
}
