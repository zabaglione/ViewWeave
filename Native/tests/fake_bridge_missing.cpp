#include <windows.h>

extern "C" __declspec(dllexport) bool initialize_bridge(const wchar_t*) { return true; }
