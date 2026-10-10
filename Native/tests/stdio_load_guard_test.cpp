#include <windows.h>
#include <array>
#include <cstdint>
#include <iostream>
#include <iterator>
#include <string>
#include "platform/windows/stdio_load_guard.h"

namespace {

constexpr DWORD kIds[] = {STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, STD_ERROR_HANDLE};

struct FakeOperations {
    std::array<HANDLE, 3> handles{};
    int setCalls{};
    int loadCalls{};
    int failSetCall{};
    DWORD lastError{};
    bool failLoad{};
    bool throwLoad{};
};

int index_for(DWORD id) {
    for (int index = 0; index < 3; ++index) if (kIds[index] == id) return index;
    return -1;
}

HANDLE fake_get(void* context, DWORD id) {
    auto& state = *static_cast<FakeOperations*>(context);
    const int index = index_for(id);
    return index < 0 ? INVALID_HANDLE_VALUE : state.handles[static_cast<size_t>(index)];
}

BOOL fake_set(void* context, DWORD id, HANDLE handle) {
    auto& state = *static_cast<FakeOperations*>(context);
    ++state.setCalls;
    if (state.setCalls == state.failSetCall) {
        state.lastError = ERROR_ACCESS_DENIED;
        return FALSE;
    }
    const int index = index_for(id);
    if (index < 0) {
        state.lastError = ERROR_INVALID_PARAMETER;
        return FALSE;
    }
    state.handles[static_cast<size_t>(index)] = handle;
    return TRUE;
}

HMODULE fake_load(void* context, LPCWSTR) {
    auto& state = *static_cast<FakeOperations*>(context);
    ++state.loadCalls;
    if (state.throwLoad) throw 1;
    if (state.failLoad) {
        state.lastError = ERROR_MOD_NOT_FOUND;
        return nullptr;
    }
    return reinterpret_cast<HMODULE>(static_cast<uintptr_t>(0x1234));
}

DWORD fake_last_error(void* context) { return static_cast<FakeOperations*>(context)->lastError; }

lkg::windows_stdio::LoadResult run_fake(FakeOperations& state) {
    const lkg::windows_stdio::LoadOperations operations{
        &state, fake_get, fake_set, fake_load, fake_last_error};
    return lkg::windows_stdio::loadWithDetachedStandardHandles(L"fake.dll", &operations);
}

bool check_seams() {
    {
        FakeOperations state{{reinterpret_cast<HANDLE>(1), nullptr, INVALID_HANDLE_VALUE}};
        const auto result = run_fake(state);
        if (!result.loaded || !result.restored || result.preparationError || result.restorationError ||
            state.loadCalls != 1 || state.setCalls != 6 || state.handles[0] != reinterpret_cast<HANDLE>(1) ||
            state.handles[1] != nullptr || state.handles[2] != INVALID_HANDLE_VALUE) return false;
    }
    {
        FakeOperations state{{reinterpret_cast<HANDLE>(1), reinterpret_cast<HANDLE>(2), reinterpret_cast<HANDLE>(3)}};
        state.failLoad = true;
        const auto result = run_fake(state);
        if (result.loaded || !result.restored || result.loadError != ERROR_MOD_NOT_FOUND ||
            state.loadCalls != 1 || state.setCalls != 6 || state.handles[0] != reinterpret_cast<HANDLE>(1) ||
            state.handles[1] != reinterpret_cast<HANDLE>(2) || state.handles[2] != reinterpret_cast<HANDLE>(3)) return false;
    }
    {
        FakeOperations state{{reinterpret_cast<HANDLE>(1), reinterpret_cast<HANDLE>(2), reinterpret_cast<HANDLE>(3)}};
        state.failSetCall = 2;
        const auto result = run_fake(state);
        if (result.loaded || result.preparationError != ERROR_ACCESS_DENIED || result.restorationError ||
            state.loadCalls != 0 || state.setCalls != 3 || state.handles[0] != reinterpret_cast<HANDLE>(1) ||
            state.handles[1] != reinterpret_cast<HANDLE>(2) || state.handles[2] != reinterpret_cast<HANDLE>(3)) return false;
    }
    {
        FakeOperations state{{reinterpret_cast<HANDLE>(1), reinterpret_cast<HANDLE>(2), reinterpret_cast<HANDLE>(3)}};
        state.failSetCall = 5;
        const auto result = run_fake(state);
        if (!result.loaded || result.restored || !result.restorationError || state.loadCalls != 1 ||
            state.setCalls != 6 || state.handles[0] != reinterpret_cast<HANDLE>(1) ||
            state.handles[1] != nullptr || state.handles[2] != reinterpret_cast<HANDLE>(3)) return false;
    }
    {
        FakeOperations state{{reinterpret_cast<HANDLE>(1), reinterpret_cast<HANDLE>(2), reinterpret_cast<HANDLE>(3)}};
        state.throwLoad = true;
        const auto result = run_fake(state);
        if (result.loaded || !result.restored || result.loadError != ERROR_UNHANDLED_EXCEPTION ||
            state.loadCalls != 1 || state.setCalls != 6 || state.handles[0] != reinterpret_cast<HANDLE>(1) ||
            state.handles[1] != reinterpret_cast<HANDLE>(2) || state.handles[2] != reinterpret_cast<HANDLE>(3)) return false;
    }
    return true;
}

std::wstring quote_arg(const std::wstring& value) {
    std::wstring quoted = L"\"";
    size_t slashes = 0;
    for (wchar_t c : value) {
        if (c == L'\\') { ++slashes; continue; }
        if (c == L'\"') quoted.append(slashes * 2 + 1, L'\\');
        else quoted.append(slashes, L'\\');
        slashes = 0;
        quoted.push_back(c);
    }
    quoted.append(slashes * 2, L'\\');
    quoted.push_back(L'\"');
    return quoted;
}

using CompareObjectHandlesFn = BOOL(WINAPI*)(HANDLE, HANDLE);

CompareObjectHandlesFn resolve_compare_handles() {
    HMODULE kernelbase = GetModuleHandleW(L"kernelbase.dll");
    return kernelbase ? reinterpret_cast<CompareObjectHandlesFn>(GetProcAddress(kernelbase, "CompareObjectHandles")) : nullptr;
}

bool read_expected(HANDLE reader, const char* expected, DWORD expectedLength, bool expectData) {
    char buffer[8]{};
    DWORD read = 0;
    const BOOL ok = ReadFile(reader, buffer, sizeof(buffer), &read, nullptr);
    if (!expectData) return !ok || read == 0;
    return ok && read == expectedLength && std::string(buffer, buffer + read) == std::string(expected, expected + expectedLength);
}

bool run_child(const std::wstring& mode, const std::wstring& fakePath, const std::wstring& shimPath) {
    wchar_t executable[32768]{};
    const DWORD length = GetModuleFileNameW(nullptr, executable, static_cast<DWORD>(std::size(executable)));
    if (!length || length >= std::size(executable)) return false;
    std::wstring command = quote_arg(std::wstring(executable, length)) + L" " + quote_arg(mode) + L" " + quote_arg(fakePath);
    if (!shimPath.empty()) command += L" " + quote_arg(shimPath);
    SECURITY_ATTRIBUTES security{sizeof(security), nullptr, TRUE};
    HANDLE readers[3]{};
    HANDLE writers[3]{};
    if (!CreatePipe(&readers[0], &writers[0], &security, 0) ||
        !CreatePipe(&readers[1], &writers[1], &security, 0) ||
        !CreatePipe(&readers[2], &writers[2], &security, 0)) return false;
    SetHandleInformation(writers[0], HANDLE_FLAG_INHERIT, 0);
    SetHandleInformation(readers[1], HANDLE_FLAG_INHERIT, 0);
    SetHandleInformation(readers[2], HANDLE_FLAG_INHERIT, 0);
    DWORD written = 0;
    if (!WriteFile(writers[0], "in", 2, &written, nullptr) || written != 2) return false;

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = readers[0];
    startup.hStdOutput = writers[1];
    startup.hStdError = writers[2];
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(executable, command.data(), nullptr, nullptr, TRUE, 0, nullptr, nullptr, &startup, &process)) return false;
    CloseHandle(readers[0]);
    CloseHandle(writers[1]);
    CloseHandle(writers[2]);
    CloseHandle(writers[0]);
    WaitForSingleObject(process.hProcess, INFINITE);
    DWORD exitCode = 1;
    GetExitCodeProcess(process.hProcess, &exitCode);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    const bool writesPipe = mode == L"--guarded" || mode == L"--missing-symbols";
    const bool outputOk = read_expected(readers[1], "out", 3, writesPipe) &&
        read_expected(readers[2], "err", 3, writesPipe);
    CloseHandle(readers[1]);
    CloseHandle(readers[2]);
    if (exitCode != 0) std::cerr << "stdio child failed mode=" << std::string(mode.begin(), mode.end()) << " exit=" << exitCode << "\n";
    if (!outputOk) std::cerr << "stdio child pipe sentinel mismatch mode=" << std::string(mode.begin(), mode.end()) << "\n";
    return exitCode == 0 && outputOk;
}

