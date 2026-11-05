#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <bcrypt.h>
#include <objbase.h>
#include <string>
#include <vector>
#include <stdexcept>
#include <array>
#include <cwchar>
#include "PackagePins.h"
#pragma comment(lib,"bcrypt.lib")
#pragma comment(lib,"shell32.lib")
#pragma comment(lib,"user32.lib")
#pragma comment(lib,"ole32.lib")

namespace {
struct Handle {
    HANDLE value{INVALID_HANDLE_VALUE};
    explicit Handle(HANDLE h=INVALID_HANDLE_VALUE):value(h){}
    ~Handle(){if(value&&value!=INVALID_HANDLE_VALUE)CloseHandle(value);}
    Handle(Handle const&)=delete; Handle& operator=(Handle const&)=delete;
    bool valid()const{return value&&value!=INVALID_HANDLE_VALUE;}
};
std::wstring root;
bool checkMode=false;
constexpr wchar_t promptTitle[]=L"GlassDesk：启动液态玻璃审查版";
struct PendingPrompt{DWORD session;HWND window{};};
BOOL CALLBACK FindPendingPrompt(HWND window,LPARAM parameter){
    auto& pending=*reinterpret_cast<PendingPrompt*>(parameter);
    wchar_t title[96]{},kind[32]{};
    if(!GetWindowTextW(window,title,96)||wcscmp(title,promptTitle)!=0||
        !GetClassNameW(window,kind,32)||wcscmp(kind,L"#32770")!=0)return TRUE;
    DWORD pid{},session{};GetWindowThreadProcessId(window,&pid);
    if(pid==GetCurrentProcessId()||!ProcessIdToSessionId(pid,&session)||session!=pending.session)return TRUE;
    pending.window=window;return FALSE;
}
std::wstring Path(wchar_t const* relative){return root+L"\\"+relative;}
void Require(bool ok,wchar_t const* message){if(!ok){SetLastError(ERROR_INVALID_DATA);throw std::wstring(message);}}
std::wstring Quote(std::wstring const& value){
    std::wstring result=L"\"";size_t slashes=0;
    for(wchar_t c:value){if(c==L'\\'){++slashes;continue;}
        if(c==L'\"')result.append(slashes*2+1,L'\\');else result.append(slashes,L'\\');
        slashes=0;result+=c;}
    result.append(slashes*2,L'\\');result+=L'\"';return result;
}
void RegularPath(std::wstring const& path){
    auto current=path;
    while(current.size()>3){DWORD attributes=GetFileAttributesW(current.c_str());
        Require(attributes!=INVALID_FILE_ATTRIBUTES,L"应用文件或父目录不可访问。");
        Require(!(attributes&FILE_ATTRIBUTE_REPARSE_POINT),L"启动目录包含重解析链接，已停止。");
        auto separator=current.find_last_of(L"\\/");if(separator==std::wstring::npos)break;
        current.resize(separator);}
}
std::wstring Sha256(std::wstring const& path){
    RegularPath(path);
    Handle file(CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,nullptr));
    Require(file.valid(),L"无法读取应用文件进行校验。");
    BY_HANDLE_FILE_INFORMATION info{};
    Require(GetFileInformationByHandle(file.value,&info)!=FALSE&&!(info.dwFileAttributes&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT)),L"校验对象不是普通文件。");
    LARGE_INTEGER size{};Require(GetFileSizeEx(file.value,&size)&&size.QuadPart>0&&size.QuadPart<=536870912,L"应用文件大小异常。");
    BCRYPT_ALG_HANDLE algorithm{};BCRYPT_HASH_HANDLE hash{};
    Require(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)>=0,L"SHA256 初始化失败。");
    try{
        Require(BCryptCreateHash(algorithm,&hash,nullptr,0,nullptr,0,0)>=0,L"SHA256 初始化失败。");
        std::array<unsigned char,65536> buffer{};DWORD count{};
        do {Require(ReadFile(file.value,buffer.data(),DWORD(buffer.size()),&count,nullptr)!=FALSE,L"应用文件读取失败。");
            if(count)Require(BCryptHashData(hash,buffer.data(),count,0)>=0,L"SHA256 更新失败。");}while(count);
        std::array<unsigned char,32> digest{};
        Require(BCryptFinishHash(hash,digest.data(),DWORD(digest.size()),0)>=0,L"SHA256 校验失败。");
        BCryptDestroyHash(hash);hash=nullptr;BCryptCloseAlgorithmProvider(algorithm,0);algorithm=nullptr;
        std::wstring result;constexpr wchar_t hex[]=L"0123456789ABCDEF";
        for(auto byte:digest){result+=hex[byte>>4];result+=hex[byte&15];}return result;
    }catch(...){if(hash)BCryptDestroyHash(hash);if(algorithm)BCryptCloseAlgorithmProvider(algorithm,0);throw;}
}
void CheckPackage(){
    struct Pin{wchar_t const* path;wchar_t const* hash;};
    const Pin pins[]={
        {L"guard\\r128-human-review-root\\Invoke-R109GraphBaselineOuter.ps1",BETA_OUTER_SHA},
        {L"guard\\r128-human-review-root\\Invoke-R109GraphBaselineOwner.ps1",BETA_OWNER_SHA},
        {L"guard\\r128-human-review-root\\Start-R109CimProducer.ps1",BETA_PRODUCER_ENTRY_SHA},
        {L"powershell\\pwsh.exe",BETA_PWSH_SHA},
        {L"runtime\\GlassDesk.exe",BETA_RUNTIME_EXE_SHA},
        {L"runtime\\GlassDesk.dll",BETA_RUNTIME_DLL_SHA},
        {L"runtime\\GlassDesk.Native.dll",BETA_NATIVE_SHA}};
    for(auto const& pin:pins)Require(Sha256(Path(pin.path))==pin.hash,L"应用文件校验不一致，未启动液态玻璃。请保留完整文件夹。");
}
void WriteUtf8(HANDLE file,std::wstring const& text){
    int needed=WideCharToMultiByte(CP_UTF8,0,text.data(),int(text.size()),nullptr,0,nullptr,nullptr);
    if(needed<=0)return;std::string bytes(size_t(needed),'\0');
    WideCharToMultiByte(CP_UTF8,0,text.data(),int(text.size()),bytes.data(),needed,nullptr,nullptr);
    DWORD written{};WriteFile(file,bytes.data(),DWORD(bytes.size()),&written,nullptr);
}
int Launch(){
    Handle mutex(CreateMutexW(nullptr,TRUE,L"Local\\GlassDesk.02.BetaLauncher"));
    DWORD mutexError=GetLastError();Require(mutex.valid(),L"无法建立单实例保护。");
    if(mutexError==ERROR_ALREADY_EXISTS){
        DWORD session{};
        if(ProcessIdToSessionId(GetCurrentProcessId(),&session)){
            PendingPrompt pending{session};EnumWindows(FindPendingPrompt,reinterpret_cast<LPARAM>(&pending));
            if(pending.window){ShowWindow(pending.window,SW_RESTORE);if(SetForegroundWindow(pending.window))return 0;
                MessageBoxW(nullptr,L"原启动窗口仍在等待风险确认，液态玻璃尚未启动。请打开任务栏里的 GlassDesk 确认窗口；选择确定或取消。",L"GlassDesk 0.2.beta",MB_OK|MB_ICONINFORMATION);return 2;}
        }
        MessageBoxW(nullptr,L"另一个启动请求正在处理或运行中。请使用现有请求；此提示不表示液态玻璃窗口已成功显示。",L"GlassDesk 0.2.beta",MB_OK|MB_ICONINFORMATION);return 2;
    }
    CheckPackage();
    const wchar_t* warning=L"将打开最后一版液态玻璃，固定在主屏顶部居中。\n\n"
        L"此前测试期间，DWM 在 NVIDIA 驱动中崩溃；具体原因尚未定位，仍可能导致黑屏或桌面重启。\n\n"
        L"本次由完整故障监测链监督；异常即停止，不清除旧故障，不自动重试，不降级冒充普通材质。\n"
        L"这是本机审查版，最长运行六小时。\n\n确定后授权本次受监控启动；取消则不创建图形宿主。";
    if(MessageBoxW(nullptr,warning,promptTitle,MB_OKCANCEL|MB_ICONWARNING|MB_DEFBUTTON2)!=IDOK)return 0;
    DWORD session{};Require(ProcessIdToSessionId(GetCurrentProcessId(),&session)!=FALSE,L"无法确认启动会话。");
    auto logs=Path(L"launch-logs");
    Require(CreateDirectoryW(logs.c_str(),nullptr)||GetLastError()==ERROR_ALREADY_EXISTS,L"无法建立启动日志目录。");
    RegularPath(logs);
    GUID guid{};Require(CoCreateGuid(&guid)>=0,L"无法生成启动记录标识。");wchar_t token[40]{};
    StringFromGUID2(guid,token,40);
    std::wstring logPath=logs+L"\\"+token+L".log";
    SECURITY_ATTRIBUTES security{sizeof(security),nullptr,TRUE};
    Handle log(CreateFileW(logPath.c_str(),GENERIC_WRITE,FILE_SHARE_READ,&security,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr));
    Handle input(CreateFileW(L"NUL",GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE,&security,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr));
    Require(log.valid()&&input.valid(),L"无法建立启动日志。");
    WriteUtf8(log.value,L"Explicit user-approved Liquid Glass launch; no old run/fuse is reset.\r\n");FlushFileBuffers(log.value);
    auto powerShell=Path(L"powershell\\pwsh.exe");auto script=Path(L"guard\\r128-human-review-root\\Invoke-R109GraphBaselineOuter.ps1");
    // The root selects the unique DWM in this session through CIM, then pins
    // PID, session and exact creation time for every monitor/child. Do not use
    // ProcessIdToSessionId on protected DWM processes as a duplicate preflight.
    std::wstring command=Quote(powerShell)+L" -NoLogo -NoProfile -NonInteractive -File "+Quote(script)+
        L" -HumanReview -ExpectedDwmSessionId "+std::to_wstring(session);
    STARTUPINFOW startup{};startup.cb=sizeof(startup);startup.dwFlags=STARTF_USESHOWWINDOW|STARTF_USESTDHANDLES;
    startup.wShowWindow=SW_HIDE;startup.hStdOutput=log.value;startup.hStdError=log.value;startup.hStdInput=input.value;
    PROCESS_INFORMATION process{};
    Require(CreateProcessW(powerShell.c_str(),command.data(),nullptr,nullptr,TRUE,CREATE_NO_WINDOW,nullptr,root.c_str(),&startup,&process)!=FALSE,L"受监控启动链无法运行。");
    Handle child(process.hProcess);Handle thread(process.hThread);
    Require(WaitForSingleObject(child.value,INFINITE)==WAIT_OBJECT_0,L"启动链退出状态不可确认。");
    DWORD code{};Require(GetExitCodeProcess(child.value,&code)!=FALSE&&code!=STILL_ACTIVE,L"启动链退出状态不可确认。");
    if(code!=0){std::wstring message=L"液态玻璃启动或故障监测未通过，已停止；未切换为普通材质。\n\n诊断日志：\n"+logPath;
        MessageBoxW(nullptr,message.c_str(),L"GlassDesk 液态玻璃",MB_OK|MB_ICONERROR);}
    return code==0?0:1;
}
}
int WINAPI wWinMain(HINSTANCE,HINSTANCE,PWSTR,int){
    try{
        std::vector<wchar_t> path(32768);DWORD length=GetModuleFileNameW(nullptr,path.data(),DWORD(path.size()));
        Require(length>0&&length<path.size(),L"无法定位应用目录。");root.assign(path.data(),length);
        auto separator=root.find_last_of(L"\\/");Require(separator!=std::wstring::npos,L"应用目录无效。");root.resize(separator);
        int count{};auto args=CommandLineToArgvW(GetCommandLineW(),&count);Require(args!=nullptr,L"无法读取启动参数。");
        bool check=count==2&&wcscmp(args[1],L"--check-package")==0;checkMode=check;
        bool normal=count==1;LocalFree(args);Require(check||normal,L"不支持此启动参数。");
        if(check){CheckPackage();WriteUtf8(GetStdHandle(STD_OUTPUT_HANDLE),L"package-check: PASS (no app/monitor/graphics started)\n");return 0;}
        return Launch();
    }catch(std::wstring const& error){if(checkMode)WriteUtf8(GetStdHandle(STD_ERROR_HANDLE),error+L"\n");else MessageBoxW(nullptr,error.c_str(),L"GlassDesk 启动失败",MB_OK|MB_ICONERROR);return 1;}
    catch(...){if(checkMode)WriteUtf8(GetStdHandle(STD_ERROR_HANDLE),L"package-check: FAIL\n");else MessageBoxW(nullptr,L"启动器发生错误；未改用普通材质。",L"GlassDesk 启动失败",MB_OK|MB_ICONERROR);return 1;}
}
