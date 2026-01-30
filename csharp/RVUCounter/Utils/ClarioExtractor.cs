using System.Diagnostics;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Serilog;

namespace RVUCounter.Utils;

/// <summary>
/// Extracts data from Clario application (Chrome-based worklist).
/// Used for patient class extraction, critical findings, and exam notes.
/// </summary>
public static class ClarioExtractor
{
    private const string ClarioWindowTitle = "Clario";
    private const string ClarioWorklistTitle = "Worklist";

    // Cache for Clario window (like Python's _clario_cache)
    private static AutomationElement? _cachedChromeWindow;
    private static AutomationElement? _cachedContentArea;
    private static readonly object _cacheLock = new();

    // Urgency and location terms for combining priority/class (from Python)
    private static readonly string[] UrgencyTerms =
        { "STAT", "Stroke", "Urgent", "Routine", "ASAP", "CRITICAL", "IMMEDIATE", "Trauma" };
    private static readonly string[] LocationTerms =
        { "Emergency", "Inpatient", "Outpatient", "Observation", "Ambulatory", "ED", "ER" };

    // Modality keywords — if these appear in a priority/class value, it's likely a procedure description
    private static readonly string[] ModalityKeywords =
        { "CT ", "MR ", "XR ", "US ", "NM ", "PET", "FL ", "DEXA", "MAMMO", "MRI", "MRA" };

    // Additional known patient class patterns beyond location terms
    private static readonly string[] KnownClassPatterns =
        { "Pre-Admit", "Pre Admit", "Recurring", "Rehab", "Swing Bed", "Day Surgery",
          "Same Day", "Newborn", "Hospice", "Home Health", "Skilled Nursing", "SNF" };

