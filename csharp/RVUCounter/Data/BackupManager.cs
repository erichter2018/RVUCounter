using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using RVUCounter.Core;
using Serilog;

namespace RVUCounter.Data;

/// <summary>
/// Manages backups to OneDrive, Dropbox, and local storage.
/// Ported from Python backup_manager.py.
/// </summary>
public class BackupManager
{
    private static readonly HttpClient _httpClient = new();

    private readonly string _databasePath;
    private readonly Func<Models.UserSettings> _getSettings;
    private readonly Action<Models.UserSettings>? _saveSettings;
    private readonly Func<bool>? _checkpointDatabase;
    private DateTime? _lastBackupTime;

    // Default paths to check for OneDrive
    private static readonly string[] OneDriveEnvVars = { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" };
    private static readonly string[] OneDriveRegistryPaths =
    {
        @"SOFTWARE\Microsoft\OneDrive",
        @"SOFTWARE\Microsoft\SkyDrive"
    };

    public BackupManager(
        string databasePath,
        Func<Models.UserSettings> getSettings,
        Action<Models.UserSettings>? saveSettings = null,
        Func<bool>? checkpointDatabase = null)
    {
        _databasePath = databasePath;
        _getSettings = getSettings;
        _saveSettings = saveSettings;
        _checkpointDatabase = checkpointDatabase;
    }

    #region OneDrive Detection

    /// <summary>
    /// Detect OneDrive folder location.
    /// </summary>
    public string? DetectOneDriveFolder()
    {
        // Try environment variables first
        foreach (var envVar in OneDriveEnvVars)
        {
            var path = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                Log.Debug("Found OneDrive via env var {EnvVar}: {Path}", envVar, path);
                return path;
            }
        }

        // Try registry
        try
        {
            foreach (var regPath in OneDriveRegistryPaths)
            {
                using var key = Registry.CurrentUser.OpenSubKey(regPath);
                var userFolder = key?.GetValue("UserFolder") as string;
                if (!string.IsNullOrEmpty(userFolder) && Directory.Exists(userFolder))
                {
                    Log.Debug("Found OneDrive via registry: {Path}", userFolder);
                    return userFolder;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error reading OneDrive registry");
        }

        // Try common default paths
        var defaultPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive - Personal"),
        };

        foreach (var path in defaultPaths)
        {
            if (Directory.Exists(path))
            {
                Log.Debug("Found OneDrive at default path: {Path}", path);
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Get or create the backup folder within OneDrive.
    /// </summary>
    public string? GetOneDriveBackupFolder()
    {
        var settings = _getSettings();
        var oneDrivePath = settings.OneDrivePath;

        if (string.IsNullOrEmpty(oneDrivePath))
        {
            oneDrivePath = DetectOneDriveFolder();
            if (string.IsNullOrEmpty(oneDrivePath))
            {
                Log.Warning("OneDrive folder not found");
                return null;
            }

            // Save detected path
            settings.OneDrivePath = oneDrivePath;
            _saveSettings?.Invoke(settings);
        }

        var backupFolder = Path.Combine(oneDrivePath, "Apps", "RVU Counter", "Backups");
        try
        {
            Directory.CreateDirectory(backupFolder);
            return backupFolder;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create OneDrive backup folder: {Path}", backupFolder);
            return null;
        }
    }

    #endregion

    #region Backup Operations

    /// <summary>
    /// Create a backup based on settings.
    /// </summary>
    public async Task<BackupResult> CreateBackupAsync(string trigger = "manual")
    {
        var result = new BackupResult { Trigger = trigger };
        var settings = _getSettings();

        if (!settings.CloudBackupEnabled)
        {
            result.Message = "Cloud backup is disabled";
            return result;
        }

        // Check if backup is due (skip for manual and shift_end — both are explicit triggers)
        if (trigger != "manual" && trigger != "shift_end" && !ShouldBackupNow(settings))
        {
            result.Message = "Backup not due yet";
            return result;
        }

        // OneDrive backup
        if (!string.IsNullOrEmpty(settings.OneDrivePath) || settings.CloudBackupEnabled)
        {
            var oneDriveResult = await CreateOneDriveBackupAsync();
            result.OneDriveSuccess = oneDriveResult.Success;
            result.OneDriveMessage = oneDriveResult.Message;
            if (oneDriveResult.Success)
            {
                result.BackupPath = oneDriveResult.BackupPath;
            }
        }

        // Dropbox backup
        if (settings.DropboxBackupEnabled && !string.IsNullOrEmpty(settings.DropboxRefreshToken))
        {
            var dropboxResult = await CreateDropboxBackupAsync();
            result.DropboxSuccess = dropboxResult.Success;
            result.DropboxMessage = dropboxResult.Message;
        }

        result.Success = result.OneDriveSuccess || result.DropboxSuccess;
        _lastBackupTime = DateTime.Now;

        return result;
    }

    /// <summary>
    /// Create OneDrive backup.
    /// </summary>
    public async Task<(bool Success, string Message, string? BackupPath)> CreateOneDriveBackupAsync()
    {
        var backupFolder = GetOneDriveBackupFolder();
        if (string.IsNullOrEmpty(backupFolder))
        {
            return (false, "OneDrive backup folder not available", null);
        }

        if (!File.Exists(_databasePath))
        {
            return (false, "Database file not found", null);
        }

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm");
        // Append # to filename to indicate this backup is from C# version
        var backupFileName = $"RVU_Backup_{timestamp}#.db";
        var backupPath = Path.Combine(backupFolder, backupFileName);

        try
        {
            // IMPORTANT: Checkpoint WAL before copying to ensure all data is in main DB file
            if (_checkpointDatabase != null)
            {
                Log.Debug("Checkpointing database before backup...");
                if (!_checkpointDatabase())
                {
                    Log.Warning("Database checkpoint returned false, backup may be incomplete");
                }
            }
            else
            {
                Log.Warning("No checkpoint function provided - backup may miss WAL data");
            }

            // Copy database file
            await Task.Run(() =>
            {
                File.Copy(_databasePath, backupPath, overwrite: true);
            });

            // Verify integrity
            if (await VerifyBackupIntegrityAsync(backupPath))
            {
                Log.Information("OneDrive backup created: {Path}", backupPath);

                // Clean up old backups
                await CleanupOldBackupsAsync(backupFolder);

                return (true, $"Backup created: {backupFileName}", backupPath);
            }
            else
            {
                File.Delete(backupPath);
                return (false, "Backup integrity check failed", null);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OneDrive backup failed");
            return (false, $"Backup failed: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Create Dropbox backup using API.
    /// </summary>
    public async Task<(bool Success, string Message)> CreateDropboxBackupAsync()
    {
        var settings = _getSettings();

        if (string.IsNullOrEmpty(settings.DropboxRefreshToken))
        {
            return (false, "Dropbox not configured");
        }

        if (!File.Exists(_databasePath))
        {
            return (false, "Database file not found");
        }

        string? tempPath = null;
        try
        {
            // IMPORTANT: Checkpoint WAL before copying to ensure all data is included
            if (_checkpointDatabase != null)
            {
                Log.Debug("Checkpointing database before Dropbox backup...");
                _checkpointDatabase();
            }

            // Copy database to temp file using File.Copy (same as OneDrive backup)
            // Checkpoint ensures WAL data is flushed to main file first
            tempPath = Path.Combine(Path.GetTempPath(), $"rvu_backup_{Guid.NewGuid()}.db");
            await Task.Run(() =>
            {
                File.Copy(_databasePath, tempPath, overwrite: true);
            });

            // Get access token from refresh token (PKCE - no secret needed)
            var accessToken = await RefreshDropboxTokenAsync(
                settings.DropboxAppKey,
                settings.DropboxRefreshToken);

            if (string.IsNullOrEmpty(accessToken))
            {
                return (false, "Failed to get Dropbox access token");
            }

            // Upload file - append # to indicate C# version
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm");
            var remotePath = $"/Backups/RVU_Backup_{timestamp}#.db";

            var httpClient = _httpClient;
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
            httpClient.DefaultRequestHeaders.Add("Dropbox-API-Arg",
                JsonSerializer.Serialize(new { path = remotePath, mode = "overwrite" }));

            // Read from temp file (not locked)
            var fileBytes = await File.ReadAllBytesAsync(tempPath);
            var content = new ByteArrayContent(fileBytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

            var response = await httpClient.PostAsync(
                "https://content.dropboxapi.com/2/files/upload",
                content);

            if (response.IsSuccessStatusCode)
            {
                // Verify the upload response - detect if Dropbox renamed or truncated the file
                var responseJson = await response.Content.ReadAsStringAsync();
                try
                {
                    using var responseDoc = JsonDocument.Parse(responseJson);
                    var uploadedName = responseDoc.RootElement.GetProperty("name").GetString() ?? "";
                    var uploadedSize = responseDoc.RootElement.TryGetProperty("size", out var sizeEl) ? sizeEl.GetInt64() : -1;

                    Log.Information("Dropbox backup uploaded: {Name} ({Size} bytes, local was {Local} bytes)",
                        uploadedName, uploadedSize, fileBytes.Length);

                    if (uploadedName != Path.GetFileName(remotePath))
                    {
                        Log.Warning("Dropbox renamed backup file: expected '{Expected}', got '{Actual}'",
                            Path.GetFileName(remotePath), uploadedName);
                    }

                    if (uploadedSize >= 0 && uploadedSize != fileBytes.Length)
                    {
                        Log.Warning("Dropbox file size mismatch: uploaded {Local} bytes, server reports {Remote} bytes",
                            fileBytes.Length, uploadedSize);
                    }
                }
                catch (Exception parseEx)
                {
                    Log.Debug(parseEx, "Could not parse Dropbox upload response for verification");
                }

                // Clean up old Dropbox backups
                await CleanupOldDropboxBackupsAsync(accessToken, 10);

                return (true, $"Backup uploaded to Dropbox: {remotePath}");
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Error("Dropbox upload failed: {Error}", error);
                return (false, $"Upload failed: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Dropbox backup failed");
            return (false, $"Dropbox backup failed: {ex.Message}");
        }
        finally
        {
            // Clean up temp file
            if (tempPath != null && File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    /// <summary>
    /// Refresh Dropbox access token using PKCE (no client secret required).
    /// </summary>
    private async Task<string?> RefreshDropboxTokenAsync(string? appKey, string? refreshToken)
    {
        if (string.IsNullOrEmpty(appKey) || string.IsNullOrEmpty(refreshToken))
            return null;

        try
        {
            var httpClient = _httpClient;
            // PKCE refresh tokens don't require client_secret
            var requestContent = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "grant_type", "refresh_token" },
                { "refresh_token", refreshToken },
                { "client_id", appKey }
            });

            var response = await httpClient.PostAsync(
                "https://api.dropboxapi.com/oauth2/token",
                requestContent);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("access_token").GetString();
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Error("Dropbox token refresh failed: {Status} - {Error}", response.StatusCode, error);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to refresh Dropbox token");
        }
        return null;
    }

    /// <summary>
    /// Clean up old Dropbox backups beyond retention limit.
    /// </summary>
    private async Task CleanupOldDropboxBackupsAsync(string accessToken, int retentionCount)
    {
        if (retentionCount <= 0) retentionCount = 10;

        try
        {
            var httpClient = _httpClient;
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

            var backupFiles = new List<(string Path, string Name, long Size)>();

            // List files in /Backups folder (with pagination)
            var listContent = new StringContent(
                JsonSerializer.Serialize(new { path = "/Backups" }),
                Encoding.UTF8,
                "application/json");

            var listResponse = await httpClient.PostAsync(
                "https://api.dropboxapi.com/2/files/list_folder",
                listContent);

            if (!listResponse.IsSuccessStatusCode)
            {
                var error = await listResponse.Content.ReadAsStringAsync();
                Log.Warning("Failed to list Dropbox backups for cleanup: {Error}", error);
                return;
            }

            void CollectBackupEntries(JsonElement entries)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    var name = entry.GetProperty("name").GetString() ?? "";
                    if (name.StartsWith("RVU_Backup_") && name.EndsWith(".db"))
                    {
                        var pathLower = entry.GetProperty("path_lower").GetString() ?? "";
                        long size = entry.TryGetProperty("size", out var sizeEl) ? sizeEl.GetInt64() : 0;
                        backupFiles.Add((pathLower, name, size));
                    }
                }
            }

            var listJson = await listResponse.Content.ReadAsStringAsync();
            using (var doc = JsonDocument.Parse(listJson))
            {
                CollectBackupEntries(doc.RootElement.GetProperty("entries"));

                // Handle pagination - Dropbox may not return all files in one call
                var hasMore = doc.RootElement.GetProperty("has_more").GetBoolean();
                var cursor = doc.RootElement.GetProperty("cursor").GetString();

                while (hasMore && !string.IsNullOrEmpty(cursor))
                {
                    var continueContent = new StringContent(
                        JsonSerializer.Serialize(new { cursor }),
                        Encoding.UTF8,
                        "application/json");

                    var continueResponse = await httpClient.PostAsync(
                        "https://api.dropboxapi.com/2/files/list_folder/continue",
                        continueContent);

                    if (!continueResponse.IsSuccessStatusCode) break;

                    var continueJson = await continueResponse.Content.ReadAsStringAsync();
                    using var continueDoc = JsonDocument.Parse(continueJson);
                    CollectBackupEntries(continueDoc.RootElement.GetProperty("entries"));

                    hasMore = continueDoc.RootElement.GetProperty("has_more").GetBoolean();
                    cursor = continueDoc.RootElement.GetProperty("cursor").GetString();
                }
            }

            // Sort by name descending (newest first since name contains timestamp)
            backupFiles = backupFiles.OrderByDescending(f => f.Name).ToList();

            Log.Debug("Dropbox cleanup: found {Count} backups, retention is {Retention}",
                backupFiles.Count, retentionCount);

            // Log any suspiciously small files (potential corruption/sync issues)
            foreach (var (path, name, size) in backupFiles)
            {
                if (size > 0 && size < 10240) // less than 10KB is suspicious for a DB backup
                {
                    Log.Warning("Dropbox backup is suspiciously small: {Name} ({Size} bytes)", name, size);
                }
            }

            // Delete files beyond retention limit
            if (backupFiles.Count > retentionCount)
            {
                foreach (var (path, name, _) in backupFiles.Skip(retentionCount))
                {
                    try
                    {
                        var deleteContent = new StringContent(
                            JsonSerializer.Serialize(new { path }),
                            Encoding.UTF8,
                            "application/json");

                        var deleteResponse = await httpClient.PostAsync(
                            "https://api.dropboxapi.com/2/files/delete_v2",
                            deleteContent);

                        if (deleteResponse.IsSuccessStatusCode)
                        {
                            Log.Information("Deleted old Dropbox backup: {Name}", name);
                        }
                        else
                        {
                            var error = await deleteResponse.Content.ReadAsStringAsync();
                            Log.Warning("Failed to delete old Dropbox backup {Name}: {Error}", name, error);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Error deleting old Dropbox backup: {Path}", path);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error during Dropbox backup cleanup");
        }
    }

    #region PKCE Authorization Flow

    // Store code verifier during authorization flow
    private static string? _pendingCodeVerifier;

    /// <summary>
    /// Generate a cryptographically random code verifier for PKCE.
    /// </summary>
    private static string GenerateCodeVerifier()
    {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Base64UrlEncode(bytes);
    }

    /// <summary>
    /// Generate code challenge from verifier using SHA256.
    /// </summary>
    private static string GenerateCodeChallenge(string verifier)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    /// <summary>
    /// Base64 URL encoding (no padding, URL-safe characters).
    /// </summary>
    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>
    /// Start PKCE authorization flow - opens browser for user to authorize.
    /// Returns the authorization URL.
    /// </summary>
    public static string StartPkceAuthorization(string appKey)
    {
        _pendingCodeVerifier = GenerateCodeVerifier();
        var codeChallenge = GenerateCodeChallenge(_pendingCodeVerifier);

        var authUrl = $"https://www.dropbox.com/oauth2/authorize" +
            $"?client_id={appKey}" +
            $"&response_type=code" +
            $"&code_challenge={codeChallenge}" +
            $"&code_challenge_method=S256" +
            $"&token_access_type=offline";

        // Open browser
        try
        {
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open browser for Dropbox authorization");
        }

        return authUrl;
    }

    /// <summary>
    /// Complete PKCE authorization - exchange code for tokens.
    /// Call this after user pastes the authorization code.
    /// </summary>
    public static async Task<(bool Success, string? RefreshToken, string? Error)> CompletePkceAuthorizationAsync(
        string appKey, string authorizationCode)
    {
        if (string.IsNullOrEmpty(_pendingCodeVerifier))
        {
            return (false, null, "No pending authorization. Call StartPkceAuthorization first.");
        }

        try
        {
            var httpClient = _httpClient;
            var requestContent = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "grant_type", "authorization_code" },
                { "code", authorizationCode.Trim() },
                { "client_id", appKey },
                { "code_verifier", _pendingCodeVerifier }
            });

            var response = await httpClient.PostAsync(
                "https://api.dropboxapi.com/oauth2/token",
                requestContent);

            var json = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(json);
                var refreshToken = doc.RootElement.GetProperty("refresh_token").GetString();
                _pendingCodeVerifier = null; // Clear after use
                Log.Information("Dropbox PKCE authorization successful");
                return (true, refreshToken, null);
            }
            else
            {
                Log.Error("Dropbox PKCE authorization failed: {Response}", json);
                return (false, null, $"Authorization failed: {json}");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Dropbox PKCE authorization error");
            return (false, null, ex.Message);
        }
        finally
        {
            _pendingCodeVerifier = null;
        }
    }

    #endregion

    #endregion

    #region Backup Management

    /// <summary>
    /// Check if a backup should be performed now based on schedule.
    /// </summary>
    public bool ShouldBackupNow(Models.UserSettings settings)
    {
        if (!settings.CloudBackupEnabled)
            return false;

        var schedule = settings.BackupSchedule?.ToLowerInvariant() ?? "shift_end";

        // shift_end is handled by explicit TriggerShiftEndBackupAsync call, not by timer
        if (schedule == "shift_end")
            return false;

        if (_lastBackupTime == null)
            return true;

        var elapsed = DateTime.Now - _lastBackupTime.Value;

        return schedule switch
        {
            "hourly" => elapsed.TotalHours >= 1,
            "daily" => elapsed.TotalHours >= 24,
            _ => false
        };
    }

    /// <summary>
    /// Verify backup file integrity.
    /// </summary>
    private async Task<bool> VerifyBackupIntegrityAsync(string backupPath)
    {
        try
        {
            return await Task.Run(() =>
            {
                bool isOk;
                using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={backupPath}"))
                {
                    connection.Open();
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "PRAGMA integrity_check";
                    var result = cmd.ExecuteScalar()?.ToString();
                    isOk = result == "ok";
                    connection.Close();
                }
                // Release any pooled connections to this file
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                return isOk;
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Integrity check failed for {Path}", backupPath);
            return false;
        }
    }

    /// <summary>
    /// Clean up old backup files based on retention count.
    /// </summary>
    private async Task CleanupOldBackupsAsync(string backupFolder)
    {
        var settings = _getSettings();
        var retentionCount = 10;

        await Task.Run(() =>
        {
            try
            {
                // Clear any pooled SQLite connections that might be holding backup files open
                // (integrity checks open connections to these files)
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                // Sort by filename (contains timestamp) instead of File.GetCreationTime
                // which can be unreliable in OneDrive-synced folders
                var backupFiles = Directory.GetFiles(backupFolder, "RVU_Backup_*.db")
                    .OrderByDescending(f => Path.GetFileName(f))
                    .ToList();

                Log.Debug("OneDrive cleanup: found {Count} backups, retention is {Retention}",
                    backupFiles.Count, retentionCount);

                if (backupFiles.Count > retentionCount)
                {
                    foreach (var file in backupFiles.Skip(retentionCount))
                    {
                        try
                        {
                            File.Delete(file);
                            Log.Information("Deleted old OneDrive backup: {File}", Path.GetFileName(file));
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Failed to delete old backup (may be locked by OneDrive sync): {File}",
                                Path.GetFileName(file));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error cleaning up old backups");
            }
        });
    }

    /// <summary>
    /// Get list of available backups.
    /// </summary>
    public List<BackupInfo> GetBackupHistory()
    {
        var backups = new List<BackupInfo>();
        var backupFolder = GetOneDriveBackupFolder();

        if (string.IsNullOrEmpty(backupFolder) || !Directory.Exists(backupFolder))
            return backups;

        try
        {
            var files = Directory.GetFiles(backupFolder, "RVU_Backup_*.db")
                .OrderByDescending(f => Path.GetFileName(f));

            foreach (var file in files)
            {
                var fileInfo = new FileInfo(file);
                backups.Add(new BackupInfo
                {
                    FilePath = file,
                    FileName = fileInfo.Name,
                    CreatedAt = fileInfo.CreationTime,
                    SizeBytes = fileInfo.Length
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error getting backup history");
        }

        return backups;
    }

    /// <summary>
    /// Restore from a backup file.
    /// </summary>
    public async Task<(bool Success, string Message)> RestoreFromBackupAsync(string backupPath)
    {
        if (!File.Exists(backupPath))
        {
            return (false, "Backup file not found");
        }

        // Verify integrity first
        if (!await VerifyBackupIntegrityAsync(backupPath))
        {
            return (false, "Backup file integrity check failed");
        }

        try
        {
            // Create pre-restore snapshot
            var snapshotPath = _databasePath + ".pre_restore";
            if (File.Exists(_databasePath))
            {
                File.Copy(_databasePath, snapshotPath, overwrite: true);
            }

            // Restore
            File.Copy(backupPath, _databasePath, overwrite: true);

            Log.Information("Database restored from: {Backup}", backupPath);
            return (true, "Database restored successfully. Restart app to load new data.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Restore failed");
            return (false, $"Restore failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Delete a local OneDrive backup file.
    /// </summary>
    public bool DeleteOneDriveBackup(string filePath)
    {
        try
        {
            // Clear any pooled connections that might be holding the file
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            if (File.Exists(filePath))
            {
                // Retry a few times in case file is briefly locked
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        File.Delete(filePath);
                        Log.Information("Deleted OneDrive backup: {Path}", filePath);
                        return true;
                    }
                    catch (IOException) when (i < 2)
                    {
                        System.Threading.Thread.Sleep(100);
                        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    }
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete OneDrive backup: {Path}", filePath);
            return false;
        }
    }

    /// <summary>
    /// Delete a Dropbox backup file.
    /// </summary>
    public async Task<bool> DeleteDropboxBackupAsync(string remotePath)
    {
        var settings = _getSettings();

        if (string.IsNullOrEmpty(settings.DropboxRefreshToken))
        {
            Log.Warning("Cannot delete Dropbox backup - not configured");
            return false;
        }

        try
        {
            var accessToken = await RefreshDropboxTokenAsync(
                settings.DropboxAppKey,
                settings.DropboxRefreshToken);

            if (string.IsNullOrEmpty(accessToken))
            {
                return false;
            }

            var httpClient = _httpClient;
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

            var deleteContent = new StringContent(
                JsonSerializer.Serialize(new { path = remotePath }),
                Encoding.UTF8,
                "application/json");

            var response = await httpClient.PostAsync(
                "https://api.dropboxapi.com/2/files/delete_v2",
                deleteContent);

            if (response.IsSuccessStatusCode)
            {
                Log.Information("Deleted Dropbox backup: {Path}", remotePath);
                return true;
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Error("Failed to delete Dropbox backup: {Error}", error);
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deleting Dropbox backup: {Path}", remotePath);
            return false;
        }
    }

    /// <summary>
    /// List backups stored in Dropbox.
    /// </summary>
    public async Task<List<DropboxBackupInfo>> GetDropboxBackupsAsync()
    {
        var backups = new List<DropboxBackupInfo>();
        var settings = _getSettings();

        if (string.IsNullOrEmpty(settings.DropboxRefreshToken))
        {
            return backups;
        }

        try
        {
            var accessToken = await RefreshDropboxTokenAsync(
                settings.DropboxAppKey,
                settings.DropboxRefreshToken);

            if (string.IsNullOrEmpty(accessToken))
            {
                return backups;
            }

            var httpClient = _httpClient;
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

            var listContent = new StringContent(
                JsonSerializer.Serialize(new { path = "/Backups" }),
                Encoding.UTF8,
                "application/json");

            var response = await httpClient.PostAsync(
                "https://api.dropboxapi.com/2/files/list_folder",
                listContent);

            if (!response.IsSuccessStatusCode)
            {
                return backups;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var entries = doc.RootElement.GetProperty("entries");

            foreach (var entry in entries.EnumerateArray())
            {
                var name = entry.GetProperty("name").GetString() ?? "";
                if (name.StartsWith("RVU_Backup_") && name.EndsWith(".db"))
                {
                    var pathLower = entry.GetProperty("path_lower").GetString() ?? "";
                    var pathDisplay = entry.GetProperty("path_display").GetString() ?? pathLower;
                    long size = 0;
                    if (entry.TryGetProperty("size", out var sizeElement))
                    {
                        size = sizeElement.GetInt64();
                    }

                    // Parse timestamp from filename: RVU_Backup_2026-01-25_14-30#.db
                    DateTime? timestamp = null;
                    try
                    {
                        var dateStr = name.Replace("RVU_Backup_", "").Replace("#.db", "").Replace(".db", "");
                        timestamp = DateTime.ParseExact(dateStr, "yyyy-MM-dd_HH-mm", null);
                    }
                    catch { }

                    backups.Add(new DropboxBackupInfo
                    {
                        FileName = name,
                        RemotePath = pathDisplay,
                        SizeBytes = size,
                        CreatedAt = timestamp
                    });
                }
            }

            // Sort by name descending (newest first)
            backups = backups.OrderByDescending(b => b.FileName).ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error listing Dropbox backups");
        }

        return backups;
    }

    /// <summary>
    /// Download a backup from Dropbox and restore it.
    /// </summary>
    public async Task<(bool Success, string Message)> DownloadAndRestoreDropboxBackupAsync(string remotePath)
    {
        var settings = _getSettings();

        if (string.IsNullOrEmpty(settings.DropboxRefreshToken))
        {
            return (false, "Dropbox not configured");
        }

        string? tempPath = null;
        try
        {
            var accessToken = await RefreshDropboxTokenAsync(
                settings.DropboxAppKey,
                settings.DropboxRefreshToken);

            if (string.IsNullOrEmpty(accessToken))
            {
                return (false, "Failed to get Dropbox access token");
            }

            // Download file from Dropbox
            var httpClient = _httpClient;
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
            httpClient.DefaultRequestHeaders.Add("Dropbox-API-Arg",
                JsonSerializer.Serialize(new { path = remotePath }));

            var response = await httpClient.PostAsync(
                "https://content.dropboxapi.com/2/files/download",
                null);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                return (false, $"Download failed: {error}");
            }

            // Save to temp file
            tempPath = Path.Combine(Path.GetTempPath(), $"rvu_restore_{Guid.NewGuid()}.db");
            var fileBytes = await response.Content.ReadAsByteArrayAsync();
            await File.WriteAllBytesAsync(tempPath, fileBytes);

            // Use existing restore method
            var result = await RestoreFromBackupAsync(tempPath);
            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error downloading and restoring Dropbox backup");
            return (false, $"Error: {ex.Message}");
        }
        finally
        {
            if (tempPath != null && File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    /// <summary>
    /// Get backup status for UI display.
    /// </summary>
    public BackupStatus GetBackupStatus()
    {
        var settings = _getSettings();
        var status = new BackupStatus
        {
            IsEnabled = settings.CloudBackupEnabled,
            Schedule = settings.BackupSchedule ?? "shift_end",
            LastBackupTime = _lastBackupTime
        };

        // Check OneDrive
        var oneDrivePath = settings.OneDrivePath ?? DetectOneDriveFolder();
        status.OneDriveAvailable = !string.IsNullOrEmpty(oneDrivePath);
        status.OneDrivePath = oneDrivePath;

        // Check Dropbox
        status.DropboxConfigured = !string.IsNullOrEmpty(settings.DropboxRefreshToken);

        // Get latest backup info
        var backups = GetBackupHistory();
        if (backups.Count > 0)
        {
            var latestBackup = backups[0];
            status.LastBackupTime = latestBackup.CreatedAt;
            status.LastBackupPath = latestBackup.FilePath;
            status.BackupCount = backups.Count;
        }

        return status;
    }

    #endregion
}

#region Models

public class BackupResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string Trigger { get; set; } = "";
    public string? BackupPath { get; set; }
    public bool OneDriveSuccess { get; set; }
    public string? OneDriveMessage { get; set; }
    public bool DropboxSuccess { get; set; }
    public string? DropboxMessage { get; set; }
}

public class BackupInfo
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public long SizeBytes { get; set; }
    public string SizeDisplay => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:F1} KB",
        _ => $"{SizeBytes / (1024.0 * 1024):F1} MB"
    };
}

public class BackupStatus
{
    public bool IsEnabled { get; set; }
    public string Schedule { get; set; } = "";
    public DateTime? LastBackupTime { get; set; }
    public string? LastBackupPath { get; set; }
    public int BackupCount { get; set; }
    public bool OneDriveAvailable { get; set; }
    public string? OneDrivePath { get; set; }
    public bool DropboxConfigured { get; set; }
}

public class DropboxBackupInfo
{
    public string FileName { get; set; } = "";
    public string RemotePath { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime? CreatedAt { get; set; }
    public string SizeDisplay => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:F1} KB",
        _ => $"{SizeBytes / (1024.0 * 1024):F1} MB"
    };
    public string DateDisplay => CreatedAt?.ToString("MM/dd/yyyy HH:mm") ?? "Unknown";
}

#endregion
