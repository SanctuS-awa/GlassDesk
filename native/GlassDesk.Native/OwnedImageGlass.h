#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d2d1_1.h>
#include <d3d11.h>
#include <winrt/base.h>

#include <array>
#include <cstddef>
#include <cstdint>
#include <memory>

namespace gd
{
enum class OwnedImageAlphaMode : std::uint32_t
{
    Opaque = 0,
    Straight = 1,
    Premultiplied = 2
};

struct OwnedImageGlassRect
{
    float x{};
    float y{};
    float width{};
    float height{};
};

struct OwnedImageGlassLens
{
    // Coordinates are physical pixels relative to the output image. The caller
    // supplies current button pose, rectangle, and transition opacity.
    OwnedImageGlassRect boundsPx{};
    float radiusDip{13.0f};
    float opacity{1.0f};
    // 0 selects the subtle dark control tint; 1 selects the subtle light tint.
    float lightTone{};
    float emphasis{1.0f};
};

struct OwnedImageGlassFrame
{
    // Output dimensions are physical pixels. The renderer rejects an output
    // larger than 1,000,000 pixels and does not allocate a presenter/swapchain.
    std::uint32_t widthPx{};
    std::uint32_t heightPx{};
    float dpiScaleX{1.0f};
    float dpiScaleY{1.0f};

    // The canvas origin and wallpaper image rectangle are physical desktop
    // pixels. The source rectangle describes where this complete, bounded
    // wallpaper image is displayed on the selected monitor. Callers calculate
    // this rectangle for FILL/FIT/STRETCH/CENTER; TILE/SPAN must use fallback.
    // FILL may extend beyond the monitor; FIT uses backgroundColor outside it.
    float windowOriginPxX{};
    float windowOriginPxY{};
    float imageOriginPxX{};
    float imageOriginPxY{};
    float imageDisplayWidthPx{};
    float imageDisplayHeightPx{};

    // Body bounds are physical pixels relative to the output image. Radius
    // and tension follow SurfaceTension.h. Progress is derived from body
    // height: clamp((heightDip - 52) / 124, 0, 1), for optics only.
    OwnedImageGlassRect bodyBoundsPx{};
    float radiusDip{26.0f};
    float surfaceTension{};

    float baseZoom{0.08f};
    float bend{1.0f};
    float softnessDip{0.35f};
    float tint{};
    float lightIntensity{1.0f};

    float backgroundRed{};
    float backgroundGreen{};
    float backgroundBlue{};

    bool peek{};
    bool musicControls{};
    std::uint32_t lensCount{};
    std::array<OwnedImageGlassLens, 3> lenses{};
};

class OwnedImageGlass final
{
public:
    explicit OwnedImageGlass(ID3D11Device* existingDevice);
    ~OwnedImageGlass();

    OwnedImageGlass(OwnedImageGlass&&) noexcept;
    OwnedImageGlass& operator=(OwnedImageGlass&&) noexcept;
    OwnedImageGlass(OwnedImageGlass const&) = delete;
    OwnedImageGlass& operator=(OwnedImageGlass const&) = delete;

    // Copies BGRA8 input into an app-owned texture, downsampling to at most
    // 1600 pixels on its longest edge and 1,500,000 cached pixels. Straight
    // alpha is premultiplied during the copy/resample so transparent borders
    // cannot create dark fringes. bytes must equal width * height * 4;
    // passing null with zero dimensions clears the image.
    void SetImage(
        std::uint8_t const* bgra,
        std::uint32_t width,
        std::uint32_t height,
        std::size_t bytes,
        OwnedImageAlphaMode alphaMode = OwnedImageAlphaMode::Opaque);

    // All calls are thread-affine to the construction thread. Call Render before
    // the host surface BeginDraw; never interleave a borrowed D2D draw. It returns an owned
    // BGRA8 premultiplied texture wrapped as a D2D bitmap; it never begins or
    // ends the host draw. The host clears the full update region and draws once
    // into its existing CompositionDrawingSurface. No device, context,
    // swapchain, presenter, capture path, or wallpaper file is created here.
    [[nodiscard]] winrt::com_ptr<ID2D1Bitmap1> Render(
        ID2D1DeviceContext* drawingContext,
        OwnedImageGlassFrame const& frame);

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};
} // namespace gd


