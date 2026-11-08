using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlassDesk.Platform;

internal readonly record struct SystemMasterVolumeSnapshot(bool AudioAvailable, float? VolumeScalar, bool? Muted)
{
    internal static SystemMasterVolumeSnapshot Unavailable => new(false, null, null);
}

/// <summary>Controls the eRender/eMultimedia endpoint on a dedicated MTA COM thread.</summary>
internal sealed class SystemMasterVolume : IDisposable, IAsyncDisposable
{
    private const int ENotFound = unchecked((int)0x80070490);
    private const int AudclntDeviceInvalidated = unchecked((int)0x88890004);
    private const uint EDataFlowRender = 0;
    private const uint ERoleMultimedia = 1;
    private static readonly Guid EnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid EnumeratorInterfaceId = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid EndpointVolumeInterfaceId = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid OurEventContext = new("2A8C5E0A-3142-43C0-8EB1-8C043B75D0A1");

    private readonly BlockingCollection<Action> _work = new(new ConcurrentQueue<Action>());
    private readonly object _stateGate = new();
    private readonly object _pendingVolumeGate = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _worker;
    private readonly Timer _recoveryTimer;
    private readonly Diagnostics? _diagnostics;
    private readonly AudioEndpointVolumeCallback _volumeCallback;
    private readonly AudioDeviceNotificationCallback _deviceCallback;
    private readonly List<CommandCompletion> _pendingVolumeCompletions = new();

    private SystemMasterVolumeSnapshot _snapshot = SystemMasterVolumeSnapshot.Unavailable;
    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _device;
    private IAudioEndpointVolume? _endpointVolume;
    private bool _comInitialized;
    private bool _deviceCallbackRegistered;
    private bool _volumeCallbackRegistered;
    private float? _pendingVolumeScalar;
    private bool _volumeWorkQueued;
    private long _lastVolumeWriteTimestamp;
    private long _endpointGeneration;
    private int _rebindQueued;
    private int _retryRequired;
    private int _disposeStarted;
    private int _queueDisposed;

