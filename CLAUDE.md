# RVU Counter - Claude Code Documentation

This document provides architecture details for AI assistants working on this codebase.

## Project Overview

RVU Counter is a dual-platform application for tracking Relative Value Units (RVUs) in radiology. It has two implementations:
- **C# (Primary)**: WPF application using .NET 8.0
- **Python (Legacy)**: Tkinter GUI application

Both versions share a SQLite database and aim for feature parity.

## C# Architecture

### Project Structure

```
csharp/RVUCounter/
├── Core/                    # Configuration, logging, theming, updates
│   ├── Config.cs           # App version, constants
│   ├── LoggingConfig.cs    # Serilog configuration
│   ├── ThemeManager.cs     # Dark/light mode switching
│   ├── CompensationRates.cs # Compensation calculation logic
│   └── PlatformUtils.cs    # Cross-platform utilities
│
├── Data/                    # Data persistence layer
│   ├── DataManager.cs      # Central data access, settings, RVU rules
│   └── RecordsDatabase.cs  # SQLite operations for shifts/records
│
├── Logic/                   # Business logic
│   ├── StudyTracker.cs     # Tracks active studies, detects completion
│   ├── StudyMatcher.cs     # Classifies procedures -> study types + RVU
│   ├── HIPAAMigrator.cs    # Encrypts accessions for HIPAA compliance
│   ├── PayrollSyncManager.cs # Syncs with external payroll
│   ├── ExcelChecker.cs     # Audit reconciliation
│   ├── DatabaseRepair.cs   # Fixes corrupted records
│   └── BackupManager.cs    # GitHub/OneDrive backup sync
│
├── Models/                  # Data models
│   ├── Shift.cs            # Shift entity
│   ├── StudyRecord.cs      # Study record entity
│   ├── UserSettings.cs     # All user preferences
│   ├── RvuRule.cs          # Classification rule model
│   └── WindowPosition.cs   # Saved window positions
│
├── Utils/                   # UI Automation & Extraction
│   ├── MosaicExtractor.cs  # Extracts data from Mosaic Info Hub
│   ├── ClarioExtractor.cs  # Extracts patient class from Clario
│   └── WindowExtraction.cs # Base FlaUI automation utilities
│
├── Views/                   # XAML Windows
│   ├── MainWindow.xaml     # Main application window
│   ├── SettingsWindow.xaml # User settings dialog
│   ├── StatisticsWindow.xaml # Analytics and charts
│   ├── ToolsWindow.xaml    # Utilities (manual entry, repair, etc.)
│   └── HIPAAWarningWindow.xaml # HIPAA compliance warning
│
├── ViewModels/              # MVVM ViewModels
│   ├── MainViewModel.cs    # Main window logic, study tracking
│   ├── SettingsViewModel.cs # Settings management
│   ├── StatisticsViewModel.cs # Statistics calculations
│   └── ToolsViewModel.cs   # Tools functionality
│
├── Themes/                  # Theme resources
│   ├── DarkTheme.xaml      # Dark mode colors/brushes
│   └── LightTheme.xaml     # Light mode colors/brushes
│
└── Resources/               # Embedded resources
    └── rvu_rules.yaml      # Study classification rules
```

### Key Components

#### DataManager (`Data/DataManager.cs`)
Central hub for all data operations:
- Manages `UserSettings` (YAML: `user_settings.yaml`)
- Loads `RvuTable` and `ClassificationRules` from embedded YAML
- Provides lazy-loaded `RecordsDatabase` instance
- Handles HIPAA salt generation for accession hashing
- Manages window positions

#### RecordsDatabase (`Data/RecordsDatabase.cs`)
SQLite database layer with full CRUD operations:
- **Tables**: `shifts`, `records`
- **Python Compatibility**: Auto-detects and migrates Python database format
  - Converts `time_performed` -> `timestamp`
  - Handles `is_current` column -> `shift_end IS NULL`
- Thread-safe with internal locking

#### StudyTracker (`Logic/StudyTracker.cs`)
State machine for tracking active studies:
- Monitors accessions visible in Mosaic/PowerScribe
- Detects "completion" when accession disappears
- Enforces `MinStudySeconds` threshold
- Returns `TrackedStudy` objects with duration

