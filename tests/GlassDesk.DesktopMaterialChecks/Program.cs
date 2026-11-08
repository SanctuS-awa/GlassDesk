using GlassDesk.Core;
using GlassDesk.App;

int checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new InvalidOperationException("FAIL " + label);
    checks++;
    Console.WriteLine("PASS " + label);
}

Check(!LaunchOptions.LiquidGlassCapturePreviewBuild||LaunchOptions.PreviewBuild,
    "system-compositor compile guard cannot be enabled without the UI preview build guard");

bool rejectedScreenCapture=false;
try { LaunchOptions.Parse(["--ui-preview","--live-desktop-glass"]); }
catch(InvalidOperationException) { rejectedScreenCapture=true; }
Check(rejectedScreenCapture,"removed live-screen capture flag is refused before any host creation");
string root = Path.Combine(Path.GetTempPath(), "glassdesk-desktop-material-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string fusePath = Path.Combine(root, "desktop-material-fuse.json");
    var fuse = new DesktopMaterialExperimentFuse(fusePath);
    var unarmed = DesktopMaterialSession.CreateBeforeHostCreation(
        DesktopMaterialBackend.OwnedImageCandidate, fuse, allowCandidateOnThisMachine: true);
    Check(unarmed.Backend == DesktopMaterialBackend.Solid
        && unarmed.FuseResult == DesktopMaterialFuseResult.NoAuthorization,
        "candidate is off by default when the persisted fuse is missing");

    Check(fuse.ArmNextStart() == DesktopMaterialFuseResult.Armed, "explicit action arms one startup");
    Check(fuse.ArmNextStart() == DesktopMaterialFuseResult.AlreadyArmed, "arming does not duplicate or silently refresh authorization");
    var wrongMachine = DesktopMaterialSession.CreateBeforeHostCreation(
        DesktopMaterialBackend.OwnedImageCandidate, fuse, allowCandidateOnThisMachine: false);
    Check(wrongMachine.Backend == DesktopMaterialBackend.Solid
        && wrongMachine.FuseResult == DesktopMaterialFuseResult.Consumed
        && wrongMachine.TryCompleteCleanExit(),
        "machine gate denies candidate and closes consumed authorization cleanly");
    var afterWrongMachine = DesktopMaterialSession.CreateBeforeHostCreation(
        DesktopMaterialBackend.OwnedImageCandidate, fuse, allowCandidateOnThisMachine: true);
    Check(afterWrongMachine.Backend == DesktopMaterialBackend.Solid,
        "a denied machine startup cannot leave an authorization for later auto-enable");

    Check(fuse.ArmNextStart() == DesktopMaterialFuseResult.Armed, "explicit rearm is required for a later candidate session");
    var candidate = DesktopMaterialSession.CreateBeforeHostCreation(
        DesktopMaterialBackend.OwnedImageCandidate, fuse, DesktopMaterialBackend.LegacySystemMaterial,
        allowCandidateOnThisMachine: true);
    Check(candidate.Backend == DesktopMaterialBackend.OwnedImageCandidate
        && candidate.HostOptions.Backend == DesktopMaterialBackend.OwnedImageCandidate
        && candidate.HostOptions.SessionId == candidate.SessionId
        && candidate.HostOptions.AbiVersion == 1,
        "authorized session freezes candidate backend into host creation options");
    Check(candidate.MarkFault("simulated compositor fault") && candidate.IsExperimentTripped,
        "candidate fault trips and persists its session state");
    Check(!candidate.TryCompleteCleanExit(), "clean exit cannot clear a tripped session");
    var afterFault = DesktopMaterialSession.CreateBeforeHostCreation(
        DesktopMaterialBackend.OwnedImageCandidate, fuse, allowCandidateOnThisMachine: true);
    Check(afterFault.Backend == DesktopMaterialBackend.Solid,
        "tripped fuse never automatically rearms on the next launch");
    var systemFuse = new DesktopMaterialExperimentFuse(Path.Combine(root, "system-compositor-fuse.json"));
    Check(systemFuse.ArmNextStart() == DesktopMaterialFuseResult.Armed,
        "system-compositor candidate uses the same explicit one-start arm step");
    var systemCandidate = DesktopMaterialSession.CreateBeforeHostCreation(
        DesktopMaterialBackend.LiquidGlassCapture, systemFuse, DesktopMaterialBackend.Solid,
        allowCandidateOnThisMachine: true);
    Check(systemCandidate.Backend == DesktopMaterialBackend.LiquidGlassCapture
        && systemCandidate.IsExperimentalCandidate
        && systemCandidate.HostOptions.Backend == DesktopMaterialBackend.LiquidGlassCapture
        && systemCandidate.HostOptions.SessionId == systemCandidate.SessionId
        && systemCandidate.HostOptions.AbiVersion == 1
        && !systemCandidate.HostOptions.LiquidGlassCapturePreview,
        "authorized system-compositor short-test session freezes backend 3 into ABI v1 options");
    Check(systemCandidate.MarkFault("simulated system-compositor fault") && systemCandidate.IsExperimentTripped
        && !systemCandidate.TryCompleteCleanExit(),
        "system-compositor fault durably trips its one-start session");
    var systemAfterFault = DesktopMaterialSession.CreateBeforeHostCreation(
        DesktopMaterialBackend.LiquidGlassCapture, systemFuse, allowCandidateOnThisMachine: true);
    Check(systemAfterFault.Backend == DesktopMaterialBackend.Solid
        && systemFuse.ArmNextStart() == DesktopMaterialFuseResult.Unavailable,
        "system-compositor candidate stays disabled after a fault and cannot rearm");

    Check(fuse.ArmNextStart() == DesktopMaterialFuseResult.Unavailable,
        "a tripped fuse cannot be rearmed through the normal API");
    var cleanFuse = new DesktopMaterialExperimentFuse(Path.Combine(root, "clean-fuse.json"));
    Check(cleanFuse.ArmNextStart() == DesktopMaterialFuseResult.Armed,
        "a fresh fuse can explicitly authorize an independent clean-session check");
    var clean = DesktopMaterialSession.CreateBeforeHostCreation(
        DesktopMaterialBackend.OwnedImageCandidate, cleanFuse, allowCandidateOnThisMachine: true);
    Check(clean.Backend == DesktopMaterialBackend.OwnedImageCandidate && clean.TryCompleteCleanExit(),
        "clean session records a clean result");
    Check(clean.MarkFault("late fault after clean completion") && clean.IsExperimentTripped
        && !clean.TryCompleteCleanExit(),
        "a late fault trips the persisted session and clean exit cannot clear it");
    var afterClean = DesktopMaterialSession.CreateBeforeHostCreation(
        DesktopMaterialBackend.OwnedImageCandidate, cleanFuse, allowCandidateOnThisMachine: true);
    Check(afterClean.Backend == DesktopMaterialBackend.Solid,
        "tripped completed session remains disabled on the next launch");
    Check(cleanFuse.ArmNextStart() == DesktopMaterialFuseResult.Unavailable,
        "late fault cannot be cleared by normal rearm");

    var interruptedFuse = new DesktopMaterialExperimentFuse(Path.Combine(root, "interrupted-fuse.json"));
    Check(interruptedFuse.ArmNextStart() == DesktopMaterialFuseResult.Armed,
        "interrupted-session fuse arms explicitly");
    var interrupted = DesktopMaterialSession.CreateBeforeHostCreation(
        DesktopMaterialBackend.OwnedImageCandidate, interruptedFuse, allowCandidateOnThisMachine: true);
    var afterUncleanProcess = DesktopMaterialSession.CreateBeforeHostCreation(
        DesktopMaterialBackend.OwnedImageCandidate, interruptedFuse, allowCandidateOnThisMachine: true);
    Check(interrupted.Backend == DesktopMaterialBackend.OwnedImageCandidate
        && afterUncleanProcess.Backend == DesktopMaterialBackend.Solid
        && !interrupted.TryCompleteCleanExit(),
        "a later startup trips an unfinished session and blocks candidate re-entry");
    Check(interruptedFuse.ArmNextStart() == DesktopMaterialFuseResult.Unavailable,
        "an interrupted session stays tripped without an out-of-band recovery path");

    var concurrentFuse = new DesktopMaterialExperimentFuse(Path.Combine(root, "concurrent-fuse.json"));
    Check(concurrentFuse.ArmNextStart() == DesktopMaterialFuseResult.Armed,
        "concurrency fuse arms explicitly");
    var concurrentSessions = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        DesktopMaterialSession.CreateBeforeHostCreation(DesktopMaterialBackend.OwnedImageCandidate,
            new DesktopMaterialExperimentFuse(Path.Combine(root, "concurrent-fuse.json")),
            allowCandidateOnThisMachine: true))));
    Check(concurrentSessions.Count(session => session.Backend == DesktopMaterialBackend.OwnedImageCandidate) == 1,
        "separate callers cannot consume one persisted authorization twice");

    string corruptPath = Path.Combine(root, "corrupt-fuse.json");
    var corruptFuse = new DesktopMaterialExperimentFuse(corruptPath);
    Check(corruptFuse.ArmNextStart() == DesktopMaterialFuseResult.Armed, "isolated fuse can arm before corruption check");
    File.WriteAllText(corruptPath, "{\"Version\":1,\"Generation\":2,\"Mode\":\"armed\"}");
    var corrupt = DesktopMaterialSession.CreateBeforeHostCreation(
        DesktopMaterialBackend.OwnedImageCandidate, corruptFuse, allowCandidateOnThisMachine: true);
    Check(corrupt.Backend == DesktopMaterialBackend.Solid
        && corrupt.FuseResult == DesktopMaterialFuseResult.CorruptState,
        "malformed persisted fuse fails closed");
    Check(corruptFuse.ArmNextStart() == DesktopMaterialFuseResult.CorruptState,
        "corrupt state cannot be repaired into an implicit authorization");

    string truncatedPath = Path.Combine(root, "truncated-fuse.json");
    var truncatedFuse = new DesktopMaterialExperimentFuse(truncatedPath);
    Check(truncatedFuse.ArmNextStart() == DesktopMaterialFuseResult.Armed, "truncation fuse arms explicitly");
    byte[] fuseBytes = File.ReadAllBytes(truncatedPath);
    File.WriteAllBytes(truncatedPath, fuseBytes[..(fuseBytes.Length / 2)]);
    var truncated = DesktopMaterialSession.CreateBeforeHostCreation(
        DesktopMaterialBackend.OwnedImageCandidate, truncatedFuse, allowCandidateOnThisMachine: true);
    Check(truncated.Backend == DesktopMaterialBackend.Solid
        && truncated.FuseResult == DesktopMaterialFuseResult.CorruptState,
        "truncated state cannot authorize the candidate");

    string lockedPath = Path.Combine(root, "locked-fuse.json");
    var lockFuse = new DesktopMaterialExperimentFuse(lockedPath);
    Check(lockFuse.ArmNextStart() == DesktopMaterialFuseResult.Armed, "lock-contention fuse arms explicitly");
    using (new FileStream(lockedPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
    {
        var locked = DesktopMaterialSession.CreateBeforeHostCreation(
            DesktopMaterialBackend.OwnedImageCandidate, lockFuse, allowCandidateOnThisMachine: true);
        Check(locked.Backend == DesktopMaterialBackend.Solid
            && locked.FuseResult == DesktopMaterialFuseResult.Unavailable,
            "exclusive lock contention fails closed for this startup");
    }
    Check(lockFuse.ArmNextStart() == DesktopMaterialFuseResult.AlreadyArmed,
        "lock contention does not silently create a new authorization");

    string invalidJsonPath = Path.Combine(root, "invalid-json-fuse.json");
    var invalidJsonFuse = new DesktopMaterialExperimentFuse(invalidJsonPath);
    File.WriteAllText(invalidJsonPath, "not-json");
    var invalidJson = DesktopMaterialSession.CreateBeforeHostCreation(
        DesktopMaterialBackend.OwnedImageCandidate, invalidJsonFuse, allowCandidateOnThisMachine: true);
    Check(invalidJson.Backend == DesktopMaterialBackend.Solid
        && invalidJson.FuseResult == DesktopMaterialFuseResult.CorruptState,
        "unparseable persisted state is classified as corrupt and denied");

    string blockedDirectory = Path.Combine(root, "not-a-directory");
    File.WriteAllText(blockedDirectory, "file");
    var unavailableFuse = new DesktopMaterialExperimentFuse(Path.Combine(blockedDirectory, "fuse.json"));
    var unavailable = DesktopMaterialSession.CreateBeforeHostCreation(
        DesktopMaterialBackend.OwnedImageCandidate, unavailableFuse, allowCandidateOnThisMachine: true);
    Check(unavailable.Backend == DesktopMaterialBackend.Solid
        && unavailable.FuseResult == DesktopMaterialFuseResult.Unavailable,
        "storage failures deny candidate selection");

    var legacy = DesktopMaterialSession.CreateBeforeHostCreation(
        DesktopMaterialBackend.LegacySystemMaterial, null);
    Check(legacy.Backend == DesktopMaterialBackend.LegacySystemMaterial,
        "explicit non-experimental legacy selection does not turn on candidate material");

    string appData = Path.Combine(root, "app-preview-data");
    var parsedCandidate = LaunchOptions.Parse([
        "--ui-preview", "--data-dir=" + appData,
        "--independent-graphics-machine=" + Environment.MachineName,
        "--owned-image-glass"]);
    Check(parsedCandidate.OwnedImageGlass && !parsedCandidate.ArmOwnedImageGlass
        && parsedCandidate.IndependentGraphicsMachine == Environment.MachineName
        && parsedCandidate.DataDirectory == Path.GetFullPath(appData),
        "CLI option parsing preserves the isolated owned-image machine and data flags");
    var implicitDataCandidate = LaunchOptions.Parse([
        "--ui-preview", "--independent-graphics-machine=" + Environment.MachineName,
        "--owned-image-glass"]);
    Check(implicitDataCandidate.DataDirectory == Path.Combine(AppContext.BaseDirectory, "preview-data")
        && !DesktopMaterialLaunch.AllowedOnThisMachine(implicitDataCandidate),
        "implicit preview data directory alone must not authorize candidate launch");
    const string simulatedIndependentMachine = "INDEPENDENT-TEST";
    var simulatedExplicitCandidate = LaunchOptions.Parse([
        "--ui-preview", "--data-dir=" + appData,
        "--independent-graphics-machine=" + simulatedIndependentMachine,
        "--owned-image-glass"]);
    var simulatedImplicitCandidate = LaunchOptions.Parse([
        "--ui-preview",
        "--independent-graphics-machine=" + simulatedIndependentMachine,
        "--owned-image-glass"]);
    Check(simulatedExplicitCandidate.ExplicitDataDirectory
        && DesktopMaterialLaunch.AllowedForMachine(simulatedExplicitCandidate, simulatedIndependentMachine),
        "explicit isolated data directory passes the simulated independent-machine gate");
    Check(!simulatedImplicitCandidate.ExplicitDataDirectory
        && !DesktopMaterialLaunch.AllowedForMachine(simulatedImplicitCandidate, simulatedIndependentMachine),
        "implicit preview directory fails closed even on a simulated approved machine");
    Check(!DesktopMaterialLaunch.AllowedForMachine(
            simulatedExplicitCandidate with { IndependentGraphicsMachine = "OTHER-TEST" },
            simulatedIndependentMachine),
        "candidate launch is denied when its declared machine does not match the host");
    Check(!DesktopMaterialLaunch.AllowedForMachine(simulatedExplicitCandidate, "sheng"),
        "incident host remains denied even with an explicit isolated directory");
    var selfIdentifiedCandidate = simulatedExplicitCandidate with
    {
        IndependentGraphicsMachine = Environment.MachineName
    };
    Check(DesktopMaterialLaunch.AllowedOnThisMachine(selfIdentifiedCandidate)
        == !string.Equals(Environment.MachineName, "sheng", StringComparison.OrdinalIgnoreCase),
        "actual current-host check honors the sheng hard block");
    Check(DesktopMaterialLaunch.FusePath(parsedCandidate)
        == Path.Combine(Path.GetFullPath(appData), "desktop-material-20261024-v1.json"),
        "launch helper binds the fuse file to this candidate build tag and preview data directory");
    var blockedMachineCandidate = parsedCandidate with { IndependentGraphicsMachine = "sheng" };
    Check(!DesktopMaterialLaunch.AllowedOnThisMachine(blockedMachineCandidate)
        && Throws<InvalidOperationException>(() => DesktopMaterialLaunch.Validate(blockedMachineCandidate))
        && Throws<InvalidOperationException>(() => DesktopMaterialLaunch.CreateSession(blockedMachineCandidate)),
        "sheng denies candidate launch before host construction");
    var parsedArm = LaunchOptions.Parse([
        "--ui-preview", "--data-dir=" + appData,
        "--independent-graphics-machine=sheng",
        "--arm-owned-image-glass"]);
    Check(parsedArm.ArmOwnedImageGlass && !parsedArm.OwnedImageGlass
        && Throws<InvalidOperationException>(() => DesktopMaterialLaunch.Validate(parsedArm)),
        "arm CLI flag is separate and the incident machine gate blocks it");
    var parsedArmAndRun = LaunchOptions.Parse([
        "--ui-preview", "--data-dir=" + appData,
        "--independent-graphics-machine=" + simulatedIndependentMachine,
        "--arm-owned-image-glass", "--owned-image-glass"]);
    Check(parsedArmAndRun.ArmOwnedImageGlass && parsedArmAndRun.OwnedImageGlass
        && DesktopMaterialLaunch.AllowedForMachine(parsedArmAndRun, simulatedIndependentMachine)
        && Throws<InvalidOperationException>(() => DesktopMaterialLaunch.Validate(parsedArmAndRun)),
        "combined arm and run flags are rejected by validation even when the simulated host gate passes");
    var parsedSystemCandidate = LaunchOptions.Parse([
        "--ui-preview", "--data-dir=" + appData,
        "--qa-report=" + Path.Combine(appData, "incident-qa.json"), "--qa-cycles=4",
        "--independent-graphics-machine=sheng",
        "--authorized-incident-host-short-test", "--system-compositor-glass"]);
    Check(parsedSystemCandidate.LiquidGlassCaptureGlass && parsedSystemCandidate.IncidentHostShortTest
        && parsedSystemCandidate.QaCycles == 4 && parsedSystemCandidate.ExplicitDataDirectory,
        "system-compositor CLI flag preserves the incident short-test requirements");
    Check(DesktopMaterialLaunch.AllowedForMachine(parsedSystemCandidate, "sheng")
        && !DesktopMaterialLaunch.AllowedForMachine(parsedSystemCandidate, simulatedIndependentMachine),
        "system-compositor launch is restricted to the explicitly authorized incident host");
    var parsedSystemWithoutShortTest = LaunchOptions.Parse([
        "--ui-preview", "--data-dir=" + appData,
        "--independent-graphics-machine=sheng", "--system-compositor-glass"]);
    Check(Throws<InvalidOperationException>(() => DesktopMaterialLaunch.Validate(parsedSystemWithoutShortTest)),
        "system-compositor flag without the authorized short-test token is rejected before host creation");
    Check(Throws<InvalidOperationException>(() => DesktopMaterialLaunch.CreateSession(parsedSystemCandidate)),
        "system-compositor request without an armed fuse fails closed instead of selecting the legacy material");

    string systemPreviewData=Path.Combine(root,"system-compositor-preview-data");
    var parsedSystemPreview=LaunchOptions.Parse([
        "--ui-preview","--data-dir="+systemPreviewData,
        "--independent-graphics-machine=sheng","--system-compositor-preview"]);
    Check(parsedSystemPreview.LiquidGlassCapturePreview&&!parsedSystemPreview.LiquidGlassCaptureGlass
        &&parsedSystemPreview.ExplicitDataDirectory&&parsedSystemPreview.QaCycles==0,
        "persistent-preview CLI selects a separate mode with an explicit isolated data directory");
    Check(DesktopMaterialLaunch.AllowedForMachine(parsedSystemPreview,"sheng")==LaunchOptions.LiquidGlassCapturePreviewBuild
        &&!DesktopMaterialLaunch.AllowedForMachine(parsedSystemPreview,simulatedIndependentMachine),
        "persistent preview is available only in its opted-in build on sheng");
    var parsedPreviewArm=LaunchOptions.Parse([
        "--ui-preview","--data-dir="+systemPreviewData,
        "--independent-graphics-machine=sheng","--arm-system-compositor-preview"]);
    bool currentHostIsPreviewTarget=string.Equals(Environment.MachineName,"sheng",StringComparison.OrdinalIgnoreCase);
    if(LaunchOptions.LiquidGlassCapturePreviewBuild&&currentHostIsPreviewTarget)
    {
        DesktopMaterialLaunch.Validate(parsedSystemPreview);
        var launchFuse=new DesktopMaterialExperimentFuse(DesktopMaterialLaunch.FusePath(parsedSystemPreview));
        Check(launchFuse.ArmNextStart()==DesktopMaterialFuseResult.Armed,
            "managed persistent-preview launch test arms only its temporary isolated fuse");
        var launchPreviewSession=DesktopMaterialLaunch.CreateSession(parsedSystemPreview);
        Check(launchPreviewSession.Backend==DesktopMaterialBackend.LiquidGlassCapture
            &&launchPreviewSession.HostOptions.AbiVersion==2
            &&launchPreviewSession.HostOptions.LiquidGlassCapturePreview
            &&launchPreviewSession.TryCompleteCleanExit(),
            "opted-in managed launch consumes a one-start fuse and creates version 2 backend-3 session options");
        Check(Throws<InvalidOperationException>(()=>DesktopMaterialLaunch.CreateSession(parsedPreviewArm)),
            "arm launch cannot accidentally create a desktop session after the explicit fuse update");
    }
    else
        Check(Throws<InvalidOperationException>(()=>DesktopMaterialLaunch.Validate(parsedSystemPreview)),
            "persistent preview launch validation denies an unguarded build or non-target machine");
    var previewWithoutData=LaunchOptions.Parse([
        "--ui-preview","--independent-graphics-machine=sheng","--system-compositor-preview"]);
    var previewWithoutUi=LaunchOptions.Parse([
        "--data-dir="+systemPreviewData,"--independent-graphics-machine=sheng","--system-compositor-preview"])
        with {UiPreview=false};
    Check(Throws<InvalidOperationException>(()=>DesktopMaterialLaunch.Validate(previewWithoutData))
        &&Throws<InvalidOperationException>(()=>DesktopMaterialLaunch.Validate(previewWithoutUi)),
        "persistent preview rejects implicit data and normal-app launch modes");
    var previewWithShortTest=LaunchOptions.Parse([
        "--ui-preview","--data-dir="+systemPreviewData,
        "--independent-graphics-machine=sheng","--authorized-incident-host-short-test","--system-compositor-preview"]);
    Check(Throws<InvalidOperationException>(()=>DesktopMaterialLaunch.Validate(previewWithShortTest)),
        "persistent preview cannot opt into the bounded short-test ABI or deadline");
    var previewArmAndRun=LaunchOptions.Parse([
        "--ui-preview","--data-dir="+systemPreviewData,
        "--independent-graphics-machine=sheng","--arm-system-compositor-preview","--system-compositor-preview"]);
    Check(Throws<InvalidOperationException>(()=>DesktopMaterialLaunch.Validate(previewArmAndRun)),
        "persistent-preview fuse arm and run are separate explicit launches");
    Check(parsedPreviewArm.ArmLiquidGlassCapturePreview&&!parsedPreviewArm.LiquidGlassCapturePreview
        &&DesktopMaterialLaunch.AllowedForMachine(parsedPreviewArm,"sheng")==LaunchOptions.LiquidGlassCapturePreviewBuild,
        "preview arm action uses the same build, host, and isolated-directory gate");
    var systemPreviewFuse=new DesktopMaterialExperimentFuse(Path.Combine(root,"system-preview-fuse.json"));
    Check(systemPreviewFuse.ArmNextStart()==DesktopMaterialFuseResult.Armed,
        "persistent-preview session requires a separate explicit fuse arm");
    var systemPreviewSession=DesktopMaterialSession.CreateBeforeHostCreation(
        DesktopMaterialBackend.LiquidGlassCapture,systemPreviewFuse,DesktopMaterialBackend.Solid,
        allowCandidateOnThisMachine:true,liquidGlassCapturePreview:true);
    Check(systemPreviewSession.Backend==DesktopMaterialBackend.LiquidGlassCapture
        &&systemPreviewSession.HostOptions.AbiVersion==2
        &&systemPreviewSession.HostOptions.LiquidGlassCapturePreview,
        "persistent preview freezes backend 3 into version 2 host options");
    Check(systemPreviewFuse.ArmNextStart()==DesktopMaterialFuseResult.Unavailable,
        "an active persistent-preview fuse cannot rearm while the session is running");
    Check(systemPreviewSession.TryCompleteCleanExit()
        &&systemPreviewFuse.ArmNextStart()==DesktopMaterialFuseResult.Armed,
        "only a cleanly closed persistent-preview session permits an explicit later rearm");
    var persistentSessionAfterCleanExit=DesktopMaterialSession.CreateBeforeHostCreation(
        DesktopMaterialBackend.LiquidGlassCapture,systemPreviewFuse,DesktopMaterialBackend.Solid,
        allowCandidateOnThisMachine:true,liquidGlassCapturePreview:true);
    Check(persistentSessionAfterCleanExit.HostOptions.AbiVersion==2
        &&persistentSessionAfterCleanExit.Backend==DesktopMaterialBackend.LiquidGlassCapture,
        "an explicit second arm can start a new persistent preview session");
    Check(persistentSessionAfterCleanExit.MarkFault("preview graphics fault")
        &&!persistentSessionAfterCleanExit.TryCompleteCleanExit()
        &&systemPreviewFuse.ArmNextStart()==DesktopMaterialFuseResult.Unavailable,
        "a preview fault stays tripped and clean-exit or rearm cannot clear it");
    Check(LaunchOptions.LiquidGlassCapturePreviewBuild
        ||!DesktopMaterialLaunch.AllowedForMachine(parsedSystemPreview,"sheng"),
        "persistent preview cannot bypass the build guard at runtime");

    var normalAppOptions = LaunchOptions.Parse(["--data-dir=" + appData]);
    DesktopMaterialLaunch.Validate(normalAppOptions);
    Check(!normalAppOptions.OwnedImageGlass && !normalAppOptions.LiquidGlassCaptureGlass
        && !normalAppOptions.ArmOwnedImageGlass && !normalAppOptions.LiquidGlassCapturePreview
        && !normalAppOptions.ArmLiquidGlassCapturePreview,
        "ordinary launch options do not request either experiment");
    var normalAppSession = DesktopMaterialLaunch.CreateSession(normalAppOptions);
    Check(normalAppSession.Backend == DesktopMaterialBackend.LegacySystemMaterial
        && normalAppSession.FuseResult == DesktopMaterialFuseResult.NoAuthorization,
        "ordinary app session never selects the owned-image candidate by default");

    var productionBudget = new DesktopWallpaperMappingBudget();
    Check(productionBudget.MaxImageDimension == 4096 && productionBudget.MaxImagePixels == 16_000_000
        && productionBudget.MaxImageFileBytes == 40L * 1024 * 1024,
        "default decode limits match the approved 4096 edge, 16 MP, and 40 MiB budgets");
    Check(productionBudget.Allows(new(4000, 4000))
        && !productionBudget.Allows(new(4096, 4096))
        && !productionBudget.Allows(new(4097, 1)),
        "default decoded-image budget enforces both edge and total pixels");
    Check(productionBudget.AllowsSourceFileBytes(40L * 1024 * 1024)
        && !productionBudget.AllowsSourceFileBytes(40L * 1024 * 1024 + 1)
        && !productionBudget.AllowsSourceFileBytes(0),
        "source-file byte limit is exposed for pre-decode enforcement");
    Check(DesktopWallpaperMapping.TryCreate(new(0, 0, 100, 100), new(1, 1),
            40L * 1024 * 1024, DesktopWallpaperPositionMode.Fill, out _)
        && !DesktopWallpaperMapping.TryCreate(new(0, 0, 100, 100), new(1, 1),
            40L * 1024 * 1024 + 1, DesktopWallpaperPositionMode.Fill, out _),
        "mapping overload checks source bytes before accepting the image model");

    Check(DesktopWallpaperMapping.TryCreate(new(-1920, 0, 1920, 1080), new(1000, 1000),
        DesktopWallpaperPositionMode.Fill, out var fill), "FILL mapping creates for offset physical monitor");
    Check(fill!.DestinationWidth == 1920 && Math.Abs(fill.DestinationY - (-420)) < 1e-9,
        "FILL preserves aspect and center-crops vertically");
    Check(fill.TryMapScreenPoint(-1920, 0, out var fillTopLeft)
        && fillTopLeft is { IsOnMonitor: true, IsCoveredByImage: true }
        && Math.Abs(fillTopLeft.PixelX) < 1e-9 && Math.Abs(fillTopLeft.PixelY - 218.75) < 1e-9,
        "FILL maps physical coordinates to centered crop pixels");

    Check(DesktopWallpaperMapping.TryCreate(new(100, 50, 1920, 1080), new(1000, 1000),
        DesktopWallpaperPositionMode.Fit, out var fit), "FIT mapping creates");
    Check(fit!.TryMapScreenPoint(110, 100, out var fitBar)
        && fitBar.IsOnMonitor && !fitBar.IsCoveredByImage,
        "FIT reports letterbox area as uncovered");
    Check(fit.TryMapScreenPoint(600, 500, out var fitImage)
        && fitImage.IsCoveredByImage && Math.Abs(fitImage.PixelX - (80 * 1000.0 / 1080)) < 1e-9,
        "FIT maps the centered image region");

    Check(DesktopWallpaperMapping.TryCreate(new(0, 0, 2000, 1000), new(1000, 1000),
        DesktopWallpaperPositionMode.Stretch, out var stretch), "STRETCH mapping creates");
    Check(stretch!.TryMapScreenPoint(1000, 500, out var stretchCenter)
        && stretchCenter.IsCoveredByImage && stretchCenter.PixelX == 500 && stretchCenter.PixelY == 500,
        "STRETCH scales axes independently");

    Check(DesktopWallpaperMapping.TryCreate(new(0, 0, 1920, 1080), new(1000, 1000),
        DesktopWallpaperPositionMode.Center, out var center), "CENTER mapping creates");
    Check(center!.TryMapScreenPoint(400, 100, out var centerOutside)
        && !centerOutside.IsCoveredByImage
        && center.TryMapScreenPoint(500, 100, out var centerInside) && centerInside.IsCoveredByImage,
        "CENTER preserves 1:1 pixels and identifies surrounding background");

    var strictBudget = new DesktopWallpaperMappingBudget(maxImageDimension: 8, maxImagePixels: 60);
    Check(!DesktopWallpaperMapping.TryCreate(new(0, 0, 100, 100), new(8, 8),
        DesktopWallpaperPositionMode.Fill, out _, strictBudget),
        "mapping enforces image pixel budget before sampling");
    Check(!DesktopWallpaperMapping.TryCreate(new(0, 0, 100, 100), new(9, 1),
        DesktopWallpaperPositionMode.Fill, out _, strictBudget),
        "mapping enforces maximum image dimension");
    Check(!DesktopWallpaperMapping.TryCreate(new(double.MaxValue / 2, 0, double.MaxValue, 1), new(1, 1),
        DesktopWallpaperPositionMode.Fill, out _),
        "mapping rejects monitor-coordinate overflow");
    Check(!DesktopWallpaperMapping.TryCreate(new(1e300, 0, 1, 10), new(1, 1),
        DesktopWallpaperPositionMode.Fill, out _),
        "mapping rejects monitor dimensions lost to floating-point precision");
    Check(!DesktopWallpaperMapping.TryCreate(new(0, 0, double.NaN, 10), new(1, 1),
        DesktopWallpaperPositionMode.Fill, out _),
        "mapping rejects non-finite monitor geometry");
    Check(!fill.TryMapScreenPoint(double.PositiveInfinity, 0, out _),
        "mapping rejects non-finite screen sample coordinates");

    Console.WriteLine($"{checks} desktop material checks passed.");
}
finally
{
    try { Directory.Delete(root, recursive: true); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}

static bool Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return true; }
    return false;
}
