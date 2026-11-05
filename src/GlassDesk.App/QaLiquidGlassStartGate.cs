#if GLASSDESK_UI_PREVIEW && GLASSDESK_SYSTEM_GLASS_PREVIEW
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlassDesk.App;

/// <summary>
/// QA-preview-only pre-XAML gate. It reads a root-owned JSON projection under the
/// explicit preview data directory; it has no WinRT, HWND, graphics or pixel path.
/// </summary>
internal static class QaLiquidGlassStartGate
{
    private const string GateStem = "--qa-system-compositor-start-gate";
    private const string GatePrefix = GateStem + "=";
    private const string DeadlineStem = "--qa-system-compositor-root-work-deadline-ticks";
    private const string DeadlinePrefix = DeadlineStem + "=";
    private const string FrequencyStem = "--qa-system-compositor-stopwatch-frequency";
    private const string FrequencyPrefix = FrequencyStem + "=";
    private const string GateFileName = "qa-system-compositor-start-gate.json";
    private const int MaximumBytes = 65536;
    private const double FreshSeconds = 1.5;
    private const double CimReadToleranceSeconds = 0.15;

    internal const string ProducerSha256 = "0B839767C5FCE69BB2AE9B0D01B6A2A82B6E16032FEE911345ADB60586007550";
    internal const string CoreSha256 = "40CDC6E045A12F00B744201235DC829C6C6AB53746756CBE881AE7734B0320C5";
    internal const string ConsumerSha256 = "A4B7D5814A6AA80274CBCE12E817D4A4466C17F7DF6A86C6E170796D617F39E6";
    internal const string WatchdogSha256 = "CA281669669DDE4A44405FF04217B96DEA9FC4D544B9F0867E940103DBD8B1B7";
    internal const string BindingSha256 = "2F38424EB161D527F9CA348614EEE776405B19BF23CC00AC6A531C4EA21D5828";