#### MainViewModel (`ViewModels/MainViewModel.cs`)
Main application logic:
- 6 RVU metrics with Python formula parity
- 6 compensation metrics
- Pace car comparison with prior shift
- Real-time study detection via `DispatcherTimer`
- Recent studies list (last 20)

### Database Schema

```sql
-- Shifts table
CREATE TABLE shifts (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    shift_start TEXT NOT NULL,
    shift_end TEXT,                    -- NULL = active shift
    effective_shift_start TEXT,        -- Rounded start time
    projected_shift_end TEXT,          -- Expected end
    shift_name TEXT
);

-- Records table
CREATE TABLE records (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    shift_id INTEGER NOT NULL,
    accession TEXT NOT NULL,           -- HIPAA-hashed
    procedure TEXT,
    study_type TEXT,
    rvu REAL,
    timestamp TEXT NOT NULL,
    patient_class TEXT DEFAULT 'Unknown',
    accession_count INTEGER DEFAULT 1,
    source TEXT DEFAULT 'Mosaic',
    metadata TEXT,
    duration_seconds REAL,
    from_multi_accession INTEGER DEFAULT 0,
    FOREIGN KEY (shift_id) REFERENCES shifts(id)
);
```

### Python vs C# Schema Differences

| Aspect | Python | C# |
|--------|--------|-----|
| Timestamp column | `time_performed` | `timestamp` |
| Active shift detection | `is_current = 1` | `shift_end IS NULL` |
| Multi-accession data | `individual_*` JSON columns | `accession_count` + `from_multi_accession` |
| Extra tables | `legacy_records` | (none) |

The C# `RecordsDatabase` automatically detects and migrates Python databases on startup.

### Data Flow

```
┌─────────────────────────────────────────────────┐
│   UI Automation (MosaicExtractor/FlaUI)         │
│   ↓ Extracts accession, procedure, patient_class│
├─────────────────────────────────────────────────┤
│   StudyTracker: Monitors visible accessions     │
│   ↓ Detects when study disappears (completed)   │
├─────────────────────────────────────────────────┤
│   StudyMatcher: Classifies procedure text       │
│   ↓ Returns study_type + RVU from rules         │
├─────────────────────────────────────────────────┤
│   RecordsDatabase: Persists to SQLite           │
│   ↓ Hashes accession, stores record             │
├─────────────────────────────────────────────────┤
│   MainViewModel: Updates UI                     │
│   ├─ RecentStudies: Observable collection       │
│   ├─ Stats: Total, Avg, LastHour, etc.          │
│   └─ CurrentStudy: Real-time display            │
└─────────────────────────────────────────────────┘
```

### MVVM Pattern

Uses CommunityToolkit.Mvvm for:
- `[ObservableProperty]` - Auto-generates INotifyPropertyChanged
- `[RelayCommand]` - Command binding
- `partial void On{Property}Changed()` - Property change handlers

### Dependencies

- **Microsoft.Data.Sqlite** - SQLite database
- **CommunityToolkit.Mvvm** - MVVM framework
- **FlaUI.Core/FlaUI.UIA3** - UI Automation
- **Serilog** - Structured logging
- **YamlDotNet** - YAML parsing
- **LiveChartsCore** - Charting library (statistics)

## Build Instructions

**IMPORTANT: When user says "build" or "rebuild", OR when you finish making code changes, just run the full build command immediately without asking for confirmation. This includes killing the process, compiling, and starting the app - do it all automatically.**

### Full Build & Run Command (Use This!)
```bash
taskkill //F //IM RVUCounter.exe 2>/dev/null; sleep 1; c:/Users/erik.richter/Desktop/dotnet/dotnet.exe publish "C:/Users/erik.richter/Desktop/RVUCounter/csharp/RVUCounter/RVUCounter.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true && "C:/Users/erik.richter/Desktop/RVUCounter/csharp/RVUCounter/bin/Release/net8.0-windows/win-x64/publish/RVUCounter.exe" &
```

### Common Issues
1. **"File is being used by another process"** - Kill the app first, wait a second

### Notes
- .NET 8.0 required (custom path: `c:\Users\erik.richter\Desktop\dotnet\dotnet.exe`)
- Publishes as single-file, self-contained executable
- Output location: `C:\Users\erik.richter\Desktop\RVUCounter\csharp\RVUCounter\bin\Release\net8.0-windows\win-x64\publish\RVUCounter.exe`
- Non-critical warnings in BackupManager.cs (nullable references) can be ignored

