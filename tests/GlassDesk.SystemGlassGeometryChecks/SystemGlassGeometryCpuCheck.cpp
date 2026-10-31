#include <SystemGlassGeometry.h>

#include <cmath>
#include <cstdio>
#include <limits>

namespace
{
    using gd::systemglass::BuildStatus;
    using gd::systemglass::Float2;
    using gd::systemglass::Frame;
    using gd::systemglass::SceneMesh;

    bool Check(bool condition, char const* name)
    {
        std::printf("%s: %s\n", condition ? "PASS" : "FAIL", name);
        return condition;
    }

    Frame SymmetryFrame()
    {
        Frame frame{};
        frame.widthDips = 320.0f;
        frame.heightDips = 80.0f;
        frame.radiusDips = 28.0f;
        frame.dpiScale = 1.25f;
        frame.strain = 0.0f;
        frame.progress = 0.5f;
        frame.sourceCanvasWidthPx = 800.0f;
        frame.sourceCanvasHeightPx = 400.0f;
        frame.bodySourceXPx = 100.0f;
        frame.bodySourceYPx = 50.0f;
        return frame;
    }

    gd::systemglass::LensRegion ControlLens(float centerX)
    {
        return { centerX, 40.0f, 42.0f, 42.0f, 13.0f, 1.0f, 0.5f, 0.5f };
    }

    bool FiniteAndBounded(SceneMesh const& mesh)
    {
        if (mesh.positions.empty() || mesh.positions.size() != mesh.normals.size() ||
            mesh.positions.size() != mesh.uvs.size() || mesh.positions.size() > 65000u ||
            mesh.indices.size() > 400000u || mesh.indices.size() % 3u != 0u)
            return false;

        for (std::size_t i = 0; i < mesh.positions.size(); ++i)
        {
            const auto p = mesh.positions[i];
            const auto n = mesh.normals[i];
            const Float2 uv = mesh.uvs[i];
            if (!std::isfinite(p.x) || !std::isfinite(p.y) || !std::isfinite(p.z) ||
                !std::isfinite(n.x) || !std::isfinite(n.y) || !std::isfinite(n.z) ||
                !std::isfinite(uv.x) || !std::isfinite(uv.y) ||
                p.z != 0.0f || n.x != 0.0f || n.y != 0.0f || n.z != -1.0f ||
                uv.x < 0.0f || uv.x > 1.0f || uv.y < 0.0f || uv.y > 1.0f)
                return false;
        }
        for (std::uint16_t index : mesh.indices)
        {
            if (static_cast<std::size_t>(index) >= mesh.positions.size())
                return false;
        }
        return std::isfinite(mesh.zoom) && std::isfinite(mesh.bandPx) &&
               mesh.bend == 1.0f && mesh.ior == 1.45f;
    }

    bool SymmetricUv(SceneMesh const& mesh, Frame const& frame)
    {
        const float centerU = (frame.bodySourceXPx + frame.widthDips * frame.dpiScale * 0.5f) /
                              frame.sourceCanvasWidthPx;
        const float centerV = (frame.bodySourceYPx + frame.heightDips * frame.dpiScale * 0.5f) /
                              frame.sourceCanvasHeightPx;
        const float tolerance = 0.00002f;
        for (std::uint32_t row = 0; row <= mesh.rows; ++row)
        {
            for (std::uint32_t column = 0; column <= mesh.columns; ++column)
            {
                const Float2 left = mesh.uvs[static_cast<std::size_t>(row) * (mesh.columns + 1u) + column];
                const Float2 right = mesh.uvs[static_cast<std::size_t>(row) * (mesh.columns + 1u) +
                                              (mesh.columns - column)];
                if (std::abs((left.x + right.x) - 2.0f * centerU) > tolerance)
                    return false;

                const Float2 top = mesh.uvs[static_cast<std::size_t>(row) * (mesh.columns + 1u) + column];
                const Float2 bottom = mesh.uvs[static_cast<std::size_t>(mesh.rows - row) *
                                               (mesh.columns + 1u) + column];
                if (std::abs((top.y + bottom.y) - 2.0f * centerV) > tolerance)
                    return false;
            }
        }
        return true;
    }
}

