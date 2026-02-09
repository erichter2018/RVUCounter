using System.IO;
using Microsoft.Data.Sqlite;
using RVUCounter.Core;
using RVUCounter.Data;
using Serilog;

namespace RVUCounter.Logic;

/// <summary>
/// Handles migration of database records to HIPAA-compliant hashed accession numbers.
/// Ported from Python hipaa_migration.py.
/// Uses DataManager.HashAccession() for Python-compatible hashing.
/// </summary>
public class HIPAAMigrator
{
    private readonly DataManager _dataManager;
    private readonly string _dbPath;
    private readonly BackupManager _backupManager;

    public event Action<string, double>? ProgressChanged;

    public HIPAAMigrator(DataManager dataManager, BackupManager backupManager)
    {
        _dataManager = dataManager;
        _backupManager = backupManager;
        _dbPath = Core.Config.GetDatabaseFile(PlatformUtils.GetAppRoot());
    }

    /// <summary>
    /// Run the full migration workflow.
    /// </summary>
    public async Task<MigrationResult> RunMigrationAsync()
    {
        var result = new MigrationResult();

        try
        {
            Report("Starting HIPAA Migration...", 0.0);

            // 0. Safety check - is it already hashed?
            if (await DetectIfAlreadyHashedAsync())
            {
                result.ErrorMessage =
                    "DATABASE ALREADY ENCRYPTED!\n\n" +
                    "It appears your database contains hashed records.\n\n" +
                    "This usually means you lost your 'user_settings.yaml' file " +
                    "(which contained your encryption key).\n\n" +
                    "ABORTING migration to prevent data corruption.";
                return result;
            }

            // 1. Create local backup
            await CreateLocalBackupAsync();

            // 2. Migrate main database
            await MigrateDatabaseAsync(_dbPath, true);

            // 3. Sanitize OneDrive backups
            var oneDriveFolder = _backupManager.GetOneDriveBackupFolder();
            if (!string.IsNullOrEmpty(oneDriveFolder) && Directory.Exists(oneDriveFolder))
            {
                await SanitizeOneDriveFolderAsync(oneDriveFolder);
            }

            // 4. Mark migration as complete (already compliant in C# version)
            _dataManager.SaveSettings();

            Report("Migration Complete!", 1.0);
            result.Success = true;
            result.RecordsMigrated = _recordsMigrated;
            result.BackupFilesMigrated = _backupFilesMigrated;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "HIPAA Migration failed");
            result.ErrorMessage = ex.Message;
            Report($"Error: {ex.Message}", 0.0);
        }

