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

## Distribution

**IMPORTANT: Always include `resources/rvu_rules.yaml` when distributing!**

The `rvu_rules.yaml` file is NOT embedded in the executable - it's loaded from the filesystem at runtime. Without it, all studies will be classified as "Unknown" with 0 RVU.

### Release Checklist
1. Update version in `Core/Config.cs` (AppVersion and AppVersionDate)
2. Build: `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`
3. Create release folder with:
   - `RVUCounter.exe` (from publish output)
   - `resources/rvu_rules.yaml` (from publish output or source)
4. Create zip containing both exe and resources folder
5. Create GitHub Release at https://github.com/erichter2018/RVUCounter/releases
6. Upload both `RVUCounter.exe` and `RVUCounter.zip`

### Download Links (always latest)
- https://github.com/erichter2018/RVUCounter/releases/latest/download/RVUCounter.zip
- https://github.com/erichter2018/RVUCounter/releases/latest/download/RVUCounter.exe

### Files Generated Dynamically (no need to distribute)
- `settings/user_settings.yaml` - Created on first run with defaults
- `data/rvu_records.db` - SQLite database created on first run
- `logs/` - Log files created during runtime

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

### 2026-01-26 (Session 5) - Critical Results Integration
- **Critical Results Feature**: Expanded MosaicTools WM_COPYDATA integration from 2 to 4 message types to track critical results

  - **MainWindow.xaml.cs**: Updated WM_COPYDATA handler:
    - Added `CYCLEDATA_STUDY_SIGNED_CRITICAL = 3` constant
    - Added `CYCLEDATA_STUDY_UNSIGNED_CRITICAL = 4` constant
    - Handler now detects critical message types and passes `hasCritical` flag to ViewModel

  - **MainViewModel.cs**: Critical results tracking:
    - Added `_criticalStudies` HashSet<string> for tracking critical accessions
    - Updated `HandleMosaicToolsSignedStudy(accession, hasCritical)` signature
    - Updated `HandleMosaicToolsUnsignedStudy(accession, hasCritical)` signature
    - Added `ShowCriticalOnly` filter toggle property
    - Added `CriticalResultsCount` observable property
    - Added `ToggleCriticalFilterCommand` to filter recent studies
    - Added `MarkStudyAsCritical()` and `UpdateCriticalResultsCount()` helpers
    - Updated `_preEmptiveSigned` dictionary to store `(DateTime, bool)` tuple for critical flag
    - `CalculateAndUpdateStats()` now applies critical filter and updates count
    - `UpdateRecentStudiesDisplay()` handles critical filter for temp studies

  - **Models/StudyRecord.cs**: Added `HasCriticalResult` bool property (persisted to database)

  - **Data/RecordsDatabase.cs**:
    - Added `has_critical_result INTEGER DEFAULT 0` column to records table
    - Added migration for existing databases
    - Updated all INSERT queries (`AddRecord`, `BatchAddRecords`, `InsertHistoricalShift`)
    - Updated `UpdateRecord` to include `has_critical_result`
    - Updated `ReadRecord` to read `has_critical_result` column

  - **MainWindow.xaml**: UI updates:
    - Added critical results count badge (red ⚠️ N) in Recent Studies header
    - Added ⚠️ icon before procedure name for studies with critical results
    - Updated column definitions to accommodate critical icon

  - **New Files**:
    - `Converters/PositiveToVisibilityConverter.cs` - Shows element when value > 0

  - **App.xaml**: Registered `PositiveToVisibilityConverter`

