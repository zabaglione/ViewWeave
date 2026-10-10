#include <windows.h>
#include <cstdio>

namespace {

class StaticStdioRebind {
public:
    StaticStdioRebind() {
        FILE* stream = nullptr;
        freopen_s(&stream, "NUL", "r", stdin);
        freopen_s(&stream, "NUL", "w", stdout);
        freopen_s(&stream, "NUL", "w", stderr);
    }
};

StaticStdioRebind g_staticStdioRebind;
volatile LONG g_fakeInitializeCount = 0;

}

extern "C" __declspec(dllexport) int fake_static_stdio_rebind_loaded() { return 1; }
extern "C" __declspec(dllexport) bool initialize_bridge(const wchar_t*) {
    InterlockedIncrement(&g_fakeInitializeCount);
    return TRUE;
}
extern "C" __declspec(dllexport) LONG fake_bridge_initialize_count() { return g_fakeInitializeCount; }
