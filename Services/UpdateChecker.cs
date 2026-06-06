using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace HyperVManagerTray.Services;

/// <summary>
/// Checks the GitHub Releases API for a newer version and can download + launch the installer.
/// </summary>
internal sealed class UpdateChecker(HttpClient http, ILogger<UpdateChecker> logger)
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/ezpl/HyperVManagerTray/releases/latest";

    public record CheckResult(
        bool   UpdateAvailable,
        string LatestVersion,   // empty = network error; "none" = no releases yet
        string ReleasePageUrl,
        string InstallerUrl,    // direct .exe download URL from release assets; empty if not found
        string ReleaseNotes);   // body text from the GitHub release, stripped of markdown

    /// <summary>
    /// Queries the GitHub Releases API. Never throws.
    /// </summary>
    public async Task<CheckResult> CheckAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("HyperVManagerTray", null));

            using var cts      = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);

            // 404 = no releases published yet (tags ≠ releases on GitHub)
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogInformation("Update check: no releases found on GitHub yet");
                return new(false, "none", string.Empty, string.Empty, string.Empty);
            }

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);

            var root        = doc.RootElement;
            var tagName     = root.GetProperty("tag_name").GetString() ?? string.Empty;
            var releaseUrl  = root.TryGetProperty("html_url",  out var u) ? u.GetString() ?? string.Empty : string.Empty;
            var bodyMd      = root.TryGetProperty("body",      out var b) ? b.GetString() : null;
            var releaseNotes = StripMarkdown(bodyMd);

            // Parse installer URL from assets array — find the first .exe asset
            var installerUrl = string.Empty;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        installerUrl = asset.TryGetProperty("browser_download_url", out var dl)
                            ? dl.GetString() ?? string.Empty : string.Empty;
                        break;
                    }
                }
            }

            // tag_name is e.g. "v2.1.2" — strip leading 'v'
            var latestStr = tagName.TrimStart('v');
            if (!Version.TryParse(latestStr, out var latest))
            {
                logger.LogWarning("GitHub returned an unparseable tag_name: {Tag}", tagName);
                return new(false, string.Empty, string.Empty, string.Empty, string.Empty);
            }

            var running = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            logger.LogInformation("Update check: running={Running} latest={Latest}", running, latest);

            return new(latest > running, latestStr, releaseUrl, installerUrl, releaseNotes);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Update check failed");
            return new(false, string.Empty, string.Empty, string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// Downloads the installer to %TEMP% and returns the local file path.
    /// Reports download progress (0–100) via <paramref name="progress"/>.
    /// Throws on failure — callers should catch.
    /// </summary>
    public async Task<string> DownloadInstallerAsync(string url, IProgress<int>? progress = null,
                                                      CancellationToken ct = default)
    {
        var dest = Path.Combine(Path.GetTempPath(), "HyperVManagerTray-Setup.exe");

        using var request  = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("HyperVManagerTray", null));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                                       .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total   = response.Content.Headers.ContentLength ?? -1L;
        var buffer  = new byte[81920];
        long downloaded = 0;
        int  lastPct    = -1;

        await using var src  = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst  = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None,
                                              bufferSize: 81920, useAsync: true);
        int read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            downloaded += read;
            if (progress != null && total > 0)
            {
                var pct = (int)(downloaded * 100 / total);
                if (pct != lastPct) { progress.Report(pct); lastPct = pct; }
            }
        }

        logger.LogInformation("Installer downloaded to {Path} ({Bytes:N0} bytes)", dest, downloaded);
        return dest;
    }

    /// <summary>
    /// Converts a GitHub-flavoured Markdown string to plain text suitable for display in a
    /// Win32 Task Dialog.  Handles the common patterns used in release notes:
    /// ATX headers (## …), bold (**…**), italic (*…*), inline code (`…`), list bullets (- / *).
    /// </summary>
    private static string StripMarkdown(string? md)
    {
        if (string.IsNullOrWhiteSpace(md)) return string.Empty;

        // ATX headers: ## Title → Title
        md = Regex.Replace(md, @"^#{1,6}\s+", string.Empty, RegexOptions.Multiline);
        // Bold / italic: ***text***, **text**, *text* → text
        md = Regex.Replace(md, @"\*{1,3}(.+?)\*{1,3}", "$1");
        // Inline code: `code` → code
        md = Regex.Replace(md, @"`([^`]+)`", "$1");
        // Unordered list: - item or * item → • item
        md = Regex.Replace(md, @"^[ \t]*[-*]\s+", "• ", RegexOptions.Multiline);
        // Collapse 3+ consecutive blank lines to 2
        md = Regex.Replace(md, @"\n{3,}", "\n\n");

        return md.Trim();
    }
}
