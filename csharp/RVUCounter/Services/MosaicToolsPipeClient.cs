using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace RVUCounter.Services;

/// <summary>
/// Named pipe client that connects to MosaicTools' pipe server.
/// Receives study data and study events, sends shift info back.
/// Auto-reconnects when the pipe breaks.
/// </summary>
public sealed class MosaicToolsPipeClient : IDisposable
{
    private const string PipeName = "MosaicToolsPipe";
    private const int ReconnectDelayMs = 3000;
    private const int ReadBufferSize = 8192;

    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _cts;
    private Task? _connectionTask;
    private readonly object _writeLock = new();
    private readonly object _stateLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // --- Public state ---

    /// <summary>Whether the pipe is currently connected to MosaicTools.</summary>
    public bool IsConnected
    {
        get
        {
            lock (_stateLock)
                return _pipe?.IsConnected == true;
        }
    }

    /// <summary>Latest study data received from MosaicTools.</summary>
    public PipeStudyData? LatestStudyData
    {
        get { lock (_stateLock) return _latestStudyData; }
        private set { lock (_stateLock) _latestStudyData = value; }
    }
    private PipeStudyData? _latestStudyData;

    /// <summary>When the last message was received.</summary>
    public DateTime? LastDataReceived
    {
        get { lock (_stateLock) return _lastDataReceived; }
        private set { lock (_stateLock) _lastDataReceived = value; }
    }
    private DateTime? _lastDataReceived;

    // --- Events ---

    /// <summary>Fired on the thread pool when a study_event message arrives.</summary>
    public event Action<PipeStudyEvent>? StudyEventReceived;

    /// <summary>Fired when connection state changes (connected or disconnected).</summary>
    public event Action<bool>? ConnectionStateChanged;

    // --- Lifecycle ---

    /// <summary>Start the background connect + read loop.</summary>
    public void Start()
    {
        if (_cts != null) return; // already started
        _cts = new CancellationTokenSource();
        _connectionTask = Task.Run(() => ConnectLoop(_cts.Token));
        Log.Information("MosaicToolsPipeClient started");
    }

