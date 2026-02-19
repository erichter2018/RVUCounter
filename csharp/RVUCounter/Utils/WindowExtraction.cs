using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.UIA3;
using FlaUI.UIA3.Converters;
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
    private static int _consecutiveComFailures;
    private static readonly System.Text.RegularExpressions.Regex DatePatternRegex =
        new(@"\d{1,2}[/-]\d{1,2}[/-]\d{2,4}", System.Text.RegularExpressions.RegexOptions.Compiled);

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
    /// Dispose and recreate the UIA3 automation singleton to recover from
    /// degraded COM state (e.g., after sustained OOM or 0x80131505 errors).
    /// </summary>
    public static void RecreateAutomation()
    {
        lock (_lock)
        {
            Log.Warning("Recreating UIA3 automation instance after {Failures} consecutive COM failures",
                _consecutiveComFailures);
            try
            {
                _automation?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error disposing old UIA3 automation instance");
            }
            _automation = new UIA3Automation();
            _consecutiveComFailures = 0;
        }
    }

    /// <summary>
    /// Record a successful UIA operation, resetting the failure counter.
    /// </summary>
    private static void RecordUiaSuccess()
    {
        Interlocked.Exchange(ref _consecutiveComFailures, 0);
    }

    /// <summary>
    /// Record a UIA COM failure. If consecutive failures exceed the threshold,
    /// trigger recovery by recreating the automation instance.
    /// </summary>
    private static void RecordUiaFailure()
    {
        int failures = Interlocked.Increment(ref _consecutiveComFailures);
        if (failures >= Core.Config.UiaConsecutiveFailuresBeforeRecovery)
        {
            RecreateAutomation();
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
                AutomationElement[]? windows = null;
                AutomationElement? found = null;
                try
                {
                    windows = desktop.FindAllChildren(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window));

                    foreach (var window in windows)
                    {
                        try
                        {
                            var title = window.Name;
                            if (!string.IsNullOrEmpty(title) &&
                                title.Contains(titleContains, StringComparison.OrdinalIgnoreCase))
                            {
                                Log.Debug("Found window: {Title}", title);
                                RecordUiaSuccess();
                                found = window;
                                return found;
                            }
                        }
                        catch
                        {
                            // Window may have closed, continue
                        }
                    }
                }
                finally
                {
                    if (windows != null)
                        foreach (var w in windows)
                            if (w != found) ReleaseElement(w);
                }

                Thread.Sleep(100);
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error finding window: {Title}", titleContains);
            RecordUiaFailure();
            return null;
        }
    }

    /// <summary>
    /// Find a window by process name.
    /// </summary>
    public static AutomationElement? FindWindowByProcessName(string processName)
    {
        System.Diagnostics.Process[]? processes = null;
        try
        {
            processes = System.Diagnostics.Process.GetProcessesByName(processName);
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
                            // Validate the window handle is still accessible
                            try
                            {
                                _ = window.Name; // Quick validity check
                            }
                            catch
                            {
                                Log.Debug("Stale window handle for {Process}, skipping", processName);
                                continue;
                            }
                            Log.Debug("Found window for process: {Process}", processName);
                            RecordUiaSuccess();
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
            RecordUiaFailure();
            return null;
        }
        finally
        {
            if (processes != null)
            {
                foreach (var p in processes)
                    p.Dispose();
            }
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
        var resultSet = new HashSet<AutomationElement>(ReferenceEqualityComparer.Instance);
        using var cts = new CancellationTokenSource(timeoutMs);
        AutomationElement[]? descendants = null;

        try
        {
            descendants = element.FindAllDescendants();
            foreach (var desc in descendants)
            {
                if (cts.Token.IsCancellationRequested || result.Count >= maxElements)
                    break;

                result.Add(desc);
                resultSet.Add(desc);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Warning("GetDescendants timed out after {Timeout}ms", timeoutMs);
        }
        catch (OutOfMemoryException ex)
        {
            Log.Warning(ex, "OOM during GetDescendants — recording COM failure");
            RecordUiaFailure();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error getting descendants");
            RecordUiaFailure();
        }
        finally
        {
            // Release elements that were NOT added to result (exceeded cap or timed out)
            if (descendants != null)
                foreach (var d in descendants)
                    if (!resultSet.Contains(d)) ReleaseElement(d);
        }

        if (result.Count > 0)
            RecordUiaSuccess();

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

        // Exclude common non-accession patterns, but only for multi-word text.
        // Single-token accession codes like "320CT26001546ELP" legitimately embed
        // modality codes (CT, MR) — substring matching would reject them.
        if (text.Contains(' '))
        {
            string[] excludePatterns = { "patient", "study", "date", "time", "report", "image",
                "series", "view", "chest", "head", "abdomen", "pelvis", "spine", "mri", "ct" };

            foreach (var pattern in excludePatterns)
            {
                if (lower.Contains(pattern))
                    return false;
            }
        }

        // Check for date patterns (MM/DD/YYYY or similar)
        if (DatePatternRegex.IsMatch(text))
            return false;

        // Accession should contain some digits
        int digitCount = text.Count(char.IsDigit);
        int letterCount = text.Count(char.IsLetter);

        if (digitCount < 2)
            return false;

        // Real accessions have letters mixed in, OR are longer pure-numeric strings
        // Short pure-numeric strings (< 6 chars) are likely garbage (patient IDs, room numbers, etc.)
        // Lowered from 10 to 6 to allow 7-digit numeric accessions (e.g. "1057034")
        if (letterCount == 0 && text.Length < 6)
            return false;

        // If mostly digits with few letters, require minimum length of 6
        // Lowered from 8 to 6 to allow shorter mixed accessions
        if (letterCount < 2 && text.Length < 6)
            return false;

        // Accession should be mostly alphanumeric
        int alphanumCount = text.Count(c => char.IsLetterOrDigit(c));
        if (alphanumCount < text.Length * 0.8)
            return false;

        return true;
    }

    /// <summary>
    /// Release COM RCW wrappers for an array of AutomationElements.
    /// Call after FindAllDescendants/FindAllChildren results are no longer needed.
    /// </summary>
    public static void ReleaseElements(AutomationElement[]? elements)
    {
        if (elements == null) return;
        foreach (var el in elements)
            ReleaseElement(el);
    }

    /// <summary>
    /// Release COM RCW wrappers for a list of AutomationElements.
    /// </summary>
    public static void ReleaseElements(List<AutomationElement>? elements)
    {
        if (elements == null) return;
        foreach (var el in elements)
            ReleaseElement(el);
    }

    /// <summary>
    /// Release the COM RCW wrapper for a single AutomationElement.
    /// </summary>
    public static void ReleaseElement(AutomationElement? el)
    {
        if (el == null) return;
        try
        {
            if (el.FrameworkAutomationElement is UIA3FrameworkAutomationElement uia3)
                Marshal.ReleaseComObject(uia3.NativeElement);
        }
        catch (InvalidComObjectException) { }
        catch (Exception) { }
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
