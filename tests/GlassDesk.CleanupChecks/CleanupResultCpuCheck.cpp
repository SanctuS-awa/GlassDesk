#include <CleanupResult.h>
#include <iostream>
#include <stdexcept>

int main() {
    using gd::cleanup::Result;
    using gd::cleanup::AsyncOutcome;
    int checks = 0;
    auto check = [&](bool condition, char const* name) {
        ++checks;
        if (!condition) { std::cerr << "FAILED: " << name << '\n'; return false; }
        return true;
    };
    Result initial;
    if (!check(initial.Value() == S_OK, "empty cleanup succeeds")) return 1;
    initial.Record(S_FALSE);
    if (!check(initial.Value() == S_OK, "successful status does not become a fault")) return 2;
    initial.Record(E_ACCESSDENIED);
    initial.Record(E_FAIL);
    initial.Record(S_OK);
    if (!check(initial.Value() == E_ACCESSDENIED, "later failure/success never replaces first failure")) return 3;

    Result operations;
    int attempts = 0;
    int translations = 0;
    auto translate = [&]() noexcept { ++translations; return E_INVALIDARG; };
    operations.Attempt([&] { ++attempts; return E_POINTER; }, translate);
    operations.Attempt([&] { ++attempts; throw std::runtime_error("detach failed"); }, translate);
    operations.Attempt([&] { ++attempts; }, translate);
    if (!check(attempts == 3, "all cleanup steps execute after failure")) return 4;
    if (!check(translations == 1, "exceptions translated once")) return 5;
    if (!check(operations.Value() == E_POINTER, "exception does not hide earlier COM failure")) return 6;
    Result exceptionFirst;
    exceptionFirst.Attempt([] { throw std::runtime_error("detach failed"); }, translate);
    if (!check(exceptionFirst.Value() == E_INVALIDARG, "first thrown failure propagated")) return 7;
    for (int repeat = 0; repeat < 3; ++repeat) {
        Result repeated(exceptionFirst.Value());
        repeated.Record(S_OK);
        repeated.Record(E_FAIL);
        if (!check(repeated.Value() == E_INVALIDARG, "stored cleanup result survives repeated aggregation")) return 8;
    }

    Result completed;
    completed.RecordAsync(AsyncOutcome::completed);
    if (!check(completed.Value() == S_OK, "completed queue succeeds")) return 9;
    Result timeout;
    timeout.RecordAsync(AsyncOutcome::pending);
    if (!check(timeout.Value() == HRESULT_FROM_WIN32(ERROR_TIMEOUT), "unfinished queue is timeout")) return 10;
    Result canceled;
    canceled.RecordAsync(AsyncOutcome::canceled);
    if (!check(canceled.Value() == HRESULT_FROM_WIN32(ERROR_CANCELLED), "canceled queue is failure")) return 11;
    Result asynchronousError;
    asynchronousError.RecordAsync(AsyncOutcome::error, E_ACCESSDENIED);
    if (!check(asynchronousError.Value() == E_ACCESSDENIED, "async HRESULT preserved")) return 12;
    Result malformedError;
    malformedError.RecordAsync(AsyncOutcome::error, S_OK);
    if (!check(malformedError.Value() == E_FAIL, "async Error cannot masquerade as success")) return 13;
    Result previouslyFailed(E_POINTER);
    previouslyFailed.RecordAsync(AsyncOutcome::pending);
    previouslyFailed.RecordAsync(AsyncOutcome::canceled);
    if (!check(previouslyFailed.Value() == E_POINTER, "queue failures do not replace previous detach failure")) return 14;
    Result parent;
    parent.Record(exceptionFirst.Value());
    parent.Record(asynchronousError.Value());
    if (!check(parent.Value() == E_INVALIDARG, "nested shutdown preserves child first error")) return 15;
    static_assert(noexcept(initial.Record(S_OK)));
    static_assert(noexcept(initial.RecordAsync(AsyncOutcome::pending)));
    std::cout << checks << " CPU cleanup checks passed; no COM, GPU or window initialized.\n";
    return 0;
}
