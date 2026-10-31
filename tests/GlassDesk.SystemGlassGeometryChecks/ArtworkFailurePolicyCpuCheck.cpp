#include <ArtworkFailurePolicy.h>
#include <d2derr.h>
#include <dxgi.h>
#include <cstdio>
#include <initializer_list>

int main(){
    for(HRESULT failure:{DXGI_ERROR_DEVICE_REMOVED,DXGI_ERROR_DEVICE_RESET,DXGI_ERROR_DEVICE_HUNG,
        D2DERR_RECREATE_TARGET,E_OUTOFMEMORY,E_INVALIDARG,E_UNEXPECTED})
        if(!gd::PropagateArtworkFailure(3,failure))return 1;
    for(HRESULT invalidInput:{WINCODEC_ERR_BADIMAGE,WINCODEC_ERR_BADHEADER,WINCODEC_ERR_BADSTREAMDATA,
        WINCODEC_ERR_UNKNOWNIMAGEFORMAT,WINCODEC_ERR_FRAMEMISSING,WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT,WINCODEC_ERR_STREAMREAD}){
        if(gd::PropagateArtworkFailure(3,invalidInput))return 2;
        if(!gd::PropagateArtworkFailure(2,invalidInput))return 3;
    }
    if(gd::PropagateArtworkFailure(1,DXGI_ERROR_DEVICE_REMOVED))return 4;
    std::puts("PASS: artwork decode data errors remain optional; experimental graphics/allocation/unknown failures propagate; CPU only");
}
