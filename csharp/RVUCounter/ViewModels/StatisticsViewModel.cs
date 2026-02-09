using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RVUCounter.Core;
using RVUCounter.Data;
using RVUCounter.Logic;
using RVUCounter.Models;
using Serilog;
using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using SkiaSharp;

namespace RVUCounter.ViewModels;

/// <summary>
/// ViewModel for the Statistics window.
/// Implements view modes from Python: by_hour, by_modality, by_study_type, summary
/// </summary>
public partial class StatisticsViewModel : ObservableObject
{
    private readonly DataManager _dataManager;
    private readonly TrendAnalysisService _trendService;
    private readonly ProductivityInsightsService _insightsService;
    private readonly TbwuLookup _tbwuLookup;
    private List<StudyRecord> _allRecords = new();

    [ObservableProperty]
    private string _periodDescription = "All Time";

    [ObservableProperty]
    private string _viewMode = "summary";

    [ObservableProperty]
    private string _summaryText = "";

    [ObservableProperty]
    private ObservableCollection<StatRow> _tableData = new();

    [ObservableProperty]
    private ObservableCollection<string> _tableColumns = new();

    // Summary stats
    [ObservableProperty]
    private int _totalStudies;

    [ObservableProperty]
    private double _totalRvu;

    [ObservableProperty]
    private double _avgRvuPerStudy;

    [ObservableProperty]
    private int _shiftCount;

    // Charts
    [ObservableProperty]
    private ISeries[] _chartSeries = Array.Empty<ISeries>();

    [ObservableProperty]
    private Axis[] _xAxes = Array.Empty<Axis>();

    [ObservableProperty]
    private Axis[] _yAxes = Array.Empty<Axis>();

    [ObservableProperty]
    private ISeries[] _pieSeries = Array.Empty<ISeries>();

    // Second pie series for summary view (average time to read by modality)
    [ObservableProperty]
    private ISeries[] _timePieSeries = Array.Empty<ISeries>();

    [ObservableProperty]
    private bool _showCartesianChart;

    [ObservableProperty]
    private bool _showPieChart;

    // Flag for showing side-by-side charts panel
    [ObservableProperty]
    private bool _showSideCharts;

    // Control what's shown in the side panel
    [ObservableProperty]
    private bool _showModalityToggle;  // RVU/Studies toggle (summary only)

    [ObservableProperty]
    private bool _showSecondChart = true;  // Second pie chart (summary only)

    [ObservableProperty]
    private string _sideChartTitle = "Modality Breakdown";

    [ObservableProperty]
    private string _secondChartTitle = "Avg Time to Read";

    // Show bar chart instead of pie chart in side panel
    [ObservableProperty]
    private bool _showSideBarChart;

    [ObservableProperty]
    private ISeries[] _sideBarSeries = Array.Empty<ISeries>();

    [ObservableProperty]
    private Axis[] _sideBarXAxes = Array.Empty<Axis>();

    [ObservableProperty]
    private Axis[] _sideBarYAxes = Array.Empty<Axis>();

    [ObservableProperty]
    private RectangularSection[] _sideBarSections = Array.Empty<RectangularSection>();

    // Custom legend items for bar chart
    public ObservableCollection<LegendItem> BarChartLegendItems { get; } = new();

    // All Studies view - dedicated collection with typed data for proper sorting
    [ObservableProperty]
    private ObservableCollection<AllStudyRow> _allStudiesData = new();

    [ObservableProperty]
    private bool _isAllStudiesView;

    // Sorting state for All Studies view
    private string _allStudiesSortColumn = "Timestamp";
    private bool _allStudiesSortDescending = true;

    // ===========================================
    // TIER 4 ANALYTICS - TRENDS & INSIGHTS
    // ===========================================

    // Trends view
    [ObservableProperty]
    private bool _isTrendsView;

    [ObservableProperty]
    private string _trendInsightMessage = "";

    [ObservableProperty]
    private string _trendDirection = "stable";

    [ObservableProperty]
    private string _trendArrow = "→";

    [ObservableProperty]
    private string _weekOverWeekChange = "";

    [ObservableProperty]
    private string _monthOverMonthChange = "";

    [ObservableProperty]
    private string _trendMetric = "rvu";  // "rvu" or "rvuPerHour"

    [ObservableProperty]
    private bool _trendIgnoreShortShifts;

    partial void OnTrendMetricChanged(string value) => RefreshData();
    partial void OnTrendIgnoreShortShiftsChanged(bool value) => RefreshData();

    // Heatmap view
    [ObservableProperty]
    private bool _isHeatmapView;

    [ObservableProperty]
    private ObservableCollection<HeatmapCell> _heatmapData = new();

    [ObservableProperty]
    private string _heatmapLegend = "";

    // Insights view
    [ObservableProperty]
    private bool _isInsightsView;

    [ObservableProperty]
    private ProductivityInsights? _productivityInsights;

    [ObservableProperty]
    private ObservableCollection<string> _insightRecommendations = new();

    /// <summary>
    /// Sort All Studies by the specified column. Clicking the same column toggles direction.
    /// </summary>
    [RelayCommand]
    private void SortAllStudies(string column)
    {
        if (_allStudiesSortColumn == column)
        {
            // Toggle direction
            _allStudiesSortDescending = !_allStudiesSortDescending;
        }
        else
        {
            _allStudiesSortColumn = column;
            _allStudiesSortDescending = true; // Default to descending for new column
        }
        ApplyAllStudiesSort();
    }

    private void ApplyAllStudiesSort()
    {
        var sorted = _allStudiesSortColumn switch
        {
            "RowNumber" => _allStudiesSortDescending
                ? AllStudiesData.OrderByDescending(r => r.RowNumber)
                : AllStudiesData.OrderBy(r => r.RowNumber),
            "Timestamp" => _allStudiesSortDescending
                ? AllStudiesData.OrderByDescending(r => r.Timestamp)
                : AllStudiesData.OrderBy(r => r.Timestamp),
            "Procedure" => _allStudiesSortDescending
                ? AllStudiesData.OrderByDescending(r => r.Procedure)
                : AllStudiesData.OrderBy(r => r.Procedure),
            "Rvu" => _allStudiesSortDescending
                ? AllStudiesData.OrderByDescending(r => r.Rvu)
                : AllStudiesData.OrderBy(r => r.Rvu),
            "Duration" => _allStudiesSortDescending
                ? AllStudiesData.OrderByDescending(r => r.DurationSeconds)
                : AllStudiesData.OrderBy(r => r.DurationSeconds),
            _ => AllStudiesData.OrderByDescending(r => r.Timestamp)
        };

        var list = sorted.ToList();
        AllStudiesData.Clear();
        foreach (var item in list)
            AllStudiesData.Add(item);

        // Update row numbers after sort
        for (int i = 0; i < AllStudiesData.Count; i++)
            AllStudiesData[i].RowNumber = i + 1;
    }

