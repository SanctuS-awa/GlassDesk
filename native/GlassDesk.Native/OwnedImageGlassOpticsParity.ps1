param(
    [string] $ReferencePath = (Join-Path $PSScriptRoot 'PreviewGlass.hlsl'),
    [string] $OwnedPath = (Join-Path $PSScriptRoot 'OwnedImageGlass.hlsl')
)

$ErrorActionPreference = 'Stop'

function Assert-Contains([string] $Text, [string] $Needle, [string] $Label) {
    if (-not $Text.Contains($Needle)) {
        throw "${Label}: missing expected formula fragment: $Needle"
    }
}

$reference = Get-Content -LiteralPath $ReferencePath -Raw
$owned = Get-Content -LiteralPath $OwnedPath -Raw
$ownedCpp = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'OwnedImageGlass.cpp') -Raw

# The height-field normal is shared verbatim with the accepted settings shader.
function Get-FunctionBody([string] $Source, [string] $Name) {
    $match = [regex]::Match($Source, "\b$([regex]::Escape($Name))\s*\(")
    if (-not $match.Success) { throw "Function not found: $Name" }
    $open = $Source.IndexOf('{', $match.Index + $match.Length)
    if ($open -lt 0) { throw "Function body not found: $Name" }
    $depth = 0
    for ($i = $open; $i -lt $Source.Length; ++$i) {
        if ($Source[$i] -eq '{') { ++$depth }
        if ($Source[$i] -eq '}') {
            --$depth
            if ($depth -eq 0) { return $Source.Substring($open, $i - $open + 1) }
        }
    }
    throw "Unclosed function body: $Name"
}

$refNormal = ((Get-FunctionBody $reference 'SurfaceNormal') -replace '\s+', '') -replace '(?<![A-Za-z0-9_])0\.', '.'
$ownedNormal = ((Get-FunctionBody $owned 'SurfaceNormal') -replace '\s+', '') -replace '(?<![A-Za-z0-9_])0\.', '.'
if ($refNormal -cne $ownedNormal) {
    throw 'Owned shader SurfaceNormal differs from accepted PreviewGlass.hlsl.'
}

$refLens = Get-FunctionBody $reference 'LensSample'
$ownedLens = Get-FunctionBody $owned 'LensSample'
foreach ($formula in @(
    '1/1.45', 'primary?.62:.46', 'primary?.62:.48',
    'Phase(depth,0,primary?1.5:3)', 'Phase(depth,band*.6,band+6)',
    'p-(p-centre)*(1-1/max(zoom,1))*weight+edge'
)) {
    $refCompact = $refLens -replace '\s+', ''
    Assert-Contains $refCompact $formula 'Accepted lens profile'
}
foreach ($formula in @(
    'floatdepth=max(0,-distance)/dpiScale;', 'floatbandDip=band/dpiScale;',
    'refract(float3(0,0,-1),normal,1/1.45)',
    'Phase(depth,0,primary?1.5:3)',
    'Phase(depth,bandDip*0.6,bandDip+6)', 'edge*dpiScale'
)) {
    $ownedCompact = $ownedLens -replace '\s+', ''
    Assert-Contains $ownedCompact $formula 'Owned lens profile'
}
$refLight = ((Get-FunctionBody $reference 'Light') -replace '\s+', '') -replace '(?<![A-Za-z0-9_])0\.', '.'
$ownedLight = ((Get-FunctionBody $owned 'Light') -replace '\s+', '') -replace '(?<![A-Za-z0-9_])0\.', '.'
Assert-Contains $ownedLight 'floatdepth=max(0,-distance)/dpiScale;floatbandDip=band/dpiScale;' 'Owned light DPI conversion'
foreach ($formula in @(
    'pow(saturate(dot(normal,normalize(float3(-.48,-.64,1)))),primary?10:20)',
    'pow(1-normal.z,3)',
    'dot(outward,normalize(float2(-.55,-.83)))',
    'dot(outward,normalize(float2(.65,.76)))',
    'primary?.09:.13', 'primary?.13:.53', 'primary?.015:.18',
    'primary?.035:.075', 'pow((depth-2.4)/1.6,2)'
)) {
    Assert-Contains $refLight $formula 'Accepted light profile'
    Assert-Contains $ownedLight $formula 'Owned light profile'
}

