namespace RVUCounter.Models;

/// <summary>
/// A single classification condition with required/excluded keywords
/// </summary>
public class ClassificationCondition
{
    /// <summary>
    /// Keywords that MUST ALL be present (case-insensitive)
    /// </summary>
    public List<string> RequiredKeywords { get; set; } = new();

    /// <summary>
    /// At least ONE of these keywords must be present
    /// </summary>
    public List<string> AnyOfKeywords { get; set; } = new();

    /// <summary>
    /// Keywords that must NOT be present
    /// </summary>
    public List<string> ExcludedKeywords { get; set; } = new();

    /// <summary>
    /// Check if this condition matches the given procedure text
    /// </summary>
    public bool Matches(string procedureText)
    {
        var lowerText = procedureText.ToLowerInvariant();

        // All required keywords must be present
        foreach (var keyword in RequiredKeywords)
        {
            if (!lowerText.Contains(keyword.ToLowerInvariant()))
            {
                return false;
            }
        }

        // At least one of any_of keywords must be present (if specified)
        if (AnyOfKeywords.Count > 0)
        {
            bool anyMatch = false;
            foreach (var keyword in AnyOfKeywords)
            {
                if (lowerText.Contains(keyword.ToLowerInvariant()))
                {
                    anyMatch = true;
                    break;
                }
            }
            if (!anyMatch)
            {
                return false;
            }
        }

        // None of the excluded keywords should be present
        foreach (var keyword in ExcludedKeywords)
        {
            if (lowerText.Contains(keyword.ToLowerInvariant()))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Container for all RVU rules loaded from YAML
/// </summary>
public class RvuRulesConfig
{
    /// <summary>
    /// Map of study type to RVU value
    /// </summary>
    public Dictionary<string, double> RvuTable { get; set; } = new();

    /// <summary>
    /// Classification rules for procedure text matching
    /// </summary>
    public Dictionary<string, List<ClassificationCondition>> ClassificationRules { get; set; } = new();

    /// <summary>
    /// Direct procedure text to study type lookups (exact match)
    /// </summary>
    public Dictionary<string, string> DirectLookups { get; set; } = new();
}
