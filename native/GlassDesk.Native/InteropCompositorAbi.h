#pragma once

// Private, undocumented Windows composition ABI declarations. The GUIDs and
// vtable order are transcribed from ADeltaX's public interop sample and
// accompanying explanation; this header contains declarations only:
// https://gist.github.com/ADeltaX/c0e565a50d2cedb62dab5c13674cb8b5
// https://blog.adeltax.com/interopcompositor-and-coredispatcher/
// These interfaces are not in the public Windows SDK contract and may change.

#include <dcomp.h>
#include <inspectable.h>
#include <unknwn.h>

namespace system_compositor_lens::private_composition_abi
{
    MIDL_INTERFACE("9bb59fc9-3326-4c32-bf06-d6b415ac2bc5")
    IInteropCompositorPartnerCallback : public IUnknown
    {
    public:
        virtual HRESULT STDMETHODCALLTYPE NotifyDirty() = 0;
        virtual HRESULT STDMETHODCALLTYPE NotifyDeferralState(bool deferRequested) = 0;
    };

    MIDL_INTERFACE("e7894c70-af56-4f52-b382-4b3cd263dc6f")
    IInteropCompositorPartner : public IUnknown
    {
    public:
        virtual HRESULT STDMETHODCALLTYPE MarkDirty() = 0;
        virtual HRESULT STDMETHODCALLTYPE ClearCallback() = 0;
        virtual HRESULT STDMETHODCALLTYPE CreateManipulationTransform(
            IDCompositionTransform* transform,
            REFIID iid,
            void** result) = 0;
        virtual HRESULT STDMETHODCALLTYPE RealClose() = 0;
    };

    MIDL_INTERFACE("22118adf-23f1-4801-bcfa-66cbf48cc51b")
    IInteropCompositorFactoryPartner : public IInspectable
    {
    public:
        virtual HRESULT STDMETHODCALLTYPE CreateInteropCompositor(
            IUnknown* renderingDevice,
            IInteropCompositorPartnerCallback* callback,
            REFIID iid,
            void** instance) = 0;
        virtual HRESULT STDMETHODCALLTYPE CheckEnabled(
            bool* enableInteropCompositor,
            bool* enableExposeVisual) = 0;
    };
}
