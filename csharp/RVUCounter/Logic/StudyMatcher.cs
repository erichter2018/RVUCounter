using RVUCounter.Models;
using Serilog;

namespace RVUCounter.Logic;

/// <summary>
/// Matches procedure text to study types and RVU values.
/// Ported from Python study_matcher.py with full algorithm parity.
/// </summary>
public static class StudyMatcher
{
    // Keyword to study type mappings for modality detection
    private static readonly Dictionary<string, string> KeywordStudyTypes = new()
    {
        { "ct cap", "CT CAP" },
        { "ct ap", "CT AP" },
        { "cta", "CTA Brain" },
        { "ultrasound", "US Other" },
        { "mri", "MRI Other" },
        { "mr ", "MRI Other" },
        { "us ", "US Other" },
        { "x-ray", "XR Other" },
        { "xr ", "XR Other" },
        { "xr\t", "XR Other" },
        { "nuclear", "NM Other" },
        { "nm ", "NM Other" }
    };

    // Prefix to study type mappings
    private static readonly Dictionary<string, string> PrefixStudyTypes = new()
    {
        { "xr", "XR Other" },
        { "x-", "XR Other" },
        { "ct", "CT Other" },
        { "mr", "MRI Other" },
        { "us", "US Other" },
        { "nm", "NM Other" }
    };

    /// <summary>
    /// Match a procedure to a study type and RVU value.
    /// Implements the full Python matching algorithm.
    /// </summary>
    public static (string StudyType, double Rvu) MatchStudyType(
        string procedureText,
        Dictionary<string, double> rvuTable,
        Dictionary<string, List<ClassificationCondition>>? classificationRules = null,
        Dictionary<string, string>? directLookups = null)
    {
        if (string.IsNullOrWhiteSpace(procedureText))
        {
            return ("Unknown", 0.0);
        }

        if (rvuTable == null || rvuTable.Count == 0)
        {
            Log.Error("MatchStudyType called without rvu_table parameter");
            return ("Unknown", 0.0);
        }

        classificationRules ??= new Dictionary<string, List<ClassificationCondition>>();
        directLookups ??= new Dictionary<string, string>();

        var procedureLower = procedureText.ToLowerInvariant().Trim();
        var procedureStripped = procedureText.Trim();

        // STEP 1: Check classification rules FIRST (highest priority after direct lookups)
        var classificationMatch = TryClassificationRules(procedureLower, classificationRules, rvuTable);
        if (classificationMatch.HasValue)
        {
            Log.Debug("Classification rule match: {Procedure} -> {StudyType}", procedureText, classificationMatch.Value.StudyType);
            return classificationMatch.Value;
        }

        // STEP 2: Try exact match
        foreach (var kvp in rvuTable)
        {
            if (kvp.Key.ToLowerInvariant() == procedureLower)
            {
                Log.Debug("Exact match: {Procedure} -> {StudyType}", procedureText, kvp.Key);
                return (kvp.Key, kvp.Value);
            }
        }

        // STEP 3: Try keyword matching (longer keywords first)
        var keywordMatch = TryKeywordMatch(procedureLower, rvuTable);
        if (keywordMatch.HasValue)
        {
            Log.Debug("Keyword match: {Procedure} -> {StudyType}", procedureText, keywordMatch.Value.StudyType);
            return keywordMatch.Value;
        }

        // STEP 4: Try prefix matching
        var prefixMatch = TryPrefixMatch(procedureLower, rvuTable);
        if (prefixMatch.HasValue)
        {
            Log.Debug("Prefix match: {Procedure} -> {StudyType}", procedureText, prefixMatch.Value.StudyType);
            return prefixMatch.Value;
        }

        // STEP 5: Try partial matching (most specific first, excluding "Other" types initially)
        var partialMatch = TryPartialMatch(procedureLower, rvuTable);
        if (partialMatch.HasValue)
        {
            Log.Debug("Partial match: {Procedure} -> {StudyType}", procedureText, partialMatch.Value.StudyType);
            return partialMatch.Value;
        }

        Log.Warning("No match found for procedure: {Procedure}", procedureText);
        return ("Unknown", 0.0);
    }

    private static (string StudyType, double Rvu)? TryClassificationRules(
        string procedureLower,
        Dictionary<string, List<ClassificationCondition>> classificationRules,
        Dictionary<string, double> rvuTable)
    {
        foreach (var kvp in classificationRules)
        {
            var studyType = kvp.Key;
            var conditions = kvp.Value;

            foreach (var condition in conditions)
            {
                // Special case for CT Spine: exclude only if ALL excluded keywords are present
                if (studyType == "CT Spine" && condition.ExcludedKeywords?.Count > 0)
                {
                    var allExcluded = condition.ExcludedKeywords.All(kw =>
                        procedureLower.Contains(kw.ToLowerInvariant()));
                    if (allExcluded)
                        continue;
                }
                // For other rules: exclude if ANY excluded keyword is present
                else if (condition.ExcludedKeywords?.Count > 0)
                {
                    var anyExcluded = condition.ExcludedKeywords.Any(kw =>
                        procedureLower.Contains(kw.ToLowerInvariant()));
                    if (anyExcluded)
                        continue;
                }

                // Check if all required keywords are present
                var requiredMatch = true;
                if (condition.RequiredKeywords?.Count > 0)
                {
                    requiredMatch = condition.RequiredKeywords.All(kw =>
                        procedureLower.Contains(kw.ToLowerInvariant()));
                }

                // Check if at least one of any_of_keywords is present (if specified)
                var anyOfMatch = true;
                if (condition.AnyOfKeywords?.Count > 0)
                {
                    anyOfMatch = condition.AnyOfKeywords.Any(kw =>
                        procedureLower.Contains(kw.ToLowerInvariant()));
                }

                // Match if all required keywords are present AND any_of matches
                if (requiredMatch && anyOfMatch)
                {
                    if (rvuTable.TryGetValue(studyType, out var rvu))
                    {
                        return (studyType, rvu);
                    }
                    break;
                }
            }
        }

        return null;
    }

