using System.IO;
using Microsoft.Data.Sqlite;
using RVUCounter.Core;
using Serilog;

namespace RVUCounter.Data;

/// <summary>
/// Provides TBWU (Time-Based Work Unit) lookup from the tbwu_rules.db SQLite database.
/// Matches procedure names case-insensitively with fallback to abbreviation normalization
/// and word-overlap fuzzy matching. Defaults to ER TBWU when patient class is unknown.
/// </summary>
public class TbwuLookup : IDisposable
{
    private readonly SqliteConnection? _connection;
    private readonly Dictionary<string, TbwuRow> _cache = new(StringComparer.OrdinalIgnoreCase);
    private List<string>? _allProcedureNames;
    private bool _disposed;

    public bool IsAvailable => _connection != null;

    // Abbreviation expansions for normalization (order matters: longer patterns first)
    private static readonly (string Abbrev, string Expansion)[] Abbreviations =
    {
        ("WWO", "WITHOUT THEN WITH"),
        ("W/O", "WITHOUT"),
        ("W/", "WITH"),
        ("WO ", "WITHOUT "),
        ("ABD ", "ABDOMEN "),
        ("ANGIO ", "ANGIOGRAPHY "),
    };

    // Words to strip during fuzzy matching (don't affect clinical meaning much)
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND", "THE", "OF", "OR", "A"
    };

    /// <summary>
    /// Per-procedure TBWU compensation multipliers (applied on top of TBWU value × rate).
    /// Only applies to TBWU-era studies. Most procedures default to 1.0.
    /// Keyed by canonical procedure name from tbwu_rules.db (case-insensitive).
    /// </summary>
    private static readonly Dictionary<string, double> TbwuCompensationMultipliers = new(StringComparer.OrdinalIgnoreCase)
    {
        { "CT BRAIN PERFUSION CTA HEAD WITH IV CONTRAST", 1.4 },
        { "CT HEAD NECK ANGIOGRAPHY WITH IV CONTRAST", 1.3 },
        { "CT BRAIN PERFUSION CTA HEAD NECK WITH IV CONTRAST", 1.6 },
        { "CT HEAD NECK ANGIOGRAPHY WITHOUT THEN WITH IV CONTRAST", 1.3 },
        { "CT BRAIN PERFUSION CTA HEAD NECK WITHOUT THEN WITH IV CONTRAST", 1.5 },
        { "MR BRAIN VENOGRAPHY WITHOUT IV CONTRAST", 1.4 },
        { "MR BRAIN ANGIOGRAPHY WITHOUT IV CONTRAST", 1.3 },
        { "MR NECK ANGIOGRAPHY WITHOUT IV CONTRAST", 1.4 },
        { "MR BRAIN WITHOUT IV CONTRAST", 1.25 },
        { "MR BRAIN VENOGRAPHY WITHOUT THEN WITH IV CONTRAST", 1.4 },
        { "CT HEAD PERFUSION WITH IV CONTRAST", 1.5 },
    };

    public TbwuLookup(string? baseDir = null)
    {
        var dir = baseDir ?? PlatformUtils.GetAppRoot();
        var dbPath = Config.GetTbwuDatabaseFile(dir);

        if (!File.Exists(dbPath))
        {
            Log.Warning("TBWU database not found at {Path}", dbPath);
            return;
        }

        try
        {
            _connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            _connection.Open();
            Log.Debug("Opened TBWU database from {Path}", dbPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to open TBWU database");
            _connection = null;
        }
    }

    /// <summary>
    /// Look up the TBWU value for a procedure name and patient class.
    /// Tries: 1) exact match, 2) normalized abbreviations, 3) fuzzy word-overlap.
    /// </summary>
    public double? GetTbwu(string procedureName, string patientClass)
    {
        if (_disposed || _connection == null || string.IsNullOrEmpty(procedureName))
            return null;

        var row = GetRow(procedureName);
        if (row == null) return null;

        return GetTbwuForPatientClass(row, patientClass);
    }

    /// <summary>
    /// Calculate total TBWU-based compensation for a set of study records.
    /// Uses the same $/RVU rate but substitutes TBWU for RVU.
    /// </summary>
    public double CalculateTotalTbwuCompensation(IEnumerable<Models.StudyRecord> records, string role)
    {
        return records.Sum(r => CalculateTbwuCompensation(r, role));
    }

    /// <summary>
    /// Calculate TBWU-based compensation for a single study.
    /// Applies per-procedure compensation multiplier for TBWU-era studies.
    /// </summary>
    public double CalculateTbwuCompensation(Models.StudyRecord record, string role)
    {
        if (_disposed || _connection == null || string.IsNullOrEmpty(record.Procedure))
            return 0.0;

        var row = GetRow(record.Procedure);
        if (row == null) return 0.0;

        var tbwu = GetTbwuForPatientClass(row, record.PatientClass);
        var time = record.TimeFinished ?? record.Timestamp;
        // TBWU compensation always uses TBWU rates (regardless of era)
        var rate = CompensationRates.GetTbwuRate(time, role);
        var comp = tbwu * rate * row.CompensationMultiplier;

        return comp;
    }

    /// <summary>
    /// Get the total TBWU for a set of records (sum of individual TBWUs).
    /// </summary>
    public double GetTotalTbwu(IEnumerable<Models.StudyRecord> records)
    {
        return records.Sum(r => GetTbwu(r.Procedure, r.PatientClass) ?? 0.0);
    }

    /// <summary>
    /// Get the compensation multiplier for a procedure (1.0 for most procedures).
    /// </summary>
    public double GetCompensationMultiplier(string procedureName)
    {
        if (string.IsNullOrEmpty(procedureName)) return 1.0;
        var row = GetRow(procedureName);
        return row?.CompensationMultiplier ?? 1.0;
    }

    private TbwuRow? GetRow(string procedureName)
    {
        // Check cache first (covers exact, normalized, and fuzzy results)
        if (_cache.TryGetValue(procedureName, out var cached))
            return cached;

        if (_connection == null) return null;

        // 1) Exact match (case-insensitive)
        var row = QueryExact(procedureName);
        if (row != null)
        {
            _cache[procedureName] = row;
            return row;
        }

        // 2) Normalize abbreviations and try exact match again
        var normalized = NormalizeProcedure(procedureName);
        if (!string.Equals(normalized, procedureName, StringComparison.OrdinalIgnoreCase))
        {
            row = QueryExact(normalized);
            if (row != null)
            {
                _cache[procedureName] = row;
                Log.Debug("TBWU fuzzy match (normalized): '{Original}' -> '{Normalized}'", procedureName, normalized);
                return row;
            }
        }

        // 3) Fuzzy word-overlap match against all procedure names
        var bestMatch = FindBestFuzzyMatch(normalized);
        if (bestMatch != null)
        {
            row = QueryExact(bestMatch);
            if (row != null)
            {
                _cache[procedureName] = row;
                Log.Information("TBWU fuzzy match: '{Original}' -> '{Match}'", procedureName, bestMatch);
                return row;
            }
        }

        Log.Debug("No TBWU match for procedure: '{Procedure}'", procedureName);
        return null;
    }

    private TbwuRow? QueryExact(string procedureName)
    {
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT procedure_name, er_tbwu, ip_tbwu, op_tbwu FROM tbwu_rules WHERE procedure_name = @name COLLATE NOCASE LIMIT 1";
            cmd.Parameters.AddWithValue("@name", procedureName);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var matchedName = reader.GetString(0);
                return new TbwuRow
                {
                    MatchedName = matchedName,
                    ErTbwu = reader.GetDouble(1),
                    IpTbwu = reader.GetDouble(2),
                    OpTbwu = reader.GetDouble(3),
                    CompensationMultiplier = TbwuCompensationMultipliers.GetValueOrDefault(matchedName, 1.0)
                };
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to lookup TBWU for {Procedure}", procedureName);
        }
        return null;
    }

    /// <summary>
    /// Expand common abbreviations to their full forms.
    /// </summary>
    private static string NormalizeProcedure(string name)
    {
        var result = name.ToUpperInvariant();
        foreach (var (abbrev, expansion) in Abbreviations)
        {
            result = result.Replace(abbrev, expansion);
        }
        // Collapse multiple spaces
        while (result.Contains("  "))
            result = result.Replace("  ", " ");
        return result.Trim();
    }

    /// <summary>
    /// Find the best matching procedure name using word-overlap (Jaccard-like) scoring.
    /// Only returns a match if the modality prefix matches and similarity >= 0.6.
    /// </summary>
    private string? FindBestFuzzyMatch(string normalizedInput)
    {
        if (_connection == null) return null;

        // Lazy-load all procedure names
        if (_allProcedureNames == null)
        {
            _allProcedureNames = new List<string>();
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT procedure_name FROM tbwu_rules";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    _allProcedureNames.Add(reader.GetString(0));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load TBWU procedure names for fuzzy matching");
                return null;
            }
        }

        var inputWords = GetSignificantWords(normalizedInput);
        if (inputWords.Count == 0) return null;

        // Extract modality prefix (first word: CT, MR, US, XR, etc.)
        var inputModality = inputWords[0];

        string? bestMatch = null;
        double bestScore = 0.6; // Minimum threshold

        foreach (var candidate in _allProcedureNames)
        {
            var candidateWords = GetSignificantWords(candidate.ToUpperInvariant());
            if (candidateWords.Count == 0) continue;

            // Modality must match
            if (!string.Equals(candidateWords[0], inputModality, StringComparison.OrdinalIgnoreCase))
                continue;

            // Jaccard similarity on word sets (excluding modality since it always matches)
            var inputSet = new HashSet<string>(inputWords.Skip(1), StringComparer.OrdinalIgnoreCase);
            var candidateSet = new HashSet<string>(candidateWords.Skip(1), StringComparer.OrdinalIgnoreCase);

            var intersection = inputSet.Count(w => candidateSet.Contains(w));
            var union = new HashSet<string>(inputSet, StringComparer.OrdinalIgnoreCase);
            union.UnionWith(candidateSet);

            if (union.Count == 0) continue;
            var score = (double)intersection / union.Count;

            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = candidate;
            }
        }

        return bestMatch;
    }

    /// <summary>
    /// Split into words, removing stop words and short noise.
    /// </summary>
    private static List<string> GetSignificantWords(string text)
    {
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1 && !StopWords.Contains(w))
            .ToList();
    }

    private static double GetTbwuForPatientClass(TbwuRow row, string patientClass)
    {
        var pc = (patientClass ?? "").ToUpperInvariant();
        if (pc.Contains("INPATIENT") || pc == "IP")
            return row.IpTbwu;
        if (pc.Contains("OUTPATIENT") || pc == "OP")
            return row.OpTbwu;
        // Default to ER for "ER", "Emergency", "Unknown", or anything else
        return row.ErTbwu;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _connection?.Close();
            _connection?.Dispose();
            _disposed = true;
        }
    }

    private class TbwuRow
    {
        public string MatchedName { get; set; } = "";
        public double ErTbwu { get; set; }
        public double IpTbwu { get; set; }
        public double OpTbwu { get; set; }
        public double CompensationMultiplier { get; set; } = 1.0;
    }
}
