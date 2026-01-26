using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.UIA3;
using Serilog;

namespace RVUCounter.Utils;

/// <summary>
/// Base utilities for Windows UI Automation using FlaUI.
/// Provides window finding and element caching.
/// </summary>
public static class WindowExtraction
{
    private static UIA3Automation? _automation;
    private static readonly object _lock = new();

    /// <summary>
    /// Get or create the UIA3 automation instance.
    /// </summary>
    public static UIA3Automation GetAutomation()
    {
        lock (_lock)
        {
            _automation ??= new UIA3Automation();
            return _automation;
        }
    }

    /// <summary>
    /// Get the desktop element (cached for performance).
    /// </summary>
    public static AutomationElement GetDesktop()
    {
        return GetAutomation().GetDesktop();
    }

    /// <summary>
    /// Find a window by title (partial match).
    /// </summary>
    public static AutomationElement? FindWindowByTitle(string titleContains, int timeoutMs = Core.Config.UiAutomationTimeoutMs)
    {
        try
        {
            var automation = GetAutomation();
            var desktop = automation.GetDesktop();
            var cf = automation.ConditionFactory;

            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);

            while (DateTime.Now < deadline)
            {
                var windows = desktop.FindAllChildren(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window));

                foreach (var window in windows)
                {
                    try
                    {
                        var title = window.Name;
                        if (!string.IsNullOrEmpty(title) && 
                            title.Contains(titleContains, StringComparison.OrdinalIgnoreCase))
                        {
                            Log.Debug("Found window: {Title}", title);
                            return window;
                        }
                    }
                    catch
                    {
                        // Window may have closed, continue
                    }
                }

                Thread.Sleep(100);
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error finding window: {Title}", titleContains);
            return null;
        }
    }

    /// <summary>
    /// Find a window by process name.
    /// </summary>
    public static AutomationElement? FindWindowByProcessName(string processName)
    {
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName(processName);
            if (processes.Length == 0)
            {
                return null;
            }

            var automation = GetAutomation();

            foreach (var process in processes)
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        var window = automation.FromHandle(process.MainWindowHandle);
                        if (window != null)
                        {
                            Log.Debug("Found window for process: {Process}", processName);
                            return window;
                        }
                    }
                }
                catch
                {
                    // Continue to next process
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error finding window by process: {Process}", processName);
            return null;
        }
    }

    /// <summary>
    /// Get all descendants of an element with timeout protection.
    /// </summary>
    public static List<AutomationElement> GetDescendantsWithTimeout(
        AutomationElement element,
        int maxElements = Core.Config.UiAutomationMaxElements,
        int timeoutMs = 10000)
    {
        var result = new List<AutomationElement>();
        var cts = new CancellationTokenSource(timeoutMs);

        try
        {
            var descendants = element.FindAllDescendants();
            foreach (var desc in descendants)
            {
                if (cts.Token.IsCancellationRequested || result.Count >= maxElements)
                    break;

                result.Add(desc);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Warning("GetDescendants timed out after {Timeout}ms", timeoutMs);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error getting descendants");
        }

        return result;
    }

    /// <summary>
    /// Safely get the Name property from an element with timeout.
    /// </summary>
    public static string GetElementName(AutomationElement element, int timeoutMs = 1000)
    {
        try
        {
            var task = Task.Run(() =>
            {
                try
                {
                    return element.Name ?? "";
                }
                catch
                {
                    return "";
                }
            });

            if (task.Wait(timeoutMs))
            {
                return task.Result;
            }

            return "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Safely get text from an element with timeout.
    /// Returns AutomationId or Value pattern as alternative text.
    /// </summary>
    public static string GetElementText(AutomationElement element, int timeoutMs = 1000)
    {
        try
        {
            var task = Task.Run(() =>
            {
                try
                {
                    // Try value pattern first (for text boxes, etc.)
                    if (element.Patterns.Value.IsSupported)
                    {
                        var value = element.Patterns.Value.Pattern.Value.ValueOrDefault;
                        if (!string.IsNullOrEmpty(value))
                            return value;
                    }

                    // Fall back to AutomationId
                    return element.Properties.AutomationId.ValueOrDefault ?? "";
                }
                catch
                {
                    return "";
                }
            });

            if (task.Wait(timeoutMs))
            {
                return task.Result;
            }

            return "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Check if a string looks like an accession number.
    /// </summary>
    public static bool IsAccessionLike(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 5 || text.Length > 25)
            return false;

        // Accession patterns: alphanumeric, often with specific prefixes
        // Exclude dates, common words, etc.
        var lower = text.ToLowerInvariant();

        // Exclude common non-accession patterns
        string[] excludePatterns = { "patient", "study", "date", "time", "report", "image",
            "series", "view", "chest", "head", "abdomen", "pelvis", "spine", "mri", "ct" };

        foreach (var pattern in excludePatterns)
        {
            if (lower.Contains(pattern))
                return false;
        }

        // Check for date patterns (MM/DD/YYYY or similar)
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\d{1,2}[/-]\d{1,2}[/-]\d{2,4}"))
            return false;

        // Accession should contain some digits
        int digitCount = text.Count(char.IsDigit);
        int letterCount = text.Count(char.IsLetter);

        if (digitCount < 2)
            return false;

        // Real accessions have letters mixed in, OR are longer pure-numeric strings
        // Short pure-numeric strings (< 10 chars) are likely garbage (patient IDs, room numbers, etc.)
        if (letterCount == 0 && text.Length < 10)
            return false;

        // If mostly digits with few letters, require minimum length of 8
        // This catches garbage like "132562" while allowing real accessions like "SST2601230019274CST"
        if (letterCount < 2 && text.Length < 8)
            return false;

        // Accession should be mostly alphanumeric
        int alphanumCount = text.Count(c => char.IsLetterOrDigit(c));
        if (alphanumCount < text.Length * 0.8)
            return false;

        return true;
    }

    /// <summary>
    /// Dispose of automation resources.
    /// </summary>
    public static void Cleanup()
    {
        lock (_lock)
        {
            _automation?.Dispose();
            _automation = null;
        }
    }
}
