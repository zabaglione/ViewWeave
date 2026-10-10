// Compatibility dispatcher. CMake selects the transport backend directly.
#if defined(_WIN32)
#include "platform/windows/bridge_worker_transport_windows.cpp"
#else
#include "platform/unsupported/bridge_worker_transport_unsupported.cpp"
#endif
