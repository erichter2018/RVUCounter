using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Serilog;

namespace RVUCounter.Utils;

/// <summary>
/// Extracts study data from Mosaic Info Hub.
/// Mosaic is a WinForms app with WebView2 containing study information.
/// </summary>
public static class MosaicExtractor
{
    private const string MosaicWindowTitle = "Mosaic Reporting";
    private const string MosaicProcessName = "Mosaic.InfoHub";

    /// <summary>
    /// Element info record capturing Name, Text, Index, ControlType, and ClassName.
    /// ControlType and ClassName added for Mosaic 2.0.3 compatibility (e.g. accession
    /// is now a Button control, status words appear as intermediate elements).
    /// </summary>
    private record MosaicElementInfo(string Name, string Text, int Index, ControlType? ControlType, string ClassName);

    /// <summary>
    /// Status words that Mosaic 2.0.3 inserts between the "Current Study" label and the
    /// accession Button. These must be skipped during accession lookahead.
    /// </summary>
    private static readonly HashSet<string> StatusWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "DRAFTED", "UNDRAFTED", "SIGNED", "UNSIGNED", "PRELIMINARY", "FINAL"
    };

    /// <summary>
    /// Find the Mosaic Info Hub main window.
    /// </summary>
    public static AutomationElement? FindMosaicWindow()
    {
        // Try by process name first
        var window = WindowExtraction.FindWindowByProcessName(MosaicProcessName);
        if (window != null)
            return window;

        // Fallback to title search
        return WindowExtraction.FindWindowByTitle(MosaicWindowTitle, timeoutMs: 2000);
    }

    /// <summary>
    /// Check if Mosaic is currently running.
    /// </summary>
    public static bool IsMosaicRunning()
    {
        return FindMosaicWindow() != null;
    }

    /// <summary>
    /// Extract study data from Mosaic.
    /// Uses multi-pass approach like Python version for better accuracy.
    /// Returns dictionary with: procedure, accession, patient_class, multiple_accessions
    /// </summary>
    public static MosaicStudyData? ExtractStudyData()
    {
        try
        {
            var window = FindMosaicWindow();
            if (window == null)
            {
                Log.Debug("Mosaic window not found");
                return null;
            }

            // Get all descendants
            var elements = WindowExtraction.GetDescendantsWithTimeout(window, maxElements: Core.Config.UiAutomationMaxElements, timeoutMs: 8000);
            Log.Debug("Found {Count} elements in Mosaic", elements.Count);

            var result = new MosaicStudyData();

            // Build element list with text, control type, and class name
            var elementData = new List<MosaicElementInfo>();
            for (int i = 0; i < elements.Count; i++)
            {
                try
                {
                    var text = WindowExtraction.GetElementText(elements[i]) ?? "";
                    var name = WindowExtraction.GetElementName(elements[i]) ?? "";

                    ControlType? controlType = null;
                    try { controlType = elements[i].Properties.ControlType.ValueOrDefault; }
                    catch { /* some elements don't expose ControlType */ }

                    string className = "";
                    try { className = elements[i].Properties.ClassName.ValueOrDefault ?? ""; }
                    catch { /* some elements don't expose ClassName */ }

                    if (!string.IsNullOrWhiteSpace(text) || !string.IsNullOrWhiteSpace(name))
                    {
                        elementData.Add(new MosaicElementInfo(name.Trim(), text.Trim(), i, controlType, className));
                    }
                }
                catch { /* Element inaccessible - expected for some UIA elements */ }
            }

            // Single pass: extract all fields by recognizing labels and looking ahead
            // Priority for accession: "Current Study" label > "Accession" label > fallback scan
            // Priority for procedure: embedded in accession extraction > "Description:" label
            bool foundCurrentStudy = false;
            bool foundAccessionLabel = false;

            for (int i = 0; i < elementData.Count; i++)
            {
                var name = elementData[i].Name;
                var text = elementData[i].Text;
                var combined = $"{name} {text}";
                var combinedLower = combined.ToLowerInvariant();
                var nameLower = name.ToLowerInvariant();

                // --- Accession via "Current Study" label (highest priority) ---
                if (!foundCurrentStudy && combinedLower.Contains("current study"))
                {
                    foundCurrentStudy = true;
                    for (int j = i + 1; j < Math.Min(i + 15, elementData.Count); j++)
                    {
                        var nextName = elementData[j].Name;

                        // Mosaic 2.0.3: skip empty elements between label and accession
                        if (string.IsNullOrWhiteSpace(nextName))
                            continue;

                        // Mosaic 2.0.3: skip status words (DRAFTED, SIGNED, etc.)
                        if (StatusWords.Contains(nextName.Trim()))
                            continue;

                        if (!nextName.EndsWith(":") &&
                            !nextName.ToLowerInvariant().Contains("mrn"))
                        {
                            var extracted = ExtractAccessionWithProcedure(nextName);
                            if (extracted != null)
                            {
                                result.Accession = extracted.Value.Accession;
                                if (!string.IsNullOrEmpty(extracted.Value.Procedure))
                                    result.Procedure = extracted.Value.Procedure;
                                Log.Debug("Found accession via 'Current Study' label: {Accession}", result.Accession);
                                break;
                            }
                        }
                    }
                }

                // --- Accession via "Accession" label (second priority) ---
                if (!foundAccessionLabel && string.IsNullOrEmpty(result.Accession) &&
                    combinedLower.Contains("accession") && combined.Contains(":"))
                {
                    foundAccessionLabel = true;
                    for (int j = i + 1; j < Math.Min(i + 15, elementData.Count); j++)
                    {
                        var nextName = elementData[j].Name;
                        var nextText = elementData[j].Text;

                        if (nextName.ToLowerInvariant().Contains("mrn") ||
                            nextText.ToLowerInvariant().Contains("mrn"))
                            continue;

                        if (!string.IsNullOrEmpty(nextName))
                        {
                            var extracted = ExtractAccessionWithProcedure(nextName);
                            if (extracted != null)
                            {
                                result.Accession = extracted.Value.Accession;
                                if (!string.IsNullOrEmpty(extracted.Value.Procedure) &&
                                    string.IsNullOrEmpty(result.Procedure))
                                    result.Procedure = extracted.Value.Procedure;
                                Log.Debug("Found accession via 'Accession' label: {Accession}", result.Accession);
                                break;
                            }
                        }

                        if (!string.IsNullOrEmpty(nextText))
                        {
                            var extracted = ExtractAccessionWithProcedure(nextText);
                            if (extracted != null)
                            {
                                result.Accession = extracted.Value.Accession;
                                Log.Debug("Found accession via 'Accession' label (text): {Accession}", result.Accession);
                                break;
                            }
                        }
                    }
                }

                // --- Procedure via "Description:" label ---
                if (string.IsNullOrEmpty(result.Procedure) && nameLower.Contains("description:"))
                {
                    if (name.Contains(":"))
                    {
                        var procValue = name.Split(':', 2)[1].Trim();
                        if (!string.IsNullOrEmpty(procValue))
                            result.Procedure = procValue;
                    }
                    if (string.IsNullOrEmpty(result.Procedure))
                    {
                        for (int j = i + 1; j < Math.Min(i + 3, elementData.Count); j++)
                        {
                            var nextName = elementData[j].Name;
                            if (!string.IsNullOrEmpty(nextName) && !nextName.EndsWith(":"))
                            {
                                result.Procedure = nextName;
                                break;
                            }
                        }
                    }
                }

                // --- Patient class ---
                if (string.IsNullOrEmpty(result.PatientClass))
                {
                    var pc = ExtractPatientClass(name) ?? ExtractPatientClass(text);
                    if (!string.IsNullOrEmpty(pc))
                        result.PatientClass = pc;
                }

                // --- Patient name (memory only) ---
                if (string.IsNullOrEmpty(result.PatientName))
                {
                    var patientName = ExtractPatientName(name);
                    if (!string.IsNullOrEmpty(patientName))
                    {
                        result.PatientName = patientName;
                        Log.Debug("Found patient name: {PatientName}", result.PatientName);
                    }
                }

                // --- Site code (memory only) ---
                if (string.IsNullOrEmpty(result.SiteCode))
                {
                    var siteCode = ExtractSiteCode(name) ?? ExtractSiteCode(text);
                    if (!string.IsNullOrEmpty(siteCode))
                    {
                        result.SiteCode = siteCode;
                        Log.Debug("Found site code: {SiteCode}", result.SiteCode);
                    }
                    // Mosaic 2.0.3: label and value may be in separate elements
                    else if (name.Trim().Equals("Site Code:", StringComparison.OrdinalIgnoreCase))
                    {
                        for (int j = i + 1; j < Math.Min(i + 4, elementData.Count); j++)
                        {
                            var nextName = elementData[j].Name.Trim();
                            if (!string.IsNullOrWhiteSpace(nextName) &&
                                Regex.IsMatch(nextName, @"^[A-Z]{2,5}$", RegexOptions.IgnoreCase))
                            {
                                result.SiteCode = nextName.ToUpperInvariant();
                                Log.Debug("Found site code via label lookahead: {SiteCode}", result.SiteCode);
                                break;
                            }
                        }
                    }
                }

                // --- MRN (memory only) ---
                if (string.IsNullOrEmpty(result.Mrn))
                {
                    var mrn = ExtractMrn(name) ?? ExtractMrn(text);
                    if (!string.IsNullOrEmpty(mrn))
                    {
                        result.Mrn = mrn;
                        Log.Debug("Found MRN: {Mrn}", result.Mrn);
                    }
                    // Mosaic 2.0.3: label and value may be in separate elements
                    else if (name.Trim().Equals("MRN:", StringComparison.OrdinalIgnoreCase))
                    {
                        for (int j = i + 1; j < Math.Min(i + 4, elementData.Count); j++)
                        {
                            var nextName = elementData[j].Name.Trim();
                            if (!string.IsNullOrWhiteSpace(nextName) &&
                                Regex.IsMatch(nextName, @"^[A-Z0-9]{5,20}$", RegexOptions.IgnoreCase))
                            {
                                result.Mrn = nextName.ToUpperInvariant();
                                Log.Debug("Found MRN via label lookahead: {Mrn}", result.Mrn);
                                break;
                            }
                        }
                    }
                }

                // --- Fallback: accession from any text containing accession-like pattern ---
                // Skip elements containing "mrn" to avoid extracting MRN as accession
                if (string.IsNullOrEmpty(result.Accession) &&
                    !nameLower.Contains("mrn") && !text.ToLowerInvariant().Contains("mrn"))
                {
                    var accessions = ExtractAccessionsFromText(name);
                    if (accessions.Count == 0)
                        accessions = ExtractAccessionsFromText(text);
                    if (accessions.Count > 0)
                    {
                        result.Accession = accessions[0];
                        result.AllAccessions = accessions;
                        result.IsMultiAccession = accessions.Count > 1;
                        Log.Debug("Found accession via fallback scan: {Accession} (multi={IsMulti})", result.Accession, result.IsMultiAccession);
                    }
                }
            }

            // Safety net: reject accession if it matches the extracted MRN
            // The MRN is per-patient, not per-study, so using it as accession would
            // cause all studies from the same patient to hash identically
            if (!string.IsNullOrEmpty(result.Accession) && !string.IsNullOrEmpty(result.Mrn))
            {
                var accTrimmed = result.Accession.Trim();
                var mrnTrimmed = result.Mrn.Trim();
                if (accTrimmed.Equals(mrnTrimmed, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warning("Rejected accession '{Accession}' - matches MRN '{Mrn}'. " +
                        "If your site uses MRN as accession, this study was dropped.",
                        result.Accession, result.Mrn);
                    result.Accession = null;
                    result.AllAccessions.Clear();
                    result.IsMultiAccession = false;
                }
            }

            if (!string.IsNullOrEmpty(result.Accession))
            {
                Log.Information("Extracted from Mosaic: {Accession} - {Procedure}",
                    result.Accession, result.Procedure);
            }

            return !string.IsNullOrEmpty(result.Accession) ? result : null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error extracting data from Mosaic");
            return null;
        }
    }

    /// <summary>
    /// Extract accession and optional procedure from text like "ACC123 (CT HEAD)"
    /// </summary>
    private static (string Accession, string Procedure)? ExtractAccessionWithProcedure(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // Pattern: "ACC (PROC)"
        var match = Regex.Match(text, @"^([^(]+)\s*\(([^)]+)\)");
        if (match.Success)
        {
            var acc = match.Groups[1].Value.Trim();
            var proc = match.Groups[2].Value.Trim();
            if (WindowExtraction.IsAccessionLike(acc))
            {
                return (acc, proc);
            }
        }

        // Just an accession
        if (WindowExtraction.IsAccessionLike(text.Trim()))
        {
            return (text.Trim(), "");
        }

        return null;
    }

    /// <summary>
    /// Extract all accession-like strings from text.
    /// </summary>
    private static List<string> ExtractAccessionsFromText(string text)
    {
        var accessions = new List<string>();

        // Common accession patterns
        // Pattern 1: Alphanumeric with at least 2 digits, 6-15 chars
        var pattern1 = new Regex(@"\b[A-Z]{0,3}\d{2,}[A-Z0-9]*\b", RegexOptions.IgnoreCase);

        // Pattern 2: Format like "ACC12345" or "12345678"
        var pattern2 = new Regex(@"\b[A-Z]{2,3}\d{5,10}\b", RegexOptions.IgnoreCase);

        foreach (Match match in pattern1.Matches(text))
        {
            var candidate = match.Value.Trim();
            if (WindowExtraction.IsAccessionLike(candidate))
            {
                accessions.Add(candidate);
            }
        }

        foreach (Match match in pattern2.Matches(text))
        {
            var candidate = match.Value.Trim();
            if (!accessions.Contains(candidate) && WindowExtraction.IsAccessionLike(candidate))
            {
                accessions.Add(candidate);
            }
        }

        return accessions;
    }



    /// <summary>
    /// Extract patient class from text.
    /// </summary>
    private static string? ExtractPatientClass(string text)
    {
        var lower = text.ToLowerInvariant();

        if (lower.Contains("inpatient") || lower.Contains("ip"))
            return "Inpatient";
        if (lower.Contains("outpatient") || lower.Contains("op"))
            return "Outpatient";
        if (lower.Contains("emergency") || lower.Contains("er ") || lower.Contains("ed "))
            return "Emergency";

        return null;
    }

    /// <summary>
    /// Extract patient name from text.
    /// Mosaic displays patient names as all-caps: "LASTNAME FIRSTNAME" or "LASTNAME FIRSTNAME MIDDLE"
    /// </summary>
    private static string? ExtractPatientName(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = text.Trim();

        // Patient names are typically 2-4 words, all uppercase, 5-50 chars
        if (text.Length < 5 || text.Length > 50)
            return null;

        // Must be all uppercase (Mosaic format)
        if (text != text.ToUpperInvariant())
            return null;

        // Must have 2-4 space-separated words (last, first, optional middle/suffix)
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || words.Length > 4)
            return null;

        // Each word should be alphabetic (allow hyphens for hyphenated names)
        foreach (var word in words)
        {
            if (!Regex.IsMatch(word, @"^[A-Z][A-Z\-']*$"))
                return null;
        }

        // Reject common UI/medical terms that might look like names
        string[] excludePatterns = {
            "CURRENT STUDY", "SITE CODE", "SITE GROUP", "BODY PART",
            "STUDY DATE", "ORDERING", "REASON FOR", "MRN", "ACCESSION",
            "FINAL REPORT", "CLINICAL HISTORY", "COMPARISON", "IMPRESSION",
            "FINDINGS", "EXAM", "VIEW", "CONTRAST", "BILATERAL",
            "LINES TUBES", "SOFT TISSUES", "BONES", "BOWEL",
            "SIGN FINAL", "PROCESS REPORT", "CREATE IMPRESSION",
            "OPEN", "CLOSE", "MINIMIZE", "MAXIMIZE", "ACTIONS"
        };

        // Use word boundary matching instead of substring to avoid
        // rejecting names like "LINESMITH" which contains "LINES"
        if (excludePatterns.Any(p => text == p ||
            text.StartsWith(p + " ") ||
            text.EndsWith(" " + p) ||
            text.Contains(" " + p + " ")))
            return null;

        // Reject report/findings text that structurally resembles names
        // (e.g., "APPENDIX IS NORMAL", "LUNGS ARE CLEAR")
        // Real patient names never contain these common English words
        string[] nonNameWords = {
            "IS", "ARE", "WAS", "WERE", "NO", "NOT", "THE", "AND", "FOR",
            "WITH", "WITHOUT", "HAS", "HAVE", "HAD", "BUT", "FROM",
            "NORMAL", "CLEAR", "SEEN", "NOTED", "ABSENT", "PRESENT",
            "ACUTE", "STABLE", "WITHIN", "LIMITS", "UNREMARKABLE",
            "NEGATIVE", "POSITIVE", "MILD", "MODERATE", "SEVERE"
        };

        var wordSet = new HashSet<string>(words);
        if (nonNameWords.Any(w => wordSet.Contains(w)))
            return null;

        // Convert to title case for display: "MILLSON DIANA" -> "Millson Diana"
        var titleCase = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLower());
        return titleCase;
    }

    /// <summary>
    /// Extract site code from text.
    /// Format: "Site Code: MLC" or just a 2-4 letter site code near site label
    /// </summary>
    private static string? ExtractSiteCode(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // Look for "Site Code: XXX" pattern
        var match = Regex.Match(text, @"Site\s*Code:\s*([A-Z]{2,5})", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups[1].Value.ToUpperInvariant();
        }

        return null;
    }

    /// <summary>
    /// Extract MRN (Medical Record Number) from text.
    /// Format: "MRN: 1057034TCR" - alphanumeric, typically 6-15 characters
    /// </summary>
    private static string? ExtractMrn(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // Look for "MRN: XXX" pattern
        var match = Regex.Match(text, @"MRN:\s*([A-Z0-9]{5,20})", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups[1].Value.ToUpperInvariant();
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Mosaic 2.0.3 Report Content Extraction (DEFERRED — no consumers yet)
    // -----------------------------------------------------------------------
    // In 2.0.3, report text lives inside a ProseMirror editor (Document element
    // named "Report for: <Accession>"). To extract it:
    //   1. Find the Document element by name prefix "Report for:"
    //   2. Collect all Text descendants (these are the report paragraphs)
    //   3. Join with newlines, optionally score by keyword (FINDINGS, IMPRESSION, etc.)
    // NOTE: In 2.0.2, the report Document was named "reportEditor" (or similar).
    //       The name changed in 2.0.3 to "Report for: <Accession>".
    // This does NOT affect current extraction logic — we find Mosaic by process
    // name / window title, not by Document name. Implement when report content
    // is needed by a feature (e.g., critical result detection from report text).
    // -----------------------------------------------------------------------

    /// <summary>
    /// Get a list of all visible accessions in Mosaic (for tracking comparison).
    /// </summary>
    public static List<string> GetVisibleAccessions()
    {
        try
        {
            var window = FindMosaicWindow();
            if (window == null)
                return new List<string>();

            var elements = WindowExtraction.GetDescendantsWithTimeout(window, maxElements: Core.Config.MosaicScanMaxElements, timeoutMs: Core.Config.UiAutomationTimeoutMs);
            var accessions = new HashSet<string>();

            foreach (var element in elements)
            {
                try
                {
                    var text = WindowExtraction.GetElementText(element);
                    var extracted = ExtractAccessionsFromText(text);
                    foreach (var acc in extracted)
                    {
                        accessions.Add(acc);
                    }
                }
                catch
                {
                    // Skip
                }
            }

            return accessions.ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error getting visible accessions from Mosaic");
            return new List<string>();
        }
    }
}

/// <summary>
/// Data extracted from Mosaic for a study.
/// </summary>
public class MosaicStudyData
{
    public string? Accession { get; set; }
    public string? Procedure { get; set; }
    public string PatientClass { get; set; } = "Unknown";
    public bool IsMultiAccession { get; set; }
    public List<string> AllAccessions { get; set; } = new();

    /// <summary>
    /// Patient name extracted from Mosaic (memory only, not persisted).
    /// Format: "LASTNAME FIRSTNAME" (all caps from Mosaic)
    /// </summary>
    public string? PatientName { get; set; }

    /// <summary>
    /// Site code extracted from Mosaic (memory only, not persisted).
    /// Example: "MLC", "UNM", etc.
    /// </summary>
    public string? SiteCode { get; set; }

    /// <summary>
    /// Medical Record Number (MRN) extracted from Mosaic (memory only, not persisted).
    /// Required for opening studies via XML file drop.
    /// Example: "1057034TCR", "980562570MCR"
    /// </summary>
    public string? Mrn { get; set; }
}
