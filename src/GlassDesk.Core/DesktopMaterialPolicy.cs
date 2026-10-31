using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlassDesk.Core;

/// <summary>The material selected for one desktop host lifetime.</summary>
public enum DesktopMaterialBackend
{
    Solid = 0,
    LegacySystemMaterial = 1,
    OwnedImageCandidate = 2,
    SystemCompositorCandidate = 3
}

public enum DesktopMaterialFuseResult
{
    NoAuthorization,
    Consumed,
    Armed,
    AlreadyArmed,
    CorruptState,
    Unavailable
}

/// <summary>
/// Persisted, one-start authorization for an experimental desktop-material backend.
/// The default (missing file) is disabled. The only path to an armed state is an
/// explicit call to <see cref="ArmNextStart"/>.
/// </summary>
public sealed class DesktopMaterialExperimentFuse
{
    private const int SchemaVersion = 1;
    private const int MaximumStateBytes = 4096;
    private const int MaximumFaultReasonCharacters = 512;
    private readonly string statePath;

    public DesktopMaterialExperimentFuse(string stateFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFilePath);
        statePath = Path.GetFullPath(stateFilePath);
    }

    /// <summary>Explicitly arms one subsequent startup. It never rearms an armed fuse.</summary>
    public DesktopMaterialFuseResult ArmNextStart() => WithLockedState((state, exists) =>
    {
        if (state is null && exists) return DesktopMaterialFuseResult.CorruptState;
        if (state?.Mode == FuseMode.Armed) return DesktopMaterialFuseResult.AlreadyArmed;
        if (state?.Mode == FuseMode.Running) return DesktopMaterialFuseResult.Unavailable;
        if (state?.Mode == FuseMode.Tripped) return DesktopMaterialFuseResult.Unavailable;

        long generation;
        try { generation = checked((state?.Generation ?? 0) + 1); }
        catch (OverflowException) { return DesktopMaterialFuseResult.Unavailable; }

        return TryWrite(new(generation, FuseMode.Armed, Guid.Empty, string.Empty))
            ? DesktopMaterialFuseResult.Armed
            : DesktopMaterialFuseResult.Unavailable;
    }, DesktopMaterialFuseResult.Unavailable, DesktopMaterialFuseResult.CorruptState);

    /// <summary>
    /// Atomically consumes an authorization and persists the running session before
    /// returning. Call on every startup before constructing any native host.
    /// </summary>
    public DesktopMaterialFuseResult ConsumeForStartup(Guid sessionId) => WithLockedState((state, exists) =>
    {
        if (sessionId == Guid.Empty) return DesktopMaterialFuseResult.Unavailable;
        if (!exists) return DesktopMaterialFuseResult.NoAuthorization;
        if (state is null) return DesktopMaterialFuseResult.CorruptState;
        if (state.Mode == FuseMode.Running)
        {
            // A later startup proves the previous process did not close the
            // experiment cleanly. Trip it before considering any new host.
            long interruptedGeneration;
            try { interruptedGeneration = checked(state.Generation + 1); }
            catch (OverflowException) { return DesktopMaterialFuseResult.Unavailable; }
            bool persistedTrip = TryWrite(new(interruptedGeneration, FuseMode.Tripped, state.SessionId,
                "Previous experiment session ended without a clean exit."));
            return persistedTrip ? DesktopMaterialFuseResult.NoAuthorization : DesktopMaterialFuseResult.Unavailable;
        }
        if (state.Mode != FuseMode.Armed) return DesktopMaterialFuseResult.NoAuthorization;

        long generation;
        try { generation = checked(state.Generation + 1); }
        catch (OverflowException) { return DesktopMaterialFuseResult.Unavailable; }

        return TryWrite(new(generation, FuseMode.Running, sessionId, string.Empty))
            ? DesktopMaterialFuseResult.Consumed
            : DesktopMaterialFuseResult.Unavailable;
    }, DesktopMaterialFuseResult.Unavailable, DesktopMaterialFuseResult.CorruptState);

    /// <summary>Persistently trips this session. A later clean-exit call cannot clear it.</summary>
    public bool MarkFault(Guid sessionId, string reason)
    {
        if (sessionId == Guid.Empty) return false;
        string normalizedReason = NormalizeFaultReason(reason);
        return WithLockedState((state, exists) =>
        {
            if (!exists || state is null) return false;
            if (state.SessionId != sessionId) return false;
            if (state.Mode == FuseMode.Tripped) return true;
            if (state.Mode is not (FuseMode.Running or FuseMode.Clean)) return false;
            long generation;
            try { generation = checked(state.Generation + 1); }
            catch (OverflowException) { return false; }
            return TryWrite(new(generation, FuseMode.Tripped, sessionId, normalizedReason));
        }, ioFailure: false, corruptState: false);
    }

    /// <summary>Marks a consumed experiment clean only while it is still Running.</summary>
    public bool TryCompleteCleanExit(Guid sessionId)
    {
        if (sessionId == Guid.Empty) return false;
        return WithLockedState((state, exists) =>
        {
            if (!exists || state is null || state.SessionId != sessionId) return false;
            if (state.Mode == FuseMode.Tripped) return false;
            if (state.Mode == FuseMode.Clean) return true;
            if (state.Mode != FuseMode.Running) return false;
            long generation;
            try { generation = checked(state.Generation + 1); }
            catch (OverflowException) { return false; }
            return TryWrite(new(generation, FuseMode.Clean, sessionId, string.Empty));
        }, ioFailure: false, corruptState: false);
    }

    private T WithLockedState<T>(Func<PersistedState?, bool, T> action, T ioFailure, T corruptState)
    {
        try
        {
            string? directory = Path.GetDirectoryName(statePath);
            if (string.IsNullOrEmpty(directory)) return ioFailure;
            Directory.CreateDirectory(directory);

            // FileShare.None coordinates separate app processes as well as callers in this process.
            using var gate = new FileStream(statePath + ".lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            bool exists = File.Exists(statePath);
            PersistedState? state = exists ? TryRead() : null;
            if (exists && state is null) return corruptState;
            return action(state, exists);
        }
        catch (JsonException)
        {
            return corruptState;
        }
        catch (Exception)
        {
            // Any lock, filesystem, or serialization error denies the experiment.
            return ioFailure;
        }
    }

    private PersistedState? TryRead()
    {
        using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > MaximumStateBytes) return null;
        byte[] bytes = new byte[checked((int)stream.Length)];
        int read = 0;
        while (read < bytes.Length)
        {
            int count = stream.Read(bytes, read, bytes.Length - read);
            if (count == 0) return null;
            read += count;
        }

        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 4
        });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 6) return null;
        if (!root.TryGetProperty("Version", out JsonElement version) || !version.TryGetInt32(out int parsedVersion)
            || parsedVersion != SchemaVersion) return null;
        if (!root.TryGetProperty("Generation", out JsonElement generation) || !generation.TryGetInt64(out long parsedGeneration)
            || parsedGeneration < 0) return null;
        if (!root.TryGetProperty("Mode", out JsonElement modeElement) || modeElement.ValueKind != JsonValueKind.String
            || !TryParseMode(modeElement.GetString(), out FuseMode mode)) return null;
        if (!root.TryGetProperty("SessionId", out JsonElement sessionElement) || sessionElement.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(sessionElement.GetString(), "N", out Guid sessionId)) return null;
        if (!root.TryGetProperty("FaultReason", out JsonElement reasonElement) || reasonElement.ValueKind != JsonValueKind.String)
            return null;
        string reason = reasonElement.GetString() ?? string.Empty;
        if (reason.Length > MaximumFaultReasonCharacters) return null;
        if (!IsValidTuple(mode, sessionId, reason)) return null;
        if (!root.TryGetProperty("Seal", out JsonElement sealElement) || sealElement.ValueKind != JsonValueKind.String)
            return null;

        string seal = sealElement.GetString() ?? string.Empty;
        byte[] actual;
        byte[] expected;
        try
        {
            actual = Convert.FromHexString(seal);
            expected = Convert.FromHexString(ComputeSeal(parsedGeneration, mode, sessionId, reason));
        }
        catch (FormatException) { return null; }
        return CryptographicOperations.FixedTimeEquals(actual, expected)
            ? new PersistedState(parsedGeneration, mode, sessionId, reason)
            : null;
    }

    private bool TryWrite(PersistedState state)
    {
        string? directory = Path.GetDirectoryName(statePath);
        if (string.IsNullOrEmpty(directory)) return false;

        string temporary = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var document = new StateDocument(SchemaVersion, state.Generation, ToString(state.Mode),
                state.SessionId.ToString("N"), state.FaultReason,
                ComputeSeal(state.Generation, state.Mode, state.SessionId, state.FaultReason));
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, statePath, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception) { }
            return false;
        }
    }

    private static bool IsValidTuple(FuseMode mode, Guid sessionId, string reason) => mode switch
    {
        FuseMode.Disarmed or FuseMode.Armed => sessionId == Guid.Empty && reason.Length == 0,
        FuseMode.Running or FuseMode.Clean => sessionId != Guid.Empty && reason.Length == 0,
        FuseMode.Tripped => sessionId != Guid.Empty && reason.Length > 0,
        _ => false
    };

    private static string ComputeSeal(long generation, FuseMode mode, Guid sessionId, string reason)
    {
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(new SealPayload(
            SchemaVersion, generation, ToString(mode), sessionId.ToString("N"), reason));
        return Convert.ToHexString(SHA256.HashData(canonical));
    }

    private static string NormalizeFaultReason(string? reason)
    {
        string trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length == 0) trimmed = "Desktop material experiment fault.";
        return trimmed.Length <= MaximumFaultReasonCharacters
            ? trimmed
            : trimmed[..MaximumFaultReasonCharacters];
    }

    private static string ToString(FuseMode mode) => mode switch
    {
        FuseMode.Disarmed => "disarmed",
        FuseMode.Armed => "armed",
        FuseMode.Running => "running",
        FuseMode.Clean => "clean",
        FuseMode.Tripped => "tripped",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static bool TryParseMode(string? value, out FuseMode mode)
    {
        mode = value switch
        {
            "disarmed" => FuseMode.Disarmed,
            "armed" => FuseMode.Armed,
            "running" => FuseMode.Running,
            "clean" => FuseMode.Clean,
            "tripped" => FuseMode.Tripped,
            _ => (FuseMode)(-1)
        };
        return Enum.IsDefined(mode);
    }

    private enum FuseMode { Disarmed, Armed, Running, Clean, Tripped }
    private sealed record PersistedState(long Generation, FuseMode Mode, Guid SessionId, string FaultReason);
    private sealed record StateDocument(int Version, long Generation, string Mode, string SessionId, string FaultReason, string Seal);
    private sealed record SealPayload(int Version, long Generation, string Mode, string SessionId, string FaultReason);
}

