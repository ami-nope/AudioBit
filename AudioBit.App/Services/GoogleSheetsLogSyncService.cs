using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AudioBit.App.Infrastructure;
using AudioBit.Core;
using AudioBit.Core.Diagnostics;
using Microsoft.Win32;

namespace AudioBit.App.Services;

internal sealed class GoogleSheetsLogSyncService : IDisposable
{
    private const string DefaultGoogleSheetsEndpointUrl = "https://script.google.com/macros/s/AKfycbyJ18zYC1bt84TBibdX4-4tmO3b9unFpC7gl54TUHYPY0mES3WJl8nAUYBuYKqYBd_o/exec";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ErrorContextWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ErrorContextCooldown = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeZoneInfo IndiaStandardTimeZone = ResolveIndiaStandardTimeZone();
    private static readonly object FileGate = new();

    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly Channel<QueuedLogUpload> _uploadQueue;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _processorTask;
    private readonly string _localLogPath;
    private readonly string _appVersion;
    private readonly object _errorContextGate = new();

    private bool _disposed;
    private DateTimeOffset _lastErrorContextUploadUtc = DateTimeOffset.MinValue;

    public GoogleSheetsLogSyncService(HttpClient? httpClient = null)
    {
        DeviceName = ResolveDeviceName();
        DeviceId = ResolveDeviceId();
        SheetName = SanitizeSheetName(DeviceName);
        EndpointUrl = GoogleSheetsEndpointResolver.Resolve(DefaultGoogleSheetsEndpointUrl);
        _appVersion = AppVersionInfo.GetDisplayVersion(Assembly.GetExecutingAssembly());
        _httpClient = httpClient ?? NetworkClientFactory.CreateHttpClient(DefaultTimeout, acceptHeader: "application/json");
        _disposeHttpClient = httpClient is null;
        _uploadQueue = Channel.CreateUnbounded<QueuedLogUpload>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        Directory.CreateDirectory(AudioBitPaths.LogsDirectoryPath);
        _localLogPath = Path.Combine(AudioBitPaths.LogsDirectoryPath, "audiobit.log");

        AppLog.EntryWritten += OnEntryWritten;
        WriteInternalDiagnostic($"Google Sheets endpoint resolved to '{EndpointUrl}'.");
        _processorTask = Task.Run(ProcessUploadsAsync);
    }

    public string EndpointUrl { get; }

    public string DeviceName { get; }

    public string SheetName { get; }

    public string DeviceId { get; }

