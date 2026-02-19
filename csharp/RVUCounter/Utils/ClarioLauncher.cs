using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Serilog;

namespace RVUCounter.Utils;

/// <summary>
/// Opens studies in Clario by automating the Chrome-based Clario worklist.
/// Flow: paste accession → Enter → wait for results → click action column.
/// </summary>
public static class ClarioLauncher
{
    /// <summary>
    /// Searches for an accession in Clario and clicks the action column to open the study.
    /// </summary>
    public static async Task<bool> OpenStudyByAccession(string accession)
    {
        if (string.IsNullOrWhiteSpace(accession))
        {
            Log.Warning("ClarioLauncher: Cannot open study - accession is empty");
            return false;
        }

        return await Task.Run(() => OpenStudyByAccessionInternal(accession));
    }

    private static bool OpenStudyByAccessionInternal(string accession)
    {
        try
        {
            var chromeWindow = ClarioExtractor.FindClarioChromeWindow(useCache: false);
            if (chromeWindow == null)
            {
                Log.Warning("ClarioLauncher: Clario Chrome window not found");
                return false;
            }

            // Step 1: Find and populate the accession search field
            var searchField = FindAccessionSearchField(chromeWindow);
            if (searchField == null)
            {
                Log.Warning("ClarioLauncher: Could not find accession search field");
                return false;
            }

            Log.Debug("ClarioLauncher: Entering accession {Accession}", accession);
            if (!SetFieldValue(searchField, accession))
            {
                Log.Warning("ClarioLauncher: Could not set accession value");
                return false;
            }

            // Step 2: Press Enter to trigger search
            Log.Debug("ClarioLauncher: Pressing Enter to search");
            searchField.Focus();
            Thread.Sleep(100);
            Keyboard.Press(VirtualKeyShort.ENTER);

            // Step 3: Wait for search results to load
            Thread.Sleep(2000);

            // Step 4: Find and click the action column in search results
            // Re-fetch window reference since the view changed
            chromeWindow = ClarioExtractor.FindClarioChromeWindow(useCache: false);
            if (chromeWindow == null)
            {
                Log.Warning("ClarioLauncher: Lost Clario window after search");
                return false;
            }

            var actionCell = FindActionColumnCell(chromeWindow);
            if (actionCell == null)
            {
                Log.Warning("ClarioLauncher: Could not find action column in search results");
                return false;
            }

            // Step 5: Click at 55% from left edge of the action cell
            ClickActionCell(actionCell);

            Log.Information("ClarioLauncher: Opened study {Accession} via action column", accession);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ClarioLauncher: Error opening study {Accession}", accession);
            return false;
        }
    }