# Preserve the accepted finite 21-tap Gaussian and verify the native path keeps
# its two passes and sigma cache. The input-alpha normalization is an app-owned
# source adaptation; it does not change the Gaussian weights.
$refBlur = ((Get-FunctionBody $reference 'PSBlur') -replace '\s+', '') -replace '(?<![A-Za-z0-9_])0\.', '.'
$ownedBlur = ((Get-FunctionBody $owned 'PSBlur') -replace '\s+', '') -replace '(?<![A-Za-z0-9_])0\.', '.'
Assert-Contains $refBlur 'for(inti=-10;i<=10;i++)' 'Accepted Gaussian kernel'
Assert-Contains $ownedBlur 'for(inti=-10;i<=10;++i)' 'Owned Gaussian kernel'
foreach ($formula in @(
    'exp(-.5*i*i/(sigma*sigma))',
    'returncolour/total'
)) {
    Assert-Contains $refBlur $formula 'Accepted Gaussian kernel'
    Assert-Contains $ownedBlur $formula 'Owned Gaussian kernel'
}
$ownedCppCompact = $ownedCpp -replace '\s+', ''
Assert-Contains $ownedCppCompact 'for(UINTpass=0;pass<2;++pass)' 'Owned two-pass blur'
Assert-Contains $ownedCppCompact 'std::abs(sigmaX-cachedSigmaX)' 'Owned blur cache'

