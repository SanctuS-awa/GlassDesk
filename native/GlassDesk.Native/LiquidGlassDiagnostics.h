#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <algorithm>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <iterator>
#include <mutex>
#include <string>
#include <thread>
#include <utility>

namespace gd::liquidglass::diagnostics
{

inline constexpr wchar_t kLiquidGlassPluginVersion[] = L"0.3";
inline constexpr wchar_t kLiquidGlassDllVersion[] = L"0.2.2";
inline constexpr wchar_t kLiquidGlassShaderVersion[] = L"0.3";

// A single worker owns the UTF-8 log file. Rendering only enqueues short,
// content-free records; no capture or Composition callback waits on disk IO.
class Writer final
{
public:
    Writer() noexcept
    {
        try { worker_ = std::thread([this] { Run(); }); workerAvailable_=true; }
        catch (...) { OutputDebugStringW(L"GlassDesk liquid-glass logger thread unavailable\n"); }
    }

    ~Writer()
    {
        {
            std::lock_guard<std::mutex> lock(mutex_);
            stopping_ = true;
        }
        ready_.notify_one();
        if (worker_.joinable()) worker_.join();
    }

    void Write(std::wstring const& line) noexcept
    {
        if(!workerAvailable_)
        {
            RecordFailure(line);
            return;
        }
        try
        {
            bool overflow=false;
            {
                std::lock_guard<std::mutex> lock(mutex_);
                if (queue_.size() >= 4096)
                {
                    overflow=true;
                }
                else queue_.push_back(line);
            }
            if(overflow){RecordFailure(line);return;}
            ready_.notify_one();
        }
        catch (...)
        {
            RecordFailure(line);
        }
    }

private:
    static constexpr uint64_t kMaxBytes = 2ull * 1024ull * 1024ull;
    std::mutex mutex_;
    std::condition_variable ready_;
    std::deque<std::wstring> queue_;
    std::deque<std::wstring> failedWrites_;
    std::thread worker_;
    bool stopping_{};
    bool workerAvailable_{};
    bool formatChecked_{};
    bool legacyEncodingBlocked_{};
    std::wstring path_;

    void RecordFailure(std::wstring const& line) noexcept
    {
        try
        {
            std::lock_guard<std::mutex> lock(mutex_);
            if(failedWrites_.size()>=128)failedWrites_.pop_front();
            failedWrites_.push_back(line);
        }
        catch(...){}
        OutputDebugStringW(line.c_str());
        OutputDebugStringW(L"\n");
    }

    bool ResolvePath()
    {
        if (!path_.empty()) return true;
        wchar_t local[MAX_PATH]{};
        const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", local, static_cast<DWORD>(std::size(local)));
        if (length == 0 || length >= std::size(local)) return false;
        path_ = local;
        path_ += L"\\GlassDesk\\logs\\liquid-glass.log";
        const size_t separator = path_.find_last_of(L'\\');
        const std::wstring directory = path_.substr(0, separator);
        const size_t parentSeparator = directory.find_last_of(L'\\');
        if (parentSeparator != std::wstring::npos)
            CreateDirectoryW(directory.substr(0, parentSeparator).c_str(), nullptr);
        CreateDirectoryW(directory.c_str(), nullptr);
        return true;
    }

    bool PreserveLegacyEncoding()
    {
        if (formatChecked_) return !legacyEncodingBlocked_;
        formatChecked_ = true;
        HANDLE file = CreateFileW(path_.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            const DWORD error=GetLastError();
            if(error==ERROR_FILE_NOT_FOUND||error==ERROR_PATH_NOT_FOUND)return true;
            legacyEncodingBlocked_=true;return false;
        }
        BYTE prefix[2]{};
        DWORD read{};
        const BOOL readOk = ReadFile(file, prefix, sizeof(prefix), &read, nullptr);
        CloseHandle(file);
        if (!readOk){legacyEncodingBlocked_=true;return false;}
        const bool utf16Bom=read>=2&&prefix[0]==0xff&&prefix[1]==0xfe;
        const bool utf16Ascii=read>=2&&prefix[1]==0&&prefix[0]>=0x20;
        if (!utf16Bom&&!utf16Ascii) return true;
        SYSTEMTIME now{};
        GetLocalTime(&now);
        wchar_t legacy[MAX_PATH]{};
        const int count = swprintf_s(legacy, std::size(legacy), L"%s.legacy-%04u%02u%02u-%02u%02u%02u",
            path_.c_str(), static_cast<unsigned>(now.wYear), static_cast<unsigned>(now.wMonth),
            static_cast<unsigned>(now.wDay), static_cast<unsigned>(now.wHour),
            static_cast<unsigned>(now.wMinute), static_cast<unsigned>(now.wSecond));
        if(count<=0||!MoveFileExW(path_.c_str(),legacy,MOVEFILE_REPLACE_EXISTING))
        {legacyEncodingBlocked_=true;return false;}
        return true;
    }