    private static (string StudyType, double Rvu)? TryKeywordMatch(
        string procedureLower,
        Dictionary<string, double> rvuTable)
    {
        // Sort keywords by length (longer first for more specific matches)
        var sortedKeywords = KeywordStudyTypes.Keys.OrderByDescending(k => k.Length);

        foreach (var keyword in sortedKeywords)
        {
            if (procedureLower.Contains(keyword))
            {
                var studyType = KeywordStudyTypes[keyword];
                if (rvuTable.TryGetValue(studyType, out var rvu))
                {
                    return (studyType, rvu);
                }
            }
        }

        return null;
    }

    private static (string StudyType, double Rvu)? TryPrefixMatch(
        string procedureLower,
        Dictionary<string, double> rvuTable)
    {
        if (procedureLower.Length < 2)
            return null;

        // Check for 3-character prefixes first (XA for fluoroscopy)
        if (procedureLower.Length >= 3)
        {
            var firstThree = procedureLower[..3];
            if (firstThree is "xa " or "xa\t")
            {
                // XA is fluoroscopy (XR modality)
                if (rvuTable.TryGetValue("XR Other", out var xaRvu))
                    return ("XR Other", xaRvu);
                return ("XR Other", 0.3);
            }
        }

        // Check 2-character prefixes
        var firstTwo = procedureLower[..2];
        if (PrefixStudyTypes.TryGetValue(firstTwo, out var studyType))
        {
            if (rvuTable.TryGetValue(studyType, out var rvu))
            {
                return (studyType, rvu);
            }
        }

        return null;
    }

    private static (string StudyType, double Rvu)? TryPartialMatch(
        string procedureLower,
        Dictionary<string, double> rvuTable)
    {
        var matches = new List<(int Score, string StudyType, double Rvu)>();
        var otherMatches = new List<(int Score, string StudyType, double Rvu)>();
        (string StudyType, double Rvu)? petCtMatch = null;

        foreach (var kvp in rvuTable)
        {
            var studyType = kvp.Key;
            var rvu = kvp.Value;
            var studyLower = studyType.ToLowerInvariant();

            // Special handling for PET CT - only match if both "pet" and "ct" appear
            if (studyLower == "pet ct")
            {
                if (procedureLower.Contains("pet") && procedureLower.Contains("ct"))
                {
                    petCtMatch = (studyType, rvu);
                }
                continue;
            }

            // Special handling for CTA Brain with Perfusion
            if (studyLower == "cta brain with perfusion")
            {
                var hasCtaIndicator = procedureLower.Contains("cta") ||
                                      procedureLower.Contains("angio") ||
                                      procedureLower.Contains("angiography");
                if (!hasCtaIndicator)
                    continue;
            }

            // Check for partial match
            if (studyLower.Contains(procedureLower) || procedureLower.Contains(studyLower))
            {
                var score = studyType.Length;

                if (studyLower.Contains(" other") || studyLower.EndsWith(" other"))
                {
                    otherMatches.Add((score, studyType, rvu));
                }
                else
                {
                    matches.Add((score, studyType, rvu));
                }
            }
        }

        // Return most specific non-"Other" match if found
        if (matches.Count > 0)
        {
            var best = matches.OrderByDescending(m => m.Score).FirstOrDefault();
            if (best != default)
                return (best.StudyType, best.Rvu);
        }

        // Try "Other" types as fallback
        if (otherMatches.Count > 0)
        {
            var best = otherMatches.OrderByDescending(m => m.Score).FirstOrDefault();
            if (best != default)
            {
                Log.Debug("Using 'Other' type fallback: {StudyType}", best.StudyType);
                return (best.StudyType, best.Rvu);
            }
        }

        // Absolute last resort: PET CT
        if (petCtMatch.HasValue)
        {
            Log.Debug("Using PET CT as last resort match");
            return petCtMatch.Value;
        }

        return null;
    }

    /// <summary>
    /// Classify a multi-accession batch (e.g., "5 XR studies").
    /// </summary>
    public static (string StudyType, double TotalRvu, int Count) ClassifyMultiAccession(
        string procedureText,
        int accessionCount,
        Dictionary<string, double> rvuTable,
        Dictionary<string, List<ClassificationCondition>>? classificationRules = null)
    {
        var (studyType, rvu) = MatchStudyType(procedureText, rvuTable, classificationRules);

        // For multi-accession records, prefix with "Multiple"
        var displayType = accessionCount > 1 ? $"Multiple {studyType}" : studyType;

        return (displayType, rvu * accessionCount, accessionCount);
    }

    /// <summary>
    /// Extract modality from a study type (e.g., "CT CAP" -> "CT").
    /// </summary>
    public static string ExtractModality(string studyType)
    {
        if (string.IsNullOrWhiteSpace(studyType))
            return "Unknown";

        var parts = studyType.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "Unknown";

        var first = parts[0].ToUpperInvariant();

        return first switch
        {
            "CTA" => "CT",
            "MRA" => "MRI",
            "PET" => "PET",
            "MULTIPLE" when parts.Length > 1 => ExtractModality(string.Join(" ", parts.Skip(1))),
            _ => first
        };
    }
}