### 2026-01-26 (Session 5 continued) - Patient Name & Site Code Extraction
- **Patient Info Scraping**: Added memory-only extraction of patient name and site code from Mosaic

  - **MosaicStudyData class**: Added new properties:
    - `PatientName` - Patient name from Mosaic (e.g., "Millson Diana")
    - `SiteCode` - Site code from Mosaic (e.g., "MLC")

  - **MosaicExtractor.cs**: Added extraction methods:
    - `ExtractPatientName()` - Detects all-caps names like "MILLSON DIANA", converts to title case
    - `ExtractSiteCode()` - Parses "Site Code: MLC" pattern
    - Integrated into main `ExtractStudyData()` method (single scrape pass)

  - **MainViewModel.cs**: Memory-only storage:
    - Added `_patientNameCache` Dictionary<string, string> (keyed by hashed accession)
    - Added `_siteCodeCache` Dictionary<string, string> (keyed by hashed accession)
    - Added `GetPatientName(hashedAccession)` and `GetSiteCode(hashedAccession)` lookup methods
    - Added `GetStudyTooltip(study)` helper for formatting tooltip text
    - Added `CurrentPatientName` and `CurrentSiteCode` observable properties for current study display
    - Caches are populated during scan but never persisted to database

  - **MainWindow.xaml**: UI updates:
    - Recent Studies items now have rich tooltips showing:
      - Patient name (bold, if available)
      - Site code (if available)
      - Procedure, Study Type, RVU, Patient Class, Time
      - Critical result indicator
    - Current Study panel now displays:
      - Patient name (bold, top line when available)
      - Site code inline with Patient Class: "Patient Class: Inpatient | Site: MLC"

  - **New Converters**:
    - `PatientNameConverter.cs` - Looks up patient name from ViewModel cache
    - `PatientNameVisibilityConverter.cs` - Shows/hides based on cache presence
    - `SiteCodeConverter.cs` - Looks up and formats site code
    - `SiteCodeVisibilityConverter.cs` - Shows/hides based on cache presence
    - `NonEmptyToVisibilityConverter.cs` - Generic non-empty string visibility

  - **Privacy by Design**:
    - Patient names and site codes are stored in memory only
    - Never written to SQLite database or any file
    - Cleared automatically when app restarts
    - Uses hashed accession as key for lookup

### 2026-01-27 (Session 6) - ClarioLauncher: Open Study via UI Automation
- **ClarioLauncher.cs** completely rewritten — replaced XML file drop with UI automation

  - **How it works** (full flow):
    1. Find Clario Chrome window via `ClarioExtractor.FindClarioChromeWindow(useCache: false)`
    2. Find accession search field using **fulltext anchor** strategy:
       - Query all Edit controls via `FindAllDescendants(ByControlType.Edit)`
       - Find the `fulltext-*-inputEl` field (stable AutomationId prefix)
       - The **next** Edit field after it is always the accession search box
       - This is stable regardless of how many extra fields appear when search results are open
    3. Set the accession value (UIA Value pattern, or Focus + Ctrl+A + type fallback)
    4. Press Enter to trigger search
    5. Wait 2 seconds for results to load
    6. Re-fetch Clario window, scan all DataItem elements
    7. Find action column cell: ClassName must contain both `search_result` and `action-col-cell`
    8. Click at 55% from left edge, center vertically via `FlaUI.Core.Input.Mouse.Click()`

  - **Key technical decisions**:
    - All UI automation runs on `Task.Run()` background thread — UIA3 COM singleton may be created on thread pool thread (from enrichment), calling from UI thread silently fails
    - FlaUI's flat `FindAllDescendants()` only finds 3 Edit fields in Chrome; the native UIA condition query `FindAllDescendants(ByControlType.Edit)` finds all 12+
    - Index-based field selection is fragile (field count changes when search results are open); anchor-based (`fulltext-*` → next field) is stable
    - Action cell must match `search_result` in ClassName to avoid clicking worklist rows

  - **Clario search form layout** (ExtJS grid, all Edit fields unnamed):
    - `textfield-XXXX-inputEl` — Last Name
    - `textfield-XXXX-inputEl` — First Name (ClassName contains `navigation_search_firstName_fieldCls`)
    - `fulltext-XXXX-inputEl` — Fulltext search (has trigger button with `trigger-search` class)
    - `textfield-XXXX-inputEl` — **Accession** (the one we target)
    - `comboMany-XXXX-inputEl` — Modality combo
    - `textfield-XXXX-inputEl` — Other
    - IDs are dynamic (numbers change across sessions), but prefixes and order are stable

  - **`MainViewModel.OpenInClario`**: Calls `await ClarioLauncher.OpenStudyByAccession(originalAccession)` — no MRN needed, async Task

