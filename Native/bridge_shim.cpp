// Compatibility dispatcher for source-including native tests. CMake selects platform sources directly.
#if defined(_WIN32)
#include "platform/windows/bridge_shim_windows.cpp"
#elif defined(__APPLE__)
#include "platform/macos/bridge_shim_macos.cpp"
#else
#include "platform/unsupported/bridge_shim_unsupported.cpp"
#endif