    /// <summary>
    /// Delete a study record from the database
    /// </summary>
    [RelayCommand]
    private void DeleteStudy(AllStudyRow? row)
    {
        if (row == null) return;

        try
        {
            _dataManager.Database.DeleteRecord(row.RecordId);
            AllStudiesData.Remove(row);

            // Update row numbers
            for (int i = 0; i < AllStudiesData.Count; i++)
                AllStudiesData[i].RowNumber = i + 1;

            // Refresh totals
            TotalStudies = AllStudiesData.Count;
            TotalRvu = AllStudiesData.Sum(r => r.Rvu);
            AvgRvuPerStudy = TotalStudies > 0 ? TotalRvu / TotalStudies : 0;
            SummaryText = $"Total: {TotalStudies} studies  |  {TotalRvu:F1} RVU  |  Avg: {AvgRvuPerStudy:F2} RVU/study";

            Log.Information("Deleted study record {RecordId}", row.RecordId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete study record {RecordId}", row.RecordId);
        }
    }

    // Projection settings (bound to UI controls)
    // These reset at the start of each month
    public int ProjectionShifts
    {
        get
        {
            CheckAndResetMonthlyProjections();
            return _dataManager.Settings.ProjectionDays;
        }
        set
        {
            if (value >= 0 && value <= 31)
            {
                _dataManager.Settings.ProjectionDays = value;
                _dataManager.Settings.LastProjectionMonth = DateTime.Now.ToString("yyyy-MM");
                _dataManager.SaveSettings();
                OnPropertyChanged();
                RefreshData();
            }
        }
    }

    public int ProjectionExtraHours
    {
        get
        {
            CheckAndResetMonthlyProjections();
            return _dataManager.Settings.ProjectionExtraHours;
        }
        set
        {
            if (value >= 0 && value <= 200)
            {
                _dataManager.Settings.ProjectionExtraHours = value;
                _dataManager.Settings.LastProjectionMonth = DateTime.Now.ToString("yyyy-MM");
                _dataManager.SaveSettings();
                OnPropertyChanged();
                RefreshData();
            }
        }
    }

    /// <summary>
    /// Reset projection settings if we're in a new month
    /// </summary>
    private void CheckAndResetMonthlyProjections()
    {
        var currentMonth = DateTime.Now.ToString("yyyy-MM");
        if (_dataManager.Settings.LastProjectionMonth != currentMonth)
        {
            _dataManager.Settings.ProjectionDays = 14;  // Default shifts
            _dataManager.Settings.ProjectionExtraHours = 0;
            _dataManager.Settings.LastProjectionMonth = currentMonth;
            _dataManager.SaveSettings();
        }
    }

    [RelayCommand]
    private void IncrementProjectionValue(string? key)
    {
        if (key == "ProjectionDays") ProjectionShifts++;
        else if (key == "ProjectionExtraHours") ProjectionExtraHours++;
    }

    [RelayCommand]
    private void DecrementProjectionValue(string? key)
    {
        if (key == "ProjectionDays") ProjectionShifts--;
        else if (key == "ProjectionExtraHours") ProjectionExtraHours--;
    }

    // Modality chart mode: "RVU" or "Studies"
    [ObservableProperty]
    private string _modalityChartMode = "RVU";

    // Legend text paint for white labels (smaller font)
    public SolidColorPaint LegendTextPaint { get; } = new SolidColorPaint(SKColors.White);

    // Legend text size (2 points smaller than default ~14)
    public double LegendTextSize { get; } = 11;

    // Tooltip text paint and size (4 points smaller, dark for light background)
    public SolidColorPaint TooltipTextPaint { get; } = new SolidColorPaint(new SKColor(30, 30, 30));
    public double TooltipTextSize { get; } = 10;

    // Consistent colors for modalities across shift changes
    private static readonly Dictionary<string, SKColor> ModalityColors = new()
    {
        { "CT", new SKColor(66, 133, 244) },    // Blue
        { "MR", new SKColor(52, 168, 83) },     // Green
        { "XR", new SKColor(251, 188, 4) },     // Yellow/Gold
        { "US", new SKColor(234, 67, 53) },     // Red
        { "NM", new SKColor(154, 75, 200) },    // Purple
        { "FL", new SKColor(255, 136, 0) },     // Orange
        { "IR", new SKColor(0, 188, 212) },     // Cyan
        { "DEXA", new SKColor(139, 195, 74) },  // Light Green
        { "PET", new SKColor(233, 30, 99) },    // Pink
        { "MAMMO", new SKColor(121, 85, 72) },  // Brown
        { "Unknown", new SKColor(158, 158, 158) } // Gray
    };

    private SKColor GetModalityColor(string modality)
    {
        if (ModalityColors.TryGetValue(modality.ToUpperInvariant(), out var color))
            return color;
        // Fallback: generate a consistent color from the modality name
        // Use deterministic hash (String.GetHashCode is non-deterministic across restarts in .NET Core)
        int hash = 5381;
        foreach (char c in modality)
            hash = ((hash << 5) + hash) + c;
        return new SKColor(
            (byte)((hash >> 16) & 0xFF | 0x40),
            (byte)((hash >> 8) & 0xFF | 0x40),
            (byte)(hash & 0xFF | 0x40)
        );
    }

    partial void OnModalityChartModeChanged(string value)
    {
        // Refresh charts when mode changes
        if (ViewMode == "summary")
            RefreshData();
    }

    // User preference for showing charts (toggled via checkbox)
    [ObservableProperty]
    private bool _chartEnabled = true;

    // Whether chart checkbox should be visible (hidden for comparison view)
    [ObservableProperty]
    private bool _showChartToggle = true;

    // Combined property: show chart only if both enabled by view AND user preference
    public bool IsChartVisible => (ShowPieChart || ShowCartesianChart) && ChartEnabled;

    // Combined property for summary side-by-side charts
    public bool IsSideChartsVisible => ShowSideCharts && ChartEnabled;

    partial void OnChartEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsChartVisible));
        OnPropertyChanged(nameof(IsSideChartsVisible));
    }

    partial void OnShowPieChartChanged(bool value)
    {
        OnPropertyChanged(nameof(IsChartVisible));
    }

    partial void OnShowCartesianChartChanged(bool value)
    {
        OnPropertyChanged(nameof(IsChartVisible));
    }

    partial void OnShowSideChartsChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSideChartsVisible));
    }

    // Dynamic column headers and visibility
    [ObservableProperty]
    private string _col1Header = "";

    [ObservableProperty]
    private string _col2Header = "Studies";

    [ObservableProperty]
    private string _col3Header = "RVU";

    [ObservableProperty]
    private string _col4Header = "Avg/Study";

    [ObservableProperty]
    private string _col5Header = "% RVU";

    [ObservableProperty]
    private bool _showCol2 = true;

    [ObservableProperty]
    private bool _showCol3 = true;

    [ObservableProperty]
    private bool _showCol4 = true;

    [ObservableProperty]
    private bool _showCol5 = true;

    // Custom Date Range
    [ObservableProperty]
    private DateTime _customStartDate = DateTime.Today.AddDays(-7);

    [ObservableProperty]
    private DateTime _customEndDate = DateTime.Today;

    partial void OnCustomStartDateChanged(DateTime value)
    {
        if (SelectedPeriod == "custom_range") RefreshData();
    }

    partial void OnCustomEndDateChanged(DateTime value)
    {
        if (SelectedPeriod == "custom_range") RefreshData();
    }

    // Period selection
    [ObservableProperty]
    private string _selectedPeriod = "all_time";

    [ObservableProperty]
    private ObservableCollection<Shift> _shifts = new();

    [ObservableProperty]
    private Shift? _selectedShift;

    // Comparison view - selecting two shifts
    [ObservableProperty]
    private Shift? _comparisonShift1;

    [ObservableProperty]
    private Shift? _comparisonShift2;

    // Pane widths (saved to settings)
    private double _leftPaneWidth;
    public double LeftPaneWidth
    {
        get => _leftPaneWidth;
        set
        {
            if (SetProperty(ref _leftPaneWidth, value))
            {
                _dataManager.Settings.StatisticsLeftPaneWidth = value;
                _dataManager.SaveSettings();
            }
        }
    }

    private double _chartsPaneWidth;
    public double ChartsPaneWidth
    {
        get => _chartsPaneWidth;
        set
        {
            if (SetProperty(ref _chartsPaneWidth, value))
            {
                _dataManager.Settings.StatisticsChartsPaneWidth = value;
                _dataManager.SaveSettings();
            }
        }
    }

    public StatisticsViewModel(DataManager dataManager)
    {
        _dataManager = dataManager;
        _trendService = new TrendAnalysisService(_dataManager.Database);
        _insightsService = new ProductivityInsightsService(_dataManager.Database);
        _tbwuLookup = new TbwuLookup();

        // Load pane widths from settings
        _leftPaneWidth = _dataManager.Settings.StatisticsLeftPaneWidth;
        _chartsPaneWidth = _dataManager.Settings.StatisticsChartsPaneWidth;

        LoadShifts();
        LoadRecords();

        // Default to current shift if exists, otherwise prior shift
        var currentShift = _dataManager.Database.GetCurrentShift();
        if (currentShift != null)
        {
            SelectedPeriod = "current_shift";
        }
        else if (Shifts.Any())
        {
            SelectedShift = Shifts.FirstOrDefault();
            SelectedPeriod = "selected_shift";
        }

        RefreshData();
    }

    private void LoadShifts()
    {
        Shifts.Clear();
        var dbShifts = _dataManager.Database.GetAllShifts();

        // Populate each shift with its study count and RVU
        foreach (var shift in dbShifts)
        {
            var records = _dataManager.Database.GetRecordsForShift(shift.Id);
            shift.TotalStudies = records.Count;
            shift.TotalRvu = records.Sum(r => r.Rvu);
            Shifts.Add(shift);
        }

        // Add current shift if exists and not already in the list
        var current = _dataManager.Database.GetCurrentShift();
        if (current != null && !Shifts.Any(s => s.Id == current.Id))
        {
            var currentRecords = _dataManager.Database.GetRecordsForShift(current.Id);
            current.TotalStudies = currentRecords.Count;
            current.TotalRvu = currentRecords.Sum(r => r.Rvu);
            Shifts.Insert(0, current);
        }

        ShiftCount = Shifts.Count;
    }

    private void LoadRecords()
    {
        _allRecords = _dataManager.Database.GetAllRecords();
    }

    private List<StudyRecord> GetRecordsForPeriod()
    {
        switch (SelectedPeriod)
        {
            case "current_shift":
                var currentShift = _dataManager.Database.GetCurrentShift();
                if (currentShift != null)
                {
                    PeriodDescription = $"Current Shift ({currentShift.ShiftStart:M/d h:mm tt})";
                    return _dataManager.Database.GetRecordsForShift(currentShift.Id);
                }
                PeriodDescription = "No Active Shift";
                return new List<StudyRecord>();

            case "prior_shift":
                var priorShift = Shifts.FirstOrDefault(s => !s.IsActive); // Assumes Shifts is ordered desc
                if (priorShift != null)
                {
                    PeriodDescription = $"Prior Shift ({priorShift.DisplayLabel})";
                     return _dataManager.Database.GetRecordsForShift(priorShift.Id);
                }
                PeriodDescription = "No Prior Shift Found";
                return new List<StudyRecord>();

            case "today":
                var today = DateTime.Today;
                PeriodDescription = $"Today ({today:M/d/yyyy})";
                return _allRecords.Where(r => r.Timestamp.Date == today).ToList();

            case "this_week":
                var weekStart = DateTime.Today.AddDays(-(int)DateTime.Today.DayOfWeek);
                PeriodDescription = $"This Week ({weekStart:M/d} - {weekStart.AddDays(6):M/d})";
                return _allRecords.Where(r => r.Timestamp >= weekStart).ToList();

            case "this_work_week":
                // Work week = Monday to Friday
                var daysSinceMonday = ((int)DateTime.Today.DayOfWeek + 6) % 7; // Monday = 0
                var workWeekStart = DateTime.Today.AddDays(-daysSinceMonday);
                var workWeekEnd = workWeekStart.AddDays(4); // Friday
                PeriodDescription = $"This Work Week ({workWeekStart:M/d} - {workWeekEnd:M/d})";
                return _allRecords.Where(r => r.Timestamp >= workWeekStart && r.Timestamp < workWeekEnd.AddDays(1)).ToList();

            case "last_work_week":
                var lastDaysSinceMonday = ((int)DateTime.Today.DayOfWeek + 6) % 7;
                var lastWorkWeekStart = DateTime.Today.AddDays(-lastDaysSinceMonday - 7);
                var lastWorkWeekEnd = lastWorkWeekStart.AddDays(4);
                PeriodDescription = $"Last Work Week ({lastWorkWeekStart:M/d} - {lastWorkWeekEnd:M/d})";
                return _allRecords.Where(r => r.Timestamp >= lastWorkWeekStart && r.Timestamp < lastWorkWeekEnd.AddDays(1)).ToList();

            case "this_month":
                var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                PeriodDescription = $"This Month ({monthStart:MMMM yyyy})";
                return _allRecords.Where(r => r.Timestamp >= monthStart).ToList();

            case "last_month":
                var lastMonthEnd = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddDays(-1);
                var lastMonthStart = new DateTime(lastMonthEnd.Year, lastMonthEnd.Month, 1);
                PeriodDescription = $"Last Month ({lastMonthStart:MMMM yyyy})";
                return _allRecords.Where(r => r.Timestamp >= lastMonthStart && r.Timestamp <= lastMonthEnd).ToList();

            case "last_3_months":
                var threeMonthsAgo = DateTime.Today.AddMonths(-3);
                PeriodDescription = $"Last 3 Months ({threeMonthsAgo:M/d/yy} - {DateTime.Today:M/d/yy})";
                return _allRecords.Where(r => r.Timestamp >= threeMonthsAgo).ToList();

            case "last_year":
                var oneYearAgo = DateTime.Today.AddYears(-1);
                PeriodDescription = $"Last Year ({oneYearAgo:M/d/yy} - {DateTime.Today:M/d/yy})";
                return _allRecords.Where(r => r.Timestamp >= oneYearAgo).ToList();

            case "selected_shift":
                if (SelectedShift != null)
                {
                    PeriodDescription = $"Shift: {SelectedShift.DisplayLabel}";
                    return _dataManager.Database.GetRecordsForShift(SelectedShift.Id);
                }
                return new List<StudyRecord>();

            case "custom_range":
                PeriodDescription = $"Custom: {CustomStartDate:M/d/yyyy} - {CustomEndDate:M/d/yyyy}";
                return _dataManager.Database.GetRecordsInDateRange(CustomStartDate, CustomEndDate.AddDays(1).AddTicks(-1));

            default: // all_time
                PeriodDescription = "All Time";
                return _allRecords;
        }
        }


    private List<StudyRecord> ExpandMultiAccessionRecords(List<StudyRecord> records)
    {
        var expanded = new List<StudyRecord>();
        var seenMultiGroups = new HashSet<string>();

        foreach (var r in records)
        {
            // Format 1: Legacy combined record with "Multiple ..." prefix
            if (r.StudyType.StartsWith("Multiple ") && r.AccessionCount > 1 && !r.FromMultiAccession)
            {
                var modality = r.StudyType.Replace("Multiple ", "").Trim();
                var rvuPer = r.AccessionCount > 0 ? r.Rvu / r.AccessionCount : r.Rvu;

                for (int i = 0; i < r.AccessionCount; i++)
                {
                    var clone = r.Clone();
                    clone.StudyType = modality;
                    clone.Rvu = rvuPer;
                    clone.Procedure = $"{r.Procedure} ({i + 1}/{r.AccessionCount})";
                    expanded.Add(clone);
                }
            }
            // Format 2: Individual records from multi-accession group (current format)
            // These are already expanded - just add them directly
            else if (r.FromMultiAccession)
            {
                // Track the group to avoid any potential duplicates
                if (!string.IsNullOrEmpty(r.MultiAccessionGroup))
                    seenMultiGroups.Add(r.MultiAccessionGroup);
                expanded.Add(r);
            }
            // Format 3: Regular single-accession record
            else
            {
                expanded.Add(r);
            }
        }
        return expanded;
    }

    [RelayCommand]
    public void RefreshData()
    {
        var rawRecords = GetRecordsForPeriod();
        var records = ExpandMultiAccessionRecords(rawRecords);

        // Update summary stats
        TotalStudies = records.Count;
        TotalRvu = records.Sum(r => r.Rvu);
        AvgRvuPerStudy = TotalStudies > 0 ? TotalRvu / TotalStudies : 0;

        SummaryText = $"Total: {TotalStudies} studies  |  {TotalRvu:F1} RVU  |  Avg: {AvgRvuPerStudy:F2} RVU/study";

        // Reset chart visibility (individual Display methods may override)
        ShowCartesianChart = false;
        ShowPieChart = false;
        ShowSideCharts = false;
        ShowModalityToggle = false;
        ShowSecondChart = false;
        ShowSideBarChart = false;
        SideChartTitle = "Modality Breakdown";
        SecondChartTitle = "Avg Time to Read";
        ShowChartToggle = true;  // Show toggle by default (comparison view will hide it)
        IsAllStudiesView = false;  // Reset - only DisplayAllStudies sets this to true
        IsTrendsView = false;
        IsHeatmapView = false;
        IsInsightsView = false;

        // Display based on view mode
        switch (ViewMode)
        {
            case "by_hour":
                DisplayByHour(records);
                break;
            case "by_modality":
                DisplayByModality(records);
                break;
            case "by_study_type":
                DisplayByStudyType(records);
                break;
            case "by_patient_class":
                DisplayByPatientClass(records);
                break;
            case "by_body_part":
                DisplayByBodyPart(records);
                break;
            case "all_studies":
                DisplayAllStudies(records);
                break;
            case "projection":
                DisplayProjection(records);
                break;
            case "compensation":
                DisplayCompensation(records);
                break;
            case "comparison":
                DisplayComparison();
                break;
            case "trends":
                DisplayTrends();
                break;
            case "heatmap":
                DisplayHeatmap();
                break;
            case "insights":
                DisplayInsights();
                break;
            default:
                DisplaySummary(records);
                break;
        }
    }

    private void DisplaySummary(List<StudyRecord> records)
    {
        TableColumns.Clear();
        TableColumns.Add("Metric");
        TableColumns.Add("Value");

        // Summary view: use side-by-side charts (not above-table)
        ShowCartesianChart = false;
        ShowPieChart = false;
        ShowSideCharts = true;
        ShowModalityToggle = true;  // Show RVU/Studies toggle
        ShowSecondChart = true;     // Show second chart (avg time)
        SideChartTitle = "Modality Breakdown";
        SecondChartTitle = "Avg Time to Read";

        // Summary view uses 2 columns: Metric | Value (hide others)
        Col1Header = "Metric";
        Col2Header = "Value";
        ShowCol2 = true;
        ShowCol3 = false;
        ShowCol4 = false;
        ShowCol5 = false;

        TableData.Clear();

        // Helper to format hour
        string FormatHour(int h) => $"{(h % 12 == 0 ? 12 : h % 12)}{(h < 12 ? "am" : "pm")}";

        // Helper to format duration
        string FormatDuration(double seconds)
        {
            if (seconds < 60) return $"{(int)seconds}s";
            var mins = (int)(seconds / 60);
            var secs = (int)(seconds % 60);
            return secs > 0 ? $"{mins}m {secs}s" : $"{mins}m";
        }

        // ============ BASIC STATS ============
        TableData.Add(new StatRow { Col1 = "Total Studies", Col2 = TotalStudies.ToString() });
        TableData.Add(new StatRow { Col1 = "Total RVU", Col2 = $"{TotalRvu:F1}" });
        TableData.Add(new StatRow { Col1 = "Average RVU per Study", Col2 = $"{AvgRvuPerStudy:F2}" });

        if (TotalStudies > 0)
        {
            var role = _dataManager.Settings.Role ?? "Partner";
            // Use TimeFinished for time span and compensation (like Python)
            var finishTimes = records.Select(r => r.TimeFinished ?? r.Timestamp).ToList();
            var timeSpan = finishTimes.Max() - finishTimes.Min();
            var hoursSpan = Math.Max(timeSpan.TotalHours, 1);
            // Calculate compensation using TimeFinished for rate lookup
            var totalComp = records.Sum(r => Core.CompensationRates.CalculateCompensation(r.Rvu, r.TimeFinished ?? r.Timestamp, role));
            var compPerHour = totalComp / hoursSpan;

            // Hourly Compensation Rate
            TableData.Add(new StatRow { Col1 = "Hourly Compensation Rate", Col2 = $"${compPerHour:N2}/hr" });

            // TBWU equivalent
            if (_tbwuLookup.IsAvailable)
            {
                var totalTbwuComp = _tbwuLookup.CalculateTotalTbwuCompensation(records, role);
                var tbwuCompPerHour = totalTbwuComp / hoursSpan;
                TableData.Add(new StatRow { Col1 = "Hourly Compensation Rate (TBWU)", Col2 = $"${tbwuCompPerHour:N2}/hr" });
            }

            // ============ COMPENSATION ============
            TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
            TableData.Add(new StatRow { Col1 = "Total Compensation", Col2 = $"${totalComp:N0}" });
            if (_tbwuLookup.IsAvailable)
            {
                var summaryTbwuComp = _tbwuLookup.CalculateTotalTbwuCompensation(records, role);
                TableData.Add(new StatRow { Col1 = "Total Compensation (TBWU)", Col2 = $"${summaryTbwuComp:N0}" });
            }

            // Projected Monthly Income (uses current month data)
            {
                var currentMonthName = DateTime.Now.ToString("MMMM");
                var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                var monthEnd = monthStart.AddMonths(1);
                var currentMonthRecords = _allRecords.Where(r => r.Timestamp >= monthStart && r.Timestamp < monthEnd).ToList();

                var actualMonthComp = CompensationRates.CalculateTotalCompensation(currentMonthRecords, role);
                var actualMonthRvu = currentMonthRecords.Sum(r => r.Rvu);
                var shiftLength = _dataManager.Settings.ShiftLengthHours;
                var actualMonthHours = 0.0;
                if (currentMonthRecords.Any())
                {
                    var monthShiftGroups = currentMonthRecords.GroupBy(r => r.ShiftId);
                    foreach (var shift in monthShiftGroups)
                    {
                        var shiftDuration = (shift.Max(r => r.Timestamp) - shift.Min(r => r.Timestamp)).TotalHours;
                        shiftDuration = Math.Max(shiftDuration, 1);
                        if (Math.Abs(shiftDuration - shiftLength) <= 0.5)
                            shiftDuration = shiftLength;
                        actualMonthHours += shiftDuration;
                    }
                }

                CheckAndResetMonthlyProjections();
                var targetShifts = _dataManager.Settings.ProjectionDays;
                var extraHours = _dataManager.Settings.ProjectionExtraHours;
                var targetMonthlyHours = (targetShifts * shiftLength) + extraHours;
                var remainingHours = Math.Max(0, targetMonthlyHours - actualMonthHours);

                var projCompPerHour = actualMonthHours > 0 ? actualMonthComp / actualMonthHours : (hoursSpan > 0 ? totalComp / hoursSpan : 0);
                var projectedMonthlyComp = actualMonthComp + (projCompPerHour * remainingHours);

                TableData.Add(new StatRow { Col1 = $"Projected {currentMonthName} Income", Col2 = $"${projectedMonthlyComp:N0}" });
            }

            // ============ TIME/EFFICIENCY ============
            TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
            TableData.Add(new StatRow { Col1 = "Time Span", Col2 = $"{hoursSpan:F1} hours" });
            TableData.Add(new StatRow { Col1 = "Studies per Hour", Col2 = $"{TotalStudies / hoursSpan:F1}" });
            TableData.Add(new StatRow { Col1 = "RVU per Hour", Col2 = $"{TotalRvu / hoursSpan:F1}" });

            // ============ SHIFT STATS (based on selected period's records) ============
            // Group records by shift to get shift stats for just the selected period
            var shiftGroups = records.GroupBy(r => r.ShiftId).ToList();
            if (shiftGroups.Count > 1)
            {
                TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
                TableData.Add(new StatRow { Col1 = "Total Shifts Completed", Col2 = shiftGroups.Count.ToString() });

                // Find highest RVU shift and most efficient shift from the records in this period
                var shiftStats = shiftGroups.Select(g => new
                {
                    ShiftId = g.Key,
                    TotalRvu = g.Sum(r => r.Rvu),
                    StartTime = g.Min(r => r.Timestamp),
                    EndTime = g.Max(r => r.Timestamp),
                    Duration = (g.Max(r => r.Timestamp) - g.Min(r => r.Timestamp)).TotalHours
                }).ToList();

                var highestRvuShift = shiftStats.OrderByDescending(s => s.TotalRvu).FirstOrDefault();
                if (highestRvuShift != null)
                    TableData.Add(new StatRow { Col1 = "Highest RVU Shift", Col2 = $"{highestRvuShift.StartTime:MM/dd/yyyy}, {highestRvuShift.TotalRvu:F1} RVU" });

                // Most efficient = highest RVU/hour
                var mostEfficientShift = shiftStats
                    .Select(s => new { s.StartTime, s.TotalRvu, RvuPerHour = s.TotalRvu / Math.Max(s.Duration, 1) })
                    .OrderByDescending(x => x.RvuPerHour)
                    .FirstOrDefault();
                if (mostEfficientShift != null)
                    TableData.Add(new StatRow { Col1 = "Most Efficient Shift", Col2 = $"{mostEfficientShift.StartTime:MM/dd/yyyy}, {mostEfficientShift.RvuPerHour:F1} RVU/hr" });
            }

            // ============ HOURLY ANALYSIS ============
            TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
            // Use TimeFinished for hour groupings (like Python), fallback to Timestamp
            var hourGroups = records
                .Where(r => r.TimeFinished.HasValue || r.Timestamp != default)
                .GroupBy(r => (r.TimeFinished ?? r.Timestamp).Hour)
                .ToList();
            if (hourGroups.Any())
            {
                var busiestHour = hourGroups.OrderByDescending(g => g.Count()).FirstOrDefault();
                var mostProductiveHour = hourGroups.OrderByDescending(g => g.Sum(r => r.Rvu)).FirstOrDefault();

                if (busiestHour != null)
                    TableData.Add(new StatRow { Col1 = "Busiest Hour", Col2 = $"{FormatHour(busiestHour.Key)} ({busiestHour.Count()} studies)" });
                if (mostProductiveHour != null)
                    TableData.Add(new StatRow { Col1 = "Most Productive Hour", Col2 = $"{FormatHour(mostProductiveHour.Key)} ({mostProductiveHour.Sum(r => r.Rvu):F1} RVU)" });

                // Fastest hour (lowest avg time per study)
                var recordsWithDuration = records.Where(r => r.DurationSeconds > 0).ToList();
                if (recordsWithDuration.Any())
                {
                    var hourGroupsWithDuration = recordsWithDuration
                        .GroupBy(r => (r.TimeFinished ?? r.Timestamp).Hour)
                        .Where(g => g.Count() >= 3) // Need at least 3 studies to be meaningful
                        .Select(g => new { Hour = g.Key, AvgTime = g.Average(r => r.DurationSeconds), Count = g.Count() })
                        .ToList();

                    if (hourGroupsWithDuration.Any())
                    {
                        var fastestHour = hourGroupsWithDuration.OrderBy(h => h.AvgTime).FirstOrDefault();
                        if (fastestHour != null)
                            TableData.Add(new StatRow { Col1 = "Fastest Hour", Col2 = $"{FormatHour(fastestHour.Hour)} ({FormatDuration(fastestHour.AvgTime ?? 0)} avg, {fastestHour.Count} studies)" });
                    }
                }
            }

            // ============ MODALITY BREAKDOWN ============
            TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
            var modalities = records.GroupBy(r => ExtractModality(r.StudyType))
                .OrderByDescending(g => g.Count())
                .ToList();

            if (modalities.Any())
            {
                var topModality = modalities.FirstOrDefault();
                if (topModality != null)
                    TableData.Add(new StatRow { Col1 = "Top Modality", Col2 = $"{topModality.Key} ({topModality.Count()} studies)" });

                TableData.Add(new StatRow { Col1 = "Modality Breakdown", Col2 = "" });
                foreach (var group in modalities)
                {
                    var pct = (group.Count() / (double)TotalStudies) * 100;
                    TableData.Add(new StatRow { Col1 = $"  {group.Key}", Col2 = $"{pct:F1}% ({group.Count()} studies)" });
                }
            }

            // ============ AVERAGE TIME TO READ ============
            var allWithDuration = records.Where(r => r.DurationSeconds > 0).ToList();
            if (allWithDuration.Any())
            {
                TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
                var avgTimeOverall = allWithDuration.Average(r => r.DurationSeconds) ?? 0;
                TableData.Add(new StatRow { Col1 = "Average Time to Read", Col2 = FormatDuration(avgTimeOverall) });

                TableData.Add(new StatRow { Col1 = "Average Time to Read by Modality", Col2 = "" });
                var modalityTimes = allWithDuration.GroupBy(r => ExtractModality(r.StudyType))
                    .OrderByDescending(g => g.Count())
                    .ToList();

                foreach (var group in modalityTimes)
                {
                    var avgTime = group.Average(r => r.DurationSeconds) ?? 0;
                    TableData.Add(new StatRow { Col1 = $"  {group.Key}", Col2 = $"{FormatDuration(avgTime)} ({group.Count()} studies)" });
                }
            }
        }

        // Populate pie charts for summary view (shown when ChartEnabled is true)
        // Chart 1: Modality breakdown - switchable between RVU and Studies
        if (TotalStudies > 0)
        {
            var modalityForChart = records.GroupBy(r => ExtractModality(r.StudyType))
                .Select(g => new { Modality = g.Key, Rvu = Math.Round(g.Sum(r => r.Rvu), 1), Count = g.Count() })
                .OrderByDescending(m => ModalityChartMode == "RVU" ? m.Rvu : m.Count)
                .Take(6)
                .ToList();

            var modalitySeriesList = new List<ISeries>();
            foreach (var item in modalityForChart)
            {
                var color = GetModalityColor(item.Modality);
                // Show both values, order depends on selected mode
                string legendName = ModalityChartMode == "RVU"
                    ? $"{item.Modality}: {item.Rvu:F1} RVU ({item.Count})"
                    : $"{item.Modality}: {item.Count} ({item.Rvu:F1} RVU)";

                double chartValue = ModalityChartMode == "RVU" ? item.Rvu : item.Count;

                modalitySeriesList.Add(new PieSeries<double>
                {
                    Values = new double[] { chartValue },
                    Name = legendName,
                    DataLabelsSize = 0,
                    Fill = new SolidColorPaint(color),
                    ToolTipLabelFormatter = _ => string.Empty  // Name already shows in tooltip
                });
            }
            PieSeries = modalitySeriesList.ToArray();

            // Chart 2: Average time to read by modality
            var recordsWithDuration = records.Where(r => r.DurationSeconds > 0).ToList();
            if (recordsWithDuration.Any())
            {
                var timeByModality = recordsWithDuration
                    .GroupBy(r => ExtractModality(r.StudyType))
                    .Select(g => new {
                        Modality = g.Key,
                        AvgTime = Math.Round(g.Average(r => r.DurationSeconds) ?? 0),
                        Count = g.Count()
                    })
                    .Where(m => m.Count >= 2)  // Only include modalities with enough data
                    .OrderByDescending(m => m.AvgTime)
                    .Take(6)
                    .ToList();

                var timeSeriesList = new List<ISeries>();
                foreach (var item in timeByModality)
                {
                    var avgSeconds = item.AvgTime;
                    string timeStr = avgSeconds < 60
                        ? $"{(int)avgSeconds}s"
                        : $"{(int)(avgSeconds / 60)}m {(int)(avgSeconds % 60)}s";
                    var color = GetModalityColor(item.Modality);
                    var timeLegendName = $"{item.Modality}: {timeStr}";

                    timeSeriesList.Add(new PieSeries<double>
                    {
                        Values = new double[] { avgSeconds },
                        Name = timeLegendName,
                        DataLabelsSize = 0,
                        Fill = new SolidColorPaint(color),
                        ToolTipLabelFormatter = _ => string.Empty  // Name already shows in tooltip
                    });
                }
                TimePieSeries = timeSeriesList.ToArray();
            }
            else
            {
                TimePieSeries = Array.Empty<ISeries>();
            }
        }
        else
        {
            PieSeries = Array.Empty<ISeries>();
            TimePieSeries = Array.Empty<ISeries>();
        }
    }

    private void DisplayByHour(List<StudyRecord> records)
    {
        // Use side panel for chart like Summary/Compensation
        ShowCartesianChart = false;
        ShowPieChart = false;
        ShowSideCharts = true;
        ShowSideBarChart = true;
        ShowSecondChart = false;
        ShowModalityToggle = false;
        SideChartTitle = "RVU by Hour";

        Col1Header = "Metric";
        Col2Header = "Value";
        ShowCol2 = true;
        ShowCol3 = false;
        ShowCol4 = false;
        ShowCol5 = false;

        TableData.Clear();
        BarChartLegendItems.Clear();

        if (!records.Any())
        {
            TableData.Add(new StatRow { Col1 = "No data for selected period", Col2 = "" });
            SideBarSeries = Array.Empty<ISeries>();
            SideBarSections = Array.Empty<RectangularSection>();
            return;
        }

        var role = _dataManager.Settings.Role;

        // Group by hour
        var hourData = new Dictionary<int, (int studies, double rvu, double comp)>();
        foreach (var record in records)
        {
            var hour = (record.TimeFinished ?? record.Timestamp).Hour;
            if (!hourData.ContainsKey(hour))
                hourData[hour] = (0, 0, 0);

            var current = hourData[hour];
            var comp = CompensationRates.CalculateCompensation(record.Rvu, record.TimeFinished ?? record.Timestamp, role);
            hourData[hour] = (current.studies + 1, current.rvu + record.Rvu, current.comp + comp);
        }

        // Sort hours starting from shift start
        int? startHour = records.Min(r => r.Timestamp).Hour;
        var sortedHours = new List<int>();
        for (int offset = 0; offset < 24; offset++)
        {
            int hour = (startHour.Value + offset) % 24;
            if (hourData.ContainsKey(hour))
                sortedHours.Add(hour);
        }

        var totalComp = CompensationRates.CalculateTotalCompensation(records, role);
        var totalTbwuComp = _tbwuLookup.IsAvailable ? _tbwuLookup.CalculateTotalTbwuCompensation(records, role) : 0.0;

        // Build per-hour TBWU comp data
        var hourTbwuComp = new Dictionary<int, double>();
        if (_tbwuLookup.IsAvailable)
        {
            foreach (var record in records)
            {
                var hour = (record.TimeFinished ?? record.Timestamp).Hour;
                if (!hourTbwuComp.ContainsKey(hour))
                    hourTbwuComp[hour] = 0;
                hourTbwuComp[hour] += _tbwuLookup.CalculateTbwuCompensation(record, role);
            }
        }

        // ============ SUMMARY ============
        TableData.Add(new StatRow { Col1 = "SUMMARY", Col2 = "", IsHeader = true });
        TableData.Add(new StatRow { Col1 = "Total Studies", Col2 = TotalStudies.ToString() });
        TableData.Add(new StatRow { Col1 = "Total RVU", Col2 = $"{TotalRvu:F1}" });
        TableData.Add(new StatRow { Col1 = "Total Compensation", Col2 = $"${totalComp:N0}" });
        if (_tbwuLookup.IsAvailable)
            TableData.Add(new StatRow { Col1 = "Total Compensation (TBWU)", Col2 = $"${totalTbwuComp:N0}" });

        if (hourData.Any())
        {
            var peakHour = hourData.OrderByDescending(h => h.Value.rvu).FirstOrDefault();
            if (peakHour.Value != default)
            {
                var peakHour12 = peakHour.Key % 12 == 0 ? 12 : peakHour.Key % 12;
                TableData.Add(new StatRow { Col1 = "Peak Hour", Col2 = $"{peakHour12}{(peakHour.Key < 12 ? "AM" : "PM")} ({peakHour.Value.rvu:F1} RVU)" });
            }
        }

        // ============ HOURLY BREAKDOWN ============
        TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
        TableData.Add(new StatRow { Col1 = "HOURLY BREAKDOWN", Col2 = "", IsHeader = true });

        foreach (var hour in sortedHours)
        {
            var data = hourData[hour];
            int hour12 = hour % 12 == 0 ? 12 : hour % 12;
            string amPm = hour < 12 ? "AM" : "PM";
            var compStr = $"{data.studies} studies, {data.rvu:F1} RVU, ${data.comp:N0}";
            if (_tbwuLookup.IsAvailable && hourTbwuComp.TryGetValue(hour, out var tbwuC))
                compStr += $" (TBWU: ${tbwuC:N0})";
            TableData.Add(new StatRow
            {
                Col1 = $"  {hour12}{amPm}",
                Col2 = compStr
            });
        }

        // ============ TOTALS ============
        TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
        var totalStr = $"{TotalStudies} studies, {TotalRvu:F1} RVU, ${totalComp:N0}";
        if (_tbwuLookup.IsAvailable)
            totalStr += $" (TBWU: ${totalTbwuComp:N0})";
        TableData.Add(new StatRow { Col1 = "TOTAL", Col2 = totalStr, IsTotal = true });

        // Build horizontal bar chart for side panel
        if (sortedHours.Any())
        {
            var maxRvu = sortedHours.Max(h => hourData[h].rvu);
            var avgRvu = sortedHours.Average(h => hourData[h].rvu);

            // Reverse so shift start (first hour) is at TOP of chart (RowSeries renders index 0 at bottom)
            var displayHours = new List<int>(sortedHours);
            displayHours.Reverse();

            var labels = displayHours.Select(h =>
            {
                int h12 = h % 12 == 0 ? 12 : h % 12;
                return $"{h12}{(h < 12 ? "AM" : "PM")}";
            }).ToArray();

            var values = displayHours.Select(h => hourData[h].rvu).ToArray();

            // Build study count lookup for data labels
            var studyCounts = displayHours.Select(h => hourData[h].studies).ToArray();
            var peakIdx = Array.IndexOf(values, values.Max());

            // Gradient fill: steel blue (bottom/low) -> cornflower -> bright cyan (top/high)
            var barSeries = new RowSeries<double>
            {
                Values = values,
                Padding = 2,
                MaxBarWidth = 500,
                DataLabelsPaint = new SolidColorPaint(SKColors.White),
                DataLabelsSize = 11,
                DataLabelsPosition = DataLabelsPosition.End,
                DataLabelsFormatter = point =>
                {
                    var idx = point.Index;
                    var count = idx >= 0 && idx < studyCounts.Length ? studyCounts[idx] : 0;
                    return $"{point.Model:F1}  ({count})";
                },
                Fill = new LinearGradientPaint(
                    new[] { new SKColor(70, 130, 180), new SKColor(100, 149, 237), new SKColor(255, 195, 60) },
                    new SKPoint(0, 1), new SKPoint(0, 0)),
                Stroke = new SolidColorPaint(new SKColor(255, 255, 255, 40)) { StrokeThickness = 0.5f }
            };

            SideBarSeries = new ISeries[] { barSeries };
            SideBarYAxes = new Axis[]
            {
                new Axis
                {
                    Labels = labels,
                    LabelsPaint = new SolidColorPaint(SKColors.White),
                    TextSize = 12,
                    SeparatorsPaint = new SolidColorPaint(new SKColor(80, 80, 80)) { StrokeThickness = 0.5f },
                    ShowSeparatorLines = true
                }
            };
            SideBarXAxes = new Axis[]
            {
                new Axis
                {
                    LabelsPaint = new SolidColorPaint(new SKColor(200, 200, 200)),
                    TextSize = 10,
                    MinLimit = 0,
                    SeparatorsPaint = new SolidColorPaint(new SKColor(80, 80, 80)) { StrokeThickness = 0.5f },
                    ShowSeparatorLines = true
                }
            };

            // Average RVU dashed line
            SideBarSections = new RectangularSection[]
            {
                new RectangularSection
                {
                    Xi = avgRvu,
                    Xj = avgRvu,
                    Stroke = new SolidColorPaint(new SKColor(255, 255, 255, 100)) { StrokeThickness = 1.5f, PathEffect = new DashEffect(new float[] { 6, 4 }) }
                }
            };
        }
        else
        {
            SideBarSections = Array.Empty<RectangularSection>();
        }
    }

    private void DisplayByModality(List<StudyRecord> records)
    {
        // Use side panel for pie chart
        ShowCartesianChart = false;
        ShowPieChart = false;
        ShowSideCharts = true;
        ShowSideBarChart = false;
        ShowSecondChart = false;
        ShowModalityToggle = false;
        SideChartTitle = "RVU by Modality";

        Col1Header = "Metric";
        Col2Header = "Value";
        ShowCol2 = true;
        ShowCol3 = false;
        ShowCol4 = false;
        ShowCol5 = false;

        TableData.Clear();

        if (!records.Any())
        {
            TableData.Add(new StatRow { Col1 = "No data for selected period", Col2 = "" });
            PieSeries = Array.Empty<ISeries>();
            return;
        }

        var role = _dataManager.Settings.Role;

        // Group by modality with compensation
        var modalityData = records.GroupBy(r => ExtractModality(r.StudyType))
            .Select(g => new
            {
                Modality = g.Key,
                Studies = g.Count(),
                Rvu = g.Sum(r => r.Rvu),
                Comp = CompensationRates.CalculateTotalCompensation(g, role),
                AvgTime = g.Where(r => r.DurationSeconds > 0).Any()
                    ? g.Where(r => r.DurationSeconds > 0).Average(r => r.DurationSeconds) : null
            })
            .OrderByDescending(m => m.Rvu)
            .ToList();

        var totalComp = modalityData.Sum(m => m.Comp);

        // ============ SUMMARY ============
        TableData.Add(new StatRow { Col1 = "SUMMARY", Col2 = "", IsHeader = true });
        TableData.Add(new StatRow { Col1 = "Total Studies", Col2 = TotalStudies.ToString() });
        TableData.Add(new StatRow { Col1 = "Total RVU", Col2 = $"{TotalRvu:F1}" });
        TableData.Add(new StatRow { Col1 = "Total Compensation", Col2 = $"${totalComp:N0}" });
        if (_tbwuLookup.IsAvailable)
        {
            var totalTbwuComp = _tbwuLookup.CalculateTotalTbwuCompensation(records, role);
            TableData.Add(new StatRow { Col1 = "Total Compensation (TBWU)", Col2 = $"${totalTbwuComp:N0}" });
        }
        TableData.Add(new StatRow { Col1 = "Modalities", Col2 = modalityData.Count.ToString() });

        // ============ BY MODALITY ============
        TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
        TableData.Add(new StatRow { Col1 = "BY MODALITY", Col2 = "", IsHeader = true });

        foreach (var m in modalityData)
        {
            var pctRvu = TotalRvu > 0 ? (m.Rvu / TotalRvu * 100) : 0;
            var compStr = $"{m.Studies} studies, {m.Rvu:F1} RVU ({pctRvu:F0}%), ${m.Comp:N0}";
            if (_tbwuLookup.IsAvailable)
            {
                // Get records for this modality group to calculate TBWU comp
                var modalityRecords = records.Where(r => ExtractModality(r.StudyType) == m.Modality);
                var tbwuComp = _tbwuLookup.CalculateTotalTbwuCompensation(modalityRecords, role);
                compStr += $" (TBWU: ${tbwuComp:N0})";
            }
            TableData.Add(new StatRow
            {
                Col1 = $"  {m.Modality}",
                Col2 = compStr
            });
        }

        // ============ AVERAGE TIME BY MODALITY ============
        var modalitiesWithTime = modalityData.Where(m => m.AvgTime.HasValue).ToList();
        if (modalitiesWithTime.Any())
        {
            TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
            TableData.Add(new StatRow { Col1 = "AVERAGE TIME TO READ", Col2 = "", IsHeader = true });

            foreach (var m in modalitiesWithTime.OrderByDescending(m => m.AvgTime))
            {
                var avgSec = m.AvgTime ?? 0;
                var timeStr = avgSec < 60 ? $"{(int)avgSec}s" : $"{(int)(avgSec / 60)}m {(int)(avgSec % 60)}s";
                TableData.Add(new StatRow { Col1 = $"  {m.Modality}", Col2 = timeStr });
            }
        }

        // ============ TOTALS ============
        TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
        var totalLineModality = $"{TotalStudies} studies, {TotalRvu:F1} RVU, ${totalComp:N0}";
        if (_tbwuLookup.IsAvailable)
        {
            var totalTbwuCompMod = _tbwuLookup.CalculateTotalTbwuCompensation(records, role);
            totalLineModality += $" (TBWU: ${totalTbwuCompMod:N0})";
        }
        TableData.Add(new StatRow { Col1 = "TOTAL", Col2 = totalLineModality, IsTotal = true });

        // Populate Pie Chart for side panel
        var seriesList = new List<ISeries>();
        foreach (var m in modalityData)
        {
            var color = GetModalityColor(m.Modality);
            seriesList.Add(new PieSeries<double>
            {
                Values = new double[] { m.Rvu },
                Name = $"{m.Modality}: {m.Rvu:F1}",
                Fill = new SolidColorPaint(color),
                DataLabelsSize = 0
            });
        }
        PieSeries = seriesList.ToArray();
    }

    private void DisplayByStudyType(List<StudyRecord> records)
    {
        // No chart for study type - data table only
        ShowCartesianChart = false;
        ShowPieChart = false;
        ShowSideCharts = false;

        Col1Header = "Metric";
        Col2Header = "Value";
        ShowCol2 = true;
        ShowCol3 = false;
        ShowCol4 = false;
        ShowCol5 = false;

        TableData.Clear();

        if (!records.Any())
        {
            TableData.Add(new StatRow { Col1 = "No data for selected period", Col2 = "" });
            return;
        }

        var role = _dataManager.Settings.Role;

        // Group by study type with compensation
        var typeData = records.GroupBy(r =>
            {
                var studyType = string.IsNullOrWhiteSpace(r.StudyType) ? "(Unknown)" : r.StudyType;
                if (studyType.StartsWith("Multiple "))
                {
                    var modality = studyType.Replace("Multiple ", "").Trim();
                    studyType = !string.IsNullOrEmpty(modality) ? $"{modality} Other" : "(Unknown)";
                }
                return studyType;
            })
            .Select(g => new
            {
                StudyType = g.Key,
                Studies = g.Count(),
                Rvu = g.Sum(r => r.Rvu),
                Comp = CompensationRates.CalculateTotalCompensation(g, role)
            })
            .OrderByDescending(t => t.Rvu)
            .ToList();

        var totalComp = typeData.Sum(t => t.Comp);
        var totalTbwuComp = _tbwuLookup.IsAvailable ? _tbwuLookup.CalculateTotalTbwuCompensation(records, role) : 0.0;

        // ============ SUMMARY ============
        TableData.Add(new StatRow { Col1 = "SUMMARY", Col2 = "", IsHeader = true });
        TableData.Add(new StatRow { Col1 = "Total Studies", Col2 = TotalStudies.ToString() });
        TableData.Add(new StatRow { Col1 = "Total RVU", Col2 = $"{TotalRvu:F1}" });
        TableData.Add(new StatRow { Col1 = "Total Compensation", Col2 = $"${totalComp:N0}" });
        if (_tbwuLookup.IsAvailable)
            TableData.Add(new StatRow { Col1 = "Total Compensation (TBWU)", Col2 = $"${totalTbwuComp:N0}" });
        TableData.Add(new StatRow { Col1 = "Unique Study Types", Col2 = typeData.Count.ToString() });

        // ============ TOP STUDY TYPES ============
        TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
        TableData.Add(new StatRow { Col1 = "BY STUDY TYPE", Col2 = "", IsHeader = true });

        foreach (var t in typeData)
        {
            var pctRvu = TotalRvu > 0 ? (t.Rvu / TotalRvu * 100) : 0;
            TableData.Add(new StatRow
            {
                Col1 = $"  {t.StudyType}",
                Col2 = $"{t.Studies} studies, {t.Rvu:F1} RVU ({pctRvu:F0}%), ${t.Comp:N0}"
            });
        }

        // ============ TOTALS ============
        TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
        var totalAvg = TotalStudies > 0 ? TotalRvu / TotalStudies : 0;
        var totalLineStudyType = $"{TotalStudies} studies, {TotalRvu:F1} RVU, ${totalComp:N0}";
        if (_tbwuLookup.IsAvailable)
            totalLineStudyType += $" (TBWU: ${totalTbwuComp:N0})";
        TableData.Add(new StatRow { Col1 = "TOTAL", Col2 = totalLineStudyType, IsTotal = true });
    }

    private void DisplayByPatientClass(List<StudyRecord> records)
    {
        // Use side panel for pie chart
        ShowCartesianChart = false;
        ShowPieChart = false;
        ShowSideCharts = true;
        ShowSideBarChart = false;
        ShowSecondChart = false;
        ShowModalityToggle = false;
        SideChartTitle = "RVU by Patient Class";

        Col1Header = "Metric";
        Col2Header = "Value";
        ShowCol2 = true;
        ShowCol3 = false;
        ShowCol4 = false;
        ShowCol5 = false;

        TableData.Clear();

        if (!records.Any())
        {
            TableData.Add(new StatRow { Col1 = "No data for selected period", Col2 = "" });
            PieSeries = Array.Empty<ISeries>();
            return;
        }

        var role = _dataManager.Settings.Role;

        // Group by patient class with compensation
        var classData = records.GroupBy(r => string.IsNullOrWhiteSpace(r.PatientClass) ? "(Unknown)" : r.PatientClass)
            .Select(g => new
            {
                PatientClass = g.Key,
                Studies = g.Count(),
                Rvu = g.Sum(r => r.Rvu),
                Comp = CompensationRates.CalculateTotalCompensation(g, role)
            })
            .OrderByDescending(c => c.Rvu)
            .ToList();

        var totalComp = classData.Sum(c => c.Comp);
        var totalTbwuComp = _tbwuLookup.IsAvailable ? _tbwuLookup.CalculateTotalTbwuCompensation(records, role) : 0.0;

        // ============ SUMMARY ============
        TableData.Add(new StatRow { Col1 = "SUMMARY", Col2 = "", IsHeader = true });
        TableData.Add(new StatRow { Col1 = "Total Studies", Col2 = TotalStudies.ToString() });
        TableData.Add(new StatRow { Col1 = "Total RVU", Col2 = $"{TotalRvu:F1}" });
        TableData.Add(new StatRow { Col1 = "Total Compensation", Col2 = $"${totalComp:N0}" });
        if (_tbwuLookup.IsAvailable)
            TableData.Add(new StatRow { Col1 = "Total Compensation (TBWU)", Col2 = $"${totalTbwuComp:N0}" });

        // ============ BY PATIENT CLASS ============
        TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
        TableData.Add(new StatRow { Col1 = "BY PATIENT CLASS", Col2 = "", IsHeader = true });

        foreach (var c in classData)
        {
            var pctRvu = TotalRvu > 0 ? (c.Rvu / TotalRvu * 100) : 0;
            var classCompStr = $"{c.Studies} studies, {c.Rvu:F1} RVU ({pctRvu:F0}%), ${c.Comp:N0}";
            if (_tbwuLookup.IsAvailable)
            {
                var classTbwuComp = _tbwuLookup.CalculateTotalTbwuCompensation(
                    records.Where(r => (string.IsNullOrWhiteSpace(r.PatientClass) ? "(Unknown)" : r.PatientClass) == c.PatientClass), role);
                classCompStr += $" (TBWU: ${classTbwuComp:N0})";
            }
            TableData.Add(new StatRow
            {
                Col1 = $"  {c.PatientClass}",
                Col2 = classCompStr
            });
        }

        // ============ BY LOCATION ============
        // Map patient classes to locations by checking for keywords (handles "STAT Emergency", "IP" etc.)
        string MapToLocation(string patientClass)
        {
            if (string.IsNullOrWhiteSpace(patientClass))
                return "Unknown";

            var normalized = patientClass.Trim().ToLowerInvariant();

            // Check for emergency keywords
            if (normalized.Contains("emergency") || normalized.Contains("er ") || normalized == "er" ||
                normalized.Contains("ed ") || normalized == "ed" || normalized.Contains("e.d."))
                return "Emergency";

            // Check for inpatient keywords (including pre-admit)
            if (normalized.Contains("inpatient") || normalized.Contains("ip ") || normalized == "ip" ||
                normalized.Contains("pre-admit") || normalized.Contains("preadmit") || normalized.Contains("pre admit") ||
                normalized.Contains("admit"))
                return "Inpatient";

            // Check for outpatient keywords
            if (normalized.Contains("outpatient") || normalized.Contains("op ") || normalized == "op" ||
                normalized.Contains("ambulatory"))
                return "Outpatient";

            // Unknown if nothing matches
            if (normalized.Contains("unknown") || normalized == "(unknown)")
                return "Unknown";

            return "Unknown";
        }

        var locationData = records.GroupBy(r => MapToLocation(string.IsNullOrWhiteSpace(r.PatientClass) ? "(Unknown)" : r.PatientClass))
            .Select(g => new
            {
                Location = g.Key,
                Studies = g.Count(),
                Rvu = g.Sum(r => r.Rvu),
                Comp = CompensationRates.CalculateTotalCompensation(g, role)
            })
            .OrderByDescending(l => l.Rvu)
            .ToList();

        TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
        TableData.Add(new StatRow { Col1 = "BY LOCATION", Col2 = "", IsHeader = true });

        foreach (var l in locationData)
        {
            var pctRvu = TotalRvu > 0 ? (l.Rvu / TotalRvu * 100) : 0;
            TableData.Add(new StatRow
            {
                Col1 = $"  {l.Location}",
                Col2 = $"{l.Studies} studies, {l.Rvu:F1} RVU ({pctRvu:F0}%), ${l.Comp:N0}"
            });
        }

        // ============ TOTALS ============
        TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
        var totalLinePatientClass = $"{TotalStudies} studies, {TotalRvu:F1} RVU, ${totalComp:N0}";
        if (_tbwuLookup.IsAvailable)
            totalLinePatientClass += $" (TBWU: ${totalTbwuComp:N0})";
        TableData.Add(new StatRow { Col1 = "TOTAL", Col2 = totalLinePatientClass, IsTotal = true });

        // Populate Pie Chart for side panel
        var classColors = new Dictionary<string, SKColor>
        {
            { "Inpatient", new SKColor(234, 67, 53) },
            { "Outpatient", new SKColor(66, 133, 244) },
            { "Emergency", new SKColor(251, 188, 4) },
            { "ED", new SKColor(251, 188, 4) },
            { "(Unknown)", new SKColor(158, 158, 158) }
        };

        var seriesList = new List<ISeries>();
        foreach (var c in classData)
        {
            var color = classColors.GetValueOrDefault(c.PatientClass, new SKColor(52, 168, 83));
            seriesList.Add(new PieSeries<double>
            {
                Values = new double[] { c.Rvu },
                Name = $"{c.PatientClass}: {c.Rvu:F1}",
                Fill = new SolidColorPaint(color),
                DataLabelsSize = 0
            });
        }
        PieSeries = seriesList.ToArray();
    }

    /// <summary>
    /// Extract modality from study type (e.g., "CT CAP" -> "CT")
    /// Matches Python logic in _display_by_modality
    /// </summary>
    private string ExtractModality(string studyType)
    {
        if (string.IsNullOrWhiteSpace(studyType))
            return "Unknown";

        var parts = studyType.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "Unknown";

        var first = parts[0].ToUpperInvariant();

        // Handle "Multiple XR" -> extract actual modality
        if (first == "MULTIPLE" && parts.Length > 1)
            first = parts[1].ToUpperInvariant();

        // Normalize modality names to standard abbreviations
        return first switch
        {
            "ULTRASOUND" => "US",
            "COMPUTED" => "CT",
            "MAGNETIC" => "MR",
            "X-RAY" or "XRAY" => "XR",
            "FLUOROSCOPY" or "FLUORO" => "FL",
            "NUCLEAR" => "NM",
            "MAMMOGRAPHY" or "MAMMOGRAM" => "MAMMO",
            _ => first
        };
    }

    partial void OnViewModeChanged(string value)
    {
        RefreshData();
    }

    partial void OnSelectedPeriodChanged(string value)
    {
        RefreshData();
    }

    partial void OnSelectedShiftChanged(Shift? value)
    {
        if (value != null)
        {
            SelectedPeriod = "selected_shift";
            // Always refresh - SelectedPeriod might already be "selected_shift"
            // if user clicks different shifts, so OnSelectedPeriodChanged won't fire
            RefreshData();
        }
    }
    [RelayCommand]
    public void DeleteShift(Shift shift)
    {
        if (shift == null) return;
        
        try 
        {
            _dataManager.Database.DeleteShift(shift.Id);
            Shifts.Remove(shift);
            if (SelectedShift == shift)
            {
                SelectedShift = null;
                SelectedPeriod = "all_time"; // Reset to all time or some default
            }
            RefreshData();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete shift {Id}", shift.Id);
        }
    }

    private void DisplayProjection(List<StudyRecord> records)
    {
        // No chart for projection - data table only
        ShowCartesianChart = false;
        ShowPieChart = false;
        ShowSideCharts = false;

        Col1Header = "Metric";
        Col2Header = "Value";
        ShowCol2 = true;
        ShowCol3 = false;
        ShowCol4 = false;
        ShowCol5 = false;

        TableData.Clear();

        var role = _dataManager.Settings.Role;
        var monthlyTarget = _dataManager.Settings.MonthlyRvuTarget;
        var shiftLength = _dataManager.Settings.ShiftLengthHours;

        // ============ CURRENT PERIOD STATS ============
        TableData.Add(new StatRow { Col1 = "CURRENT PERIOD", Col2 = "", IsHeader = true });

        if (!records.Any())
        {
            TableData.Add(new StatRow { Col1 = "No data for selected period", Col2 = "" });
        }
        else
        {
            var totalComp = CompensationRates.CalculateTotalCompensation(records, role);
            var timeSpan = records.Max(r => r.Timestamp) - records.Min(r => r.Timestamp);
            var totalHours = Math.Max(timeSpan.TotalHours, 1);
            var days = Math.Max(timeSpan.TotalDays, 1);

            TableData.Add(new StatRow { Col1 = "Total Studies", Col2 = TotalStudies.ToString() });
            TableData.Add(new StatRow { Col1 = "Total RVU", Col2 = $"{TotalRvu:F1}" });
            TableData.Add(new StatRow { Col1 = "Total Compensation", Col2 = $"${totalComp:N0}" });
            TableData.Add(new StatRow { Col1 = "Time Span", Col2 = $"{days:F1} days ({totalHours:F1} hours)" });

            var rvuPerHour = TotalRvu / totalHours;
            var rvuPerDay = TotalRvu / days;
            var compPerHour = totalComp / totalHours;

            TableData.Add(new StatRow { Col1 = "RVU/Hour", Col2 = $"{rvuPerHour:F1}" });
            TableData.Add(new StatRow { Col1 = "RVU/Day", Col2 = $"{rvuPerDay:F1}" });
            TableData.Add(new StatRow { Col1 = "Comp/Hour", Col2 = $"${compPerHour:N0}" });

            // ============ PROJECTIONS ============
            TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
            TableData.Add(new StatRow { Col1 = "PROJECTIONS (at current pace)", Col2 = "", IsHeader = true });

            // Weekly (5 work days, ~45 hours)
            var weeklyHours = 5 * shiftLength;
            var weeklyRvu = rvuPerHour * weeklyHours;
            var weeklyComp = compPerHour * weeklyHours;
            TableData.Add(new StatRow { Col1 = "Weekly (5 days)", Col2 = $"{weeklyRvu:F0} RVU, ${weeklyComp:N0}" });

            // Monthly (20 work days)
            var monthlyHours = 20 * shiftLength;
            var monthlyRvu = rvuPerHour * monthlyHours;
            var monthlyComp = compPerHour * monthlyHours;
            TableData.Add(new StatRow { Col1 = "Monthly (20 days)", Col2 = $"{monthlyRvu:N0} RVU, ${monthlyComp:N0}" });

            // Annual (240 work days)
            var annualHours = 240 * shiftLength;
            var annualRvu = rvuPerHour * annualHours;
            var annualComp = compPerHour * annualHours;
            TableData.Add(new StatRow { Col1 = "Annual (240 days)", Col2 = $"{annualRvu:N0} RVU, ${annualComp:N0}" });

            // ============ TARGET ANALYSIS ============
            if (monthlyTarget > 0)
            {
                TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
                TableData.Add(new StatRow { Col1 = "TARGET ANALYSIS", Col2 = "", IsHeader = true });
                TableData.Add(new StatRow { Col1 = "Monthly Target", Col2 = $"{monthlyTarget:N0} RVU" });

                var pctOfTarget = (monthlyRvu / monthlyTarget) * 100;
                TableData.Add(new StatRow { Col1 = "Projected vs Target", Col2 = $"{pctOfTarget:F0}% ({monthlyRvu:N0}/{monthlyTarget:N0})" });

                if (monthlyRvu < monthlyTarget)
                {
                    var deficit = monthlyTarget - monthlyRvu;
                    var extraHoursNeeded = deficit / rvuPerHour;
                    TableData.Add(new StatRow { Col1 = "Shortfall", Col2 = $"{deficit:F0} RVU ({extraHoursNeeded:F1} extra hours needed)" });
                }
                else
                {
                    var surplus = monthlyRvu - monthlyTarget;
                    TableData.Add(new StatRow { Col1 = "Surplus", Col2 = $"+{surplus:F0} RVU above target" });
                }
            }

            // ============ TOTALS ============
            TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
            TableData.Add(new StatRow { Col1 = "PROJECTED ANNUAL", Col2 = $"{annualRvu:N0} RVU, ${annualComp:N0}", IsTotal = true });
        }
    }

    private void DisplayAllStudies(List<StudyRecord> records)
    {
        // No chart for all studies - use dedicated DataGrid
        ShowCartesianChart = false;
        ShowPieChart = false;
        ShowSideCharts = false;
        IsAllStudiesView = true;

        // Clear the regular table data (not used in this view)
        TableData.Clear();
        AllStudiesData.Clear();

        if (!records.Any())
        {
            return;
        }

        // Sort by TimeFinished (or Timestamp), descending - most recent first
        var sortedRecords = records
            .OrderByDescending(x => x.TimeFinished ?? x.Timestamp)
            .ToList();

        var num = 1;
        foreach (var r in sortedRecords)
        {
            var timestamp = r.TimeFinished ?? r.Timestamp;
            AllStudiesData.Add(new AllStudyRow
            {
                RecordId = r.Id,
                RowNumber = num,
                Timestamp = timestamp,
                Procedure = r.Procedure ?? r.StudyType ?? "",
                StudyType = r.StudyType ?? "",
                Rvu = r.Rvu,
                DurationSeconds = r.DurationSeconds ?? 0
            });
            num++;
        }

        // Reset sort state
        _allStudiesSortColumn = "Timestamp";
        _allStudiesSortDescending = true;
    }

    private void DisplayCompensation(List<StudyRecord> records)
    {
        TableColumns.Clear();
        TableColumns.Add("Category");
        TableColumns.Add("Value");

        // Compensation view: use side charts panel with vertical bar chart
        ShowCartesianChart = false;
        ShowPieChart = false;
        ShowSideCharts = true;
        ShowModalityToggle = false;
        ShowSecondChart = false;
        ShowSideBarChart = true;  // Use bar chart instead of pie
        SideChartTitle = "Income by Modality";

        // Compensation: 2 columns (Category | Value)
        Col1Header = "Category";
        Col2Header = "Value";
        ShowCol2 = true;
        ShowCol3 = false;
        ShowCol4 = false;
        ShowCol5 = false;

        TableData.Clear();

        var role = _dataManager.Settings.Role;
        var monthlyTarget = _dataManager.Settings.MonthlyRvuTarget;

        // Calculate total compensation using per-record rates
        var totalComp = CompensationRates.CalculateTotalCompensation(records, role);
        var avgCompPerRvu = TotalRvu > 0 ? totalComp / TotalRvu : 0;
        var totalTbwuComp = _tbwuLookup.IsAvailable ? _tbwuLookup.CalculateTotalTbwuCompensation(records, role) : 0.0;
        var totalTbwu = _tbwuLookup.IsAvailable ? _tbwuLookup.GetTotalTbwu(records) : 0.0;

        // ============ RVU Summary ============
        TableData.Add(new StatRow { Col1 = "RVU SUMMARY", Col2 = "", IsHeader = true });
        TableData.Add(new StatRow { Col1 = "Total Studies", Col2 = TotalStudies.ToString() });
        TableData.Add(new StatRow { Col1 = "Total RVU", Col2 = $"{TotalRvu:F1}" });
        if (_tbwuLookup.IsAvailable)
            TableData.Add(new StatRow { Col1 = "Total TBWU", Col2 = $"{totalTbwu:F1}" });
        TableData.Add(new StatRow { Col1 = "Avg RVU/Study", Col2 = $"{AvgRvuPerStudy:F2}" });

        // ============ Compensation ============
        TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
        TableData.Add(new StatRow { Col1 = "COMPENSATION", Col2 = "", IsHeader = true });

        TableData.Add(new StatRow { Col1 = $"Role", Col2 = role });
        TableData.Add(new StatRow { Col1 = "Total Compensation", Col2 = $"${totalComp:N2}" });
        if (_tbwuLookup.IsAvailable)
            TableData.Add(new StatRow { Col1 = "Total Compensation (TBWU)", Col2 = $"${totalTbwuComp:N2}" });
        TableData.Add(new StatRow { Col1 = "Avg $/RVU", Col2 = $"${avgCompPerRvu:F2}" });

        if (TotalStudies > 0)
        {
            // Time calculations
            var timeSpan = records.Max(r => r.Timestamp) - records.Min(r => r.Timestamp);
            var hoursSpan = Math.Max(timeSpan.TotalHours, 1);

            TableData.Add(new StatRow { Col1 = "Hours Worked", Col2 = $"{hoursSpan:F1}" });
            TableData.Add(new StatRow { Col1 = "Comp/Hour", Col2 = $"${totalComp / hoursSpan:N2}" });
            if (_tbwuLookup.IsAvailable)
                TableData.Add(new StatRow { Col1 = "Comp/Hour (TBWU)", Col2 = $"${totalTbwuComp / hoursSpan:N2}" });
            TableData.Add(new StatRow { Col1 = "RVU/Hour", Col2 = $"{TotalRvu / hoursSpan:F1}" });
            TableData.Add(new StatRow { Col1 = "Studies/Hour", Col2 = $"{TotalStudies / hoursSpan:F1}" });

            // ============ Projected Income ============
            // This section uses CURRENT MONTH data regardless of selected period
            var currentMonthName = DateTime.Now.ToString("MMMM");
            var currentMonthYear = DateTime.Now.ToString("MMMM yyyy");
            TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
            TableData.Add(new StatRow { Col1 = $"PROJECTED INCOME ({currentMonthYear})", Col2 = "", IsHeader = true });

            // Get all records from current month
            var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            var monthEnd = monthStart.AddMonths(1);
            var currentMonthRecords = _allRecords.Where(r => r.Timestamp >= monthStart && r.Timestamp < monthEnd).ToList();

            // Calculate actual this month
            var actualMonthRvu = currentMonthRecords.Sum(r => r.Rvu);
            var actualMonthComp = CompensationRates.CalculateTotalCompensation(currentMonthRecords, role);

            // Calculate actual hours worked with 9-hour rounding for qualifying shifts
            var shiftLength = _dataManager.Settings.ShiftLengthHours;
            var actualMonthHours = 0.0;
            if (currentMonthRecords.Any())
            {
                // Group by shift and calculate duration for each
                var shiftGroups = currentMonthRecords.GroupBy(r => r.ShiftId);
                foreach (var shift in shiftGroups)
                {
                    var shiftDuration = (shift.Max(r => r.Timestamp) - shift.Min(r => r.Timestamp)).TotalHours;
                    shiftDuration = Math.Max(shiftDuration, 1); // Minimum 1 hour per shift

                    // Round shifts within 30 minutes of the expected shift length to that length
                    // (e.g., 8.5-9.5 hours rounds to 9 hours)
                    if (Math.Abs(shiftDuration - shiftLength) <= 0.5)
                    {
                        shiftDuration = shiftLength;
                    }
                    actualMonthHours += shiftDuration;
                }
            }

            // Get projection settings (these reset monthly)
            CheckAndResetMonthlyProjections();
            var targetShifts = _dataManager.Settings.ProjectionDays;
            var extraHours = _dataManager.Settings.ProjectionExtraHours;

            // Calculate target hours from shifts * shift length + extra hours
            var targetMonthlyHours = (targetShifts * shiftLength) + extraHours;

            // Remaining hours = target - actual (simple subtraction)
            var remainingHours = Math.Max(0, targetMonthlyHours - actualMonthHours);

            // Calculate hourly rate from current month (or selected period if no month data)
            var compPerHour = actualMonthHours > 0 ? actualMonthComp / actualMonthHours : (hoursSpan > 0 ? totalComp / hoursSpan : 0);
            var rvuPerHour = actualMonthHours > 0 ? actualMonthRvu / actualMonthHours : (hoursSpan > 0 ? TotalRvu / hoursSpan : 0);

            // Project remaining
            var projectedRemainingComp = compPerHour * remainingHours;
            var projectedRemainingRvu = rvuPerHour * remainingHours;

            // Total projected for month = actual + remaining
            var projectedMonthlyRvu = actualMonthRvu + projectedRemainingRvu;
            var projectedMonthlyComp = actualMonthComp + projectedRemainingComp;
            var projectedAnnualComp = projectedMonthlyComp * 12;

            // Display - hours worked first, then target total, then spinners for target settings
            TableData.Add(new StatRow { Col1 = $"Hours Worked in {currentMonthName}", Col2 = $"{actualMonthHours:F1}" });
            TableData.Add(new StatRow { Col1 = $"Target {currentMonthName} Hours", Col2 = $"{targetMonthlyHours:F0}" });
            TableData.Add(new StatRow { Col1 = $"Target {currentMonthName} Shifts", Col2 = $"{targetShifts}", IsEditable = true, EditKey = "ProjectionDays" });
            TableData.Add(new StatRow { Col1 = "Extra Hours", Col2 = $"{extraHours}", IsEditable = true, EditKey = "ProjectionExtraHours" });
            TableData.Add(new StatRow { Col1 = "Remaining Hours", Col2 = $"{remainingHours:F1}" });

            TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
            TableData.Add(new StatRow { Col1 = $"Actual {currentMonthName}", Col2 = $"${actualMonthComp:N0} ({actualMonthRvu:F1} RVU)" });
            TableData.Add(new StatRow { Col1 = "Projected Remaining", Col2 = $"${projectedRemainingComp:N0} ({projectedRemainingRvu:F1} RVU)" });
            TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
            TableData.Add(new StatRow { Col1 = $"Projected {currentMonthName} RVU", Col2 = $"{projectedMonthlyRvu:N0}" });
            TableData.Add(new StatRow { Col1 = $"Projected {currentMonthName} Income", Col2 = $"${projectedMonthlyComp:N0}" });
            TableData.Add(new StatRow { Col1 = "Projected Annual Income", Col2 = $"${projectedAnnualComp:N0}" });

            // TBWU projected income
            if (_tbwuLookup.IsAvailable)
            {
                var actualMonthTbwuComp = _tbwuLookup.CalculateTotalTbwuCompensation(currentMonthRecords, role);
                var tbwuCompPerHour = actualMonthHours > 0 ? actualMonthTbwuComp / actualMonthHours : (hoursSpan > 0 ? totalTbwuComp / hoursSpan : 0);
                var projectedRemainingTbwuComp = tbwuCompPerHour * remainingHours;
                var projectedMonthlyTbwuComp = actualMonthTbwuComp + projectedRemainingTbwuComp;
                var projectedAnnualTbwuComp = projectedMonthlyTbwuComp * 12;

                TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
                TableData.Add(new StatRow { Col1 = "TBWU PROJECTED INCOME", Col2 = "", IsHeader = true });
                TableData.Add(new StatRow { Col1 = $"Actual {currentMonthName} (TBWU)", Col2 = $"${actualMonthTbwuComp:N0}" });
                TableData.Add(new StatRow { Col1 = $"Projected {currentMonthName} Income (TBWU)", Col2 = $"${projectedMonthlyTbwuComp:N0}" });
                TableData.Add(new StatRow { Col1 = "Projected Annual Income (TBWU)", Col2 = $"${projectedAnnualTbwuComp:N0}" });
            }

            // Target comparison if set
            if (monthlyTarget > 0)
            {
                var pctOfTarget = (projectedMonthlyRvu / monthlyTarget) * 100;
                TableData.Add(new StatRow { Col1 = $"% of {currentMonthName} Target", Col2 = $"{pctOfTarget:F0}% ({projectedMonthlyRvu:F0}/{monthlyTarget:F0})" });
            }

            // ============ By Modality ============
            TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
            TableData.Add(new StatRow { Col1 = "BY MODALITY", Col2 = "", IsHeader = true });

            // Calculate compensation per modality using actual per-record rates
            var modalities = records.GroupBy(r => ExtractModality(r.StudyType))
                .Select(g => new
                {
                    Modality = g.Key,
                    Studies = g.Count(),
                    Rvu = g.Sum(r => r.Rvu),
                    Comp = CompensationRates.CalculateTotalCompensation(g, role)
                })
                .OrderByDescending(m => m.Comp)
                .ToList();

            foreach (var m in modalities)
            {
                var modCompStr = $"${m.Comp:N0} ({m.Studies} studies, {m.Rvu:F1} RVU)";
                if (_tbwuLookup.IsAvailable)
                {
                    var modTbwuComp = _tbwuLookup.CalculateTotalTbwuCompensation(
                        records.Where(r => ExtractModality(r.StudyType) == m.Modality), role);
                    modCompStr += $" (TBWU: ${modTbwuComp:N0})";
                }
                TableData.Add(new StatRow
                {
                    Col1 = $"  {m.Modality}",
                    Col2 = modCompStr
                });
            }

            // Build horizontal bar chart for income by modality
            // Single series with per-point coloring for thick bars - show ALL modalities
            var chartModalities = modalities.AsEnumerable().Reverse().ToList();  // Reversed so highest at top
            var colors = chartModalities.Select(m => GetModalityColor(m.Modality)).ToArray();
            var labels = chartModalities.Select(m => m.Modality).ToArray();

            // Build legend items for custom legend
            BarChartLegendItems.Clear();
            foreach (var m in modalities)  // Original order (highest first) for legend
            {
                BarChartLegendItems.Add(new LegendItem
                {
                    Color = GetModalityColor(m.Modality),
                    Label = $"{m.Modality}: ${m.Comp:N0}"
                });
            }

            var barSeries = new RowSeries<double>
            {
                Values = chartModalities.Select(m => m.Comp).ToArray(),
                Padding = 2,
                MaxBarWidth = double.MaxValue,
                DataLabelsPaint = new SolidColorPaint(SKColors.White),
                DataLabelsSize = 11,
                DataLabelsPosition = DataLabelsPosition.End,
                DataLabelsFormatter = point => $"${point.Model:N0}",
                Name = "Income"
            };

            // Apply per-point colors
            barSeries.PointMeasured += point =>
            {
                if (point.Visual is not null && point.Index < colors.Length)
                {
                    point.Visual.Fill = new SolidColorPaint(colors[point.Index]);
                }
            };

            SideBarSeries = new ISeries[] { barSeries };
            SideBarSections = Array.Empty<RectangularSection>();

            // Y axis shows modality names
            SideBarYAxes = new Axis[]
            {
                new Axis
                {
                    Labels = labels,
                    LabelsPaint = new SolidColorPaint(SKColors.White),
                    TextSize = 11,
                    SeparatorsPaint = new SolidColorPaint(new SKColor(80, 80, 80)),
                    ShowSeparatorLines = true,
                    MinStep = 1,
                    ForceStepToMin = true  // Force all labels to show
                }
            };

            // X axis shows dollar values with grid lines
            SideBarXAxes = new Axis[]
            {
                new Axis
                {
                    LabelsPaint = new SolidColorPaint(SKColors.White),
                    TextSize = 10,
                    Labeler = value => value >= 1000 ? $"${value/1000:F1}k" : $"${value:N0}",
                    MinLimit = 0,
                    SeparatorsPaint = new SolidColorPaint(new SKColor(80, 80, 80)),
                    ShowSeparatorLines = true,
                    TicksPaint = new SolidColorPaint(new SKColor(120, 120, 120))
                }
            };

            // ============ Target Progress ============
            if (monthlyTarget > 0)
            {
                TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
                TableData.Add(new StatRow { Col1 = "TARGET PROGRESS", Col2 = "", IsHeader = true });
                TableData.Add(new StatRow { Col1 = "Monthly Target", Col2 = $"{monthlyTarget:F0} RVU" });

                var pctComplete = (TotalRvu / monthlyTarget) * 100;
                TableData.Add(new StatRow { Col1 = "Progress", Col2 = $"{pctComplete:F1}% ({TotalRvu:F1}/{monthlyTarget:F0})" });

                var remaining = monthlyTarget - TotalRvu;
                if (remaining > 0)
                {
                    TableData.Add(new StatRow { Col1 = "Remaining", Col2 = $"{remaining:F1} RVU" });

                    // Days remaining estimate
                    var avgPerDay = TotalRvu / Math.Max(1, (records.Max(r => r.Timestamp) - records.Min(r => r.Timestamp)).TotalDays);
                    if (avgPerDay > 0)
                    {
                        var daysNeeded = remaining / avgPerDay;
                        TableData.Add(new StatRow { Col1 = "Est. Days Needed", Col2 = $"{daysNeeded:F1} days (at current pace)" });
                    }
                }
                else
                {
                    TableData.Add(new StatRow { Col1 = "Status", Col2 = "TARGET ACHIEVED!" });
                }
            }
        }
        else
        {
            PieSeries = Array.Empty<ISeries>();
        }
    }

    private void DisplayByBodyPart(List<StudyRecord> records)
    {
        // Use side panel for pie chart
        ShowCartesianChart = false;
        ShowPieChart = false;
        ShowSideCharts = true;
        ShowSideBarChart = false;
        ShowSecondChart = false;
        ShowModalityToggle = false;
        SideChartTitle = "RVU by Body Part";

        Col1Header = "Metric";
        Col2Header = "Value";
        ShowCol2 = true;
        ShowCol3 = false;
        ShowCol4 = false;
        ShowCol5 = false;

        TableData.Clear();

        if (!records.Any())
        {
            TableData.Add(new StatRow { Col1 = "No data for selected period", Col2 = "" });
            PieSeries = Array.Empty<ISeries>();
            return;
        }

        var role = _dataManager.Settings.Role;

        // Group by body part with compensation
        var bodyPartData = records.GroupBy(r => ExtractBodyPart(r.StudyType))
            .Select(g => new
            {
                BodyPart = g.Key,
                Studies = g.Count(),
                Rvu = g.Sum(r => r.Rvu),
                Comp = CompensationRates.CalculateTotalCompensation(g, role)
            })
            .OrderByDescending(b => b.Rvu)
            .ToList();

        var totalComp = bodyPartData.Sum(b => b.Comp);
        var totalTbwuComp = _tbwuLookup.IsAvailable ? _tbwuLookup.CalculateTotalTbwuCompensation(records, role) : 0.0;

        // ============ SUMMARY ============
        TableData.Add(new StatRow { Col1 = "SUMMARY", Col2 = "", IsHeader = true });
        TableData.Add(new StatRow { Col1 = "Total Studies", Col2 = TotalStudies.ToString() });
        TableData.Add(new StatRow { Col1 = "Total RVU", Col2 = $"{TotalRvu:F1}" });
        TableData.Add(new StatRow { Col1 = "Total Compensation", Col2 = $"${totalComp:N0}" });
        if (_tbwuLookup.IsAvailable)
            TableData.Add(new StatRow { Col1 = "Total Compensation (TBWU)", Col2 = $"${totalTbwuComp:N0}" });
        TableData.Add(new StatRow { Col1 = "Body Regions", Col2 = bodyPartData.Count.ToString() });

        // ============ BY BODY PART ============
        TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
        TableData.Add(new StatRow { Col1 = "BY BODY PART", Col2 = "", IsHeader = true });

        foreach (var b in bodyPartData)
        {
            var pctRvu = TotalRvu > 0 ? (b.Rvu / TotalRvu * 100) : 0;
            var bpCompStr = $"{b.Studies} studies, {b.Rvu:F1} RVU ({pctRvu:F0}%), ${b.Comp:N0}";
            if (_tbwuLookup.IsAvailable)
            {
                var bpTbwuComp = _tbwuLookup.CalculateTotalTbwuCompensation(
                    records.Where(r => ExtractBodyPart(r.StudyType) == b.BodyPart), role);
                bpCompStr += $" (TBWU: ${bpTbwuComp:N0})";
            }
            TableData.Add(new StatRow
            {
                Col1 = $"  {b.BodyPart}",
                Col2 = bpCompStr
            });
        }

        // ============ TOTALS ============
        TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
        var totalLineBodyPart = $"{TotalStudies} studies, {TotalRvu:F1} RVU, ${totalComp:N0}";
        if (_tbwuLookup.IsAvailable)
            totalLineBodyPart += $" (TBWU: ${totalTbwuComp:N0})";
        TableData.Add(new StatRow { Col1 = "TOTAL", Col2 = totalLineBodyPart, IsTotal = true });

        // Pie chart for side panel
        var bodyPartColors = new Dictionary<string, SKColor>
        {
            { "Head/Brain", new SKColor(66, 133, 244) },
            { "Neck/C-Spine", new SKColor(52, 168, 83) },
            { "Chest/Thorax", new SKColor(251, 188, 4) },
            { "Abdomen", new SKColor(234, 67, 53) },
            { "Pelvis", new SKColor(154, 75, 200) },
            { "Chest/Abd/Pelvis", new SKColor(255, 136, 0) },
            { "Spine", new SKColor(0, 188, 212) },
            { "Extremity", new SKColor(139, 195, 74) },
            { "Cardiac", new SKColor(233, 30, 99) },
            { "Vascular", new SKColor(121, 85, 72) },
            { "Other", new SKColor(158, 158, 158) }
        };

        var seriesList = new List<ISeries>();
        foreach (var b in bodyPartData.Take(8))
        {
            var color = bodyPartColors.GetValueOrDefault(b.BodyPart, new SKColor(100, 100, 100));
            var pct = TotalRvu > 0 ? (b.Rvu / TotalRvu * 100) : 0;
            seriesList.Add(new PieSeries<double>
            {
                Values = new double[] { b.Rvu },
                Name = $"{b.BodyPart}: {b.Rvu:F1} RVU ({pct:F0}%)",
                Fill = new SolidColorPaint(color),
                DataLabelsSize = 0,
                ToolTipLabelFormatter = _ => string.Empty
            });
        }
        PieSeries = seriesList.ToArray();
    }

    private void DisplayComparison()
    {
        TableColumns.Clear();
        TableColumns.Add("Metric");
        TableColumns.Add("Shift 1");
        TableColumns.Add("Shift 2");
        TableColumns.Add("Difference");

        ShowCartesianChart = true;
        ShowPieChart = false;
        ShowChartToggle = false;  // Hide chart toggle for comparison view (always show chart)

        // Comparison: 4 columns
        Col1Header = "Metric";
        Col2Header = "Shift 1";
        Col3Header = "Shift 2";
        Col4Header = "Difference";
        ShowCol2 = true;
        ShowCol3 = true;
        ShowCol4 = true;
        ShowCol5 = false;

        TableData.Clear();

        // Get two most recent shifts if not selected
        if (ComparisonShift1 == null && Shifts.Count > 0)
            ComparisonShift1 = Shifts.FirstOrDefault(s => !s.IsActive) ?? Shifts.FirstOrDefault();
        if (ComparisonShift2 == null && Shifts.Count > 1)
            ComparisonShift2 = Shifts.Skip(1).FirstOrDefault(s => !s.IsActive && s != ComparisonShift1);

        if (ComparisonShift1 == null || ComparisonShift2 == null)
        {
            TableData.Add(new StatRow { Col1 = "At least 2 shifts required for comparison", Col2 = "", Col3 = "", Col4 = "" });
            PeriodDescription = "Need 2+ shifts to compare";
            ChartSeries = Array.Empty<ISeries>();
            return;
        }

        var records1 = ExpandMultiAccessionRecords(_dataManager.Database.GetRecordsForShift(ComparisonShift1.Id));
        var records2 = ExpandMultiAccessionRecords(_dataManager.Database.GetRecordsForShift(ComparisonShift2.Id));

        PeriodDescription = $"Comparing: {ComparisonShift1.DisplayLabel} vs {ComparisonShift2.DisplayLabel}";

        // Calculate stats for each shift
        int count1 = records1.Count, count2 = records2.Count;
        double rvu1 = records1.Sum(r => r.Rvu), rvu2 = records2.Sum(r => r.Rvu);
        double avg1 = count1 > 0 ? rvu1 / count1 : 0;
        double avg2 = count2 > 0 ? rvu2 / count2 : 0;

        var duration1 = records1.Any() ? (records1.Max(r => r.Timestamp) - records1.Min(r => r.Timestamp)).TotalHours : 0;
        var duration2 = records2.Any() ? (records2.Max(r => r.Timestamp) - records2.Min(r => r.Timestamp)).TotalHours : 0;
        duration1 = Math.Max(1, duration1);
        duration2 = Math.Max(1, duration2);

        var rvuHr1 = rvu1 / duration1;
        var rvuHr2 = rvu2 / duration2;

        // ============ BASIC COMPARISON ============
        TableData.Add(new StatRow { Col1 = "BASIC COMPARISON", Col2 = "", Col3 = "", Col4 = "", IsHeader = true });
        AddComparisonRow("Total Studies", count1, count2, "{0:N0}");
        AddComparisonRow("Total RVU", rvu1, rvu2, "{0:F1}");
        AddComparisonRow("Avg RVU/Study", avg1, avg2, "{0:F2}");

        // ============ EFFICIENCY ============
        TableData.Add(new StatRow { Col1 = "", Col2 = "", Col3 = "", Col4 = "", IsSpacer = true });
        TableData.Add(new StatRow { Col1 = "EFFICIENCY", Col2 = "", Col3 = "", Col4 = "", IsHeader = true });
        AddComparisonRow("Duration (hrs)", duration1, duration2, "{0:F1}");
        AddComparisonRow("RVU/Hour", rvuHr1, rvuHr2, "{0:F1}");
        AddComparisonRow("Studies/Hour", count1 / duration1, count2 / duration2, "{0:F1}");

        // ============ MODALITY BREAKDOWN ============
        TableData.Add(new StatRow { Col1 = "", Col2 = "", Col3 = "", Col4 = "", IsSpacer = true });
        TableData.Add(new StatRow { Col1 = "BY MODALITY", Col2 = "", Col3 = "", Col4 = "", IsHeader = true });

        var mod1 = records1.GroupBy(r => ExtractModality(r.StudyType))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Rvu));
        var mod2 = records2.GroupBy(r => ExtractModality(r.StudyType))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Rvu));

        var allMods = mod1.Keys.Union(mod2.Keys).OrderBy(m => m).ToList();

        foreach (var mod in allMods)
        {
            var v1 = mod1.GetValueOrDefault(mod, 0);
            var v2 = mod2.GetValueOrDefault(mod, 0);
            AddComparisonRow($"  {mod} RVU", v1, v2, "{0:F1}");
        }

        // ============ COMPENSATION ============
        var baseRate = _dataManager.Settings.BaseRvuRate;
        if (baseRate > 0)
        {
            TableData.Add(new StatRow { Col1 = "", Col2 = "", Col3 = "", Col4 = "", IsSpacer = true });
            TableData.Add(new StatRow { Col1 = "COMPENSATION", Col2 = "", Col3 = "", Col4 = "", IsHeader = true });
            AddComparisonRow("Total Compensation", rvu1 * baseRate, rvu2 * baseRate, "${0:N0}");
            AddComparisonRow("Comp/Hour", (rvu1 * baseRate) / duration1, (rvu2 * baseRate) / duration2, "${0:N0}");
        }

        // ============ CHART - Bar comparison by modality ============
        var chartLabels = allMods.Count > 0 ? allMods : new List<string> { "No Data" };
        var values1 = allMods.Select(m => mod1.GetValueOrDefault(m, 0)).ToList();
        var values2 = allMods.Select(m => mod2.GetValueOrDefault(m, 0)).ToList();

        ChartSeries = new ISeries[]
        {
            new ColumnSeries<double>
            {
                Values = values1.ToArray(),
                Name = "Shift 1",
                Fill = new SolidColorPaint(SKColors.CornflowerBlue),
                MaxBarWidth = 25
            },
            new ColumnSeries<double>
            {
                Values = values2.ToArray(),
                Name = "Shift 2",
                Fill = new SolidColorPaint(SKColors.Orange),
                MaxBarWidth = 25
            }
        };

        XAxes = new Axis[]
        {
            new Axis
            {
                Labels = chartLabels,
                LabelsRotation = 0,
                TextSize = 10
            }
        };

        YAxes = new Axis[]
        {
            new Axis
            {
                Labeler = value => value.ToString("F0"),
                Name = "RVU"
            }
        };
    }

    private void AddComparisonRow(string metric, double v1, double v2, string format)
    {
        var diff = v1 - v2;
        var diffStr = diff > 0 ? $"+{string.Format(format, diff)}" : string.Format(format, diff);
        if (Math.Abs(diff) < 0.01) diffStr = "-";

        TableData.Add(new StatRow
        {
            Col1 = metric,
            Col2 = string.Format(format, v1),
            Col3 = string.Format(format, v2),
            Col4 = diffStr
        });
    }

    /// <summary>
    /// Extract body part from study type (e.g., "CT HEAD" -> "Head", "MRI BRAIN" -> "Brain")
    /// </summary>
    private string ExtractBodyPart(string studyType)
    {
        if (string.IsNullOrWhiteSpace(studyType))
            return "Unknown";

        var upper = studyType.ToUpperInvariant();

        // Common body part mappings
        if (upper.Contains("HEAD") || upper.Contains("BRAIN")) return "Head/Brain";
        if (upper.Contains("NECK") || upper.Contains("CERVICAL") || upper.Contains("C-SPINE") || upper.Contains("C SPINE")) return "Neck/C-Spine";
        if (upper.Contains("CHEST") || upper.Contains("THORAX") || upper.Contains("LUNG")) return "Chest/Thorax";
        if (upper.Contains("ABDOMEN") || upper.Contains("ABD")) return "Abdomen";
        if (upper.Contains("PELVIS")) return "Pelvis";
        if (upper.Contains("CAP")) return "Chest/Abd/Pelvis";
        if (upper.Contains("SPINE") || upper.Contains("LUMBAR") || upper.Contains("THORACIC") || upper.Contains("L-SPINE") || upper.Contains("T-SPINE")) return "Spine";
        if (upper.Contains("EXTREMITY") || upper.Contains("ARM") || upper.Contains("LEG") || upper.Contains("HAND") || upper.Contains("FOOT") ||
            upper.Contains("KNEE") || upper.Contains("ANKLE") || upper.Contains("WRIST") || upper.Contains("ELBOW") || upper.Contains("SHOULDER") ||
            upper.Contains("HIP")) return "Extremity";
        if (upper.Contains("CARDIAC") || upper.Contains("HEART") || upper.Contains("CORONARY")) return "Cardiac";
        if (upper.Contains("VASCULAR") || upper.Contains("ANGIO") || upper.Contains("CTA") || upper.Contains("MRA")) return "Vascular";

        // If no body part found, use modality
        var parts = studyType.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
            return parts[1]; // Return second word as body part guess

        return "Other";
    }

    // ===========================================
    // TIER 4 ANALYTICS DISPLAY METHODS
    // ===========================================

    private void DisplayTrends()
    {
        IsTrendsView = true;
        IsHeatmapView = false;
        IsInsightsView = false;

        ShowCartesianChart = true;
        ShowPieChart = false;
        ShowSideCharts = false;

        Col1Header = "Metric";
        Col2Header = "Value";
        ShowCol2 = true;
        ShowCol3 = false;
        ShowCol4 = false;
        ShowCol5 = false;

        TableData.Clear();

        // Determine date range based on selected period
        var (startDate, endDate) = GetDateRangeForPeriod();

        // Calculate minimum shift hours for filtering
        double minShiftHours = 0;
        if (TrendIgnoreShortShifts)
        {
            minShiftHours = Math.Max(0, _dataManager.Settings.ShiftLengthHours - 1.0);
        }

        var isRvuPerHour = TrendMetric == "rvuPerHour";
        var analysis = _trendService.AnalyzeTrends(startDate, endDate, TrendMetric, minShiftHours);

        // Update trend properties
        TrendInsightMessage = analysis.InsightMessage;
        TrendDirection = analysis.TrendDirection;
        TrendArrow = TrendAnalysisService.GetTrendArrow(analysis.TrendDirection);

        if (analysis.WeekOverWeekChange != 0)
            WeekOverWeekChange = $"{(analysis.WeekOverWeekChange > 0 ? "+" : "")}{analysis.WeekOverWeekChange:F1}%";
        else
            WeekOverWeekChange = "-";

        if (analysis.MonthOverMonthChange != 0)
            MonthOverMonthChange = $"{(analysis.MonthOverMonthChange > 0 ? "+" : "")}{analysis.MonthOverMonthChange:F1}%";
        else
            MonthOverMonthChange = "-";

        // Display summary stats
        TableData.Add(new StatRow { Col1 = "TREND ANALYSIS", Col2 = "", IsHeader = true });
        TableData.Add(new StatRow { Col1 = "Trend Direction", Col2 = $"{TrendArrow} {TrendDirection.ToUpperInvariant()}" });
        TableData.Add(new StatRow { Col1 = "Week over Week", Col2 = WeekOverWeekChange });
        TableData.Add(new StatRow { Col1 = "Month over Month", Col2 = MonthOverMonthChange });

        TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
        TableData.Add(new StatRow { Col1 = "PERIOD SUMMARY", Col2 = "", IsHeader = true });
        TableData.Add(new StatRow { Col1 = "Shifts", Col2 = analysis.TotalShifts.ToString() });
        TableData.Add(new StatRow { Col1 = "Total RVU", Col2 = $"{analysis.TotalRvu:F1}" });
        TableData.Add(new StatRow { Col1 = "Total Studies", Col2 = analysis.TotalStudies.ToString() });
        TableData.Add(new StatRow { Col1 = "Avg RVU/Shift", Col2 = $"{analysis.AvgRvuPerShift:F1}" });
        TableData.Add(new StatRow { Col1 = "Avg RVU/Hour", Col2 = $"{analysis.AvgRvuPerHour:F1}" });

        if (analysis.BestShiftDate.HasValue)
        {
            TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
            TableData.Add(new StatRow { Col1 = "BEST PERFORMANCE", Col2 = "", IsHeader = true });
            TableData.Add(new StatRow { Col1 = "Best Shift", Col2 = $"{analysis.BestShiftDate.Value:ddd MMM d}" });
            TableData.Add(new StatRow { Col1 = "Best Shift RVU", Col2 = $"{analysis.BestShiftRvu:F1}" });
            if (analysis.BestShiftRvuPerHour > 0)
                TableData.Add(new StatRow { Col1 = "Best RVU/Hour", Col2 = $"{analysis.BestShiftRvuPerHour:F1}" });
        }

        if (TrendIgnoreShortShifts)
        {
            TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
            TableData.Add(new StatRow { Col1 = $"Excluding shifts < {minShiftHours:F0}h", Col2 = "" });
        }

        // Chart: per-shift values with rolling averages
        if (analysis.ShiftData.Count > 0)
        {
            var metricLabel = isRvuPerHour ? "RVU/h" : "RVU";
            var shiftValues = analysis.ShiftData.Select(d => isRvuPerHour ? d.RvuPerHour : d.TotalRvu).ToArray();
            var rolling7 = analysis.ShiftData.Select(d => d.RollingAvg7).ToArray();
            var rolling30 = analysis.ShiftData.Select(d => d.RollingAvg30).ToArray();
            var labels = analysis.ShiftData.Select(d => d.Date.ToString("M/d")).ToArray();

            ChartSeries = new ISeries[]
            {
                new ColumnSeries<double>
                {
                    Values = shiftValues,
                    Name = $"Shift {metricLabel}",
                    Fill = new SolidColorPaint(new SKColor(66, 133, 244, 180)),
                    MaxBarWidth = 20
                },
                new LineSeries<double>
                {
                    Values = rolling7,
                    Name = "7-Shift Avg",
                    Stroke = new SolidColorPaint(new SKColor(52, 168, 83)) { StrokeThickness = 2 },
                    Fill = null,
                    GeometrySize = 0
                },
                new LineSeries<double>
                {
                    Values = rolling30,
                    Name = "30-Shift Avg",
                    Stroke = new SolidColorPaint(new SKColor(251, 188, 4)) { StrokeThickness = 2 },
                    Fill = null,
                    GeometrySize = 0
                }
            };

            XAxes = new Axis[]
            {
                new Axis
                {
                    Labels = labels,
                    LabelsPaint = new SolidColorPaint(SKColors.White),
                    TextSize = 10,
                    LabelsRotation = -45
                }
            };

            YAxes = new Axis[]
            {
                new Axis
                {
                    LabelsPaint = new SolidColorPaint(SKColors.White),
                    TextSize = 11,
                    Labeler = value => isRvuPerHour ? $"{value:F1}" : $"{value:F0}",
                    MinLimit = 0
                }
            };
        }
    }

    private void DisplayHeatmap()
    {
        IsTrendsView = false;
        IsHeatmapView = true;
        IsInsightsView = false;

        ShowCartesianChart = false;
        ShowPieChart = false;
        ShowSideCharts = false;

        Col1Header = "Metric";
        Col2Header = "Value";
        ShowCol2 = true;
        ShowCol3 = false;
        ShowCol4 = false;
        ShowCol5 = false;

        TableData.Clear();

        // Generate heatmap for last year
        var endDate = DateTime.Now.Date;
        var startDate = endDate.AddYears(-1);

        var heatmapCells = _insightsService.GenerateHeatmap(startDate, endDate);
        HeatmapData.Clear();
        foreach (var cell in heatmapCells)
            HeatmapData.Add(cell);

        // Summary stats
        var cellsWithData = heatmapCells.Where(c => c.StudyCount > 0).ToList();

        TableData.Add(new StatRow { Col1 = "HEATMAP SUMMARY", Col2 = "", IsHeader = true });
        TableData.Add(new StatRow { Col1 = "Days with Activity", Col2 = cellsWithData.Count.ToString() });

        if (cellsWithData.Any())
        {
            TableData.Add(new StatRow { Col1 = "Total RVU (Year)", Col2 = $"{cellsWithData.Sum(c => c.RvuTotal):F1}" });
            TableData.Add(new StatRow { Col1 = "Total Studies", Col2 = cellsWithData.Sum(c => c.StudyCount).ToString() });
            TableData.Add(new StatRow { Col1 = "Avg RVU per Active Day", Col2 = $"{cellsWithData.Average(c => c.RvuTotal):F1}" });

            var bestDay = cellsWithData.OrderByDescending(c => c.RvuTotal).First();
            TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
            TableData.Add(new StatRow { Col1 = "BEST DAY", Col2 = "", IsHeader = true });
            TableData.Add(new StatRow { Col1 = "Date", Col2 = bestDay.Date.ToString("MMM d, yyyy") });
            TableData.Add(new StatRow { Col1 = "RVU", Col2 = $"{bestDay.RvuTotal:F1}" });
            TableData.Add(new StatRow { Col1 = "Studies", Col2 = bestDay.StudyCount.ToString() });

            // Streaks
            var streaks = CalculateStreaks(heatmapCells);
            if (streaks.longest > 0)
            {
                TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
                TableData.Add(new StatRow { Col1 = "ACTIVITY STREAKS", Col2 = "", IsHeader = true });
                TableData.Add(new StatRow { Col1 = "Longest Streak", Col2 = $"{streaks.longest} days" });
                TableData.Add(new StatRow { Col1 = "Current Streak", Col2 = $"{streaks.current} days" });
            }
        }

        // Legend
        HeatmapLegend = "Less ░░░▓▓█ More";
    }

    private (int longest, int current) CalculateStreaks(List<HeatmapCell> cells)
    {
        var orderedCells = cells.OrderBy(c => c.Date).ToList();
        int longest = 0, current = 0, streak = 0;

        foreach (var cell in orderedCells)
        {
            if (cell.StudyCount > 0)
            {
                streak++;
                longest = Math.Max(longest, streak);
            }
            else
            {
                streak = 0;
            }
        }

        // Current streak (from today backwards)
        var today = DateTime.Now.Date;
        for (int i = orderedCells.Count - 1; i >= 0; i--)
        {
            if (orderedCells[i].Date <= today && orderedCells[i].StudyCount > 0)
                current++;
            else if (orderedCells[i].Date <= today)
                break;
        }

        return (longest, current);
    }

    private void DisplayInsights()
    {
        IsTrendsView = false;
        IsHeatmapView = false;
        IsInsightsView = true;

        ShowCartesianChart = true;
        ShowPieChart = false;
        ShowSideCharts = false;

        Col1Header = "Insight";
        Col2Header = "Value";
        ShowCol2 = true;
        ShowCol3 = false;
        ShowCol4 = false;
        ShowCol5 = false;

        TableData.Clear();

        // Determine date range
        var (startDate, endDate) = GetDateRangeForPeriod();

        var insights = _insightsService.AnalyzeProductivity(startDate, endDate);
        ProductivityInsights = insights;

        InsightRecommendations.Clear();
        foreach (var rec in insights.Recommendations)
            InsightRecommendations.Add(rec);

        // Display insights
        TableData.Add(new StatRow { Col1 = "PRODUCTIVITY INSIGHTS", Col2 = "", IsHeader = true });

        // Best time
        var hourStr = insights.BestHourOfDay == 0 ? "12 AM" :
            insights.BestHourOfDay < 12 ? $"{insights.BestHourOfDay} AM" :
            insights.BestHourOfDay == 12 ? "12 PM" :
            $"{insights.BestHourOfDay - 12} PM";
        TableData.Add(new StatRow { Col1 = "Most Productive Hour", Col2 = hourStr });
        TableData.Add(new StatRow { Col1 = "Best Day of Week", Col2 = insights.BestDayOfWeek.ToString() });
        TableData.Add(new StatRow { Col1 = "Best Day Avg RVU", Col2 = $"{insights.BestDayAvgRvu:F1}" });

        // Speed insights
        if (!string.IsNullOrEmpty(insights.FastestStudyType))
        {
            TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
            TableData.Add(new StatRow { Col1 = "SPEED ANALYSIS", Col2 = "", IsHeader = true });
            TableData.Add(new StatRow { Col1 = "Fastest Study Type", Col2 = $"{insights.FastestStudyType} ({insights.FastestAvgMinutes:F1} min)" });
            if (!string.IsNullOrEmpty(insights.SlowestStudyType))
                TableData.Add(new StatRow { Col1 = "Slowest Study Type", Col2 = $"{insights.SlowestStudyType} ({insights.SlowestAvgMinutes:F1} min)" });
        }

        // Top modality
        if (!string.IsNullOrEmpty(insights.TopModality))
        {
            TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
            TableData.Add(new StatRow { Col1 = "MODALITY FOCUS", Col2 = "", IsHeader = true });
            TableData.Add(new StatRow { Col1 = "Top Modality", Col2 = $"{insights.TopModality} ({insights.TopModalityPercent:F0}% of RVU)" });
        }

        // Recommendations
        if (insights.Recommendations.Any())
        {
            TableData.Add(new StatRow { Col1 = "", Col2 = "", IsSpacer = true });
            TableData.Add(new StatRow { Col1 = "RECOMMENDATIONS", Col2 = "", IsHeader = true });
            foreach (var rec in insights.Recommendations)
                TableData.Add(new StatRow { Col1 = $"  {rec}", Col2 = "" });
        }

        // Chart: RVU by hour of day
        if (insights.RvuByHour.Any())
        {
            var hours = Enumerable.Range(0, 24).ToArray();
            var rvuByHour = hours.Select(h => insights.RvuByHour.GetValueOrDefault(h, 0)).ToArray();
            var labels = hours.Select(h => h == 0 ? "12a" : h < 12 ? $"{h}a" : h == 12 ? "12p" : $"{h-12}p").ToArray();

            ChartSeries = new ISeries[]
            {
                new ColumnSeries<double>
                {
                    Values = rvuByHour,
                    Name = "RVU by Hour",
                    Fill = new SolidColorPaint(new SKColor(66, 133, 244)),
                    MaxBarWidth = 15
                }
            };

            XAxes = new Axis[]
            {
                new Axis
                {
                    Labels = labels,
                    LabelsPaint = new SolidColorPaint(SKColors.White),
                    TextSize = 9,
                    LabelsRotation = 0
                }
            };

            YAxes = new Axis[]
            {
                new Axis
                {
                    LabelsPaint = new SolidColorPaint(SKColors.White),
                    TextSize = 11,
                    Labeler = value => $"{value:F0}",
                    MinLimit = 0
                }
            };
        }
    }

    private (DateTime startDate, DateTime endDate) GetDateRangeForPeriod()
    {
        var now = DateTime.Now;
        var today = now.Date;

        return SelectedPeriod switch
        {
            "today" => (today, now),
            "this_week" => (today.AddDays(-(int)today.DayOfWeek), now),
            "this_month" => (new DateTime(today.Year, today.Month, 1), now),
            "last_month" => (new DateTime(today.Year, today.Month, 1).AddMonths(-1),
                            new DateTime(today.Year, today.Month, 1).AddDays(-1)),
            "last_3_months" => (today.AddMonths(-3), now),
            "last_year" => (today.AddYears(-1), now),
            "selected_shift" when SelectedShift != null =>
                (SelectedShift.ShiftStart, SelectedShift.ShiftEnd ?? now),
            _ => (DateTime.MinValue, now)
        };
    }
}

