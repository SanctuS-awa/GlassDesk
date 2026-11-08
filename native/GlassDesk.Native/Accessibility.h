#pragma once
#include <UIAutomation.h>
#include <wrl.h>
#include <memory>
#include <mutex>
#include <vector>
#include <string>
#include <algorithm>
#include <cmath>
#include <utility>
#include "NativeApi.h"
#include "AccessibleSurfaceHit.h"

namespace gd
{
constexpr UINT InvokeMessage = WM_APP + 72;
constexpr UINT RangeMessage = WM_APP + 73;
struct AccessibleItem
{
    int id{}; std::wstring name; float x{}, y{}, width{}, height{}; bool enabled{}, range{};
    double value{}, minimum{}, maximum{};
};
struct AccessibleState
{
    std::mutex mutex;
    HWND hwnd{};
    IRawElementProviderFragment* root{}; // Borrowed while the host owns the provider.
    RECT rectangle{}; float offsetX{},offsetY{}; unsigned dpi{96}; bool visible{},closing{};
    input::AccessibleSurfaceShape surfaceShape{};
    std::wstring name{L"GlassDesk 音乐小组件"};
    std::vector<AccessibleItem> items;
};
struct RangeRequest { int id; double value; };
using Microsoft::WRL::ComPtr;
using Microsoft::WRL::RuntimeClass;
using Microsoft::WRL::RuntimeClassFlags;
using Microsoft::WRL::ClassicCom;
using Microsoft::WRL::FtmBase;

class ElementProvider;
ComPtr<ElementProvider> MakeElement(std::shared_ptr<AccessibleState> const& state, int id, bool root);

class ElementProvider final : public RuntimeClass<RuntimeClassFlags<ClassicCom>,
    IRawElementProviderSimple, IRawElementProviderFragment, IRawElementProviderFragmentRoot,
    IInvokeProvider, IRangeValueProvider, FtmBase>
{
    std::shared_ptr<AccessibleState> state;
    int id{}; bool isRoot{};
    HRESULT Read(AccessibleItem& item, RECT* rectangle=nullptr, unsigned* dpi=nullptr)
    {
        std::lock_guard lock(state->mutex);
        if (!state->hwnd || !state->visible) return UIA_E_ELEMENTNOTAVAILABLE;
        if (rectangle) *rectangle=state->rectangle;
        if (dpi) *dpi=state->dpi;
        if (isRoot) { item.name=state->name; item.enabled=true; return S_OK; }
        auto found=std::find_if(state->items.begin(),state->items.end(),[this](auto const& entryData){return entryData.id==id;});
        if(found==state->items.end()) return UIA_E_ELEMENTNOTAVAILABLE;
        item=*found; if(state->dpi){const float dipPerPixel=96.0f/static_cast<float>(state->dpi);item.x+=state->offsetX*dipPerPixel;item.y+=state->offsetY*dipPerPixel;} return S_OK;
    }
public:
    ElementProvider(std::shared_ptr<AccessibleState> entryData, int identifier, bool root)
        : state(std::move(entryData)), id(identifier), isRoot(root) {}
    HRESULT STDMETHODCALLTYPE get_ProviderOptions(ProviderOptions* result) override
    { if(!result)return E_POINTER; *result=static_cast<ProviderOptions>(ProviderOptions_ServerSideProvider|ProviderOptions_UseComThreading); return S_OK; }
    HRESULT STDMETHODCALLTYPE GetPatternProvider(PATTERNID pattern, IUnknown** result) override
    {
        if(!result)return E_POINTER; *result=nullptr;
        AccessibleItem item; auto hr=Read(item); if(FAILED(hr))return hr;
        if(!isRoot && !item.range && pattern==UIA_InvokePatternId) return QueryInterface(__uuidof(IInvokeProvider),reinterpret_cast<void**>(result));
        if(!isRoot && item.range && pattern==UIA_RangeValuePatternId) return QueryInterface(__uuidof(IRangeValueProvider),reinterpret_cast<void**>(result));
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetPropertyValue(PROPERTYID property, VARIANT* result) override
    {
        if(!result)return E_POINTER; VariantInit(result);
        AccessibleItem item; RECT rectangle{};unsigned dpi{};auto hr=Read(item,&rectangle,&dpi); if(FAILED(hr))return hr;
        switch(property)
        {
        case UIA_NamePropertyId:
            result->vt=VT_BSTR; result->bstrVal=SysAllocString(item.name.c_str()); return result->bstrVal ? S_OK : E_OUTOFMEMORY;
        case UIA_AutomationIdPropertyId:
        {
            auto text=isRoot?L"GlassDesk.Widget":L"GlassDesk.Control."+std::to_wstring(id);
            result->vt=VT_BSTR; result->bstrVal=SysAllocString(text.c_str()); return result->bstrVal?S_OK:E_OUTOFMEMORY;
        }
        case UIA_ControlTypePropertyId: result->vt=VT_I4; result->lVal=isRoot?UIA_PaneControlTypeId:(item.range?UIA_SliderControlTypeId:UIA_ButtonControlTypeId); break;
        case UIA_IsEnabledPropertyId: result->vt=VT_BOOL; result->boolVal=item.enabled?VARIANT_TRUE:VARIANT_FALSE; break;
        case UIA_IsControlElementPropertyId:
        case UIA_IsContentElementPropertyId: result->vt=VT_BOOL; result->boolVal=VARIANT_TRUE; break;
        case UIA_IsKeyboardFocusablePropertyId:
        case UIA_HasKeyboardFocusPropertyId: result->vt=VT_BOOL; result->boolVal=VARIANT_FALSE; break;
        case UIA_IsOffscreenPropertyId:
        {
            float scale=dpi/96.0f;
            bool outside=!isRoot&&(item.x+item.width<=0||item.y+item.height<=0||item.x*scale>=rectangle.right-rectangle.left||item.y*scale>=rectangle.bottom-rectangle.top);
            result->vt=VT_BOOL;result->boolVal=outside?VARIANT_TRUE:VARIANT_FALSE;break;
        }
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE get_HostRawElementProvider(IRawElementProviderSimple** result) override
    {
        if(!result)return E_POINTER; *result=nullptr;
        std::lock_guard lock(state->mutex);
        if(!state->hwnd)return UIA_E_ELEMENTNOTAVAILABLE;
        return isRoot ? UiaHostProviderFromHwnd(state->hwnd,result) : S_OK;
    }
    HRESULT STDMETHODCALLTYPE Navigate(NavigateDirection direction, IRawElementProviderFragment** result) override
    {
        if(!result)return E_POINTER; *result=nullptr;
        int target{}; bool make=false;
        {
            std::lock_guard lock(state->mutex);
            if(!state->hwnd || !state->visible)return UIA_E_ELEMENTNOTAVAILABLE;
            if(!isRoot && direction==NavigateDirection_Parent)
            { if(state->root){*result=state->root;(*result)->AddRef();} return S_OK; }
            if(isRoot && !state->items.empty())
            {
                if(direction==NavigateDirection_FirstChild){target=state->items.front().id;make=true;}
                if(direction==NavigateDirection_LastChild){target=state->items.back().id;make=true;}
            }
            else if(!isRoot)
            {
                auto item=std::find_if(state->items.begin(),state->items.end(),[this](auto const& entryData){return entryData.id==id;});
                if(item==state->items.end())return UIA_E_ELEMENTNOTAVAILABLE;
                if(direction==NavigateDirection_NextSibling && std::next(item)!=state->items.end()){target=std::next(item)->id;make=true;}
                if(direction==NavigateDirection_PreviousSibling && item!=state->items.begin()){target=std::prev(item)->id;make=true;}
            }
        }
        return make ? MakeElement(state,target,false).CopyTo(result) : S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetRuntimeId(SAFEARRAY** result) override
    {
        if(!result)return E_POINTER; *result=nullptr;
        std::lock_guard lock(state->mutex); if(!state->hwnd)return UIA_E_ELEMENTNOTAVAILABLE;
        // Root is HWND-backed; its runtime identifier is supplied by UIA.
        if(isRoot)return S_OK;
        int values[]{UiaAppendRuntimeId,id};
        auto array=SafeArrayCreateVector(VT_I4,0,2); if(!array)return E_OUTOFMEMORY;
        for(LONG index=0;index<2;++index)SafeArrayPutElement(array,&index,&values[index]);
        *result=array; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE get_BoundingRectangle(UiaRect* result) override
    {
        if(!result)return E_POINTER;
        AccessibleItem item; RECT rect{}; unsigned dpi{}; auto hr=Read(item,&rect,&dpi); if(FAILED(hr))return hr;
        float scale=dpi/96.0f;
        if(isRoot)*result=UiaRect{double(rect.left),double(rect.top),double(rect.right-rect.left),double(rect.bottom-rect.top)};
        else
        {
            double left=std::clamp(double(rect.left+item.x*scale),double(rect.left),double(rect.right));
            double top=std::clamp(double(rect.top+item.y*scale),double(rect.top),double(rect.bottom));
            double right=std::clamp(double(rect.left+(item.x+item.width)*scale),double(rect.left),double(rect.right));
            double bottom=std::clamp(double(rect.top+(item.y+item.height)*scale),double(rect.top),double(rect.bottom));
            *result=UiaRect{left,top,std::max(0.0,right-left),std::max(0.0,bottom-top)};
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetEmbeddedFragmentRoots(SAFEARRAY** result) override {if(!result)return E_POINTER;*result=nullptr;return S_OK;}
    HRESULT STDMETHODCALLTYPE SetFocus() override {return S_OK;} // Non-activating widget deliberately never takes focus.
    HRESULT STDMETHODCALLTYPE get_FragmentRoot(IRawElementProviderFragmentRoot** result) override
    {
        if(!result)return E_POINTER;*result=nullptr;
        std::lock_guard lock(state->mutex); if(!state->hwnd || !state->root)return UIA_E_ELEMENTNOTAVAILABLE;
        return state->root->QueryInterface(__uuidof(IRawElementProviderFragmentRoot),reinterpret_cast<void**>(result));
    }
    HRESULT STDMETHODCALLTYPE ElementProviderFromPoint(double x,double y,IRawElementProviderFragment** result) override
    {
        if(!result)return E_POINTER;*result=nullptr;
        int target{};bool found=false;
        {
            std::lock_guard lock(state->mutex);if(!state->hwnd || !state->visible)return UIA_E_ELEMENTNOTAVAILABLE;
            if(x<state->rectangle.left||y<state->rectangle.top||x>=state->rectangle.right||y>=state->rectangle.bottom)return S_OK;
            if(state->dpi==0)return S_OK;
            const float dipPerPixel=96.0f/static_cast<float>(state->dpi);
            auto px=(static_cast<float>(x)-static_cast<float>(state->rectangle.left)-state->offsetX)*dipPerPixel;
            auto py=(static_cast<float>(y)-static_cast<float>(state->rectangle.top)-state->offsetY)*dipPerPixel;
            if(!input::AccessibleSurfaceContainsDips(state->surfaceShape,
                static_cast<float>(px),static_cast<float>(py)))return S_OK;
            for(auto item=state->items.rbegin();item!=state->items.rend();++item)
                if(px>=item->x && py>=item->y && px<item->x+item->width && py<item->y+item->height){target=item->id;found=true;break;}
            if(!found){ if(state->root){*result=state->root;(*result)->AddRef();} return S_OK; }
        }
        return MakeElement(state,target,false).CopyTo(result);
    }
    HRESULT STDMETHODCALLTYPE GetFocus(IRawElementProviderFragment** result) override {if(!result)return E_POINTER;*result=nullptr;return S_OK;}
    HRESULT STDMETHODCALLTYPE Invoke() override
    {
        std::lock_guard lock(state->mutex); if(state->closing || !state->hwnd || !state->visible)return UIA_E_ELEMENTNOTAVAILABLE;
        auto item=std::find_if(state->items.begin(),state->items.end(),[this](auto const& entryData){return entryData.id==id;});
        if(item==state->items.end())return UIA_E_ELEMENTNOTAVAILABLE;
        if(!item->enabled)return UIA_E_ELEMENTNOTENABLED;
        return PostMessageW(state->hwnd,InvokeMessage,static_cast<WPARAM>(id),0)?S_OK:HRESULT_FROM_WIN32(GetLastError());
    }
    HRESULT STDMETHODCALLTYPE SetValue(double entryData) override
    {
        std::lock_guard lock(state->mutex);if(state->closing || !state->hwnd || !state->visible)return UIA_E_ELEMENTNOTAVAILABLE;
        auto item=std::find_if(state->items.begin(),state->items.end(),[this](auto const& entry){return entry.id==id;});
        if(item==state->items.end())return UIA_E_ELEMENTNOTAVAILABLE;
        if(!item->enabled)return UIA_E_ELEMENTNOTENABLED;
        if(!item->range || !std::isfinite(entryData) || entryData<item->minimum || entryData>item->maximum)return E_INVALIDARG;
        auto request=std::make_unique<RangeRequest>(RangeRequest{id,entryData});
        if(!PostMessageW(state->hwnd,RangeMessage,0,reinterpret_cast<LPARAM>(request.get())))return HRESULT_FROM_WIN32(GetLastError());
        request.release(); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE get_Value(double* result) override {if(!result)return E_POINTER;AccessibleItem item;auto hr=Read(item);if(SUCCEEDED(hr))*result=item.value;return hr;}
    HRESULT STDMETHODCALLTYPE get_IsReadOnly(BOOL* result) override {if(!result)return E_POINTER;AccessibleItem item;auto hr=Read(item);if(SUCCEEDED(hr))*result=!item.enabled;return hr;}
    HRESULT STDMETHODCALLTYPE get_Maximum(double* result) override {if(!result)return E_POINTER;AccessibleItem item;auto hr=Read(item);if(SUCCEEDED(hr))*result=item.maximum;return hr;}
    HRESULT STDMETHODCALLTYPE get_Minimum(double* result) override {if(!result)return E_POINTER;AccessibleItem item;auto hr=Read(item);if(SUCCEEDED(hr))*result=item.minimum;return hr;}
    HRESULT STDMETHODCALLTYPE get_LargeChange(double* result) override {if(!result)return E_POINTER;*result=5;return S_OK;}
    HRESULT STDMETHODCALLTYPE get_SmallChange(double* result) override
    {
        if(!result)return E_POINTER;
        AccessibleItem item;auto hr=Read(item);if(FAILED(hr))return hr;
        // WidgetActions.HomeVolume is the 0..100 system-volume control; its
        // keyboard and UIA increment is specified as two percentage points.
        *result=item.id==370?2.0:1.0;return S_OK;
    }
};
inline ComPtr<ElementProvider> MakeElement(std::shared_ptr<AccessibleState> const& state,int id,bool root)
{ return Microsoft::WRL::Make<ElementProvider>(state,id,root); }

// Keep HWND-backed identity available while UIA disconnects its references.
// Block new input posts first, then invalidate identity and drain queued input
// in Host::Shutdown. The callback is injectable for no-window CPU regression.
template<class Disconnect>
HRESULT DisconnectAccessibleProvider(std::shared_ptr<AccessibleState> const& state,
    IRawElementProviderSimple* provider,Disconnect&& disconnect)
{
    {std::lock_guard lock(state->mutex);state->closing=true;}
    const HRESULT result=provider?std::forward<Disconnect>(disconnect)(provider):S_OK;
    {std::lock_guard lock(state->mutex);state->hwnd=nullptr;state->root=nullptr;state->visible=false;state->items.clear();}
    return result;
}
}
