param([Parameter(Mandatory=$true)][string]$NativeDirectory)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\','/')
function Get-RootRelativePath([string]$Path){
    $taskFullPath=[IO.Path]::GetFullPath($Path)
    $taskRootPrefix=$taskRoot+[IO.Path]::DirectorySeparatorChar
    if(!$taskFullPath.StartsWith($taskRootPrefix,[StringComparison]::OrdinalIgnoreCase)){throw "Path is outside GlassDesk: $Path"}
    return $taskFullPath.Substring($taskRootPrefix.Length).Replace('\','/')
}
function Get-Sha256Hex([string]$Path){
    $taskAlgorithm=[Security.Cryptography.SHA256]::Create()
    $taskStream=$null
    try{
        $taskStream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        $taskDigest=$taskAlgorithm.ComputeHash($taskStream)
        return [BitConverter]::ToString($taskDigest).Replace('-','').ToLowerInvariant()
    }finally{
        if($taskStream){$taskStream.Dispose()}
        $taskAlgorithm.Dispose()
    }
}
$taskNative=[IO.Path]::GetFullPath($NativeDirectory).TrimEnd('\','/')
$taskCandidateRoots=@(
    [IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts/native-candidates')).TrimEnd('\','/'),
    [IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts/native-desktop-candidate')).TrimEnd('\','/')
)
$taskNativeParent=[IO.Path]::GetDirectoryName($taskNative)
$taskNativeName=[IO.Path]::GetFileName($taskNative)
$taskNativeDirect=[IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts\native')).TrimEnd('\','/')
$taskIsNativeDirect=[StringComparer]::OrdinalIgnoreCase.Equals($taskNative,$taskNativeDirect)
if(!$taskIsNativeDirect -and (!($taskCandidateRoots | Where-Object {[StringComparer]::OrdinalIgnoreCase.Equals($taskNativeParent,$_)} ) -or $taskNativeName -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,62}[A-Za-z0-9])?$')){
    throw 'NativeDirectory must be a single-level candidate under artifacts/native-candidates or artifacts/native-desktop-candidate, or exactly artifacts\native. Pass -p:GlassDeskNativeDirectory=<candidate path>.'
}

# GetAttributes observes reparse-point entries without relying on Test-Path,
# which can report a dangling symbolic link as absent.
$taskCheckPath=$taskNative
while($taskCheckPath -and ![StringComparer]::OrdinalIgnoreCase.Equals($taskCheckPath,$taskRoot)){
    $taskAttributes=$null
    try{$taskAttributes=[IO.File]::GetAttributes($taskCheckPath)}
    catch [IO.FileNotFoundException]{}
    catch [IO.DirectoryNotFoundException]{}
    catch{throw "Cannot safely inspect native candidate path: $taskCheckPath. $($_.Exception.Message)"}
    $taskEntry=Get-Item -LiteralPath $taskCheckPath -Force -ErrorAction SilentlyContinue
    if(($null -ne $taskAttributes -and ($taskAttributes -band [IO.FileAttributes]::ReparsePoint)) -or
       ($taskEntry -and ($taskEntry.Attributes -band [IO.FileAttributes]::ReparsePoint))){
        throw "Native candidate path passes through a reparse point: $taskCheckPath"
    }
    $taskCheckPath=[IO.Path]::GetDirectoryName($taskCheckPath)
}
if(!(Test-Path -LiteralPath $taskNative -PathType Container)){throw "Native candidate directory does not exist: $taskNative"}

foreach($taskName in @('GlassDesk.Native.dll')) {
    if(!(Test-Path -LiteralPath (Join-Path $taskNative $taskName) -PathType Leaf)) {
        throw "Missing native input: $taskName. Compile into a fresh candidate with scripts/build-native.ps1."
    }
}
if(Test-Path -LiteralPath (Join-Path $taskNative 'GlassDesk.CaptureWorker.exe') -PathType Leaf){
    throw 'Capture workers are prohibited in native candidates; do not reuse an old capture-enabled build.'
}

$taskManifestPath=Join-Path $taskNative 'GlassDesk.Native.candidate.json'
if(!(Test-Path -LiteralPath $taskManifestPath -PathType Leaf)){throw 'Native candidate build provenance is missing. Rebuild into a fresh candidate with scripts/build-native.ps1.'}
if([IO.File]::GetAttributes($taskManifestPath) -band [IO.FileAttributes]::ReparsePoint){throw 'Native candidate build provenance must not be a reparse point.'}
if((Get-Item -LiteralPath $taskManifestPath -Force).Length -gt 2MB){throw 'Native candidate build provenance exceeds the accepted size.'}
try{$taskManifest=Get-Content -LiteralPath $taskManifestPath -Raw | ConvertFrom-Json -ErrorAction Stop}
catch{throw "Native candidate build provenance is invalid JSON: $($_.Exception.Message)"}
if($taskManifest.SchemaVersion -ne 1){throw 'Native candidate build provenance uses an unsupported schema.'}
$taskExpectedCandidateDirectory=Get-RootRelativePath $taskNative
if(![StringComparer]::OrdinalIgnoreCase.Equals([string]$taskManifest.CandidateDirectory,$taskExpectedCandidateDirectory)){
    throw 'Native candidate provenance belongs to a different output directory.'
}
if([string]$taskManifest.Configuration -notin @('Release','Debug')){throw 'Native candidate provenance has an invalid build configuration.'}
if($taskManifest.RegressionIncluded -isnot [bool]){throw 'Native candidate provenance has an invalid regression-build flag.'}

$taskInputFiles=[Collections.Generic.List[IO.FileInfo]]::new()
$taskSourceRoot=Join-Path $taskRoot 'native\GlassDesk.Native'
$taskSourceExtensions=@('.c','.cc','.cpp','.cxx','.h','.hh','.hpp','.hxx','.inl','.hlsl','.rc','.def')
foreach($taskSourceFile in (Get-ChildItem -LiteralPath $taskSourceRoot -File -Recurse -Force | Where-Object {$_.Extension -in $taskSourceExtensions -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]'})){
    $taskInputFiles.Add($taskSourceFile)
}
foreach($taskBuildScript in @('scripts/build-native.ps1','scripts/verify-native.ps1','scripts/verify-capture-free.ps1')){
    $taskInputFiles.Add((Get-Item -LiteralPath (Join-Path $taskRoot $taskBuildScript) -Force))
}
$taskExpectedInputHashes=@{}
foreach($taskInputFile in ($taskInputFiles | Sort-Object FullName)){
    $taskAttributes=[IO.File]::GetAttributes($taskInputFile.FullName)
    if($taskAttributes -band [IO.FileAttributes]::ReparsePoint){throw "Native provenance input is a reparse point: $($taskInputFile.FullName)"}
    $taskInputPath=Get-RootRelativePath $taskInputFile.FullName
    $taskExpectedInputHashes[$taskInputPath]=Get-Sha256Hex $taskInputFile.FullName
}
$taskManifestInputs=@($taskManifest.Inputs)
if($taskManifestInputs.Count -ne $taskExpectedInputHashes.Count){throw 'Native candidate provenance does not cover the current native source and build-script set.'}
$taskSeenInputs=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($taskRecord in $taskManifestInputs){
    $taskInputPath=[string]$taskRecord.Path
    $taskInputHash=[string]$taskRecord.Sha256
    if(!$taskInputPath -or $taskInputHash -notmatch '^[0-9a-f]{64}$' -or !$taskSeenInputs.Add($taskInputPath)){
        throw 'Native candidate provenance contains a malformed or duplicate source hash record.'
    }
    if(!$taskExpectedInputHashes.ContainsKey($taskInputPath)){throw "Native candidate provenance contains an unexpected source input: $taskInputPath"}
    if($taskExpectedInputHashes[$taskInputPath] -cne $taskInputHash){throw "Native candidate is stale for source input: $taskInputPath"}
}

$taskExpectedOutputNames=@('GlassDesk.Native.dll')
if($taskManifest.RegressionIncluded){$taskExpectedOutputNames+= 'GlassDesk.NativeRegression.exe'}
$taskManifestOutputs=@($taskManifest.Outputs)
if($taskManifestOutputs.Count -ne $taskExpectedOutputNames.Count){throw 'Native candidate provenance does not cover the expected compiled outputs.'}
$taskSeenOutputs=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($taskRecord in $taskManifestOutputs){
    $taskOutputName=[string]$taskRecord.Path
    $taskOutputHash=[string]$taskRecord.Sha256
    if($taskOutputName -notin $taskExpectedOutputNames -or $taskOutputHash -notmatch '^[0-9a-f]{64}$' -or !$taskSeenOutputs.Add($taskOutputName)){
        throw 'Native candidate provenance contains an unexpected, malformed, or duplicate output hash record.'
    }
    $taskOutputPath=Join-Path $taskNative $taskOutputName
    if(!(Test-Path -LiteralPath $taskOutputPath -PathType Leaf)){throw "Native candidate output is missing: $taskOutputName"}
    $taskOutputAttributes=[IO.File]::GetAttributes($taskOutputPath)
    if($taskOutputAttributes -band [IO.FileAttributes]::ReparsePoint){throw "Native candidate output is a reparse point: $taskOutputName"}
    $taskActualOutputHash=Get-Sha256Hex $taskOutputPath
    if($taskActualOutputHash -cne $taskOutputHash){throw "Native candidate output hash does not match its build provenance: $taskOutputName"}
}

# Inspect the PE export table from bounded file data. Never load the module:
# LoadLibrary can run DllMain and initialize graphics during a managed build.
if(-not ('GlassDeskBuildExportCheckV2' -as [type])){
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public static class GlassDeskBuildExportCheckV2 {
    private const long MaxImageBytes = 256L * 1024L * 1024L;
    private const int MaxSections = 96;
    private const uint MaxTableEntries = 65536;
    private static byte[] image;

    private static void Require(long offset, long count) {
        if (offset < 0 || count < 0 || offset > image.LongLength || count > image.LongLength - offset)
            throw new InvalidDataException("PE field is outside the file.");
    }

    private static ushort U16(long offset) {
        Require(offset, 2);
        int index = checked((int)offset);
        return (ushort)(image[index] | (image[index + 1] << 8));
    }

    private static uint U32(long offset) {
        Require(offset, 4);
        int index = checked((int)offset);
        return unchecked((uint)(image[index] | (image[index + 1] << 8) |
            (image[index + 2] << 16) | (image[index + 3] << 24)));
    }

    private static long RvaToOffset(uint rva, long sectionTable, ushort sectionCount, out long available) {
        for (int i = 0; i < sectionCount; i++) {
            long section = checked(sectionTable + (long)i * 40L);
            Require(section, 40);
            uint virtualAddress = U32(section + 12);
            uint rawSize = U32(section + 16);
            uint rawPointer = U32(section + 20);
            if (rva < virtualAddress) continue;
            ulong delta = (ulong)rva - virtualAddress;
            if (delta >= rawSize) continue;
            long offset = checked((long)rawPointer + (long)delta);
            available = (long)rawSize - (long)delta;
            Require(offset, available);
            return offset;
        }
        throw new InvalidDataException("PE RVA is not backed by section data.");
    }

    private static long RvaRangeToOffset(uint rva, long count, long sectionTable, ushort sectionCount) {
        if (count < 0) throw new InvalidDataException("Negative PE range length.");
        long available;
        long offset = RvaToOffset(rva, sectionTable, sectionCount, out available);
        if (count > available) throw new InvalidDataException("PE range crosses a section boundary.");
        Require(offset, count);
        return offset;
    }

    private static string AsciiZ(uint rva, long maxLength, long sectionTable, ushort sectionCount) {
        long available;
        long offset = RvaToOffset(rva, sectionTable, sectionCount, out available);
        long boundedLength = Math.Min(Math.Min(available, maxLength), 4096L);
        if (boundedLength <= 0) throw new InvalidDataException("Empty PE string range.");
        Require(offset, boundedLength);
        int start = checked((int)offset);
        int end = checked(start + (int)boundedLength);
        int cursor = start;
        while (cursor < end && image[cursor] != 0) cursor++;
        if (cursor == end) throw new InvalidDataException("PE string is not terminated inside its section range.");
        return Encoding.ASCII.GetString(image, start, cursor - start);
    }

    private static void ReadImage(string path) {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            if (stream.Length < 64 || stream.Length > MaxImageBytes)
                throw new InvalidDataException("Native PE size is outside the accepted bounds.");
            image = new byte[checked((int)stream.Length)];
            int offset = 0;
            while (offset < image.Length) {
                int read = stream.Read(image, offset, image.Length - offset);
                if (read <= 0) throw new EndOfStreamException("Native PE changed while being read.");
                offset = checked(offset + read);
            }
        }
    }

    public static string[] ReadExports(string path) {
        ReadImage(path);
        if (U16(0) != 0x5A4D) throw new InvalidDataException("Not a PE image (DOS signature).");
        long pe = U32(0x3C);
        if (U32(pe) != 0x00004550) throw new InvalidDataException("Not a PE image (PE signature).");
        if (U16(pe + 4) != 0x8664) throw new InvalidDataException("Native PE is not x64.");

        ushort sectionCount = U16(pe + 6);
        if (sectionCount == 0 || sectionCount > MaxSections)
            throw new InvalidDataException("PE section count is outside the accepted bounds.");
        ushort optionalSize = U16(pe + 20);
        long optional = checked(pe + 24);
        Require(optional, optionalSize);
        if (optionalSize < 120) throw new InvalidDataException("PE32+ optional header is too small for an export data directory.");
        if (U16(optional) != 0x20B) throw new InvalidDataException("Native PE must use the PE32+ optional header.");

        uint directoryCount = U32(optional + 108);
        long directories = checked(optional + 112);
        uint maxDirectoryCount = (uint)((optionalSize - 112) / 8);
        if (directoryCount < 1 || directoryCount > maxDirectoryCount)
            throw new InvalidDataException("PE data-directory count exceeds the optional-header bounds.");

        long sectionTable = checked(optional + optionalSize);
        long sectionTableBytes = checked((long)sectionCount * 40L);
        Require(sectionTable, sectionTableBytes);
        for (int i = 0; i < sectionCount; i++) {
            long section = checked(sectionTable + (long)i * 40L);
            uint rawSize = U32(section + 16);
            uint rawPointer = U32(section + 20);
            if (rawSize != 0) Require(rawPointer, rawSize);
        }

        uint exportRva = U32(directories);
        uint exportSize = U32(directories + 4);
        if (exportRva == 0 || exportSize < 40 || exportSize > 16U * 1024U * 1024U)
            throw new InvalidDataException("PE export directory size is invalid.");
        ulong exportEnd = (ulong)exportRva + exportSize;
        if (exportEnd > 0x100000000UL) throw new InvalidDataException("PE export range overflows the RVA space.");
        long export = RvaRangeToOffset(exportRva, exportSize, sectionTable, sectionCount);

        uint functionCount = U32(export + 20);
        uint nameCount = U32(export + 24);
        uint functionsRva = U32(export + 28);
        uint namesRva = U32(export + 32);
        uint ordinalsRva = U32(export + 36);
        if (functionCount == 0 || functionCount > MaxTableEntries || nameCount == 0 ||
            nameCount > MaxTableEntries || nameCount > functionCount)
            throw new InvalidDataException("PE export table counts are invalid.");

        long functions = RvaRangeToOffset(functionsRva, checked((long)functionCount * 4L), sectionTable, sectionCount);
        long names = RvaRangeToOffset(namesRva, checked((long)nameCount * 4L), sectionTable, sectionCount);
        long ordinals = RvaRangeToOffset(ordinalsRva, checked((long)nameCount * 2L), sectionTable, sectionCount);
        var result = new List<string>((int)nameCount);
        for (uint i = 0; i < nameCount; i++) {
            uint nameRva = U32(checked(names + (long)i * 4L));
            ushort ordinal = U16(checked(ordinals + (long)i * 2L));
            if (ordinal >= functionCount) throw new InvalidDataException("PE export ordinal is outside the function table.");
            uint functionRva = U32(checked(functions + (long)ordinal * 4L));
            if (functionRva == 0) throw new InvalidDataException("PE named export has a null function RVA.");

            if ((ulong)functionRva >= exportRva && (ulong)functionRva < exportEnd) {
                long forwarderBytes = checked((long)(exportEnd - functionRva));
                string forwarder = AsciiZ(functionRva, forwarderBytes, sectionTable, sectionCount);
                if (forwarder.Length == 0) throw new InvalidDataException("PE forwarder export is empty.");
            } else {
                long available;
                RvaToOffset(functionRva, sectionTable, sectionCount, out available);
            }

            string name = AsciiZ(nameRva, 4096L, sectionTable, sectionCount);
            if (name.Length == 0) throw new InvalidDataException("PE export name is empty.");
            result.Add(name);
        }
        return result.ToArray();
    }
}
'@
}

$taskNativeDll=Join-Path $taskNative 'GlassDesk.Native.dll'
$taskExports=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($taskExport in [GlassDeskBuildExportCheckV2]::ReadExports($taskNativeDll)){$null=$taskExports.Add($taskExport)}
foreach($taskSymbol in @('GDGetNativeAbiInfo','GDConfigureTypographyV2','GDMeasureTextV2','GDRenderSurfaceV2','GDCreateLocalGlass','GDRenderLocalGlass','GDDestroyLocalGlass','GDSetLocalGlassImage','GDCreateV2','GDSetOwnedGlassImage','GDPresentOwnedGlass','GDTripOwnedGlass')) {
foreach($taskSymbol in @('GdLiquidGlass_Create','GdLiquidGlass_Update','GdLiquidGlass_SetEnabled','GdLiquidGlass_Destroy','GdLiquidGlass_ResetFuse','GdLiquidGlass_GetInkIsDark')) {
    if(!$taskExports.Contains($taskSymbol)) {
        throw "Native input is missing the rewritten liquid-glass capture export ($taskSymbol). Rebuild into a fresh candidate with scripts/build-native.ps1; do not reuse an old native DLL."
    }
}
    if(!$taskExports.Contains($taskSymbol)) {
        throw "Native input is incompatible ($taskSymbol missing). Rebuild into a fresh candidate with scripts/build-native.ps1; do not reuse an old native DLL."
    }
}
foreach($taskSymbol in @('GDGetLiveGlassState','GDFreezeLiveGlassEvidence','GDSaveOwnedGlassEvidence')){
    if($taskExports.Contains($taskSymbol)){throw "Native input still exports the prohibited live-capture API: $taskSymbol"}
}
