#pragma once

#include <windows.h>
#include <d2d1effects.h>
#include <d2d1effects_2.h>
#include <windows.graphics.effects.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Foundation.Numerics.h>
#include <winrt/Windows.Graphics.Effects.h>
#include <winrt/Windows.UI.Composition.h>
#include <cmath>
#include <cwchar>
#include <utility>
#include <vector>

namespace gd::systemglass::ink {
using namespace winrt;
using namespace winrt::Windows::Foundation;
using namespace winrt::Windows::Graphics::Effects;
using namespace winrt::Windows::UI::Composition;

using EffectInterop = ABI::Windows::Graphics::Effects::IGraphicsEffectD2D1Interop;

// r18/m3 failed while entering this new extension. The existing optical
// SceneMesh pipeline is independent. Keep this extension unreachable until
// the triggering fault is identified; CPU descriptor checks alone cannot
// clear the quarantine. Preserve every consumed session's fuse/evidence.
inline constexpr bool kAdaptiveInkQuarantined = true;

// Read-only native diagnostic reported through GDGetSystemGlassInkStage.
// Values intentionally remain numeric ABI data so diagnostics never depend
// on parsing an exception string.
enum class InkStage : uint32_t {
    None = 0,
    BeforeEnsureFactory = 1,
    CreatingEffectFactory = 2,
    EffectFactoryCreated = 3,
    CreatingWhiteSource = 4,
    FactoryReady = 5,
    CreatingEffectBrush = 10,
    EffectBrushCreated = 11,
    CreatingDesktopBrush = 12,
    DesktopBrushCreated = 13,
    CreatingMaskBrush = 14,
    MaskBrushCreated = 15,
    SettingDesktopSource = 20,
    DesktopSourceSet = 21,
    SettingWhiteSource = 22,
    WhiteSourceSet = 23,
    SettingMaskSource = 24,
    MaskSourceSet = 25,
    SettingTintProperty = 30,
    MappingFrame = 31,
    SettingBlurProperty = 32,
    RegisteringBinding = 33,
    Success = 40,
};

// A small public IGraphicsEffect description adapter. The Composition effect
// runtime compiles these built-in D2D effects; this app does not provide a
// private shader or a CPU image path.
struct EffectDescription : implements<EffectDescription,
    IGraphicsEffect, IGraphicsEffectSource, EffectInterop> {
    GUID effectId{};
    hstring name;
    std::vector<hstring> propertyNames;
    std::vector<IPropertyValue> properties;
    std::vector<IGraphicsEffectSource> sources;

    hstring Name() const { return name; }
    void Name(hstring const& value) { name = value; }

    HRESULT __stdcall GetEffectId(GUID* result) noexcept override {
        if (!result) return E_POINTER;
        *result = effectId;
        return S_OK;
    }

    HRESULT __stdcall GetNamedPropertyMapping(LPCWSTR propertyName,
        UINT* propertyIndex,
        ABI::Windows::Graphics::Effects::GRAPHICS_EFFECT_PROPERTY_MAPPING* mapping) noexcept override {
        if (!propertyName || !propertyIndex || !mapping) return E_POINTER;
        for (UINT index = 0; index < propertyNames.size(); ++index) {
            if (wcscmp(propertyName, propertyNames[index].c_str()) == 0) {
                *propertyIndex = index;
                *mapping = ABI::Windows::Graphics::Effects::GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT;
                return S_OK;
            }
        }
        return E_INVALIDARG;
    }

    HRESULT __stdcall GetPropertyCount(UINT* result) noexcept override {
        if (!result) return E_POINTER;
        *result = static_cast<UINT>(properties.size());
        return S_OK;
    }

    HRESULT __stdcall GetProperty(UINT propertyIndex,
        ABI::Windows::Foundation::IPropertyValue** result) noexcept override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (propertyIndex >= properties.size()) return E_INVALIDARG;
        auto property = properties[propertyIndex];
        *result = static_cast<ABI::Windows::Foundation::IPropertyValue*>(detach_abi(property));
        return S_OK;
    }

    HRESULT __stdcall GetSource(UINT sourceIndex,
        ABI::Windows::Graphics::Effects::IGraphicsEffectSource** result) noexcept override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (sourceIndex >= sources.size()) return E_INVALIDARG;
        auto source = sources[sourceIndex];
        *result = static_cast<ABI::Windows::Graphics::Effects::IGraphicsEffectSource*>(detach_abi(source));
        return S_OK;
    }

    HRESULT __stdcall GetSourceCount(UINT* result) noexcept override {
        if (!result) return E_POINTER;
        *result = static_cast<UINT>(sources.size());
        return S_OK;
    }
};

struct Binding final {
    weak_ref<CompositionEffectBrush> brush;
    CompositionSurfaceBrush desktop{ nullptr };
    CompositionSurfaceBrush glyphMask{ nullptr };
    float x{}, y{}, width{}, height{};
};

