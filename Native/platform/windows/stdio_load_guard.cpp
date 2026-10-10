#include "stdio_load_guard.h"

namespace lkg::windows_stdio {
namespace {

constexpr DWORD kStandardIds[] = {STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, STD_ERROR_HANDLE};

HANDLE defaultGetStdHandle(void*, DWORD id) { return ::GetStdHandle(id); }
BOOL defaultSetStdHandle(void*, DWORD id, HANDLE handle) { return ::SetStdHandle(id, handle); }
HMODULE defaultLoadLibrary(void*, LPCWSTR path) {
    return ::LoadLibraryExW(path, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
}
DWORD defaultGetLastError(void*) { return ::GetLastError(); }

}

LoadResult loadWithDetachedStandardHandles(LPCWSTR path, const LoadOperations* suppliedOperations) {
    LoadOperations defaults{};
    defaults.getStdHandle = defaultGetStdHandle;
    defaults.setStdHandle = defaultSetStdHandle;
    defaults.loadLibrary = defaultLoadLibrary;
    defaults.getLastError = defaultGetLastError;
    const LoadOperations& operations = suppliedOperations ? *suppliedOperations : defaults;
    LoadResult result{};
    if (!path || !*path || !operations.getStdHandle || !operations.setStdHandle ||
        !operations.loadLibrary || !operations.getLastError) {
        result.preparationError = ERROR_INVALID_PARAMETER;
        result.restored = true;
        return result;
    }

    HANDLE saved[3]{};
    bool changedOrUncertain[3]{};
    bool preparationFailed = false;
    bool loadAttempted = false;
    try {
        for (size_t index = 0; index < 3; ++index)
            saved[index] = operations.getStdHandle(operations.context, kStandardIds[index]);

        for (size_t index = 0; index < 3; ++index) {
            changedOrUncertain[index] = true;
            if (!operations.setStdHandle(operations.context, kStandardIds[index], nullptr)) {
                changedOrUncertain[index] = false;
                preparationFailed = true;
                result.preparationError = operations.getLastError(operations.context);
                break;
            }
        }
        if (!preparationFailed) {
            loadAttempted = true;
            result.module = operations.loadLibrary(operations.context, path);
            result.loaded = result.module != nullptr;
            if (!result.loaded) result.loadError = operations.getLastError(operations.context);
        }
    } catch (...) {
        if (loadAttempted) result.loadError = ERROR_UNHANDLED_EXCEPTION;
        else {
            preparationFailed = true;
            result.preparationError = ERROR_UNHANDLED_EXCEPTION;
        }
    }

    result.restored = true;
    for (size_t index = 0; index < 3; ++index) {
        if (!changedOrUncertain[index]) continue;
        try {
            if (!operations.setStdHandle(operations.context, kStandardIds[index], saved[index])) {
                if (!result.restorationError) result.restorationError = operations.getLastError(operations.context);
                result.restored = false;
            }
        } catch (...) {
            if (!result.restorationError) result.restorationError = ERROR_UNHANDLED_EXCEPTION;
            result.restored = false;
        }
    }
    return result;
}

}