- **Bug fixes this session**:
  - UIA3 COM threading: `FindClarioChromeWindow` returned null from UI thread but worked from background thread. Fixed by wrapping all automation in `Task.Run()`
  - Chrome address bar matched as "search" field — added exclusion for `address`/`omnibox` in Name/AutomationId
  - FlaUI `FindAllDescendants()` missed deep Chrome renderer elements (only 3 of 12 Edit fields) — switched to UIA condition-filtered query which finds all
  - History search field (`navigation_history_Search_textfield-inputEl`) matched before accession field — switched from keyword matching to fulltext-anchor strategy

## Current State (Session End: 2026-01-26)

### Last Build
- **Status**: Successfully compiled and published
- **Location**: `C:\Users\erik.richter\Desktop\RVUCounter\csharp\RVUCounter\bin\Release\net8.0-windows\win-x64\publish\RVUCounter.exe`
- **Warnings**: Only MVVM toolkit warnings (pre-existing, non-critical)

### Completed This Session (Session 5)
**Critical Results Integration with MosaicTools:**
1. ✅ Added 2 new message types (SIGNED_CRITICAL=3, UNSIGNED_CRITICAL=4)
2. ✅ Updated WM_COPYDATA handler to detect critical messages
3. ✅ Added HasCriticalResult property to StudyRecord model
4. ✅ Database schema migration for has_critical_result column
5. ✅ All INSERT/UPDATE queries updated to include critical flag
6. ✅ Critical results count badge in Recent Studies header
7. ✅ ⚠️ icon shown next to studies with critical results
8. ✅ ShowCriticalOnly filter to view only critical studies
9. ✅ Pre-emptive signed messages preserve critical flag

**Patient Name & Site Code Extraction:**
10. ✅ Added patient name extraction from Mosaic (all-caps "LASTNAME FIRSTNAME" format)
11. ✅ Added site code extraction from Mosaic ("Site Code: MLC" pattern)
12. ✅ Memory-only storage (never persisted to database for privacy)
13. ✅ Rich tooltips on Recent Studies showing patient name and site
14. ✅ Current Study panel shows patient name and site code

**UI Cleanup & Critical Filter:**
15. ✅ Removed right-click "View Details" popup (tooltips now show all info)
16. ✅ Added clickable critical filter button (⚠️ badge) next to Undo
17. ✅ Button toggles between showing all studies vs critical-only
18. ✅ Button changes color when filter is active (red = filtering)
19. ✅ Shortened "Temporary - No shift started" to "Temporary"
20. ✅ Updated About section with comprehensive HIPAA/privacy language
21. ✅ Application rebuilt and published successfully

### Feature Parity Status
The C# application now has comprehensive feature parity with Python plus:
- **Auto-Update**: Rename-based update mechanism (works while app is running)
- **Critical Results**: MosaicTools integration with visual indicators and filtering
- **Patient Info (Memory Only)**: Patient name and site code extraction with tooltips
- **Recent Studies**: Procedure name, delete button, time ago, duration, red for invalid modalities, ⚠️ for critical, rich tooltips with patient info
- **Current Study**: Patient name, site code, accession, procedure, RVU
- **Pace Car**: Arrow symbols with ahead/behind text
- **Statistics**: All 11 view modes with correct column formats
- **Settings**: ShowTime toggle, AutoUpdateEnabled toggle
- **Skipped**: PowerScribe and mini mode (as requested)

### Potential Future Work
- Multi-accession study handling still needs review (Python has complex logic for this)
- Test with actual Mosaic to verify extraction works correctly
- Consider adding more detailed logging for debugging study flow
- Test auto-update with actual GitHub releases
- Add filter button UI to toggle critical-only view (currently only via command)