int main()
{
    using gd::systemglass::BuildSceneMesh;

    int failures = 0;
    SceneMesh mesh;
    Frame frame = SymmetryFrame();
    failures += !Check(gd::systemglass::SameOpticalFrame(frame, frame),
                       "unchanged optical frame matches the compositor frame cache");
    Frame sizeChanged = frame;
    sizeChanged.widthDips += 1.0f;
    Frame sourceChanged = frame;
    sourceChanged.bodySourceXPx += 1.0f;
    Frame dpiChanged = frame;
    dpiChanged.dpiScale += 0.25f;
    Frame styleChanged = frame;
    styleChanged.strain += 0.1f;
    styleChanged.lensCount = 1u;
    styleChanged.lenses[0] = ControlLens(80.0f);
    Frame lensStyleChanged = styleChanged;
    lensStyleChanged.lenses[0].emphasis += 0.1f;
    failures += !Check(!gd::systemglass::SameOpticalFrame(frame, sizeChanged) &&
                       !gd::systemglass::SameOpticalFrame(frame, sourceChanged) &&
                       !gd::systemglass::SameOpticalFrame(frame, dpiChanged) &&
                       !gd::systemglass::SameOpticalFrame(frame, styleChanged) &&
                       !gd::systemglass::SameOpticalFrame(styleChanged, lensStyleChanged),
                       "size, source origin, DPI, contour, and lens style changes invalidate the frame cache");
    Frame toneOnly = styleChanged;
    toneOnly.lenses[0].lightInk = 1.0f;
    failures += !Check(!gd::systemglass::SameOpticalFrame(styleChanged, toneOnly),
                       "light-ink-only lens changes invalidate the frame cache");
    Frame unusedSlotChanged = styleChanged;
    unusedSlotChanged.lenses[2] = { 17.0f, 23.0f, 11.0f, 13.0f, 5.0f, 0.25f, 0.75f, 1.0f };
    failures += !Check(gd::systemglass::SameOpticalFrame(styleChanged, unusedSlotChanged),
                       "unused fixed-capacity lens slots do not invalidate the frame cache");
    Frame invalidLensCount = styleChanged;
    invalidLensCount.lensCount = static_cast<std::uint32_t>(invalidLensCount.lenses.size() + 1u);
    failures += !Check(!gd::systemglass::SameOpticalFrame(invalidLensCount, invalidLensCount),
                       "invalid lens counts are never accepted as cached optical frames");

    const BuildStatus validStatus = BuildSceneMesh(frame, mesh);
    failures += !Check(validStatus == BuildStatus::success, "valid frame builds");
    failures += !Check(FiniteAndBounded(mesh), "positions, normals, UVs, and indices are finite and bounded");
    failures += !Check(std::abs(mesh.zoom - 1.0f) < 0.000001f &&
                       mesh.bandPx >= 18.0f * frame.dpiScale &&
                       mesh.bandPx <= 36.0f * frame.dpiScale,
                       "screen-aligned interior and DPI-scaled optical band");
    failures += !Check(SymmetricUv(mesh, frame), "zero-strain contour and UVs preserve horizontal and vertical symmetry");

    // A point in the flat center must sample the same physical screen pixel,
    // not a point shifted toward the widget center by global magnification.
    for (const auto size : { Float2{200.0f, 52.0f}, Float2{320.0f, 176.0f}, Float2{360.0f, 476.0f} })
    {
        Frame aligned = frame;
        aligned.widthDips = size.x; aligned.heightDips = size.y;
        aligned.radiusDips = std::min(26.0f, size.y * .5f);
        aligned.sourceCanvasWidthPx = 800.0f; aligned.sourceCanvasHeightPx = 800.0f;
        SceneMesh alignedMesh;
        Float2 sample{}, uv{};
        const float x = size.x * .5f + 20.0f, y = size.y * .5f;
        const bool valid = BuildSceneMesh(aligned, alignedMesh) == BuildStatus::success &&
            gd::systemglass::detail::RefractedSample(aligned, alignedMesh, aligned.radiusDips, x, y, sample) &&
            gd::systemglass::detail::FullSourceUv(aligned, sample, uv);
        failures += !Check(valid && std::abs(uv.x - (aligned.bodySourceXPx + x * aligned.dpiScale) / aligned.sourceCanvasWidthPx) < .000001f &&
                          std::abs(uv.y - (aligned.bodySourceYPx + y * aligned.dpiScale) / aligned.sourceCanvasHeightPx) < .000001f,
                          "capsule, peek or details interior preserves screen coordinates");
    }

    const std::size_t lensEdge = static_cast<std::size_t>(40u) * (mesh.columns + 1u) + 95u;
    const Float2 baselineLensUv = mesh.uvs[lensEdge];
    const auto* cachedPositions = mesh.positions.data();
    const auto* cachedIndices = mesh.indices.data();
    const auto cachedPosition = mesh.positions[0];
    const std::uint16_t cachedIndex = mesh.indices[0];
    Frame singleLens = frame;
    singleLens.lensCount = 1u;
    singleLens.lenses[0] = ControlLens(80.0f);
    failures += !Check(BuildSceneMesh(singleLens, mesh) == BuildStatus::success &&
                       FiniteAndBounded(mesh), "one rounded control lens builds on the primary mesh");
    Float2 lensPoint{};
    Float2 primaryPoint{};
    Float2 expectedLensUv{};
    const float primaryRadius = gd::SurfaceRadius(frame.widthDips, frame.heightDips,
                                                  frame.radiusDips, frame.strain);
    const bool expectedLensMapping =
        gd::systemglass::detail::SecondaryLensSample(singleLens, singleLens.lenses[0], 95.0f, 40.0f, lensPoint) &&
        gd::systemglass::detail::RefractedSample(singleLens, mesh, primaryRadius,
                                                 lensPoint.x, lensPoint.y, primaryPoint) &&
        gd::systemglass::detail::FullSourceUv(singleLens, primaryPoint, expectedLensUv);
    failures += !Check(std::abs(mesh.uvs[lensEdge].x -
                       ((frame.bodySourceXPx + 95.0f * frame.dpiScale) / frame.sourceCanvasWidthPx)) > 0.000001f,
                       "secondary round lens changes its local UV mapping");
    failures += !Check(expectedLensMapping &&
                       std::abs(mesh.uvs[lensEdge].x - expectedLensUv.x) < 0.000001f &&
                       std::abs(mesh.uvs[lensEdge].y - expectedLensUv.y) < 0.000001f,
                       "secondary lens sample is remapped through the primary body contour");
    failures += !Check(mesh.positions.data() == cachedPositions && mesh.indices.data() == cachedIndices &&
                       mesh.positions[0].x == cachedPosition.x && mesh.positions[0].y == cachedPosition.y &&
                       mesh.indices[0] == cachedIndex,
                       "lens animation reuses cached positions and indices");

    Frame transparentLens = frame;
    transparentLens.lensCount = 1u;
    transparentLens.lenses[0] = ControlLens(80.0f);
    transparentLens.lenses[0].opacity = 0.0f;
    SceneMesh transparentMesh;
    failures += !Check(BuildSceneMesh(transparentLens, transparentMesh) == BuildStatus::success &&
                       std::abs(transparentMesh.uvs[lensEdge].x - baselineLensUv.x) < 0.000001f &&
                       std::abs(transparentMesh.uvs[lensEdge].y - baselineLensUv.y) < 0.000001f,
                       "zero-opacity lens leaves primary UVs unchanged");

    Frame threeLenses = frame;
    threeLenses.lensCount = 3u;
    threeLenses.lenses[0] = ControlLens(80.0f);
    threeLenses.lenses[1] = ControlLens(160.0f);
    threeLenses.lenses[2] = ControlLens(240.0f);
    SceneMesh threeLensMesh;
    failures += !Check(BuildSceneMesh(threeLenses, threeLensMesh) == BuildStatus::success &&
                       FiniteAndBounded(threeLensMesh), "three rounded control lenses stay within one mesh budget");
    Frame dock = frame;
    dock.lensCount = 2u;
    dock.lenses[0] = {132.0f, 40.0f, 224.0f, 56.0f, 28.0f, 1.0f, 0.0f, 0.5f};
    dock.lenses[1] = {272.0f, 40.0f, 56.0f, 56.0f, 28.0f, 1.0f, 0.0f, 0.5f};
    auto tooWideDock = dock.lenses[0];
    tooWideDock.widthDips = gd::systemglass::kMaxSystemLensWidthDips + 1.0f;
    failures += !Check(gd::systemglass::ValidSystemLensInput(dock.lenses[0]) &&
                       gd::systemglass::ValidSystemLensInput(dock.lenses[1]) &&
                       !gd::systemglass::ValidSystemLensInput(tooWideDock),
                       "native lens entry accepts Dock and rejects width over shared limit");
    SceneMesh dockMesh;
    failures += !Check(BuildSceneMesh(dock, dockMesh) == BuildStatus::success &&
                       FiniteAndBounded(dockMesh), "wide dock pill and separate circle build on one bounded mesh");
    Frame navigation = dock;
    navigation.widthDips = 360.0f;
    navigation.heightDips = 512.0f;
    navigation.sourceCanvasHeightPx = 900.0f;
    navigation.lensCount = 3u;
    navigation.lenses[0] = {148.0f, 468.0f, 224.0f, 56.0f, 28.0f, 1.0f, 0.0f, 0.5f};
    navigation.lenses[1] = {312.0f, 468.0f, 56.0f, 56.0f, 28.0f, 1.0f, 0.0f, 0.5f};
    navigation.lenses[2] = {312.0f, 300.0f, 48.0f, 264.0f, 24.0f, 1.0f, 0.0f, 0.5f};
    SceneMesh navigationMesh;
    auto tooTall = navigation.lenses[2];
    tooTall.heightDips = gd::systemglass::kMaxSystemLensHeightDips + 1.0f;
    failures += !Check(gd::systemglass::ValidSystemLensInput(navigation.lenses[2]) &&
                       !gd::systemglass::ValidSystemLensInput(tooTall) &&
                       BuildSceneMesh(navigation, navigationMesh) == BuildStatus::success &&
                       FiniteAndBounded(navigationMesh),
                       "upright navigation, dock and trigger share one bounded mesh");
    // Sample the actual rounded corner along radial normals. The glass should
    // pull the source inward at the rim without reversing the source order.
    bool cornersValid = true;
    for (float dpi : {1.0f, 1.5f, 2.0f})
    {
        Frame corner = frame;
        corner.dpiScale = dpi;
        corner.sourceCanvasWidthPx = 900.0f;
        corner.sourceCanvasHeightPx = 600.0f;
        SceneMesh cornerMesh;
        const auto cornerStatus = BuildSceneMesh(corner, cornerMesh);
        if (cornerStatus != BuildStatus::success)
        { std::printf("corner mesh failed dpi=%.2f status=%d\n", dpi, static_cast<int>(cornerStatus)); cornersValid = false; continue; }
        const float r = gd::SurfaceRadius(corner.widthDips, corner.heightDips, corner.radiusDips, corner.strain);
        const float band = cornerMesh.bandPx / dpi;
        for (float angle : {0.2617994f, 0.7853982f, 1.3089970f})
        {
            const float nx = std::cos(angle), ny = -std::sin(angle);
            const float cx = corner.widthDips - r, cy = r;
            float previousDepth = -1.0f;
            for (int i = 0; i <= 40; ++i)
            {
                const float depth = band * static_cast<float>(i) / 40.0f;
                const float x = cx + (r - depth) * nx, y = cy + (r - depth) * ny;
                Float2 sample{};
                if (!gd::systemglass::detail::RefractedSample(corner, cornerMesh, r, x, y, sample))
                { cornersValid = false; break; }
                const float mappedDepth = r - ((sample.x - cx) * nx + (sample.y - cy) * ny);
                if (!std::isfinite(mappedDepth) || mappedDepth < depth - .02f ||
                    (i > 0 && mappedDepth + .02f < previousDepth) ||
                    (i == 0 && mappedDepth < band * .25f))
                {
                    std::printf("corner diagnostic dpi=%.2f angle=%.3f i=%d depth=%.3f mapped=%.3f previous=%.3f band=%.3f r=%.3f\n", dpi, angle, i, depth, mappedDepth, previousDepth, band, r);
                    cornersValid = false;
                }
                previousDepth = mappedDepth;
            }
        }
    }
    failures += !Check(cornersValid, "corner refraction remains inward and preserves radial source order across DPI and angles");
    // Captured from the user's coordinate-only diagnostics: the source begins
    // at screen Y=0 while the widget begins at Y=36 physical pixels. This
    // verifies that UV selection accounts for the offset exactly once.
    Frame observed = frame;
    observed.heightDips = 176.0f;
    observed.dpiScale = 1.5f;
    observed.bodySourceXPx = 77.0f;
    observed.bodySourceYPx = 36.0f;
    observed.sourceCanvasWidthPx = 600.0f;
    observed.sourceCanvasHeightPx = 768.0f;
    SceneMesh observedMesh;
    Float2 alignedSample{}, alignedUv{};
    const float observedRadius = gd::SurfaceRadius(observed.widthDips, observed.heightDips,
                                                    observed.radiusDips, observed.strain);
    const bool observedAligned = BuildSceneMesh(observed, observedMesh) == BuildStatus::success &&
        gd::systemglass::detail::RefractedSample(observed, observedMesh, observedRadius, 160.0f, 88.0f, alignedSample) &&
        gd::systemglass::detail::FullSourceUv(observed, alignedSample, alignedUv) &&
        std::abs(alignedUv.y * observed.sourceCanvasHeightPx - (36.0f + 88.0f * 1.5f)) < .01f;
    failures += !Check(observedAligned, "36-pixel observed source offset enters center UV exactly once");
    const auto roundedControl = ControlLens(80.0f);
    failures += !Check(std::abs(gd::systemglass::detail::LensDistance(roundedControl, 80.0f, 19.0f)) < 0.000001f &&
                       gd::systemglass::detail::LensDistance(roundedControl, 100.0f, 50.0f) < 0.0f,
                       "42 by 42 DIP radius-13 lens is a rounded rectangle, not a circle");

    const std::size_t nearTopCenter = static_cast<std::size_t>(8u) * (mesh.columns + 1u) + mesh.columns / 2u;
    const float unwarpedNearTopV = (frame.bodySourceYPx + 8.0f * frame.dpiScale) /
                                   frame.sourceCanvasHeightPx;
    failures += !Check(mesh.uvs[nearTopCenter].y > unwarpedNearTopV,
                       "edge refraction samples inward from the rounded top contour");

    Frame strained = frame;
    strained.strain = 1.15f;
    SceneMesh positiveStrain;
    failures += !Check(BuildSceneMesh(strained, positiveStrain) == BuildStatus::success &&
                       FiniteAndBounded(positiveStrain), "maximum positive strain remains finite");
    strained.strain = -1.15f;
    SceneMesh negativeStrain;
    failures += !Check(BuildSceneMesh(strained, negativeStrain) == BuildStatus::success &&
                       FiniteAndBounded(negativeStrain), "maximum negative strain remains finite");

    Frame clamped = frame;
    clamped.sourceCanvasWidthPx = 400.0f;
    clamped.sourceCanvasHeightPx = 100.0f;
    clamped.bodySourceXPx = 0.0f;
    clamped.bodySourceYPx = 0.0f;
    SceneMesh edgeMesh;
    const bool edgeBuilt = BuildSceneMesh(clamped, edgeMesh) == BuildStatus::success;
    Float2 outsideUv{};
    const bool outsideClamped = gd::systemglass::detail::FullSourceUv(clamped, { 400.0f, -10.0f }, outsideUv);
    failures += !Check(edgeBuilt && FiniteAndBounded(edgeMesh) && outsideClamped && outsideUv.x == 1.0f && outsideUv.y == 0.0f,
                       "UV clamps samples outside full-source edges");

    Frame invalid = frame;
    invalid.widthDips = std::numeric_limits<float>::quiet_NaN();
    failures += !Check(BuildSceneMesh(invalid, mesh) == BuildStatus::invalidFrame && mesh.positions.empty(),
                       "non-finite frame rejected and output cleared");
    invalid = frame;
    invalid.bodySourceXPx = 1.0f;
    invalid.sourceCanvasWidthPx = 400.0f;
    failures += !Check(BuildSceneMesh(invalid, mesh) == BuildStatus::invalidFrame,
                       "source body extent outside canvas rejected");
    invalid = frame;
    invalid.lensCount = 4u;
    failures += !Check(BuildSceneMesh(invalid, mesh) == BuildStatus::invalidFrame,
                       "more than three lens regions rejected");
    invalid = frame;
    invalid.lensCount = 1u;
    invalid.lenses[0] = ControlLens(5.0f);
    failures += !Check(BuildSceneMesh(invalid, mesh) == BuildStatus::invalidFrame,
                       "lens rectangle outside primary body rejected");
    invalid = frame;
    invalid.lensCount = 1u;
    invalid.lenses[0] = ControlLens(80.0f);
    invalid.lenses[0].emphasis = std::numeric_limits<float>::quiet_NaN();
    failures += !Check(BuildSceneMesh(invalid, mesh) == BuildStatus::invalidFrame,
                       "non-finite lens controls rejected");

    Frame maximum{};
    maximum.widthDips = 1024.0f;
    maximum.heightDips = 1024.0f;
    maximum.radiusDips = 512.0f;
    maximum.dpiScale = 0.5f;
    maximum.strain = 1.15f;
    maximum.progress = 1.0f;
    maximum.sourceCanvasWidthPx = 1024.0f;
    maximum.sourceCanvasHeightPx = 1024.0f;
    SceneMesh adaptive;
    const BuildStatus adaptiveStatus = BuildSceneMesh(maximum, adaptive);
    failures += !Check(adaptiveStatus == BuildStatus::success && adaptive.gridStep > 1u &&
                       FiniteAndBounded(adaptive), "adaptive grid stays within GPU vertex and index limits");

    std::printf("System glass geometry CPU checks: %s (%d failures)\n",
                failures == 0 ? "PASS" : "FAIL", failures);
    return failures == 0 ? 0 : 1;
}
