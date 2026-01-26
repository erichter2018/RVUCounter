using System.IO;
using Serilog;
using Serilog.Events;

namespace RVUCounter.Core;

/// <summary>
/// Logging configuration using Serilog.
/// Provides FIFO log trimming similar to Python version.
/// </summary>
public static class LoggingConfig
{
    private static bool _isInitialized;
    private static string? _logFilePath;

    /// <summary>
    /// Initialize Serilog with file and console sinks.
    /// </summary>
    /// <param name="baseDir">Application base directory</param>
    public static void Initialize(string baseDir)
    {
        if (_isInitialized) return;

        var logsDir = Config.GetLogsPath(baseDir);
        Directory.CreateDirectory(logsDir);

        _logFilePath = Path.Combine(logsDir, "rvu_counter.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                _logFilePath,
                rollingInterval: RollingInterval.Infinite,
                fileSizeLimitBytes: Config.LogMaxBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 2,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        _isInitialized = true;

        Log.Information("======================================");
        Log.Information("RVU Counter Starting - v{Version}", Config.AppVersion);
        Log.Information("======================================");
    }

    /// <summary>
    /// Close and flush the logger.
    /// </summary>
    public static void Shutdown()
    {
        Log.Information("RVU Counter Shutting Down");
        Log.CloseAndFlush();
    }

    /// <summary>
    /// Get the current log file path.
    /// </summary>
    public static string? LogFilePath => _logFilePath;
}
