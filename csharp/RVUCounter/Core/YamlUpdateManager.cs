using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using Serilog;

namespace RVUCounter.Core;

/// <summary>
/// Manages automatic updates of rvu_rules.yaml from GitHub.
/// Ported from Python yaml_update_manager.py.
/// </summary>
public class YamlUpdateManager
{
    private readonly string _localRulesPath;
    private readonly string _remoteUrl;

    private static readonly Regex VersionRegex = new(@"^#\s*version:\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly HttpClient _httpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "RVUCounter/2.0");
        return client;
    }

    public YamlUpdateManager(
        string localRulesPath,
        string? remoteUrl = null)
    {
        _localRulesPath = localRulesPath;
        _remoteUrl = remoteUrl ?? "https://raw.githubusercontent.com/erichter/RVUCounter/main/python/settings/rvu_rules.yaml";
    }

    /// <summary>
    /// Check for and apply YAML rules updates.
    /// </summary>
    public async Task<YamlUpdateResult> CheckAndUpdateAsync()
    {
        var result = new YamlUpdateResult();

        try
        {
            // Get local version
            var localVersion = GetLocalVersion();
            result.LocalVersion = localVersion;
            Log.Debug("Local rules version: {Version}", localVersion ?? "unknown");

            // Get remote content and version
            var remoteContent = await FetchRemoteRulesAsync();
            if (string.IsNullOrEmpty(remoteContent))
            {
                result.Message = "Could not fetch remote rules";
                return result;
            }

            var remoteVersion = ParseVersion(remoteContent);
            result.RemoteVersion = remoteVersion;
            Log.Debug("Remote rules version: {Version}", remoteVersion ?? "unknown");

            // Compare versions
            if (!IsNewerVersion(remoteVersion, localVersion))
            {
                result.Message = "Rules are up to date";
                result.Success = true;
                return result;
            }

            // Create backup of local file
            if (File.Exists(_localRulesPath))
            {
                var backupPath = _localRulesPath + ".bak";
                File.Copy(_localRulesPath, backupPath, overwrite: true);
                Log.Debug("Created backup: {Path}", backupPath);
            }

            // Write new content
            await File.WriteAllTextAsync(_localRulesPath, remoteContent);

            result.Success = true;
            result.Updated = true;
            result.Message = $"Updated rules from {localVersion ?? "unknown"} to {remoteVersion}";
            Log.Information("YAML rules updated to version {Version}", remoteVersion);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "YAML update failed");
            result.Message = $"Update failed: {ex.Message}";
        }

        return result;
    }

    /// <summary>
    /// Get version from local rules file.
    /// </summary>
    public string? GetLocalVersion()
    {
        try
        {
            if (!File.Exists(_localRulesPath))
                return null;

            var content = File.ReadAllText(_localRulesPath);
            return ParseVersion(content);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error reading local rules version");
            return null;
        }
    }

    /// <summary>
    /// Parse version from YAML content.
    /// Looks for "# version: X.Y.Z" comment at top of file.
    /// </summary>
    private string? ParseVersion(string content)
    {
        var match = VersionRegex.Match(content);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Fetch remote rules content.
    /// </summary>
    private async Task<string?> FetchRemoteRulesAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync(_remoteUrl);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync();
            }
            Log.Warning("Failed to fetch remote rules: {Status}", response.StatusCode);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error fetching remote rules");
        }
        return null;
    }

    /// <summary>
    /// Compare versions.
    /// </summary>
    private bool IsNewerVersion(string? remote, string? local)
    {
        if (string.IsNullOrEmpty(remote))
            return false;
        if (string.IsNullOrEmpty(local))
            return true;

        try
        {
            var remoteParts = remote.Split('.').Select(int.Parse).ToArray();
            var localParts = local.Split('.').Select(int.Parse).ToArray();

            for (int i = 0; i < Math.Max(remoteParts.Length, localParts.Length); i++)
            {
                var r = i < remoteParts.Length ? remoteParts[i] : 0;
                var l = i < localParts.Length ? localParts[i] : 0;

                if (r > l) return true;
                if (r < l) return false;
            }
        }
        catch
        {
            return string.Compare(remote, local, StringComparison.OrdinalIgnoreCase) > 0;
        }

        return false;
    }

    /// <summary>
    /// Start background update check.
    /// </summary>
    public void StartBackgroundCheck()
    {
        Task.Run(async () =>
        {
            try
            {
                // Small delay to not impact startup
                await Task.Delay(5000);
                var result = await CheckAndUpdateAsync();

                if (result.Updated)
                {
                    Log.Information("Background YAML update completed: {Message}", result.Message);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Background YAML update failed");
            }
        });
    }
}

/// <summary>
/// Result of YAML update check.
/// </summary>
public class YamlUpdateResult
{
    public bool Success { get; set; }
    public bool Updated { get; set; }
    public string Message { get; set; } = "";
    public string? LocalVersion { get; set; }
    public string? RemoteVersion { get; set; }
}
