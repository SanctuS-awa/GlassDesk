using System.Reflection;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using GlassDesk.Core;

namespace GlassDesk.Plugins;

/// <summary>Owns trusted extension lifetimes and removes contributions on faults or disable.</summary>
public sealed class PluginHost : IAsyncDisposable
{
    sealed class Entry(PluginManifest manifest,string? path,Func<IGlassDeskPlugin>? factory)
    {
        public PluginManifest Manifest=manifest;
        public string? Path=path;
        public Func<IGlassDeskPlugin>? Factory=factory;
        public IGlassDeskPlugin? Plugin;
        public PluginLoadContext? LoadContext;
        public Context? Context;
        public CancellationTokenSource? Cancellation;
        public IReadOnlyList<IPluginContribution> Contributions=[];
        public bool Enabled;
        public PluginRunState State=PluginRunState.Disabled;
        public string? Reason;
        public string Resources=manifest.ResourceDescription;
        public WeakReference? PendingUnload;
        public bool UnsafeUnload;
        public Task? InFlight;
    }
    sealed class Context(PluginHost host,Entry entry) : IPluginContext
    {
        int epoch,active,revoked;
        public bool Active {get=>Volatile.Read(ref active)!=0;set{Volatile.Write(ref active,value?1:0);Interlocked.Increment(ref epoch);}}
        public bool Revoked=>Volatile.Read(ref revoked)!=0;
        public string PluginId=>entry.Manifest.Id;
        public PluginSceneState Scene=>host.scene;
        public event Action<PluginSceneState>? SceneChanged;
        public JsonElement? ReadConfiguration()=>host.GetConfiguration(PluginId);
        public ValueTask SaveConfigurationAsync(JsonElement value,CancellationToken cancellationToken=default)
        {ObjectDisposedException.ThrowIf(Revoked,this);return host.SaveConfigurationAsync(PluginId,value,cancellationToken);}
        void Post(Action action)
        {
            int currentEpoch=Volatile.Read(ref epoch);
            if(Revoked||!Active)return;
            host.dispatcher.TryEnqueue(()=>{if(!Revoked&&Active&&Volatile.Read(ref epoch)==currentEpoch&&!host.disposed)action();});
        }
        public void RequestRender()=>Post(()=>host.Changed?.Invoke());
        public void SubmitVisual(string id,IReadOnlyList<PluginDrawOp> ops)
        {
            if(!entry.Contributions.OfType<PluginContentPage>().Any(p=>p.Id==id))throw new InvalidOperationException("视觉请求必须对应已注册内容页。");
            if(ops.Count>128||ops.Any(o=>o.Text?.Length>2048||!float.IsFinite(o.X)||!float.IsFinite(o.Y)||!float.IsFinite(o.Width)||!float.IsFinite(o.Height)))throw new ArgumentOutOfRangeException(nameof(ops));
            var copy=ops.ToArray();Post(()=>{host.visuals[PluginId+"/"+id]=copy;host.VisualsChanged?.Invoke(host.visuals);});
        }
        public void SubmitCompanion(CompanionVisualState? state)
        {if(!entry.Contributions.OfType<PluginCompanion>().Any())throw new InvalidOperationException("角色请求需要已注册角色贡献。");Post(()=>{host.companions[PluginId]=state;host.PublishCompanion();});}
        public void SetMaterial(string id,bool active)
        {if(!entry.Contributions.OfType<PluginMaterial>().Any(m=>m.Id==id))throw new InvalidOperationException("材质请求需要对应已注册材质贡献。");Post(()=>{host.materials[PluginId+"/"+id]=active;host.MaterialChanged?.Invoke(id,host.materials.Any(p=>p.Key.EndsWith("/"+id,StringComparison.Ordinal)&&p.Value));});}
        public void ReportResourceUsage(string value)=>Post(()=>{entry.Resources=value.Length>2048?value[..2048]:value;host.Changed?.Invoke();});
        public void Publish(PluginSceneState value)=>SceneChanged?.Invoke(value);
        public void Revoke(){Volatile.Write(ref revoked,1);Active=false;SceneChanged=null;}
    }
    readonly IUiDispatcher dispatcher;
    readonly SemaphoreSlim gate=new(1,1);
    readonly ConcurrentDictionary<string,Entry> entries=new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string,JsonElement> configuration=new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string,SemaphoreSlim> configGates=new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string,CompanionVisualState?> companions=new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string,bool> materials=new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string,IReadOnlyList<PluginDrawOp>> visuals=new(StringComparer.OrdinalIgnoreCase);
    readonly string root,configRoot;
    volatile PluginSceneState scene=new(WidgetForm.Capsule,true,MediaSnapshot.Disconnected,false,0);
    bool disposed;
    WidgetPreferences builtinPreferences=new();
    WidgetPreferences? builtinPreview;
    CompanionVisualState LatestBuiltinAppearance(CompanionVisualState state)
    {
        var current=Volatile.Read(ref builtinPreview)??Volatile.Read(ref builtinPreferences);
        return state with {Color=current.CompanionColor,AppearanceId=current.CompanionAppearanceId};
    }
    void PublishCompanion()
    {
        var current=companions.LastOrDefault(pair=>pair.Value is not null);
        if(current.Value is { } state)
            CompanionChanged?.Invoke(current.Key=="glassdesk.companion"?LatestBuiltinAppearance(state):state);
        else if(entries.TryGetValue("glassdesk.companion",out var builtin)&&builtin.State==PluginRunState.Running)
            CompanionChanged?.Invoke(LatestBuiltinAppearance(new(CompanionExpression.Calm,builtinPreferences.CompanionColor)));
        else CompanionChanged?.Invoke(null);
    }
    public void PreviewBuiltinAppearance(uint color,string appearanceId)
    {
        Volatile.Write(ref builtinPreview,new WidgetPreferences {CompanionColor=color|0xff000000,CompanionAppearanceId=CompanionAppearanceIds.Normalize(appearanceId)});
        dispatcher.TryEnqueue(()=>{if(!disposed)PublishCompanion();});
    }
    public bool IsBuiltinCompanionActive=>companions.LastOrDefault(pair=>pair.Value is not null).Key=="glassdesk.companion";
    public event Action? Changed;
    public event Action<CompanionVisualState?>? CompanionChanged;
    public event Action<string,bool>? MaterialChanged;
    public event Action<IReadOnlyDictionary<string,IReadOnlyList<PluginDrawOp>>>? VisualsChanged;
    public IReadOnlyList<PluginStatus> Statuses=>entries.Values.Select(e=>new PluginStatus(e.Manifest.Id,e.Manifest.Name,e.Manifest.Version,e.Enabled,e.State,e.Resources,e.Reason,e.Factory is not null)).ToArray();
    public IReadOnlyList<PluginContentPage> Pages=>entries.Values.Where(e=>e.State is PluginRunState.Running or PluginRunState.Paused)
        .SelectMany(e=>e.Contributions.OfType<PluginContentPage>().Select(p=>p with {Id=e.Manifest.Id+"/"+p.Id,Items=p.Items.Select(i=>i.CommandId is null?i:i with {CommandId=e.Manifest.Id+"/"+i.CommandId}).ToArray()})).ToArray();
    public IReadOnlyList<PluginSetting> Settings(string id)=>entries.TryGetValue(id,out var e)?e.Contributions.OfType<PluginSetting>().ToArray():[];
    public PluginHost(IUiDispatcher dispatcher,string dataDirectory)
    {
        this.dispatcher=dispatcher;root=Path.Combine(dataDirectory,"plugins");configRoot=Path.Combine(dataDirectory,"plugin-config");
        AddBuiltIn("glassdesk.companion","陪伴球",true,()=>new CompanionPlugin(),"回应展开、指针与音乐变化；不规则短眨眼和注视，隐藏时停止动作。");
        AddBuiltIn("glassdesk.liquidglass","液态玻璃",false,()=>new LiquidGlassPlugin(),"启用时通过 GPU 桌面捕获显示液态玻璃；关闭时释放捕获资源并恢复系统磨砂。","0.3");
        if(Directory.Exists(root))foreach(string directory in Directory.EnumerateDirectories(root).Where(d=>!Path.GetFileName(d).StartsWith('.')))
        {
            try
            {
                var manifest=PluginPackage.ReadManifest(directory);
                if(!string.Equals(Path.GetFileName(directory),manifest.Id,StringComparison.OrdinalIgnoreCase)||entries.ContainsKey(manifest.Id))continue;
                entries.TryAdd(manifest.Id,new(manifest,directory,null));
            }
            catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { }
        }
    }
    void AddBuiltIn(string id,string name,bool enabled,Func<IGlassDeskPlugin> factory,string resource,string version="0.2.0")
    {entries.TryAdd(id,new(new(id,name,version,PluginApi.Version,"","",resource),null,factory){Enabled=enabled});}
    public JsonElement? GetConfiguration(string id)
    {
        if(configuration.TryGetValue(id,out var value))return value.Clone();
        if(!entries.ContainsKey(id))return null;
        var path=Path.Combine(configRoot,id+".json");
        try
        {
            if(!File.Exists(path)||new FileInfo(path).Length>65536)return null;
            using var doc=JsonDocument.Parse(File.ReadAllText(path));configuration[id]=doc.RootElement.Clone();return configuration[id].Clone();
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException){return null;}
    }
    public async ValueTask SaveConfigurationAsync(string id,JsonElement value,CancellationToken cancellationToken=default)
    {
        if(!entries.ContainsKey(id))throw new KeyNotFoundException("插件不存在。");
        string text=value.GetRawText();if(Encoding.UTF8.GetByteCount(text)>65536)throw new InvalidDataException("插件配置过大。");
        var writeGate=configGates.GetOrAdd(id,_=>new(1,1));await writeGate.WaitAsync(cancellationToken);
        try
        {
            await WriteConfigurationLockedAsync(id,value,text,cancellationToken);
        }
        finally{writeGate.Release();}
    }
    async ValueTask WriteConfigurationLockedAsync(string id,JsonElement value,string text,CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(configRoot);string path=Path.Combine(configRoot,id+".json"),temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{await File.WriteAllTextAsync(temporary,text,cancellationToken);File.Move(temporary,path,true);configuration[id]=value.Clone();}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    async ValueTask MergeBuiltinAppearanceAsync(WidgetPreferences preferences)
    {
        const string id="glassdesk.companion";
        var writeGate=configGates.GetOrAdd(id,_=>new(1,1));await writeGate.WaitAsync();
        try
        {
            var before=GetConfiguration(id);
            var value=before is {ValueKind:JsonValueKind.Object}?JsonNode.Parse(before.Value.GetRawText())!.AsObject():new JsonObject();
            value["color"]=preferences.CompanionColor;value["appearanceId"]=preferences.CompanionAppearanceId;
            using var document=JsonDocument.Parse(value.ToJsonString());
            string text=document.RootElement.GetRawText();
            if(before?.GetRawText()!=text)await WriteConfigurationLockedAsync(id,document.RootElement,text,CancellationToken.None);
        }
        finally{writeGate.Release();}
    }
    public async Task ApplyPreferencesAsync(WidgetPreferences preferences)
    {
        await gate.WaitAsync();
        try
        {
            Volatile.Write(ref builtinPreferences,preferences);
            var pending=Volatile.Read(ref builtinPreview);
            if(pending?.CompanionColor==preferences.CompanionColor&&pending.CompanionAppearanceId==preferences.CompanionAppearanceId)
                Interlocked.CompareExchange(ref builtinPreview,null,pending);
            await MergeBuiltinAppearanceAsync(preferences);
            foreach(var entry in entries.Values)
            {
                bool enabled=entry.Manifest.Id switch{"glassdesk.companion"=>preferences.CompanionEnabled,"glassdesk.liquidglass"=>preferences.LiquidGlassEnabled,_=>preferences.PluginEnabled.GetValueOrDefault(entry.Manifest.Id,false)};
                entry.Enabled=enabled;
                if(!enabled){await StopAsync(entry);continue;}
                if(scene.Visible)await StartAsync(entry);else if(entry.State==PluginRunState.Running)await PauseAsync(entry);
            }
            if(preferences.CompanionEnabled&&scene.Visible&&entries["glassdesk.companion"].State==PluginRunState.Running)
                dispatcher.TryEnqueue(()=>{if(!disposed)PublishCompanion();});
        }
        finally{gate.Release();Changed?.Invoke();}
    }
    public async Task UpdateSceneAsync(PluginSceneState value)
    {
        await gate.WaitAsync();
        try
        {
            scene=value;
            foreach(var entry in entries.Values.ToArray())
            {
                if(!entry.Enabled)continue;
                try
                {
                    if(!value.Visible&&entry.State==PluginRunState.Running)await PauseAsync(entry);
                    else if(value.Visible&&entry.State is PluginRunState.Paused or PluginRunState.Disabled or PluginRunState.Stopped)await StartAsync(entry);
                    if(entry.Context is not null)
                        await InvokePluginAsync(entry,()=>{entry.Context?.Publish(value);return Task.CompletedTask;},TimeSpan.FromSeconds(5));
                }
                catch(Exception error){await FaultAsync(entry,error);}
            }
        }
        finally{gate.Release();Changed?.Invoke();}
    }
    async Task StartAsync(Entry entry)
    {
        if(entry.UnsafeUnload||entry.State is PluginRunState.Running or PluginRunState.Faulted or PluginRunState.RestartRequired)return;
        try
        {
            entry.State=PluginRunState.Starting;entry.Reason=null;
            if(entry.Plugin is null)
            {
                entry.Context=new(this,entry);
                await InvokePluginAsync(entry,()=>
                {
                    if(entry.Factory is not null)entry.Plugin=entry.Factory();
                    else
                    {
                        string path=PluginPackage.SafePath(entry.Path!,entry.Manifest.EntryAssembly);
                        entry.LoadContext=new(path);
                        var assembly=entry.LoadContext.LoadFromAssemblyPath(path);
                        var type=assembly.GetType(entry.Manifest.EntryType,true)!;
                        if(!typeof(IGlassDeskPlugin).IsAssignableFrom(type)||type.IsAbstract)throw new InvalidDataException("插件入口没有实现 IGlassDeskPlugin。");
                        entry.Plugin=(IGlassDeskPlugin)Activator.CreateInstance(type)!;
                    }
                    entry.Plugin.Initialize(entry.Context!);
                    entry.Contributions=entry.Plugin.RegisterContributions().Take(129).ToArray();
                    if(entry.Contributions.Count>128||entry.Contributions.Any(p=>p.Id.Length is <1 or >80)||entry.Contributions.Select(p=>p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=entry.Contributions.Count)
                        throw new InvalidDataException("插件贡献标识无效或重复。");
                    return Task.CompletedTask;
                },TimeSpan.FromSeconds(5));
            }
            entry.Cancellation=new();entry.Context!.Active=true;
            await InvokePluginAsync(entry,()=>entry.Plugin!.StartAsync(entry.Cancellation.Token).AsTask(),TimeSpan.FromSeconds(5));
            entry.State=PluginRunState.Running;
        }
        catch(Exception error){await FaultAsync(entry,error);}
    }
    static async Task InvokePluginAsync(Entry entry,Func<Task> invocation,TimeSpan timeout)
    {
        // Third-party synchronous prefixes and constructors cannot block the UI.
        // In-process code is still trusted: a timed-out task is quarantined, never forcibly unloaded.
        var task=entry.Factory is null?Task.Run(invocation):invocation();
        entry.InFlight=task;
        _=task.ContinueWith(completed=>{_=completed.Exception;},CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        try{await task.WaitAsync(timeout);}
        catch(TimeoutException){entry.UnsafeUnload=true;entry.Cancellation?.Cancel();throw;}
        finally{if(task.IsCompleted)entry.InFlight=null;}
    }
    void Quarantine(Entry entry)
    {
        entry.UnsafeUnload=true;entry.Context?.Revoke();entry.Cancellation?.Cancel();
        Clear(entry);entry.State=PluginRunState.RestartRequired;
        entry.Reason="插件任务仍在运行，已撤销其贡献；请重启后再更新或移除。";
        // Keep the context/plugin/ALC pinned while its code may still be executing.
    }
    void Clear(Entry entry)
    {
        string prefix=entry.Manifest.Id+"/";
        foreach(string id in visuals.Keys.Where(k=>k.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).ToArray())visuals.Remove(id);
        companions.Remove(entry.Manifest.Id);
        foreach(string id in materials.Keys.Where(k=>k.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).ToArray())
        {materials.Remove(id);string material=id[prefix.Length..];MaterialChanged?.Invoke(material,materials.Any(m=>m.Key.EndsWith("/"+material,StringComparison.Ordinal)&&m.Value));}
        PublishCompanion();VisualsChanged?.Invoke(visuals);
    }
    async Task PauseAsync(Entry entry)
    {
        if(entry.Context is not null)entry.Context.Active=false;
        entry.Cancellation?.Cancel();
        try{if(entry.Plugin is not null)await InvokePluginAsync(entry,()=>entry.Plugin.PauseAsync(CancellationToken.None).AsTask(),TimeSpan.FromSeconds(5));}
        catch(Exception error){await FaultAsync(entry,error);return;}
        entry.Cancellation?.Dispose();entry.Cancellation=null;
        Clear(entry);entry.State=PluginRunState.Paused;
    }
    async Task StopAsync(Entry entry)
    {
        if(entry.InFlight is {IsCompleted:false}){Quarantine(entry);return;}
        if(entry.Plugin is null)
        {
            entry.Context?.Revoke();entry.Context=null;
            entry.UnsafeUnload|=entry.LoadContext?.NativeLoaded==true;
            entry.PendingUnload=DetachLoadContext(entry);Clear(entry);
            entry.State=entry.UnsafeUnload?PluginRunState.RestartRequired:PluginRunState.Disabled;return;
        }
        entry.Context!.Active=false;entry.Cancellation?.Cancel();
        bool failed=false;
        try{await InvokePluginAsync(entry,()=>entry.Plugin.StopAsync(CancellationToken.None).AsTask(),TimeSpan.FromSeconds(5));}
        catch(Exception error){entry.Reason="停止失败："+error.GetType().Name;failed=true;}
        if(entry.InFlight is {IsCompleted:false}){Quarantine(entry);return;}
        try{await InvokePluginAsync(entry,()=>entry.Plugin.DisposeAsync().AsTask(),TimeSpan.FromSeconds(5));}
        catch(Exception error){entry.Reason="释放失败："+error.GetType().Name;failed=true;}
        if(entry.InFlight is {IsCompleted:false}){Quarantine(entry);return;}
        entry.Context.Revoke();entry.Context=null;entry.Contributions=[];entry.Plugin=null;
        entry.Cancellation?.Dispose();entry.Cancellation=null;Clear(entry);
        bool native=entry.LoadContext?.NativeLoaded==true||entry.Manifest.RequiresRestart;
        entry.UnsafeUnload|=failed||native||entry.InFlight is {IsCompleted:false};
        entry.PendingUnload=DetachLoadContext(entry);
        entry.State=entry.UnsafeUnload?PluginRunState.RestartRequired:PluginRunState.Disabled;
        if(native)entry.Reason="包含原生组件，更新或移除需重启。";
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference? DetachLoadContext(Entry entry)
    {
        var loadContext=entry.LoadContext;entry.LoadContext=null;
        if(loadContext is null)return null;
        var weak=new WeakReference(loadContext);loadContext.Unload();return weak;
    }
    static async Task RequireUnloadedAsync(Entry entry)
    {
        await Task.Yield();
        for(int attempt=0;attempt<3&&entry.PendingUnload?.IsAlive==true;attempt++)
        {GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();if(entry.PendingUnload.IsAlive)await Task.Delay(25);}
        if(entry.UnsafeUnload||entry.PendingUnload?.IsAlive==true)
        {entry.State=PluginRunState.RestartRequired;entry.Reason="插件资源尚未可靠卸载，更新或移除需重启。";throw new InvalidOperationException(entry.Reason);}
    }
    async Task FaultAsync(Entry entry,Exception error)
    {await StopAsync(entry);entry.State=entry.UnsafeUnload?PluginRunState.RestartRequired:PluginRunState.Faulted;entry.Reason=entry.UnsafeUnload?"插件任务未可靠停止，需重启："+error.GetType().Name:"插件已停止："+error.GetType().Name;}
    public async Task ExecuteAsync(string id,string commandId)
    {
        await gate.WaitAsync();
        try
        {
            if(!entries.TryGetValue(id,out var entry)||entry.State!=PluginRunState.Running)throw new InvalidOperationException("插件尚未运行。");
            var command=entry.Contributions.OfType<PluginCommand>().SingleOrDefault(c=>c.Id==commandId)??throw new KeyNotFoundException("插件操作不存在。");
            try
            {
                await InvokePluginAsync(entry,()=>command.Execute(entry.Cancellation!.Token).AsTask(),TimeSpan.FromSeconds(10));
            }
            catch(Exception error){await FaultAsync(entry,error);throw new InvalidOperationException(entry.Reason,error);}
        }
        finally{gate.Release();Changed?.Invoke();}
    }
    public async Task<string> ImportAsync(string packagePath)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        await gate.WaitAsync();string? staging=null,backup=null,target=null;bool moved=false;
        Entry? previous=null,installed=null;
        try
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            staging=Path.Combine(root,".install-"+Guid.NewGuid().ToString("N"));
            string stage=staging,package=Path.GetFullPath(packagePath);
            var manifest=await Task.Run(()=>{Directory.CreateDirectory(root);return PluginPackage.Extract(package,stage);});
            ObjectDisposedException.ThrowIf(disposed,this);
            if(entries.TryGetValue(manifest.Id,out previous))
            {
                await StopAsync(previous);
                await RequireUnloadedAsync(previous);
            }
            target=Path.Combine(root,manifest.Id);
            if(Directory.Exists(target))
            {
                backup=Path.Combine(root,".backup-"+Guid.NewGuid().ToString("N"));
                string old=target,saved=backup;await Task.Run(()=>Directory.Move(old,saved));
            }
            string destination=target;await Task.Run(()=>Directory.Move(stage,destination));staging=null;moved=true;
            installed=new Entry(manifest,target,null){Enabled=previous?.Enabled??false};entries[manifest.Id]=installed;
            if(installed.Enabled&&scene.Visible)
            {
                await StartAsync(installed);
                if(installed.State!=PluginRunState.Running)throw new InvalidOperationException(installed.Reason??"新版本插件未能启动。");
            }
            if(backup is not null)
            {
                try{string obsolete=backup;await Task.Run(()=>Directory.Delete(obsolete,true));backup=null;}
                catch(Exception error)when(error is IOException or UnauthorizedAccessException)
                {installed.Reason="新版本已启用，旧包备份暂未清理。";}
            }
            return manifest.Id;
        }
        catch(Exception original)
        {
            if(backup is not null&&target is not null)
            {
                try
                {
                    string? rejected=null;
                    if(moved&&installed is not null)
                    {
                        await StopAsync(installed);await RequireUnloadedAsync(installed);
                        rejected=Path.Combine(root,".failed-"+Guid.NewGuid().ToString("N"));
                        string current=target,failed=rejected;await Task.Run(()=>Directory.Move(current,failed));
                    }
                    if(!Directory.Exists(target))
                    {
                        string saved=backup,destination=target;await Task.Run(()=>Directory.Move(saved,destination));backup=null;
                    }
                    if(previous is not null)
                    {
                        entries[previous.Manifest.Id]=previous;
                        if(previous.Enabled&&scene.Visible&&!disposed)await StartAsync(previous);
                    }
                    if(rejected is not null)
                    {
                        try{string failed=rejected;await Task.Run(()=>Directory.Delete(failed,true));}
                        catch(Exception error)when(error is IOException or UnauthorizedAccessException){ }
                    }
                }
                catch(Exception recovery)
                {throw new InvalidOperationException($"插件更新失败，旧包已保留在 {backup??target}；请退出后恢复。",new AggregateException(original,recovery));}
                throw new InvalidOperationException("插件更新失败，已恢复之前的版本，原配置保留。",original);
            }
            throw;
        }
        finally
        {
            try
            {
                if(staging is not null&&Directory.Exists(staging))
                {
                    try{string abandoned=staging;await Task.Run(()=>Directory.Delete(abandoned,true));}
                    catch(Exception error)when(error is IOException or UnauthorizedAccessException){ }
                }
            }
            finally{gate.Release();Changed?.Invoke();}
        }
    }
    public async Task RemoveAsync(string id)
    {
        await gate.WaitAsync();
        try
        {
            if(!entries.TryGetValue(id,out var entry))return;
            if(entry.Factory is not null)throw new InvalidOperationException("内置插件可以关闭，不能移除。");
            await StopAsync(entry);
            await RequireUnloadedAsync(entry);
            Directory.Delete(entry.Path!,true);entries.TryRemove(id,out _);
        }
        finally{gate.Release();Changed?.Invoke();}
    }
    public async ValueTask DisposeAsync()
    {
        if(disposed)return;disposed=true;await gate.WaitAsync();
        try{foreach(var entry in entries.Values)await StopAsync(entry);entries.Clear();configuration.Clear();}
        finally{gate.Release();gate.Dispose();Changed=null;CompanionChanged=null;MaterialChanged=null;VisualsChanged=null;}
    }
}