    /// <summary>
    /// Sets the value of a text field, trying Value pattern first, then keyboard fallback.
    /// </summary>
    private static bool SetFieldValue(AutomationElement field, string value)
    {
        // Try UIA Value pattern first
        try
        {
            if (field.Patterns.Value.IsSupported)
            {
                field.Patterns.Value.Pattern.SetValue(value);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ClarioLauncher: Value pattern failed, using keyboard");
        }

        // Fallback: focus + Ctrl+A + type
        try { field.Focus(); }
        catch
        {
            try { field.Click(); }
            catch (Exception ex)
            {
                Log.Warning(ex, "ClarioLauncher: Could not focus field");
                return false;
            }
        }
        Thread.Sleep(150);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Thread.Sleep(50);
        Keyboard.Type(value);
        return true;
    }

    /// <summary>
    /// Finds the accession search Edit field.
    /// Strategy: find the "fulltext-*-inputEl" field, then the next Edit field
    /// in the list is always the accession field. This is stable regardless of
    /// how many extra Edit fields appear when search results are open.
    /// </summary>
    private static AutomationElement? FindAccessionSearchField(AutomationElement chromeWindow)
    {
        AutomationElement[]? allEdits = null;
        AutomationElement? found = null;
        try
        {
            var automation = WindowExtraction.GetAutomation();
            var cf = automation.ConditionFactory;
            allEdits = chromeWindow.FindAllDescendants(cf.ByControlType(ControlType.Edit));

            Log.Debug("ClarioLauncher: {Total} Edit fields total", allEdits.Length);

            // Find the fulltext field, then return the next Edit field after it
            bool foundFulltext = false;
            foreach (var edit in allEdits)
            {
                try
                {
                    var autoId = edit.Properties.AutomationId.ValueOrDefault ?? "";

                    if (!foundFulltext)
                    {
                        if (autoId.StartsWith("fulltext-", StringComparison.OrdinalIgnoreCase))
                        {
                            foundFulltext = true;
                            Log.Debug("ClarioLauncher: Found fulltext anchor: '{Id}'", autoId);
                        }
                    }
                    else
                    {
                        // The very next Edit after fulltext is the accession field
                        Log.Information("ClarioLauncher: Using field after fulltext: AutomationId='{Id}'", autoId);
                        found = edit;
                        return found;
                    }
                }
                catch { }
            }

            Log.Warning("ClarioLauncher: Could not find fulltext anchor field");
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ClarioLauncher: Error finding accession field");
            return null;
        }
        finally
        {
            if (allEdits != null)
                foreach (var e in allEdits)
                    if (e != found) WindowExtraction.ReleaseElement(e);
        }
    }

    /// <summary>
    /// Finds the first action column cell in the search results grid.
    /// Action cells have "x-action-col-cell" or "actioncolumn" in their ClassName.
    /// Looks for DataItem elements in the search results area.
    /// </summary>
    private static AutomationElement? FindActionColumnCell(AutomationElement chromeWindow)
    {
        AutomationElement[]? allDataItems = null;
        AutomationElement? found = null;
        try
        {
            var automation = WindowExtraction.GetAutomation();
            var cf = automation.ConditionFactory;

            // Find all DataItem elements (grid cells)
            allDataItems = chromeWindow.FindAllDescendants(cf.ByControlType(ControlType.DataItem));

            Log.Debug("ClarioLauncher: Scanning {Count} DataItem elements for action column", allDataItems.Length);

            foreach (var item in allDataItems)
            {
                try
                {
                    var className = item.Properties.ClassName.ValueOrDefault ?? "";

                    // Must be an action column cell AND in the search results grid
                    // ClassName pattern: "...content_search_result_Grid_column_actionsColumn...x-action-col-cell"
                    if ((className.Contains("action-col-cell") || className.Contains("actioncolumn")) &&
                        className.Contains("search_result"))
                    {
                        Log.Information("ClarioLauncher: Found search result action cell");
                        found = item;
                        return found;
                    }
                }
                catch { }
            }

            Log.Warning("ClarioLauncher: No action column cells found");
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ClarioLauncher: Error finding action column");
            return null;
        }
        finally
        {
            if (allDataItems != null)
                foreach (var d in allDataItems)
                    if (d != found) WindowExtraction.ReleaseElement(d);
        }
    }

    /// <summary>
    /// Clicks at 55% from the left edge of the action cell (center vertically).
    /// </summary>
    private static void ClickActionCell(AutomationElement actionCell)
    {
        try
        {
            var rect = actionCell.BoundingRectangle;
            var clickX = (int)(rect.Left + rect.Width * 0.55);
            var clickY = (int)(rect.Top + rect.Height / 2.0);

            Log.Debug("ClarioLauncher: Clicking action cell at ({X}, {Y}) — rect: L={L}, T={T}, W={W}, H={H}",
                clickX, clickY, (int)rect.Left, (int)rect.Top, (int)rect.Width, (int)rect.Height);

            Mouse.Click(new System.Drawing.Point(clickX, clickY));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ClarioLauncher: Error clicking action cell");
        }
    }
}