    /// <summary>Stop the client, disconnect, cancel background loop.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        DisconnectPipe();
        Log.Information("MosaicToolsPipeClient stopped");
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _cts = null;
    }

    // --- Send shift_info to MosaicTools ---

    /// <summary>
    /// Send current shift info to MosaicTools.
    /// Safe to call from any thread; silently ignored if not connected.
    /// </summary>
    public void SendShiftInfo(double totalRvu, int recordCount, string? shiftStart, bool isActive,
                              double? currentHourRvu = null, double? priorHourRvu = null, double? estimatedTotalRvu = null)
    {
        var msg = new PipeMessage
        {
            Type = "shift_info",
            TotalRvu = totalRvu,
            RecordCount = recordCount,
            ShiftStart = shiftStart,
            IsShiftActive = isActive,
            CurrentHourRvu = currentHourRvu,
            PriorHourRvu = priorHourRvu,
            EstimatedTotalRvu = estimatedTotalRvu
        };

        SendMessage(msg);
    }

    // --- Internal: connect loop ---

    private async Task ConnectLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Log.Debug("MosaicToolsPipeClient: Attempting to connect to {Pipe}", PipeName);

                var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

                // ConnectAsync with a short timeout so we can retry quickly
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(TimeSpan.FromSeconds(5));

                try
                {
                    await pipe.ConnectAsync(connectCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timeout connecting - MosaicTools probably not running. Retry.
                    pipe.Dispose();
                    await Task.Delay(ReconnectDelayMs, ct);
                    continue;
                }

                lock (_stateLock)
                    _pipe = pipe;

                Log.Information("MosaicToolsPipeClient: Connected to MosaicTools pipe");
                ConnectionStateChanged?.Invoke(true);

                // Read loop - runs until pipe breaks or cancellation
                await ReadLoop(pipe, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break; // Clean shutdown
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "MosaicToolsPipeClient: Connection error");
            }

            // Disconnected - clean up and retry
            DisconnectPipe();
            ConnectionStateChanged?.Invoke(false);
            LatestStudyData = null;

            if (!ct.IsCancellationRequested)
            {
                Log.Debug("MosaicToolsPipeClient: Reconnecting in {Delay}ms", ReconnectDelayMs);
                try { await Task.Delay(ReconnectDelayMs, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    // --- Internal: read loop ---

    private async Task ReadLoop(NamedPipeClientStream pipe, CancellationToken ct)
    {
        var lengthBuffer = new byte[4];

        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            // Read 4-byte length prefix
            int bytesRead = await ReadExactAsync(pipe, lengthBuffer, 0, 4, ct);
            if (bytesRead < 4)
                break; // Pipe closed

            int messageLength = BitConverter.ToInt32(lengthBuffer, 0);
            if (messageLength <= 0 || messageLength > 1_000_000)
            {
                Log.Warning("MosaicToolsPipeClient: Invalid message length {Length}, disconnecting", messageLength);
                break;
            }

            // Read the JSON payload
            var jsonBuffer = new byte[messageLength];
            bytesRead = await ReadExactAsync(pipe, jsonBuffer, 0, messageLength, ct);
            if (bytesRead < messageLength)
                break; // Pipe closed

            var json = Encoding.UTF8.GetString(jsonBuffer);
            ProcessMessage(json);
        }
    }

    /// <summary>Read exactly <paramref name="count"/> bytes, returns actual count read (less means EOF).</summary>
    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), ct);
            if (read == 0)
                return totalRead; // EOF
            totalRead += read;
        }
        return totalRead;
    }

    // --- Internal: message processing ---

    private void ProcessMessage(string json)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<PipeMessage>(json, JsonOptions);
            if (msg == null) return;

            LastDataReceived = DateTime.Now;

            switch (msg.Type)
            {
                case "study_data":
                    LatestStudyData = new PipeStudyData
                    {
                        Accession = msg.Accession,
                        Description = msg.Description,
                        TemplateName = msg.TemplateName,
                        PatientName = msg.PatientName,
                        PatientGender = msg.PatientGender,
                        SiteCode = msg.SiteCode,
                        Mrn = msg.Mrn,
                        ClarioClass = msg.ClarioClass,
                        ClarioPriority = msg.ClarioPriority,
                        Drafted = msg.Drafted ?? false,
                        HasCritical = msg.HasCritical ?? false,
                        Timestamp = msg.Timestamp
                    };
                    Log.Debug("Pipe: study_data received - Accession={Accession}, Desc={Desc}, Priority={Priority}, Class={Class}",
                        msg.Accession ?? "(null)", msg.Description ?? "(null)",
                        msg.ClarioPriority ?? "(null)", msg.ClarioClass ?? "(null)");
                    break;

                case "study_event":
                    var evt = new PipeStudyEvent
                    {
                        EventType = msg.EventType ?? "",
                        Accession = msg.Accession ?? "",
                        HasCritical = msg.HasCritical ?? false
                    };
                    Log.Information("Pipe: study_event received - {EventType} {Accession} (critical={Critical})",
                        evt.EventType, evt.Accession, evt.HasCritical);
                    StudyEventReceived?.Invoke(evt);
                    break;

                default:
                    Log.Debug("Pipe: Unknown message type '{Type}'", msg.Type);
                    break;
            }
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "MosaicToolsPipeClient: Failed to deserialize message");
        }
    }

    // --- Internal: send ---

    private void SendMessage(PipeMessage msg)
    {
        lock (_writeLock)
        {
            var pipe = _pipe;
            if (pipe == null || !pipe.IsConnected) return;

            try
            {
                var json = JsonSerializer.Serialize(msg, JsonOptions);
                var jsonBytes = Encoding.UTF8.GetBytes(json);
                var lengthBytes = BitConverter.GetBytes(jsonBytes.Length);

                pipe.Write(lengthBytes, 0, 4);
                pipe.Write(jsonBytes, 0, jsonBytes.Length);
                pipe.Flush();

                Log.Debug("Pipe: Sent {Type} message ({Length} bytes)", msg.Type, jsonBytes.Length);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "MosaicToolsPipeClient: Failed to send message");
            }
        }
    }

    // --- Send distraction_alert to MosaicTools ---

    /// <summary>
    /// Send a distraction alert to MosaicTools when a study has been open too long.
    /// MosaicTools will play escalating beeps based on alertLevel.
    /// </summary>
    public void SendDistractionAlert(string? studyType, double elapsedSeconds, double expectedSeconds, int alertLevel)
    {
        var msg = new PipeMessage
        {
            Type = "distraction_alert",
            StudyType = studyType,
            ElapsedSeconds = elapsedSeconds,
            ExpectedSeconds = expectedSeconds,
            AlertLevel = alertLevel
        };

        SendMessage(msg);
        Log.Information("Pipe: Sent distraction_alert level={Level} for {StudyType} (elapsed={Elapsed:F0}s, expected={Expected:F0}s)",
            alertLevel, studyType ?? "Unknown", elapsedSeconds, expectedSeconds);
    }

    // --- Internal: cleanup ---

    private void DisconnectPipe()
    {
        lock (_stateLock)
        {
            if (_pipe != null)
            {
                try { _pipe.Dispose(); } catch { }
                _pipe = null;
            }
        }
    }
}