/// <summary>
/// Immutable backend selection produced before the native host is created. The
/// candidate can be selected only when requested, machine-gated, and fuse-authorized.
/// </summary>
public sealed class DesktopMaterialSession
{
    private readonly DesktopMaterialExperimentFuse? fuse;
    private int faultMarked;

    private DesktopMaterialSession(DesktopMaterialBackend backend, Guid sessionId,
        DesktopMaterialFuseResult fuseResult, string reason, DesktopMaterialExperimentFuse? fuse,
        bool systemCompositorPreview)
    {
        Backend = backend;
        SessionId = sessionId;
        FuseResult = fuseResult;
        Reason = reason;
        this.fuse = fuse;
        HostOptions = new DesktopMaterialHostCreationOptions(backend, sessionId,
            systemCompositorPreview && backend == DesktopMaterialBackend.SystemCompositorCandidate);
    }

    public DesktopMaterialBackend Backend { get; }
    public Guid SessionId { get; }
    public DesktopMaterialFuseResult FuseResult { get; }
    public string Reason { get; }
    public DesktopMaterialHostCreationOptions HostOptions { get; }
    public bool IsExperimentTripped => Volatile.Read(ref faultMarked) != 0;
    public bool IsExperimentalCandidate => IsExperimentalBackend(Backend);

