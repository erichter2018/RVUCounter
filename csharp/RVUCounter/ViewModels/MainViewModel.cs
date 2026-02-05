using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RVUCounter.Core;
using RVUCounter.Data;
using RVUCounter.Logic;
using RVUCounter.Models;
using RVUCounter.Services;
using RVUCounter.Views;
using Serilog;
using System.Windows.Media;

namespace RVUCounter.ViewModels;

/// <summary>
/// ViewModel for the main application window.
/// Uses CommunityToolkit.Mvvm for MVVM pattern.
/// Implements all 6 stats with Python formula parity.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly DataManager _dataManager;
    private readonly StudyTracker _studyTracker;
    private readonly FatigueDetector _fatigueDetector;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _fatigueTimer;
    private readonly DispatcherTimer _backupTimer;
    private readonly BackupManager _backupManager;

    // Named pipe client for MosaicTools bridge (replaces double-scraping)
    private MosaicToolsPipeClient? _pipeClient;
    // Track last sent shift info to avoid redundant sends
    private double _lastSentTotalRvu = -1;
    private int _lastSentRecordCount = -1;
    private double _lastSentCurrentHourRvu = -1;
    private double _lastSentPriorHourRvu = -1;
    private double _lastSentEstimatedTotalRvu = -1;

    /// <summary>
    /// Exposes DataManager for dialogs that need access to settings/database
    /// </summary>
    public DataManager DataManager => _dataManager;

    // ===========================================
    // VERSION & STATUS
    // ===========================================

    public string AppVersion => $"v{Config.AppVersion}";

    // ===========================================
    // MAIN STATS (Python parity)
    // ===========================================

    [ObservableProperty]
    private double _totalRvu;

    [ObservableProperty]
    private double _avgPerHour;

    [ObservableProperty]
    private double _lastHourRvu;

    [ObservableProperty]
    private double _lastFullHourRvu;

    [ObservableProperty]
    private string _lastFullHourRange = "";

    [ObservableProperty]
    private double _projectedThisHour;

    [ObservableProperty]
    private double _projectedShiftTotal;

    [ObservableProperty]
    private int _studyCount;

    // ===========================================
    // COMPENSATION STATS
    // ===========================================

    [ObservableProperty]
    private double _totalCompensation;

    [ObservableProperty]
    private double _avgCompensationPerHour;

    [ObservableProperty]
    private double _lastHourCompensation;

    [ObservableProperty]
    private double _lastFullHourCompensation;

    [ObservableProperty]
    private double _projectedCompensation;

    [ObservableProperty]
    private double _projectedShiftCompensation;

    [ObservableProperty]
    private bool _showCompensation;

    // ===========================================
    // COUNTER VISIBILITY (from Settings)
    // ===========================================

    [ObservableProperty]
    private bool _showTotal = true;

    [ObservableProperty]
    private bool _showAvg = true;

    [ObservableProperty]
    private bool _showLastHour = true;

    [ObservableProperty]
    private bool _showLastFullHour = true;

    [ObservableProperty]
    private bool _showProjected = true;

    [ObservableProperty]
    private bool _showProjectedShift = true;

    [ObservableProperty]
    private bool _showPaceCar = false;

    // Per-counter compensation visibility
    [ObservableProperty]
    private bool _showCompTotal = false;

    [ObservableProperty]
    private bool _showCompAvg = false;

    [ObservableProperty]
    private bool _showCompLastHour = false;

    [ObservableProperty]
    private bool _showCompLastFullHour = false;

    [ObservableProperty]
    private bool _showCompProjected = false;

    [ObservableProperty]
    private bool _showCompProjectedShift = true;

    // Dark mode
    [ObservableProperty]
    private bool _darkMode = false;

    // Show time in recent studies (matches Python's show_time setting)
    [ObservableProperty]
    private bool _showTime = false;

    // Inpatient Stat percentage tracking
    [ObservableProperty]
    private bool _showInpatientStatPercentage = true;

    [ObservableProperty]
    private double _inpatientStatPercentage;

    [ObservableProperty]
    private int _inpatientStatCount;

    // ===========================================
    // SHIFT STATE
    // ===========================================

    [ObservableProperty]
    private string _shiftStatus = "No Active Shift";

    [ObservableProperty]
    private string _shiftDuration = "0:00";

    [ObservableProperty]
    private bool _isShiftActive;

    [ObservableProperty]
    private bool _isAlwaysOnTop = true;

    private DateTime? _shiftStart;
    private DateTime? _effectiveShiftStart;
    private DateTime? _projectedShiftEnd;

    // ===========================================
    // CURRENT STUDY (real-time display)
    // ===========================================

    [ObservableProperty]
    private string _currentAccession = "-";

    [ObservableProperty]
    private string _currentProcedure = "-";

    [ObservableProperty]
    private string _currentPatientClass = "-";

    [ObservableProperty]
    private string _currentStudyType = "-";

    [ObservableProperty]
    private double _currentStudyRvu;

    [ObservableProperty]
    private string _currentDuration = "";

    [ObservableProperty]
    private string _dataSourceIndicator = "detecting...";

    // MosaicTools pipe connection state
    [ObservableProperty]
    private bool _isMosaicToolsPipeConnected;

    [ObservableProperty]
    private string _pipeStatusText = "MT";

    [ObservableProperty]
    private Brush _pipeStatusColor = Brushes.Gray;

    public string PipeStatusTooltip => IsMosaicToolsPipeConnected
        ? "Connected to MosaicTools pipe \u2014 using MT data instead of scraping"
        : "MosaicTools pipe not connected \u2014 scraping Mosaic directly";

    partial void OnIsMosaicToolsPipeConnectedChanged(bool value)
    {
        PipeStatusColor = value ? Brushes.LimeGreen : Brushes.Gray;
        OnPropertyChanged(nameof(PipeStatusTooltip));
    }

    // "already recorded" indicator - shows when current study is already in database
    [ObservableProperty]
    private bool _isCurrentStudyAlreadyRecorded;

    [ObservableProperty]
    private string _currentAccessionDisplay = "-";

    [ObservableProperty]
    private Brush _currentAccessionColor = Brushes.Gray;

    // Patient name and site code for current study (memory only - not persisted)
    [ObservableProperty]
    private string _currentPatientName = "";

    [ObservableProperty]
    private string _currentSiteCode = "";

    // ===========================================
    // RECENT STUDIES
    // ===========================================

    [ObservableProperty]
    private ObservableCollection<StudyRecord> _recentStudies = new();

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private string _recentStudiesLabel = "Recent Studies";

    [ObservableProperty]
    private Brush _recentStudiesLabelColor = Brushes.Gray;

    // Temporary studies tracked when no shift is active
    private List<StudyRecord> _temporaryStudies = new();

    // ===========================================
    // CRITICAL RESULTS TRACKING (MosaicTools)
    // ===========================================

    // Track accessions with critical results (hashed accessions)
    private readonly HashSet<string> _criticalStudies = new();
    private readonly object _criticalStudiesLock = new();

    [ObservableProperty]
    private bool _showCriticalOnly;

    [ObservableProperty]
    private int _criticalResultsCount;

    partial void OnShowCriticalOnlyChanged(bool value)
    {
        // Refresh the recent studies display when filter changes
        UpdateRecentStudiesDisplay();
        OnPropertyChanged(nameof(CriticalFilterTooltip));
    }

    /// <summary>
    /// Tooltip for the critical filter button.
    /// </summary>
    public string CriticalFilterTooltip => ShowCriticalOnly
        ? "Click to show all studies"
        : "Click to show only critical results";

    [RelayCommand]
    private void ToggleCriticalFilter()
    {
        ShowCriticalOnly = !ShowCriticalOnly;
    }

    /// <summary>
    /// Mark a study as having a critical result.
    /// Called when MosaicTools sends a critical result message.
    /// </summary>
    private void MarkStudyAsCritical(string hashedAccession)
    {
        lock (_criticalStudiesLock)
        {
            _criticalStudies.Add(hashedAccession);
        }
        UpdateCriticalResultsCount();
    }

    private void UpdateCriticalResultsCount()
    {
        // Count critical results in current recent studies
        var count = RecentStudies.Count(s => s.HasCriticalResult);
        CriticalResultsCount = count;
    }

    // ===========================================
    // UNDO/REDO (Python parity - single level)
    // ===========================================

    // When true, button shows "Redo" and will restore _lastUndoneStudy
    private bool _undoUsed;

    // Store the last undone study for redo capability
    private StudyRecord? _lastUndoneStudy;
    private int _lastUndoneShiftId;  // Remember which shift it belonged to

    [ObservableProperty]
    private string _undoButtonText = "Undo";

    // Patient class cache by accession (like Python's _clario_patient_class_cache)
    private readonly Dictionary<string, string> _clarioPatientClassCache = new();
    private readonly object _clarioCacheLock = new();
    private string _lastClarioAccession = "";

    // Circuit breaker for Clario enrichment — exponential backoff on repeated failures
    private int _clarioFailureCount;
    private DateTime _clarioBackoffUntil = DateTime.MinValue;

    // ===========================================
    // PATIENT INFO CACHE (Memory Only - Privacy)
    // ===========================================
    // These are stored in memory only and never persisted to the database.
    // Key: hashed accession, Value: patient info
    private readonly Dictionary<string, string> _patientNameCache = new();
    private readonly Dictionary<string, string> _siteCodeCache = new();
    private readonly Dictionary<string, string> _originalAccessionCache = new();
    private readonly Dictionary<string, string> _mrnCache = new();
    private readonly object _patientInfoCacheLock = new();

    /// <summary>
    /// Get patient name for a study record (memory only lookup).
    /// Returns null if not found in cache.
    /// </summary>
    public string? GetPatientName(string hashedAccession)
    {
        lock (_patientInfoCacheLock)
        {
            return _patientNameCache.TryGetValue(hashedAccession, out var name) ? name : null;
        }
    }

    /// <summary>
    /// Get site code for a study record (memory only lookup).
    /// Returns null if not found in cache.
    /// </summary>
    public string? GetSiteCode(string hashedAccession)
    {
        lock (_patientInfoCacheLock)
        {
            return _siteCodeCache.TryGetValue(hashedAccession, out var site) ? site : null;
        }
    }

    /// <summary>
    /// Get original (unhashed) accession for a study record (memory only lookup).
    /// Returns null if not found in cache. Used for Clario integration.
    /// </summary>
    public string? GetOriginalAccession(string hashedAccession)
    {
        lock (_patientInfoCacheLock)
        {
            return _originalAccessionCache.TryGetValue(hashedAccession, out var accession) ? accession : null;
        }
    }

    /// <summary>
    /// Get MRN (Medical Record Number) for a study record (memory only lookup).
    /// Returns null if not found in cache. Used for Clario integration via XML file drop.
    /// </summary>
    public string? GetMrn(string hashedAccession)
    {
        lock (_patientInfoCacheLock)
        {
            return _mrnCache.TryGetValue(hashedAccession, out var mrn) ? mrn : null;
        }
    }

    /// <summary>
    /// Get formatted tooltip text for a study record.
    /// Includes patient name and site if available in memory cache.
    /// </summary>
    public string GetStudyTooltip(StudyRecord study)
    {
        var parts = new List<string>();

        // Patient name (if available)
        var patientName = GetPatientName(study.Accession);
        if (!string.IsNullOrEmpty(patientName))
        {
            parts.Add($"Patient: {patientName}");
        }

        // Site code (if available)
        var siteCode = GetSiteCode(study.Accession);
        if (!string.IsNullOrEmpty(siteCode))
        {
            parts.Add($"Site: {siteCode}");
        }

        // Study details
        parts.Add($"Procedure: {study.Procedure}");
        parts.Add($"Study Type: {study.StudyType}");
        parts.Add($"RVU: {study.Rvu:F1}");
        parts.Add($"Patient Class: {study.PatientClass}");
        parts.Add($"Time: {study.Timestamp:h:mm tt}");

        if (study.DurationSeconds.HasValue)
        {
            var duration = TimeSpan.FromSeconds(study.DurationSeconds.Value);
            parts.Add($"Duration: {(int)duration.TotalMinutes}m {duration.Seconds}s");
        }

        if (study.HasCriticalResult)
        {
            parts.Add("Critical Result: Yes");
        }

        return string.Join("\n", parts);
    }

    // ===========================================
    // DISTRACTION ALERT
    // ===========================================
    // Average duration by study type — cached from database, refreshed periodically
    private Dictionary<string, double> _avgDurationByStudyType = new();
    private DateTime _avgDurationCacheTime = DateTime.MinValue;

    // Track which alerts have been sent: accession → last alert level sent
    private readonly Dictionary<string, int> _distractionAlertsSent = new();
    private string _distractionAlertCurrentAccession = "";

    /// <summary>
    /// Refresh the average duration cache from the database (all-time).
    /// Called on shift start and periodically.
    /// </summary>
    private void RefreshAverageDurationCache()
    {
        try
        {
            _avgDurationByStudyType = _dataManager.Database.GetAverageDurations();
            _avgDurationCacheTime = DateTime.Now;
            Log.Debug("Refreshed avg duration cache: {Count} study types", _avgDurationByStudyType.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to refresh average duration cache");
        }
    }

    /// <summary>
    /// Check if the current study has been open too long and send a distraction alert via pipe.
    /// Called from ApplyScanResults after updating CurrentDuration.
    /// </summary>
    private void CheckDistractionAlert(string accession, string studyType, double trackedSeconds)
    {
        var settings = _dataManager.Settings;
        if (!settings.DistractionAlertEnabled) return;
        if (_pipeClient == null || !_pipeClient.IsConnected) return;

        // If accession changed, reset alert tracking
        if (accession != _distractionAlertCurrentAccession)
        {
            _distractionAlertsSent.Clear();
            _distractionAlertCurrentAccession = accession;
        }

        // Refresh cache every 30 minutes
        if ((DateTime.Now - _avgDurationCacheTime).TotalMinutes > 30)
        {
            RefreshAverageDurationCache();
        }

        // Get expected duration for this study type
        double expectedSeconds = settings.DistractionAlertFallbackSeconds;
        if (_avgDurationByStudyType.TryGetValue(studyType, out var avgDuration) && avgDuration > 0)
        {
            expectedSeconds = avgDuration;
        }

        // Calculate current threshold: multiplier + (alertLevel - 1) * escalationStep
        // First alert at multiplier (e.g. 2.0x), next at multiplier + step (e.g. 3.0x), etc.
        int lastLevel = _distractionAlertsSent.TryGetValue(accession, out var lvl) ? lvl : 0;
        int nextLevel = lastLevel + 1;
        double threshold = expectedSeconds * (settings.DistractionAlertMultiplier + (nextLevel - 1) * settings.DistractionAlertEscalationStep);

        if (trackedSeconds >= threshold)
        {
            _distractionAlertsSent[accession] = nextLevel;
            _pipeClient.SendDistractionAlert(studyType, trackedSeconds, expectedSeconds, nextLevel);
        }
    }

    // MosaicTools integration - pending studies waiting for signed/unsigned confirmation
    private readonly Dictionary<string, PendingStudy> _pendingStudies = new();
    private readonly object _pendingStudiesLock = new();
    private DispatcherTimer? _pendingStudiesTimer;

    // Pre-emptive messages - received before we detected the study completing
    // Key: raw accession, Value: timestamp when received (for cleanup)
    private readonly Dictionary<string, DateTime> _preEmptiveUnsigned = new();
    // Pre-emptive signed: stores (timestamp, hasCritical)
    private readonly Dictionary<string, (DateTime Time, bool HasCritical)> _preEmptiveSigned = new();
    private readonly object _preEmptiveUnsignedLock = new();
    private readonly object _preEmptiveSignedLock = new();

    // Inactivity auto-end feature (Python parity)
    private const int InactivityThresholdSeconds = 3600;  // 1 hour of no studies = auto-end shift
    private DateTime? _lastStudyRecordedTime;
    private DateTime? _lastMosaicActivityTime;  // Updated whenever Mosaic shows a valid accession

    // Pace car time-of-day vs elapsed time constants (Python parity)
    private const int TypicalShiftStartHour = 23;  // 11pm
    private const int PaceCalcDeviationMinutes = 30;  // If shift start differs from typical by >30 min, use elapsed time

    // Multi-accession tracking (Python parity)
    // When multiple accessions are visible simultaneously, track all of them
    // and create individual records when they complete together
    private bool _isMultiAccessionMode = false;
    private DateTime? _multiAccessionStartTime;
    private readonly Dictionary<string, MultiAccessionStudy> _multiAccessionData = new();
    private string? _currentMultiAccessionGroup;

    // ===========================================
    // PACE CAR
    // ===========================================

    [ObservableProperty]
    private double _paceDiff;

    [ObservableProperty]
    private string _paceDescription = "vs Last Shift";

    [ObservableProperty]
    private double _currentBarFactor;

    [ObservableProperty]
    private double _priorBarFactor;

    [ObservableProperty]
    private double _targetMarkerFactor;

    [ObservableProperty]
    private Brush _diffColor = Brushes.Gray;

    [ObservableProperty]
    private double _targetRvu; // Current target (prior at elapsed)

    // New properties for Python-style pace display
    [ObservableProperty]
    private string _paceCompareLabel = "Prior:";

    [ObservableProperty]
    private string _paceTimeText = "";

    [ObservableProperty]
    private string _paceDiffText = "";

    // Pace car should only be visible when shift is active AND setting is enabled
    public bool IsPaceCarVisible => IsShiftActive && ShowPaceCar;

    // Pace comparison mode - 'prior', 'goal', 'best_week', 'best_ever', or specific week
    [ObservableProperty]
    private string _paceComparisonMode = "prior";

    partial void OnIsShiftActiveChanged(bool value)
    {
        // Notify that IsPaceCarVisible may have changed
        OnPropertyChanged(nameof(IsPaceCarVisible));
    }

    partial void OnShowPaceCarChanged(bool value)
    {
        // Notify that IsPaceCarVisible may have changed
        OnPropertyChanged(nameof(IsPaceCarVisible));
        _dataManager.Settings.ShowPaceCar = value;
        _dataManager.SaveSettings();
    }

    // ===========================================
    // FATIGUE DETECTION
    // ===========================================

    [ObservableProperty]
    private bool _showFatigueWarning;

    [ObservableProperty]
    private string _fatigueWarningMessage = "";

    [RelayCommand]
    private void DismissFatigueWarning()
    {
        ShowFatigueWarning = false;
    }

    // ===========================================
    // TEAM DASHBOARD (Privacy-First)
    // ===========================================

    private TeamSyncService? _teamSyncService;
    private DispatcherTimer? _teamSyncTimer;

    [ObservableProperty]
    private bool _teamDashboardEnabled;

    [ObservableProperty]
    private string _teamCode = "";

    [ObservableProperty]
    private int _activePeerCount;

    [ObservableProperty]
    private double _teamTotalRvu;

    [ObservableProperty]
    private double _teamMinRvu;

    [ObservableProperty]
    private double _teamMaxRvu;

    [ObservableProperty]
    private ObservableCollection<TeamMemberDisplay> _teamMembers = new();

    [ObservableProperty]
    private string _teamStatusMessage = "";

    [ObservableProperty]
    private bool _showTeamPanel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TeamPanelExpandedIcon))]
    private bool _isTeamPanelExpanded = true;

    /// <summary>
    /// True = show RVU/hour (rate), False = show Total RVU
    /// Default to rate mode for fairness with different start times
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TeamModeLabel))]
    [NotifyPropertyChangedFor(nameof(TeamMinLabel))]
    [NotifyPropertyChangedFor(nameof(TeamMaxLabel))]
    private bool _teamShowRate = true;

    /// <summary>
    /// Label showing current mode (clickable to toggle)
    /// </summary>
    public string TeamModeLabel => TeamShowRate ? "/hr" : "total";

    /// <summary>
    /// Min value label for number line
    /// </summary>
    public string TeamMinLabel => TeamShowRate
        ? $"{TeamMinRvu:F0}/hr"
        : $"{TeamMinRvu:F0}";

    /// <summary>
    /// Max value label for number line
    /// </summary>
    public string TeamMaxLabel => TeamShowRate
        ? $"{TeamMaxRvu:F0}/hr"
        : $"{TeamMaxRvu:F0} RVU";

    /// <summary>
    /// Icon showing expand/collapse state
    /// </summary>
    public string TeamPanelExpandedIcon => IsTeamPanelExpanded ? "▼" : "▶";

    /// <summary>
    /// Toggle team panel expanded state
    /// </summary>
    [RelayCommand]
    private void ToggleTeamPanel()
    {
        IsTeamPanelExpanded = !IsTeamPanelExpanded;
    }

    /// <summary>
    /// Toggle between Rate and Total view modes
    /// </summary>
    [RelayCommand]
    private void ToggleTeamViewMode()
    {
        TeamShowRate = !TeamShowRate;
        // Refresh display with new mode
        _ = SyncTeamStatsAsync();
    }

    /// <summary>
    /// Show team details when anyone is connected (including yourself)
    /// </summary>
    public bool CanShowTeamDetails => ActivePeerCount >= 1;

    partial void OnActivePeerCountChanged(int value)
    {
        OnPropertyChanged(nameof(CanShowTeamDetails));
    }

    partial void OnTeamDashboardEnabledChanged(bool value)
    {
        _dataManager.Settings.TeamDashboardEnabled = value;
        _dataManager.SaveSettings();

        if (value)
        {
            InitializeTeamDashboard();
        }
        else
        {
            StopTeamDashboard();
        }
    }

    private void InitializeTeamDashboard()
    {
        if (_teamSyncService == null)
        {
            _teamSyncService = new TeamSyncService();
        }

        // Generate anonymous ID if needed (regenerate on each enable for privacy)
        if (string.IsNullOrEmpty(_dataManager.Settings.TeamAnonymousId) || !TeamDashboardEnabled)
        {
            _dataManager.Settings.TeamAnonymousId = TeamSyncService.GenerateAnonymousId();
            _dataManager.SaveSettings();
        }

        // Start sync timer (every 30 seconds)
        if (_teamSyncTimer == null)
        {
            _teamSyncTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _teamSyncTimer.Tick += async (s, e) => await SyncTeamStatsAsync();
        }
        _teamSyncTimer.Start();

        ShowTeamPanel = true;
        TeamStatusMessage = "Connecting...";

        Log.Information("Team dashboard initialized, ShowTeamPanel={ShowTeamPanel}, StorageUrl exists={HasUrl}",
            ShowTeamPanel, !string.IsNullOrEmpty(_dataManager.Settings.TeamStorageUrl));

        // Initial sync
        _ = SyncTeamStatsAsync();
    }

    private void StopTeamDashboard()
    {
        _teamSyncTimer?.Stop();
        ShowTeamPanel = false;
        TeamMembers.Clear();
        ActivePeerCount = 0;
        TeamStatusMessage = "";
    }

    private async Task SyncTeamStatsAsync()
    {
        if (_teamSyncService == null || string.IsNullOrEmpty(_dataManager.Settings.TeamStorageUrl))
        {
            TeamStatusMessage = "Not connected to team";
            return;
        }

        try
        {
            // Build our anonymous stats
            var myStats = new TeamMemberStats
            {
                Id = _dataManager.Settings.TeamAnonymousId ?? "",
                ShiftActive = IsShiftActive,
                TotalRvu = TotalRvu,
                StudyCount = StudyCount,
                RvuPerHour = AvgPerHour,
                ShiftDurationMins = _shiftStart.HasValue
                    ? (int)(DateTime.Now - _shiftStart.Value).TotalMinutes
                    : 0,
                InpatientStatPct = InpatientStatPercentage
            };

            // Update our stats
            await _teamSyncService.UpdateStatsAsync(_dataManager.Settings.TeamStorageUrl, myStats);

            // Fetch team stats
            var (members, success) = await _teamSyncService.GetTeamStatsAsync(_dataManager.Settings.TeamStorageUrl);

            if (success)
            {
                ActivePeerCount = members.Count;
                TeamTotalRvu = members.Sum(m => m.TotalRvu);

                if (CanShowTeamDetails)
                {
                    // Show panel when we have enough peers
                    ShowTeamPanel = true;

                    var (displayData, minVal, maxVal) = TeamSyncService.PrepareDisplayData(
                        members,
                        _dataManager.Settings.TeamAnonymousId ?? "",
                        TeamShowRate);

                    TeamMinRvu = minVal;
                    TeamMaxRvu = maxVal;
                    OnPropertyChanged(nameof(TeamMinLabel));
                    OnPropertyChanged(nameof(TeamMaxLabel));

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        TeamMembers.Clear();
                        foreach (var item in displayData)
                            TeamMembers.Add(item);
                    });

                    TeamStatusMessage = $"({ActivePeerCount} active)";
                }
                else
                {
                    TeamMembers.Clear();
                    TeamStatusMessage = $"Waiting for teammates ({ActivePeerCount}/4 online)";
                    // Keep panel visible with waiting message - don't auto-collapse
                    ShowTeamPanel = true;
                }

                Log.Debug("Team sync complete: {PeerCount} peers, CanShowDetails={CanShow}",
                    ActivePeerCount, CanShowTeamDetails);
            }
            else
            {
                TeamStatusMessage = "Connection error";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error syncing team stats");
            TeamStatusMessage = "Sync error";
        }
    }

    [RelayCommand]
    private async Task CreateTeamAsync()
    {
        if (_teamSyncService == null)
            _teamSyncService = new TeamSyncService();

        TeamStatusMessage = "Creating team...";

        var (success, teamCode, storageUrl, error) = await _teamSyncService.CreateTeamAsync();

        if (success)
        {
            _dataManager.Settings.TeamCode = teamCode;
            _dataManager.Settings.TeamStorageUrl = storageUrl;
            _dataManager.Settings.TeamAnonymousId = TeamSyncService.GenerateAnonymousId();
            _dataManager.SaveSettings();

            TeamCode = teamCode;
            TeamStatusMessage = $"Team created: {teamCode}";
            TeamDashboardEnabled = true;

            Log.Information("Created team {TeamCode}", teamCode);
        }
        else
        {
            TeamStatusMessage = error;
            Log.Error("Failed to create team: {Error}", error);
        }
    }

    [RelayCommand]
    private void JoinTeam(string code)
    {
        if (string.IsNullOrEmpty(code) || code.Length != 6)
        {
            TeamStatusMessage = "Invalid team code";
            return;
        }

        // Note: In a real implementation, you'd lookup the storage URL
        // For now, user needs to also provide the storage URL
        TeamCode = code.ToUpperInvariant();
        _dataManager.Settings.TeamCode = TeamCode;
        _dataManager.SaveSettings();

        TeamStatusMessage = "Enter storage URL to connect";
    }

    [RelayCommand]
    private void LeaveTeam()
    {
        TeamDashboardEnabled = false;
        _dataManager.Settings.TeamCode = null;
        _dataManager.Settings.TeamStorageUrl = null;
        _dataManager.Settings.TeamAnonymousId = null;
        _dataManager.SaveSettings();

        TeamCode = "";
        TeamStatusMessage = "";
        Log.Information("Left team");
    }

    public MainViewModel()
    {
        // Initialize data manager
        var baseDir = PlatformUtils.GetAppRoot();
        LoggingConfig.Initialize(baseDir);
        _dataManager = new DataManager(baseDir);
        _studyTracker = new StudyTracker(_dataManager.Settings.MinStudySeconds);
        _fatigueDetector = new FatigueDetector();

        // Load settings
        IsAlwaysOnTop = _dataManager.Settings.AlwaysOnTop;
        ShowCompensation = true; // Always show - we have hardcoded compensation rates

        // Counter visibility
        ShowTotal = _dataManager.Settings.ShowTotal;
        ShowAvg = _dataManager.Settings.ShowAvg;
        ShowLastHour = _dataManager.Settings.ShowLastHour;
        ShowLastFullHour = _dataManager.Settings.ShowLastFullHour;
        ShowProjected = _dataManager.Settings.ShowProjected;
        ShowProjectedShift = _dataManager.Settings.ShowProjectedShift;
        ShowPaceCar = _dataManager.Settings.ShowPaceCar;
        PaceComparisonMode = _dataManager.Settings.PaceComparisonMode;

        // Compensation visibility
        ShowCompTotal = _dataManager.Settings.ShowCompTotal;
        ShowCompAvg = _dataManager.Settings.ShowCompAvg;
        ShowCompLastHour = _dataManager.Settings.ShowCompLastHour;
        ShowCompLastFullHour = _dataManager.Settings.ShowCompLastFullHour;
        ShowCompProjected = _dataManager.Settings.ShowCompProjected;
        ShowCompProjectedShift = _dataManager.Settings.ShowCompProjectedShift;

        // Theme preset (load custom themes first, then apply)
        ThemePresets.LoadCustomPresets(_dataManager.Settings.CustomThemes);
        ThemeManager.ApplyPreset(_dataManager.Settings.ThemePreset, _dataManager.Settings.CustomThemeOverrides);
        ThemeManager.ApplyFontFamily(_dataManager.Settings.FontFamily);
        DarkMode = ThemeManager.IsDarkMode;

        // Global font size adjustment
        ThemeManager.ApplyFontSize(_dataManager.Settings.GlobalFontSizeAdjustment);

        // Show time in recent studies
        ShowTime = _dataManager.Settings.ShowTime;

        // Show inpatient stat percentage
        ShowInpatientStatPercentage = _dataManager.Settings.ShowInpatientStatPercentage;

        // Setup refresh timer (checks for new studies)
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500) // 500ms polling - balanced performance
        };
        _refreshTimer.Tick += OnRefreshTick;

        // Setup stats timer (updates time-sensitive stats)
        _statsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _statsTimer.Tick += OnStatsTick;

        // Setup fatigue detection timer (checks every 5 minutes during active shift)
        _fatigueTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(5)
        };
        _fatigueTimer.Tick += OnFatigueTick;

        // Setup backup manager and timer (checks hourly/daily schedules)
        _backupManager = new BackupManager(
            _dataManager.DatabasePath,
            () => _dataManager.Settings,
            s => _dataManager.SaveSettings(),
            () => _dataManager.Database.Checkpoint());
        _backupTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(15) // Check every 15 minutes for scheduled backups
        };
        _backupTimer.Tick += OnBackupTick;
        _backupTimer.Start(); // Always running to check schedule

        // Check for existing shift and resume
        LoadCurrentShift();

        // Set initial status based on MosaicTools integration
        if (_dataManager.Settings.MosaicToolsIntegrationEnabled)
        {
            StatusMessage = "🔗 MT: Listening";
        }

        // Perform startup update check (async, non-blocking)
        _ = PerformStartupUpdateCheckAsync();

        // Initialize team dashboard if enabled
        TeamCode = _dataManager.Settings.TeamCode ?? "";
        if (_dataManager.Settings.TeamDashboardEnabled)
        {
            _teamDashboardEnabled = true; // Set field directly to avoid triggering save
            InitializeTeamDashboard();
        }

        Log.Information("MainViewModel initialized");
    }

    private void LoadCurrentShift()
    {
        var shift = _dataManager.Database.GetCurrentShift();
        var autoResume = _dataManager.Settings.AutoResumeOnStartup;

        Log.Information("LoadCurrentShift: Found shift={ShiftId}, AutoResume={AutoResume}",
            shift?.Id ?? 0, autoResume);

        if (shift != null && autoResume)
        {
            _shiftStart = shift.ShiftStart;
            _effectiveShiftStart = shift.EffectiveShiftStart ?? shift.ShiftStart;
            _projectedShiftEnd = _effectiveShiftStart.Value.AddHours(_dataManager.Settings.ShiftLengthHours);

            IsShiftActive = true;
            ShiftStatus = $"Shift: {shift.ShiftStart:h:mm tt}";

            Log.Information("Resumed shift {Id} started at {Start}", shift.Id, shift.ShiftStart);

            CalculateAndUpdateStats();
            _statsTimer.Start();
            _fatigueTimer.Start();
            _fatigueDetector.StartNewShift(shift.ShiftStart);
            ShowFatigueWarning = false;

            // Pre-load average durations for distraction alert
            RefreshAverageDurationCache();
        }
        else
        {
            if (shift != null && !autoResume)
            {
                Log.Information("Found active shift {Id} but AutoResumeOnStartup is disabled", shift.Id);
            }
            else
            {
                Log.Information("No active shift found in database");
            }

            IsShiftActive = false;
            ShiftStatus = "No Active Shift";
            ResetStats();
        }

        // Always start refresh timer for Current Study detection
        _refreshTimer.Start();
    }

    /// <summary>
    /// Perform startup update check:
    /// 1. Clean up old version from previous update
    /// 2. Show WhatsNewWindow if version changed
    /// 3. Auto-update if enabled
    /// </summary>
    private async Task PerformStartupUpdateCheckAsync()
    {
        try
        {
            // Step 1: Check if we just updated (old version file exists)
            var justUpdated = UpdateManager.JustUpdated();
            var currentVersion = UpdateManager.GetCurrentVersion();

            // Step 2: Check if version changed (either from update or first run after new install)
            var lastSeenVersion = _dataManager.Settings.LastSeenVersion;
            if (justUpdated || (lastSeenVersion != null && lastSeenVersion != currentVersion))
            {
                Log.Information("Version changed from {Old} to {New}, showing What's New",
                    lastSeenVersion ?? "none", currentVersion);

                // Update last seen version
                _dataManager.Settings.LastSeenVersion = currentVersion;
                _dataManager.SaveSettings();

                // Show What's New window on UI thread
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var whatsNew = new WhatsNewWindow(currentVersion);
                        whatsNew.Owner = Application.Current.MainWindow;
                        whatsNew.ShowDialog();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Could not show What's New window");
                    }
                });

                // Now that we've confirmed the new version works, clean up the old one
                if (justUpdated)
                {
                    UpdateManager.CleanupOldVersion();
                }
            }
            else if (lastSeenVersion == null)
            {
                // First run - just save the current version
                _dataManager.Settings.LastSeenVersion = currentVersion;
                _dataManager.SaveSettings();
            }

            // Step 3: Auto-update check if enabled
            if (_dataManager.Settings.AutoUpdateEnabled)
            {
                // Circuit breaker: skip if we just restarted for an update (prevents loops)
                if (!UpdateManager.IsAutoUpdateSafe())
                {
                    Log.Warning("Skipping auto-update this launch (circuit breaker)");
                    // Still clean up old version if present since we got this far
                    if (justUpdated) UpdateManager.CleanupOldVersion();
                    return;
                }

                Log.Information("Auto-update enabled, checking for updates...");
                var updateManager = new UpdateManager();
                var updateInfo = await updateManager.CheckForUpdateAsync();

                if (updateInfo?.IsUpdateAvailable == true)
                {
                    Log.Information("Update available: {Version}, applying silently", updateInfo.Version);

                    if (string.IsNullOrEmpty(updateInfo.DownloadUrl))
                    {
                        Log.Warning("Update {Version} has no download URL", updateInfo.Version);
                        return;
                    }

                    StatusMessage = $"Updating to v{updateInfo.Version}...";

                    var success = await updateManager.DownloadAndApplyUpdateAsync(
                        updateInfo.DownloadUrl,
                        expectedSize: updateInfo.AssetSize);

                    if (success)
                    {
                        Log.Information("Update applied successfully, preparing to restart");

                        // Flush data before exit to prevent corruption
                        try
                        {
                            _dataManager.SaveSettings();
                            _dataManager.Database.Checkpoint();
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Error flushing data before update restart");
                        }

                        // Write circuit breaker marker before restarting
                        UpdateManager.WriteRestartMarker();
                        UpdateManager.RestartApp();
                    }
                    else
                    {
                        Log.Warning("Silent update failed for {Version}", updateInfo.Version);
                        StatusMessage = "Update failed";
                    }
                }
                else
                {
                    Log.Debug("No update available or check failed");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during startup update check");
        }
    }

    /// <summary>
    /// Refresh display settings from UserSettings after save.
    /// Called by SettingsWindow when user saves changes.
    /// </summary>
    public void RefreshSettings()
    {
        var settings = _dataManager.Settings;

        // Window behavior
        IsAlwaysOnTop = settings.AlwaysOnTop;
        
        // Counter visibility
        ShowTotal = settings.ShowTotal;
        ShowAvg = settings.ShowAvg;
        ShowLastHour = settings.ShowLastHour;
        ShowLastFullHour = settings.ShowLastFullHour;
        ShowProjected = settings.ShowProjected;
        ShowProjectedShift = settings.ShowProjectedShift;
        ShowPaceCar = settings.ShowPaceCar;

        // Compensation visibility
        ShowCompTotal = settings.ShowCompTotal;
        ShowCompAvg = settings.ShowCompAvg;
        ShowCompLastHour = settings.ShowCompLastHour;
        ShowCompLastFullHour = settings.ShowCompLastFullHour;
        ShowCompProjected = settings.ShowCompProjected;
        ShowCompProjectedShift = settings.ShowCompProjectedShift;

        // Theme
        ThemeManager.ApplyPreset(settings.ThemePreset, settings.CustomThemeOverrides);
        ThemeManager.ApplyFontFamily(settings.FontFamily);
        DarkMode = ThemeManager.IsDarkMode;

        // Show time in recent studies
        ShowTime = settings.ShowTime;

        // Show inpatient stat percentage
        ShowInpatientStatPercentage = settings.ShowInpatientStatPercentage;

        // Compensation is always available with hardcoded rates
        ShowCompensation = true;

        // Recalculate stats (role may have changed, affecting rates)
        if (IsShiftActive)
        {
            CalculateAndUpdateStats();
        }

        // Update status for MosaicTools integration
        if (settings.MosaicToolsIntegrationEnabled)
        {
            StatusMessage = "🔗 MT: Listening";
        }
        else
        {
            StatusMessage = "Ready";
        }

        // Team Dashboard - reload settings and update state
        TeamCode = settings.TeamCode ?? "";
        var wasEnabled = TeamDashboardEnabled;
        var nowEnabled = settings.TeamDashboardEnabled;

        Log.Information("RefreshSettings: Team wasEnabled={WasEnabled}, nowEnabled={NowEnabled}, StorageUrl exists={HasUrl}",
            wasEnabled, nowEnabled, !string.IsNullOrEmpty(settings.TeamStorageUrl));

        if (nowEnabled && !wasEnabled)
        {
            // Team dashboard was just enabled
            Log.Information("Team dashboard being enabled via RefreshSettings");
            _teamDashboardEnabled = true;
            OnPropertyChanged(nameof(TeamDashboardEnabled));
            InitializeTeamDashboard();
        }
        else if (!nowEnabled && wasEnabled)
        {
            // Team dashboard was just disabled
            Log.Information("Team dashboard being disabled via RefreshSettings");
            _teamDashboardEnabled = false;
            OnPropertyChanged(nameof(TeamDashboardEnabled));
            StopTeamDashboard();
        }
        else if (nowEnabled)
        {
            // Already enabled - just refresh the storage URL in case it changed
            Log.Information("Team dashboard already enabled, checking if URL changed");
            if (string.IsNullOrEmpty(settings.TeamStorageUrl))
            {
                StopTeamDashboard();
            }
            else if (!ShowTeamPanel)
            {
                // Panel not visible but should be - reinitialize
                Log.Information("Team panel not visible, reinitializing");
                InitializeTeamDashboard();
            }
        }

        Log.Information("Display settings refreshed from UserSettings");
    }

    private void ResetStats()
    {
        TotalRvu = 0;
        AvgPerHour = 0;
        LastHourRvu = 0;
        LastFullHourRvu = 0;
        LastFullHourRange = "";
        ProjectedThisHour = 0;
        ProjectedShiftTotal = 0;
        StudyCount = 0;
        ShiftDuration = "0:00";
        
        TotalCompensation = 0;
        AvgCompensationPerHour = 0;
        LastHourCompensation = 0;
        LastFullHourCompensation = 0;
        ProjectedCompensation = 0;
        ProjectedShiftCompensation = 0;
    }

    /// <summary>
    /// Calculate all stats using exact Python formulas from calculate_stats()
    /// </summary>
    private void CalculateAndUpdateStats()
    {
        if (_shiftStart == null)
        {
            ResetStats();
            return;
        }

        var shift = _dataManager.Database.GetCurrentShift();
        if (shift == null) return;

        var records = _dataManager.Database.GetRecordsForShift(shift.Id);
        var currentTime = DateTime.Now;

        // Track last study time for inactivity auto-end (Python parity)
        if (records.Count > 0)
        {
            // Use TimeFinished if available, otherwise Timestamp
            var mostRecent = records.OrderByDescending(r => r.TimeFinished ?? r.Timestamp).FirstOrDefault();
            if (mostRecent != null)
                _lastStudyRecordedTime = mostRecent.TimeFinished ?? mostRecent.Timestamp;
        }

        // Total RVU and compensation
        TotalRvu = records.Sum(r => r.Rvu);
        TotalCompensation = records.Sum(r => CalculateStudyCompensation(r));
        StudyCount = records.Count;

        // Calculate inpatient stat percentage
        // PatientClass from Clario contains combined priority+class: e.g., "Stat inpatient"
        InpatientStatCount = records.Count(r =>
            r.PatientClass.Contains("Stat inpatient", StringComparison.OrdinalIgnoreCase));
        InpatientStatPercentage = records.Count > 0
            ? (InpatientStatCount * 100.0 / records.Count)
            : 0;

        // Hours elapsed
        var hoursElapsed = (currentTime - _shiftStart.Value).TotalHours;

        // Average per hour
        AvgPerHour = hoursElapsed > 0 ? TotalRvu / hoursElapsed : 0;
        AvgCompensationPerHour = hoursElapsed > 0 ? TotalCompensation / hoursElapsed : 0;

        // Shift duration display
        var duration = currentTime - _shiftStart.Value;
        ShiftDuration = $"{(int)duration.TotalHours}:{duration.Minutes:D2}";

        // Last hour (rolling 60 minutes)
        var oneHourAgo = currentTime.AddHours(-1);
        var lastHourRecords = records.Where(r => r.Timestamp >= oneHourAgo).ToList();
        LastHourRvu = lastHourRecords.Sum(r => r.Rvu);
        LastHourCompensation = lastHourRecords.Sum(r => CalculateStudyCompensation(r));

        // Last full hour (e.g., 2am to 3am)
        var currentHourStart = new DateTime(currentTime.Year, currentTime.Month, currentTime.Day, 
            currentTime.Hour, 0, 0);
        var lastFullHourStart = currentHourStart.AddHours(-1);
        var lastFullHourEnd = currentHourStart;

        var lastFullHourRecords = records.Where(r => 
            r.Timestamp >= lastFullHourStart && r.Timestamp < lastFullHourEnd).ToList();
        LastFullHourRvu = lastFullHourRecords.Sum(r => r.Rvu);
        LastFullHourCompensation = lastFullHourRecords.Sum(r => CalculateStudyCompensation(r));
        LastFullHourRange = $"{FormatHourLabel(lastFullHourStart)}-{FormatHourLabel(lastFullHourEnd)}";

        // Projected for current hour
        var currentHourRecords = records.Where(r => r.Timestamp >= currentHourStart).ToList();
        var currentHourRvu = currentHourRecords.Sum(r => r.Rvu);
        var currentHourComp = currentHourRecords.Sum(r => CalculateStudyCompensation(r));

        var minutesIntoHour = (currentTime - currentHourStart).TotalMinutes;
        if (minutesIntoHour > 0)
        {
            ProjectedThisHour = (currentHourRvu / minutesIntoHour) * 60;
            ProjectedCompensation = (currentHourComp / minutesIntoHour) * 60;
        }
        else
        {
            ProjectedThisHour = 0;
            ProjectedCompensation = 0;
        }

        // Projected shift total
        ProjectedShiftTotal = TotalRvu;
        ProjectedShiftCompensation = TotalCompensation;

        if (_effectiveShiftStart != null && _projectedShiftEnd != null)
        {
            var timeRemaining = (_projectedShiftEnd.Value - currentTime).TotalSeconds;

            if (timeRemaining > 0 && hoursElapsed > 0)
            {
                var hoursRemaining = timeRemaining / 3600;
                var projectedAdditionalRvu = AvgPerHour * hoursRemaining;
                ProjectedShiftTotal = TotalRvu + projectedAdditionalRvu;

                var projectedAdditionalComp = AvgCompensationPerHour * hoursRemaining;
                ProjectedShiftCompensation = TotalCompensation + projectedAdditionalComp;
            }
        }

        UpdatePaceCar(hoursElapsed);

        // Update critical results count
        CriticalResultsCount = records.Count(r => r.HasCriticalResult);

        // Update recent studies list (newest first, no limit - show all shift studies)
        var previousCount = RecentStudies.Count;
        RecentStudies.Clear();

        // Apply critical filter if enabled
        var displayRecords = ShowCriticalOnly
            ? records.Where(r => r.HasCriticalResult)
            : records;

        foreach (var record in displayRecords.OrderByDescending(r => r.Timestamp))
        {
            RecentStudies.Add(record);
        }

        // Scroll to top if new study was added
        if (RecentStudies.Count > previousCount)
        {
            RequestScrollToTop();
        }

        // Update label for active shift
        RecentStudiesLabel = ShowCriticalOnly ? "Critical Results Only" : "Recent Studies";
        RecentStudiesLabelColor = ShowCriticalOnly
            ? Brushes.OrangeRed
            : Application.Current.TryFindResource("SecondaryTextBrush") as Brush ?? Brushes.Gray;

        // Send shift info to MosaicTools via pipe (if connected and values changed)
        SendShiftInfoToPipe();
    }

    private string FormatHourLabel(DateTime dt)
    {
        var hour = dt.Hour;
        var hour12 = hour % 12;
        if (hour12 == 0) hour12 = 12;
        var amPm = hour < 12 ? "am" : "pm";
        return $"{hour12}{amPm}";
    }

    private void UpdatePaceCar(double hoursElapsed)
    {
        try
        {
            var now = DateTime.Now;
            var hour = now.Hour;
            var hour12 = hour > 12 ? hour - 12 : (hour == 0 ? 12 : hour);
            if (hour12 == 0) hour12 = 12;
            var amPm = hour < 12 ? "am" : "pm";
            PaceTimeText = $" at {hour12}:{now.Minute:D2} {amPm}";

            // Python parity: time-of-day vs elapsed time logic
            // If shift start is within 30 minutes of typical (11pm), use time-of-day reference
            var effectiveHoursElapsed = hoursElapsed;
            if (_shiftStart != null)
            {
                // Calculate how far the shift start is from typical start (11pm)
                var typicalStart = new DateTime(_shiftStart.Value.Year, _shiftStart.Value.Month, _shiftStart.Value.Day,
                    TypicalShiftStartHour, 0, 0);

                // If shift started before 11pm, typical start was the previous day
                if (_shiftStart.Value.Hour < TypicalShiftStartHour && _shiftStart.Value.Hour >= 0 && _shiftStart.Value.Hour < 12)
                {
                    typicalStart = typicalStart.AddDays(-1);
                }

                var minutesFromTypical = Math.Abs((_shiftStart.Value - typicalStart).TotalMinutes);

                if (minutesFromTypical <= PaceCalcDeviationMinutes)
                {
                    // Close to typical start - use time-of-day reference (hours since 11pm)
                    var hoursSince11pm = (now - typicalStart).TotalHours;
                    // Handle day rollover
                    if (hoursSince11pm < 0)
                    {
                        hoursSince11pm += 24;
                    }
                    effectiveHoursElapsed = hoursSince11pm;
                    Log.Debug("Pace car using time-of-day reference: {Hours:F2}h since 11pm (shift start was {Min:F0} min from typical)",
                        effectiveHoursElapsed, minutesFromTypical);
                }
                else
                {
                    Log.Debug("Pace car using elapsed time: {Hours:F2}h (shift start was {Min:F0} min from typical)",
                        hoursElapsed, minutesFromTypical);
                }
            }

            double targetAtElapsed;
            double targetTotal;
            string description;
            string compareLabel;

            if (PaceComparisonMode == "goal")
            {
                // Goal mode: compare against theoretical pace
                var rvuPerHour = _dataManager.Settings.PaceGoalRvuPerHour;
                var goalHours = _dataManager.Settings.PaceGoalShiftHours;
                var goalTotal = _dataManager.Settings.PaceGoalTotalRvu;

                if (rvuPerHour <= 0) rvuPerHour = 25.0;  // Python default
                if (goalHours <= 0) goalHours = 9.0;
                if (goalTotal <= 0) goalTotal = rvuPerHour * goalHours;

                targetAtElapsed = rvuPerHour * effectiveHoursElapsed;
                targetTotal = goalTotal;
                description = $"vs Goal ({rvuPerHour:F1}/h)";
                compareLabel = "Goal:";
            }
            else
            {
                // Shift comparison mode (prior, best_week, best_ever, week_N)
                var compareShift = GetComparisonShift();

                if (compareShift == null)
                {
                    PaceDescription = "No comparison data";
                    PaceCompareLabel = "Prior:";
                    PaceTimeText = "";
                    PaceDiffText = "no data";
                    CurrentBarFactor = 0;
                    PriorBarFactor = 0;
                    TargetMarkerFactor = 0;
                    TargetRvu = 0;
                    PaceDiff = 0;
                    DiffColor = Brushes.Gray;
                    return;
                }

                var records = _dataManager.Database.GetRecordsForShift(compareShift.Id);
                targetTotal = records.Sum(r => r.Rvu);

                // Calculate RVU at elapsed time (using effectiveHoursElapsed for time-of-day reference)
                var shiftStart = compareShift.ShiftStart;
                targetAtElapsed = records
                    .Where(r => (r.Timestamp - shiftStart).TotalHours <= effectiveHoursElapsed)
                    .Sum(r => r.Rvu);

                description = $"vs {compareShift.ShiftStart:ddd M/d}";
                compareLabel = PaceComparisonMode switch
                {
                    "best_week" => "Best:",
                    "best_ever" => "Best:",
                    _ => "Prior:"
                };
            }

            PaceDescription = description;
            PaceCompareLabel = compareLabel;
            TargetRvu = targetAtElapsed;

            // Calculate diff
            PaceDiff = TotalRvu - targetAtElapsed;
            if (PaceDiff > 0.05)
            {
                DiffColor = Brushes.LimeGreen;
                PaceDiffText = $"▲ +{PaceDiff:F1} ahead";
            }
            else if (PaceDiff < -0.05)
            {
                DiffColor = Brushes.Red;
                PaceDiffText = $"▼ {PaceDiff:F1} behind";
            }
            else
            {
                DiffColor = Brushes.Gray;
                PaceDiffText = "on pace";
            }

            // Calculate bar factors
            var maxScale = Math.Max(TotalRvu, Math.Max(targetTotal, 0.1));
            CurrentBarFactor = TotalRvu / maxScale;
            PriorBarFactor = targetTotal / maxScale;
            TargetMarkerFactor = targetAtElapsed / maxScale;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in UpdatePaceCar");
        }
    }

    private Shift? GetComparisonShift()
    {
        var allShifts = _dataManager.Database.GetAllShifts()
            .Where(s => s.ShiftEnd != null)
            .OrderByDescending(s => s.ShiftStart)
            .ToList();

        var currentShiftId = _dataManager.Database.GetCurrentShift()?.Id;

        // Filter out current shift
        allShifts = allShifts.Where(s => s.Id != currentShiftId).ToList();

        if (allShifts.Count == 0) return null;

        // Populate RVU for comparison
        foreach (var s in allShifts)
        {
            s.TotalRvu = _dataManager.Database.GetTotalRvuForShift(s.Id);
        }

        return PaceComparisonMode switch
        {
            "prior" => allShifts.FirstOrDefault(),
            "best_week" => GetBestThisWeek(allShifts),
            "best_ever" => allShifts.Where(s => s.Duration.TotalHours >= 7 && s.Duration.TotalHours <= 11)
                                    .OrderByDescending(s => s.TotalRvu).FirstOrDefault(),
            var m when m.StartsWith("week_") => GetWeekShift(allShifts, m),
            _ => allShifts.FirstOrDefault()
        };
    }

    private Shift? GetBestThisWeek(List<Shift> shifts)
    {
        var now = DateTime.Now;
        var daysToMonday = ((int)now.DayOfWeek + 6) % 7;
        var weekStart = now.Date.AddDays(-daysToMonday);

        return shifts
            .Where(s => s.ShiftStart >= weekStart)
            .OrderByDescending(s => s.TotalRvu)
            .FirstOrDefault();
    }

    private Shift? GetWeekShift(List<Shift> shifts, string mode)
    {
        // mode is like "week_0", "week_1", etc.
        if (!int.TryParse(mode.Replace("week_", ""), out var idx))
            return shifts.FirstOrDefault();

        var now = DateTime.Now;
        var daysToMonday = ((int)now.DayOfWeek + 6) % 7;
        var weekStart = now.Date.AddDays(-daysToMonday);

        var thisWeekShifts = shifts
            .Where(s => s.ShiftStart >= weekStart)
            .OrderBy(s => s.ShiftStart)
            .ToList();

        return idx < thisWeekShifts.Count ? thisWeekShifts[idx] : null;
    }

    private double CalculateStudyCompensation(StudyRecord record)
    {
        // Use hardcoded compensation rates based on study completion time and role
        return CompensationRates.CalculateCompensation(
            record.Rvu,
            record.Timestamp,
            _dataManager.Settings.Role);
    }

    // ===========================================
    // MOSAICTOOLS PIPE CLIENT
    // ===========================================

    /// <summary>
    /// Initialize and start the named pipe client for MosaicTools bridge.
    /// Called from MainWindow.Loaded.
    /// </summary>
    public void StartPipeClient()
    {
        if (_pipeClient != null) return;
        _pipeClient = new MosaicToolsPipeClient();

        _pipeClient.StudyEventReceived += OnPipeStudyEvent;
        _pipeClient.ConnectionStateChanged += OnPipeConnectionChanged;

        _pipeClient.Start();
    }

    /// <summary>
    /// Stop the pipe client. Called from MainWindow.Closing.
    /// </summary>
    public void StopPipeClient()
    {
        if (_pipeClient == null) return;
        _pipeClient.StudyEventReceived -= OnPipeStudyEvent;
        _pipeClient.ConnectionStateChanged -= OnPipeConnectionChanged;
        _pipeClient.Dispose();
        _pipeClient = null;
    }

    private void OnPipeConnectionChanged(bool connected)
    {
        // Marshal to UI thread
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsMosaicToolsPipeConnected = connected;
            if (connected)
            {
                Log.Information("MosaicTools pipe connected - will use pipe data instead of scraping");
                // Send current shift info immediately on connect
                SendShiftInfoToPipe();
            }
            else
            {
                Log.Information("MosaicTools pipe disconnected - falling back to own scraping");
            }
        });
    }

    private void OnPipeStudyEvent(PipeStudyEvent evt)
    {
        // Marshal to UI thread — these call into HandleMosaicToolsSigned/Unsigned
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            switch (evt.EventType)
            {
                case "signed":
                    HandleMosaicToolsSignedStudy(evt.Accession, evt.HasCritical);
                    break;
                case "unsigned":
                    HandleMosaicToolsUnsignedStudy(evt.Accession, evt.HasCritical);
                    break;
                default:
                    Log.Warning("Unknown pipe study event type: {EventType}", evt.EventType);
                    break;
            }
        });
    }

    /// <summary>
    /// Build a ScanResult from pipe study data (no scraping needed).
    /// </summary>
    private ScanResult? BuildScanResultFromPipe()
    {
        var pipeData = _pipeClient?.LatestStudyData;
        if (pipeData == null) return null;

        // Build patient class from Clario priority + class (e.g., "STAT" + "IP" → "Stat Inpatient")
        var patientClass = BuildPatientClassFromClario(pipeData.ClarioPriority, pipeData.ClarioClass);

        // Convert pipe data into MosaicStudyData
        var studyData = new Utils.MosaicStudyData
        {
            Accession = pipeData.Accession,
            Procedure = pipeData.Description,  // MT sends "description" for procedure text
            PatientClass = patientClass,
            PatientName = pipeData.PatientName,
            SiteCode = pipeData.SiteCode,
            Mrn = pipeData.Mrn
        };

        return new ScanResult(
            IsMosaicRunning: true,
            IsClarioRunning: false,
            CurrentAccession: pipeData.Accession,
            StudyData: !string.IsNullOrEmpty(pipeData.Accession) ? studyData : null,
            ClarioData: null);
    }

    /// <summary>
    /// Build normalized patient class from Clario priority and class fields.
    /// Delegates to ClarioExtractor.CombinePriorityAndClass which already handles
    /// ED/ER→Emergency normalization, urgency/location splitting, and deduplication.
    /// </summary>
    private static string BuildPatientClassFromClario(string? priority, string? clarioClass)
    {
        var result = Utils.ClarioExtractor.CombinePriorityAndClass(priority, clarioClass);
        return !string.IsNullOrEmpty(result) ? result : "Unknown";
    }

    /// <summary>
    /// Send current shift info to MosaicTools via pipe (if connected and values changed).
    /// </summary>
    private void SendShiftInfoToPipe()
    {
        if (_pipeClient == null || !_pipeClient.IsConnected) return;

        var totalRvu = TotalRvu;
        var recordCount = StudyCount;
        var currentHourRvu = ProjectedThisHour;
        var priorHourRvu = LastFullHourRvu;
        var estimatedTotalRvu = ProjectedShiftTotal;

        // Only send if values changed
        if (totalRvu == _lastSentTotalRvu && recordCount == _lastSentRecordCount &&
            currentHourRvu == _lastSentCurrentHourRvu && priorHourRvu == _lastSentPriorHourRvu &&
            estimatedTotalRvu == _lastSentEstimatedTotalRvu)
            return;

        _lastSentTotalRvu = totalRvu;
        _lastSentRecordCount = recordCount;
        _lastSentCurrentHourRvu = currentHourRvu;
        _lastSentPriorHourRvu = priorHourRvu;
        _lastSentEstimatedTotalRvu = estimatedTotalRvu;

        _pipeClient.SendShiftInfo(
            totalRvu,
            recordCount,
            _shiftStart?.ToString("o"),
            IsShiftActive,
            currentHourRvu,
            priorHourRvu,
            estimatedTotalRvu);
    }

    private bool _isScanning = false;

    private async void OnRefreshTick(object? sender, EventArgs e)
    {
        // Prevent overlapping scans
        if (_isScanning) return;
        _isScanning = true;

        try
        {
            // When MosaicTools pipe is connected, use pipe data instead of scraping
            if (_pipeClient != null && _pipeClient.IsConnected)
            {
                var pipeResult = BuildScanResultFromPipe();
                if (pipeResult != null)
                {
                    ApplyScanResults(pipeResult);
                    // Override after ApplyScanResults (which sets "Mosaic")
                    DataSourceIndicator = "MT Pipe";
                    // No Clario enrichment needed — pipe data already includes patient class
                    return;
                }
                // Pipe connected but no data yet — fall through to own scraping
            }

            // Fallback: Run Mosaic extraction on background thread
            var result = await Task.Run(() => PerformMosaicScan());

            // Apply Mosaic results on UI thread (no Clario delay)
            if (result != null)
            {
                ApplyScanResults(result);
            }
            else
            {
                // Extraction failed entirely - still let tracker count misses
                // so studies can complete via the grace period mechanism
                var completedStudies = _studyTracker.CheckCompleted(DateTime.Now, "");
                if (completedStudies.Count > 0)
                    ProcessCompletedStudies(completedStudies);
            }

            // Fire-and-forget: Clario patient class enrichment runs separately
            // so it never blocks the current study display.
            // Skip if already enriched (avoid re-scraping Clario every scan cycle).
            // Circuit breaker: skip if in backoff period after repeated failures.
            var accession = result?.CurrentAccession;
            if (!string.IsNullOrEmpty(accession) && result?.IsMosaicRunning == true)
            {
                bool alreadyCached;
                lock (_clarioCacheLock)
                {
                    alreadyCached = _clarioPatientClassCache.ContainsKey(accession);
                }
                if (!alreadyCached && DateTime.Now >= _clarioBackoffUntil)
                {
                    _ = Task.Run(() => PerformClarioEnrichment(accession));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error during Mosaic scan");
        }
        finally
        {
            _isScanning = false;
        }
    }

    // Track the last accession seen for detecting changes
    private string _lastSeenAccession = "";

    private record ScanResult(
        bool IsMosaicRunning,
        bool IsClarioRunning,
        string? CurrentAccession,
        Utils.MosaicStudyData? StudyData,
        Utils.ClarioPatientClassData? ClarioData);

    private ScanResult PerformMosaicScan()
    {
        // This runs on background thread — Mosaic only, no Clario (Clario runs separately)
        if (Utils.MosaicExtractor.IsMosaicRunning())
        {
            var studyData = Utils.MosaicExtractor.ExtractStudyData();
            var currentAccession = studyData?.Accession;
            return new ScanResult(true, false, currentAccession, studyData, null);
        }
        else if (Utils.ClarioExtractor.IsClarioRunning())
        {
            return new ScanResult(false, true, null, null, null);
        }

        return new ScanResult(false, false, null, null, null);
    }

    /// <summary>
    /// Runs Clario patient class extraction on a background thread, then applies
    /// the result on the UI thread. Separated from PerformMosaicScan so Mosaic
    /// results display immediately without waiting for Clario's slower extraction.
    /// </summary>
    private void PerformClarioEnrichment(string accession)
    {
        try
        {
            if (!Utils.ClarioExtractor.IsClarioRunning())
            {
                RecordClarioFailure();
                return;
            }

            var clarioData = Utils.ClarioExtractor.ExtractPatientClass(targetAccession: accession);
            if (clarioData == null || string.IsNullOrEmpty(clarioData.PatientClass))
            {
                RecordClarioFailure();
                return;
            }

            // Success — reset circuit breaker
            _clarioFailureCount = 0;
            _clarioBackoffUntil = DateTime.MinValue;

            Log.Debug("Clario enrichment for {Accession}: {PatientClass}",
                accession, clarioData.PatientClass);

            // Cache the result (thread-safe)
            lock (_clarioCacheLock)
            {
                _clarioPatientClassCache[accession] = clarioData.PatientClass;
                _lastClarioAccession = clarioData.Accession;
            }

            // Update UI on dispatcher thread if this accession is still current
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (CurrentAccession == accession)
                {
                    CurrentPatientClass = clarioData.PatientClass;
                    Log.Debug("Clario enriched current study: {PatientClass} for {Accession}",
                        clarioData.PatientClass, accession);
                }

                // Only update tracker if study is still active (not completed)
                if (_studyTracker.IsTracking(accession))
                {
                    var tracked = _studyTracker.GetStudy(accession);
                    if (tracked != null)
                    {
                        tracked.PatientClass = clarioData.PatientClass;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Clario enrichment failed for {Accession}", accession);
            RecordClarioFailure();
        }
    }

    /// <summary>
    /// Record a Clario enrichment failure and apply exponential backoff.
    /// Backoff: min(2^failures * 5s, 60s).
    /// </summary>
    private void RecordClarioFailure()
    {
        _clarioFailureCount++;
        double backoffSeconds = Math.Min(Math.Pow(2, _clarioFailureCount) * 5, 60);
        _clarioBackoffUntil = DateTime.Now.AddSeconds(backoffSeconds);
        Log.Debug("Clario circuit breaker: failure #{Count}, backing off for {Seconds:F0}s",
            _clarioFailureCount, backoffSeconds);
    }

    // Invalid procedure values that indicate "no study" (matches Python)
    private static readonly string[] InvalidProcedures = { "n/a", "na", "no report", "" };

    private void ApplyScanResults(ScanResult result)
    {
        // This runs on UI thread
        if (result.IsMosaicRunning)
        {
            DataSourceIndicator = "Mosaic";

            var studyData = result.StudyData;
            var currentAccession = result.CurrentAccession ?? "";
            var currentProcedure = studyData?.Procedure ?? "";
            var currentPatientClass = studyData?.PatientClass ?? "Unknown";
            var currentPatientName = studyData?.PatientName;
            var currentSiteCode = studyData?.SiteCode;
            var currentTime = DateTime.Now;

            // Update Mosaic activity time whenever we see a valid accession
            // This prevents the inactivity auto-end from firing while the user is actively reading
            if (!string.IsNullOrEmpty(currentAccession))
            {
                _lastMosaicActivityTime = currentTime;
            }

            // Store patient name, site code, MRN, and original accession in memory (never persisted to DB)
            if (!string.IsNullOrEmpty(currentAccession))
            {
                var hashedAccession = _dataManager.HashAccession(currentAccession);
                var currentMrn = studyData?.Mrn;
                lock (_patientInfoCacheLock)
                {
                    // Store original accession for Clario lookup
                    _originalAccessionCache[hashedAccession] = currentAccession;

                    if (!string.IsNullOrEmpty(currentPatientName))
                    {
                        _patientNameCache[hashedAccession] = currentPatientName;
                    }
                    if (!string.IsNullOrEmpty(currentSiteCode))
                    {
                        _siteCodeCache[hashedAccession] = currentSiteCode;
                    }
                    if (!string.IsNullOrEmpty(currentMrn))
                    {
                        _mrnCache[hashedAccession] = currentMrn;
                    }
                }
            }

            // Clario patient class enrichment runs asynchronously (PerformClarioEnrichment).
            // Check the cache here in case Clario already enriched this accession on a prior cycle.
            if ((currentPatientClass == "Unknown" || string.IsNullOrEmpty(currentPatientClass)) &&
                !string.IsNullOrEmpty(currentAccession))
            {
                lock (_clarioCacheLock)
                {
                    if (_clarioPatientClassCache.TryGetValue(currentAccession, out var cachedClass))
                    {
                        currentPatientClass = cachedClass;
                        Log.Debug("Enriched patient class from Clario cache: {PatientClass}", currentPatientClass);
                    }
                }
            }

            // Check if procedure is "n/a" or similar (matches Python logic)
            var isProcedureNA = string.IsNullOrWhiteSpace(currentProcedure) ||
                                InvalidProcedures.Contains(currentProcedure.ToLowerInvariant().Trim());

            // MULTI-ACCESSION HANDLING (Python parity)
            // Check if multiple accessions are visible simultaneously
            var allAccessions = studyData?.AllAccessions ?? new List<string>();
            var isMultiAccession = allAccessions.Count > 1;

            if (isMultiAccession && !_isMultiAccessionMode)
            {
                // Entering multi-accession mode
                _isMultiAccessionMode = true;
                _multiAccessionStartTime = currentTime;
                _currentMultiAccessionGroup = $"MAG-{currentTime:yyyyMMddHHmmss}";
                _multiAccessionData.Clear();

                Log.Information("Entering multi-accession mode with {Count} accessions: {Accessions}",
                    allAccessions.Count, string.Join(", ", allAccessions));

                // Track all accessions with their procedures
                foreach (var acc in allAccessions)
                {
                    if (!_multiAccessionData.ContainsKey(acc))
                    {
                        var (studyType, rvu) = StudyMatcher.MatchStudyType(
                            currentProcedure,
                            _dataManager.RvuTable,
                            _dataManager.ClassificationRules);

                        _multiAccessionData[acc] = new MultiAccessionStudy
                        {
                            Accession = acc,
                            Procedure = currentProcedure,
                            StudyType = studyType,
                            Rvu = rvu,
                            FirstSeen = currentTime,
                            PatientClass = currentPatientClass
                        };
                    }
                }
            }
            else if (!isMultiAccession && _isMultiAccessionMode)
            {
                // Exiting multi-accession mode - record all tracked studies
                Log.Information("Exiting multi-accession mode - recording {Count} studies", _multiAccessionData.Count);

                var totalDuration = _multiAccessionStartTime != null
                    ? (currentTime - _multiAccessionStartTime.Value).TotalSeconds
                    : 0;
                var durationPerStudy = _multiAccessionData.Count > 0
                    ? totalDuration / _multiAccessionData.Count
                    : 0;

                // Only record if duration meets minimum threshold
                if (durationPerStudy >= _dataManager.Settings.MinStudySeconds && _multiAccessionData.Count > 0)
                {
                    var currentShift = _dataManager.Database.GetCurrentShift();

                    foreach (var kvp in _multiAccessionData)
                    {
                        var maStudy = kvp.Value;
                        var record = new StudyRecord
                        {
                            Accession = _dataManager.HashAccession(maStudy.Accession),
                            Procedure = maStudy.Procedure,
                            StudyType = maStudy.StudyType,
                            Rvu = maStudy.Rvu,
                            Timestamp = maStudy.FirstSeen,
                            TimeFinished = currentTime,
                            PatientClass = maStudy.PatientClass,
                            DurationSeconds = durationPerStudy,
                            FromMultiAccession = true,
                            MultiAccessionGroup = _currentMultiAccessionGroup,
                            AccessionCount = _multiAccessionData.Count,
                            Source = "Mosaic"
                        };

                        if (currentShift != null && IsShiftActive)
                        {
                            // Check for duplicates
                            var existing = _dataManager.Database.FindRecordByAccession(currentShift.Id, record.Accession);
                            if (existing == null)
                            {
                                try
                                {
                                    _dataManager.Database.AddRecord(currentShift.Id, record);
                                    _studyTracker.MarkSeen(maStudy.Accession);
                                    Log.Information("Multi-accession record added: {Accession} -> {StudyType} ({Rvu} RVU), duration={Duration:F1}s",
                                        maStudy.Accession, maStudy.StudyType, maStudy.Rvu, durationPerStudy);
                                }
                                catch (Exception ex)
                                {
                                    Log.Error(ex, "Failed to add multi-accession record for {Accession} - study will be retried", maStudy.Accession);
                                }
                            }
                        }
                        else if (!IsShiftActive)
                        {
                            // Add to temporary studies
                            if (!_temporaryStudies.Any(s => s.Accession == record.Accession))
                            {
                                _temporaryStudies.Add(record);
                                Log.Information("Multi-accession temp study: {Accession} -> {StudyType}", maStudy.Accession, maStudy.StudyType);
                            }
                        }
                    }

                    CalculateAndUpdateStats();
                    StatusMessage = $"Multi: {_multiAccessionData.Count} studies ({_multiAccessionData.Sum(m => m.Value.Rvu):F1} RVU)";
                }
                else
                {
                    Log.Information("Dropped multi-accession studies - duration too short ({Duration:F1}s < {Min}s threshold)",
                        durationPerStudy, _dataManager.Settings.MinStudySeconds);
                }

                // Reset multi-accession state
                _isMultiAccessionMode = false;
                _multiAccessionStartTime = null;
                _currentMultiAccessionGroup = null;
                _multiAccessionData.Clear();

                // Don't continue normal processing - multi-accession handled separately
                _lastSeenAccession = "";  // Reset so next single-accession scan starts clean
                UpdateRecentStudiesDisplay();
                return;
            }
            else if (_isMultiAccessionMode && isMultiAccession)
            {
                // Still in multi-accession mode - update tracking with any new accessions
                foreach (var acc in allAccessions)
                {
                    if (!_multiAccessionData.ContainsKey(acc))
                    {
                        var (studyType, rvu) = StudyMatcher.MatchStudyType(
                            currentProcedure,
                            _dataManager.RvuTable,
                            _dataManager.ClassificationRules);

                        _multiAccessionData[acc] = new MultiAccessionStudy
                        {
                            Accession = acc,
                            Procedure = currentProcedure,
                            StudyType = studyType,
                            Rvu = rvu,
                            FirstSeen = currentTime,
                            PatientClass = currentPatientClass
                        };

                        Log.Debug("Added new accession to multi-accession group: {Accession}", acc);
                    }
                }

                // Update current study display for multi-accession mode
                CurrentAccession = $"{allAccessions.Count} studies";
                CurrentProcedure = currentProcedure;
                CurrentPatientClass = currentPatientClass;
                CurrentStudyType = "Multiple";
                CurrentStudyRvu = _multiAccessionData.Sum(m => m.Value.Rvu);
                CurrentDuration = _multiAccessionStartTime != null
                    ? FormatDurationWithAvg((currentTime - _multiAccessionStartTime.Value).TotalSeconds, "Multiple")
                    : "";
                IsCurrentStudyAlreadyRecorded = false;
                CurrentAccessionDisplay = $"Multi: {allAccessions.Count}";
                CurrentAccessionColor = System.Windows.Media.Brushes.Orange;

                _lastSeenAccession = string.Join(",", allAccessions);
                return;  // Don't continue normal single-accession processing
            }

            // STEP 1: Check for completed studies (Python-like logic)
            // Study is completed when:
            // 1. Current accession is different from what we were tracking, OR
            // 2. No accession is visible (study closed), OR
            // 3. Procedure changed to "n/a" (matches Python - complete all active studies)
            List<TrackedStudy> completedStudies;
            if (isProcedureNA && _studyTracker.TrackedCount > 0)
            {
                // Procedure is n/a - complete all active studies (Python behavior)
                Log.Information("Procedure is N/A - completing all {Count} active studies", _studyTracker.TrackedCount);
                completedStudies = _studyTracker.CheckCompleted(currentTime, "");
            }
            else
            {
                completedStudies = _studyTracker.CheckCompleted(currentTime, currentAccession);
            }

            // STEP 2: Process completed studies - add to database
            var shift = _dataManager.Database.GetCurrentShift();
            foreach (var completed in completedStudies)
            {
                // Study should already have study type and RVU from when it was added
                // But re-classify in case procedure was updated
                var (studyType, rvu) = StudyMatcher.MatchStudyType(
                    completed.Procedure,
                    _dataManager.RvuTable,
                    _dataManager.ClassificationRules);

                // Use the better values (prefer non-Unknown)
                if (studyType == "Unknown" && completed.StudyType != "Unknown")
                    studyType = completed.StudyType;
                if (rvu == 0 && completed.Rvu > 0)
                    rvu = completed.Rvu;

                // Enrich patient class from Clario cache if Unknown (like Python)
                var patientClassToRecord = completed.PatientClass;
                if (string.IsNullOrEmpty(patientClassToRecord) || patientClassToRecord == "Unknown")
                {
                    lock (_clarioCacheLock)
                    {
                        if (_clarioPatientClassCache.TryGetValue(completed.Accession, out var cachedClass))
                        {
                            patientClassToRecord = cachedClass;
                            Log.Debug("Enriched completed study with cached Clario patient class: {PatientClass}", cachedClass);
                        }
                    }
                }

                var record = new StudyRecord
                {
                    Accession = _dataManager.HashAccession(completed.Accession),
                    Procedure = completed.Procedure,
                    StudyType = studyType,
                    Rvu = rvu,
                    Timestamp = completed.FirstSeen,  // When study was started
                    TimeFinished = completed.CompletedAt ?? currentTime,  // When study was completed
                    PatientClass = patientClassToRecord ?? "Unknown",
                    DurationSeconds = completed.Duration,
                    Source = "Mosaic"
                };

                if (shift != null && IsShiftActive)
                {
                    // Check if already exists in active shift
                    var existing = _dataManager.Database.FindRecordByAccession(shift.Id, record.Accession);
                    if (existing != null)
                    {
                        // Update duration and time_finished if higher (Python behavior)
                        if (record.DurationSeconds > (existing.DurationSeconds ?? 0))
                        {
                            existing.DurationSeconds = record.DurationSeconds;
                            existing.TimeFinished = record.TimeFinished;  // Also update time_finished
                            _dataManager.Database.UpdateRecord(existing);
                            Log.Information("Updated study duration: {Accession} -> {Duration:F1}s",
                                completed.Accession, record.DurationSeconds);
                        }
                        continue;
                    }

                    // MosaicTools integration: route to pending queue if enabled
                    if (_dataManager.Settings.MosaicToolsIntegrationEnabled)
                    {
                        AddToPendingQueue(record, completed.Accession);
                    }
                    else
                    {
                        // Direct add to database (original behavior)
                        try
                        {
                            _dataManager.Database.AddRecord(shift.Id, record);
                            _studyTracker.MarkSeen(completed.Accession);

                            // Record for fatigue detection
                            if (completed.Duration > 0)
                                _fatigueDetector.RecordStudy(DateTime.Now, completed.Duration);

                            if (!_dataManager.Settings.MosaicToolsIntegrationEnabled)
                                StatusMessage = $"Counted: {studyType} ({rvu:F1} RVU)";
                            Log.Information("Auto-counted study: {Accession} -> {StudyType} ({Rvu} RVU), Duration: {Duration:F1}s",
                                completed.Accession, studyType, rvu, completed.Duration);

                            CalculateAndUpdateStats();
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Failed to add record for {Accession} - study will be retried", completed.Accession);
                        }
                    }
                }
                else
                {
                    // No active shift - route through pending queue if MosaicTools enabled
                    if (_dataManager.Settings.MosaicToolsIntegrationEnabled)
                    {
                        AddToPendingQueue(record, completed.Accession);
                    }
                    else
                    {
                        // Direct add to temporary studies
                        if (!_temporaryStudies.Any(s => s.Accession == record.Accession))
                        {
                            _temporaryStudies.Add(record);
                            StatusMessage = $"Temp: {studyType} ({rvu:F1} RVU) - No shift active";
                            Log.Information("Added temporary study (no shift): {Accession} -> {StudyType} ({Rvu} RVU)",
                                completed.Accession, studyType, rvu);

                            UpdateRecentStudiesDisplay();
                        }
                    }
                }
            }

            // STEP 3: Update current study display
            if (!string.IsNullOrEmpty(currentAccession))
            {
                CurrentAccession = currentAccession;
                CurrentProcedure = !string.IsNullOrEmpty(currentProcedure) ? currentProcedure : "-";
                CurrentPatientClass = currentPatientClass;
                CurrentPatientName = currentPatientName ?? "";
                CurrentSiteCode = currentSiteCode ?? "";

                // Check if study is already recorded (like Python's "already recorded" display)
                // Using 'shift' from STEP 2 above
                var hashedAccession = _dataManager.HashAccession(currentAccession);
                var existingRecord = shift != null
                    ? _dataManager.Database.FindRecordByAccession(shift.Id, hashedAccession)
                    : null;
                IsCurrentStudyAlreadyRecorded = existingRecord != null;
                CurrentAccessionDisplay = IsCurrentStudyAlreadyRecorded ? "already recorded" : currentAccession;
                // Red (#C62828) for "already recorded", gray otherwise (like Python)
                CurrentAccessionColor = IsCurrentStudyAlreadyRecorded
                    ? new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28))
                    : Brushes.Gray;

                // Match study type for display
                var (studyType, rvu) = StudyMatcher.MatchStudyType(
                    currentProcedure,
                    _dataManager.RvuTable,
                    _dataManager.ClassificationRules);
                CurrentStudyType = studyType;
                CurrentStudyRvu = rvu;

                // STEP 4: Add/update current study in tracker
                // Always add when we have a valid accession, even with empty procedure.
                // The tracker's AddStudy() handles updating procedure on subsequent cycles.
                // Only skip when procedure is explicitly N/A (which triggers completion above).
                if (!isProcedureNA)
                {
                    _studyTracker.AddStudy(
                        currentAccession,
                        currentProcedure,  // may be empty - tracker handles updates later
                        currentTime,
                        _dataManager.RvuTable,
                        _dataManager.ClassificationRules,
                        currentPatientClass);

                    // Update tracker duration display
                    var trackedStudy = _studyTracker.GetStudy(currentAccession);
                    if (trackedStudy != null)
                    {
                        CurrentDuration = FormatDurationWithAvg(trackedStudy.TrackedSeconds, CurrentStudyType);

                        // Check if study has been open too long → send distraction alert
                        CheckDistractionAlert(currentAccession, CurrentStudyType, trackedStudy.TrackedSeconds);
                    }
                    else
                    {
                        CurrentDuration = "";
                    }
                }
                else
                {
                    // Procedure is n/a - don't track, clear duration
                    CurrentDuration = "";
                }

                _lastSeenAccession = currentAccession;
            }
            else
            {
                // No current study visible
                ClearCurrentStudy();
                _lastSeenAccession = "";
            }
        }
        else if (result.IsClarioRunning)
        {
            DataSourceIndicator = "Clario";
            // When switching to Clario, complete any Mosaic studies
            var completedStudies = _studyTracker.CheckCompleted(DateTime.Now, "");
            ProcessCompletedStudies(completedStudies);
        }
        else
        {
            DataSourceIndicator = "detecting...";
            ClearCurrentStudy();
            // When no source detected, complete any active studies
            var completedStudies = _studyTracker.CheckCompleted(DateTime.Now, "");
            ProcessCompletedStudies(completedStudies);
            _lastSeenAccession = "";
        }
    }

    /// <summary>
    /// Formats seconds as "Xm Xs" or "Xs" (matches Python / DurationConverter).
    /// </summary>
    private static string FormatDuration(double totalSeconds)
    {
        var secs = (int)totalSeconds;
        if (secs < 60)
            return $"{secs}s";
        return $"{secs / 60}m {secs % 60}s";
    }

    /// <summary>
    /// Formats current duration with optional avg duration when distraction alert is enabled.
    /// Example: "(1m 23s  avg 2m 05s)" or "(1m 23s)" if disabled/no avg.
    /// </summary>
    private string FormatDurationWithAvg(double totalSeconds, string studyType)
    {
        var current = FormatDuration(totalSeconds);
        if (_dataManager.Settings.DistractionAlertEnabled &&
            _avgDurationByStudyType.TryGetValue(studyType, out var avgSeconds) && avgSeconds > 0)
        {
            var avgSecs = (int)avgSeconds;
            var avg = avgSecs < 60 ? $"{avgSecs}s" : $"{avgSecs / 60}m {avgSecs % 60:D2}s";
            return $"({current}  avg {avg})";
        }
        return $"({current})";
    }

    /// <summary>
    /// Process completed studies and add them to database or temp storage.
    /// </summary>
    private void ProcessCompletedStudies(List<TrackedStudy> completedStudies)
    {
        if (completedStudies.Count == 0) return;

        // Clear redo buffer when new studies are being recorded (Python parity)
        ClearRedoBuffer();

        var shift = _dataManager.Database.GetCurrentShift();
        var currentTime = DateTime.Now;

        foreach (var completed in completedStudies)
        {
            var (studyType, rvu) = StudyMatcher.MatchStudyType(
                completed.Procedure,
                _dataManager.RvuTable,
                _dataManager.ClassificationRules);

            // Use better values
            if (studyType == "Unknown" && completed.StudyType != "Unknown")
                studyType = completed.StudyType;
            if (rvu == 0 && completed.Rvu > 0)
                rvu = completed.Rvu;

            // Enrich patient class from Clario cache if Unknown (like Python)
            var patientClassToRecord = completed.PatientClass;
            if (string.IsNullOrEmpty(patientClassToRecord) || patientClassToRecord == "Unknown")
            {
                lock (_clarioCacheLock)
                {
                    if (_clarioPatientClassCache.TryGetValue(completed.Accession, out var cachedClass))
                    {
                        patientClassToRecord = cachedClass;
                        Log.Debug("Enriched completed study with cached Clario patient class: {PatientClass}", cachedClass);
                    }
                }
            }

            var record = new StudyRecord
            {
                Accession = _dataManager.HashAccession(completed.Accession),
                Procedure = completed.Procedure,
                StudyType = studyType,
                Rvu = rvu,
                Timestamp = completed.FirstSeen,  // When study was started
                TimeFinished = completed.CompletedAt ?? currentTime,  // When study was completed
                PatientClass = patientClassToRecord ?? "Unknown",
                DurationSeconds = completed.Duration,
                Source = "Mosaic"
            };

            if (shift != null && IsShiftActive)
            {
                var existing = _dataManager.Database.FindRecordByAccession(shift.Id, record.Accession);
                if (existing != null)
                {
                    if (record.DurationSeconds > (existing.DurationSeconds ?? 0))
                    {
                        existing.DurationSeconds = record.DurationSeconds;
                        existing.TimeFinished = record.TimeFinished;  // Also update time_finished
                        _dataManager.Database.UpdateRecord(existing);
                    }
                    continue;
                }

                // MosaicTools integration: route to pending queue if enabled
                if (_dataManager.Settings.MosaicToolsIntegrationEnabled)
                {
                    AddToPendingQueue(record, completed.Accession);
                }
                else
                {
                    // Direct add to database (original behavior)
                    try
                    {
                        _dataManager.Database.AddRecord(shift.Id, record);
                        _studyTracker.MarkSeen(completed.Accession);
                        if (!_dataManager.Settings.MosaicToolsIntegrationEnabled)
                            StatusMessage = $"Counted: {studyType} ({rvu:F1} RVU)";
                        Log.Information("Auto-counted study: {Accession} -> {StudyType} ({Rvu} RVU)",
                            completed.Accession, studyType, rvu);

                        CalculateAndUpdateStats();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to add record for {Accession} - study will be retried", completed.Accession);
                    }
                }
            }
            else
            {
                // No active shift - route through pending queue if MosaicTools enabled
                if (_dataManager.Settings.MosaicToolsIntegrationEnabled)
                {
                    AddToPendingQueue(record, completed.Accession);
                }
                else
                {
                    // Direct add to temporary studies
                    if (!_temporaryStudies.Any(s => s.Accession == record.Accession))
                    {
                        _temporaryStudies.Add(record);
                        StatusMessage = $"Temp: {studyType} ({rvu:F1} RVU) - No shift active";
                        Log.Information("Added temporary study (no shift): {Accession} -> {StudyType} ({Rvu} RVU)",
                            completed.Accession, studyType, rvu);

                        UpdateRecentStudiesDisplay();
                    }
                }
            }
        }
    }

    private void ClearCurrentStudy()
    {
        CurrentAccession = "-";
        CurrentAccessionDisplay = "-";
        IsCurrentStudyAlreadyRecorded = false;
        CurrentAccessionColor = Brushes.Gray;
        CurrentProcedure = "-";
        CurrentPatientClass = "-";
        CurrentPatientName = "";
        CurrentSiteCode = "";
        CurrentStudyType = "-";
        CurrentStudyRvu = 0;
        CurrentDuration = "";
    }

    /// <summary>
    /// Update the Recent Studies display based on current state.
    /// Shows temporary studies (red) when no shift is active, or shift studies (gray) when active.
    /// </summary>
    private void UpdateRecentStudiesDisplay()
    {
        if (IsShiftActive)
        {
            // Active shift - show normal label with gray color
            // Recent studies come from the database in CalculateAndUpdateStats
            // which already handles critical filter and updates the label
            CalculateAndUpdateStats();
        }
        else
        {
            // Update critical results count for temporary studies
            CriticalResultsCount = _temporaryStudies.Count(s => s.HasCriticalResult);

            // No active shift - show temp studies with red warning
            if (_temporaryStudies.Count > 0)
            {
                RecentStudiesLabel = ShowCriticalOnly ? "Critical (Temp)" : "Temporary";
                RecentStudiesLabelColor = ShowCriticalOnly ? Brushes.OrangeRed : Brushes.Red;

                // Apply critical filter for temp studies
                var displayStudies = ShowCriticalOnly
                    ? _temporaryStudies.Where(s => s.HasCriticalResult)
                    : _temporaryStudies;

                // Show temp studies in the recent list (no limit)
                RecentStudies.Clear();
                foreach (var record in displayStudies.OrderByDescending(r => r.Timestamp))
                {
                    RecentStudies.Add(record);
                }
                StudyCount = _temporaryStudies.Count;
            }
            else
            {
                RecentStudiesLabel = "Recent Studies";
                RecentStudiesLabelColor = Application.Current.TryFindResource("SecondaryTextBrush") as Brush ?? Brushes.Gray;
                RecentStudies.Clear();
                StudyCount = 0;
                CriticalResultsCount = 0;
            }
        }
    }

    private void OnStatsTick(object? sender, EventArgs e)
    {
        // Lightweight update for time-based metrics (runs every 5 seconds)
        if (IsShiftActive && _shiftStart != null)
        {
            CalculateAndUpdateStats();

            // Check for inactivity auto-end (Python parity)
            // If no studies recorded AND no Mosaic activity for InactivityThresholdSeconds (1 hour), auto-end shift
            // Use the most recent of _lastStudyRecordedTime and _lastMosaicActivityTime
            // so that actively working on a study (visible in Mosaic) prevents auto-end
            var lastActivity = _lastStudyRecordedTime;
            if (_lastMosaicActivityTime != null && (lastActivity == null || _lastMosaicActivityTime > lastActivity))
                lastActivity = _lastMosaicActivityTime;
            if (lastActivity != null)
            {
                var secondsSinceLastActivity = (DateTime.Now - lastActivity.Value).TotalSeconds;
                if (secondsSinceLastActivity >= InactivityThresholdSeconds)
                {
                    Log.Information("Auto-ending shift due to {Seconds:F0}s of inactivity (threshold: {Threshold}s)",
                        secondsSinceLastActivity, InactivityThresholdSeconds);

                    // End shift at the time of the last study
                    _dataManager.Database.EndCurrentShift(_lastStudyRecordedTime.Value);
                    IsShiftActive = false;
                    ShiftStatus = "Auto-ended (inactive)";
                    _shiftStart = null;
                    _effectiveShiftStart = null;
                    _projectedShiftEnd = null;
                    _lastStudyRecordedTime = null;
                    _lastMosaicActivityTime = null;

                    _statsTimer.Stop();
                    _fatigueTimer.Stop();
                    _fatigueDetector.EndShift();
                    ShowFatigueWarning = false;
                    ResetStats();
                    UpdateRecentStudiesDisplay();
                    StatusMessage = "Shift auto-ended due to inactivity";
                }
            }
        }
    }

    private void OnFatigueTick(object? sender, EventArgs e)
    {
        // Only check fatigue during active shift
        if (!IsShiftActive) return;

        var analysis = _fatigueDetector.Analyze();
        if (analysis.FatigueDetected)
        {
            FatigueWarningMessage = analysis.AlertMessage;
            ShowFatigueWarning = true;
            Log.Information("Fatigue warning displayed: {Message}", analysis.AlertMessage);
        }
    }

    private async void OnBackupTick(object? sender, EventArgs e)
    {
        // Check if scheduled backup is due (hourly/daily)
        if (_backupManager.ShouldBackupNow(_dataManager.Settings))
        {
            Log.Information("Scheduled backup triggered");
            try
            {
                var result = await _backupManager.CreateBackupAsync("scheduled");
                if (result.Success)
                {
                    Log.Information("Scheduled backup completed: {Message}", result.Message);
                }
                else
                {
                    Log.Warning("Scheduled backup failed: {Message}", result.Message);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Scheduled backup error");
            }
        }
    }

    /// <summary>
    /// Trigger backup on shift end (if cloud backup enabled with shift_end schedule).
    /// </summary>
    private async Task TriggerShiftEndBackupAsync()
    {
        var settings = _dataManager.Settings;
        if (!settings.CloudBackupEnabled)
            return;

        var schedule = settings.BackupSchedule?.ToLowerInvariant() ?? "shift_end";
        if (schedule != "shift_end")
            return;

        Log.Information("Triggering shift-end backup");
        try
        {
            var result = await _backupManager.CreateBackupAsync("shift_end");
            if (result.Success)
            {
                Log.Information("Shift-end backup completed: {Message}", result.Message);
                StatusMessage = "Backup saved";
            }
            else
            {
                Log.Warning("Shift-end backup failed: {Message}", result.Message);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Shift-end backup error");
        }
    }

    [RelayCommand]
    private void StartShift()
    {
        if (IsShiftActive) return;

        // Check for temporary studies
        bool keepTempStudies = false;
        DateTime? retroactiveStart = null;

        if (_temporaryStudies.Count > 0)
        {
            var totalRvu = _temporaryStudies.Sum(s => s.Rvu);
            var result = MessageBox.Show(
                $"You have {_temporaryStudies.Count} temporary studies ({totalRvu:F1} RVU) recorded without a shift.\n\n" +
                "Would you like to add them to the new shift?\n\n" +
                "• Yes - Add studies to the new shift\n" +
                "• No - Discard temporary studies",
                "Temporary Studies Found",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel)
            {
                Log.Information("Shift start cancelled by user");
                return;
            }

            keepTempStudies = (result == MessageBoxResult.Yes);

            // If keeping temp studies with significant RVU, retroactively extend shift
            if (keepTempStudies && totalRvu > 5.0)
            {
                var earliestTime = _temporaryStudies.Min(s => s.Timestamp);
                retroactiveStart = earliestTime.Date.Add(new TimeSpan(earliestTime.Hour, earliestTime.Minute, 0));
                Log.Information("Retroactively extending shift start to {Start} based on {Rvu:F1} RVU in temporary studies",
                    retroactiveStart, totalRvu);
            }

            if (!keepTempStudies)
            {
                Log.Information("User chose to discard {Count} temporary studies", _temporaryStudies.Count);
            }
            else
            {
                Log.Information("User chose to add {Count} temporary studies to new shift", _temporaryStudies.Count);
            }
        }

        var now = retroactiveStart ?? DateTime.Now;

        // Calculate effective shift start (round to hour if within 15 minutes)
        DateTime effectiveStart;
        if (now.Minute <= 15)
        {
            effectiveStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
        }
        else
        {
            effectiveStart = now;
        }

        var projectedEnd = effectiveStart.AddHours(_dataManager.Settings.ShiftLengthHours);

        _dataManager.Database.StartShift(now, effectiveStart, projectedEnd);
        _shiftStart = now;
        _effectiveShiftStart = effectiveStart;
        _projectedShiftEnd = projectedEnd;

        // Add temporary studies to the new shift
        if (keepTempStudies)
        {
            var shift = _dataManager.Database.GetCurrentShift();
            if (shift != null)
            {
                foreach (var tempStudy in _temporaryStudies)
                {
                    try
                    {
                        _dataManager.Database.AddRecord(shift.Id, tempStudy);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to add temporary study {Accession} to new shift", tempStudy.Accession);
                    }
                }
                Log.Information("Added {Count} temporary studies to new shift", _temporaryStudies.Count);
            }
        }

        // Clear temporary studies regardless of choice
        _temporaryStudies.Clear();

        IsShiftActive = true;
        ShiftStatus = $"Shift: {now:h:mm tt}";

        _refreshTimer.Start();
        _statsTimer.Start();
        _fatigueTimer.Start();
        _fatigueDetector.StartNewShift(now);
        ShowFatigueWarning = false;

        // Pre-load average durations for distraction alert
        RefreshAverageDurationCache();

        CalculateAndUpdateStats();
        UpdateRecentStudiesDisplay();
        StatusMessage = "Shift started";
        Log.Information("Shift started at {Time}", now);
    }

    [RelayCommand]
    private async Task EndShiftAsync()
    {
        if (!IsShiftActive) return;

        _dataManager.Database.EndCurrentShift();
        IsShiftActive = false;
        ShiftStatus = "No Active Shift";
        _shiftStart = null;
        _effectiveShiftStart = null;
        _projectedShiftEnd = null;

        // Don't stop refresh timer - we still want to track temp studies
        _statsTimer.Stop();
        _fatigueTimer.Stop();
        _fatigueDetector.EndShift();
        ShowFatigueWarning = false;

        ResetStats();
        UpdateRecentStudiesDisplay();
        StatusMessage = "Shift ended";
        Log.Information("Shift ended");

        // Trigger backup after shift ends (if enabled)
        await TriggerShiftEndBackupAsync();
    }

    [RelayCommand]
    private void AddManualStudy()
    {
        var shift = _dataManager.Database.GetCurrentShift();
        if (shift == null)
        {
            MessageBox.Show("Please start a shift first.", "No Active Shift",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // TODO: Open manual study entry dialog
        var now = DateTime.Now;
        var record = new StudyRecord
        {
            Accession = _dataManager.HashAccession($"MANUAL-{now:HHmmss}"),
            Procedure = "Manual Entry",
            StudyType = "CT Other",
            Rvu = 1.0,
            Timestamp = now,
            TimeFinished = now,  // Manual entry - instant completion
            Source = "Manual"
        };

        _dataManager.Database.AddRecord(shift.Id, record);
        CalculateAndUpdateStats();
        StatusMessage = "Study added manually";
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var settingsWindow = new Views.SettingsWindow(_dataManager, this)
        {
            Owner = Application.Current.MainWindow
        };
        
        if (settingsWindow.ShowDialog() == true)
        {
            // RefreshSettings is called by SettingsWindow on save
            StatusMessage = "Settings saved";
        }
    }

    [RelayCommand]
    private void OpenStatistics()
    {
        var statsWindow = new Views.StatisticsWindow(_dataManager)
        {
            Owner = Application.Current.MainWindow
        };
        statsWindow.Show();
    }

    [RelayCommand]
    private void OpenTools()
    {
        var toolsWindow = new Views.ToolsWindow(_dataManager, () => 
        {
            // Callback when database changes (e.g. manual entry or repair)
            CalculateAndUpdateStats();
        })
        {
            Owner = Application.Current.MainWindow
        };
        toolsWindow.ShowDialog();
    }

    [RelayCommand]
    private void DeleteStudy(StudyRecord? study)
    {
        if (study == null) return;

        // Show confirmation dialog
        var result = System.Windows.MessageBox.Show(
            $"Delete this study?\n\n{study.Procedure}\n{study.Rvu:F1} RVU",
            "Confirm Delete",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (result != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        // Check if this is a temporary study (not in database)
        var tempStudy = _temporaryStudies.FirstOrDefault(s =>
            s.Accession == study.Accession && s.Timestamp == study.Timestamp);

        if (tempStudy != null)
        {
            // Remove from temporary studies list
            _temporaryStudies.Remove(tempStudy);
            RecentStudies.Remove(study);
            StudyCount = _temporaryStudies.Count;
            StatusMessage = "Temporary study removed";
            Log.Debug("Removed temporary study: {Accession}", study.Accession);
            UpdateRecentStudiesDisplay();
        }
        else if (study.Id > 0)
        {
            // Database study - delete from database
            _dataManager.Database.DeleteRecord(study.Id);
            CalculateAndUpdateStats();
            StatusMessage = "Study deleted";
            Log.Debug("Deleted study from database: {Id}", study.Id);
        }
        else
        {
            // Fallback: just remove from observable collection
            RecentStudies.Remove(study);
            StatusMessage = "Study removed";
        }
    }

    // Event to notify UI to scroll to top when new study is added
    public event EventHandler? ScrollToTopRequested;

    private void RequestScrollToTop()
    {
        ScrollToTopRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task OpenInClario(StudyRecord? study)
    {
        Log.Debug("OpenInClario called with study: {Study}", study?.Procedure ?? "null");

        if (study == null) return;

        // Get the original (unhashed) accession from memory cache
        var originalAccession = GetOriginalAccession(study.Accession);
        Log.Debug("OpenInClario: hashed={Hashed}, original={Original}",
            study.Accession, originalAccession ?? "NOT FOUND");

        if (string.IsNullOrEmpty(originalAccession))
        {
            Log.Warning("Cannot open in Clario: original accession not in memory cache for {HashedAccession}",
                study.Accession);
            StatusMessage = "Cannot open - accession not in memory";
            return;
        }

        // Try to open via UI automation (search and click in Clario)
        StatusMessage = "Opening in Clario...";
        Log.Debug("OpenInClario: Calling ClarioLauncher.OpenStudyByAccession");
        if (await Utils.ClarioLauncher.OpenStudyByAccession(originalAccession))
        {
            StatusMessage = $"Opened in Clario";
            Log.Information("Opened study via UI automation: {Accession}", originalAccession);
        }
        else
        {
            StatusMessage = "Could not open study in Clario";
            Log.Warning("Failed to open study via UI automation: {Accession}", originalAccession);
        }
    }

    [RelayCommand]
    private void ChangePaceComparison(string mode)
    {
        try
        {
            PaceComparisonMode = mode;
            _dataManager.Settings.PaceComparisonMode = mode;
            _dataManager.SaveSettings();

            // Update pace description based on mode
            PaceDescription = mode switch
            {
                "prior" => "vs Prior Shift",
                "goal" => "vs Daily Goal",
                "best_week" => "vs Best This Week",
                "best_ever" => "vs Best Ever",
                "week_1" => "vs 1 Week Ago",
                "week_2" => "vs 2 Weeks Ago",
                "week_3" => "vs 3 Weeks Ago",
                "week_4" => "vs 4 Weeks Ago",
                _ when mode.StartsWith("week_") => $"vs This Week",
                _ => "vs Prior Shift"
            };

            // Recalculate pace car with new comparison
            if (IsShiftActive && _shiftStart != null && _effectiveShiftStart != null)
            {
                var hoursElapsed = (DateTime.Now - _effectiveShiftStart.Value).TotalHours;
                UpdatePaceCar(hoursElapsed);
            }

            Log.Information("Changed pace comparison mode to {Mode}", mode);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in ChangePaceComparison for mode {Mode}", mode);
        }
    }

    [RelayCommand]
    private void UndoLast()
    {
        if (_undoUsed && _lastUndoneStudy != null)
        {
            // REDO - restore the study
            if (_lastUndoneShiftId > 0)
            {
                // Restore to database
                _dataManager.Database.AddRecord(_lastUndoneShiftId, _lastUndoneStudy);
                Log.Information("Redo: Restored study {Accession} to database", _lastUndoneStudy.Accession);
            }
            else if (_lastUndoneStudy.ShiftId == 0)
            {
                // Restore to temporary studies
                _temporaryStudies.Add(_lastUndoneStudy);
                Log.Information("Redo: Restored temporary study {Accession}", _lastUndoneStudy.Accession);
            }

            _lastUndoneStudy = null;
            _lastUndoneShiftId = 0;
            _undoUsed = false;
            UndoButtonText = "Undo";
            StatusMessage = "Study restored";
            CalculateAndUpdateStats();
        }
        else if (RecentStudies.Count > 0 && !_undoUsed)
        {
            // UNDO - remove last study and store for potential redo
            var lastStudy = RecentStudies.FirstOrDefault();
            if (lastStudy == null) return;

            // Clone the study before removing
            _lastUndoneStudy = lastStudy.Clone();

            // Check if this is a temporary study
            var tempStudy = _temporaryStudies.FirstOrDefault(s =>
                s.Accession == lastStudy.Accession && s.Timestamp == lastStudy.Timestamp);

            if (tempStudy != null)
            {
                // Remove from temporary studies
                _temporaryStudies.Remove(tempStudy);
                RecentStudies.Remove(lastStudy);
                _lastUndoneShiftId = 0;  // Mark as temporary
                StudyCount = _temporaryStudies.Count;
                Log.Debug("Undo: Removed temporary study {Accession}", lastStudy.Accession);
                UpdateRecentStudiesDisplay();
            }
            else if (lastStudy.Id > 0)
            {
                // Remove from database
                _lastUndoneShiftId = lastStudy.ShiftId;
                _dataManager.Database.DeleteRecord(lastStudy.Id);
                Log.Debug("Undo: Removed database study {Id}", lastStudy.Id);
                CalculateAndUpdateStats();
            }
            else
            {
                // Fallback: just remove from observable collection
                RecentStudies.Remove(lastStudy);
                _lastUndoneShiftId = 0;
            }

            _undoUsed = true;
            UndoButtonText = "Redo";
            StatusMessage = "Study undone - click Redo to restore";
        }
    }

    /// <summary>
    /// Clears the redo buffer. Call this when a new study is recorded.
    /// </summary>
    private void ClearRedoBuffer()
    {
        if (_lastUndoneStudy != null)
        {
            _lastUndoneStudy = null;
            _lastUndoneShiftId = 0;
            _undoUsed = false;
            UndoButtonText = "Undo";
        }
    }

    partial void OnIsAlwaysOnTopChanged(bool value)
    {
        _dataManager.Settings.AlwaysOnTop = value;
        _dataManager.SaveSettings();
    }

    public void SaveWindowPosition(double x, double y, double width, double height)
    {
        _dataManager.SaveWindowPosition("main", x, y, width, height);
    }

    public WindowPosition? GetSavedWindowPosition()
    {
        return _dataManager.Settings.MainWindowPosition;
    }

    // ===========================================
    // MOSAICTOOLS INTEGRATION
    // ===========================================

    /// <summary>
    /// Handle a "study signed" message from MosaicTools.
    /// Moves the study from pending to database.
    /// </summary>
    public void HandleMosaicToolsSignedStudy(string accession, bool hasCritical = false)
    {
        // Always show message for debugging/testing, even if integration disabled
        var criticalIndicator = hasCritical ? " ⚠️" : "";
        StatusMessage = $"✅ Signed{criticalIndicator}";
        Log.Information("MosaicTools received SIGNED{Critical} message for: {Accession}",
            hasCritical ? " (CRITICAL)" : "", accession);

        if (!_dataManager.Settings.MosaicToolsIntegrationEnabled)
        {
            StatusMessage = $"✅ Signed (off){criticalIndicator}";
            return;
        }

        lock (_pendingStudiesLock)
        {
            if (_pendingStudies.TryGetValue(accession, out var pending))
            {
                _pendingStudies.Remove(accession);

                // Apply critical result flag
                if (hasCritical)
                {
                    pending.Record.HasCriticalResult = true;
                    Log.Information("MosaicTools confirmed SIGNED with CRITICAL RESULT - adding to database: {Accession}", accession);
                    StatusMessage = $"✅ +{pending.Record.StudyType} ⚠️";
                }
                else
                {
                    Log.Information("MosaicTools confirmed SIGNED - adding to database: {Accession}", accession);
                    StatusMessage = $"✅ +{pending.Record.StudyType}";
                }

                // Add to database
                AddPendingStudyToDatabase(pending);
            }
            else
            {
                // Study not found in pending queue.
                // Check if it's still being actively tracked (user still reading it).
                // If so, just store the signed/critical flags for when it naturally completes.
                if (_studyTracker.IsTracking(accession))
                {
                    // Study is still visible in Mosaic — this is a spurious or early
                    // SIGNED message. Store the critical flag so it can be applied when
                    // the study naturally completes, but do NOT create a pre-emptive
                    // entry with a fallback timer.
                    lock (_preEmptiveSignedLock)
                    {
                        _preEmptiveSigned[accession] = (DateTime.Now, hasCritical);
                    }
                    Log.Information("MosaicTools SIGNED for actively-tracked study (still visible) - stored for later: {Accession}", accession);
                    StatusMessage = $"✅ Signed (tracking){criticalIndicator}";
                    // No timer needed — the study will be processed when it leaves the tracker
                    return;
                }

                // True pre-emptive: study is not in pending and not in tracker.
                // MosaicTools detected the study change before our extraction did.
                // Store it so we add immediately when detected (no timeout wait).
                lock (_preEmptiveSignedLock)
                {
                    _preEmptiveSigned[accession] = (DateTime.Now, hasCritical);
                    Log.Information("MosaicTools PRE-EMPTIVE SIGNED{Critical} - will count immediately when detected: {Accession}",
                        hasCritical ? " (CRITICAL)" : "", accession);
                    StatusMessage = $"✅ Pre-signed{criticalIndicator}";
                }

                // Start timer to clean up old pre-emptive entries
                EnsurePendingStudiesTimerRunning();
            }
        }
    }

    /// <summary>
    /// Handle a "study unsigned" message from MosaicTools.
    /// Discards the study - does not add to database.
    /// </summary>
    public void HandleMosaicToolsUnsignedStudy(string accession, bool hasCritical = false)
    {
        // Always show message for debugging/testing, even if integration disabled
        // Note: Critical flag is accepted for API consistency but doesn't affect unsigned studies
        StatusMessage = "❌ Unsigned";
        Log.Information("MosaicTools received UNSIGNED{Critical} message for: {Accession}",
            hasCritical ? " (CRITICAL)" : "", accession);

        if (!_dataManager.Settings.MosaicToolsIntegrationEnabled)
        {
            StatusMessage = "❌ Unsigned (off)";
            return;
        }

        bool foundInPending = false;
        lock (_pendingStudiesLock)
        {
            if (_pendingStudies.TryGetValue(accession, out var pending))
            {
                _pendingStudies.Remove(accession);
                foundInPending = true;
                Log.Information("MosaicTools confirmed UNSIGNED - discarding study: {Accession} ({StudyType}, {Rvu} RVU)",
                    accession, pending.Record.StudyType, pending.Record.Rvu);
                StatusMessage = $"❌ -{pending.Record.StudyType}";
            }
        }

        // Also check and remove from temporary studies (safety measure)
        // The accession in temporary is hashed, so we need to hash for comparison
        if (!foundInPending)
        {
            var hashedAccession = _dataManager.HashAccession(accession);
            var tempStudy = _temporaryStudies.FirstOrDefault(s => s.Accession == hashedAccession);
            if (tempStudy != null)
            {
                _temporaryStudies.Remove(tempStudy);
                Log.Information("MosaicTools UNSIGNED - removed from temporary studies: {Accession} ({StudyType}, {Rvu} RVU)",
                    accession, tempStudy.StudyType, tempStudy.Rvu);
                StatusMessage = "❌ Temp removed";
                UpdateRecentStudiesDisplay();
            }
            else if (_studyTracker.IsTracking(accession))
            {
                // Study is still being actively tracked (user still reading it).
                // Spurious unsigned message — just store it for when the study completes.
                lock (_preEmptiveUnsignedLock)
                {
                    _preEmptiveUnsigned[accession] = DateTime.Now;
                }
                Log.Information("MosaicTools UNSIGNED for actively-tracked study (still visible) - stored for later: {Accession}", accession);
                StatusMessage = "❌ Unsigned (tracking)";
                // No timer needed — will be checked when the study leaves the tracker
            }
            else
            {
                // True pre-emptive: not in pending, not in tracker, not in temp.
                // MosaicTools detected the study change before we did.
                // Track it so we discard when we detect the study completing.
                lock (_preEmptiveUnsignedLock)
                {
                    _preEmptiveUnsigned[accession] = DateTime.Now;
                    Log.Information("MosaicTools PRE-EMPTIVE UNSIGNED - will discard when detected: {Accession}", accession);
                    StatusMessage = "❌ Pre-unsigned";
                }

                // Start timer to clean up old pre-emptive entries
                EnsurePendingStudiesTimerRunning();
            }
        }
    }

    /// <summary>
    /// Add a completed study to the pending queue (when MosaicTools integration is enabled).
    /// The study will be added to database when signed, discarded when unsigned,
    /// or added on timeout (default behavior).
    /// </summary>
    private void AddToPendingQueue(StudyRecord record, string rawAccession)
    {
        // First check if we received a pre-emptive UNSIGNED for this accession
        // This happens when MosaicTools detects the study change before we do
        lock (_preEmptiveUnsignedLock)
        {
            if (_preEmptiveUnsigned.TryGetValue(rawAccession, out var unsignedTime))
            {
                _preEmptiveUnsigned.Remove(rawAccession);
                Log.Information("Discarding study due to pre-emptive UNSIGNED (received {Ago:F1}s ago): {Accession} ({StudyType}, {Rvu} RVU)",
                    (DateTime.Now - unsignedTime).TotalSeconds, rawAccession, record.StudyType, record.Rvu);
                StatusMessage = "❌ Pre-skip";
                return; // Don't add to pending - it was already marked unsigned
            }
        }

        // Check if we received a pre-emptive SIGNED for this accession
        // If so, add immediately without waiting
        lock (_preEmptiveSignedLock)
        {
            if (_preEmptiveSigned.TryGetValue(rawAccession, out var signedData))
            {
                _preEmptiveSigned.Remove(rawAccession);
                var (signedTime, hasCritical) = signedData;

                // Apply critical result flag from pre-emptive message
                record.HasCriticalResult = hasCritical;

                var criticalMsg = hasCritical ? " (CRITICAL)" : "";
                Log.Information("Adding study immediately due to pre-emptive SIGNED{Critical} (received {Ago:F1}s ago): {Accession} ({StudyType}, {Rvu} RVU)",
                    criticalMsg, (DateTime.Now - signedTime).TotalSeconds, rawAccession, record.StudyType, record.Rvu);
                StatusMessage = hasCritical ? "✅ Pre-add ⚠️" : "✅ Pre-add";

                // Add directly - create a PendingStudy just for the AddPendingStudyToDatabase call
                var pending = new PendingStudy
                {
                    Record = record,
                    RawAccession = rawAccession,
                    AddedAt = DateTime.Now,
                    TimeoutAt = DateTime.Now
                };
                AddPendingStudyToDatabase(pending);
                return;
            }
        }

        lock (_pendingStudiesLock)
        {
            var pending = new PendingStudy
            {
                Record = record,
                RawAccession = rawAccession,
                AddedAt = DateTime.Now,
                TimeoutAt = DateTime.Now.AddSeconds(_dataManager.Settings.MosaicToolsTimeoutSeconds)
            };
            _pendingStudies[rawAccession] = pending;
            Log.Information("Added study to pending queue, waiting for MosaicTools: {Accession}", rawAccession);
            StatusMessage = "⏳ Pending";

            // Start/restart the timer to check for timeouts
            EnsurePendingStudiesTimerRunning();
        }
    }

    /// <summary>
    /// Ensure the pending studies timeout timer is running.
    /// </summary>
    private void EnsurePendingStudiesTimerRunning()
    {
        if (_pendingStudiesTimer == null)
        {
            _pendingStudiesTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _pendingStudiesTimer.Tick += OnPendingStudiesTimerTick;
        }

        if (!_pendingStudiesTimer.IsEnabled)
        {
            _pendingStudiesTimer.Start();
        }
    }

    /// <summary>
    /// Timer tick to check for timed-out pending studies and clean up old pre-emptive entries.
    /// </summary>
    private void OnPendingStudiesTimerTick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        List<PendingStudy> timedOut = new();

        lock (_pendingStudiesLock)
        {
            foreach (var kvp in _pendingStudies.ToList())
            {
                if (now >= kvp.Value.TimeoutAt)
                {
                    timedOut.Add(kvp.Value);
                    _pendingStudies.Remove(kvp.Key);
                }
            }

        }

        // Clean up old pre-emptive unsigned entries (older than 30 seconds)
        bool hasPreEmptiveUnsigned = false;
        lock (_preEmptiveUnsignedLock)
        {
            var expiredEntries = _preEmptiveUnsigned
                .Where(kvp => (now - kvp.Value).TotalSeconds > 30)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredEntries)
            {
                _preEmptiveUnsigned.Remove(key);
                Log.Debug("Cleaned up expired pre-emptive UNSIGNED entry: {Accession}", key);
            }

            hasPreEmptiveUnsigned = _preEmptiveUnsigned.Count > 0;
        }

        // Clean up old pre-emptive signed entries (older than 60 seconds)
        // Just drop them — creating 0-RVU "Unknown" records is worse than missing a count.
        // The study will be counted normally when it actually completes via extraction.
        bool hasPreEmptiveSigned = false;
        lock (_preEmptiveSignedLock)
        {
            var expiredEntries = _preEmptiveSigned
                .Where(kvp => (now - kvp.Value.Time).TotalSeconds > 60)
                .ToList();

            foreach (var kvp in expiredEntries)
            {
                Log.Debug("Pre-emptive SIGNED expired without match - discarding (study will be counted normally when it completes): {Accession}", kvp.Key);
                _preEmptiveSigned.Remove(kvp.Key);
            }

            hasPreEmptiveSigned = _preEmptiveSigned.Count > 0;
        }

        // Stop timer if no pending studies and no pre-emptive entries to clean up
        lock (_pendingStudiesLock)
        {
            if (_pendingStudies.Count == 0 && !hasPreEmptiveUnsigned && !hasPreEmptiveSigned)
            {
                _pendingStudiesTimer?.Stop();
            }
        }

        // Process timed-out studies (add to database as default behavior)
        foreach (var pending in timedOut)
        {
            Log.Information("MosaicTools timeout - adding study to database (default): {Accession}", pending.RawAccession);
            StatusMessage = "⏱️ Timeout: added";
            AddPendingStudyToDatabase(pending);
        }
    }

    /// <summary>
    /// Add a pending study to the database.
    /// </summary>
    private void AddPendingStudyToDatabase(PendingStudy pending)
    {
        var shift = _dataManager.Database.GetCurrentShift();
        if (shift != null)
        {
            // Check for duplicates
            if (_dataManager.Settings.IgnoreDuplicateAccessions)
            {
                var existing = _dataManager.Database.FindRecordByAccession(shift.Id, pending.Record.Accession);
                if (existing != null)
                {
                    Log.Debug("Skipping duplicate accession from pending: {Accession}", pending.RawAccession);
                    return;
                }
            }

            try
            {
                _dataManager.Database.AddRecord(shift.Id, pending.Record);
                _studyTracker.MarkSeen(pending.RawAccession);
                // Don't set StatusMessage here - caller already set MosaicTools message
                Log.Information("Added pending study to database: {Accession} -> {StudyType} ({Rvu} RVU)",
                    pending.RawAccession, pending.Record.StudyType, pending.Record.Rvu);

                CalculateAndUpdateStats();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to add pending record for {Accession} - study will be retried", pending.RawAccession);
            }
        }
        else
        {
            // No shift - add to temporary
            if (!_temporaryStudies.Any(s => s.Accession == pending.Record.Accession))
            {
                _temporaryStudies.Add(pending.Record);
                // Don't set StatusMessage here - caller already set MosaicTools message
                Log.Information("Added pending study to temporary (no shift): {Accession}", pending.RawAccession);
                UpdateRecentStudiesDisplay();
            }
        }
    }


    public void Dispose()
    {
        _refreshTimer.Tick -= OnRefreshTick;
        _refreshTimer.Stop();
        _statsTimer.Tick -= OnStatsTick;
        _statsTimer.Stop();
        _fatigueTimer.Tick -= OnFatigueTick;
        _fatigueTimer.Stop();
        _backupTimer.Tick -= OnBackupTick;
        _backupTimer.Stop();
        if (_pendingStudiesTimer != null)
        {
            _pendingStudiesTimer.Tick -= OnPendingStudiesTimerTick;
            _pendingStudiesTimer.Stop();
        }
        _teamSyncTimer?.Stop();
        _teamSyncService?.Dispose();
        StopPipeClient();
        Utils.WindowExtraction.Cleanup();
        _dataManager.Dispose();
        LoggingConfig.Shutdown();
    }
}

/// <summary>
/// Represents a study waiting for MosaicTools signed/unsigned confirmation.
/// </summary>
internal class PendingStudy
{
    public required StudyRecord Record { get; set; }
    public required string RawAccession { get; set; }
    public DateTime AddedAt { get; set; }
    public DateTime TimeoutAt { get; set; }
}

/// <summary>
/// Represents a study in a multi-accession group (Python parity).
/// When multiple accessions are visible simultaneously, we track each one
/// and create individual records when they complete together.
/// </summary>
internal class MultiAccessionStudy
{
    public required string Accession { get; set; }
    public required string Procedure { get; set; }
    public required string StudyType { get; set; }
    public required double Rvu { get; set; }
    public required DateTime FirstSeen { get; set; }
    public string PatientClass { get; set; } = "Unknown";
}