    bool RotateIfNeeded(size_t incoming)
    {
        WIN32_FILE_ATTRIBUTE_DATA info{};
        if (!GetFileAttributesExW(path_.c_str(), GetFileExInfoStandard, &info))
        {
            const DWORD error=GetLastError();
            return error==ERROR_FILE_NOT_FOUND||error==ERROR_PATH_NOT_FOUND;
        }
        const uint64_t size = (static_cast<uint64_t>(info.nFileSizeHigh) << 32) | info.nFileSizeLow;
        if (size + incoming <= kMaxBytes) return true;
        std::wstring one = path_ + L".1";
        std::wstring two = path_ + L".2";
        if(!DeleteFileW(two.c_str())&&GetLastError()!=ERROR_FILE_NOT_FOUND)return false;
        if(GetFileAttributesW(one.c_str())!=INVALID_FILE_ATTRIBUTES&&
            !MoveFileExW(one.c_str(),two.c_str(),MOVEFILE_REPLACE_EXISTING))return false;
        return MoveFileExW(path_.c_str(),one.c_str(),MOVEFILE_REPLACE_EXISTING)!=FALSE;
    }

    void Append(std::wstring const& wide)
    {
        if (!ResolvePath())
        {
            RecordFailure(wide);
            return;
        }
        if(!PreserveLegacyEncoding()){RecordFailure(wide);return;}
        std::wstring line = wide;
        line += L"\r\n";
        const int required = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, line.data(),
            static_cast<int>(line.size()), nullptr, 0, nullptr, nullptr);
        if (required <= 0)
        {
            RecordFailure(wide);
            return;
        }
        std::string utf8(static_cast<size_t>(required), '\0');
        if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, line.data(), static_cast<int>(line.size()),
            utf8.data(), required, nullptr, nullptr) != required)
        {
            RecordFailure(wide);
            return;
        }
        if(!RotateIfNeeded(utf8.size())){RecordFailure(wide);return;}
        HANDLE file = CreateFileW(path_.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            RecordFailure(wide);
            return;
        }
        DWORD written{};
        const BOOL ok = WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
        CloseHandle(file);
        if (!ok || static_cast<size_t>(written) != utf8.size())
        {
            RecordFailure(wide);
        }
    }

    void Run() noexcept
    {
        for (;;)
        {
            std::wstring line;
            {
                std::unique_lock<std::mutex> lock(mutex_);
                ready_.wait(lock, [this] { return stopping_ || !queue_.empty(); });
                if (queue_.empty() && stopping_) return;
                line = std::move(queue_.front());
                queue_.pop_front();
            }
            try { Append(line); }
            catch (...) { RecordFailure(line); }
        }
    }
};

inline Writer& LogWriter() noexcept
{
    static Writer writer;
    return writer;
}

inline void Log(wchar_t const* event, wchar_t const* fields = L"") noexcept
{
    static const ULONGLONG runStart=GetTickCount64();
    SYSTEMTIME now{};
    GetLocalTime(&now);
    wchar_t line[2048]{};
    const int length = swprintf_s(line, std::size(line),
        L"%04u-%02u-%02uT%02u:%02u:%02u.%03u+08:00 [Asia/Shanghai] runId=%08X-%llu event=%ls buildId=0.2.2 dllVersion=%ls shaderVersion=%ls pluginVersion=%ls %ls",
        static_cast<unsigned>(now.wYear),static_cast<unsigned>(now.wMonth),static_cast<unsigned>(now.wDay),
        static_cast<unsigned>(now.wHour),static_cast<unsigned>(now.wMinute),static_cast<unsigned>(now.wSecond),
        static_cast<unsigned>(now.wMilliseconds),static_cast<unsigned>(GetCurrentProcessId()),
        static_cast<unsigned long long>(runStart),event ? event : L"unknown", kLiquidGlassDllVersion,
        kLiquidGlassShaderVersion,kLiquidGlassPluginVersion,fields ? fields : L"");
    if (length <= 0) return;
    LogWriter().Write(line);
}

} // namespace gd::liquidglass::diagnostics