    /// <summary>Persists a fault for this candidate session. Repeated calls are harmless.</summary>
    public bool MarkFault(string reason)
    {
        if (!IsExperimentalCandidate || FuseResult != DesktopMaterialFuseResult.Consumed
            || fuse is null) return false;
        Interlocked.Exchange(ref faultMarked, 1);
        return fuse.MarkFault(SessionId, reason);
    }

    /// <summary>Records a clean finish only if no fault has tripped this session.</summary>
    public bool TryCompleteCleanExit()
    {
        if (IsExperimentTripped || fuseResultNotConsumable() || fuse is null) return false;
        bool completed = fuse.TryCompleteCleanExit(SessionId);
        return completed && !IsExperimentTripped;
    }

    /// <summary>
    /// Resolves and consumes any one-start authorization. Call this before host
    /// construction. A non-candidate startup consumes and cleanly closes an armed token.
    /// </summary>
    public static DesktopMaterialSession CreateBeforeHostCreation(
        DesktopMaterialBackend requestedBackend,
        DesktopMaterialExperimentFuse? fuse,
        DesktopMaterialBackend fallbackBackend = DesktopMaterialBackend.Solid,
        bool allowCandidateOnThisMachine = false,
        bool systemCompositorPreview = false)
    {
        Guid sessionId = Guid.NewGuid();
        DesktopMaterialFuseResult fuseResult = fuse?.ConsumeForStartup(sessionId) ?? DesktopMaterialFuseResult.Unavailable;
        bool requestedValid = IsDefined(requestedBackend);
        bool fallbackValid = fallbackBackend is DesktopMaterialBackend.Solid or DesktopMaterialBackend.LegacySystemMaterial;
        DesktopMaterialBackend fallback = fallbackValid ? fallbackBackend : DesktopMaterialBackend.Solid;

        DesktopMaterialBackend selected;
        string reason;
        if (IsExperimentalBackend(requestedBackend) && requestedValid
            && allowCandidateOnThisMachine && fuseResult == DesktopMaterialFuseResult.Consumed)
        {
            selected = requestedBackend;
            reason = "The requested experimental desktop-material candidate consumed a one-start authorization on an allowed machine.";
        }
        else if (IsExperimentalBackend(requestedBackend) && !allowCandidateOnThisMachine)
        {
            selected = fallback;
            reason = "The candidate was denied by the machine authorization gate.";
        }
        else if (IsExperimentalBackend(requestedBackend))
        {
            selected = fallback;
            reason = "The candidate was denied because its one-start authorization was absent, corrupt, or unavailable.";
        }
        else if (requestedValid)
        {
            selected = requestedBackend;
            reason = "The requested non-experimental backend was selected.";
        }
        else
        {
            selected = fallback;
            reason = "The request was invalid; the configured safe fallback was selected.";
        }

        if (!IsExperimentalBackend(selected) && fuseResult == DesktopMaterialFuseResult.Consumed)
            _ = fuse!.TryCompleteCleanExit(sessionId);

        return new DesktopMaterialSession(selected, sessionId, fuseResult, reason, fuse, systemCompositorPreview);
    }

    private bool fuseResultNotConsumable() => FuseResult != DesktopMaterialFuseResult.Consumed;
    private static bool IsExperimentalBackend(DesktopMaterialBackend backend) =>
        backend is DesktopMaterialBackend.OwnedImageCandidate or DesktopMaterialBackend.SystemCompositorCandidate;
    private static bool IsDefined(DesktopMaterialBackend backend) => Enum.IsDefined(backend);
}

/// <summary>Read-only host creation data. No mutator can change the backend later.</summary>
public sealed class DesktopMaterialHostCreationOptions
{
    internal DesktopMaterialHostCreationOptions(DesktopMaterialBackend backend, Guid sessionId,
        bool systemCompositorPreview)
    {
        Backend = backend;
        SessionId = sessionId;
        SystemCompositorPreview = systemCompositorPreview;
    }

    public int AbiVersion => SystemCompositorPreview ? 2 : 1;
    public DesktopMaterialBackend Backend { get; }
    public Guid SessionId { get; }
    public bool SystemCompositorPreview { get; }
}
