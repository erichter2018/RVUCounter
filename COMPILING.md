# Compiling RVU Counter

## Prerequisites
- **Dotnet Location**: The `dotnet` executable is located at `c:\Users\erik.richter\Desktop\dotnet\dotnet.exe`.
  - **Note**: It is **NOT** in the system PATH, so you must use the full path or the user-specified location.

## Build Steps

### 1. Kill Running Instance
Before building, ensure the application is not running to avoid file ownership/lock errors.

```powershell
Stop-Process -Name "RVUCounter" -ErrorAction SilentlyContinue
```

### 2. Run Build Command
Run the following command from the C# project directory: `c:\Users\erik.richter\Desktop\RVUCounter\csharp\RVUCounter`.

```powershell
& "c:\Users\erik.richter\Desktop\dotnet\dotnet.exe" publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "bin\Release\net8.0-windows\win-x64\publish"
```

## Output
The executable will be located at:
`c:\Users\erik.richter\Desktop\RVUCounter\csharp\RVUCounter\bin\Release\net8.0-windows\win-x64\publish\RVUCounter.exe`