// ===========================================
// Message DTOs
// ===========================================

/// <summary>
/// Unified JSON message envelope for all pipe messages.
/// Fields are nullable; only relevant fields are populated per message type.
/// </summary>
internal class PipeMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    // study_data fields (MT → RVU)
    [JsonPropertyName("accession")]
    public string? Accession { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("templateName")]
    public string? TemplateName { get; set; }

    [JsonPropertyName("patientName")]
    public string? PatientName { get; set; }

    [JsonPropertyName("patientGender")]
    public string? PatientGender { get; set; }

    [JsonPropertyName("siteCode")]
    public string? SiteCode { get; set; }

    [JsonPropertyName("mrn")]
    public string? Mrn { get; set; }

    [JsonPropertyName("clarioClass")]
    public string? ClarioClass { get; set; }

    [JsonPropertyName("clarioPriority")]
    public string? ClarioPriority { get; set; }

    [JsonPropertyName("drafted")]
    public bool? Drafted { get; set; }

    [JsonPropertyName("hasCritical")]
    public bool? HasCritical { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    // study_event fields (MT → RVU)
    [JsonPropertyName("eventType")]
    public string? EventType { get; set; }

    // shift_info fields (RVU → MT)
    [JsonPropertyName("totalRvu")]
    public double? TotalRvu { get; set; }

    [JsonPropertyName("recordCount")]
    public int? RecordCount { get; set; }

    [JsonPropertyName("shiftStart")]
    public string? ShiftStart { get; set; }

    [JsonPropertyName("isShiftActive")]
    public bool? IsShiftActive { get; set; }

    [JsonPropertyName("currentHourRvu")]
    public double? CurrentHourRvu { get; set; }

    [JsonPropertyName("priorHourRvu")]
    public double? PriorHourRvu { get; set; }

    [JsonPropertyName("estimatedTotalRvu")]
    public double? EstimatedTotalRvu { get; set; }

    // distraction_alert fields (RVU → MT)
    [JsonPropertyName("studyType")]
    public string? StudyType { get; set; }

    [JsonPropertyName("elapsedSeconds")]
    public double? ElapsedSeconds { get; set; }

    [JsonPropertyName("expectedSeconds")]
    public double? ExpectedSeconds { get; set; }

    [JsonPropertyName("alertLevel")]
    public int? AlertLevel { get; set; }
}

/// <summary>Study data received from MosaicTools via pipe.</summary>
public class PipeStudyData
{
    public string? Accession { get; set; }
    /// <summary>Procedure description (e.g., "CT HEAD WO CONTRAST")</summary>
    public string? Description { get; set; }
    /// <summary>Template name from PowerScribe (e.g., "CT Head")</summary>
    public string? TemplateName { get; set; }
    public string? PatientName { get; set; }
    public string? PatientGender { get; set; }
    public string? SiteCode { get; set; }
    public string? Mrn { get; set; }
    /// <summary>Clario patient class (e.g., "IP", "OP", "ED")</summary>
    public string? ClarioClass { get; set; }
    /// <summary>Clario priority (e.g., "STAT", "Routine")</summary>
    public string? ClarioPriority { get; set; }
    /// <summary>Whether a draft report exists</summary>
    public bool Drafted { get; set; }
    public bool HasCritical { get; set; }
    public string? Timestamp { get; set; }
}

/// <summary>Study event (signed/unsigned) received from MosaicTools via pipe.</summary>
public class PipeStudyEvent
{
    /// <summary>"signed" or "unsigned"</summary>
    public string EventType { get; set; } = "";
    public string Accession { get; set; } = "";
    public bool HasCritical { get; set; }
}