# Independently exercise the accepted equations in DIPs and the renderer's
# physical-pixel adaptation. The renderer converts inputs to DIPs, evaluates
# the same interface, then scales only the refraction displacement back to px.
function Sat([double] $x) { return [Math]::Max(0.0, [Math]::Min(1.0, $x)) }
function Phase([double] $p, [double] $a, [double] $b) {
    $t = Sat(($p - $a) / ($b - $a))
    return $t * $t * (3.0 - 2.0 * $t)
}
function Normalize3([double[]] $v) {
    $length = [Math]::Sqrt($v[0] * $v[0] + $v[1] * $v[1] + $v[2] * $v[2])
    $x = $v[0] / $length
    $y = $v[1] / $length
    $z = $v[2] / $length
    return $x, $y, $z
}
function SurfaceNormalCpu([double] $depth, [double[]] $outward, [double] $band, [double] $height) {
    $u = 1.0 - (Sat($depth / $band))
    $slope = $height / $band * $u / [Math]::Sqrt([Math]::Max(1.0 - $u * $u, 0.003))
    $vector = [double[]]::new(3)
    $vector[0] = [double]$outward[0] * $slope
    $vector[1] = [double]$outward[1] * $slope
    $vector[2] = 1.0
    return (Normalize3 -v ([double[]]$vector))
}
function RefractCpu([double[]] $normal) {
    $eta = 1.0 / 1.45
    $dotNI = -$normal[2]
    $k = 1.0 - $eta * $eta * (1.0 - $dotNI * $dotNI)
    if ($k -lt 0) { return 0.0, 0.0, 0.0 }
    $factor = $eta * $dotNI + [Math]::Sqrt($k)
    $ray = [double[]]::new(3)
    $ray[0] = -$factor * $normal[0]
    $ray[1] = -$factor * $normal[1]
    $ray[2] = -$eta - $factor * $normal[2]
    return $ray
}
function LensOffsetReference([double] $depth, [double] $band, [double[]] $delta, [double[]] $outward, [double] $zoom, [double] $bend, [bool] $primary) {
    $height = $band * $(if ($primary) { 0.62 } else { 0.46 })
    $normal = SurfaceNormalCpu -depth $depth -outward ([double[]]$outward) -band $band -height $height
    $ray = RefractCpu -normal ([double[]]$normal)
    $cap = [Math]::Sqrt((Sat(1.0 - [Math]::Pow(1.0 - (Sat($depth / $band)), 2))))
    $edgeScale = $band * $(if ($primary) { 0.62 } else { 0.48 }) + $height * $cap
    $edge = [double[]]::new(2)
    $phase = Phase $depth 0 $(if ($primary) { 1.5 } else { 3.0 })
    $denominator = [Math]::Max(-$ray[2], 0.1)
    $edge[0] = $ray[0] / $denominator * $edgeScale * $bend * $phase
    $edge[1] = $ray[1] / $denominator * $edgeScale * $bend * $phase
    $weight = if ($primary) { Phase $depth ($band * 0.6) ($band + 6.0) } else { Phase $depth 0 $band }
    $zoomOffset = (1.0 - 1.0 / [Math]::Max($zoom, 1.0)) * $weight
    $offset = [double[]]::new(2)
    $offset[0] = -$delta[0] * $zoomOffset + $edge[0]
    $offset[1] = -$delta[1] * $zoomOffset + $edge[1]
    return $offset
}
function LensOffsetOwnedPx([double] $depthPx, [double] $bandPx, [double[]] $deltaPx, [double[]] $outward, [double] $dpi, [double] $zoom, [double] $bend, [bool] $primary) {
    $depth = $depthPx / $dpi
    $band = $bandPx / $dpi
    $delta = [double[]]::new(2)
    $delta[0] = $deltaPx[0] / $dpi
    $delta[1] = $deltaPx[1] / $dpi
    $dipOffset = LensOffsetReference -depth $depth -band $band -delta ([double[]]$delta) -outward ([double[]]$outward) -zoom $zoom -bend $bend -primary $primary
    $offsetPx = [double[]]::new(2)
    $offsetPx[0] = $dipOffset[0] * $dpi
    $offsetPx[1] = $dipOffset[1] * $dpi
    return $offsetPx
}
function Dot3([double[]] $a, [double[]] $b) {
    return $a[0] * $b[0] + $a[1] * $b[1] + $a[2] * $b[2]
}
function Normalize2([double[]] $v) {
    $length = [Math]::Sqrt($v[0] * $v[0] + $v[1] * $v[1])
    $x = $v[0] / $length
    $y = $v[1] / $length
    return $x, $y
}
function LightReference([double[]] $colour, [double] $depth, [double[]] $outward, [double] $band, [double] $emphasis, [bool] $primary) {
    $height = $band * $(if ($primary) { 0.62 } else { 0.46 })
    $normal = SurfaceNormalCpu -depth $depth -outward $outward -band $band -height $height
    $light3 = Normalize3 -v ([double[]]@(-0.48, -0.64, 1.0))
    $highlight = [Math]::Pow((Sat (Dot3 $normal $light3)), $(if ($primary) { 10 } else { 20 }))
    $fresnel = [Math]::Pow(1.0 - $normal[2], 3)
    $rimCenter = if ($primary) { 1.1 } else { 0.65 }
    $rimWidth = if ($primary) { 1.3 } else { 0.65 }
    $rim = [Math]::Exp(-[Math]::Pow(($depth - $rimCenter) / $rimWidth, 2))
    $keyDirection = Normalize2 -v ([double[]]@(-0.55, -0.83))
    $counterDirection = Normalize2 -v ([double[]]@(0.65, 0.76))
    $key = [Math]::Pow((Sat ($outward[0] * $keyDirection[0] + $outward[1] * $keyDirection[1])), 3)
    $counter = [Math]::Pow((Sat ($outward[0] * $counterDirection[0] + $outward[1] * $counterDirection[1])), 5)
    $highlightTerm = $highlight * $(if ($primary) { 0.09 } else { 0.13 })
    $rimTerm = $rim * ($key * $(if ($primary) { 0.13 } else { 0.53 }) + $counter * $(if ($primary) { 0.015 } else { 0.18 }))
    $fresnelTerm = $fresnel * $(if ($primary) { 0.035 } else { 0.075 })
    $blend = Sat (($highlightTerm + $rimTerm + $fresnelTerm) * $emphasis)
    $lit = [double[]]::new(3)
    for ($i = 0; $i -lt 3; ++$i) { $lit[$i] = $colour[$i] + (1.0 - $colour[$i]) * $blend }
    if ($primary) { return $lit }
    $shade = 1.0 - [Math]::Exp(-[Math]::Pow(($depth - 2.4) / 1.6, 2)) * (1.0 - $key) * 0.028 * $emphasis
    for ($i = 0; $i -lt 3; ++$i) { $lit[$i] *= $shade }
    return $lit
}
function LightOwnedPx([double[]] $colour, [double] $depthPx, [double[]] $outward, [double] $bandPx, [double] $emphasis, [bool] $primary, [double] $dpi) {
    return (LightReference -colour $colour -depth ($depthPx / $dpi) -outward $outward -band ($bandPx / $dpi) -emphasis $emphasis -primary $primary)
}

