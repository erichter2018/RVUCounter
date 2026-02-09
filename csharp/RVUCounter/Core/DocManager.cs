using System.IO;
using System.Net.Http;
using Serilog;

namespace RVUCounter.Core;

/// <summary>
/// Checks for and downloads missing documentation files.
/// Ported from Python doc_manager.py.
/// </summary>
public class DocManager
{
    private readonly string _docDir;
    private readonly string _baseUrl;
    private static readonly HttpClient _httpClient = CreateHttpClient();

    /// <summary>
    /// Core documentation files required for the app.
    /// </summary>
    private static readonly string[] DocFiles =
    {
        "README.md",
        "Body_Part_Organization_Plan.md",
        $"WHATS_NEW_v{Config.AppVersion.Split(' ')[0]}.md",
        "AUTO_UPDATE_DESIGN.md",
        "YAML_Migration.md"
    };

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "RVU-Counter-Doc-Healer");
        client.Timeout = TimeSpan.FromSeconds(10);
        return client;
    }

    public DocManager(string? rootDir = null)
    {
        var root = rootDir ?? PlatformUtils.GetAppRoot();
        _docDir = Path.Combine(root, "documentation");
        _baseUrl = $"https://raw.githubusercontent.com/{Config.GitHubOwner}/{Config.GitHubRepo}/{Config.BackupBranch}/documentation";
    }

    /// <summary>
    /// Check for missing docs and download them.
    /// </summary>
    public async Task EnsureDocsAsync(bool force = false)
    {
        try
        {
            if (!Directory.Exists(_docDir))
            {
                Directory.CreateDirectory(_docDir);
            }

            var missingFiles = new List<string>();
            foreach (var filename in DocFiles)
            {
                var path = Path.Combine(_docDir, filename);
                if (force || !File.Exists(path))
                {
                    missingFiles.Add(filename);
                }
            }

            if (missingFiles.Count == 0)
            {
                return;
            }

            Log.Information("Missing {Count} documentation files. Downloading...", missingFiles.Count);

            foreach (var filename in missingFiles)
            {
                await DownloadDocAsync(filename);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error ensuring documentation files");
        }
    }

    /// <summary>
    /// Download a single documentation file.
    /// </summary>
    private async Task DownloadDocAsync(string filename)
    {
        try
        {
            var url = $"{_baseUrl}/{filename}";
            var dest = Path.Combine(_docDir, filename);

            var response = await _httpClient.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(dest, content);
                Log.Information("Downloaded: {Filename}", filename);
            }
            else
            {
                Log.Warning("Failed to download doc {Filename}: {Status}", filename, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to download doc {Filename}", filename);
        }
    }

    /// <summary>
    /// Get the path to a documentation file.
    /// </summary>
    public string? GetDocPath(string filename)
    {
        var path = Path.Combine(_docDir, filename);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Read the contents of a documentation file.
    /// </summary>
    public async Task<string?> ReadDocAsync(string filename)
    {
        var path = GetDocPath(filename);
        if (path == null)
        {
            // Try to download it first
            await DownloadDocAsync(filename);
            path = GetDocPath(filename);
        }

        if (path == null)
            return null;

        return await File.ReadAllTextAsync(path);
    }

    /// <summary>
    /// Get the What's New content for a specific version.
    /// </summary>
    public async Task<string?> GetWhatsNewAsync(string? version = null)
    {
        version ??= Config.AppVersion.Split(' ')[0];
        var filename = $"WHATS_NEW_v{version}.md";
        return await ReadDocAsync(filename);
    }

    /// <summary>
    /// Start background documentation check.
    /// </summary>
    public void StartBackgroundCheck()
    {
        Task.Run(async () =>
        {
            try
            {
                // Small delay to not impact startup
                await Task.Delay(3000);
                await EnsureDocsAsync();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Background doc check failed");
            }
        });
    }
}
