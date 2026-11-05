#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <ole2.h>
#include <Accessibility.h>
#include <cstdio>

int main() {
    auto state=std::make_shared<gd::AccessibleState>();
    // A non-null identity marker only. No API receives it as a real window.
    state->hwnd=reinterpret_cast<HWND>(uintptr_t{1});state->visible=true;
    state->items.push_back({7,L"range",0,0,10,10,true,true,5,0,10});
    auto root=gd::MakeElement(state,0,true);
    auto child=gd::MakeElement(state,7,false);
    state->root=root.Get();
    bool identityLive=false,inputsBlocked=false,called=false;
    HRESULT hr=gd::DisconnectAccessibleProvider(state,root.Get(),[&](IRawElementProviderSimple*) {
        called=true;
        SAFEARRAY* identity=nullptr;
        identityLive=root->GetRuntimeId(&identity)==S_OK;
        if(identity)SafeArrayDestroy(identity);
        inputsBlocked=child->Invoke()==UIA_E_ELEMENTNOTAVAILABLE&&
            child->SetValue(6)==UIA_E_ELEMENTNOTAVAILABLE;
        return E_ACCESSDENIED; // Real disconnection failures must remain visible.
    });
    if(!called||!identityLive||!inputsBlocked||hr!=E_ACCESSDENIED)return 1;
    if(state->hwnd||state->root||state->visible||!state->items.empty()||!state->closing)return 2;
    SAFEARRAY* after=nullptr;
    if(root->GetRuntimeId(&after)!=UIA_E_ELEMENTNOTAVAILABLE)return 3;
    bool unexpected=false;
    if(gd::DisconnectAccessibleProvider(state,nullptr,[&](IRawElementProviderSimple*) {unexpected=true;return E_FAIL;})!=S_OK||unexpected)return 4;
    std::puts("PASS: UIA identity survives until disconnect; new input is blocked; state then invalidates; original failure preserved. No window/UIA server/graphics created.");
    return 0;
}
