#if GLASSDESK_UI_PREVIEW && GLASSDESK_SYSTEM_GLASS_PREVIEW
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace GlassDesk.App;

/// <summary>
/// Private preview-only renderer state input. It never creates or moves OS pointer input;
/// playlist transitions are fenced to the synthetic adapter and only change the form/track fixture.
/// </summary>
internal sealed class QaTransportFeedback : IDisposable
{
    const int MaxInputBytes = 64;
    const int MaxAckBytes = 8 * 1024;
    const double SettleDelayMs = 700;
    static readonly JsonSerializerOptions AckJsonOptions = new() { MaxDepth = 8 };
    static double Now => Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;

    readonly string _inputPath, _ackPath;
    readonly DispatcherQueueTimer _timer;
    readonly Func<bool> _stopped;
    readonly Action<string> _apply;
    readonly Func<object> _describe;
    readonly Action<Exception> _fail;
    string? _lastAppliedState, _pendingState;
    object? _duringMotion;
    double _settleAt;
    bool _disposed, _failed, _timerStopped;

    public QaTransportFeedback(string dataDirectory, DispatcherQueue queue,
        Func<bool> stopped, Action<string> apply, Func<object> describe, Action<Exception> fail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(queue);
        _stopped = stopped ?? throw new ArgumentNullException(nameof(stopped));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _describe = describe ?? throw new ArgumentNullException(nameof(describe));
        _fail = fail ?? throw new ArgumentNullException(nameof(fail));

        string directory = Path.GetFullPath(dataDirectory);
        FileAttributes directoryAttributes = File.GetAttributes(directory);
        if ((directoryAttributes & FileAttributes.Directory) == 0
            || (directoryAttributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("QA transport feedback requires a regular app data directory.");
        _inputPath = FixedChildPath(directory, "qa-transport-feedback.txt");
        _ackPath = FixedChildPath(directory, "qa-transport-feedback-ack.json");

        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.IsRepeating = true;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    static string FixedChildPath(string directory, string name)
    {
        string path = Path.GetFullPath(Path.Combine(directory, name));
        string prefix = Path.EndsInDirectorySeparator(directory)
            ? directory : directory + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("QA transport feedback path escaped its data directory.");
        return path;
    }

    internal static bool IsAllowedState(string state)
        => state is "rest" or "hover" or "pressed" or "release" or "next-details" or "previous-peek";

    void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || _failed) return;
        try
        {
            if (!CanRun()) return;
            string? state = ReadState();
            if (state is not null && !string.Equals(state, _lastAppliedState, StringComparison.Ordinal))
            {
                if (!CanRun()) return;
                _apply(state);
                if (!CanRun()) return;

                _lastAppliedState = state;
                _pendingState = state;
                _settleAt = Now + SettleDelayMs;
                _duringMotion = _describe();
                if (!CanRun() || !WriteAck("active", settledMotion: null)) return;
            }

            if (_pendingState is not null && Now >= _settleAt)
            {
                if (!CanRun()) return;
                object settledMotion = _describe();
                if (!CanRun() || !WriteAck("settled", settledMotion)) return;
                _pendingState = null;
                _duringMotion = null;
            }
        }
        catch (Exception error) { FailStop(error); }
    }

    bool CanRun()
    {
        if (_disposed || _failed) return false;
        if (_stopped())
        {
            StopTimer();
            return false;
        }
        return true;
    }

    string? ReadState()
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(_inputPath); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("QA transport feedback input cannot be a reparse point.");
        if ((attributes & FileAttributes.Directory) != 0)
            throw new InvalidDataException("QA transport feedback input must be a regular file.");

        byte[] bytes = new byte[MaxInputBytes + 1];
        int length;
        try
        {
            using var input = new FileStream(_inputPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, MaxInputBytes + 1, FileOptions.SequentialScan);
            if (input.Length > MaxInputBytes)
                throw new InvalidDataException("QA transport feedback input exceeds 64 bytes.");
            length = 0;
            while (length < bytes.Length)
            {
                int read = input.Read(bytes, length, bytes.Length - length);
                if (read == 0) break;
                length += read;
            }
            if (length > MaxInputBytes || input.Length > MaxInputBytes)
                throw new InvalidDataException("QA transport feedback input exceeds 64 bytes.");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }

        if (length == 0) throw new InvalidDataException("QA transport feedback input is empty.");
        for (int i = 0; i < length; i++)
            if (bytes[i] > 0x7f) throw new InvalidDataException("QA transport feedback input must be ASCII.");

        string state = Encoding.ASCII.GetString(bytes, 0, length).Trim(' ', '\t', '\r', '\n');
        if (!IsAllowedState(state))
            throw new InvalidDataException("QA transport feedback accepts only rest, hover, pressed, release, next-details, or previous-peek.");
        return state;
    }

    bool WriteAck(string phase, object? settledMotion)
    {
        if (!CanRun()) return false;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1,
            State = _pendingState,
            Phase = phase,
            CapturedAt = DateTimeOffset.UtcNow,
            DuringMotion = _duringMotion,
            SettledMotion = settledMotion
        }, AckJsonOptions);
        if (payload.Length > MaxAckBytes)
            throw new InvalidDataException("QA transport feedback acknowledgement exceeds 8 KB.");

        RejectAckReparsePoint();
        if (!CanRun()) return false;
        using var output = new FileStream(_ackPath, FileMode.Create, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, options: FileOptions.SequentialScan);
        output.Write(payload);
        output.Flush();
        return true;
    }

    void RejectAckReparsePoint()
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(_ackPath); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException("QA transport feedback acknowledgement target must be a regular file.");
    }

    void FailStop(Exception error)
    {
        if (_failed || _disposed) return;
        _failed = true;
        StopTimer();
        try { _fail(error); }
        catch { /* Keep a private preview failure from escaping a timer callback. */ }
    }

    void StopTimer()
    {
        if (_timerStopped) return;
        _timerStopped = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopTimer();
    }
}
#endif