/// <summary>
/// Represents a row in the statistics table.
/// </summary>
public class StatRow
{
    public string Col1 { get; set; } = "";
    public string Col2 { get; set; } = "";
    public string Col3 { get; set; } = "";
    public string Col4 { get; set; } = "";
    public string Col5 { get; set; } = "";
    public string Col6 { get; set; } = "";
    public bool IsTotal { get; set; }
    public bool IsHeader { get; set; }  // Section headers like "═══ BASIC STATS ═══"
    public bool IsSpacer { get; set; }  // Empty spacer rows between sections
    public bool IsEditable { get; set; }  // Row with editable number control
    public string EditKey { get; set; } = "";  // Key for identifying which setting to edit
}

/// <summary>
/// Legend item for custom bar chart legend
/// </summary>
public class LegendItem
{
    public SKColor Color { get; set; }
    public string Label { get; set; } = "";
}

/// <summary>
/// Represents a study row in the All Studies view with typed properties for proper sorting.
/// </summary>
public class AllStudyRow
{
    public int RecordId { get; set; }  // Database ID for deletion
    public int RowNumber { get; set; }
    public DateTime Timestamp { get; set; }
    public string Procedure { get; set; } = "";
    public string StudyType { get; set; } = "";
    public double Rvu { get; set; }
    public double DurationSeconds { get; set; }

    // Display properties (formatted strings)
    public string RowNumberDisplay => RowNumber.ToString();
    public string DateDisplay => Timestamp.ToString("M/d/yyyy");
    public string TimeDisplay => Timestamp.ToString("h:mm tt");
    public string RvuDisplay => $"{Rvu:F1}";
    public string DurationDisplay
    {
        get
        {
            if (DurationSeconds <= 0) return "-";
            if (DurationSeconds < 60) return $"{(int)DurationSeconds}s";
            return $"{(int)(DurationSeconds / 60)}m {(int)(DurationSeconds % 60)}s";
        }
    }
}
