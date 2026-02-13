using System.IO;
using Microsoft.Data.Sqlite;
using RVUCounter.Core;
using RVUCounter.Models;
using Serilog;

namespace RVUCounter.Data;

/// <summary>
/// SQLite database layer for RVU Counter.
/// Handles all database operations for shifts and study records.
/// Schema-compatible with the Python version.
/// </summary>
public class RecordsDatabase : IDisposable
{
    private readonly string _dbPath;
    private SqliteConnection? _connection;
    private readonly object _lock = new();

    public RecordsDatabase(string dbPath)
    {
        _dbPath = dbPath;
        EnsureDirectoryExists();
        Connect();
        CreateTables();
    }

    private void EnsureDirectoryExists()
    {
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private void Connect()
    {
        var connectionString = $"Data Source={_dbPath}";
        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        // Enable WAL mode for better concurrent read/write access
        // This allows readers to not block writers and vice versa
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL";
            var result = cmd.ExecuteScalar();
            Log.Debug("SQLite journal mode set to: {Mode}", result);
        }

        // Set busy timeout for concurrent access from other apps
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA busy_timeout={Core.Config.DatabaseBusyTimeoutMs}";
            cmd.ExecuteNonQuery();
        }

        Log.Debug("Database connection opened: {Path}", _dbPath);
    }

    private void CreateTables()
    {
        lock (_lock)
        {
            // First check if this is a Python database and migrate if needed
            if (IsPythonDatabase())
            {
                Log.Information("Detected Python database format, migrating...");
                MigratePythonDatabase();
            }

            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS shifts (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    shift_start TEXT NOT NULL,
                    shift_end TEXT,
                    effective_shift_start TEXT,
                    projected_shift_end TEXT,
                    shift_name TEXT,
                    is_current INTEGER DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS records (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    shift_id INTEGER NOT NULL,
                    accession TEXT NOT NULL,
                    procedure TEXT,
                    study_type TEXT,
                    rvu REAL,
                    timestamp TEXT NOT NULL,
                    time_finished TEXT,
                    patient_class TEXT DEFAULT 'Unknown',
                    accession_count INTEGER DEFAULT 1,
                    source TEXT DEFAULT 'Mosaic',
                    metadata TEXT,
                    duration_seconds REAL,
                    from_multi_accession INTEGER DEFAULT 0,
                    multi_accession_group TEXT,
                    has_critical_result INTEGER DEFAULT 0,
                    FOREIGN KEY (shift_id) REFERENCES shifts(id)
                );

                CREATE INDEX IF NOT EXISTS idx_records_shift_id ON records(shift_id);
                CREATE INDEX IF NOT EXISTS idx_records_accession ON records(accession);
                CREATE INDEX IF NOT EXISTS idx_records_timestamp ON records(timestamp);
            ";
            cmd.ExecuteNonQuery();

            // Run migrations for existing databases
            MigrateSchema();

            Log.Debug("Database tables created/verified");
        }
    }

    /// <summary>
    /// Check if this database was created by the Python version.
    /// Python uses time_performed instead of timestamp.
    /// </summary>
    private bool IsPythonDatabase()
    {
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(records)";
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                var columnName = reader.GetString(1);
                if (columnName == "time_performed")
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Migrate a Python database to C# format.
    /// </summary>
    private void MigratePythonDatabase()
    {
        try
        {
            // Step 1: Add missing columns to shifts table
            AddColumnIfMissing("shifts", "shift_name", "TEXT");

            // Step 2: Handle is_current -> shift_end conversion for shifts
            // In Python, is_current=1 means active shift (no shift_end)
            // First check if is_current column exists
            if (ColumnExists("shifts", "is_current"))
            {
                using var cmd = _connection!.CreateCommand();
                // For shifts where is_current=1, ensure shift_end is NULL
                cmd.CommandText = "UPDATE shifts SET shift_end = NULL WHERE is_current = 1";
                cmd.ExecuteNonQuery();
                Log.Information("Converted is_current=1 shifts to have NULL shift_end");
            }

            // Step 3: Migrate records table - rename time_performed to timestamp
            if (ColumnExists("records", "time_performed") && !ColumnExists("records", "timestamp"))
            {
                // SQLite doesn't support RENAME COLUMN directly in older versions,
                // so we need to recreate the table
                using var transaction = _connection!.BeginTransaction();
                try
                {
                    using var cmd = _connection.CreateCommand();

                    // Create new records table with correct schema
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS records_new (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            shift_id INTEGER NOT NULL,
                            accession TEXT NOT NULL,
                            procedure TEXT,
                            study_type TEXT,
                            rvu REAL,
                            timestamp TEXT NOT NULL,
                            patient_class TEXT DEFAULT 'Unknown',
                            accession_count INTEGER DEFAULT 1,
                            source TEXT DEFAULT 'Mosaic',
                            metadata TEXT,
                            duration_seconds REAL,
                            from_multi_accession INTEGER DEFAULT 0,
                            FOREIGN KEY (shift_id) REFERENCES shifts(id)
                        )";
                    cmd.ExecuteNonQuery();

                    // Copy data from old table, mapping time_performed to timestamp
                    cmd.CommandText = @"
                        INSERT INTO records_new (id, shift_id, accession, procedure, study_type, rvu, timestamp,
                            patient_class, duration_seconds, from_multi_accession)
                        SELECT id, shift_id, accession, procedure, study_type, rvu,
                            COALESCE(time_performed, datetime('now')),
                            COALESCE(patient_class, 'Unknown'),
                            duration_seconds,
                            COALESCE(from_multi_accession, 0)
                        FROM records";
                    cmd.ExecuteNonQuery();

                    // Drop old table and rename new one
                    cmd.CommandText = "DROP TABLE records";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = "ALTER TABLE records_new RENAME TO records";
                    cmd.ExecuteNonQuery();

                    transaction.Commit();
                    Log.Information("Migrated records table from Python format (time_performed -> timestamp)");
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }

            // Step 4: Add missing columns that Python might not have
            AddColumnIfMissing("records", "accession_count", "INTEGER DEFAULT 1");
            AddColumnIfMissing("records", "source", "TEXT DEFAULT 'Mosaic'");
            AddColumnIfMissing("records", "metadata", "TEXT");

            Log.Information("Python database migration completed");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error migrating Python database");
            throw;
        }
    }

    /// <summary>
    /// Check if a column exists in a table.
    /// </summary>
    private bool ColumnExists(string tableName, string columnName)
    {
        using var cmd = _connection!.CreateCommand();
        // Safe: tableName is always a hardcoded string from internal callers; PRAGMA doesn't support parameters
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            if (reader.GetString(1) == columnName)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Add a column if it doesn't exist.
    /// </summary>
    private void AddColumnIfMissing(string tableName, string columnName, string columnDef)
    {
        if (!ColumnExists(tableName, columnName))
        {
            try
            {
                using var cmd = _connection!.CreateCommand();
                // Safe: tableName/columnName/columnDef are always hardcoded strings; ALTER TABLE doesn't support parameters for identifiers
                cmd.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDef}";
                cmd.ExecuteNonQuery();
                Log.Information("Added {Column} column to {Table} table", columnName, tableName);
            }
            catch (SqliteException ex)
            {
                Log.Warning(ex, "Could not add column {Column} to {Table}", columnName, tableName);
            }
        }
    }

    private void MigrateSchema()
    {
        // Add duration_seconds column if missing
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "ALTER TABLE records ADD COLUMN duration_seconds REAL";
            cmd.ExecuteNonQuery();
            Log.Information("Added duration_seconds column to records table");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { /* Column already exists */ }

        // Add from_multi_accession column if missing
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "ALTER TABLE records ADD COLUMN from_multi_accession INTEGER DEFAULT 0";
            cmd.ExecuteNonQuery();
            Log.Information("Added from_multi_accession column to records table");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { /* Column already exists */ }

        // Add time_finished column if missing (Python parity)
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "ALTER TABLE records ADD COLUMN time_finished TEXT";
            cmd.ExecuteNonQuery();
            Log.Information("Added time_finished column to records table");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { /* Column already exists */ }

        // Add multi_accession_group column if missing (Python parity)
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "ALTER TABLE records ADD COLUMN multi_accession_group TEXT";
            cmd.ExecuteNonQuery();
            Log.Information("Added multi_accession_group column to records table");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { /* Column already exists */ }

        // Add is_current column to shifts if missing (for Python/external tool compatibility)
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "ALTER TABLE shifts ADD COLUMN is_current INTEGER DEFAULT 0";
            cmd.ExecuteNonQuery();
            Log.Information("Added is_current column to shifts table");

            // Sync is_current with shift_end IS NULL for existing shifts
            using var syncCmd = _connection!.CreateCommand();
            syncCmd.CommandText = "UPDATE shifts SET is_current = CASE WHEN shift_end IS NULL THEN 1 ELSE 0 END";
            syncCmd.ExecuteNonQuery();
            Log.Information("Synced is_current values based on shift_end");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { /* Column already exists */ }

        // Add has_critical_result column if missing (MosaicTools critical results integration)
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "ALTER TABLE records ADD COLUMN has_critical_result INTEGER DEFAULT 0";
            cmd.ExecuteNonQuery();
            Log.Information("Added has_critical_result column to records table");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { /* Column already exists */ }
    }

    #region Shift Operations

    /// <summary>
    /// Get the current active shift (no end time).
    /// Also cleans up any orphaned "current" shifts if multiple exist.
    /// </summary>
    public Shift? GetCurrentShift()
    {
        lock (_lock)
        {
            // First, check for and fix multiple "current" shifts
            CleanupOrphanedCurrentShifts();

            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT * FROM shifts WHERE shift_end IS NULL ORDER BY id DESC LIMIT 1";
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var shift = ReadShift(reader);
                Log.Debug("GetCurrentShift: Found active shift {Id} started at {Start}", shift.Id, shift.ShiftStart);
                return shift;
            }
            Log.Debug("GetCurrentShift: No active shift found (all shifts have end times)");
            return null;
        }
    }

    /// <summary>
    /// Clean up orphaned "current" shifts - if multiple shifts have no end time,
    /// keep only the most recent one. Others are either deleted (if empty) or
    /// ended 10 minutes after their last study.
    /// </summary>
    private void CleanupOrphanedCurrentShifts()
    {
        // Get all shifts without end times
        var orphanedShifts = new List<Shift>();
        using (var cmd = _connection!.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM shifts WHERE shift_end IS NULL ORDER BY id DESC";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                orphanedShifts.Add(ReadShift(reader));
            }
        }

        // If only 0 or 1 current shift, nothing to clean up
        if (orphanedShifts.Count <= 1)
            return;

        Log.Warning("Found {Count} orphaned 'current' shifts - cleaning up", orphanedShifts.Count);

        // Keep the most recent one (first in list since ordered DESC), clean up all others
        using var transaction = _connection!.BeginTransaction();
        try
        {
            for (int i = 1; i < orphanedShifts.Count; i++)
            {
                var orphan = orphanedShifts[i];

                // Check if this orphan has any studies
                var studyCount = GetRecordCountForShift(orphan.Id);

                if (studyCount == 0)
                {
                    // No studies - delete the empty shift entirely
                    using var deleteCmd = _connection!.CreateCommand();
                    deleteCmd.CommandText = "DELETE FROM shifts WHERE id = @id";
                    deleteCmd.Parameters.AddWithValue("@id", orphan.Id);
                    deleteCmd.ExecuteNonQuery();

                    Log.Information("Deleted empty orphaned shift {Id} (started {Start}, no studies)",
                        orphan.Id, orphan.ShiftStart);
                }
                else
                {
                    // Has studies - find the last one and end 10 minutes after
                    DateTime endTime;
                    using (var cmd = _connection!.CreateCommand())
                    {
                        cmd.CommandText = "SELECT MAX(timestamp) FROM records WHERE shift_id = @shiftId";
                        cmd.Parameters.AddWithValue("@shiftId", orphan.Id);
                        var result = cmd.ExecuteScalar();

                        if (result == null || result == DBNull.Value)
                        {
                            // No timestamp found despite studyCount > 0 (data corruption or race)
                            endTime = orphan.ShiftStart.AddMinutes(10);
                        }
                        else
                        {
                            var lastStudy = DateTime.Parse(result.ToString()!);
                            endTime = lastStudy.AddMinutes(10);
                        }
                    }

                    // Update the shift with the end time and clear is_current
                    using (var cmd = _connection!.CreateCommand())
                    {
                        cmd.CommandText = "UPDATE shifts SET shift_end = @end, is_current = 0 WHERE id = @id";
                        cmd.Parameters.AddWithValue("@end", endTime.ToString("o"));
                        cmd.Parameters.AddWithValue("@id", orphan.Id);
                        cmd.ExecuteNonQuery();
                    }

                    Log.Information("Auto-ended orphaned shift {Id} (started {Start}) at {End} with {Count} studies",
                        orphan.Id, orphan.ShiftStart, endTime, studyCount);
                }
            }
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Start a new shift.
    /// </summary>
    public int StartShift(DateTime shiftStart, DateTime? effectiveStart = null, DateTime? projectedEnd = null, string? shiftName = null)
    {
        lock (_lock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO shifts (shift_start, effective_shift_start, projected_shift_end, shift_name, is_current)
                VALUES (@start, @effective, @projected, @name, 1);
                SELECT last_insert_rowid();
            ";
            cmd.Parameters.AddWithValue("@start", shiftStart.ToString("o"));
            cmd.Parameters.AddWithValue("@effective", effectiveStart?.ToString("o") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@projected", projectedEnd?.ToString("o") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@name", shiftName ?? (object)DBNull.Value);

            var id = Convert.ToInt32(cmd.ExecuteScalar());
            Log.Information("Started new shift {Id} at {Start}", id, shiftStart);
            return id;
        }
    }

    /// <summary>
    /// End the current shift. If no studies were recorded, deletes the shift instead.
    /// </summary>
    public int? EndCurrentShift(DateTime? shiftEnd = null)
    {
        lock (_lock)
        {
            var current = GetCurrentShift();
            if (current == null) return null;

            // Check if shift has any studies
            var studyCount = GetRecordCountForShift(current.Id);

            if (studyCount == 0)
            {
                // No studies - delete the empty shift entirely
                using var deleteCmd = _connection!.CreateCommand();
                deleteCmd.CommandText = "DELETE FROM shifts WHERE id = @id";
                deleteCmd.Parameters.AddWithValue("@id", current.Id);
                deleteCmd.ExecuteNonQuery();

                Log.Information("Deleted empty shift {Id} (no studies recorded)", current.Id);
                return null;
            }

            var end = shiftEnd ?? DateTime.Now;
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "UPDATE shifts SET shift_end = @end, is_current = 0 WHERE id = @id";
            cmd.Parameters.AddWithValue("@end", end.ToString("o"));
            cmd.Parameters.AddWithValue("@id", current.Id);
            cmd.ExecuteNonQuery();

            Log.Information("Ended shift {Id} at {End} with {Count} studies", current.Id, end, studyCount);
            return current.Id;
        }
    }

    /// <summary>
    /// Get all completed (historical) shifts.
    /// </summary>
    public List<Shift> GetAllShifts()
    {
        lock (_lock)
        {
            var shifts = new List<Shift>();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT * FROM shifts WHERE shift_end IS NOT NULL ORDER BY shift_start DESC";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                shifts.Add(ReadShift(reader));
            }
            return shifts;
        }
    }

    /// <summary>
    /// Get a shift by ID.
    /// </summary>
    public Shift? GetShiftById(int shiftId)
    {
        lock (_lock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT * FROM shifts WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", shiftId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadShift(reader) : null;
        }
    }

    /// <summary>
    /// Delete a shift and all its records.
    /// </summary>
    public void DeleteShift(int shiftId)
    {
        lock (_lock)
        {
            using var transaction = _connection!.BeginTransaction();
            try
            {
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM records WHERE shift_id = @id";
                    cmd.Parameters.AddWithValue("@id", shiftId);
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM shifts WHERE id = @id";
                    cmd.Parameters.AddWithValue("@id", shiftId);
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
                Log.Information("Deleted shift {Id} and its records", shiftId);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    /// <summary>
    /// Get shifts within a date range.
    /// </summary>
    public List<Shift> GetShiftsInDateRange(DateTime startDate, DateTime endDate)
    {
        lock (_lock)
        {
            var shifts = new List<Shift>();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                SELECT * FROM shifts
                WHERE shift_start >= @start AND shift_start <= @end
                ORDER BY shift_start DESC
            ";
            cmd.Parameters.AddWithValue("@start", startDate.ToString("o"));
            cmd.Parameters.AddWithValue("@end", endDate.ToString("o"));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                shifts.Add(ReadShift(reader));
            }
            return shifts;
        }
    }

    /// <summary>
    /// Delete empty shifts (shifts with no records) within a date range.
    /// </summary>
    public int DeleteEmptyShifts(DateTime startDate, DateTime endDate)
    {
        lock (_lock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM shifts
                WHERE id NOT IN (SELECT DISTINCT shift_id FROM records)
                AND shift_start >= @start AND shift_start <= @end
            ";
            cmd.Parameters.AddWithValue("@start", startDate.ToString("o"));
            cmd.Parameters.AddWithValue("@end", endDate.ToString("o"));
            var count = cmd.ExecuteNonQuery();
            if (count > 0)
            {
                Log.Information("Deleted {Count} empty shifts", count);
            }
            return count;
        }
    }

    /// <summary>
    /// Clear all data from the database.
    /// </summary>
    public void ClearAllData()
    {
        lock (_lock)
        {
            using var transaction = _connection!.BeginTransaction();
            try
            {
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM records";
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM shifts";
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
                Log.Information("Cleared all data from database");
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    /// <summary>
    /// Update effective start and projected end times for current shift.
    /// </summary>
    public void UpdateCurrentShiftTimes(DateTime? effectiveStart = null, DateTime? projectedEnd = null)
    {
        lock (_lock)
        {
            var current = GetCurrentShift();
            if (current == null) return;

            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                UPDATE shifts 
                SET effective_shift_start = COALESCE(@effective, effective_shift_start),
                    projected_shift_end = COALESCE(@projected, projected_shift_end)
                WHERE id = @id
            ";
            cmd.Parameters.AddWithValue("@effective", effectiveStart?.ToString("o") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@projected", projectedEnd?.ToString("o") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@id", current.Id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Update shift_end for a specific shift by ID.
    /// </summary>
    public void UpdateShiftEnd(int shiftId, DateTime newEnd)
    {
        lock (_lock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "UPDATE shifts SET shift_end = @end WHERE id = @id";
            cmd.Parameters.AddWithValue("@end", newEnd.ToString("o"));
            cmd.Parameters.AddWithValue("@id", shiftId);
            cmd.ExecuteNonQuery();
        }
    }

    private Shift ReadShift(SqliteDataReader reader)
    {
        return new Shift
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            ShiftStart = DateTime.Parse(reader.GetString(reader.GetOrdinal("shift_start"))),
            ShiftEnd = reader.IsDBNull(reader.GetOrdinal("shift_end"))
                ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("shift_end"))),
            EffectiveShiftStart = reader.IsDBNull(reader.GetOrdinal("effective_shift_start"))
                ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("effective_shift_start"))),
            ProjectedShiftEnd = reader.IsDBNull(reader.GetOrdinal("projected_shift_end"))
                ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("projected_shift_end"))),
            ShiftName = reader.IsDBNull(reader.GetOrdinal("shift_name"))
                ? null : reader.GetString(reader.GetOrdinal("shift_name"))
        };
    }

    #endregion

    #region Record Operations

    /// <summary>
    /// Add a study record to a shift.
    /// </summary>
    public int AddRecord(int shiftId, StudyRecord record)
    {
        lock (_lock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO records (shift_id, accession, procedure, study_type, rvu, timestamp, time_finished, patient_class, accession_count, source, metadata, duration_seconds, from_multi_accession, multi_accession_group, has_critical_result)
                VALUES (@shiftId, @accession, @procedure, @studyType, @rvu, @timestamp, @timeFinished, @patientClass, @accessionCount, @source, @metadata, @duration, @fromMulti, @multiGroup, @hasCritical);
                SELECT last_insert_rowid();
            ";
            cmd.Parameters.AddWithValue("@shiftId", shiftId);
            cmd.Parameters.AddWithValue("@accession", record.Accession);
            cmd.Parameters.AddWithValue("@procedure", record.Procedure);
            cmd.Parameters.AddWithValue("@studyType", record.StudyType);
            cmd.Parameters.AddWithValue("@rvu", record.Rvu);
            cmd.Parameters.AddWithValue("@timestamp", record.Timestamp.ToString("o"));
            cmd.Parameters.AddWithValue("@timeFinished", record.TimeFinished?.ToString("o") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@patientClass", record.PatientClass);
            cmd.Parameters.AddWithValue("@accessionCount", record.AccessionCount);
            cmd.Parameters.AddWithValue("@source", record.Source);
            cmd.Parameters.AddWithValue("@metadata", record.Metadata ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@duration", record.DurationSeconds ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@fromMulti", record.FromMultiAccession ? 1 : 0);
            cmd.Parameters.AddWithValue("@multiGroup", record.MultiAccessionGroup ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@hasCritical", record.HasCriticalResult ? 1 : 0);

            var id = Convert.ToInt32(cmd.ExecuteScalar());
            Log.Debug("Added record {Id}: {Accession} - {StudyType}{Critical}", id, record.Accession, record.StudyType,
                record.HasCriticalResult ? " (CRITICAL)" : "");
            return id;
        }
    }

    /// <summary>
    /// Add multiple records in a single transaction (batch insert).
    /// </summary>
    public void BatchAddRecords(int shiftId, IEnumerable<StudyRecord> records)
    {
        lock (_lock)
        {
            using var transaction = _connection!.BeginTransaction();
            try
            {
                var count = 0;
                foreach (var record in records)
                {
                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO records (shift_id, accession, procedure, study_type, rvu, timestamp, time_finished, patient_class, accession_count, source, metadata, duration_seconds, from_multi_accession, multi_accession_group, has_critical_result)
                        VALUES (@shiftId, @accession, @procedure, @studyType, @rvu, @timestamp, @timeFinished, @patientClass, @accessionCount, @source, @metadata, @duration, @fromMulti, @multiGroup, @hasCritical)
                    ";
                    cmd.Parameters.AddWithValue("@shiftId", shiftId);
                    cmd.Parameters.AddWithValue("@accession", record.Accession);
                    cmd.Parameters.AddWithValue("@procedure", record.Procedure);
                    cmd.Parameters.AddWithValue("@studyType", record.StudyType);
                    cmd.Parameters.AddWithValue("@rvu", record.Rvu);
                    cmd.Parameters.AddWithValue("@timestamp", record.Timestamp.ToString("o"));
                    cmd.Parameters.AddWithValue("@timeFinished", record.TimeFinished?.ToString("o") ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@patientClass", record.PatientClass);
                    cmd.Parameters.AddWithValue("@accessionCount", record.AccessionCount);
                    cmd.Parameters.AddWithValue("@source", record.Source);
                    cmd.Parameters.AddWithValue("@metadata", record.Metadata ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@duration", record.DurationSeconds ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@fromMulti", record.FromMultiAccession ? 1 : 0);
                    cmd.Parameters.AddWithValue("@multiGroup", record.MultiAccessionGroup ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@hasCritical", record.HasCriticalResult ? 1 : 0);
                    cmd.ExecuteNonQuery();
                    count++;
                }
                transaction.Commit();
                Log.Information("Batch added {Count} records to shift {ShiftId}", count, shiftId);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    /// <summary>
    /// Get all records for a specific shift.
    /// </summary>
    public List<StudyRecord> GetRecordsForShift(int shiftId)
    {
        lock (_lock)
        {
            var records = new List<StudyRecord>();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT * FROM records WHERE shift_id = @shiftId ORDER BY timestamp DESC";
            cmd.Parameters.AddWithValue("@shiftId", shiftId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                records.Add(ReadRecord(reader));
            }
            return records;
        }
    }

    /// <summary>
    /// Get records for the current active shift.
    /// </summary>
    public List<StudyRecord> GetCurrentShiftRecords()
    {
        var current = GetCurrentShift();
        return current == null ? new List<StudyRecord>() : GetRecordsForShift(current.Id);
    }

    /// <summary>
    /// Find a record by accession within a shift.
    /// </summary>
    public StudyRecord? FindRecordByAccession(int shiftId, string accession)
    {
        lock (_lock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT * FROM records WHERE shift_id = @shiftId AND accession = @accession LIMIT 1";
            cmd.Parameters.AddWithValue("@shiftId", shiftId);
            cmd.Parameters.AddWithValue("@accession", accession);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadRecord(reader) : null;
        }
    }

    /// <summary>
    /// Get all records within a date range.
    /// </summary>
    public List<StudyRecord> GetRecordsInDateRange(DateTime startDate, DateTime endDate)
    {
        lock (_lock)
        {
            var records = new List<StudyRecord>();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                SELECT * FROM records 
                WHERE timestamp >= @start AND timestamp <= @end
                ORDER BY timestamp DESC
            ";
            cmd.Parameters.AddWithValue("@start", startDate.ToString("o"));
            cmd.Parameters.AddWithValue("@end", endDate.ToString("o"));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                records.Add(ReadRecord(reader));
            }
            return records;
        }
    }

    /// <summary>
    /// Get all records from all shifts.
    /// </summary>
    public List<StudyRecord> GetAllRecords()
    {
        lock (_lock)
        {
            var records = new List<StudyRecord>();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT * FROM records ORDER BY timestamp DESC";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                records.Add(ReadRecord(reader));
            }
            return records;
        }
    }

    /// <summary>
    /// Delete a record by ID.
    /// </summary>
    public void DeleteRecord(int recordId)
    {
        lock (_lock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM records WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", recordId);
            cmd.ExecuteNonQuery();
            Log.Debug("Deleted record {Id}", recordId);
        }
    }

    /// <summary>
    /// Delete a record by accession within a shift.
    /// </summary>
    public void DeleteRecordByAccession(int shiftId, string accession)
    {
        lock (_lock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM records WHERE shift_id = @shiftId AND accession = @accession";
            cmd.Parameters.AddWithValue("@shiftId", shiftId);
            cmd.Parameters.AddWithValue("@accession", accession);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Assign a record to a different shift (for fixing orphan records).
    /// </summary>
    public void AssignRecordToShift(int recordId, int shiftId)
    {
        lock (_lock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "UPDATE records SET shift_id = @shiftId WHERE id = @id";
            cmd.Parameters.AddWithValue("@shiftId", shiftId);
            cmd.Parameters.AddWithValue("@id", recordId);
            cmd.ExecuteNonQuery();
            Log.Debug("Assigned record {RecordId} to shift {ShiftId}", recordId, shiftId);
        }
    }

    /// <summary>
    /// Delete records by accession hashes within a date range (for payroll reconciliation).
    /// </summary>
    public int DeleteRecordsByAccessions(IEnumerable<string> accessionHashes, DateTime startDate, DateTime endDate)
    {
        lock (_lock)
        {
            var count = 0;
            using var transaction = _connection!.BeginTransaction();
            try
            {
                foreach (var accession in accessionHashes)
                {
                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = @"
                        DELETE FROM records
                        WHERE accession = @accession
                        AND timestamp >= @start AND timestamp <= @end
                    ";
                    cmd.Parameters.AddWithValue("@accession", accession);
                    cmd.Parameters.AddWithValue("@start", startDate.ToString("o"));
                    cmd.Parameters.AddWithValue("@end", endDate.ToString("o"));
                    count += cmd.ExecuteNonQuery();
                }
                transaction.Commit();
                Log.Information("Deleted {Count} records by accession in date range", count);
                return count;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    /// <summary>
    /// Get average duration by study type for a date range (for estimating durations).
    /// </summary>
    public Dictionary<string, double> GetAverageDurations(DateTime startDate, DateTime endDate)
    {
        lock (_lock)
        {
            var result = new Dictionary<string, double>();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                SELECT study_type, AVG(duration_seconds) as avg_duration
                FROM records
                WHERE timestamp >= @start AND timestamp <= @end
                AND duration_seconds IS NOT NULL AND duration_seconds > 0
                GROUP BY study_type
            ";
            cmd.Parameters.AddWithValue("@start", startDate.ToString("o"));
            cmd.Parameters.AddWithValue("@end", endDate.ToString("o"));

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var studyType = reader.GetString(0);
                var avgDuration = reader.IsDBNull(1) ? 120.0 : reader.GetDouble(1);
                result[studyType] = avgDuration;
            }
            return result;
        }
    }

    /// <summary>
    /// Update an existing record.
    /// </summary>
    public void UpdateRecord(StudyRecord record)
    {
        lock (_lock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                UPDATE records
                SET procedure = @procedure, study_type = @studyType, rvu = @rvu,
                    patient_class = @patientClass, accession_count = @accessionCount,
                    source = @source, metadata = @metadata, duration_seconds = @duration,
                    from_multi_accession = @fromMulti, time_finished = @timeFinished,
                    multi_accession_group = @multiGroup, has_critical_result = @hasCritical
                WHERE id = @id
            ";
            cmd.Parameters.AddWithValue("@procedure", record.Procedure);
            cmd.Parameters.AddWithValue("@studyType", record.StudyType);
            cmd.Parameters.AddWithValue("@rvu", record.Rvu);
            cmd.Parameters.AddWithValue("@patientClass", record.PatientClass);
            cmd.Parameters.AddWithValue("@accessionCount", record.AccessionCount);
            cmd.Parameters.AddWithValue("@source", record.Source);
            cmd.Parameters.AddWithValue("@metadata", record.Metadata ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@duration", record.DurationSeconds ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@fromMulti", record.FromMultiAccession ? 1 : 0);
            cmd.Parameters.AddWithValue("@timeFinished", record.TimeFinished?.ToString("o") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@multiGroup", record.MultiAccessionGroup ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@hasCritical", record.HasCriticalResult ? 1 : 0);
            cmd.Parameters.AddWithValue("@id", record.Id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Update timestamps by an offset for payroll synchronization.
    /// </summary>
    public int BatchUpdateTimestampsByOffset(int shiftId, TimeSpan offset)
    {
        lock (_lock)
        {
            var records = GetRecordsForShift(shiftId);
            var count = 0;

            using var transaction = _connection!.BeginTransaction();
            try
            {
                foreach (var record in records)
                {
                    var newTimestamp = record.Timestamp.Add(offset);
                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = "UPDATE records SET timestamp = @timestamp WHERE id = @id";
                    cmd.Parameters.AddWithValue("@timestamp", newTimestamp.ToString("o"));
                    cmd.Parameters.AddWithValue("@id", record.Id);
                    cmd.ExecuteNonQuery();
                    count++;
                }
                transaction.Commit();
                Log.Information("Updated {Count} record timestamps by {Offset}", count, offset);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }

            return count;
        }
    }

    /// <summary>
    /// Batch update study types and RVU values (for database repair).
    /// Python parity: uses single transaction for efficiency.
    /// </summary>
    public int BatchUpdateStudyTypes(List<(int Id, string NewType, double NewRvu)> updates, Action<int, int>? progress = null)
    {
        lock (_lock)
        {
            var count = 0;
            var total = updates.Count;

            using var transaction = _connection!.BeginTransaction();
            try
            {
                foreach (var (id, newType, newRvu) in updates)
                {
                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = "UPDATE records SET study_type = @type, rvu = @rvu WHERE id = @id";
                    cmd.Parameters.AddWithValue("@type", newType);
                    cmd.Parameters.AddWithValue("@rvu", newRvu);
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.ExecuteNonQuery();
                    count++;
                    progress?.Invoke(count, total);
                }
                transaction.Commit();
                Log.Information("Batch updated {Count} record study types", count);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }

            return count;
        }
    }

    /// <summary>
    /// Batch update time_finished and rvu for matched records (payroll reconciliation).
    /// Updates records by accession hash within a date range.
    /// </summary>
    public int BatchUpdatePayrollData(List<(string Accession, DateTime TimeFinished, double Rvu)> updates)
    {
        lock (_lock)
        {
            var count = 0;
            using var transaction = _connection!.BeginTransaction();
            try
            {
                foreach (var (accession, timeFinished, rvu) in updates)
                {
                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = "UPDATE records SET time_finished = @tf, rvu = @rvu WHERE accession = @acc";
                    cmd.Parameters.AddWithValue("@tf", timeFinished.ToString("o"));
                    cmd.Parameters.AddWithValue("@rvu", rvu);
                    cmd.Parameters.AddWithValue("@acc", accession);
                    count += cmd.ExecuteNonQuery();
                }
                transaction.Commit();
                Log.Information("Batch updated payroll data for {Count} records", count);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
            return count;
        }
    }

    /// <summary>
    /// Insert a completed historical shift without records.
    /// </summary>
    public int InsertHistoricalShift(DateTime shiftStart, DateTime shiftEnd, string? shiftName = null)
    {
        lock (_lock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO shifts (shift_start, shift_end, shift_name)
                VALUES (@start, @end, @name);
                SELECT last_insert_rowid();
            ";
            cmd.Parameters.AddWithValue("@start", shiftStart.ToString("o"));
            cmd.Parameters.AddWithValue("@end", shiftEnd.ToString("o"));
            cmd.Parameters.AddWithValue("@name", shiftName ?? (object)DBNull.Value);

            var shiftId = Convert.ToInt32(cmd.ExecuteScalar());
            Log.Information("Inserted historical shift {Id}: {Start} to {End}", shiftId, shiftStart, shiftEnd);
            return shiftId;
        }
    }

    /// <summary>
    /// Insert a completed historical shift with records (for audit reconciliation).
    /// </summary>
    public int InsertHistoricalShift(DateTime shiftStart, DateTime shiftEnd, IEnumerable<StudyRecord> records, string? shiftName = null)
    {
        lock (_lock)
        {
            using var transaction = _connection!.BeginTransaction();
            try
            {
                // Insert shift
                int shiftId;
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = @"
                        INSERT INTO shifts (shift_start, shift_end, shift_name)
                        VALUES (@start, @end, @name);
                        SELECT last_insert_rowid();
                    ";
                    cmd.Parameters.AddWithValue("@start", shiftStart.ToString("o"));
                    cmd.Parameters.AddWithValue("@end", shiftEnd.ToString("o"));
                    cmd.Parameters.AddWithValue("@name", shiftName ?? (object)DBNull.Value);
                    shiftId = Convert.ToInt32(cmd.ExecuteScalar());
                }

                // Insert records
                var recordCount = 0;
                foreach (var record in records)
                {
                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO records (shift_id, accession, procedure, study_type, rvu, timestamp, time_finished, patient_class, accession_count, source, metadata, duration_seconds, from_multi_accession, multi_accession_group, has_critical_result)
                        VALUES (@shiftId, @accession, @procedure, @studyType, @rvu, @timestamp, @timeFinished, @patientClass, @accessionCount, @source, @metadata, @duration, @fromMulti, @multiGroup, @hasCritical)
                    ";
                    cmd.Parameters.AddWithValue("@shiftId", shiftId);
                    cmd.Parameters.AddWithValue("@accession", record.Accession);
                    cmd.Parameters.AddWithValue("@procedure", record.Procedure);
                    cmd.Parameters.AddWithValue("@studyType", record.StudyType);
                    cmd.Parameters.AddWithValue("@rvu", record.Rvu);
                    cmd.Parameters.AddWithValue("@timestamp", record.Timestamp.ToString("o"));
                    cmd.Parameters.AddWithValue("@timeFinished", record.TimeFinished?.ToString("o") ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@patientClass", record.PatientClass);
                    cmd.Parameters.AddWithValue("@accessionCount", record.AccessionCount);
                    cmd.Parameters.AddWithValue("@source", record.Source);
                    cmd.Parameters.AddWithValue("@metadata", record.Metadata ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@duration", record.DurationSeconds ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@fromMulti", record.FromMultiAccession ? 1 : 0);
                    cmd.Parameters.AddWithValue("@multiGroup", record.MultiAccessionGroup ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@hasCritical", record.HasCriticalResult ? 1 : 0);
                    cmd.ExecuteNonQuery();
                    recordCount++;
                }

                transaction.Commit();
                Log.Information("Inserted historical shift {Id} with {Count} records", shiftId, recordCount);
                return shiftId;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    /// <summary>
    /// Get statistics grouped by study type.
    /// </summary>
    public Dictionary<string, (int Count, double TotalRvu)> GetStatsByStudyType(int? shiftId = null)
    {
        lock (_lock)
        {
            var stats = new Dictionary<string, (int Count, double TotalRvu)>();
            using var cmd = _connection!.CreateCommand();

            if (shiftId.HasValue)
            {
                cmd.CommandText = @"
                    SELECT study_type, COUNT(*) as count, COALESCE(SUM(rvu), 0) as total_rvu
                    FROM records WHERE shift_id = @shiftId
                    GROUP BY study_type ORDER BY total_rvu DESC
                ";
                cmd.Parameters.AddWithValue("@shiftId", shiftId.Value);
            }
            else
            {
                cmd.CommandText = @"
                    SELECT study_type, COUNT(*) as count, COALESCE(SUM(rvu), 0) as total_rvu
                    FROM records
                    GROUP BY study_type ORDER BY total_rvu DESC
                ";
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var studyType = reader.IsDBNull(0) ? "Unknown" : reader.GetString(0);
                var count = reader.GetInt32(1);
                var totalRvu = reader.GetDouble(2);
                stats[studyType] = (count, totalRvu);
            }
            return stats;
        }
    }

    /// <summary>
    /// Get average duration by study type.
    /// </summary>
    public Dictionary<string, double> GetAverageDurations()
    {
        lock (_lock)
        {
            var durations = new Dictionary<string, double>();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                SELECT study_type, AVG(duration_seconds) as avg_duration
                FROM records
                WHERE duration_seconds IS NOT NULL AND duration_seconds > 0
                GROUP BY study_type
            ";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var studyType = reader.IsDBNull(0) ? "Unknown" : reader.GetString(0);
                var avgDuration = reader.IsDBNull(1) ? 0 : reader.GetDouble(1);
                durations[studyType] = avgDuration;
            }
            return durations;
        }
    }

    /// <summary>
    /// Get accessions for a date range (for audit comparison).
    /// </summary>
    public List<string> GetAccessionsInRange(DateTime startDate, DateTime endDate)
    {
        lock (_lock)
        {
            var accessions = new List<string>();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                SELECT accession FROM records
                WHERE timestamp >= @start AND timestamp <= @end
            ";
            cmd.Parameters.AddWithValue("@start", startDate.ToString("o"));
            cmd.Parameters.AddWithValue("@end", endDate.ToString("o"));

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                accessions.Add(reader.GetString(0));
            }
            return accessions;
        }
    }

    /// <summary>
    /// Get total RVU for a shift.
    /// </summary>
    public double GetTotalRvuForShift(int shiftId)
    {
        lock (_lock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(rvu), 0) FROM records WHERE shift_id = @shiftId";
            cmd.Parameters.AddWithValue("@shiftId", shiftId);
            return Convert.ToDouble(cmd.ExecuteScalar());
        }
    }

    /// <summary>
    /// Get record count for a shift.
    /// </summary>
    public int GetRecordCountForShift(int shiftId)
    {
        lock (_lock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM records WHERE shift_id = @shiftId";
            cmd.Parameters.AddWithValue("@shiftId", shiftId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    private StudyRecord ReadRecord(SqliteDataReader reader)
    {
        var record = new StudyRecord
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            ShiftId = reader.GetInt32(reader.GetOrdinal("shift_id")),
            Accession = reader.GetString(reader.GetOrdinal("accession")),
            Procedure = reader.IsDBNull(reader.GetOrdinal("procedure"))
                ? string.Empty : reader.GetString(reader.GetOrdinal("procedure")),
            StudyType = reader.IsDBNull(reader.GetOrdinal("study_type"))
                ? string.Empty : reader.GetString(reader.GetOrdinal("study_type")),
            Rvu = reader.IsDBNull(reader.GetOrdinal("rvu"))
                ? 0 : reader.GetDouble(reader.GetOrdinal("rvu")),
            Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("timestamp"))),
            PatientClass = reader.IsDBNull(reader.GetOrdinal("patient_class"))
                ? "Unknown" : reader.GetString(reader.GetOrdinal("patient_class")),
            AccessionCount = reader.IsDBNull(reader.GetOrdinal("accession_count"))
                ? 1 : reader.GetInt32(reader.GetOrdinal("accession_count")),
            Source = reader.IsDBNull(reader.GetOrdinal("source"))
                ? "Mosaic" : reader.GetString(reader.GetOrdinal("source")),
            Metadata = reader.IsDBNull(reader.GetOrdinal("metadata"))
                ? null : reader.GetString(reader.GetOrdinal("metadata"))
        };

        // Read new columns with fallback for older databases
        // GetOrdinal throws InvalidOperationException when the column doesn't exist
        try
        {
            var durationOrdinal = reader.GetOrdinal("duration_seconds");
            record.DurationSeconds = reader.IsDBNull(durationOrdinal) ? null : reader.GetDouble(durationOrdinal);
        }
        catch (InvalidOperationException) { /* Column doesn't exist in older schema */ }

        try
        {
            var fromMultiOrdinal = reader.GetOrdinal("from_multi_accession");
            record.FromMultiAccession = !reader.IsDBNull(fromMultiOrdinal) && reader.GetInt32(fromMultiOrdinal) == 1;
        }
        catch (InvalidOperationException) { /* Column doesn't exist in older schema */ }

        try
        {
            var timeFinishedOrdinal = reader.GetOrdinal("time_finished");
            if (!reader.IsDBNull(timeFinishedOrdinal))
            {
                var timeFinishedStr = reader.GetString(timeFinishedOrdinal);
                if (!string.IsNullOrEmpty(timeFinishedStr))
                {
                    record.TimeFinished = DateTime.Parse(timeFinishedStr);
                }
            }
        }
        catch (InvalidOperationException) { /* Column doesn't exist in older schema */ }

        try
        {
            var multiGroupOrdinal = reader.GetOrdinal("multi_accession_group");
            record.MultiAccessionGroup = reader.IsDBNull(multiGroupOrdinal) ? null : reader.GetString(multiGroupOrdinal);
        }
        catch (InvalidOperationException) { /* Column doesn't exist in older schema */ }

        try
        {
            var hasCriticalOrdinal = reader.GetOrdinal("has_critical_result");
            record.HasCriticalResult = !reader.IsDBNull(hasCriticalOrdinal) && reader.GetInt32(hasCriticalOrdinal) == 1;
        }
        catch (InvalidOperationException) { /* Column doesn't exist in older schema */ }

        return record;
    }

    #endregion

    /// <summary>
    /// Checkpoint the WAL file, writing all pending changes to the main database file.
    /// This is important to call before backing up the database to ensure the backup is complete.
    /// </summary>
    /// <param name="truncate">If true, truncates the WAL file after checkpointing (TRUNCATE mode).
    /// If false, uses PASSIVE mode which doesn't block readers.</param>
    /// <returns>True if checkpoint succeeded, false otherwise.</returns>
    public bool Checkpoint(bool truncate = true)
    {
        lock (_lock)
        {
            try
            {
                using var cmd = _connection!.CreateCommand();
                // TRUNCATE mode: checkpoint and truncate WAL to zero bytes
                // PASSIVE mode: checkpoint without blocking, may not checkpoint all frames
                cmd.CommandText = truncate
                    ? "PRAGMA wal_checkpoint(TRUNCATE)"
                    : "PRAGMA wal_checkpoint(PASSIVE)";

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    // Returns: busy, log, checkpointed
                    var busy = reader.GetInt32(0);
                    var log = reader.GetInt32(1);
                    var checkpointed = reader.GetInt32(2);
                    Log.Information("WAL checkpoint: busy={Busy}, log_frames={Log}, checkpointed={Checkpointed}",
                        busy, log, checkpointed);
                    return busy == 0; // Success if not busy
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "WAL checkpoint failed");
                return false;
            }
        }
    }

    /// <summary>
    /// Close the database connection.
    /// </summary>
    public void Close()
    {
        _connection?.Close();
        _connection?.Dispose();
        _connection = null;
        Log.Debug("Database connection closed");
    }

    public void Dispose()
    {
        Close();
    }
}