## Feature Parity Status

### Implemented in C#
- Basic RVU tracking and statistics
- Mosaic/Clario extraction with multi-pass detection
- Dark/light themes
- Settings persistence
- **Statistics window** - All 11 view modes:
  - `summary` - 20+ metrics (basic stats, efficiency, compensation, hourly analysis, modality breakdown, patient class)
  - `by_hour` - Hourly RVU breakdown with bar chart
  - `by_modality` - RVU by modality (CT, MR, XR, etc.) with pie chart
  - `by_study_type` - RVU by specific study type
  - `by_body_part` - RVU grouped by body part (Head, Chest, Abdomen, etc.)
  - `by_patient_class` - RVU by patient class (Inpatient, Outpatient, ED)
  - `efficiency` - Daily efficiency metrics with line chart
  - `projection` - Monthly/annual RVU projections
  - `compensation` - Earnings calculations with target tracking
  - `comparison` - Side-by-side shift comparison with bar chart
  - `all_studies` - List view of all studies
- All period options (current_shift, prior_shift, today, this_week, this_work_week, last_work_week, this_month, last_month, last_3_months, last_year, all_time, custom_range)
- Pace car comparison (multiple modes: prior shift, daily goal, best week, best ever, weeks 1-4 ago)
- HIPAA accession hashing
- Temporary studies feature (tracks studies outside shift)
- Python database auto-migration

### Missing from C# (vs Python)
- Some edge cases in multi-accession handling
- PowerScribe integration (intentionally skipped)
- Mini mode (intentionally skipped)

## Known Issues

1. **Database crash**: Fixed - C# now auto-migrates Python databases
2. **Studies outside shift**: Fixed - Now tracked as temporary with red label
3. **Simple studies**: Fixed - Multi-pass extraction now properly detects "Current Study" label
4. **Statistics parity**: Fixed - All 11 view modes implemented with full feature parity

## Recent Changes