    public async Task<RecentLogUploadResult> UploadRecentWindowAsync(TimeSpan window, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var safeWindow = window <= TimeSpan.Zero ? ErrorContextWindow : window;
        var entries = AppLog.GetRecentEntries(safeWindow, maxEntries: 1000);
        if (entries.Count == 0)
        {
            WriteInternalDiagnostic("Manual Google Sheets export skipped because there were no recent log entries.");
            return new RecentLogUploadResult(SheetName, null, null, 0, false, null);
        }

        var payload = BuildBatchPayload(
            entries,
            kind: "manual-window",
            level: AppLogLevel.Info,
            category: "ManualLogExport",
            message: $"Manual export for the last {safeWindow.TotalMinutes:N0} minute(s).",
            details: $"Exported {entries.Count} log entries from device '{DeviceName}'.");

        WriteInternalDiagnostic(
            $"Manual Google Sheets export started for {entries.Count} entries from the last {safeWindow.TotalMinutes:N0} minute(s). "
            + $"endpoint='{EndpointUrl}'");
        var receipt = await SendPayloadAsync(payload, cancellationToken).ConfigureAwait(false);
        WriteInternalDiagnostic(
            $"Manual Google Sheets export completed for {entries.Count} entries. "
            + $"requestedSheet='{SheetName}' confirmedSheet='{receipt.ConfirmedSheetName ?? "(unconfirmed)"}' "
            + $"createdSheet={FormatNullableBoolean(receipt.CreatedSheet)} response='{TruncateForDiagnostic(receipt.RawResponse)}'");
        return new RecentLogUploadResult(
            SheetName,
            receipt.ConfirmedSheetName,
            receipt.CreatedSheet,
            entries.Count,
            true,
            receipt.RawResponse);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AppLog.EntryWritten -= OnEntryWritten;
        _uploadQueue.Writer.TryComplete();

        if (!_processorTask.Wait(TimeSpan.FromSeconds(4)))
        {
            _disposeCts.Cancel();

            try
            {
                _processorTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
                
            }
        }

        _disposeCts.Dispose();

        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private void OnEntryWritten(AppLogEntry entry)
    {
        AppendUnifiedLog(entry);
        if (entry.Level == AppLogLevel.Trace)
        {
            return;
        }

        QueueUpload(new QueuedLogUpload(BuildSingleEntryPayload(entry)));

        if (entry.Level < AppLogLevel.Error)
        {
            return;
        }

        var shouldQueueContext = false;
        lock (_errorContextGate)
        {
            if (entry.TimestampUtc - _lastErrorContextUploadUtc >= ErrorContextCooldown)
            {
                _lastErrorContextUploadUtc = entry.TimestampUtc;
                shouldQueueContext = true;
            }
        }

        if (!shouldQueueContext)
        {
            return;
        }

        var contextEntries = AppLog.GetEntriesSince(entry.TimestampUtc - ErrorContextWindow, maxEntries: 250);
        if (contextEntries.Count == 0)
        {
            return;
        }

        var contextMessage = $"Automatic error context export triggered by '{entry.Category}'.";
        var contextDetails = $"Captured {contextEntries.Count} entries from the preceding {ErrorContextWindow.TotalMinutes:N0} minutes.";
        QueueUpload(new QueuedLogUpload(BuildBatchPayload(
            contextEntries,
            kind: "error-context",
            level: AppLogLevel.Error,
            category: entry.Category,
            message: contextMessage,
            details: contextDetails)));
    }

    private async Task ProcessUploadsAsync()
    {
        try
        {
            await foreach (var upload in _uploadQueue.Reader.ReadAllAsync(_disposeCts.Token).ConfigureAwait(false))
            {
                try
                {
                    await SendUploadAsync(upload, _disposeCts.Token).ConfigureAwait(false);
                    upload.TrySetCompleted();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    WriteInternalDiagnostic($"Google Sheets upload failed: {ex.Message}");
                    upload.TrySetFaulted(ex);
                }
            }
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
            
        }
    }

    private async Task SendUploadAsync(QueuedLogUpload upload, CancellationToken cancellationToken)
    {
        await SendPayloadAsync(upload.Payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GoogleSheetsUploadReceipt> SendPayloadAsync(GoogleSheetsLogPayload payload, CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var response = await _httpClient.PostAsJsonAsync(EndpointUrl, payload, JsonOptions, cancellationToken).ConfigureAwait(false);
                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var receipt = ParseUploadReceipt(content);

                if (!receipt.IsSuccess)
                {
                    WriteInternalDiagnostic($"Google Sheets upload returned an unexpected response: {content}");
                }

                return receipt;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;

                if (attempt == 3)
                {
                    break;
                }

                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastError ?? new InvalidOperationException("Google Sheets upload failed.");
    }

    private static GoogleSheetsUploadReceipt ParseUploadReceipt(string? content)
    {
        var normalized = string.IsNullOrWhiteSpace(content)
            ? null
            : content.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new GoogleSheetsUploadReceipt(true, null, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(normalized);
            var root = document.RootElement;
            var explicitSuccess = TryReadBoolean(root, "success");
            if (explicitSuccess is null)
            {
                var status = TryReadString(root, "status");
                explicitSuccess = string.IsNullOrWhiteSpace(status)
                    ? null
                    : string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase);
            }

            var confirmedSheetName = TryReadFirstString(root, "sheetName", "sheet", "tabName", "worksheetName", "destinationSheetName");
            var createdSheet = TryReadFirstBoolean(root, "createdSheet", "sheetCreated", "newSheetCreated", "created");
            return new GoogleSheetsUploadReceipt(explicitSuccess ?? true, confirmedSheetName, createdSheet, normalized);
        }
        catch (JsonException)
        {
            return new GoogleSheetsUploadReceipt(
                normalized.Contains("success", StringComparison.OrdinalIgnoreCase),
                null,
                null,
                normalized);
        }
    }

    private static string? TryReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool? TryReadBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static string? TryReadFirstString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = TryReadString(element, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static bool? TryReadFirstBoolean(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = TryReadBoolean(element, propertyName);
            if (value.HasValue)
            {
                return value.Value;
            }
        }

        return null;
    }

    private GoogleSheetsLogPayload BuildSingleEntryPayload(AppLogEntry entry)
    {
        return new GoogleSheetsLogPayload(
            SheetName,
            DeviceName,
            DeviceId,
            _appVersion,
            "single",
            FormatTimestampForGoogleSheets(entry.TimestampUtc),
            entry.SequenceId,
            entry.Level.ToString(),
            entry.Category,
            entry.Message,
            entry.Details,
            null,
            null,
            null);
    }

    private GoogleSheetsLogPayload BuildBatchPayload(
        IReadOnlyList<AppLogEntry> entries,
        string kind,
        AppLogLevel level,
        string category,
        string message,
        string details)
    {
        var exportedEntries = entries
            .Select(entry => new GoogleSheetsLogEntry(
                FormatTimestampForGoogleSheets(entry.TimestampUtc),
                entry.SequenceId,
                entry.Level.ToString(),
                entry.Category,
                entry.Message,
                entry.Details))
            .ToArray();

        return new GoogleSheetsLogPayload(
            SheetName,
            DeviceName,
            DeviceId,
            _appVersion,
            kind,
            FormatTimestampForGoogleSheets(DateTimeOffset.UtcNow),
            entries.Count == 0 ? 0 : entries[^1].SequenceId,
            level.ToString(),
            category,
            message,
            details,
            exportedEntries,
            entries.Count,
            ErrorContextWindow.TotalMinutes);
    }

    private void QueueUpload(QueuedLogUpload upload)
    {
        if (_disposed)
        {
            upload.TrySetFaulted(new ObjectDisposedException(nameof(GoogleSheetsLogSyncService)));
            return;
        }

        if (!_uploadQueue.Writer.TryWrite(upload))
        {
            upload.TrySetFaulted(new InvalidOperationException("Google Sheets upload queue is unavailable."));
        }
    }

    private void AppendUnifiedLog(AppLogEntry entry)
    {
        var builder = new StringBuilder();
        builder.AppendLine(entry.ToDisplayLine());

        if (!string.IsNullOrWhiteSpace(entry.Details))
        {
            using var reader = new StringReader(entry.Details);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                builder.Append("    ").AppendLine(line);
            }
        }

        lock (FileGate)
        {
            File.AppendAllText(_localLogPath, builder.ToString());
        }
    }

    private void WriteInternalDiagnostic(string message)
    {
        var line = $"[{DateTimeOffset.UtcNow:O}] [GoogleSheetsSync] {message}{Environment.NewLine}";

        lock (FileGate)
        {
            File.AppendAllText(_localLogPath, line);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static string FormatNullableBoolean(bool? value)
    {
        return value.HasValue ? value.Value.ToString() : "(unconfirmed)";
    }

    private static string TruncateForDiagnostic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(empty)";
        }

        var normalized = value.Trim();
        return normalized.Length <= 200
            ? normalized
            : normalized[..200];
    }

    private static string ResolveDeviceName()
    {
        return string.IsNullOrWhiteSpace(Environment.MachineName)
            ? "Unknown Device"
            : Environment.MachineName.Trim();
    }

    private static string ResolveDeviceId()
    {
        try
        {
            var machineGuid = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null) as string;
            if (!string.IsNullOrWhiteSpace(machineGuid))
            {
                return machineGuid.Trim();
            }
        }
        catch
        {
            
        }

        return ResolveDeviceName();
    }

