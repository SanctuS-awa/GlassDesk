#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <dwrite_3.h>
#include <winrt/base.h>
#include <cstdint>
#include <memory>
#include <string>
#include <vector>

// Shared by the headless P1 probe and the V2 measure/draw bridge. No HWND,
// registered font, graphics-device state, or product configuration is owned here.
// Caller initializes its COM/WinRT apartment. Returned layouts are shared and
// must be treated as read-only, including their IDWriteTextLayout interface.
namespace gd::typography
{
enum class Profile : uint32_t { SystemA=0, Baseline=1, NotoQa=2 };
enum class Optical : uint32_t { Small=0, Text=1, Display=2 };
constexpr uint32_t TabularNumbers=1, TabularDigitsOnly=2;
struct Token
{
    uint32_t role;
    char const* name;
    float size, lineHeight;
    uint32_t latinWeight, cjkWeight;
    float trackingEm;
    Optical optical;
    uint32_t flags;
};
Token const& TokenForRole(uint32_t role);
std::vector<Token> Tokens();

struct Request
{
    std::wstring text, locale;
    uint32_t role{}, fontWeight{}, cjkWeight{}, flags{};
    float fontSize{14}, lineHeight{}, baseline{}, trackingEm{};
    float width{1000}, height{64}, dpi{96};
    bool bold{}, icon{}, wrap{}, center{}, trim{true}, paragraphCenter{true};
};
struct Metrics
{
    float advance{}, widthIncludingTrailingWhitespace{}, height{}, baseline{}, lineHeight{};
    uint32_t lineCount{};
};
struct InkBounds
{
    float left{}, top{}, right{}, bottom{};
};
struct ScriptRun
{
    uint32_t start{}, length{}, isoScriptCode{}, isoScriptNumber{}, requestedWeight{};
    std::wstring locale;
};
struct SimulationFallback
{
    uint32_t start{}, length{}, requestedWeight{}, selectedRealWeight{};
    std::wstring family;
};
struct Layout
{
    winrt::com_ptr<IDWriteTextLayout> textLayout;
    Metrics metrics;
    std::wstring text, requestedFamily, resolvedLocale;
    Profile profile{};
    uint32_t revision{}, latinWeight{}, cjkWeight{}, flags{};
    float trackingEm{};
    Optical optical{};
    bool opticalAxisRequested{};
    std::vector<ScriptRun> scripts;
    std::vector<SimulationFallback> simulationFallbacks;
};
struct CacheStats { size_t formats{}, layouts{}, formatHits{}, layoutHits{}, layoutsCreated{}; };

class Engine
{
public:
    explicit Engine(IDWriteFactory* factory=nullptr);
    ~Engine();
    Engine(Engine const&)=delete;
    Engine& operator=(Engine const&)=delete;
    // NotoQa requires a private local font path. A failing load is an error, never
    // a silently labelled SystemA result. Configuration invalidates both caches.
    void Configure(Profile profile, uint32_t tokenRevision=1, std::wstring const& qaFontPath={});
    std::shared_ptr<Layout const> CreateLayout(Request const& request);
    CacheStats Stats() const;
    IDWriteFactory* Factory() const;
    std::wstring PrivateFamily() const;
private:
    struct Impl;
    std::unique_ptr<Impl> impl;
};

// Returns a layout-local visible-extents rectangle from DirectWrite's overhang
// metrics. Coordinates are relative to the layout alignment edges.
InkBounds InkBoundsForLayout(IDWriteTextLayout* layout);
}