        return result;
    }

    private int _recordsMigrated;
    private int _backupFilesMigrated;

    /// <summary>
    /// Create a secure local backup of the unhashed database.
    /// </summary>
    private async Task CreateLocalBackupAsync()
    {
        Report("Creating pre-migration backup...", 0.1);

        var backupDir = Path.Combine(
            Environment.CurrentDirectory,
            "RVU_PRE_MIGRATION_BACKUP_DO_NOT_UPLOAD");
        Directory.CreateDirectory(backupDir);

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var backupPath = Path.Combine(backupDir, $"rvu_records_RAW_UNHASHED_{timestamp}.db");

        await Task.Run(() =>
        {
            // Use SQLite backup API
            using var source = new SqliteConnection($"Data Source={_dbPath}");
            source.Open();
            using var destination = new SqliteConnection($"Data Source={backupPath}");
            destination.Open();

            source.BackupDatabase(destination);
        });

        Report($"Backup saved to: {backupPath}", 0.15);
    }

    /// <summary>
    /// Migrate a single SQLite database file.
    /// </summary>
    private async Task MigrateDatabaseAsync(string filePath, bool isMain)
    {
        var name = Path.GetFileName(filePath);
        Report($"Migrating {name}...", null);

        await Task.Run(() =>
        {
            using var connection = new SqliteConnection($"Data Source={filePath}");
            connection.Open();

            // Count total records
            using var countCmd = connection.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM records";
            var totalRows = Convert.ToInt32(countCmd.ExecuteScalar());

            if (totalRows == 0)
            {
                return;
            }

            // Read all records
            using var selectCmd = connection.CreateCommand();
            selectCmd.CommandText = "SELECT id, accession FROM records";

            var updates = new List<(string Hash, int Id)>();
            using (var reader = selectCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var id = reader.GetInt32(0);
                    var accession = reader.IsDBNull(1) ? "" : reader.GetString(1);

                    if (!string.IsNullOrEmpty(accession))
                    {
                        // Use DataManager's Python-compatible hash algorithm
                        var hashed = _dataManager.HashAccession(accession);
                        updates.Add((hashed, id));
                    }
                }
            }

            // Batch update
            var batchSize = Core.Config.HipaaBatchSize;
            var processed = 0;

            using var transaction = connection.BeginTransaction();
            try
            {
                foreach (var (hash, id) in updates)
                {
                    using var updateCmd = connection.CreateCommand();
                    updateCmd.CommandText = "UPDATE records SET accession = @hash WHERE id = @id";
                    updateCmd.Parameters.AddWithValue("@hash", hash);
                    updateCmd.Parameters.AddWithValue("@id", id);
                    updateCmd.ExecuteNonQuery();

                    processed++;

                    if (isMain && processed % batchSize == 0)
                    {
                        var pct = 0.15 + (0.25 * ((double)processed / totalRows));
                        Report($"Hashing records: {processed}/{totalRows}", pct);
                    }
                }

                transaction.Commit();
                _recordsMigrated += processed;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }

            // Vacuum
            if (isMain)
            {
                Report($"Vacuuming {name}...", 0.4);
            }

            using var vacuumCmd = connection.CreateCommand();
            vacuumCmd.CommandText = "VACUUM";
            vacuumCmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Sanitize OneDrive backup folder.
    /// </summary>
    private async Task SanitizeOneDriveFolderAsync(string folder)
    {
        Report("Sanitizing OneDrive backups...", 0.4);

        var files = Directory.GetFiles(folder, "*.db");
        var total = files.Length;

        for (int i = 0; i < files.Length; i++)
        {
            var filePath = files[i];
            var fileName = Path.GetFileName(filePath);

            try
            {
                // Migrate content
                await MigrateDatabaseAsync(filePath, false);

                // Rename for readability
                var newName = GenerateReadableName(fileName, filePath);
                if (newName != fileName)
                {
                    var newPath = Path.Combine(folder, newName);
                    if (File.Exists(newPath))
                    {
                        File.Delete(newPath);
                    }
                    File.Move(filePath, newPath);
                }

                _backupFilesMigrated++;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to sanitize {FileName}", fileName);
            }

            var progress = 0.4 + (0.6 * ((double)(i + 1) / total));
            Report($"Processed {i + 1}/{total} OneDrive files", progress);
        }
    }

    /// <summary>
    /// Generate a readable filename with date.
    /// </summary>
    private string GenerateReadableName(string oldName, string fullPath)
    {
        try
        {
            // Try to parse timestamp from filename
            // Old: rvu_records_20260108_085843.db
            if (oldName.StartsWith("rvu_records_") && oldName.Length >= 27)
            {
                var tsPart = oldName.Substring(12, 15); // 20260108_085843
                var dt = DateTime.ParseExact(tsPart, "yyyyMMdd_HHmmss", null);
                return $"RVU_Backup_{dt:yyyy-MM-dd_HH-mm}.db";
            }
            else
            {
                // Fallback to file modification time
                var mtime = File.GetLastWriteTime(fullPath);
                return $"RVU_Backup_{mtime:yyyy-MM-dd_HH-mm}.db";
            }
        }
        catch
        {
            return oldName;
        }
    }

    /// <summary>
    /// Check if the database already contains hashed records.
    /// </summary>
    private async Task<bool> DetectIfAlreadyHashedAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                if (!File.Exists(_dbPath))
                    return false;

                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT accession FROM records LIMIT 50";

                var accessions = new List<string>();
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (!reader.IsDBNull(0))
                        {
                            accessions.Add(reader.GetString(0));
                        }
                    }
                }

                if (accessions.Count == 0)
                    return false;

                // Check if records look like SHA-256 hashes (64 hex chars)
                var hashLikeCount = accessions.Count(acc =>
                    acc.Length == 64 && acc.All(c => Uri.IsHexDigit(c)));

                // If more than 80% look like hashes, assume already hashed
                return (double)hashLikeCount / accessions.Count > 0.8;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not check database state");
                return false;
            }
        });
    }

    private void Report(string message, double? progress)
    {
        Log.Information("HIPAA Migration: {Message}", message);
        ProgressChanged?.Invoke(message, progress ?? 0);
    }
}

/// <summary>
/// Result of HIPAA migration.
/// </summary>
public class MigrationResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int RecordsMigrated { get; set; }
    public int BackupFilesMigrated { get; set; }
}

