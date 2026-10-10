#pragma once

#include <windows.h>

namespace lkg::windows_stdio {

struct LoadOperations {
    void* context{};
    HANDLE (*getStdHandle)(void*, DWORD){};
    BOOL (*setStdHandle)(void*, DWORD, HANDLE){};
    HMODULE (*loadLibrary)(void*, LPCWSTR){};
    DWORD (*getLastError)(void*){};
};

struct LoadResult {
    HMODULE module{};
    DWORD preparationError{};
    DWORD loadError{};
    DWORD restorationError{};
    bool loaded{};
    bool restored{};
};

// Process standard handles are temporarily detached while a vendor DLL's
// private CRT static initializers run. Other threads can observe this window.
LoadResult loadWithDetachedStandardHandles(LPCWSTR path, const LoadOperations* operations = nullptr);

}
