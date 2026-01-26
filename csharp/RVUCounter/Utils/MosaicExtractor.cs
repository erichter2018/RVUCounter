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

            // Build element list with text
            var elementData = new List<(string Name, string Text, int Index)>();
            for (int i = 0; i < elements.Count; i++)
            {
                try
                {
                    var text = WindowExtraction.GetElementText(elements[i]) ?? "";
                    var name = WindowExtraction.GetElementName(elements[i]) ?? "";
                    if (!string.IsNullOrWhiteSpace(text) || !string.IsNullOrWhiteSpace(name))
                    {
                        elementData.Add((name.Trim(), text.Trim(), i));
                    }
                }
                catch { /* Skip problematic elements */ }
            }

            // =====================================================================
            // FIRST PASS: Look for "Current Study" label - accession is right below
            // This is the most reliable method for single accessions
            // =====================================================================
            for (int i = 0; i < elementData.Count; i++)
            {
                var (name, text, _) = elementData[i];
                var combined = $"{name} {text}".ToLowerInvariant();

                if (combined.Contains("current study"))
                {
                    // Look at nearby elements for the accession (should be right below)
                    for (int j = i + 1; j < Math.Min(i + 15, elementData.Count); j++)
                    {
                        var nextName = elementData[j].Name;

                        // Skip if it looks like a label or MRN
                        if (!string.IsNullOrEmpty(nextName) && !nextName.EndsWith(":") &&
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
                    break;
                }
            }

            // =====================================================================
            // SECOND PASS: Look for explicit "Accession" label
            // =====================================================================
            if (string.IsNullOrEmpty(result.Accession))
            {
                for (int i = 0; i < elementData.Count; i++)
                {
                    var (name, text, _) = elementData[i];
                    var combined = $"{name} {text}";

                    if (combined.ToLowerInvariant().Contains("accession") && combined.Contains(":"))
                    {
                        for (int j = i + 1; j < Math.Min(i + 15, elementData.Count); j++)
                        {
                            var nextName = elementData[j].Name;
                            var nextText = elementData[j].Text;

                            // Skip MRN values
                            if (nextName.ToLowerInvariant().Contains("mrn") ||
                                nextText.ToLowerInvariant().Contains("mrn"))
                                continue;

                            // Try name first
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

                            // Try text
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
                        break;
                    }
                }
            }

            // =====================================================================
            // THIRD PASS: Look for "Description:" label for procedure
            // =====================================================================
            if (string.IsNullOrEmpty(result.Procedure))
            {
                for (int i = 0; i < elementData.Count; i++)
                {
                    var name = elementData[i].Name;

                    if (name.ToLowerInvariant().Contains("description:"))
                    {
                        // Value might be after the colon in the same element
                        if (name.Contains(":"))
                        {
                            var procValue = name.Split(':', 2)[1].Trim();
                            if (!string.IsNullOrEmpty(procValue))
                            {
                                result.Procedure = procValue;
                                break;
                            }
                        }
                        // Or look at next element
                        for (int j = i + 1; j < Math.Min(i + 3, elementData.Count); j++)
                        {
                            var nextName = elementData[j].Name;
                            if (!string.IsNullOrEmpty(nextName) && !nextName.EndsWith(":"))
                            {
                                result.Procedure = nextName;
                                break;
                            }
                        }
                        break;
                    }
                }
            }

            // =====================================================================
            // FOURTH PASS: Look for procedure keywords (CT, MR, XR, etc.)
            // =====================================================================
            if (string.IsNullOrEmpty(result.Procedure))
            {
                foreach (var (name, _, _) in elementData)
                {
                    if (IsProcedureText(name))
                    {
                        result.Procedure = name;
                        break;
                    }
                }
            }

            // =====================================================================
            // FALLBACK PASS: Scan for any accession-like strings
            // =====================================================================
            if (string.IsNullOrEmpty(result.Accession))
            {
                foreach (var (name, text, _) in elementData)
                {
                    var accessions = ExtractAccessionsFromText(name);
                    if (accessions.Count > 0)
                    {
                        result.Accession = accessions[0];
                        result.AllAccessions = accessions;
                        result.IsMultiAccession = accessions.Count > 1;
                        Log.Debug("Found accession via fallback scan: {Accession} (multi={IsMulti})", result.Accession, result.IsMultiAccession);
                        break;
                    }

                    accessions = ExtractAccessionsFromText(text);
                    if (accessions.Count > 0)
                    {
                        result.Accession = accessions[0];
                        result.AllAccessions = accessions;
                        result.IsMultiAccession = accessions.Count > 1;
                        Log.Debug("Found accession via fallback scan (text): {Accession} (multi={IsMulti})", result.Accession, result.IsMultiAccession);
                        break;
                    }
                }
            }

            // Look for patient class
            foreach (var (name, text, _) in elementData)
            {
                var pc = ExtractPatientClass(name) ?? ExtractPatientClass(text);
                if (!string.IsNullOrEmpty(pc))
                {
                    result.PatientClass = pc;
                    break;
                }
            }

            // =====================================================================
            // VALIDATION: Reject false positives from UI elements
            // =====================================================================
            if (!string.IsNullOrEmpty(result.Accession))
            {
                // Check if procedure looks like a real procedure or just UI garbage
                if (!string.IsNullOrEmpty(result.Procedure) && IsUiGarbageText(result.Procedure))
                {
                    Log.Debug("Rejecting extraction - procedure looks like UI text: {Procedure}", result.Procedure);
                    return null;
                }

                // If we only found via fallback scan (no "Current Study" or "Accession" label),
                // require a valid-looking procedure to confirm it's real
                if (string.IsNullOrEmpty(result.Procedure) || !IsProcedureText(result.Procedure))
                {
                    // No valid procedure found - this is likely a false positive
                    Log.Debug("Rejecting extraction - no valid procedure found for accession {Accession}", result.Accession);
                    return null;
                }

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
    /// Check if text looks like a procedure description.
    /// </summary>
    private static bool IsProcedureText(string text)
    {
        // Procedures are typically 5-80 characters
        if (string.IsNullOrWhiteSpace(text) || text.Length < 5 || text.Length > 80)
            return false;

        // First check if it's UI garbage
        if (IsUiGarbageText(text))
            return false;

        var lower = text.ToLowerInvariant();

        // Procedure keywords
        string[] keywords = { "ct ", "mri ", "mr ", "xr ", "x-ray", "ultrasound", "us ",
            "chest", "abdomen", "pelvis", "brain", "head", "spine", "neck",
            "with contrast", "without contrast", "w/o contrast", "w/ contrast",
            "bilateral", "left", "right", "extremity", "ankle", "knee", "hip",
            "shoulder", "wrist", "elbow", "foot", "hand", "finger", "toe",
            "lumbar", "thoracic", "cervical", "cardiac", "heart", "aorta",
            "angio", "venous", "arterial", "doppler", "echo", "mammogram",
            "fluoro", "pet", "nuclear", "bone scan", "dexa" };

        return keywords.Any(k => lower.Contains(k));
    }

    /// <summary>
    /// Check if text looks like UI garbage (not a real procedure).
    /// </summary>
    private static bool IsUiGarbageText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        // Procedures are concise - reject long text (likely dictation/transcript)
        if (text.Length > 80)
            return true;

        var lower = text.ToLowerInvariant();

        // Common UI element patterns that are NOT procedures
        string[] uiPatterns = {
            "profile", "status", "available", "unavailable", "online", "offline",
            "logged in", "logged out", "sign in", "sign out", "login", "logout",
            "settings", "preferences", "options", "menu", "toolbar", "button",
            "click", "select", "choose", "enter", "type", "search",
            "loading", "please wait", "processing", "saving",
            "welcome", "hello", "user", "admin", "guest",
            "version", "copyright", "license", "help", "about",
            "minimize", "maximize", "close", "exit", "cancel", "ok",
            "yes", "no", "confirm", "submit", "apply", "reset",
            "notification", "alert", "warning", "error", "info",
            "tab", "panel", "window", "dialog", "form",
            "n/a", "none", "empty", "null", "undefined",
            // Dictation/transcript patterns
            "please ", "summarize", "irrelevant", "repetitive",
            "only if", "make sure", "remember to", "don't forget",
            "note that", "be sure", "ensure that"
        };

        return uiPatterns.Any(pattern => lower.Contains(pattern));
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
}