    /// <summary>
    /// Find Chrome window with 'Clario - Worklist' tab.
    /// Uses cache if available and valid.
    /// </summary>
    public static AutomationElement? FindClarioChromeWindow(bool useCache = true)
    {
        lock (_cacheLock)
        {
            // Check cache first
            if (useCache && _cachedChromeWindow != null)
            {
                try
                {
                    // Validate cache by checking if window still exists
                    var _ = _cachedChromeWindow.Name;
                    return _cachedChromeWindow;
                }
                catch
                {
                    Log.Debug("Clario cache validation failed, clearing cache");
                    _cachedChromeWindow = null;
                    _cachedContentArea = null;
                }
            }
        }

        try
        {
            var automation = WindowExtraction.GetAutomation();
            var desktop = automation.GetDesktop();
            var cf = automation.ConditionFactory;

            var windows = desktop.FindAllChildren(cf.ByControlType(ControlType.Window));

            foreach (var window in windows)
            {
                try
                {
                    var title = window.Name?.ToLowerInvariant() ?? "";

                    // Exclude test/viewer windows and RVU Counter
                    if (title.Contains("rvu counter") ||
                        title.Contains("test") ||
                        title.Contains("viewer") ||
                        title.Contains("ui elements") ||
                        title.Contains("diagnostic"))
                    {
                        continue;
                    }

                    // Look for Chrome window with "clario" and "worklist" in title
                    if (title.Contains("clario") && title.Contains("worklist"))
                    {
                        Log.Debug("Found Clario window: '{Title}'", window.Name);
                        try
                        {
                            var className = window.Properties.ClassName.ValueOrDefault?.ToLowerInvariant() ?? "";
                            if (className.Contains("chrome"))
                            {
                                lock (_cacheLock)
                                {
                                    _cachedChromeWindow = window;
                                }
                                return window;
                            }
                        }
                        catch (Exception ex)
                        {
                            // If we can't check class name, still return it if title matches
                            Log.Debug(ex, "Could not check class name for Clario window, using title match");
                            lock (_cacheLock)
                            {
                                _cachedChromeWindow = window;
                            }
                            return window;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Error checking window during Clario search");
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error finding Clario Chrome window");
        }

        // Window not found — clear both caches so stale references don't linger
        lock (_cacheLock)
        {
            _cachedChromeWindow = null;
            _cachedContentArea = null;
        }
        return null;
    }

    /// <summary>
    /// Find the Chrome content area (where the web page is rendered).
    /// </summary>
    public static AutomationElement? FindClarioContentArea(AutomationElement? chromeWindow, bool useCache = true)
    {
        if (chromeWindow == null)
            return null;

        lock (_cacheLock)
        {
            // Check cache first
            if (useCache && _cachedContentArea != null)
            {
                try
                {
                    var _ = _cachedContentArea.Properties.ControlType.ValueOrDefault;
                    return _cachedContentArea;
                }
                catch
                {
                    _cachedContentArea = null;
                }
            }
        }

        try
        {
            // Get descendants with limit to prevent blocking
            var descendants = WindowExtraction.GetDescendantsWithTimeout(chromeWindow, maxElements: Core.Config.ClarioMaxElements, timeoutMs: Core.Config.UiAutomationTimeoutMs);

            // Look for elements with control type Document or Pane
            foreach (var child in descendants)
            {
                try
                {
                    var controlType = child.Properties.ControlType.ValueOrDefault;
                    if (controlType == ControlType.Document || controlType == ControlType.Pane)
                    {
                        var name = child.Name ?? "";
                        if (!string.IsNullOrEmpty(name) && name.Length > 10)
                        {
                            lock (_cacheLock)
                            {
                                _cachedContentArea = child;
                            }
                            return child;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Error checking element for content area");
                    continue;
                }
            }

            // Fallback: look for automation_id patterns
            foreach (var child in descendants)
            {
                try
                {
                    var automationId = child.Properties.AutomationId.ValueOrDefault ?? "";
                    if (automationId.Contains("content", StringComparison.OrdinalIgnoreCase) ||
                        automationId.Contains("render", StringComparison.OrdinalIgnoreCase))
                    {
                        lock (_cacheLock)
                        {
                            _cachedContentArea = child;
                        }
                        return child;
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Error checking automation_id for content area");
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error finding Clario content area");
        }

        // Last resort: return the window itself
        lock (_cacheLock)
        {
            _cachedContentArea = chromeWindow;
        }
        return chromeWindow;
    }

    /// <summary>
    /// Check if Clario is currently running.
    /// </summary>
    public static bool IsClarioRunning()
    {
        var window = FindClarioChromeWindow();
        if (window == null)
        {
            Log.Debug("Clario: No Chrome window found with 'clario' in title");
        }
        return window != null;
    }

    /// <summary>
    /// Extract patient class from Clario - Worklist.
    /// </summary>
    /// <param name="targetAccession">Optional accession to match. If provided, only returns data if accession matches.</param>
    /// <returns>ClarioPatientClassData with patient_class and accession, or null if not found</returns>
    public static ClarioPatientClassData? ExtractPatientClass(string? targetAccession = null)
    {
        try
        {
            // Find Chrome window
            var chromeWindow = FindClarioChromeWindow(useCache: true);
            if (chromeWindow == null)
            {
                Log.Debug("Clario: Chrome window not found");
                return null;
            }

            // Find content area
            var contentArea = FindClarioContentArea(chromeWindow, useCache: true);
            if (contentArea == null)
            {
                Log.Debug("Clario: Content area not found");
                return null;
            }

            // Staggered depth search: try 12, then 18, then 25 (like Python)
            var data = new ExtractedClarioData();
            int[] searchDepths = { 12, 18, 25 };

            foreach (var maxDepth in searchDepths)
            {
                Log.Debug("Clario: Searching at depth {Depth}", maxDepth);

                var elements = GetAllElementsRecursive(contentArea, maxDepth);
                var extractedData = ExtractDataFromElements(elements);

                // Update data with any newly found values
                if (string.IsNullOrEmpty(data.Priority) && !string.IsNullOrEmpty(extractedData.Priority))
                {
                    data.Priority = extractedData.Priority;
                    Log.Debug("Clario: Found Priority='{Priority}' at depth {Depth}", data.Priority, maxDepth);
                }
                if (string.IsNullOrEmpty(data.Class) && !string.IsNullOrEmpty(extractedData.Class))
                {
                    data.Class = extractedData.Class;
                    Log.Debug("Clario: Found Class='{Class}' at depth {Depth}", data.Class, maxDepth);
                }
                if (string.IsNullOrEmpty(data.Accession) && !string.IsNullOrEmpty(extractedData.Accession))
                {
                    data.Accession = extractedData.Accession;
                    Log.Debug("Clario: Found Accession at depth {Depth}", maxDepth);
                }

                // Stop if we found all three required values
                if (!string.IsNullOrEmpty(data.Priority) &&
                    !string.IsNullOrEmpty(data.Class) &&
                    !string.IsNullOrEmpty(data.Accession))
                {
                    Log.Debug("Clario: Found all three values at depth {Depth}, stopping search", maxDepth);
                    break;
                }
            }

            // Check if we found any useful data
            if (string.IsNullOrEmpty(data.Priority) && string.IsNullOrEmpty(data.Class))
            {
                Log.Debug("Clario: No priority or class found");
                return null;
            }

            Log.Information("Clario: Extracted raw data - Priority='{Priority}', Class='{Class}'",
                data.Priority, data.Class);

            // Combine priority and class
            var patientClass = CombinePriorityAndClass(data.Priority, data.Class);

            Log.Debug("Clario: After combining - Combined='{PatientClass}'", patientClass);

            // If target_accession provided, verify it matches
            if (targetAccession != null)
            {
                if (!string.IsNullOrEmpty(data.Accession) &&
                    !data.Accession.Trim().Equals(targetAccession.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    Log.Debug("Clario: Accession mismatch - expected '{Expected}'", targetAccession);
                    return null;
                }
            }

            // Return patient class and accession
            if (!string.IsNullOrEmpty(patientClass))
            {
                Log.Debug("Clario: Returning patient_class='{PatientClass}'", patientClass);
                return new ClarioPatientClassData
                {
                    PatientClass = patientClass,
                    Accession = data.Accession ?? ""
                };
            }

            Log.Debug("Clario: No patient_class found after combining");
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Clario extraction error");
            return null;
        }
    }

    /// <summary>
    /// Recursively get all UI elements from an element (like Python's get_all_elements_clario).
    /// Enforces element count cap and time limit to prevent OOM storms.
    /// </summary>
    private static List<ClarioElementData> GetAllElementsRecursive(AutomationElement element, int maxDepth)
    {
        var elements = new List<ClarioElementData>();
        var stopwatch = Stopwatch.StartNew();
        int maxElements = Core.Config.ClarioMaxElements;
        int timeoutMs = Core.Config.ClarioExtractionTimeoutMs;

        GetElementsRecursiveInternal(element, 0, maxDepth, elements, maxElements, stopwatch, timeoutMs);

        Log.Debug("Clario: Collected {Count} elements at max depth {Depth} in {Elapsed}ms",
            elements.Count, maxDepth, stopwatch.ElapsedMilliseconds);
        return elements;
    }

    private static void GetElementsRecursiveInternal(
        AutomationElement element,
        int depth,
        int maxDepth,
        List<ClarioElementData> elements,
        int maxElements,
        Stopwatch stopwatch,
        int timeoutMs)
    {
        if (depth > maxDepth)
            return;

        // Check element count cap
        if (elements.Count >= maxElements)
            return;

        // Check time limit
        if (stopwatch.ElapsedMilliseconds >= timeoutMs)
            return;

        try
        {
            string automationId = "";
            string name = "";
            string text = "";

            // Silent catches — property access failures are expected and transient.
            // Logging each one generates 98K lines in 5 minutes.
            try { automationId = element.Properties.AutomationId.ValueOrDefault ?? ""; } catch { }
            try { name = element.Name ?? ""; } catch { }
            try
            {
                if (element.Patterns.Value.IsSupported)
                {
                    text = element.Patterns.Value.Pattern.Value.ValueOrDefault ?? "";
                }
            }
            catch { }

            // Only include elements with some meaningful content
            if (!string.IsNullOrEmpty(automationId) || !string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(text))
            {
                elements.Add(new ClarioElementData
                {
                    Depth = depth,
                    AutomationId = automationId.Trim(),
                    Name = name.Trim(),
                    Text = text.Length > 100 ? text.Substring(0, 100) : text.Trim()
                });
            }

            // Check caps again before recursing into children
            if (elements.Count >= maxElements || stopwatch.ElapsedMilliseconds >= timeoutMs)
                return;

            // Recursively get children
            try
            {
                var children = element.FindAllChildren();
                foreach (var child in children)
                {
                    if (elements.Count >= maxElements || stopwatch.ElapsedMilliseconds >= timeoutMs)
                        return;
                    GetElementsRecursiveInternal(child, depth + 1, maxDepth, elements, maxElements, stopwatch, timeoutMs);
                }
            }
            catch { /* Children enumeration can fail transiently — silent */ }
        }
        catch { /* Element processing can fail transiently — silent */ }
    }

    /// <summary>
    /// Validate that a candidate priority value looks like a real priority (e.g. "STAT ER")
    /// and not a procedure description or patient name picked up by FindNextValue.
    /// </summary>
    private static bool IsValidPriority(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        // Too long — real priorities are short like "STAT ER", "Routine", "Urgent Emergency"
        if (value.Length > 50)
        {
            Log.Debug("Clario: Rejecting priority (too long, {Len} chars): '{Value}'", value.Length, value);
            return false;
        }

        // Contains modality keyword — likely a procedure description
        if (ModalityKeywords.Any(kw => value.Contains(kw, StringComparison.OrdinalIgnoreCase)))
        {
            Log.Debug("Clario: Rejecting priority (contains modality keyword): '{Value}'", value);
            return false;
        }

        // Must contain at least one recognized urgency or location term
        bool hasRecognizedTerm =
            UrgencyTerms.Any(t => value.Contains(t, StringComparison.OrdinalIgnoreCase)) ||
            LocationTerms.Any(t => value.Contains(t, StringComparison.OrdinalIgnoreCase));

        if (!hasRecognizedTerm)
        {
            Log.Debug("Clario: Rejecting priority (no recognized term): '{Value}'", value);
        }
        return hasRecognizedTerm;
    }

    /// <summary>
    /// Validate that a candidate patient class value looks like a real patient class
    /// (e.g. "Inpatient", "Emergency", "Pre-Admit") and not a patient name or facility.
    /// </summary>
    private static bool IsValidPatientClass(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        // Too long — facility names like "Rapides Regional Medical Center - Emergency Department"
        if (value.Length > 60)
        {
            Log.Debug("Clario: Rejecting class (too long, {Len} chars): '{Value}'", value.Length, value);
            return false;
        }

        // Contains modality keyword — likely a procedure description
        if (ModalityKeywords.Any(kw => value.Contains(kw, StringComparison.OrdinalIgnoreCase)))
        {
            Log.Debug("Clario: Rejecting class (contains modality keyword): '{Value}'", value);
            return false;
        }

        // Accept if contains a recognized location term
        if (LocationTerms.Any(t => value.Contains(t, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Accept if matches a known class pattern
        if (KnownClassPatterns.Any(p => value.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Accept if contains a recognized urgency term (some systems put urgency in class field)
        if (UrgencyTerms.Any(t => value.Contains(t, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Reject if all words are capitalized with 2+ chars — likely a patient name ("Anterrica Myles")
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2 && words.All(w => w.Length >= 2 && char.IsUpper(w[0]) && w.Skip(1).All(char.IsLower)))
        {
            Log.Debug("Clario: Rejecting class (looks like patient name): '{Value}'", value);
            return false;
        }

        Log.Debug("Clario: Rejecting class (no recognized term): '{Value}'", value);
        return false;
    }

    /// <summary>
    /// Extract priority, class, and accession from element data (like Python's extract_data_from_elements).
    /// </summary>
    private static ExtractedClarioData ExtractDataFromElements(List<ClarioElementData> elementData)
    {
        var data = new ExtractedClarioData();

        for (int i = 0; i < elementData.Count; i++)
        {
            if (!string.IsNullOrEmpty(data.Priority) &&
                !string.IsNullOrEmpty(data.Class) &&
                !string.IsNullOrEmpty(data.Accession))
            {
                break;
            }

            var elem = elementData[i];
            var name = elem.Name;
            var text = elem.Text;
            var automationId = elem.AutomationId;

            // PRIORITY
            if (string.IsNullOrEmpty(data.Priority))
            {
                string? candidatePriority = null;
                if (!string.IsNullOrEmpty(automationId) &&
                    automationId.Contains("priority", StringComparison.OrdinalIgnoreCase))
                {
                    candidatePriority = FindNextValue(elementData, i);
                }
                else if (!string.IsNullOrEmpty(name) &&
                         name.Contains("priority", StringComparison.OrdinalIgnoreCase) &&
                         name.Contains(":"))
                {
                    candidatePriority = FindNextValue(elementData, i);
                }

                if (candidatePriority != null && IsValidPriority(candidatePriority))
                {
                    data.Priority = candidatePriority;
                }
            }

            // CLASS (but not "priority" in automation_id)
            if (string.IsNullOrEmpty(data.Class))
            {
                string? candidateClass = null;
                if (!string.IsNullOrEmpty(automationId) &&
                    automationId.Contains("class", StringComparison.OrdinalIgnoreCase) &&
                    !automationId.Contains("priority", StringComparison.OrdinalIgnoreCase))
                {
                    candidateClass = FindNextValue(elementData, i);
                }
                else if (!string.IsNullOrEmpty(name) &&
                         name.Contains("class", StringComparison.OrdinalIgnoreCase) &&
                         name.Contains(":") &&
                         !name.Contains("priority", StringComparison.OrdinalIgnoreCase))
                {
                    candidateClass = FindNextValue(elementData, i);
                }

                if (candidateClass != null && IsValidPatientClass(candidateClass))
                {
                    data.Class = candidateClass;
                }
            }

            // ACCESSION
            if (string.IsNullOrEmpty(data.Accession))
            {
                if (!string.IsNullOrEmpty(automationId) &&
                    automationId.Contains("accession", StringComparison.OrdinalIgnoreCase))
                {
                    data.Accession = FindNextAccessionValue(elementData, i);
                }
                else if (!string.IsNullOrEmpty(name) &&
                         name.Contains("accession", StringComparison.OrdinalIgnoreCase) &&
                         name.Contains(":"))
                {
                    data.Accession = FindNextAccessionValue(elementData, i);
                }
            }
        }

        return data;
    }

    /// <summary>
    /// Find the next meaningful value after a label element.
    /// </summary>
    private static string FindNextValue(List<ClarioElementData> elements, int startIndex)
    {
        string[] skipValues = { "priority", "class", "accession" };

        for (int j = startIndex + 1; j < Math.Min(startIndex + 10, elements.Count); j++)
        {
            var nextElem = elements[j];
            var nextName = nextElem.Name;
            var nextText = nextElem.Text;

            if (!string.IsNullOrEmpty(nextName) &&
                !nextName.Contains(":") &&
                !skipValues.Any(s => nextName.Equals(s, StringComparison.OrdinalIgnoreCase)))
            {
                return nextName;
            }
            else if (!string.IsNullOrEmpty(nextText) &&
                     !nextText.Contains(":") &&
                     !skipValues.Any(s => nextText.Equals(s, StringComparison.OrdinalIgnoreCase)))
            {
                return nextText;
            }
        }

        return "";
    }

    /// <summary>
    /// Find the next accession value after a label element.
    /// </summary>
    private static string FindNextAccessionValue(List<ClarioElementData> elements, int startIndex)
    {
        for (int j = startIndex + 1; j < Math.Min(startIndex + 10, elements.Count); j++)
        {
            var nextElem = elements[j];
            var nextName = nextElem.Name;
            var nextText = nextElem.Text;

            if (!string.IsNullOrEmpty(nextName) &&
                !nextName.Contains(":") &&
                nextName.Length > 5 &&
                !nextName.Contains(" "))
            {
                return nextName;
            }
            else if (!string.IsNullOrEmpty(nextText) &&
                     !nextText.Contains(":") &&
                     nextText.Length > 5 &&
                     !nextText.Contains(" "))
            {
                return nextText;
            }
        }

        return "";
    }

    /// <summary>
    /// Combine Priority and Class into a single patient_class string (like Python's _combine_priority_and_class_clario).
    /// </summary>
    public static string CombinePriorityAndClass(string? priorityValue, string? classValue)
    {
        priorityValue = priorityValue?.Trim() ?? "";
        classValue = classValue?.Trim() ?? "";

        // Normalize: Replace ED/ER with "Emergency"
        if (!string.IsNullOrEmpty(priorityValue))
        {
            priorityValue = priorityValue.Replace("ED", "Emergency").Replace("ER", "Emergency");
        }
        if (!string.IsNullOrEmpty(classValue))
        {
            classValue = classValue.Replace("ED", "Emergency").Replace("ER", "Emergency");
        }

        // Extract urgency from Priority
        var urgencyParts = new List<string>();
        var locationFromPriority = new List<string>();

        if (!string.IsNullOrEmpty(priorityValue))
        {
            var priorityParts = priorityValue.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in priorityParts)
            {
                var isUrgency = UrgencyTerms.Any(term =>
                    part.Contains(term, StringComparison.OrdinalIgnoreCase));
                var isLocation = LocationTerms.Any(term =>
                    part.Contains(term, StringComparison.OrdinalIgnoreCase));

                if (isUrgency)
                {
                    urgencyParts.Add(part);
                }
                else if (isLocation)
                {
                    locationFromPriority.Add(part);
                }
            }
        }

        // Extract location from Class
        string locationFromClass = "";
        if (!string.IsNullOrEmpty(classValue))
        {
            foreach (var locationTerm in LocationTerms)
            {
                if (classValue.Contains(locationTerm, StringComparison.OrdinalIgnoreCase))
                {
                    locationFromClass = locationTerm;
                    break;
                }
            }
            if (string.IsNullOrEmpty(locationFromClass))
            {
                locationFromClass = classValue;
            }
        }

        // Determine final location (prefer Class over Priority)
        string finalLocation = !string.IsNullOrEmpty(locationFromClass)
            ? locationFromClass
            : string.Join(" ", locationFromPriority);

        // Remove redundant location from urgency parts
        if (!string.IsNullOrEmpty(finalLocation))
        {
            urgencyParts = urgencyParts
                .Where(part => !finalLocation.Contains(part, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Combine: urgency + location
        var combinedParts = new List<string>();
        combinedParts.AddRange(urgencyParts);
        if (!string.IsNullOrEmpty(finalLocation))
        {
            combinedParts.Add(finalLocation);
        }

        return string.Join(" ", combinedParts).Trim();
    }

    /// <summary>
    /// Clear the Clario window cache.
    /// </summary>
    public static void ClearCache()
    {
        lock (_cacheLock)
        {
            _cachedChromeWindow = null;
            _cachedContentArea = null;
        }
    }

    #region Existing Exam Notes / Critical Findings methods

    /// <summary>
    /// Find the Clario main window (legacy method for exam notes).
    /// </summary>
    public static AutomationElement? FindClarioWindow()
    {
        // Try Chrome window first (Clario Worklist)
        var chromeWindow = FindClarioChromeWindow();
        if (chromeWindow != null)
            return chromeWindow;

        // Fallback to process name
        var window = WindowExtraction.FindWindowByProcessName("Clario");
        if (window != null)
            return window;

        // Fallback to title search
        return WindowExtraction.FindWindowByTitle(ClarioWindowTitle, timeoutMs: 2000);
    }

    /// <summary>
    /// Find exam note elements in Clario.
    /// </summary>
    public static List<ClarioExamNote> GetExamNotes()
    {
        var notes = new List<ClarioExamNote>();

        try
        {
            var window = FindClarioWindow();
            if (window == null)
            {
                Log.Debug("Clario window not found");
                return notes;
            }

            var elements = WindowExtraction.GetDescendantsWithTimeout(window, maxElements: 2000, timeoutMs: 8000);

            foreach (var element in elements)
            {
                try
                {
                    var name = element.Name ?? "";
                    var controlType = element.Properties.ControlType.ValueOrDefault;

                    // Look for exam note containers
                    if (controlType == ControlType.DataItem &&
                        name.Contains("EXAM NOTE", StringComparison.OrdinalIgnoreCase))
                    {
                        var note = ParseExamNote(element, name);
                        if (note != null)
                        {
                            notes.Add(note);
                        }
                    }
                }
                catch
                {
                    // Skip problematic elements
                }
            }

            Log.Debug("Found {Count} exam notes in Clario", notes.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error getting exam notes from Clario");
        }

        return notes;
    }

    /// <summary>
    /// Parse an exam note element into structured data.
    /// </summary>
    private static ClarioExamNote? ParseExamNote(AutomationElement element, string rawText)
    {
        try
        {
            var note = new ClarioExamNote { RawText = rawText };

            // Extract accession from the note
            var accessionMatch = Regex.Match(rawText, @"\b[A-Z]{0,3}\d{6,12}\b", RegexOptions.IgnoreCase);
            if (accessionMatch.Success)
            {
                note.Accession = accessionMatch.Value;
            }

            // Extract date/time
            var dateMatch = Regex.Match(rawText, @"\d{1,2}/\d{1,2}/\d{2,4}");
            if (dateMatch.Success)
            {
                if (DateTime.TryParse(dateMatch.Value, out var date))
                {
                    note.NoteDate = date;
                }
            }

            // Extract physician name (usually after "by" or "Dr.")
            var physicianMatch = Regex.Match(rawText, @"(?:by|Dr\.?)\s+([A-Z][a-z]+(?:\s+[A-Z][a-z]+)?)", RegexOptions.IgnoreCase);
            if (physicianMatch.Success)
            {
                note.Physician = physicianMatch.Groups[1].Value;
            }

            // Get the actual note content from children
            try
            {
                var children = element.FindAllChildren();
                foreach (var child in children)
                {
                    var childText = WindowExtraction.GetElementText(child);
                    if (!string.IsNullOrWhiteSpace(childText) && childText.Length > 20)
                    {
                        note.Content = childText;
                        break;
                    }
                }
            }
            catch
            {
                // Children may not be accessible
            }

            return note.Accession != null ? note : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Scrape critical findings from Clario for a specific accession.
    /// </summary>
    public static CriticalFindingsData? GetCriticalFindings(string accession)
    {
        try
        {
            var window = FindClarioWindow();
            if (window == null)
                return null;

            var elements = WindowExtraction.GetDescendantsWithTimeout(window, maxElements: 3000, timeoutMs: 10000);
            var findings = new CriticalFindingsData { Accession = accession };

            bool foundAccession = false;
            var contentBuilder = new System.Text.StringBuilder();

            foreach (var element in elements)
            {
                try
                {
                    var text = WindowExtraction.GetElementText(element);
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    // Check if we found the accession
                    if (text.Contains(accession, StringComparison.OrdinalIgnoreCase))
                    {
                        foundAccession = true;
                    }

                    // Look for critical findings keywords
                    var lower = text.ToLowerInvariant();
                    if (lower.Contains("critical") || lower.Contains("finding") ||
                        lower.Contains("urgent") || lower.Contains("alert"))
                    {
                        findings.HasCriticalFinding = true;
                    }

                    // Collect note content
                    if (foundAccession && text.Length > 10 && !text.Contains("Button") && !text.Contains("Tab"))
                    {
                        contentBuilder.AppendLine(text);
                    }
                }
                catch
                {
                    // Skip
                }
            }

            if (foundAccession)
            {
                findings.NoteContent = contentBuilder.ToString();
                return findings;
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error getting critical findings from Clario for {Accession}", accession);
            return null;
        }
    }

    #endregion
}

#region Data Classes

/// <summary>
/// Internal class for element data during extraction.
/// </summary>
internal class ClarioElementData
{
    public int Depth { get; set; }
    public string AutomationId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>
/// Internal class for extracted Clario data before combining.
/// </summary>
internal class ExtractedClarioData
{
    public string Priority { get; set; } = "";
    public string Class { get; set; } = "";
    public string Accession { get; set; } = "";
}

/// <summary>
/// Patient class data extracted from Clario.
/// </summary>
public class ClarioPatientClassData
{
    public string PatientClass { get; set; } = "";
    public string Accession { get; set; } = "";
}

/// <summary>
/// Represents an exam note from Clario.
/// </summary>
public class ClarioExamNote
{
    public string? Accession { get; set; }
    public string? Content { get; set; }
    public string? Physician { get; set; }
    public DateTime? NoteDate { get; set; }
    public string RawText { get; set; } = "";
}

/// <summary>
/// Critical findings data from Clario.
/// </summary>
public class CriticalFindingsData
{
    public string Accession { get; set; } = "";
    public bool HasCriticalFinding { get; set; }
    public string NoteContent { get; set; } = "";
}

#endregion
