#pragma once
#include <windows.h>
#include <type_traits>
#include <utility>

namespace gd::cleanup {
enum class AsyncOutcome { completed, pending, canceled, error };

// No COM, queue, device or window operations. Used by every shutdown layer.
class Result final {
public:
    explicit Result(HRESULT previous = S_OK) noexcept
        : first(FAILED(previous) ? previous : S_OK) {}
    void Record(HRESULT status) noexcept {
        if (SUCCEEDED(first) && FAILED(status)) first = status;
    }
    HRESULT Value() const noexcept { return first; }

    template<class Operation, class ExceptionResult>
    void Attempt(Operation&& operation, ExceptionResult&& exceptionResult) noexcept {
        try {
            if constexpr (std::is_void_v<std::invoke_result_t<Operation>>) {
                std::forward<Operation>(operation)();
            } else {
                Record(std::forward<Operation>(operation)());
            }
        } catch (...) {
            Record(std::forward<ExceptionResult>(exceptionResult)());
        }
    }
    void RecordAsync(AsyncOutcome outcome, HRESULT error = S_OK) noexcept {
        switch (outcome) {
        case AsyncOutcome::completed: break;
        case AsyncOutcome::pending: Record(HRESULT_FROM_WIN32(ERROR_TIMEOUT)); break;
        case AsyncOutcome::canceled: Record(HRESULT_FROM_WIN32(ERROR_CANCELLED)); break;
        case AsyncOutcome::error: Record(FAILED(error) ? error : E_FAIL); break;
        }
    }
private:
    HRESULT first;
};
}
