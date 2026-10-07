cmake_minimum_required(VERSION 3.20)
project(D3D12WarpInteropProbe LANGUAGES CXX)

if(NOT WIN32)
    message(FATAL_ERROR "D3D12WarpInteropProbe is Windows-only")
endif()

add_executable(d3d12_warp_interop_probe d3d12_warp_interop_probe.cpp)
target_compile_features(d3d12_warp_interop_probe PRIVATE cxx_std_17)
target_compile_definitions(d3d12_warp_interop_probe PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(d3d12_warp_interop_probe PRIVATE d3d12 dxgi d3d11)
