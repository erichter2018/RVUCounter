using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
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
    private static AutomationElement? _cachedDesktop;
    private static readonly object _lock = new();
    private static int _consecutiveComFailures;
    private static int _uiaCallCount;
    private static long _lastUiaResetTick64;
    private static long _lastHeartbeatTick64;
    private const long UiaResetIntervalMs = 300_000; // 5 minutes
    private const int UiaResetCallThreshold = 100;
    private const long HeartbeatIntervalMs = 60_000; // 1 minute
    private static readonly System.Text.RegularExpressions.Regex DatePatternRegex =
        new(@"\d{1,2}[/-]\d{1,2}[/-]\d{2,4}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Get or create the UIA3 automation instance.
    /// </summary>
    public static UIA3Automation GetAutomation()
    {
        lock (_lock)
        {
            _automation ??= Task.Run(() => new UIA3Automation()).GetAwaiter().GetResult();
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

            ReleaseElement(_cachedDesktop);
            _cachedDesktop = null;
            ClarioExtractor.ClearCache();

            try
            {
                _automation?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error disposing old UIA3 automation instance");
            }
            _automation = Task.Run(() => new UIA3Automation()).GetAwaiter().GetResult();
            _consecutiveComFailures = 0;
            _lastUiaResetTick64 = Environment.TickCount64;
            Interlocked.Exchange(ref _uiaCallCount, 0);
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
    /// Get the desktop element, cached to avoid creating a new COM wrapper each call.
    /// </summary>
    public static AutomationElement GetDesktop()
    {
        var desktop = _cachedDesktop;
        if (desktop != null)
        {
            try
            {
                _ = desktop.Properties.ProcessId.ValueOrDefault;
                return desktop;
            }
            catch
            {
                _cachedDesktop = null;
            }
        }
        desktop = GetAutomation().GetDesktop();
        _cachedDesktop = desktop;
        return desktop;
    }

    /// <summary>
    /// Periodic UIA connection reset to combat Chrome's ever-growing accessibility tree.
    /// Call at the top of scrape timer callbacks.
    /// </summary>
    public static void ResetUiaConnectionIfNeeded()
    {
        Interlocked.Increment(ref _uiaCallCount);

        long now = Environment.TickCount64;
        long elapsed = now - _lastUiaResetTick64;
        if (elapsed < UiaResetIntervalMs || _uiaCallCount < UiaResetCallThreshold)
        {
            LogHeartbeatIfNeeded(now);
            return;
        }

        lock (_lock)
        {
            var callsSinceReset = Interlocked.Exchange(ref _uiaCallCount, 0);
            Log.Information("Periodic UIA reset ({Calls} calls since last reset)", callsSinceReset);

            // Release all cached elements before disposing automation
            ReleaseElement(_cachedDesktop);
            _cachedDesktop = null;
            ClarioExtractor.ClearCache();

            // Force GC to release remaining COM wrappers before disposing
            GC.Collect(2, GCCollectionMode.Forced);
            GC.WaitForPendingFinalizers();

            try { _automation?.Dispose(); } catch { }
            _automation = Task.Run(() => new UIA3Automation()).GetAwaiter().GetResult();

            _lastUiaResetTick64 = Environment.TickCount64;
            Log.Information("UIA reset complete");
        }
    }

    /// <summary>
    /// Log periodic heartbeat with memory/handle stats for diagnosing degradation.
    /// </summary>
    private static void LogHeartbeatIfNeeded(long now)
    {
        if (now - _lastHeartbeatTick64 < HeartbeatIntervalMs)
            return;
        _lastHeartbeatTick64 = now;

        try
        {
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            Log.Debug("UIA heartbeat: managed={ManagedMB}MB, workingSet={WsMB}MB, " +
                "private={PrivMB}MB, handles={Handles}, threads={Threads}, " +
                "GC={G0}/{G1}/{G2}, uiaCalls={UiaCalls}",
                GC.GetTotalMemory(false) / 1024 / 1024,
                proc.WorkingSet64 / 1024 / 1024,
                proc.PrivateMemorySize64 / 1024 / 1024,
                proc.HandleCount,
                proc.Threads.Count,
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                _uiaCallCount);
        }
        catch { }
    }

    /// <summary>
    /// Find a window by title (partial match).
    /// </summary>
    public static AutomationElement? FindWindowByTitle(string titleContains, int timeoutMs = Core.Config.UiAutomationTimeoutMs)
    {
        try
        {
            var automation = GetAutomation();
            var desktop = GetDesktop();
            var cf = automation.ConditionFactory;

            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);

            while (DateTime.Now < deadline)
            {
                AutomationElement[]? windows = null;
                AutomationElement? found = null;
                try
                {
                    windows = desktop.FindAllChildren(cf.ByControlType(ControlType.Window));

                    foreach (var window in windows)
                    {
                        try
                        {
                            var title = window.Properties.Name.ValueOrDefault ?? "";
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
                Log.Debug("No processes found for {Process}", processName);
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
            // No CacheRequest — elements need live COM references for property reads.
            // CacheRequest returns cache-only elements whose live reads fail (all properties
            // come back empty for WebView2 content that loads asynchronously).
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

    // UIA property IDs for cached reads via native COM GetCachedPropertyValue
    private const int UIA_NamePropertyId = 30005;
    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_ClassNamePropertyId = 30012;
    private const int UIA_AutomationIdPropertyId = 30011;

    /// <summary>
    /// Read a cached property from the native COM element (instant, in-process).
    /// Returns null if the element doesn't have cached data.
    /// </summary>
    private static object? GetNativeCachedProperty(AutomationElement el, int propertyId)
    {
        if (el.FrameworkAutomationElement is UIA3FrameworkAutomationElement uia3)
        {
            dynamic native = uia3.NativeElement;
            return native.GetCachedPropertyValue(propertyId);
        }
        return null;
    }

    /// <summary>
    /// Get Name from cache (instant), falling back to live read if not cached.
    /// </summary>
    public static string GetCachedName(AutomationElement el)
    {
        try
        {
            var val = GetNativeCachedProperty(el, UIA_NamePropertyId);
            if (val is string s) return s;
        }
        catch { }
        try { return el.Name ?? ""; } catch { return ""; }
    }

    /// <summary>
    /// Get AutomationId from cache (instant), falling back to live read if not cached.
    /// </summary>
    public static string GetCachedAutoId(AutomationElement el)
    {
        try
        {
            var val = GetNativeCachedProperty(el, UIA_AutomationIdPropertyId);
            if (val is string s) return s;
        }
        catch { }
        try { return el.Properties.AutomationId.ValueOrDefault ?? ""; } catch { return ""; }
    }

    /// <summary>
    /// Get ControlType from cache (instant), falling back to live read if not cached.
    /// </summary>
    public static ControlType? GetCachedControlType(AutomationElement el)
    {
        try
        {
            var val = GetNativeCachedProperty(el, UIA_ControlTypePropertyId);
            if (val is ControlType ct) return ct;
            if (val is int ctId) return (ControlType)ctId;
        }
        catch { }
        try { return el.Properties.ControlType.ValueOrDefault; } catch { return null; }
    }

    /// <summary>
    /// Get ClassName from cache (instant), falling back to live read if not cached.
    /// </summary>
    public static string GetCachedClassName(AutomationElement el)
    {
        try
        {
            var val = GetNativeCachedProperty(el, UIA_ClassNamePropertyId);
            if (val is string s) return s;
        }
        catch { }
        try { return el.Properties.ClassName.ValueOrDefault ?? ""; } catch { return ""; }
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
            ReleaseElement(_cachedDesktop);
            _cachedDesktop = null;
            _automation?.Dispose();
            _automation = null;
        }
    }
}
