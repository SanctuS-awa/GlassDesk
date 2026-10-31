#pragma once
#include "NativeApi.h"

// Additive ABI. V1 creation keeps the original backend. Candidate choice occurs
// BEFORE any Composition backdrop or graphics device is constructed.
struct GDHostCreationOptions
{
    uint32_t size, version, backend, reserved; // reserved bit 0: explicitly authorized 60s incident-host preview test.
    uint64_t sessionLow, sessionHigh;
};
struct GDOwnedGlassImageMapping
{
    uint32_t size, version;
    float imageX, imageY, displayedWidth, displayedHeight;
    int32_t monitorX, monitorY, monitorWidth, monitorHeight;
    uint32_t backgroundArgb, reserved;
};
struct GDOwnedGlassLens
{
    float centerX, centerY, width, height, radius, opacity, emphasis, lightInk;
};
static_assert(sizeof(GDHostCreationOptions)==32);
static_assert(sizeof(GDOwnedGlassImageMapping)==48);
static_assert(sizeof(GDOwnedGlassLens)==32);
extern "C" __declspec(dllexport) HRESULT __stdcall GDCreateV2(GDCallback callback,void* user,GDHostCreationOptions const* options,void** context,HWND* hwnd);
extern "C" __declspec(dllexport) HRESULT __stdcall GDSetOwnedGlassImage(void* context,uint8_t const* pixels,uint32_t width,uint32_t height,uint32_t bytes,GDOwnedGlassImageMapping const* mapping);
extern "C" __declspec(dllexport) HRESULT __stdcall GDPresentOwnedGlass(void* context,int eligible,GDOwnedGlassLens const* lenses,uint32_t count,uint64_t sequence);
extern "C" __declspec(dllexport) HRESULT __stdcall GDTripOwnedGlass(void* context);

// Geometry-only secondary optics; never accepts background buffers.
extern "C" __declspec(dllexport) HRESULT __stdcall GDSetSystemGlassLenses(void* context,GDOwnedGlassLens const* lenses,uint32_t count);
