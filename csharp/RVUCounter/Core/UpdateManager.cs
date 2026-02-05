using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Serilog;

namespace RVUCounter.Core;

/// <summary>
/// Manages application updates via GitHub Releases.
/// Uses rename-based update mechanism (MosaicTools approach) that works even while app is running.
/// </summary>
public class UpdateManager
{
    private readonly string _currentVersion;
    private readonly string _githubOwner;
    private readonly string _githubRepo;
    private readonly HttpClient _httpClient;

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

    public UpdateManager(
        string? currentVersion = null,
        string? githubOwner = null,
        string? githubRepo = null)
    {
        _currentVersion = currentVersion ?? GetCurrentVersion();
        _githubOwner = githubOwner ?? Config.GitHubOwner;
        _githubRepo = githubRepo ?? Config.GitHubRepo;
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        });
        _httpClient.DefaultRequestHeaders.Add("User-Agent", $"RVUCounter/{_currentVersion}");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/octet-stream");
    }

    /// <summary>
    /// Get current version from assembly.
    /// </summary>
    public static string GetCurrentVersion()
    {
        return Config.AppVersion;
    }

    /// <summary>
    /// Get the path to the current executable.
    /// </summary>
    private static string GetCurrentExePath()
    {
        return Process.GetCurrentProcess().MainModule?.FileName
            ?? Path.Combine(AppContext.BaseDirectory, "RVUCounter.exe");
    }

    /// <summary>
    /// Get the path to the old version executable (for cleanup).
    /// </summary>
    private static string GetOldExePath()
    {
        var currentPath = GetCurrentExePath();
        var dir = Path.GetDirectoryName(currentPath) ?? AppContext.BaseDirectory;
        return Path.Combine(dir, "RVUCounter_old.exe");
    }

    /// <summary>
    /// Check if we just updated (old version file exists).
    /// Does NOT delete the old version immediately - keeps it as a rollback until
    /// the app has fully started successfully.
    /// </summary>
    /// <returns>True if an old version exists (indicating we just updated)</returns>
    public static bool JustUpdated()
    {
        return File.Exists(GetOldExePath());
    }

    /// <summary>
    /// Clean up old version file after confirming the new version is stable.
    /// Call this after the app has fully started and is working.
    /// </summary>
    public static void CleanupOldVersion()
    {
        var oldExePath = GetOldExePath();

        try
        {
            if (File.Exists(oldExePath))
            {
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        File.Delete(oldExePath);
                        Log.Information("Cleaned up old version: {Path}", oldExePath);
                        return;
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(500);
                    }
                }

                Log.Warning("Could not delete old version file after retries: {Path}", oldExePath);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error cleaning up old version: {Path}", oldExePath);
        }
    }

    /// <summary>
    /// Write a marker file before restarting for an update.
    /// Used as a circuit breaker to prevent restart loops.
    /// </summary>
    public static void WriteRestartMarker()
    {
        try
        {
            var markerPath = GetRestartMarkerPath();
            File.WriteAllText(markerPath, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not write restart marker");
        }
    }

    /// <summary>
    /// Check if a recent update restart happened (circuit breaker).
    /// Returns true if it's safe to auto-update, false if we should skip.
    /// </summary>
    public static bool IsAutoUpdateSafe()
    {
        try
        {
            var markerPath = GetRestartMarkerPath();
            if (!File.Exists(markerPath))
                return true;

            var content = File.ReadAllText(markerPath).Trim();
            File.Delete(markerPath);

            if (DateTime.TryParse(content, null, System.Globalization.DateTimeStyles.RoundtripKind, out var lastRestart))
            {
                var elapsed = DateTime.UtcNow - lastRestart;
                if (elapsed.TotalMinutes < 3)
                {
                    Log.Warning("Auto-update circuit breaker: app restarted for update {Elapsed:F0}s ago, skipping auto-update this launch",
                        elapsed.TotalSeconds);
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error checking restart marker");
            // If we can't check, err on the side of caution
            try { File.Delete(GetRestartMarkerPath()); } catch { }
        }

        return true;
    }

    private static string GetRestartMarkerPath()
    {
        var dir = Path.GetDirectoryName(GetCurrentExePath()) ?? AppContext.BaseDirectory;
        return Path.Combine(dir, ".update_restart");
    }

    /// <summary>
    /// Check for available updates.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            var url = $"https://api.github.com/repos/{_githubOwner}/{_githubRepo}/releases/latest";

            using var cts = new CancellationTokenSource(ApiTimeout);
            var response = await _httpClient.GetStringAsync(url, cts.Token);

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
            var name = root.GetProperty("name").GetString() ?? "";
            var body = root.GetProperty("body").GetString() ?? "";
            var htmlUrl = root.GetProperty("html_url").GetString() ?? "";
            var publishedAt = root.TryGetProperty("published_at", out var pubProp)
                ? DateTime.Parse(pubProp.GetString() ?? "")
                : DateTime.Now;

            // Find download asset - prefer ZIP over EXE (corporate security friendly)
            string? downloadUrl = null;
            string? assetName = null;
            long assetSize = 0;

            if (root.TryGetProperty("assets", out var assets))
            {
                // First pass: look for ZIP
                foreach (var asset in assets.EnumerateArray())
                {
                    var assetFileName = asset.GetProperty("name").GetString() ?? "";
                    if (assetFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.GetProperty("browser_download_url").GetString();
                        assetName = assetFileName;
                        assetSize = asset.GetProperty("size").GetInt64();
                        break;
                    }
                }

                // Second pass: fall back to EXE if no ZIP
                if (downloadUrl == null)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var assetFileName = asset.GetProperty("name").GetString() ?? "";
                        if (assetFileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.GetProperty("browser_download_url").GetString();
                            assetName = assetFileName;
                            assetSize = asset.GetProperty("size").GetInt64();
                            break;
                        }
                    }
                }
            }

            var info = new UpdateInfo
            {
                Version = tagName,
                Name = name,
                ReleaseNotes = body,
                ReleaseUrl = htmlUrl,
                DownloadUrl = downloadUrl,
                AssetName = assetName,
                AssetSize = assetSize,
                PublishedAt = publishedAt
            };

            // Compare versions
            info.IsUpdateAvailable = IsNewerVersion(tagName, _currentVersion);

            Log.Debug("Update check: current={Current}, latest={Latest}, available={Available}",
                _currentVersion, tagName, info.IsUpdateAvailable);

            return info;
        }
        catch (TaskCanceledException)
        {
            Log.Warning("Update check timed out after {Timeout}s", ApiTimeout.TotalSeconds);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to check for updates");
            return null;
        }
    }

    /// <summary>
    /// Compare semantic versions.
    /// </summary>
    private bool IsNewerVersion(string latest, string current)
    {
        try
        {
            var latestParts = latest.Split('.').Select(int.Parse).ToArray();
            var currentParts = current.Split('.').Select(int.Parse).ToArray();

            for (int i = 0; i < Math.Max(latestParts.Length, currentParts.Length); i++)
            {
                var l = i < latestParts.Length ? latestParts[i] : 0;
                var c = i < currentParts.Length ? currentParts[i] : 0;

                if (l > c) return true;
                if (l < c) return false;
            }
        }
        catch
        {
            // If parsing fails, fall back to string comparison
            return string.Compare(latest, current, StringComparison.OrdinalIgnoreCase) > 0;
        }

        return false;
    }

    /// <summary>
    /// Download and apply update using rename-based approach.
    /// This works even while the app is running because Windows allows renaming open files.
    /// </summary>
    /// <returns>True if update was successfully applied and app should restart</returns>
    public async Task<bool> DownloadAndApplyUpdateAsync(
        string downloadUrl,
        IProgress<double>? progress = null,
        long expectedSize = 0)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"RVUCounter_Update_{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(tempDir);

            // Strip query parameters from URL for filename
            var uri = new Uri(downloadUrl);
            var fileName = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrEmpty(fileName))
                fileName = "RVUCounter.zip";
            var tempFile = Path.Combine(tempDir, fileName);

            // Download with timeout
            Log.Information("Downloading update from: {Url} (filename: {FileName})", downloadUrl, fileName);

            using var cts = new CancellationTokenSource(DownloadTimeout);
            using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            Log.Information("Download response: {StatusCode} {ReasonPhrase}, ContentLength={ContentLength}",
                (int)response.StatusCode, response.ReasonPhrase, response.Content.Headers.ContentLength);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            var downloadedBytes = 0L;

            await using var contentStream = await response.Content.ReadAsStreamAsync(cts.Token);
            await using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);

            var buffer = new byte[65536];
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, cts.Token)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cts.Token);
                downloadedBytes += bytesRead;

                if (totalBytes > 0)
                {
                    progress?.Report((double)downloadedBytes / totalBytes);
                }
            }

            Log.Information("Download complete: {Path} ({Size} bytes)", tempFile, downloadedBytes);

            // Verify download size matches what GitHub reported
            if (expectedSize > 0 && downloadedBytes != expectedSize)
            {
                Log.Error("Download size mismatch: got {Actual} bytes, expected {Expected} bytes",
                    downloadedBytes, expectedSize);
                return false;
            }

            // Verify download isn't suspiciously small (corrupt/truncated)
            if (downloadedBytes < 1024 * 100) // less than 100KB is suspicious for an exe/zip
            {
                Log.Error("Downloaded file is suspiciously small: {Size} bytes", downloadedBytes);
                return false;
            }

            // Extract if ZIP, otherwise use the downloaded file directly
            string newExePath;
            string? extractedDir = null;
            if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                extractedDir = Path.Combine(tempDir, "extracted");
                ZipFile.ExtractToDirectory(tempFile, extractedDir);

                // Find the exe in the extracted folder
                var exeFiles = Directory.GetFiles(extractedDir, "RVUCounter.exe", SearchOption.AllDirectories);
                if (exeFiles.Length == 0)
                {
                    exeFiles = Directory.GetFiles(extractedDir, "*.exe", SearchOption.AllDirectories);
                }

                if (exeFiles.Length == 0)
                {
                    Log.Error("No executable found in downloaded ZIP");
                    return false;
                }

                newExePath = exeFiles[0];
                Log.Information("Extracted executable: {Path}", newExePath);
            }
            else
            {
                newExePath = tempFile;
            }

            // Apply the update using rename approach
            return ApplyUpdate(newExePath, extractedDir);
        }
        catch (TaskCanceledException)
        {
            Log.Error("Update download timed out after {Timeout} minutes", DownloadTimeout.TotalMinutes);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download and apply update");
            return false;
        }
        finally
        {
            // Cleanup temp directory (but not immediately as we may need the new exe)
            try
            {
                // Schedule cleanup after a delay
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    try
                    {
                        if (Directory.Exists(tempDir))
                        {
                            Directory.Delete(tempDir, true);
                        }
                    }
                    catch { /* ignore cleanup errors */ }
                });
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Apply update by renaming files.
    /// Windows allows renaming files that are in use, making this approach robust.
    /// </summary>
    private bool ApplyUpdate(string newExePath, string? extractedDir = null)
    {
        var currentExe = GetCurrentExePath();
        var oldExe = GetOldExePath();
        var currentDir = Path.GetDirectoryName(currentExe) ?? AppContext.BaseDirectory;
        var tempNewExe = Path.Combine(currentDir, "RVUCounter_new.exe");
        var step3Done = false;

        try
        {
            Log.Information("Applying update: {New} -> {Current} (appDir: {Dir})", newExePath, currentExe, currentDir);

            // Step 1: Copy new exe to app directory with temp name
            File.Copy(newExePath, tempNewExe, overwrite: true);
            Log.Information("Step 1: Copied new exe to: {Path}", tempNewExe);

            // Step 2: Delete old version if it exists from a previous failed update
            if (File.Exists(oldExe))
            {
                try
                {
                    File.Delete(oldExe);
                    Log.Information("Step 2: Deleted existing old version file");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Step 2: Could not delete existing old version file, trying overwrite");
                }
            }

            // Step 3: Rename current exe to _old (Windows allows this even when running!)
            File.Move(currentExe, oldExe, overwrite: true);
            step3Done = true;
            Log.Information("Step 3: Renamed current exe to: {Path}", oldExe);

            // Step 4: Rename new exe to current
            File.Move(tempNewExe, currentExe);
            Log.Information("Step 4: Renamed new exe to: {Path}", currentExe);

            // Step 5: Copy resources folder if present in extracted ZIP
            if (extractedDir != null)
            {
                CopyResourcesFromExtracted(extractedDir, currentDir);
            }

            Log.Information("Update applied successfully. Restart required.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to apply update");

            // CRITICAL: If step 3 succeeded but step 4 failed, there's no RVUCounter.exe!
            // Roll back by renaming _old back to the original name.
            if (step3Done && !File.Exists(currentExe) && File.Exists(oldExe))
            {
                try
                {
                    File.Move(oldExe, currentExe);
                    Log.Information("Rolled back: restored original exe from _old");
                }
                catch (Exception rollbackEx)
                {
                    Log.Error(rollbackEx, "CRITICAL: Rollback failed! RVUCounter.exe is missing. " +
                        "Manually rename RVUCounter_old.exe to RVUCounter.exe to recover.");
                }
            }

            // Clean up temp new exe if it's still around
            try { if (File.Exists(tempNewExe)) File.Delete(tempNewExe); } catch { }

            return false;
        }
    }

    /// <summary>
    /// Copy resources folder from extracted ZIP to application directory.
    /// </summary>
    private static void CopyResourcesFromExtracted(string extractedDir, string appDir)
    {
        try
        {
            // Look for resources folder in extracted content
            var resourcesDirs = Directory.GetDirectories(extractedDir, "resources", SearchOption.AllDirectories);
            if (resourcesDirs.Length == 0)
            {
                Log.Debug("No resources folder found in extracted update");
                return;
            }

            var sourceResources = resourcesDirs[0];
            var targetResources = Path.Combine(appDir, "resources");

            // Create target resources folder if it doesn't exist
            Directory.CreateDirectory(targetResources);

            // Copy all files from source to target, overwriting existing
            foreach (var file in Directory.GetFiles(sourceResources, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceResources, file);
                var targetFile = Path.Combine(targetResources, relativePath);
                var targetDir = Path.GetDirectoryName(targetFile);
                if (targetDir != null)
                    Directory.CreateDirectory(targetDir);

                File.Copy(file, targetFile, overwrite: true);
                Log.Information("Updated resource: {File}", relativePath);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to copy resources from update (non-fatal)");
        }
    }

    /// <summary>
    /// Restart the application.
    /// </summary>
    public static void RestartApp()
    {
        try
        {
            var currentExe = GetCurrentExePath();
            var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
            var argsString = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

            Log.Information("Restarting application: {Exe} {Args}", currentExe, argsString);

            var startInfo = new ProcessStartInfo
            {
                FileName = currentExe,
                Arguments = argsString,
                UseShellExecute = true
            };

            Process.Start(startInfo);

            // Exit current process
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to restart application");
        }
    }

    /// <summary>
    /// Open release page in browser.
    /// </summary>
    public static void OpenReleasePage(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to open release page");
        }
    }
}

/// <summary>
/// Information about an available update.
/// </summary>
public class UpdateInfo
{
    public string Version { get; set; } = "";
    public string Name { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public string ReleaseUrl { get; set; } = "";
    public string? DownloadUrl { get; set; }
    public string? AssetName { get; set; }
    public long AssetSize { get; set; }
    public DateTime PublishedAt { get; set; }
    public bool IsUpdateAvailable { get; set; }

    public string AssetSizeDisplay => AssetSize switch
    {
        < 1024 => $"{AssetSize} B",
        < 1024 * 1024 => $"{AssetSize / 1024.0:F1} KB",
        _ => $"{AssetSize / (1024.0 * 1024):F1} MB"
    };
}