bool read_input_sentinel(HANDLE reader) {
    DWORD read = 0;
    char buffer[8]{};
    return ReadFile(reader, buffer, sizeof(buffer), &read, nullptr) && read == 2 && std::string(buffer, buffer + read) == "in";
}

int child_test(const wchar_t* mode, const wchar_t* fakePath, const wchar_t* shimPath = nullptr) {
    if (wcscmp(mode, L"--outside-resident") == 0) {
        if (!shimPath) return 50;
        HMODULE vendor = LoadLibraryExW(fakePath, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!vendor) return 51;
        HMODULE shim = LoadLibraryExW(shimPath, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!shim) return 52;
        using LoadFn = int32_t (*)(const char*);
        using InitializeFn = int32_t (*)(const char*);
        using LastErrorFn = int32_t (*)(char*, uint32_t);
        auto load = reinterpret_cast<LoadFn>(GetProcAddress(shim, "lkg_bridge_load"));
        auto initialize = reinterpret_cast<InitializeFn>(GetProcAddress(shim, "lkg_bridge_initialize"));
        auto lastError = reinterpret_cast<LastErrorFn>(GetProcAddress(shim, "lkg_bridge_last_error"));
        auto fakeInitCount = reinterpret_cast<LONG (*)()>(GetProcAddress(vendor, "fake_bridge_initialize_count"));
        if (!load || !initialize || !lastError || !fakeInitCount) return 53;
        char path[32768]{};
        const DWORD length = WideCharToMultiByte(CP_UTF8, 0, fakePath, -1, path, sizeof(path), nullptr, nullptr);
        if (!length) return 54;
        if (load(path) != -3 || initialize("outside-resident-test") != -1 || fakeInitCount() != 0) return 55;
        char error[512]{};
        lastError(error, sizeof(error));
        return std::string(error).find("refusing pre-resident vendor module") != std::string::npos ? 0 : 56;
    }
    if (wcscmp(mode, L"--missing-symbols") == 0) {
        if (!shimPath) return 57;
        HANDLE saved[3] = {GetStdHandle(STD_INPUT_HANDLE), GetStdHandle(STD_OUTPUT_HANDLE), GetStdHandle(STD_ERROR_HANDLE)};
        HANDLE held[3]{};
        auto compare = resolve_compare_handles();
        if (!compare) return 58;
        for (int index = 0; index < 3; ++index) {
            if (!saved[index] || saved[index] == INVALID_HANDLE_VALUE) return 59 + index;
            if (!DuplicateHandle(GetCurrentProcess(), saved[index], GetCurrentProcess(), &held[index], 0, FALSE, DUPLICATE_SAME_ACCESS)) return 62 + index;
            if (!compare(saved[index], held[index])) return 65 + index;
        }
        HMODULE shim = LoadLibraryExW(shimPath, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!shim) return 68;
        using LoadFn = int32_t (*)(const char*);
        using InitializeFn = int32_t (*)(const char*);
        auto load = reinterpret_cast<LoadFn>(GetProcAddress(shim, "lkg_bridge_load"));
        auto initialize = reinterpret_cast<InitializeFn>(GetProcAddress(shim, "lkg_bridge_initialize"));
        char path[32768]{};
        const DWORD length = WideCharToMultiByte(CP_UTF8, 0, fakePath, -1, path, sizeof(path), nullptr, nullptr);
        if (!load || !initialize || !length || load(path) != -4 || initialize("missing-symbol-test") != -1) return 69;
        for (int index = 0; index < 3; ++index) {
            if (!compare(saved[index], held[index]) || !compare(GetStdHandle(kIds[index]), held[index])) return 70 + index;
        }
        if (!read_input_sentinel(GetStdHandle(STD_INPUT_HANDLE))) return 73;
        DWORD written = 0;
        if (!WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), "out", 3, &written, nullptr) || written != 3) return 74;
        if (!WriteFile(GetStdHandle(STD_ERROR_HANDLE), "err", 3, &written, nullptr) || written != 3) return 75;
        return 0;
    }
    HANDLE saved[3] = {GetStdHandle(STD_INPUT_HANDLE), GetStdHandle(STD_OUTPUT_HANDLE), GetStdHandle(STD_ERROR_HANDLE)};
    HANDLE held[3]{};
    auto compare = resolve_compare_handles();
    if (!compare) return 30;
    for (int index = 0; index < 3; ++index) {
        if (!saved[index] || saved[index] == INVALID_HANDLE_VALUE) return 31 + index;
        if (!DuplicateHandle(GetCurrentProcess(), saved[index], GetCurrentProcess(), &held[index], 0, FALSE, DUPLICATE_SAME_ACCESS)) return 31 + index;
        if (!compare(saved[index], held[index])) return 34 + index;
    }

    HMODULE module = nullptr;
    if (wcscmp(mode, L"--baseline") == 0) {
        module = LoadLibraryExW(fakePath, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!module) return 37;
        for (int index = 0; index < 3; ++index) {
            if (compare(saved[index], held[index]) || compare(GetStdHandle(kIds[index]), held[index])) return 38 + index;
        }
        return 0;
    }
    if (wcscmp(mode, L"--guarded") != 0) return 39;
    const auto result = lkg::windows_stdio::loadWithDetachedStandardHandles(fakePath);
    if (!result.loaded || !result.restored || !result.module) return 42;
    for (int index = 0; index < 3; ++index) {
        if (!compare(saved[index], held[index]) || !compare(GetStdHandle(kIds[index]), held[index])) return 43 + index;
    }
    if (!read_input_sentinel(GetStdHandle(STD_INPUT_HANDLE))) return 46;
    DWORD written = 0;
    if (!WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), "out", 3, &written, nullptr) || written != 3) return 47;
    if (!WriteFile(GetStdHandle(STD_ERROR_HANDLE), "err", 3, &written, nullptr) || written != 3) return 48;
    return 0;
}

}