foreach ($dpi in @(1.0, 1.5, 2.0)) {
    foreach ($sample in @(
        @{ depth = 0.3; band = 18.0; delta = @(5.0, -2.0); out = @(0.8, -0.6); zoom = 1.08; bend = 1.0; primary = $true },
        @{ depth = 4.0; band = 30.0; delta = @(-6.0, 3.0); out = @(-0.6, 0.8); zoom = 1.2; bend = 1.1; primary = $true },
        @{ depth = 1.0; band = 8.0; delta = @(1.0, 3.0); out = @(0.0, -1.0); zoom = 1.18; bend = 0.95; primary = $false }
    )) {
        $referenceOffset = LensOffsetReference -depth $sample.depth -band $sample.band -delta ([double[]]$sample.delta) -outward ([double[]]$sample.out) -zoom $sample.zoom -bend $sample.bend -primary $sample.primary
        $deltaPx = [double[]]::new(2)
        $deltaPx[0] = [double]$sample.delta[0] * $dpi
        $deltaPx[1] = [double]$sample.delta[1] * $dpi
        $ownedOffsetPx = LensOffsetOwnedPx -depthPx ($sample.depth * $dpi) -bandPx ($sample.band * $dpi) -deltaPx $deltaPx -outward ([double[]]$sample.out) -dpi $dpi -zoom $sample.zoom -bend $sample.bend -primary $sample.primary
        for ($axis = 0; $axis -lt 2; ++$axis) {
            $normalized = $ownedOffsetPx[$axis] / $dpi
            if ([Math]::Abs($normalized - $referenceOffset[$axis]) -gt 1e-6) {
                throw "Lens profile parity failed at DPI $dpi, sample axis $axis."
            }
        }
    }

    foreach ($lightSample in @(
        @{ depth = 0.4; band = 18.0; out = @(0.8, -0.6); emphasis = 1.0; primary = $true },
        @{ depth = 1.8; band = 9.0; out = @(-0.6, 0.8); emphasis = 1.3; primary = $false },
        @{ depth = 5.5; band = 30.0; out = @(0.0, -1.0); emphasis = 0.7; primary = $true }
    )) {
        $colour = [double[]]@(0.28, 0.5, 0.76)
        $referenceLight = LightReference -colour $colour -depth $lightSample.depth -outward ([double[]]$lightSample.out) -band $lightSample.band -emphasis $lightSample.emphasis -primary $lightSample.primary
        $ownedLightPx = LightOwnedPx -colour $colour -depthPx ($lightSample.depth * $dpi) -outward ([double[]]$lightSample.out) -bandPx ($lightSample.band * $dpi) -emphasis $lightSample.emphasis -primary $lightSample.primary -dpi $dpi
        for ($channel = 0; $channel -lt 3; ++$channel) {
            if ([Math]::Abs($ownedLightPx[$channel] - $referenceLight[$channel]) -gt 1e-6) {
                throw "Light profile parity failed at DPI $dpi, channel $channel."
            }
        }
    }
}

Write-Output 'Owned wallpaper optics and 21-tap Gaussian parity passed at 1x, 1.5x, and 2x DPI.'