    private static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow
    };

    internal enum Disposition { Pending, Accepted, Rejected }
    internal sealed record Validation(Disposition Disposition, string Reason);
    internal sealed record Request(string RunToken, long RootWorkDeadlineStopwatchTicks, long StopwatchFrequency);
    internal sealed record SelfIdentity(int Pid, int SessionId, string ProcessName, string StartUtc, long StartTimeTicks, string ExecutablePath);

    // Envelope fields RunToken, SourcePins and DwmIdentity reuse the r96 owner-control/result names.
    // Nested objects are strict projections of the existing r96 host-identity, r84 watchdog/heartbeat,
    // and r82 producer state fields. RootWorkDeadlineStopwatchTicks is the one new absolute gate deadline.
    internal sealed class GateEnvelope
    {
        public int? SchemaVersion { get; init; }
        public string? RunToken { get; init; }
        public long? RootWorkDeadlineStopwatchTicks { get; init; }
        public long? StopwatchFrequency { get; init; }
        public SourcePins? SourcePins { get; init; }
        public HostIdentity? HostIdentity { get; init; }
        public DwmIdentity? DwmIdentity { get; init; }
        public WatchdogState? WatchdogState { get; init; }
        public WatchdogHeartbeat? WatchdogHeartbeat { get; init; }
        public ProducerState? ProducerState { get; init; }
    }

    internal sealed class SourcePins
    {
        public string? Producer { get; init; }
        public string? Core { get; init; }
        public string? Consumer { get; init; }
        public string? Watchdog { get; init; }
        public string? Binding { get; init; }
    }
    internal sealed class HostIdentity
    {
        public int? Pid { get; init; }
        public int? SessionId { get; init; }
        public string? ProcessName { get; init; }
        public string? StartUtc { get; init; }
        public string? StartTimeTicks { get; init; }
        public string? ExecutablePath { get; init; }
        public int? OwnerPid { get; init; }
    }
    internal sealed class DwmIdentity
    {
        public int? Pid { get; init; }
        public int? SessionId { get; init; }
        public string? CreationDateUtc { get; init; }
    }
    internal sealed class PinnedMonitorSources
    {
        public string? ProducerSourceSha256 { get; init; }
        public string? CoreSourceSha256 { get; init; }
        public string? ConsumerSourceSha256 { get; init; }
    }
    internal sealed class WatchdogState
    {
        public string? State { get; init; }
        public string? AtUtc { get; init; }
        public string? StartedUtc { get; init; }
        public int? Pid { get; init; }
        public int? SessionId { get; init; }
        public string? TargetStartUtc { get; init; }
        public int[]? BaselineDwm { get; init; }
        public string? Reason { get; init; }
        public bool? KillRequested { get; init; }
        public bool? ExitConfirmed { get; init; }
        public bool? Killed { get; init; }
        public bool? ScreenRead { get; init; }
        public bool? DwmMonitorGuardEnabled { get; init; }
        public string? DwmMonitorState { get; init; }
        public bool? DwmMonitorProcessVerified { get; init; }
        public PinnedMonitorSources? DwmMonitorPinnedSource { get; init; }
    }
    internal sealed class HeartbeatHostIdentity
    {
        public int? Pid { get; init; }
        public int? SessionId { get; init; }
        public string? ProcessName { get; init; }
        public string? StartUtc { get; init; }
    }
    internal sealed class WatchdogHeartbeat
    {
        public HeartbeatHostIdentity? HostIdentity { get; init; }
        public string? AtUtc { get; init; }
        public string? LatestValidatedCimAtUtc { get; init; }
        public string? LatestValidatedCimSampleUtc { get; init; }
        public string? LatestValidatedCimState { get; init; }
        public string? LatestValidatedCimReason { get; init; }
        public PinnedMonitorSources? PinnedSource { get; init; }
    }
    internal sealed class ProducerState
    {
        public int? SchemaVersion { get; init; }
        public string? MonitorKind { get; init; }
        public string? Source { get; init; }
        public string? RealMethod { get; init; }
        public bool? SyntheticState { get; init; }
        public string? State { get; init; }
        public string? AtUtc { get; init; }
        public long? StopwatchFrequency { get; init; }
        public int? LogicalProcessorCount { get; init; }
        public bool? TargetStopped { get; init; }
        public int? ExpectedPid { get; init; }
        public int? ExpectedSessionId { get; init; }
        public string? ExpectedName { get; init; }
        public string? ExpectedCreationDateUtc { get; init; }
        public int? ObservedPid { get; init; }
        public int? ObservedSessionId { get; init; }
        public string? ObservedName { get; init; }
        public string? ObservedCreationDateUtc { get; init; }
        public int? SampleCount { get; init; }
        public int? ValidIntervalCount { get; init; }
        public int? ConsecutiveHighIntervals { get; init; }
        public string? NormalizedLogicalCpuPercent { get; init; }
        public string? LatestSampleUtc { get; init; }
        public string? ProducerSourceSha256 { get; init; }
        public string? CoreSourceSha256 { get; init; }
    }

    internal static bool IsQaInvocation(string[] args) =>
        args.Any(a => a == "--system-compositor-preview" ||
            a.StartsWith(GateStem, StringComparison.Ordinal) ||
            a.StartsWith(DeadlineStem, StringComparison.Ordinal) ||
            a.StartsWith(FrequencyStem, StringComparison.Ordinal));

    /// <summary>Pure argument/options preflight. Call before any single-instance IPC.</summary>
    internal static void ValidateLaunchRequest(string[] args, LaunchOptions options) =>
        _ = ParseRequest(args, options);

    /// <summary>Called after CreateSession and before COM/XAML startup. Preview always requires a gate.</summary>
    internal static void WaitIfRequired(string[] args, LaunchOptions options)
    {
        Request? request = ParseRequest(args, options);
        if (request is null) return;
        if (options.DataDirectory is null || !options.ExplicitDataDirectory)
            throw new InvalidOperationException("QA startup gate requires an explicit isolated data directory.");

        string dataDirectory = Path.GetFullPath(options.DataDirectory);
        AssertNoReparseDirectoryChain(dataDirectory);
        string gatePath = Path.GetFullPath(Path.Combine(dataDirectory, GateFileName));
        if (!IsDirectChild(dataDirectory, gatePath))
            throw new InvalidOperationException("QA startup gate path escaped its isolated data directory.");

        SelfIdentity self = ReadSelfIdentity();
        long startTicks = Stopwatch.GetTimestamp();
        long localDeadline = checked(startTicks + checked(5L * Stopwatch.Frequency));
        long effectiveDeadline = Math.Min(localDeadline, request.RootWorkDeadlineStopwatchTicks);
        if (startTicks >= effectiveDeadline)
            throw new InvalidOperationException("QA startup gate root deadline has already expired.");

        while (Stopwatch.GetTimestamp() < effectiveDeadline)
        {
            byte[]? bytes = ReadGateSnapshot(gatePath, dataDirectory);
            if (bytes is not null)
            {
                GateEnvelope envelope = DeserializeStrict(bytes);
                Validation result = Validate(envelope, request, self, DateTimeOffset.UtcNow,
                    Stopwatch.GetTimestamp(), localDeadline);
                if (result.Disposition == Disposition.Accepted) return;
                if (result.Disposition == Disposition.Rejected)
                    throw new InvalidOperationException("QA startup gate rejected: " + result.Reason);
            }
            SleepAtMost(effectiveDeadline, 50);
        }
        throw new InvalidOperationException("QA startup gate did not reach exact RUNNING before the monotonic deadline.");
    }

    private static Request? ParseRequest(string[] args, LaunchOptions options)
    {
        string[] gateArgs = args.Where(a => a.StartsWith(GateStem, StringComparison.Ordinal)).ToArray();
        string[] deadlineArgs = args.Where(a => a.StartsWith(DeadlineStem, StringComparison.Ordinal)).ToArray();
        string[] frequencyArgs = args.Where(a => a.StartsWith(FrequencyStem, StringComparison.Ordinal)).ToArray();
        bool hasAnyGateArg = gateArgs.Length != 0 || deadlineArgs.Length != 0 || frequencyArgs.Length != 0;

        if (!options.LiquidGlassCapturePreview)
        {
            if (hasAnyGateArg) throw new InvalidOperationException("QA startup-gate arguments require --system-compositor-preview.");
            return null;
        }

        if (!options.UiPreview || !options.ExplicitDataDirectory || options.DataDirectory is null ||
            !StringComparer.OrdinalIgnoreCase.Equals(options.IndependentGraphicsMachine, "SHENG-AWA") ||
            options.QaReport is not null || options.QaCycles != 0 || options.IncidentHostShortTest || options.QuitExisting)
            throw new InvalidOperationException("QA startup gate requires the isolated system-compositor preview launch contract.");
        if (args.Count(a => a == "--system-compositor-preview") != 1 ||
            args.Count(a => a == "--ui-preview") != 1 ||
            args.Count(a => a.StartsWith("--data-dir=", StringComparison.Ordinal)) != 1 ||
            args.Count(a => a.StartsWith("--independent-graphics-machine=", StringComparison.Ordinal)) != 1)
            throw new InvalidOperationException("QA startup-gate launch arguments are duplicate or incomplete.");
        if (args.Any(a => a is "--owned-image-glass" or "--system-compositor-glass" or "--arm-owned-image-glass" or
            "--arm-system-compositor-preview" or "--authorized-incident-host-short-test"))
            throw new InvalidOperationException("QA startup gate conflicts with another material/fuse mode.");
        if (gateArgs.Length != 1 || !gateArgs[0].StartsWith(GatePrefix, StringComparison.Ordinal) ||
            deadlineArgs.Length != 1 || !deadlineArgs[0].StartsWith(DeadlinePrefix, StringComparison.Ordinal) ||
            frequencyArgs.Length != 1 || !frequencyArgs[0].StartsWith(FrequencyPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Preview startup requires one nonce, root deadline and Stopwatch frequency.");

        string token = gateArgs[0][GatePrefix.Length..];
        if (!IsLowerHex32(token)) throw new InvalidOperationException("QA startup-gate nonce must be 32 lowercase hex characters.");
        if (!long.TryParse(deadlineArgs[0][DeadlinePrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out long rootDeadline) || rootDeadline <= 0)
            throw new InvalidOperationException("QA root work deadline must be a positive integer Stopwatch tick.");
        if (!long.TryParse(frequencyArgs[0][FrequencyPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out long frequency) ||
            frequency <= 0 || frequency != Stopwatch.Frequency)
            throw new InvalidOperationException("QA root Stopwatch frequency does not match this process.");
        return new Request(token, rootDeadline, frequency);
    }

    /// <summary>Pure validation: no file, process, clock, WinRT or graphics access.</summary>
    internal static Validation Validate(GateEnvelope? gate, Request request, SelfIdentity self,
        DateTimeOffset nowUtc, long nowStopwatchTicks, long localDeadlineStopwatchTicks)
    {
        long effectiveDeadline = Math.Min(request.RootWorkDeadlineStopwatchTicks, localDeadlineStopwatchTicks);
        if (nowStopwatchTicks >= effectiveDeadline) return Reject("DEADLINE_EXPIRED");
        if (gate is null) return Pending("GATE_FILE_NOT_LANDED");
        if (gate.SchemaVersion != 1) return Reject("SCHEMA_VERSION");
        if (!StringComparer.Ordinal.Equals(gate.RunToken, request.RunToken)) return Reject("RUN_TOKEN");
        if (gate.RootWorkDeadlineStopwatchTicks != request.RootWorkDeadlineStopwatchTicks ||
            gate.StopwatchFrequency != request.StopwatchFrequency || gate.StopwatchFrequency != Stopwatch.Frequency)
            return Reject("ROOT_CLOCK_CONTRACT");

        // Terminal is considered before RUNNING, heartbeat or previous live fields.
        string? watchState = gate.WatchdogState?.State;
        string? producerState = gate.ProducerState?.State;
        if (watchState is "FINISHED" or "FAILED" or "STOPPED" || producerState is "FAILED" or "STOPPED")
            return Reject("TERMINAL_STATE");
        if (watchState is not ("READY" or "RUNNING")) return Reject("WATCHDOG_STATE");

        if (!PinsMatch(gate.SourcePins) || !HostMatches(gate.HostIdentity, self)) return Reject("PIN_OR_HOST_IDENTITY");
        if (!DwmAndProducerMatch(gate.DwmIdentity, gate.ProducerState, self, nowUtc)) return Reject("DWM_OR_PRODUCER_IDENTITY");
        if (!WatchdogMonitorContractMatches(gate.WatchdogState, gate.SourcePins, gate.DwmIdentity, nowUtc))
            return Reject("WATCHDOG_GUARD_CONTRACT");

        if (watchState == "READY") return Pending("WATCHDOG_READY");
        if (!WatchdogHostMatches(gate.WatchdogState, gate.HostIdentity, nowUtc)) return Reject("WATCHDOG_HOST_IDENTITY");
        if (gate.WatchdogHeartbeat is null) return Pending("RUNNING_HEARTBEAT_NOT_PUBLISHED_YET");
        if (!HeartbeatMatches(gate.WatchdogHeartbeat, gate.HostIdentity, gate.ProducerState, gate.SourcePins, nowUtc))
            return Reject("HEARTBEAT_OR_FRESH_CIM_STATE");
        return new Validation(Disposition.Accepted, "EXACT_FRESH_RUNNING");
    }

    private static byte[]? ReadGateSnapshot(string path, string dataDirectory)
    {
        AssertNoReparseDirectoryChain(dataDirectory);
        if (!TryCheckPlainFile(path)) return null;
        var share = FileShare.Read | FileShare.Delete;
        long openStart = Stopwatch.GetTimestamp();
        long openDeadline = checked(openStart + Math.Max(1, Stopwatch.Frequency / 20));
        FileStream? stream = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (Stopwatch.GetTimestamp() >= openDeadline) throw new IOException("QA gate file open exceeded 50 ms.");
            try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, share); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException error) when (IsSharingViolation(error) && attempt < 2 && Stopwatch.GetTimestamp() < openDeadline)
            {
                Thread.Sleep(5);
                continue;
            }
            if (Stopwatch.GetTimestamp() >= openDeadline)
            {
                stream?.Dispose();
                throw new IOException("QA gate file open exceeded 50 ms.");
            }
            break;
        }
        if (stream is null) throw new IOException("QA gate file could not be opened.");
        using (stream)
        {
            long length = stream.Length;
            if (length < 0 || length > MaximumBytes || length > int.MaxValue)
                throw new InvalidDataException("QA gate JSON exceeded its 64 KiB bound.");
            byte[] bytes = new byte[(int)length];
            stream.ReadExactly(bytes);
            if (stream.Position != length || stream.Length != length)
                throw new IOException("QA gate file changed or was short-read.");
            AssertNoReparseDirectoryChain(dataDirectory);
            if (!TryCheckPlainFile(path)) throw new IOException("QA gate file disappeared while reading.");
            return bytes;
        }
    }

    private static GateEnvelope DeserializeStrict(byte[] bytes)
    {
        string json = new UTF8Encoding(false, true).GetString(bytes);
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        RejectDuplicateProperties(document.RootElement);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("QA gate envelope must be a JSON object.");
        return JsonSerializer.Deserialize<GateEnvelope>(document.RootElement.GetRawText(), StrictJson)
            ?? throw new JsonException("QA gate envelope was null.");
    }

    private static bool PinsMatch(SourcePins? pins) => pins is not null &&
        StringComparer.Ordinal.Equals(pins.Producer, ProducerSha256) &&
        StringComparer.Ordinal.Equals(pins.Core, CoreSha256) &&
        StringComparer.Ordinal.Equals(pins.Consumer, ConsumerSha256) &&
        StringComparer.Ordinal.Equals(pins.Watchdog, WatchdogSha256) &&
        StringComparer.Ordinal.Equals(pins.Binding, BindingSha256);

    private static bool HostMatches(HostIdentity? host, SelfIdentity self) => host is not null &&
        host.Pid == self.Pid && host.SessionId == self.SessionId && host.OwnerPid is > 0 &&
        StringComparer.OrdinalIgnoreCase.Equals(host.ProcessName, self.ProcessName) &&
        SameExecutablePath(host.ExecutablePath, self.ExecutablePath) &&
        TryUtcTicks(host.StartUtc, out long startUtcTicks) && startUtcTicks == self.StartTimeTicks &&
        long.TryParse(host.StartTimeTicks, NumberStyles.None, CultureInfo.InvariantCulture, out long startTicks) && startTicks == self.StartTimeTicks;

    private static bool SameExecutablePath(string? candidate, string expected)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate)) return false;
        try { return StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(candidate), expected); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        { return false; }
    }

    private static bool DwmAndProducerMatch(DwmIdentity? dwm, ProducerState? producer, SelfIdentity self, DateTimeOffset nowUtc)
    {
        if (dwm?.CreationDateUtc is null || !TryUtcTicks(dwm.CreationDateUtc, out long dwmCreatedTicks) ||
            dwmCreatedTicks > nowUtc.UtcDateTime.Ticks) return false;
        if (dwm is null || producer is null || dwm.Pid is null or <= 0 || dwm.SessionId != self.SessionId ||
            producer.SchemaVersion != 1 || producer.MonitorKind != "dwm-cim-process-cost-v1" ||
            producer.Source != "Win32_Process.CIM" || producer.RealMethod != "Get-CimInstance.Win32_Process" ||
            producer.SyntheticState != false || producer.State != "RUNNING" || producer.TargetStopped != false ||
            producer.LogicalProcessorCount != 16 || producer.StopwatchFrequency != Stopwatch.Frequency ||
            producer.SampleCount is null || producer.ValidIntervalCount is null || producer.SampleCount.Value < 2 ||
            producer.ValidIntervalCount.Value < 1 || producer.ValidIntervalCount.Value != producer.SampleCount.Value - 1 ||
            producer.ConsecutiveHighIntervals is null || producer.ConsecutiveHighIntervals.Value < 0 ||
            producer.ConsecutiveHighIntervals.Value >= 2 || !TryBoundedDecimal(producer.NormalizedLogicalCpuPercent, out decimal cpuPercent) ||
            cpuPercent is < 0m or > 100m || (cpuPercent > 10m) != (producer.ConsecutiveHighIntervals.Value >= 1) ||
            producer.ExpectedName != "dwm.exe" || producer.ObservedName != "dwm.exe" ||
            producer.ExpectedPid != dwm.Pid || producer.ObservedPid != dwm.Pid ||
            producer.ExpectedSessionId != dwm.SessionId || producer.ObservedSessionId != dwm.SessionId ||
            !SameUtc(producer.ExpectedCreationDateUtc, dwm.CreationDateUtc) ||
            !SameUtc(producer.ObservedCreationDateUtc, dwm.CreationDateUtc) ||
            !StringComparer.Ordinal.Equals(producer.ProducerSourceSha256, ProducerSha256) ||
            !StringComparer.Ordinal.Equals(producer.CoreSourceSha256, CoreSha256)) return false;
        return IsFresh(producer.AtUtc, nowUtc) && IsFresh(producer.LatestSampleUtc, nowUtc);
    }

    private static bool WatchdogMonitorContractMatches(WatchdogState? state, SourcePins? pins, DwmIdentity? dwm, DateTimeOffset nowUtc)
    {
        if (state is null || !TryUtcTicks(state.AtUtc, out long stateAt) || !TryUtcTicks(state.StartedUtc, out long startedAt) ||
            stateAt < startedAt || startedAt > nowUtc.UtcDateTime.Ticks || stateAt > nowUtc.UtcDateTime.Ticks) return false;
        if (pins is null || dwm?.Pid is null || state.ScreenRead != false ||
            state.DwmMonitorGuardEnabled != true || state.DwmMonitorState != "RUNNING" || state.DwmMonitorProcessVerified != true ||
            state.KillRequested != false || state.ExitConfirmed != false || state.Killed != false ||
            state.DwmMonitorPinnedSource is null || state.BaselineDwm is null || state.BaselineDwm.Length != 1 || state.BaselineDwm[0] != dwm.Pid)
            return false;
        return StringComparer.Ordinal.Equals(state.DwmMonitorPinnedSource.ProducerSourceSha256, pins.Producer) &&
            StringComparer.Ordinal.Equals(state.DwmMonitorPinnedSource.CoreSourceSha256, pins.Core) &&
            StringComparer.Ordinal.Equals(state.DwmMonitorPinnedSource.ConsumerSourceSha256, pins.Consumer);
    }

    private static bool WatchdogHostMatches(WatchdogState? state, HostIdentity? host, DateTimeOffset nowUtc) => state is not null && host is not null &&
        state.Pid == host.Pid && state.SessionId == host.SessionId && SameUtc(state.TargetStartUtc, host.StartUtc) &&
        TryUtcTicks(state.AtUtc, out long stateAt) && TryUtcTicks(state.StartedUtc, out long startedAt) &&
        TryUtcTicks(host.StartUtc, out long hostStart) && stateAt >= hostStart && stateAt >= startedAt &&
        startedAt <= nowUtc.UtcDateTime.Ticks && stateAt <= nowUtc.UtcDateTime.Ticks;

    private static bool IsFreshRelativeTo(string? text, string? referenceText) =>
        TryUtcTicks(text, out long stampTicks) && TryUtcTicks(referenceText, out long referenceTicks) &&
        stampTicks <= referenceTicks &&
        referenceTicks - stampTicks <= (long)(FreshSeconds * TimeSpan.TicksPerSecond);

    private static bool HeartbeatMatches(WatchdogHeartbeat? heartbeat, HostIdentity? host, ProducerState? producer, SourcePins? pins, DateTimeOffset nowUtc)
    {
        HeartbeatHostIdentity? id = heartbeat?.HostIdentity;
        if (heartbeat is null || id is null || host is null || producer is null || pins is null ||
            heartbeat.LatestValidatedCimState != "RUNNING" ||
            id.Pid != host.Pid || id.SessionId != host.SessionId ||
            !StringComparer.OrdinalIgnoreCase.Equals(id.ProcessName, host.ProcessName) || !SameUtc(id.StartUtc, host.StartUtc) ||
            !StringComparer.Ordinal.Equals(heartbeat.PinnedSource?.ProducerSourceSha256, pins.Producer) ||
            !StringComparer.Ordinal.Equals(heartbeat.PinnedSource?.CoreSourceSha256, pins.Core) ||
            !StringComparer.Ordinal.Equals(heartbeat.PinnedSource?.ConsumerSourceSha256, pins.Consumer) ||
            !IsFresh(heartbeat.AtUtc, nowUtc) ||
            !IsFreshRelativeTo(heartbeat.LatestValidatedCimAtUtc, heartbeat.AtUtc) ||
            !IsFreshRelativeTo(heartbeat.LatestValidatedCimSampleUtc, heartbeat.AtUtc) ||
            !TryUtcTicks(heartbeat.LatestValidatedCimSampleUtc, out long heartbeatSample) ||
            !TryUtcTicks(producer.LatestSampleUtc, out long producerSample)) return false;
        return heartbeatSample <= producerSample + (long)(CimReadToleranceSeconds * TimeSpan.TicksPerSecond);
    }

    private static SelfIdentity ReadSelfIdentity()
    {
        using Process process = Process.GetCurrentProcess();
        string path = Path.GetFullPath(process.MainModule?.FileName ?? Environment.ProcessPath ??
            throw new InvalidOperationException("Current executable path is unavailable."));
        long ticks = process.StartTime.ToUniversalTime().Ticks;
        return new SelfIdentity(process.Id, process.SessionId, process.ProcessName,
            new DateTime(ticks, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture), ticks, path);
    }

    private static bool IsFresh(string? text, DateTimeOffset nowUtc) =>
        TryUtcTicks(text, out long ticks) && ticks <= nowUtc.UtcDateTime.Ticks &&
        nowUtc.UtcDateTime.Ticks - ticks <= (long)(FreshSeconds * TimeSpan.TicksPerSecond);

    private static bool TryBoundedDecimal(string? text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out value);

    private static bool SameUtc(string? left, string? right) =>
        TryUtcTicks(left, out long leftTicks) && TryUtcTicks(right, out long rightTicks) && leftTicks == rightTicks;

    private static bool TryUtcTicks(string? text, out long ticks)
    {
        ticks = 0;
        if (string.IsNullOrWhiteSpace(text) || !HasExplicitOffset(text) ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed)) return false;
        ticks = parsed.UtcDateTime.Ticks;
        return true;
    }

    private static bool HasExplicitOffset(string text) => text.EndsWith('Z') ||
        (text.Length >= 6 && (text[^6] is '+' or '-') && text[^3] == ':');

    private static bool IsLowerHex32(string value) => value.Length == 32 && value.All(c =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSharingViolation(IOException error) => (error.HResult & 0xffff) is 32 or 33;

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON property: " + property.Name);
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    private static bool TryCheckPlainFile(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) != 0)
                throw new IOException("QA gate path is a reparse point or directory.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void AssertNoReparseDirectoryChain(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (!current.Exists) throw new DirectoryNotFoundException("QA data directory is missing: " + current.FullName);
            if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("QA data directory has a reparse ancestor: " + current.FullName);
            current = current.Parent;
        }
    }

    private static bool IsDirectChild(string parent, string child)
    {
        string parentFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        string childFull = Path.GetFullPath(child);
        return StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(childFull), parentFull) &&
            StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(childFull), GateFileName);
    }

    private static void SleepAtMost(long deadlineTicks, int capMilliseconds)
    {
        long remaining = deadlineTicks - Stopwatch.GetTimestamp();
        if (remaining <= 0) return;
        double ms = remaining * 1000.0 / Stopwatch.Frequency;
        Thread.Sleep((int)Math.Clamp(Math.Ceiling(ms), 1, capMilliseconds));
    }

    private static Validation Pending(string reason) => new(Disposition.Pending, reason);
    private static Validation Reject(string reason) => new(Disposition.Rejected, reason);
}
#endif
