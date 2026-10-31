#pragma once
#include <d2d1effects.h>
#include <windows.graphics.effects.interop.h>
#include <winrt/Windows.Graphics.Effects.h>
#include <winrt/Windows.Foundation.h>
#include <cmath>
#include <cwchar>
// Empirical correction for the measured, hash-locked 26100 WCG Scene path.
// This is not an Apple optical constant or an HDR SDR-white adjustment.
namespace gd::systemglass::color {
using namespace winrt;
using winrt::Windows::Foundation::IPropertyValue;
using winrt::Windows::Foundation::PropertyValue;
        constexpr float kMeasuredWcgLinearGain = 1.7f;
        constexpr GUID kGammaTransferEffectId{
            0x409444c4, 0xc419, 0x41a0, {0xb0, 0xc1, 0x8c, 0xd0, 0xc0, 0xa1, 0x8e, 0x42}};

        using EffectInterop = ABI::Windows::Graphics::Effects::IGraphicsEffectD2D1Interop;

        struct GammaTransferDescription :
            implements<GammaTransferDescription,
                       winrt::Windows::Graphics::Effects::IGraphicsEffect,
                       winrt::Windows::Graphics::Effects::IGraphicsEffectSource,
                       EffectInterop>
        {
            hstring name{L"GammaTransferCorrection"};
            winrt::Windows::Graphics::Effects::IGraphicsEffectSource source{nullptr};

            hstring Name() const { return name; }
            void Name(hstring const& value) { name = value; }

            HRESULT __stdcall GetEffectId(GUID* result) noexcept override
            {
                if (!result)
                    return E_POINTER;
                *result = kGammaTransferEffectId;
                return S_OK;
            }

            HRESULT __stdcall GetNamedPropertyMapping(
                LPCWSTR propertyName,
                UINT* propertyIndex,
                ABI::Windows::Graphics::Effects::GRAPHICS_EFFECT_PROPERTY_MAPPING* mapping) noexcept override
            {
                if (!propertyName || !propertyIndex || !mapping)
                    return E_POINTER;

                struct NamedProperty
                {
                    wchar_t const* name;
                    D2D1_GAMMATRANSFER_PROP index;
                };
                static constexpr NamedProperty properties[]{
                    {L"RedAmplitude", D2D1_GAMMATRANSFER_PROP_RED_AMPLITUDE},
                    {L"RedExponent", D2D1_GAMMATRANSFER_PROP_RED_EXPONENT},
                    {L"RedOffset", D2D1_GAMMATRANSFER_PROP_RED_OFFSET},
                    {L"RedDisable", D2D1_GAMMATRANSFER_PROP_RED_DISABLE},
                    {L"GreenAmplitude", D2D1_GAMMATRANSFER_PROP_GREEN_AMPLITUDE},
                    {L"GreenExponent", D2D1_GAMMATRANSFER_PROP_GREEN_EXPONENT},
                    {L"GreenOffset", D2D1_GAMMATRANSFER_PROP_GREEN_OFFSET},
                    {L"GreenDisable", D2D1_GAMMATRANSFER_PROP_GREEN_DISABLE},
                    {L"BlueAmplitude", D2D1_GAMMATRANSFER_PROP_BLUE_AMPLITUDE},
                    {L"BlueExponent", D2D1_GAMMATRANSFER_PROP_BLUE_EXPONENT},
                    {L"BlueOffset", D2D1_GAMMATRANSFER_PROP_BLUE_OFFSET},
                    {L"BlueDisable", D2D1_GAMMATRANSFER_PROP_BLUE_DISABLE},
                    {L"AlphaAmplitude", D2D1_GAMMATRANSFER_PROP_ALPHA_AMPLITUDE},
                    {L"AlphaExponent", D2D1_GAMMATRANSFER_PROP_ALPHA_EXPONENT},
                    {L"AlphaOffset", D2D1_GAMMATRANSFER_PROP_ALPHA_OFFSET},
                    {L"AlphaDisable", D2D1_GAMMATRANSFER_PROP_ALPHA_DISABLE},
                    {L"ClampOutput", D2D1_GAMMATRANSFER_PROP_CLAMP_OUTPUT},
                };

                for (auto const& property : properties)
                {
                    if (wcscmp(propertyName, property.name) == 0)
                    {
                        *propertyIndex = static_cast<UINT>(property.index);
                        *mapping = ABI::Windows::Graphics::Effects::GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT;
                        return S_OK;
                    }
                }
                return E_INVALIDARG;
            }

            HRESULT __stdcall GetPropertyCount(UINT* result) noexcept override
            {
                if (!result)
                    return E_POINTER;
                *result = D2D1_GAMMATRANSFER_PROP_CLAMP_OUTPUT + 1u;
                return S_OK;
            }

            HRESULT __stdcall GetProperty(
                UINT propertyIndex,
                ABI::Windows::Foundation::IPropertyValue** result) noexcept override
            {
                if (!result)
                    return E_POINTER;
                *result = nullptr;

                try
                {
                    const float exponent = 1.0f / 2.4f;
                    const float amplitude = 1.055f /
                        std::pow(kMeasuredWcgLinearGain, exponent);
                    if (propertyIndex == D2D1_GAMMATRANSFER_PROP_RED_AMPLITUDE ||
                        propertyIndex == D2D1_GAMMATRANSFER_PROP_GREEN_AMPLITUDE ||
                        propertyIndex == D2D1_GAMMATRANSFER_PROP_BLUE_AMPLITUDE)
                    {
                        auto property = PropertyValue::CreateSingle(amplitude).as<IPropertyValue>();
                        *result = static_cast<ABI::Windows::Foundation::IPropertyValue*>(detach_abi(property));
                    }
                    else if (propertyIndex == D2D1_GAMMATRANSFER_PROP_RED_EXPONENT ||
                             propertyIndex == D2D1_GAMMATRANSFER_PROP_GREEN_EXPONENT ||
                             propertyIndex == D2D1_GAMMATRANSFER_PROP_BLUE_EXPONENT)
                    {
                        auto property = PropertyValue::CreateSingle(exponent).as<IPropertyValue>();
                        *result = static_cast<ABI::Windows::Foundation::IPropertyValue*>(detach_abi(property));
                    }
                    else if (propertyIndex == D2D1_GAMMATRANSFER_PROP_RED_OFFSET ||
                             propertyIndex == D2D1_GAMMATRANSFER_PROP_GREEN_OFFSET ||
                             propertyIndex == D2D1_GAMMATRANSFER_PROP_BLUE_OFFSET)
                    {
                        auto property = PropertyValue::CreateSingle(-0.055f).as<IPropertyValue>();
                        *result = static_cast<ABI::Windows::Foundation::IPropertyValue*>(detach_abi(property));
                    }
                    else if (propertyIndex == D2D1_GAMMATRANSFER_PROP_ALPHA_AMPLITUDE ||
                             propertyIndex == D2D1_GAMMATRANSFER_PROP_ALPHA_EXPONENT)
                    {
                        auto property = PropertyValue::CreateSingle(1.0f).as<IPropertyValue>();
                        *result = static_cast<ABI::Windows::Foundation::IPropertyValue*>(detach_abi(property));
                    }
                    else if (propertyIndex == D2D1_GAMMATRANSFER_PROP_ALPHA_OFFSET)
                    {
                        auto property = PropertyValue::CreateSingle(0.0f).as<IPropertyValue>();
                        *result = static_cast<ABI::Windows::Foundation::IPropertyValue*>(detach_abi(property));
                    }
                    else if (propertyIndex == D2D1_GAMMATRANSFER_PROP_RED_DISABLE ||
                             propertyIndex == D2D1_GAMMATRANSFER_PROP_GREEN_DISABLE ||
                             propertyIndex == D2D1_GAMMATRANSFER_PROP_BLUE_DISABLE)
                    {
                        auto property = PropertyValue::CreateBoolean(false).as<IPropertyValue>();
                        *result = static_cast<ABI::Windows::Foundation::IPropertyValue*>(detach_abi(property));
                    }
                    else if (propertyIndex == D2D1_GAMMATRANSFER_PROP_ALPHA_DISABLE ||
                             propertyIndex == D2D1_GAMMATRANSFER_PROP_CLAMP_OUTPUT)
                    {
                        auto property = PropertyValue::CreateBoolean(true).as<IPropertyValue>();
                        *result = static_cast<ABI::Windows::Foundation::IPropertyValue*>(detach_abi(property));
                    }
                    else
                    {
                        return E_INVALIDARG;
                    }
                    return S_OK;
                }
                catch (...)
                {
                    return to_hresult();
                }
            }

            HRESULT __stdcall GetSource(
                UINT sourceIndex,
                ABI::Windows::Graphics::Effects::IGraphicsEffectSource** result) noexcept override
            {
                if (!result)
                    return E_POINTER;
                *result = nullptr;
                if (sourceIndex != 0 || !source)
                    return E_INVALIDARG;
                auto copy = source;
                *result = static_cast<ABI::Windows::Graphics::Effects::IGraphicsEffectSource*>(detach_abi(copy));
                return S_OK;
            }

            HRESULT __stdcall GetSourceCount(UINT* result) noexcept override
            {
                if (!result)
                    return E_POINTER;
                *result = 1;
                return S_OK;
            }
        };

}
