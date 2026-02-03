# RVU Counter - Claude Code Documentation

## Project Overview

RVU Counter is a WPF application (.NET 8.0) for tracking Relative Value Units (RVUs) in radiology.

## C# Architecture

### Project Structure

```
csharp/RVUCounter/
├── Core/           # Config, logging, theming, updates
├── Data/           # DataManager, RecordsDatabase (SQLite)
├── Logic/          # StudyTracker, StudyMatcher, BackupManager
├── Models/         # Shift, StudyRecord, UserSettings, RvuRule
├── Utils/          # MosaicExtractor, ClarioExtractor, ClarioLauncher
├── Views/          # XAML Windows
├── ViewModels/     # MVVM ViewModels
├── Themes/         # Dark/Light theme resources
├── Converters/     # Value converters for XAML bindings
└── Resources/      # rvu_rules.yaml, tbwu_rules.db (NOT embedded)
```

### Key Components

- **DataManager**: Central hub for settings (YAML) and RVU rules
- **RecordsDatabase**: SQLite layer with Python database auto-migration
- **StudyTracker**: State machine tracking active studies, detects completion
- **MainViewModel**: Main app logic, 6 RVU metrics, 6 compensation metrics, pace car
- **MosaicExtractor/ClarioExtractor**: UI Automation (FlaUI) for data extraction
- **MosaicToolsPipeClient**: Named pipe integration with MosaicTools

### Database Schema

```sql
CREATE TABLE shifts (
    id INTEGER PRIMARY KEY, shift_start TEXT, shift_end TEXT,
    effective_shift_start TEXT, projected_shift_end TEXT, shift_name TEXT
);
CREATE TABLE records (
    id INTEGER PRIMARY KEY, shift_id INTEGER, accession TEXT, procedure TEXT,
    study_type TEXT, rvu REAL, timestamp TEXT, patient_class TEXT,
    accession_count INTEGER, source TEXT, metadata TEXT, duration_seconds REAL,
    from_multi_accession INTEGER, has_critical_result INTEGER,
    FOREIGN KEY (shift_id) REFERENCES shifts(id)
);
```

## Build Instructions

**When user says "build" or after code changes, run immediately without asking:**

```bash
taskkill //F //IM RVUCounter.exe 2>/dev/null; sleep 1; c:/Users/erik.richter/Desktop/dotnet/dotnet.exe publish "C:/Users/erik.richter/Desktop/RVUCounter/csharp/RVUCounter/RVUCounter.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true && "C:/Users/erik.richter/Desktop/RVUCounter/csharp/RVUCounter/bin/Release/net8.0-windows/win-x64/publish/RVUCounter.exe" &
```

**Output**: `csharp/RVUCounter/bin/Release/net8.0-windows/win-x64/publish/RVUCounter.exe`

## Tool Paths

- **dotnet**: `c:/Users/erik.richter/Desktop/dotnet/dotnet.exe`
- **gh CLI**: `"C:/Users/erik.richter/Desktop/GH CLI/gh.exe"`
- **sqlite3**: `"C:/Users/erik.richter/Desktop/sqlite-tools/sqlite3.exe"`

## Release Checklist

1. Update version in `Core/Config.cs` AND `RVUCounter.csproj`
2. Build (command above, without the `&& RVUCounter.exe &` part)
3. Copy to release:
   ```bash
   cp csharp/RVUCounter/bin/Release/net8.0-windows/win-x64/publish/RVUCounter.exe release/
   cp csharp/RVUCounter/Resources/rvu_rules.yaml release/resources/
   cp csharp/RVUCounter/Resources/tbwu_rules.db release/resources/
   ```
4. Create zip:
   ```bash
   cd release && rm -f RVUCounter.zip && powershell -Command "Compress-Archive -Path 'RVUCounter.exe','resources' -DestinationPath 'RVUCounter.zip' -Force"
   ```
5. Git commit and push
6. Create GitHub Release:
   ```bash
   "C:/Users/erik.richter/Desktop/GH CLI/gh.exe" release create vX.Y.Z --title "vX.Y.Z - Title" --notes "notes" release/RVUCounter.zip release/RVUCounter.exe
   ```

## Distribution Notes

**Always include `resources/` folder** - contains `rvu_rules.yaml` and `tbwu_rules.db` (loaded at runtime, not embedded).

## Key Features

- RVU tracking with Mosaic/Clario UI automation
- MosaicTools integration (named pipe, critical results)
- 11 statistics view modes with charts
- Auto-update (rename-based)
- Cloud backup (OneDrive/Dropbox)
- Themes (20 presets + custom)
- HIPAA-compliant accession hashing
- Patient info (memory-only, never persisted)