### 2026-01-19 (Session 3) - UI Parity & Display Formatting
- **Recent Studies Display Overhaul** (matches Python exactly):
  - Now shows **Procedure** text instead of Study Type
  - Added **delete button** (×) for each study with hover effect (red on hover)
  - RVU format changed from F2 to **F1** with " RVU" suffix (e.g., "1.5 RVU")
  - Added **Time Ago** display ("2 minutes ago") - controlled by ShowTime setting
  - Added **Duration** display ("1m 30s") - controlled by ShowTime setting
  - Added **red color** for invalid modality procedures (dark red #8B0000)
  - Uses Consolas font like Python for monospace alignment

- **New Value Converters** (in `Converters/` folder):
  - `TimeAgoConverter.cs` - Converts DateTime to "X minute(s) ago" format
  - `DurationConverter.cs` - Converts seconds to "1m 30s" format
  - `ModalityColorConverter.cs` - Returns dark red for invalid modalities
  - `PaceDiffConverter.cs` - Formats pace diff as "▲ +5.3 ahead" or "▼ 2.1 behind"

- **Current Study Display**:
  - Changed RVU format from F2 to F1 to match Python

- **Pace Car Improvements**:
  - Now shows **▲/▼ arrows** with "ahead"/"behind" text like Python
  - Format: "▲ +5.3 ahead" or "▼ 2.1 behind" or "on pace"

- **Statistics All Studies View**:
  - Updated columns: #, Date, Time, Procedure, Study Type (matches Python)
  - RVU format changed from F2 to F1

- **ShowTime Setting**:
  - Added `ShowTime` property to MainViewModel
  - Time row in Recent Studies is now conditional based on this setting
  - Setting available in Settings → General → "Show time in display"

- **Files Added**:
  - `Converters/TimeAgoConverter.cs`
  - `Converters/DurationConverter.cs`
  - `Converters/ModalityColorConverter.cs`
  - `Converters/PaceDiffConverter.cs`

- **Files Modified**:
  - `MainWindow.xaml` - Complete Recent Studies template redesign
  - `MainViewModel.cs` - Added ShowTime property
  - `App.xaml` - Registered new converters
  - `Views/PaceCarControl.xaml` - Added PaceDiffConverter usage
  - `ViewModels/StatisticsViewModel.cs` - Updated All Studies view columns

### 2026-01-19 (Session 2) - Critical Study Tracking Fix
- **MAJOR FIX: Study Tracking Logic Rewrite**
  - **Problem**: C# was tracking ALL visible accessions via `GetVisibleAccessions()` instead of just the current study. Studies in worklists/history never "disappeared" so never completed.
  - **Solution**: Rewrote to match Python's approach - track based on CURRENT STUDY accession only
  - **Changes**:
    - Removed `GetVisibleAccessions()` and `UpdateWithCurrentAccessions()` from main flow
    - Now uses `CheckCompleted(currentTime, currentAccession)` - studies complete when current accession changes
    - `AddStudy()` now properly passes RVU table and classification rules for correct study type/RVU
    - Added N/A procedure handling - when procedure is "n/a", completes all active studies (matches Python)
    - Studies now properly flow: Detection → Tracking → Completion → Database → RecentStudies list

- **Key Flow Changes in `ApplyScanResults()`**:
  1. Extract current accession from Mosaic (single study, not worklist)
  2. Check if procedure is "n/a" - if so, complete all active studies
  3. Call `CheckCompleted()` to detect studies that disappeared
  4. Process completed studies → add to database → update stats
  5. Add current study to tracker (only if procedure is valid)
  6. Update UI display

- **Files Modified**:
  - `MainViewModel.cs`: Complete rewrite of `PerformMosaicScan()` and `ApplyScanResults()`
  - Added `ProcessCompletedStudies()` helper method
  - Added `InvalidProcedures` constant for n/a detection
  - Added `_lastSeenAccession` field for tracking changes

### 2026-01-19 (Session 1)
- **Database Migration**: Added Python database migration support to `RecordsDatabase`
  - Auto-detects `time_performed` column and migrates to `timestamp`
  - Handles `is_current` -> `shift_end IS NULL` conversion
  - Added missing column migration (`source`, `metadata`, `accession_count`)

- **Temporary Studies Feature**: Added tracking of studies opened outside a shift
  - Shows "Temporary - No shift started" label in red when no shift is active
  - Prompts user when starting shift: add temp studies to shift or discard
  - Retroactively extends shift start if temp studies have >5 RVU
  - `MainViewModel._temporaryStudies` stores temp records

- **Improved Mosaic Extraction**: Enhanced `MosaicExtractor.ExtractStudyData()`
  - Multi-pass extraction like Python version
  - Pass 1: Look for "Current Study" label (most reliable for single accessions)
  - Pass 2: Look for "Accession" label
  - Pass 3: Look for "Description:" label for procedure
  - Pass 4: Procedure keywords (CT, MR, XR, etc.)
  - Pass 5: Fallback scan for any accession-like strings

- **Statistics Period Options**: Added missing periods
  - `this_work_week` - Monday to Friday of current week
  - `last_work_week` - Monday to Friday of previous week
  - `last_month` - Full previous month
  - `last_3_months` - Rolling 3 months
  - `last_year` - Rolling 1 year

- **WindowExtraction**: Added `GetElementName()` method for separate name/text extraction

- **Pace Car Improvements**:
  - Pace car now only visible when shift is active AND setting is enabled (`IsPaceCarVisible` computed property)
  - Added click handler to show comparison mode selector popup
  - Options: Prior Shift, Daily Goal, Best This Week, Best Ever, and weeks 1-4 ago
  - `PaceComparisonMode` persisted to settings file
  - `ChangePaceComparisonCommand` to switch comparison modes

- **Window Position Saving**: Verified window positions are saved via `DataManager.SaveWindowPosition()` which calls `SaveSettings()` to persist to YAML file

- **Statistics View Overhaul**: Massive expansion of `StatisticsViewModel.cs`
  - **Summary view**: Now shows 20+ metrics organized in sections:
    - Basic Stats (total, avg, median, min/max RVU)
    - Efficiency (time span, studies/hour, RVU/hour, avg time per study)
    - Compensation (total earnings, comp/hour, % of monthly target)
    - Hourly Analysis (busiest hour, most productive hour)
    - Top Modalities (top 6 by RVU with percentages)
    - Patient Class breakdown
  - **Compensation view**: Full earnings breakdown with target tracking
    - Base rate calculations
    - By modality compensation
    - Monthly target progress with days remaining estimate
    - Bonus calculation when target exceeded
  - **Body Part view**: Groups studies by anatomical region
    - Head/Brain, Neck/C-Spine, Chest/Thorax, Abdomen, Pelvis, Spine, Extremity, Cardiac, Vascular
    - Pie chart visualization
  - **Comparison view**: Side-by-side shift comparison
    - Basic metrics (studies, RVU, averages)
    - Efficiency metrics (duration, RVU/hour)
    - Modality breakdown comparison
    - Bar chart comparing modalities between shifts
  - Helper methods: `ExtractBodyPart()`, `AddComparisonRow()`
  - `ComparisonShift1` and `ComparisonShift2` properties for shift selection

### 2026-01-24 (Session 4) - Auto-Update Implementation
- **Auto-Update Feature**: Implemented MosaicTools-style auto-update using rename-based approach
  - **UpdateManager.cs** completely rewritten:
    - Uses rename-based update (Windows allows renaming running executables)
    - `RVUCounter.exe` → `RVUCounter_old.exe` → new exe becomes `RVUCounter.exe`
    - `CleanupOldVersion()` - Deletes `_old.exe` on startup, returns true if just updated
    - `DownloadAndApplyUpdateAsync()` - Downloads ZIP/EXE, extracts if needed, applies update
    - `RestartApp()` - Restarts preserving command line args
    - Prefers ZIP over EXE (corporate security friendly)
    - API timeout: 15 seconds, Download timeout: 5 minutes
    - `GetCurrentVersion()` - Gets version from assembly metadata

  - **UserSettings.cs**: Added `AutoUpdateEnabled` property (default: true)

  - **MainViewModel.cs**: Added `PerformStartupUpdateCheckAsync()`:
    1. Cleans up old version from previous update
    2. Shows WhatsNewWindow if version changed
    3. Auto-updates if enabled (prompts user, allows skipping)
    4. Skipped versions are remembered in `SkippedVersion` setting

  - **SettingsViewModel.cs**: Added:
    - `AutoUpdateEnabled` binding
    - `CheckForUpdatesCommand` - Manual update check with progress
    - `IsCheckingForUpdates` / `UpdateStatusMessage` for UI state
    - `AppVersionDisplay` property for About tab

  - **SettingsWindow.xaml**: Added Updates section in General tab:
    - "Automatically check for updates on startup" checkbox
    - "Check for Updates" button with status message

  - **New Files**:
    - `Converters/InverseBoolConverter.cs` - For button enabled state

  - **Config.cs**: Updated version to `2.0.0` and date to `01/24/2026`

  - **RVUCounter.csproj**: Added `AssemblyVersion` and `FileVersion` properties

## Current State (Session End: 2026-01-24)

### Last Build
- **Status**: Successfully compiled and published
- **Location**: `C:\Users\erik.richter\Desktop\RVUCounter\csharp\RVUCounter\bin\Release\net8.0-windows\win-x64\publish\RVUCounter.exe`
- **Warnings**: Only minor CS0219 warning in PaceComparisonDialog.cs (non-critical)

### Completed This Session (Session 4)
Auto-update implementation using MosaicTools-style rename approach:
1. ✅ Rewrote `UpdateManager.cs` with rename-based update mechanism
2. ✅ Added `CleanupOldVersion()` for post-update cleanup
3. ✅ Added `AutoUpdateEnabled` setting (default: true)
4. ✅ Startup update check with WhatsNewWindow display on version change
5. ✅ Manual "Check for Updates" button in Settings
6. ✅ Progress reporting during download
7. ✅ Version skip functionality (user can choose to skip a version)
8. ✅ Prefers ZIP over EXE for downloads
9. ✅ Proper timeouts (15s API, 5min download)
10. ✅ Application rebuilt and published successfully

### Feature Parity Status
The C# application now has comprehensive feature parity with Python plus auto-update:
- **Auto-Update**: Rename-based update mechanism (works while app is running)
- **Recent Studies**: Procedure name, delete button, time ago, duration, red for invalid modalities
- **Current Study**: Correct RVU format (F1)
- **Pace Car**: Arrow symbols with ahead/behind text
- **Statistics**: All 11 view modes with correct column formats
- **Settings**: ShowTime toggle, AutoUpdateEnabled toggle
- **Skipped**: PowerScribe and mini mode (as requested)

### Potential Future Work
- Multi-accession study handling still needs review (Python has complex logic for this)
- Test with actual Mosaic to verify extraction works correctly
- Consider adding more detailed logging for debugging study flow
- Test auto-update with actual GitHub releases
