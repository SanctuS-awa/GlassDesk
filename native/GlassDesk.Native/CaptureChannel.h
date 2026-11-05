#error Screen_capture_is_prohibited_by_the_GlassDesk_user_requirement
#pragma once
#include "CaptureProtocol.h"
#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <winrt/base.h>
#include <string>
#include <vector>

namespace gd
{
struct CaptureChannel
{
    HANDLE mapping{},stop{},job{},process{},texture{};
    CaptureControl* control{};
    ~CaptureChannel(){Stop();}
    void Stop()noexcept
    {
        if(stop)SetEvent(stop);
        if(process)WaitForSingleObject(process,100);
        if(job)CloseHandle(job);job=nullptr; // KILL_ON_JOB_CLOSE: no orphan capture.
        if(process){WaitForSingleObject(process,500);CloseHandle(process);}process=nullptr;
        if(control)UnmapViewOfFile(control);control=nullptr;
        if(mapping)CloseHandle(mapping);mapping=nullptr;
        if(stop)CloseHandle(stop);stop=nullptr;
        if(texture)CloseHandle(texture);texture=nullptr;
    }
    void Start(ID3D11Device* device,ID3D11Texture2D* target,HMONITOR monitor,unsigned width,unsigned height)
    {
        using namespace winrt;
        Stop();SECURITY_ATTRIBUTES security{sizeof(security),nullptr,TRUE};
        mapping=CreateFileMappingW(INVALID_HANDLE_VALUE,&security,PAGE_READWRITE,0,sizeof(CaptureControl),nullptr);check_bool(mapping!=nullptr);
        control=static_cast<CaptureControl*>(MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(CaptureControl)));check_bool(control!=nullptr);
        new(control)CaptureControl();
        stop=CreateEventW(&security,TRUE,FALSE,nullptr);check_bool(stop!=nullptr);
        com_ptr<IDXGIResource1> resource;check_hresult(target->QueryInterface(__uuidof(IDXGIResource1),resource.put_void()));
        check_hresult(resource->CreateSharedHandle(&security,DXGI_SHARED_RESOURCE_READ|DXGI_SHARED_RESOURCE_WRITE,nullptr,&texture));
        com_ptr<IDXGIDevice> dxgi;check_hresult(device->QueryInterface(__uuidof(IDXGIDevice),dxgi.put_void()));
        com_ptr<IDXGIAdapter> adapter;check_hresult(dxgi->GetAdapter(adapter.put()));DXGI_ADAPTER_DESC desc{};check_hresult(adapter->GetDesc(&desc));
        control->adapter=desc.AdapterLuid;control->monitor=reinterpret_cast<uint64_t>(monitor);
        control->textureHandle=reinterpret_cast<uint64_t>(texture);control->width=width;control->height=height;
        job=CreateJobObjectW(nullptr,nullptr);check_bool(job!=nullptr);
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};limits.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        check_bool(SetInformationJobObject(job,JobObjectExtendedLimitInformation,&limits,sizeof(limits)));
        std::wstring path(32768,L'\0');auto length=GetModuleFileNameW(nullptr,path.data(),static_cast<DWORD>(path.size()));check_bool(length&&length<path.size());
        path.resize(length);path=path.substr(0,path.find_last_of(L"\\/"))+L"\\GlassDesk.CaptureWorker.exe";
        std::wstring command=L"\""+path+L"\" "+std::to_wstring(reinterpret_cast<uintptr_t>(mapping))+L" "+std::to_wstring(reinterpret_cast<uintptr_t>(stop));
        SIZE_T bytes{};InitializeProcThreadAttributeList(nullptr,1,0,&bytes);
        std::vector<unsigned char> storage(bytes);auto attributes=reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage.data());
        check_bool(InitializeProcThreadAttributeList(attributes,1,0,&bytes));
        PROCESS_INFORMATION info{};
        try
        {
            HANDLE handles[]{mapping,stop,texture};
            check_bool(UpdateProcThreadAttribute(attributes,0,PROC_THREAD_ATTRIBUTE_HANDLE_LIST,handles,sizeof(handles),nullptr,nullptr));
            STARTUPINFOEXW startup{};startup.StartupInfo.cb=sizeof(startup);startup.lpAttributeList=attributes;
            check_bool(CreateProcessW(path.c_str(),command.data(),nullptr,nullptr,TRUE,
                CREATE_NO_WINDOW|CREATE_SUSPENDED|EXTENDED_STARTUPINFO_PRESENT,nullptr,nullptr,&startup.StartupInfo,&info));
            process=info.hProcess;
            if(!AssignProcessToJobObject(job,process)){auto error=GetLastError();TerminateProcess(process,1);throw hresult_error(HRESULT_FROM_WIN32(error));}
            check_bool(ResumeThread(info.hThread)!=static_cast<DWORD>(-1));
        }
        catch(...){if(info.hThread)CloseHandle(info.hThread);DeleteProcThreadAttributeList(attributes);throw;}
        CloseHandle(info.hThread);DeleteProcThreadAttributeList(attributes);
        SetHandleInformation(mapping,HANDLE_FLAG_INHERIT,0);SetHandleInformation(stop,HANDLE_FLAG_INHERIT,0);SetHandleInformation(texture,HANDLE_FLAG_INHERIT,0);
    }
    void Request(RECT source)
    {
        if(EqualRect(&source,&control->source)&&InterlockedCompareExchange(&control->request,0,0))return;
        InterlockedIncrement(&control->request);control->source=source;MemoryBarrier();InterlockedIncrement(&control->request);
    }
    DWORD Pid()const noexcept{return process?GetProcessId(process):0;}
};
}