inline bool ValidRequestRect(float x,float y,float width,float height) noexcept {
    if(!std::isfinite(x)||!std::isfinite(y)||!std::isfinite(width)||!std::isfinite(height)||
        x < -1024.0f||x > 1024.0f||y < -1024.0f||y > 1024.0f||
        width <= 0.0f||height <= 0.0f||width > 512.0f||height > 512.0f)
        return false;
    const float right=x+width,bottom=y+height;
    return std::isfinite(right)&&std::isfinite(bottom)&&
        std::abs(right)<=2048.0f&&std::abs(bottom)<=2048.0f;
}

inline IPropertyValue Single(float value) {
    return PropertyValue::CreateSingle(value).as<IPropertyValue>();
}

inline IPropertyValue Boolean(bool value) {
    return PropertyValue::CreateBoolean(value).as<IPropertyValue>();
}

inline IPropertyValue SingleArray(float const* values, uint32_t count) {
    return PropertyValue::CreateSingleArray(array_view<float const>(values, values + count)).as<IPropertyValue>();
}

inline IPropertyValue UInt32(uint32_t value) {
    return PropertyValue::CreateUInt32(value).as<IPropertyValue>();
}

inline com_ptr<EffectDescription> MakeDescription(GUID const& id, hstring const& name,
    std::vector<hstring> propertyNames, std::vector<IPropertyValue> properties,
    std::vector<IGraphicsEffectSource> sources) {
    auto result = make_self<EffectDescription>();
    result->effectId = id;
    result->name = name;
    result->propertyNames = std::move(propertyNames);
    result->properties = std::move(properties);
    result->sources = std::move(sources);
    return result;
}

inline std::vector<IGraphicsEffectSource> OneSource(IGraphicsEffectSource const& source) {
    return { source };
}

inline std::vector<IGraphicsEffectSource> TwoSources(IGraphicsEffectSource const& first,
    IGraphicsEffectSource const& second) {
    return { first, second };
}

struct EffectGraph final {
    IGraphicsEffect root{ nullptr };
    std::vector<hstring> animatableProperties;
};

// Build the exact graph description consumed by the live Composition path.
// Kept independent of Compositor/SurfaceBrush so CPU-only checks can inspect
// the same property contracts without creating a graphics device or compiling
// a Composition effect factory.
inline EffectGraph MakeInkGraph(IGraphicsEffectSource const& desktop,
    IGraphicsEffectSource const& white, IGraphicsEffectSource const& mask) {
    auto blur=MakeDescription(CLSID_D2D1GaussianBlur,L"InkBlur",
        {L"BlurAmount",L"Optimization",L"BorderMode"},
        {Single(3.0f),UInt32(D2D1_GAUSSIANBLUR_OPTIMIZATION_BALANCED),
            UInt32(D2D1_BORDER_MODE_SOFT)},OneSource(desktop));

    const float initialCoefficients[]{0.0f,1.0f,0.0f,0.0f};
    auto tintBlend=MakeDescription(CLSID_D2D1ArithmeticComposite,L"InkTint",
        {L"Coefficients",L"ClampOutput"},
        {SingleArray(initialCoefficients,4),Boolean(false)},
        TwoSources(blur.as<IGraphicsEffectSource>(),white));

    constexpr float redWeight=0.2126f,greenWeight=0.7152f,blueWeight=0.0722f;
    constexpr float slope=1.0f/0.03f;
    constexpr float offset=0.5f+0.46f*slope;
    const float matrix[]{
        -slope*redWeight,-slope*redWeight,-slope*redWeight,0.0f,
        -slope*greenWeight,-slope*greenWeight,-slope*greenWeight,0.0f,
        -slope*blueWeight,-slope*blueWeight,-slope*blueWeight,0.0f,
        0.0f,0.0f,0.0f,1.0f,
        offset,offset,offset,0.0f};
    auto threshold=MakeDescription(CLSID_D2D1ColorMatrix,L"InkThreshold",
        {L"ColorMatrix",L"AlphaMode",L"ClampOutput"},
        {SingleArray(matrix,20),UInt32(D2D1_COLORMATRIX_ALPHA_MODE_PREMULTIPLIED),Boolean(true)},
        OneSource(tintBlend.as<IGraphicsEffectSource>()));

    auto glyph=MakeDescription(CLSID_D2D1AlphaMask,L"InkGlyphMask",{}, {},
        TwoSources(threshold.as<IGraphicsEffectSource>(),mask));

    EffectGraph result;
    result.root=glyph.as<IGraphicsEffect>();
    result.animatableProperties.emplace_back(L"InkBlur.BlurAmount");
    result.animatableProperties.emplace_back(L"InkTint.Coefficients");
    return result;
}
}