    private static string FormatTimestampForGoogleSheets(DateTimeOffset timestamp)
    {
        var indiaTimestamp = TimeZoneInfo.ConvertTime(timestamp, IndiaStandardTimeZone);
        return indiaTimestamp.ToString("dd-MMM-yy || hh:mm tt", CultureInfo.InvariantCulture);
    }

    private static TimeZoneInfo ResolveIndiaStandardTimeZone()
    {
        foreach (var timeZoneId in new[] { "India Standard Time", "Asia/Kolkata" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone(
            "AudioBit.IndiaStandardTime",
            TimeSpan.FromMinutes(330),
            "(UTC+05:30) India Standard Time",
            "India Standard Time");
    }

    private static string SanitizeSheetName(string? value)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? "Unknown Device" : value.Trim();
        var invalidCharacters = new HashSet<char>(['[', ']', '*', '?', '/', '\\']);
        var builder = new StringBuilder(candidate.Length);

        foreach (var character in candidate)
        {
            builder.Append(invalidCharacters.Contains(character) ? '_' : character);
        }

        var sanitized = builder.ToString().Trim().Trim('\'');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "Unknown Device";
        }

        return sanitized.Length <= 95
            ? sanitized
            : sanitized[..95];
    }

    private sealed class QueuedLogUpload
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public QueuedLogUpload(GoogleSheetsLogPayload payload, bool waitForCompletion = false)
        {
            Payload = payload;
            WaitForCompletion = waitForCompletion;

            if (!waitForCompletion)
            {
                _completion.TrySetResult();
            }
        }

        public GoogleSheetsLogPayload Payload { get; }

        public bool WaitForCompletion { get; }

        public Task WaitForCompletionAsync(CancellationToken cancellationToken)
        {
            return _completion.Task.WaitAsync(cancellationToken);
        }

        public void TrySetCompleted()
        {
            if (!WaitForCompletion)
            {
                return;
            }

            _completion.TrySetResult();
        }

        public void TrySetFaulted(Exception exception)
        {
            if (!WaitForCompletion)
            {
                return;
            }

            _completion.TrySetException(exception);
        }
    }

    private sealed record GoogleSheetsLogPayload(
        string SheetName,
        string DeviceName,
        string DeviceId,
        string AppVersion,
        string Kind,
        string Timestamp,
        long SequenceId,
        string Level,
        string Category,
        string Message,
        string? Details,
        IReadOnlyList<GoogleSheetsLogEntry>? Entries,
        int? EntryCount,
        double? WindowMinutes);

    private sealed record GoogleSheetsLogEntry(
        string Timestamp,
        long SequenceId,
        string Level,
        string Category,
        string Message,
        string? Details);
}

internal sealed record RecentLogUploadResult(
    string RequestedSheetName,
    string? ConfirmedSheetName,
    bool? CreatedSheet,
    int EntryCount,
    bool Uploaded,
    string? RawResponse);

internal sealed record GoogleSheetsUploadReceipt(
    bool IsSuccess,
    string? ConfirmedSheetName,
    bool? CreatedSheet,
    string? RawResponse);
