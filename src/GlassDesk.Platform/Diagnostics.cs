using System.Text.Json;
using System.Runtime.InteropServices;
using GlassDesk.Core;

namespace GlassDesk.Platform;

/// <summary>Bounded in-memory system/capability diagnostics. Never accepts song strings or arbitrary exception messages.</summary>
public sealed class Diagnostics
{
    private readonly object _gate = new();
    private readonly Queue<DiagnosticEntry> _entries = new();
    private MediaCapabilities _capabilities = new();
    private bool _connected;
    public void RecordError(string component, Exception error) => Record(component, "error", error.GetType().Name, error.HResult);
    public void RecordWin32Error(string component, int error) => Record(component, "win32-error", null, error);
    public void RecordCapability(bool connected, MediaCapabilities capabilities)
    {
        lock (_gate) { _connected = connected; _capabilities = capabilities; }
    }
    public void RecordEvent(string component, string eventCode) => Record(component, eventCode, null, null);

    private void Record(string component, string eventCode, string? errorType, int? errorCode)
    {
        lock (_gate)
        {
            _entries.Enqueue(new(DateTimeOffset.UtcNow, component, eventCode, errorType, errorCode));
            while (_entries.Count > 128) _entries.Dequeue();
        }
    }

    public async Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        object report;
        lock (_gate)
        {
            report = new
            {
                SchemaVersion = 1, App = "GlassDesk", Version = "0.2.2", CapturedAt = DateTimeOffset.UtcNow,
                OperatingSystem = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Framework = RuntimeInformation.FrameworkDescription,
                MediaSource = NetEaseMediaAdapter.SourceAppId, Connected = _connected, Capabilities = _capabilities,
                Entries = _entries.ToArray()
            };
        }
        var absolutePath = Path.GetFullPath(destinationPath);
        await File.WriteAllTextAsync(absolutePath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), cancellationToken)
            .ConfigureAwait(false);
    }
    private sealed record DiagnosticEntry(DateTimeOffset At, string Component, string EventCode, string? ErrorType, int? ErrorCode);
}