    internal SystemMasterVolume(Diagnostics? diagnostics)
    {
        _diagnostics = diagnostics;
        _volumeCallback = new AudioEndpointVolumeCallback(this);
        _deviceCallback = new AudioDeviceNotificationCallback(this);
        _recoveryTimer = new Timer(OnRecoveryTimer, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        _worker = new Thread(WorkerMain)
        {
            IsBackground = true,
            Name = "GlassDesk default audio endpoint"
        };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
        _ready.Task.GetAwaiter().GetResult();
    }

    internal event Action<SystemMasterVolumeSnapshot>? Changed;

    internal SystemMasterVolumeSnapshot Snapshot
    {
        get { lock (_stateGate) return _snapshot; }
    }

    internal Task SetMasterVolumeAsync(float scalar, CancellationToken cancellationToken)
    {
        if (!float.IsFinite(scalar))
            return Task.FromException(new ArgumentOutOfRangeException(nameof(scalar), "音量值必须是有限数值。"));
        if (Volatile.Read(ref _disposeStarted) != 0)
            return Task.FromException(new ObjectDisposedException(nameof(SystemMasterVolume)));

        var completion = new CommandCompletion(cancellationToken);
        if (completion.IsCompleted)
        {
            completion.Dispose();
            return completion.Task;
        }
        if (!Snapshot.AudioAvailable)
        {
            completion.Fail(new InvalidOperationException("没有可用的输出设备。"));
            return completion.Task;
        }

        float clamped = Math.Clamp(scalar, 0f, 1f);
        lock (_pendingVolumeGate)
        {
            if (Volatile.Read(ref _disposeStarted) != 0)
            {
                completion.Fail(new ObjectDisposedException(nameof(SystemMasterVolume)));
                return completion.Task;
            }

            _pendingVolumeScalar = clamped;
            _pendingVolumeCompletions.Add(completion);
            if (!_volumeWorkQueued)
            {
                _volumeWorkQueued = true;
                if (!TryQueueWork(ProcessPendingVolume))
                    FailPendingVolumeLocked(new ObjectDisposedException(nameof(SystemMasterVolume)));
            }
        }

        return completion.Task;
    }

    internal Task SetMuteAsync(bool muted, CancellationToken cancellationToken)
    {
        var completion = new CommandCompletion(cancellationToken);
        if (completion.IsCompleted)
        {
            completion.Dispose();
            return completion.Task;
        }
        if (Volatile.Read(ref _disposeStarted) != 0)
        {
            completion.Fail(new ObjectDisposedException(nameof(SystemMasterVolume)));
            return completion.Task;
        }
        if (!Snapshot.AudioAvailable)
        {
            completion.Fail(new InvalidOperationException("没有可用的输出设备。"));
            return completion.Task;
        }

        if (!TryQueueWork(() => ProcessMute(muted, completion)))
            completion.Fail(new ObjectDisposedException(nameof(SystemMasterVolume)));
        return completion.Task;
    }

    private void WorkerMain()
    {
        int initializeResult = SystemStatusNativeMethods.CoInitializeEx(0, SystemStatusNativeMethods.CoinitMultithreaded);
        _comInitialized = initializeResult >= 0;
        if (!_comInitialized)
        {
            RecordHResult("system.audio.com-initialize", initializeResult);
            PublishSnapshot(SystemMasterVolumeSnapshot.Unavailable);
        }
        else
        {
            try { InitializeAudioNotifications(); }
            catch (Exception error)
            {
                Interlocked.Exchange(ref _retryRequired, 1);
                RecordError("system.audio.initialize", error);
                PublishSnapshot(SystemMasterVolumeSnapshot.Unavailable);
            }
        }

        _ready.TrySetResult(true);
        try
        {
            foreach (Action work in _work.GetConsumingEnumerable())
            {
                try { work(); }
                catch (Exception error) { RecordError("system.audio.worker", error); }
            }
        }
        catch (Exception error)
        {
            RecordError("system.audio.queue", error);
        }
        finally
        {
            CleanupComObjects();
            if (_comInitialized) SystemStatusNativeMethods.CoUninitialize();
            _ready.TrySetResult(true);
        }
    }

    private void InitializeAudioNotifications()
    {
        CreateEnumeratorIfNeeded();
        if (_enumerator is null) return;
        RebindDefaultEndpoint();
    }

    private void CreateEnumeratorIfNeeded()
    {
        if (!_comInitialized) return;
        if (_enumerator is null)
        {
            Guid classId = EnumeratorClassId;
            Guid interfaceId = EnumeratorInterfaceId;
            int result = SystemStatusNativeMethods.CoCreateInstance(ref classId, 0,
                SystemStatusNativeMethods.ClsctxAll, ref interfaceId, out nint instance);
            if (result < 0) ThrowForHResult(result, "创建音频设备枚举器");

            try
            {
                _enumerator = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(instance);
            }
            finally
            {
                if (instance != 0) _ = Marshal.Release(instance);
            }
        }

        if (_enumerator is null) return;
        if (!_deviceCallbackRegistered)
        {
            int registerResult = _enumerator.RegisterEndpointNotificationCallback(_deviceCallback);
            if (registerResult >= 0)
                _deviceCallbackRegistered = true;
            else
            {
                Interlocked.Exchange(ref _retryRequired, 1);
                RecordHResult("system.audio.register-device-notification", registerResult);
            }
        }
        if (_deviceCallbackRegistered) Interlocked.Exchange(ref _retryRequired, 0);
    }

    private void RebindDefaultEndpoint()
    {
        ReleaseEndpoint();
        if (Volatile.Read(ref _disposeStarted) != 0 || !_comInitialized)
        {
            PublishSnapshot(SystemMasterVolumeSnapshot.Unavailable);
            return;
        }

        try
        {
            CreateEnumeratorIfNeeded();
            if (_enumerator is null)
            {
                Interlocked.Exchange(ref _retryRequired, 1);
                PublishSnapshot(SystemMasterVolumeSnapshot.Unavailable);
                return;
            }

            int result = _enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleMultimedia, out IMMDevice device);
            if (result == ENotFound || result == AudclntDeviceInvalidated)
            {
                Interlocked.Exchange(ref _retryRequired, _deviceCallbackRegistered ? 0 : 1);
                PublishSnapshot(SystemMasterVolumeSnapshot.Unavailable);
                return;
            }
            if (result < 0) ThrowForHResult(result, "获取默认输出设备");

            _device = device;
            Guid interfaceId = EndpointVolumeInterfaceId;
            int activateResult = device.Activate(ref interfaceId, SystemStatusNativeMethods.ClsctxAll, 0, out nint endpoint);
            if (activateResult < 0) ThrowForHResult(activateResult, "打开默认输出音量控制");

            try
            {
                IAudioEndpointVolume endpointVolume = (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(endpoint);
                _endpointVolume = endpointVolume;
                int callbackResult = endpointVolume.RegisterControlChangeNotify(_volumeCallback);
                if (callbackResult < 0) ThrowForHResult(callbackResult, "注册音量变化通知");
            }
            finally
            {
                if (endpoint != 0) _ = Marshal.Release(endpoint);
            }
            _volumeCallbackRegistered = true;
            ReadAndPublishEndpointState();
            Interlocked.Exchange(ref _retryRequired, _deviceCallbackRegistered ? 0 : 1);
        }
        catch (Exception error)
        {
            Interlocked.Exchange(ref _retryRequired, 1);
            RecordError("system.audio.endpoint-bind", error);
            ReleaseEndpoint();
            PublishSnapshot(SystemMasterVolumeSnapshot.Unavailable);
        }
    }

    private void ProcessPendingVolume()
    {
        CommandCompletion[] selectedCompletions = [];
        try
        {
            WaitForVolumeWriteSlot();

            float scalar;
            lock (_pendingVolumeGate)
            {
                if (_pendingVolumeScalar is not float pendingScalar)
                {
                    _volumeWorkQueued = false;
                    return;
                }
                scalar = pendingScalar;
                _pendingVolumeScalar = null;
                selectedCompletions = _pendingVolumeCompletions.ToArray();
                _pendingVolumeCompletions.Clear();
            }

            foreach (CommandCompletion completion in selectedCompletions)
                if (completion.IsCompleted) completion.Dispose();
            CommandCompletion[] activeCompletions = selectedCompletions.Where(static completion => !completion.IsCompleted).ToArray();
            if (activeCompletions.Length == 0) return;
            if (Volatile.Read(ref _disposeStarted) != 0)
                throw new ObjectDisposedException(nameof(SystemMasterVolume));
            if (_endpointVolume is null)
                throw new InvalidOperationException("没有可用的输出设备。");

            Guid eventContext = OurEventContext;
            int setResult = _endpointVolume.SetMasterVolumeLevelScalar(scalar, ref eventContext);
            if (setResult < 0) ThrowForHResult(setResult, "设置系统主音量");

            if (scalar > 0 && Snapshot.Muted == true)
            {
                eventContext = OurEventContext;
                int unmuteResult = _endpointVolume.SetMute(false, ref eventContext);
                if (unmuteResult < 0) ThrowForHResult(unmuteResult, "取消系统静音");
            }

            _lastVolumeWriteTimestamp = Stopwatch.GetTimestamp();
            ReadAndPublishEndpointState();
            foreach (CommandCompletion completion in activeCompletions) completion.Succeed();
        }
        catch (Exception error)
        {
            _lastVolumeWriteTimestamp = Stopwatch.GetTimestamp();
            HandleEndpointFailure("system.audio.set-volume", error);
            foreach (CommandCompletion completion in selectedCompletions.Where(static completion => !completion.IsCompleted))
                completion.Fail(ToCommandException(error));
        }
        finally
        {
            QueueNextPendingVolumeWrite();
        }
    }

    private void WaitForVolumeWriteSlot()
    {
        long minimumIntervalTicks = (long)Math.Ceiling(Stopwatch.Frequency / 30.0);
        long elapsed = Stopwatch.GetTimestamp() - _lastVolumeWriteTimestamp;
        long remaining = minimumIntervalTicks - elapsed;
        if (remaining <= 0) return;

        double milliseconds = Math.Ceiling(remaining * 1000.0 / Stopwatch.Frequency);
        Thread.Sleep(TimeSpan.FromMilliseconds(milliseconds));
    }

    private void QueueNextPendingVolumeWrite()
    {
        lock (_pendingVolumeGate)
        {
            if (_pendingVolumeScalar.HasValue && _pendingVolumeCompletions.Any(static completion => !completion.IsCompleted))
            {
                if (TryQueueWork(ProcessPendingVolume)) return;
                FailPendingVolumeLocked(new ObjectDisposedException(nameof(SystemMasterVolume)));
                return;
            }

            foreach (CommandCompletion completion in _pendingVolumeCompletions)
                completion.Dispose();
            _pendingVolumeCompletions.Clear();
            _pendingVolumeScalar = null;
            _volumeWorkQueued = false;
        }
    }

    private void ProcessMute(bool muted, CommandCompletion completion)
    {
        if (completion.IsCompleted)
        {
            completion.Dispose();
            return;
        }
        if (Volatile.Read(ref _disposeStarted) != 0)
        {
            completion.Fail(new ObjectDisposedException(nameof(SystemMasterVolume)));
            return;
        }

        try
        {
            if (_endpointVolume is null) throw new InvalidOperationException("没有可用的输出设备。");
            Guid eventContext = OurEventContext;
            int result = _endpointVolume.SetMute(muted, ref eventContext);
            if (result < 0) ThrowForHResult(result, "设置系统静音状态");
            ReadAndPublishEndpointState();
            completion.Succeed();
        }
        catch (Exception error)
        {
            HandleEndpointFailure("system.audio.set-mute", error);
            completion.Fail(ToCommandException(error));
        }
    }

    private void ReadAndPublishEndpointState()
    {
        if (_endpointVolume is null) throw new InvalidOperationException("没有可用的输出设备。");

        int volumeResult = _endpointVolume.GetMasterVolumeLevelScalar(out float scalar);
        if (volumeResult < 0) ThrowForHResult(volumeResult, "读取系统主音量");
        int muteResult = _endpointVolume.GetMute(out bool muted);
        if (muteResult < 0) ThrowForHResult(muteResult, "读取系统静音状态");
        if (!float.IsFinite(scalar)) throw new InvalidOperationException("系统返回了无效的音量值。");

        PublishSnapshot(new(true, Math.Clamp(scalar, 0f, 1f), muted));
    }

    private void HandleEndpointFailure(string component, Exception error)
    {
        RecordError(component, error);
        PublishSnapshot(SystemMasterVolumeSnapshot.Unavailable);
        if (Volatile.Read(ref _disposeStarted) == 0 && _comInitialized)
            RebindDefaultEndpoint();
    }

    private void ReleaseEndpoint()
    {
        long invalidatedGeneration = Interlocked.Increment(ref _endpointGeneration);
        _volumeCallback.SetGeneration(invalidatedGeneration);
        if (_endpointVolume is not null && _volumeCallbackRegistered)
        {
            int result = _endpointVolume.UnregisterControlChangeNotify(_volumeCallback);
            if (result < 0) RecordHResult("system.audio.unregister-volume-notification", result);
        }
        _volumeCallbackRegistered = false;
        ReleaseComObject(_endpointVolume, "system.audio.release-volume");
        _endpointVolume = null;
        ReleaseComObject(_device, "system.audio.release-device");
        _device = null;
    }

    private void CleanupComObjects()
    {
        _volumeCallback.Detach();
        _deviceCallback.Detach();
        ReleaseEndpoint();
        if (_enumerator is not null && _deviceCallbackRegistered)
        {
            int result = _enumerator.UnregisterEndpointNotificationCallback(_deviceCallback);
            if (result < 0) RecordHResult("system.audio.unregister-device-notification", result);
        }
        _deviceCallbackRegistered = false;
        ReleaseComObject(_enumerator, "system.audio.release-enumerator");
        _enumerator = null;
    }

    private void QueueObservedVolume(float scalar, bool muted, long generation)
    {
        if (!float.IsFinite(scalar) || Volatile.Read(ref _disposeStarted) != 0 ||
            generation != Volatile.Read(ref _endpointGeneration)) return;
        float clamped = Math.Clamp(scalar, 0f, 1f);
        _ = TryQueueWork(() =>
        {
            if (Volatile.Read(ref _disposeStarted) == 0 && generation == Volatile.Read(ref _endpointGeneration))
                PublishSnapshot(new(true, clamped, muted));
        });
    }

    private void QueueRebind()
    {
        if (Volatile.Read(ref _disposeStarted) != 0 || Interlocked.Exchange(ref _rebindQueued, 1) != 0) return;
        if (!TryQueueWork(() =>
        {
            Interlocked.Exchange(ref _rebindQueued, 0);
            if (Volatile.Read(ref _disposeStarted) == 0) RebindDefaultEndpoint();
        }))
            Interlocked.Exchange(ref _rebindQueued, 0);
    }

    private void OnRecoveryTimer(object? state)
    {
        if (Volatile.Read(ref _disposeStarted) == 0 && Volatile.Read(ref _retryRequired) != 0)
            QueueRebind();
    }

    private bool TryQueueWork(Action action)
    {
        if (Volatile.Read(ref _disposeStarted) != 0) return false;
        try { return _work.TryAdd(action); }
        catch (ObjectDisposedException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private void PublishSnapshot(SystemMasterVolumeSnapshot snapshot)
    {
        if (!snapshot.AudioAvailable || snapshot.VolumeScalar is not float scalar || !float.IsFinite(scalar) || snapshot.Muted is not bool)
            snapshot = SystemMasterVolumeSnapshot.Unavailable;
        else
            snapshot = snapshot with { VolumeScalar = Math.Clamp(scalar, 0f, 1f) };

        lock (_stateGate)
        {
            if (_snapshot == snapshot) return;
            _snapshot = snapshot;
        }

        Action<SystemMasterVolumeSnapshot>? handlers = Changed;
        if (handlers is null) return;
        foreach (Action<SystemMasterVolumeSnapshot> handler in handlers.GetInvocationList())
        {
            try { handler(snapshot); }
            catch (Exception error) { RecordError("system.audio.subscriber", error); }
        }
    }

    private void FailPendingVolumeLocked(Exception error)
    {
        foreach (CommandCompletion completion in _pendingVolumeCompletions) completion.Fail(error);
        _pendingVolumeCompletions.Clear();
        _pendingVolumeScalar = null;
        _volumeWorkQueued = false;
    }

    private static Exception ToCommandException(Exception error) =>
        error is ObjectDisposedException
            ? error
            : new InvalidOperationException("音量暂不可用。", error);

    private static void ThrowForHResult(int hresult, string operation)
    {
        if (hresult < 0) throw new COMException($"{operation}失败。", hresult);
    }

    private void RecordHResult(string component, int hresult)
    {
        if (_diagnostics is not null) _diagnostics.RecordEvent(component, $"HRESULT 0x{hresult:X8}");
        else Trace.TraceWarning($"{component} failed (HRESULT 0x{hresult:X8}).");
    }

    private void RecordError(string component, Exception error)
    {
        if (_diagnostics is not null) _diagnostics.RecordError(component, error);
        else Trace.TraceWarning($"{component} failed ({error.GetType().Name}, HRESULT 0x{error.HResult:X8}).");
    }

    private void ReleaseComObject(object? value, string component)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { _ = Marshal.FinalReleaseComObject(value); }
        catch (Exception error) { RecordError(component, error); }
    }

    private void DisposeStart()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _recoveryTimer.Dispose();
        lock (_pendingVolumeGate)
            FailPendingVolumeLocked(new ObjectDisposedException(nameof(SystemMasterVolume)));
        try { _work.CompleteAdding(); }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        DisposeStart();
        DisposeQueueAfterWorkerExit();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        DisposeStart();
        if (Environment.CurrentManagedThreadId != _worker.ManagedThreadId)
            await Task.Run(_worker.Join).ConfigureAwait(false);
        DisposeQueueAfterWorkerExit();
        GC.SuppressFinalize(this);
    }

    private void DisposeQueueAfterWorkerExit()
    {
        if (Environment.CurrentManagedThreadId == _worker.ManagedThreadId) return;
        _worker.Join();
        if (Interlocked.Exchange(ref _queueDisposed, 1) == 0) _work.Dispose();
    }

    private sealed class CommandCompletion : IDisposable
    {
        private readonly TaskCompletionSource<bool> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;

        internal CommandCompletion(CancellationToken cancellationToken)
        {
            _registration = cancellationToken.Register(static state =>
                ((TaskCompletionSource<bool>)state!).TrySetCanceled(), _source);
        }

        internal Task Task => _source.Task;
        internal bool IsCompleted => _source.Task.IsCompleted;
        internal void Succeed() { _source.TrySetResult(true); _registration.Dispose(); }
        internal void Fail(Exception error) { _source.TrySetException(error); _registration.Dispose(); }
        public void Dispose() => _registration.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioVolumeNotificationData
    {
        public Guid EventContext;
        public int Muted;
        public float MasterVolume;
        public uint ChannelCount;
    }

    [ComVisible(true)]
    [Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(nint notificationData);
    }

    [ComVisible(true)]
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged(nint deviceId, uint newState);
        [PreserveSig] int OnDeviceAdded(nint deviceId);
        [PreserveSig] int OnDeviceRemoved(nint deviceId);
        [PreserveSig] int OnDefaultDeviceChanged(uint flow, uint role, nint defaultDeviceId);
        [PreserveSig] int OnPropertyValueChanged(nint deviceId, ref PropertyKey key);
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(uint dataFlow, uint stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(uint dataFlow, uint role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback([MarshalAs(UnmanagedType.Interface)] IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback([MarshalAs(UnmanagedType.Interface)] IMMNotificationClient client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid interfaceId, uint classContext, nint activationParameters, out nint interfacePointer);
        [PreserveSig] int OpenPropertyStore(uint access, out nint properties);
        [PreserveSig] int GetId(out nint deviceId);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify([MarshalAs(UnmanagedType.Interface)] IAudioEndpointVolumeCallback notify);
        [PreserveSig] int UnregisterControlChangeNotify([MarshalAs(UnmanagedType.Interface)] IAudioEndpointVolumeCallback notify);
        [PreserveSig] int GetChannelCount(out uint channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid eventContext);
        [PreserveSig] int VolumeStepDown(ref Guid eventContext);
        [PreserveSig] int QueryHardwareSupport(out uint hardwareSupportMask);
        [PreserveSig] int GetVolumeRange(out float minimumDb, out float maximumDb, out float incrementDb);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class AudioEndpointVolumeCallback(SystemMasterVolume owner) : IAudioEndpointVolumeCallback
    {
        private SystemMasterVolume? _owner = owner;
        private long _generation;
        public int OnNotify(nint notificationData)
        {
            try
            {
                if (notificationData == 0) return 0;
                var data = Marshal.PtrToStructure<AudioVolumeNotificationData>(notificationData);
                Volatile.Read(ref _owner)?.QueueObservedVolume(data.MasterVolume, data.Muted != 0, Volatile.Read(ref _generation));
                return 0;
            }
            catch { return unchecked((int)0x80004005); }
        }

        internal void SetGeneration(long generation) => Volatile.Write(ref _generation, generation);
        internal void Detach() => Interlocked.Exchange(ref _owner, null);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class AudioDeviceNotificationCallback(SystemMasterVolume owner) : IMMNotificationClient
    {
        private SystemMasterVolume? _owner = owner;
        public int OnDeviceStateChanged(nint deviceId, uint newState) { Volatile.Read(ref _owner)?.QueueRebind(); return 0; }
        public int OnDeviceAdded(nint deviceId) { Volatile.Read(ref _owner)?.QueueRebind(); return 0; }
        public int OnDeviceRemoved(nint deviceId) { Volatile.Read(ref _owner)?.QueueRebind(); return 0; }
        public int OnDefaultDeviceChanged(uint flow, uint role, nint defaultDeviceId)
        {
            if (flow == EDataFlowRender && role == ERoleMultimedia)
                Volatile.Read(ref _owner)?.QueueRebind();
            return 0;
        }
        public int OnPropertyValueChanged(nint deviceId, ref PropertyKey key) => 0;
        internal void Detach() => Interlocked.Exchange(ref _owner, null);
    }
}
