using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HyperVManagerTray.Services;

/// <summary>
/// Checks the GitHub Releases API to determine whether a newer version of the app is available.
/// </summary>
internal sealed class UpdateChecker(HttpClient http, ILogger<UpdateChecker> logger)
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/ezpl/HyperVManagerTray/releases/latest";

    /// <summary>
    /// Queries the GitHub Releases API for the latest release and compares it to the running version.
    /// Never throws — returns <c>UpdateAvailable = false</c> on any failure.
    /// </summary>
    public async Task<(bool UpdateAvailable, string LatestVersion, string ReleaseUrl)> CheckAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("HyperVManagerTray", null));

            using var cts      = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);

            var root       = doc.RootElement;
            var tagName    = root.GetProperty("tag_name").GetString() ?? string.Empty;
            var releaseUrl = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? string.Empty : string.Empty;

            // tag_name is e.g. "v2.0.2" — strip leading 'v' before parsing
            var latestStr = tagName.TrimStart('v');
            if (!Version.TryParse(latestStr, out var latest))
            {
                logger.LogWarning("GitHub returned an unparseable tag_name: {Tag}", tagName);
                return (false, string.Empty, string.Empty);
            }

            var running = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

            logger.LogInformation("Update check: running={Running} latest={Latest}", running, latest);

            if (latest > running)
                return (true, latestStr, releaseUrl);

            return (false, latestStr, releaseUrl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Update check failed");
            return (false, string.Empty, string.Empty);
        }
    }
}