int wmain(int argc, wchar_t** argv) {
    if (argc == 3 && (wcscmp(argv[1], L"--baseline") == 0 || wcscmp(argv[1], L"--guarded") == 0))
        return child_test(argv[1], argv[2]);
    if (argc == 4 && (wcscmp(argv[1], L"--outside-resident") == 0 || wcscmp(argv[1], L"--missing-symbols") == 0))
        return child_test(argv[1], argv[2], argv[3]);
    if (argc != 3 || !check_seams()) return 1;
    wchar_t fullPath[32768]{};
    const DWORD length = GetFullPathNameW(argv[1], static_cast<DWORD>(std::size(fullPath)), fullPath, nullptr);
    if (!length || length >= std::size(fullPath)) return 2;
    const std::wstring fakePath(fullPath, length);
    wchar_t shimFullPath[32768]{};
    const DWORD shimLength = GetFullPathNameW(argv[2], static_cast<DWORD>(std::size(shimFullPath)), shimFullPath, nullptr);
    if (!shimLength || shimLength >= std::size(shimFullPath)) return 3;
    const std::wstring shimPath(shimFullPath, shimLength);
    if (!run_child(L"--baseline", fakePath, {}) || !run_child(L"--guarded", fakePath, {}) ||
        !run_child(L"--missing-symbols", fakePath, shimPath) ||
        !run_child(L"--outside-resident", fakePath, shimPath)) return 4;
    std::cout << "stdio_rebind_guard=passed\n";
    return 0;
}
