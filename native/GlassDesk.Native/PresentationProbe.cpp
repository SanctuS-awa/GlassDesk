#error Screen_capture_is_prohibited_by_the_GlassDesk_user_requirement
// QA-only actual desktop-presentation observer. Never shows a window or sends
// input. Only ROI hashes, rectangles, and presentation timestamps are persisted;
// acquired desktop pixels and temporary readback pixels are never saved.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <winrt/base.h>
#include <algorithm>
#include <cstdint>
#include <cwchar>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <numeric>
#include <vector>

using namespace winrt;
namespace
{
struct Target {DWORD pid{};HWND hwnd{};};
BOOL CALLBACK FindTarget(HWND hwnd,LPARAM parameter)
{
    auto& target=*reinterpret_cast<Target*>(parameter);DWORD pid{};GetWindowThreadProcessId(hwnd,&pid);
    if(target.pid&&pid!=target.pid)return TRUE;
    wchar_t name[128]{};GetClassNameW(hwnd,name,128);
    if(IsWindowVisible(hwnd)&&std::wcscmp(name,L"GlassDesk.Native.Host.0.1")==0){target.hwnd=hwnd;target.pid=pid;return FALSE;}
    return TRUE;
}
struct Sample
{
    int64_t qpc{};uint64_t hash{};RECT rect{};
    unsigned accumulated{},significantPixels{};bool changed{},geometryChanged{},protectedContent{},roiChanged{},rawChanged{};
};
struct HeldFrame
{
    IDXGIOutputDuplication* duplication{};
    ~HeldFrame(){if(duplication)duplication->ReleaseFrame();}
};
double Percentile(std::vector<double> values,double proportion)
{
    if(values.empty())return 0;
    std::sort(values.begin(),values.end());return values[std::min(values.size()-1,size_t(std::ceil(proportion*double(values.size())))-1)];
}
}
int wmain(int argc,wchar_t** argv)
{
    char const* stage="arguments";
    try
    {
        Target target{};unsigned seconds=30;std::wstring output;
        for(int i=1;i<argc;++i)
        {
            if(std::wcscmp(argv[i],L"--pid")==0&&i+1<argc)target.pid=std::wcstoul(argv[++i],nullptr,10);
            else if(std::wcscmp(argv[i],L"--seconds")==0&&i+1<argc)seconds=std::wcstoul(argv[++i],nullptr,10);
            else if(std::wcscmp(argv[i],L"--output")==0&&i+1<argc)output=argv[++i];
            else throw hresult_invalid_argument();
        }
        if(output.empty()||seconds<1||seconds>600)throw hresult_invalid_argument();
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        stage="locate visible GlassDesk HWND";EnumWindows(FindTarget,reinterpret_cast<LPARAM>(&target));
        if(!target.hwnd)throw hresult_error(HRESULT_FROM_WIN32(ERROR_NOT_FOUND),L"No visible GlassDesk host for requested PID");
        DWORD affinity{};check_bool(GetWindowDisplayAffinity(target.hwnd,&affinity));
        if(affinity!=WDA_NONE)throw hresult_error(E_ACCESSDENIED,L"Disable liquid/capture exclusion before presentation probing");
        RECT firstRect{};check_bool(GetWindowRect(target.hwnd,&firstRect));auto monitor=MonitorFromRect(&firstRect,MONITOR_DEFAULTTONEAREST);
        stage="match DXGI monitor adapter/output";com_ptr<IDXGIFactory1> factory;check_hresult(CreateDXGIFactory1(__uuidof(IDXGIFactory1),factory.put_void()));
        com_ptr<IDXGIAdapter1> adapter;com_ptr<IDXGIOutput> display;DXGI_OUTPUT_DESC displayInfo{};
        for(unsigned adapterIndex=0;!display;++adapterIndex)
        {
            com_ptr<IDXGIAdapter1> candidate;auto hr=factory->EnumAdapters1(adapterIndex,candidate.put());if(hr==DXGI_ERROR_NOT_FOUND)break;check_hresult(hr);
            for(unsigned outputIndex=0;;++outputIndex)
            {
                com_ptr<IDXGIOutput> outputCandidate;hr=candidate->EnumOutputs(outputIndex,outputCandidate.put());if(hr==DXGI_ERROR_NOT_FOUND)break;check_hresult(hr);
                DXGI_OUTPUT_DESC info{};check_hresult(outputCandidate->GetDesc(&info));
                if(info.Monitor==monitor){adapter=candidate;display=outputCandidate;displayInfo=info;break;}
            }
        }
        if(!display)throw hresult_error(HRESULT_FROM_WIN32(ERROR_NOT_FOUND));
        if(displayInfo.Rotation!=DXGI_MODE_ROTATION_IDENTITY)throw hresult_error(E_NOTIMPL,L"Rotated monitor requires a rotated ROI transform");
        stage="initialize desktop duplication";com_ptr<ID3D11Device> device;com_ptr<ID3D11DeviceContext> context;D3D_FEATURE_LEVEL level{};
        check_hresult(D3D11CreateDevice(adapter.get(),D3D_DRIVER_TYPE_UNKNOWN,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,device.put(),&level,context.put()));
        com_ptr<IDXGIOutputDuplication> duplication;check_hresult(display.as<IDXGIOutput1>()->DuplicateOutput(device.get(),duplication.put()));
        com_ptr<ID3D11Texture2D> staging;unsigned stagingWidth{},stagingHeight{};
        LARGE_INTEGER frequency{},begin{};QueryPerformanceFrequency(&frequency);QueryPerformanceCounter(&begin);
        std::vector<Sample> samples;std::vector<double> changedIntervals,motionIntervals;std::vector<uint32_t> previousPixels;RECT roi=firstRect;
        samples.reserve(size_t(seconds)*240);uint64_t previousHash{};RECT previousRect{};int64_t previousChange{},previousMotion{};
        unsigned frameTimeouts{},missedDesktopFrames{},protectedFrames{},pointerOnlyFrames{},geometryChanges{};
        std::cout<<"{\"probeStarted\":true,\"pid\":"<<target.pid<<",\"seconds\":"<<seconds<<",\"roiPixelsSaved\":false}"<<std::endl;
        auto deadline=GetTickCount64()+uint64_t(seconds)*1000;
        while(GetTickCount64()<deadline&&IsWindow(target.hwnd))
        {
            stage="acquire desktop frame";DXGI_OUTDUPL_FRAME_INFO info{};com_ptr<IDXGIResource> resource;
            auto hr=duplication->AcquireNextFrame(50,&info,resource.put());if(hr==DXGI_ERROR_WAIT_TIMEOUT){++frameTimeouts;continue;}check_hresult(hr);
            HeldFrame held{duplication.get()};
            if(!info.LastPresentTime.QuadPart){++pointerOnlyFrames;continue;}
            missedDesktopFrames+=info.AccumulatedFrames>1?info.AccumulatedFrames-1:0;
            if(info.ProtectedContentMaskedOut)++protectedFrames;
            RECT rect{};check_bool(GetWindowRect(target.hwnd,&rect));RECT nextRoi{},clipped{};UnionRect(&nextRoi,&roi,&rect);
            bool roiChanged=!EqualRect(&roi,&nextRoi);roi=nextRoi;
            if(!IntersectRect(&clipped,&roi,&displayInfo.DesktopCoordinates)||!EqualRect(&roi,&clipped))throw hresult_error(E_NOTIMPL,L"Widget moved beyond selected monitor; restart probe");
            unsigned width=static_cast<unsigned>(roi.right-roi.left),height=static_cast<unsigned>(roi.bottom-roi.top);if(!width||!height)continue;
            stage="copy temporary ROI for hash";
            if(!staging||width!=stagingWidth||height!=stagingHeight)
            {
                staging=nullptr;D3D11_TEXTURE2D_DESC desc{};desc.Width=width;desc.Height=height;desc.MipLevels=desc.ArraySize=1;
                desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_STAGING;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
                check_hresult(device->CreateTexture2D(&desc,nullptr,staging.put()));stagingWidth=width;stagingHeight=height;
            }
            auto desktop=resource.as<ID3D11Texture2D>();
            D3D11_BOX box{static_cast<UINT>(roi.left-displayInfo.DesktopCoordinates.left),static_cast<UINT>(roi.top-displayInfo.DesktopCoordinates.top),0,
                static_cast<UINT>(roi.right-displayInfo.DesktopCoordinates.left),static_cast<UINT>(roi.bottom-displayInfo.DesktopCoordinates.top),1};
            context->CopySubresourceRegion(staging.get(),0,0,0,0,desktop.get(),0,&box);
            D3D11_MAPPED_SUBRESOURCE pixels{};check_hresult(context->Map(staging.get(),0,D3D11_MAP_READ,0,&pixels));
            uint64_t hash=14695981039346656037ull;unsigned significantPixels{};
            size_t pixelCount=size_t(width)*height;bool comparable=!roiChanged&&previousPixels.size()==pixelCount;
            previousPixels.resize(pixelCount);
            for(unsigned y=0;y<height;++y)
            {
                auto row=reinterpret_cast<uint32_t const*>(static_cast<unsigned char const*>(pixels.pData)+size_t(y)*pixels.RowPitch);
                for(unsigned x=0;x<width;++x)
                {
                    auto pixel=row[x];hash^=pixel;hash*=1099511628211ull;auto index=size_t(y)*width+x;
                    if(comparable)
                    {
                        auto previous=previousPixels[index];
                        int difference=std::abs(int(pixel&255)-int(previous&255))+std::abs(int((pixel>>8)&255)-int((previous>>8)&255))+std::abs(int((pixel>>16)&255)-int((previous>>16)&255));
                        if(difference>=12)++significantPixels;
                    }
                    previousPixels[index]=pixel;
                }
            }
            context->Unmap(staging.get(),0);
            // Hash a stable union ROI. A changing crop size could change its
            // hash even if no widget pixels were presented; exclude every ROI
            // growth/reset from cadence statistics instead of counting that.
            bool geometry=samples.empty()||!EqualRect(&rect,&previousRect),rawChanged=samples.empty()||(!roiChanged&&hash!=previousHash);
            bool changed=samples.empty()||(comparable&&significantPixels>=std::max(size_t(8),pixelCount/4000));
            auto stamp=info.LastPresentTime.QuadPart;
            if(roiChanged){previousChange=0;previousMotion=0;}
            if(changed&&previousChange&&stamp>previousChange)
            {
                double interval=1000.0*double(stamp-previousChange)/double(frequency.QuadPart);changedIntervals.push_back(interval);
                // Consecutive changed rectangles within 100 ms isolate the
                // active resize/move cadence from intentional dwell periods.
                if(geometry&&previousMotion==previousChange&&interval<=100)motionIntervals.push_back(interval);
            }
            if(geometry){++geometryChanges;previousMotion=stamp;}else previousMotion=0;
            if(changed)previousChange=stamp;
            samples.push_back({stamp,hash,rect,info.AccumulatedFrames,significantPixels,changed,geometry,info.ProtectedContentMaskedOut!=FALSE,roiChanged,rawChanged});
            previousHash=hash;previousRect=rect;
        }
        double sum=std::accumulate(motionIntervals.begin(),motionIntervals.end(),0.0);
        double average=motionIntervals.empty()?0:sum/double(motionIntervals.size());
        std::ofstream file{std::filesystem::path(output),std::ios::binary|std::ios::trunc};if(!file)throw hresult_error(HRESULT_FROM_WIN32(ERROR_OPEN_FAILED));
        file<<"{\n\"testKind\":\"actual desktop duplication ROI hash presentation probe\",\n\"pid\":"<<target.pid<<",\"durationSeconds\":"<<seconds<<",\"qpcFrequency\":"<<frequency.QuadPart
            <<",\"roiPixelsSaved\":false,\"productionRendererReadback\":false,\"desktopFrames\":"<<samples.size()<<",\"pointerOnlyFrames\":"<<pointerOnlyFrames<<",\"missedDesktopFrames\":"<<missedDesktopFrames
            <<",\"protectedDesktopFrames\":"<<protectedFrames<<",\"geometryChanges\":"<<geometryChanges<<",\"allChangedIntervals\":"<<changedIntervals.size()<<",\"motionIntervals\":"<<motionIntervals.size()
            <<",\"motionAverageMilliseconds\":"<<average<<",\"motionP50Milliseconds\":"<<Percentile(motionIntervals,.5)<<",\"motionP95Milliseconds\":"<<Percentile(motionIntervals,.95)<<",\"motionMaxMilliseconds\":"<<Percentile(motionIntervals,1)
            <<",\"motionIntervalsAbove25ms\":"<<std::count_if(motionIntervals.begin(),motionIntervals.end(),[](double value){return value>25;})
            <<",\"motionIntervalsAbove33_4ms\":"<<std::count_if(motionIntervals.begin(),motionIntervals.end(),[](double value){return value>33.4;})
            <<",\"pixelChangeThresholdRgbL1\":12,\"significantPixelCountThreshold\":\"max(8,roiArea/4000)\""
            <<",\"rawChangedFrames\":"<<std::count_if(samples.begin(),samples.end(),[](Sample const& s){return s.rawChanged;})<<",\"significantChangedFrames\":"<<std::count_if(samples.begin(),samples.end(),[](Sample const& s){return s.changed;})
            <<",\"finalRoi\":["<<roi.left<<","<<roi.top<<","<<roi.right<<","<<roi.bottom<<"],\"samples\":[\n";
        for(size_t i=0;i<samples.size();++i)
        {
            auto const& s=samples[i];file<<(i?",\n":"")<<"{\"qpc\":"<<s.qpc<<",\"roiHash\":\""<<std::hex<<s.hash<<std::dec<<"\",\"rect\":["<<s.rect.left<<","<<s.rect.top<<","<<s.rect.right<<","<<s.rect.bottom
                <<"],\"accumulatedFrames\":"<<s.accumulated<<",\"changed\":"<<(s.changed?"true":"false")<<",\"rawHashChanged\":"<<(s.rawChanged?"true":"false")<<",\"significantPixels\":"<<s.significantPixels
                <<",\"geometryChanged\":"<<(s.geometryChanged?"true":"false")<<",\"roiChanged\":"<<(s.roiChanged?"true":"false")<<",\"protectedContent\":"<<(s.protectedContent?"true":"false")<<"}";
        }
        file<<"\n]}\n";file.close();
        std::cout<<"{\"probeCompleted\":true,\"desktopFrames\":"<<samples.size()<<",\"motionIntervals\":"<<motionIntervals.size()<<",\"motionAverageMilliseconds\":"<<average<<",\"motionP95Milliseconds\":"<<Percentile(motionIntervals,.95)
            <<",\"missedDesktopFrames\":"<<missedDesktopFrames<<",\"protectedFrames\":"<<protectedFrames<<",\"frameTimeouts\":"<<frameTimeouts<<",\"roiPixelsSaved\":false}\n";
        return samples.empty()?2:0;
    }
    catch(hresult_error const& error){std::cerr<<"presentation-probe stage="<<stage<<" HRESULT=0x"<<std::hex<<static_cast<unsigned>(error.code())<<"\n";return 1;}
}
