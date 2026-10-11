cmake_minimum_required(VERSION 3.20)
project(lookingglass_bridge_shim LANGUAGES CXX)

include(CTest)

if (WIN32)
  include("${CMAKE_CURRENT_SOURCE_DIR}/stable_sdk_input_d3d11.cmake")
endif()

if (APPLE)
  enable_language(OBJCXX)
endif()

option(LKG_TRANSPORT_TESTS_ONLY "Build only the stubbed worker transport test" OFF)
option(LKG_D3D12_SHARED_PROBE "Build the independent D3D12 to D3D11 shared resource probe" OFF)

set(LKG_COMMON_SOURCES common/bridge_worker_common.cpp common/bridge_metadata.cpp common/subpixel_calibration.cpp)
if (WIN32)
  set(LKG_TRANSPORT_SOURCE platform/windows/bridge_worker_transport_windows.cpp)
else()
  set(LKG_TRANSPORT_SOURCE platform/unsupported/bridge_worker_transport_unsupported.cpp)
endif()

if (NOT LKG_TRANSPORT_TESTS_ONLY)
  if (WIN32)
    add_library(lookingglass_bridge_shim SHARED
      platform/windows/bridge_shim_windows.cpp
      platform/windows/stdio_load_guard.cpp
      platform/windows/bridge_subpixel_metadata.cpp
      platform/windows/manual_subpixel_presenter.cpp
      ${LKG_TRANSPORT_SOURCE}
      ${LKG_COMMON_SOURCES})
  elseif(APPLE)
    add_library(lookingglass_bridge_shim SHARED
      platform/macos/bridge_shim_macos.cpp
      platform/unsupported/bridge_d3d_stubs.cpp
      ${LKG_TRANSPORT_SOURCE}
      ${LKG_COMMON_SOURCES}
      platform/macos/bridge_metal_api_contract.cpp
      platform/macos/bridge_metal_api.mm
      platform/macos/metal_submission_lifetime.cpp
      platform/macos/unity_metal_producer.mm
      platform/macos/metal_calibration.cpp
      platform/macos/metal_bridge_cells.mm
      platform/macos/metal_display.mm)
    target_sources(lookingglass_bridge_shim PRIVATE platform/macos/bridge_metadata_worker.cpp)
  else()
    add_library(lookingglass_bridge_shim SHARED
      platform/unsupported/bridge_shim_unsupported.cpp
      ${LKG_TRANSPORT_SOURCE}
      ${LKG_COMMON_SOURCES})
    target_include_directories(lookingglass_bridge_shim PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
  endif()
  target_compile_features(lookingglass_bridge_shim PRIVATE cxx_std_17)
  if (APPLE)
    set_target_properties(lookingglass_bridge_shim PROPERTIES
      OBJCXX_STANDARD 17
      OBJCXX_STANDARD_REQUIRED ON)
  endif()
  target_compile_definitions(lookingglass_bridge_shim PRIVATE LKG_BRIDGE_SHIM_EXPORTS)
  target_include_directories(lookingglass_bridge_shim PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
  if (WIN32)
    if (NOT DEFINED UNITY_PLUGIN_API_DIR OR UNITY_PLUGIN_API_DIR STREQUAL "")
      set(UNITY_PLUGIN_API_DIR "$ENV{UNITY_PLUGIN_API_DIR}")
    endif()
    if (UNITY_PLUGIN_API_DIR STREQUAL "")
      message(FATAL_ERROR "Set UNITY_PLUGIN_API_DIR to the Unity Editor PluginAPI directory")
    endif()
    if (NOT EXISTS "${UNITY_PLUGIN_API_DIR}/IUnityInterface.h")
      message(FATAL_ERROR "UNITY_PLUGIN_API_DIR is missing IUnityInterface.h: ${UNITY_PLUGIN_API_DIR}")
    endif()
    if (NOT EXISTS "${UNITY_PLUGIN_API_DIR}/IUnityGraphicsD3D11.h")
      message(FATAL_ERROR "UNITY_PLUGIN_API_DIR is missing IUnityGraphicsD3D11.h: ${UNITY_PLUGIN_API_DIR}")
    endif()
    if (NOT EXISTS "${UNITY_PLUGIN_API_DIR}/IUnityGraphicsD3D12.h")
      message(FATAL_ERROR "UNITY_PLUGIN_API_DIR is missing IUnityGraphicsD3D12.h: ${UNITY_PLUGIN_API_DIR}")
    endif()
    target_include_directories(lookingglass_bridge_shim PRIVATE "${UNITY_PLUGIN_API_DIR}")
    file(STRINGS "${UNITY_PLUGIN_API_DIR}/IUnityGraphicsD3D12.h" unity_d3d12_v8_declaration REGEX "IUnityGraphicsD3D12v8")
    if (unity_d3d12_v8_declaration)
      target_compile_definitions(lookingglass_bridge_shim PRIVATE LKG_UNITY_GRAPHICS_D3D12_V8)
    endif()
    target_compile_definitions(lookingglass_bridge_shim PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
    target_link_libraries(lookingglass_bridge_shim PRIVATE user32 d3d11 d3d12 dxgi winhttp)
    lkg_add_stable_sdk_input(lookingglass_bridge_shim)
    lkg_add_manual_subpixel_presenter_shaders(lookingglass_bridge_shim)
    if (BUILD_TESTING)
      add_library(lookingglass_bridge_worker_test_shim SHARED
        platform/windows/bridge_shim_windows.cpp
        platform/windows/stdio_load_guard.cpp
        platform/windows/bridge_subpixel_metadata.cpp
        platform/windows/manual_subpixel_presenter.cpp
        ${LKG_TRANSPORT_SOURCE}
        ${LKG_COMMON_SOURCES})
      set_target_properties(lookingglass_bridge_worker_test_shim PROPERTIES
        OUTPUT_NAME lookingglass_bridge_worker_test_shim)
      target_compile_features(lookingglass_bridge_worker_test_shim PRIVATE cxx_std_17)
      target_compile_definitions(lookingglass_bridge_worker_test_shim PRIVATE
        LKG_BRIDGE_SHIM_EXPORTS WIN32_LEAN_AND_MEAN NOMINMAX
        LKG_WORKER_TRANSPORT_TESTING LKG_SUBPIXEL_PRESENTER_TESTS)
      target_include_directories(lookingglass_bridge_worker_test_shim PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}" "${UNITY_PLUGIN_API_DIR}")
      file(STRINGS "${UNITY_PLUGIN_API_DIR}/IUnityGraphicsD3D12.h" unity_test_d3d12_v8_declaration REGEX "IUnityGraphicsD3D12v8")
      if (unity_test_d3d12_v8_declaration)
        target_compile_definitions(lookingglass_bridge_worker_test_shim PRIVATE LKG_UNITY_GRAPHICS_D3D12_V8)
      endif()
      target_link_libraries(lookingglass_bridge_worker_test_shim PRIVATE user32 d3d11 d3d12 dxgi winhttp)
      lkg_add_stable_sdk_input(lookingglass_bridge_worker_test_shim)
      lkg_add_manual_subpixel_presenter_shaders(lookingglass_bridge_worker_test_shim)
    endif()
  elseif(APPLE)
    if (NOT DEFINED UNITY_PLUGIN_API_DIR OR UNITY_PLUGIN_API_DIR STREQUAL "")
      set(UNITY_PLUGIN_API_DIR "$ENV{UNITY_PLUGIN_API_DIR}")
    endif()
    if (UNITY_PLUGIN_API_DIR STREQUAL "")
      message(FATAL_ERROR "Set UNITY_PLUGIN_API_DIR to a Unity PluginAPI header directory")
    endif()
    foreach(unity_header IUnityInterface.h IUnityGraphics.h IUnityGraphicsMetal.h)
      if (NOT EXISTS "${UNITY_PLUGIN_API_DIR}/${unity_header}")
        message(FATAL_ERROR "UNITY_PLUGIN_API_DIR is missing ${unity_header}: ${UNITY_PLUGIN_API_DIR}")
      endif()
    endforeach()
    target_include_directories(lookingglass_bridge_shim PRIVATE "${UNITY_PLUGIN_API_DIR}")
    set_source_files_properties(
      platform/macos/bridge_metal_api.mm
      platform/macos/unity_metal_producer.mm
      platform/macos/metal_display.mm
      PROPERTIES COMPILE_FLAGS "-fno-objc-arc")
    find_library(METAL_FRAMEWORK Metal REQUIRED)
    find_library(FOUNDATION_FRAMEWORK Foundation REQUIRED)
    find_library(IOSURFACE_FRAMEWORK IOSurface REQUIRED)
    find_library(COCOA_FRAMEWORK Cocoa REQUIRED)
    find_library(QUARTZCORE_FRAMEWORK QuartzCore REQUIRED)
    target_link_libraries(lookingglass_bridge_shim PRIVATE
      ${CMAKE_DL_LIBS}
      ${METAL_FRAMEWORK}
      ${FOUNDATION_FRAMEWORK}
      ${COCOA_FRAMEWORK}
      ${QUARTZCORE_FRAMEWORK}
      ${IOSURFACE_FRAMEWORK})
  endif()
endif()

if (WIN32 AND NOT LKG_TRANSPORT_TESTS_ONLY)
  add_executable(lookingglass_bridge_probe bridge_probe.cpp)
  target_include_directories(lookingglass_bridge_probe PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
  target_link_libraries(lookingglass_bridge_probe PRIVATE user32)
  add_executable(lookingglass_bridge_threaded_probe bridge_threaded_probe.cpp)
  target_compile_features(lookingglass_bridge_threaded_probe PRIVATE cxx_std_17)
  target_include_directories(lookingglass_bridge_threaded_probe PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
  target_compile_definitions(lookingglass_bridge_threaded_probe PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
  target_link_libraries(lookingglass_bridge_threaded_probe PRIVATE d3d11 dxgi user32)
  add_executable(lookingglass_bridge_worker_session_probe bridge_worker_session_probe.cpp)
  target_compile_features(lookingglass_bridge_worker_session_probe PRIVATE cxx_std_17)
  target_include_directories(lookingglass_bridge_worker_session_probe PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
  target_compile_definitions(lookingglass_bridge_worker_session_probe PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
  target_link_libraries(lookingglass_bridge_worker_session_probe PRIVATE d3d11 dxgi user32)
endif()

if (BUILD_TESTING)
  add_executable(lookingglass_worker_common_test
    common/bridge_worker_common.cpp
    tests/portable_common_test.cpp)
  target_compile_features(lookingglass_worker_common_test PRIVATE cxx_std_17)
  target_include_directories(lookingglass_worker_common_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
  add_test(NAME lookingglass_worker_common_test COMMAND lookingglass_worker_common_test)
  add_executable(lookingglass_metadata_test
    common/bridge_metadata.cpp tests/metadata_test.cpp)
  target_compile_features(lookingglass_metadata_test PRIVATE cxx_std_17)
  target_include_directories(lookingglass_metadata_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
  add_test(NAME lookingglass_metadata_test COMMAND lookingglass_metadata_test)

  add_executable(lookingglass_subpixel_calibration_test
    common/subpixel_calibration.cpp tests/subpixel_calibration_test.cpp)
  target_compile_features(lookingglass_subpixel_calibration_test PRIVATE cxx_std_17)
  target_include_directories(lookingglass_subpixel_calibration_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
  add_test(NAME lookingglass_subpixel_calibration_test COMMAND lookingglass_subpixel_calibration_test)

  add_executable(lookingglass_metal_calibration_test
    platform/macos/metal_calibration.cpp common/subpixel_calibration.cpp tests/metal_calibration_test.cpp)
  target_compile_features(lookingglass_metal_calibration_test PRIVATE cxx_std_17)
  target_include_directories(lookingglass_metal_calibration_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
  add_test(NAME lookingglass_metal_calibration_test COMMAND lookingglass_metal_calibration_test)

  if (NOT WIN32 AND NOT APPLE AND NOT LKG_TRANSPORT_TESTS_ONLY)
    add_executable(lookingglass_unsupported_shim_abi_test tests/unsupported_shim_abi_test.cpp)
    target_compile_features(lookingglass_unsupported_shim_abi_test PRIVATE cxx_std_17)
    target_include_directories(lookingglass_unsupported_shim_abi_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
    target_link_libraries(lookingglass_unsupported_shim_abi_test PRIVATE ${CMAKE_DL_LIBS})
    add_test(NAME lookingglass_unsupported_shim_abi_test
      COMMAND lookingglass_unsupported_shim_abi_test "$<TARGET_FILE:lookingglass_bridge_shim>")
  endif()

  if (APPLE)
    if (NOT LKG_TRANSPORT_TESTS_ONLY)
      add_library(lkg_fake_metal_metadata SHARED tests/fake_metal_metadata.cpp)
      add_library(lkg_fake_geometry_metadata SHARED tests/fake_geometry_metadata.cpp)
      add_executable(lookingglass_metadata_worker_macos_test tests/metadata_worker_macos_test.cpp)
      target_compile_features(lookingglass_metadata_worker_macos_test PRIVATE cxx_std_17)
      target_include_directories(lookingglass_metadata_worker_macos_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
      target_link_libraries(lookingglass_metadata_worker_macos_test PRIVATE ${CMAKE_DL_LIBS})
      add_test(NAME lookingglass_metadata_worker_macos_test COMMAND lookingglass_metadata_worker_macos_test
        "$<TARGET_FILE:lookingglass_bridge_shim>" "$<TARGET_FILE:lkg_fake_geometry_metadata>")
      add_test(NAME lookingglass_metadata_worker_macos_shutdown_test COMMAND lookingglass_metadata_worker_macos_test
        "$<TARGET_FILE:lookingglass_bridge_shim>" "$<TARGET_FILE:lkg_fake_geometry_metadata>" shutdown-failure)
      add_executable(lookingglass_metal_display_metadata_test tests/metal_display_metadata_test.cpp)
      target_compile_features(lookingglass_metal_display_metadata_test PRIVATE cxx_std_17)
      target_include_directories(lookingglass_metal_display_metadata_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
      target_link_libraries(lookingglass_metal_display_metadata_test PRIVATE ${CMAKE_DL_LIBS})
      add_test(NAME lookingglass_metal_display_metadata_test COMMAND lookingglass_metal_display_metadata_test
        "$<TARGET_FILE:lookingglass_bridge_shim>" "$<TARGET_FILE:lkg_fake_metal_metadata>")
      add_executable(lookingglass_metal_bridge_cells_test platform/macos/metal_bridge_cells.mm
        platform/macos/metal_calibration.cpp common/subpixel_calibration.cpp tests/metal_bridge_cells_test.mm)
      target_compile_features(lookingglass_metal_bridge_cells_test PRIVATE cxx_std_17)
      set_target_properties(lookingglass_metal_bridge_cells_test PROPERTIES OBJCXX_STANDARD 17 OBJCXX_STANDARD_REQUIRED ON)
      target_include_directories(lookingglass_metal_bridge_cells_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
      target_link_libraries(lookingglass_metal_bridge_cells_test PRIVATE "-framework Foundation")
      add_test(NAME lookingglass_metal_bridge_cells_test COMMAND lookingglass_metal_bridge_cells_test)
    endif()
    add_executable(lookingglass_metal_api_contract_test
      platform/macos/bridge_metal_api_contract.cpp
      platform/macos/metal_submission_lifetime.cpp
      tests/metal_api_contract_test.cpp)
    target_compile_features(lookingglass_metal_api_contract_test PRIVATE cxx_std_17)
    target_compile_features(lookingglass_metal_api_contract_test PRIVATE cxx_std_17)
    target_include_directories(lookingglass_metal_api_contract_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
    add_test(NAME lookingglass_metal_api_contract_test COMMAND lookingglass_metal_api_contract_test)

    add_executable(lookingglass_metal_submission_lifetime_test
      platform/macos/metal_submission_lifetime.cpp
      tests/metal_submission_lifetime_test.cpp)
    target_compile_features(lookingglass_metal_submission_lifetime_test PRIVATE cxx_std_17)
    target_include_directories(lookingglass_metal_submission_lifetime_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
    add_test(NAME lookingglass_metal_submission_lifetime_test COMMAND lookingglass_metal_submission_lifetime_test)
  endif()
endif()

if (WIN32 AND BUILD_TESTING)
  add_executable(lookingglass_bridge_subpixel_metadata_test
    platform/windows/bridge_subpixel_metadata.cpp
    common/subpixel_calibration.cpp
    tests/bridge_subpixel_metadata_test.cpp)
  target_compile_features(lookingglass_bridge_subpixel_metadata_test PRIVATE cxx_std_17)
  target_compile_definitions(lookingglass_bridge_subpixel_metadata_test PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
  target_include_directories(lookingglass_bridge_subpixel_metadata_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
  target_link_libraries(lookingglass_bridge_subpixel_metadata_test PRIVATE winhttp ws2_32)
  add_test(NAME lookingglass_bridge_subpixel_metadata_test COMMAND lookingglass_bridge_subpixel_metadata_test)
  set_tests_properties(lookingglass_bridge_subpixel_metadata_test PROPERTIES TIMEOUT 30)

  add_executable(lookingglass_manual_subpixel_presenter_test
    platform/windows/manual_subpixel_presenter.cpp
    common/subpixel_calibration.cpp
    tests/manual_subpixel_presenter_test.cpp)
  target_compile_features(lookingglass_manual_subpixel_presenter_test PRIVATE cxx_std_17)
  target_compile_definitions(lookingglass_manual_subpixel_presenter_test PRIVATE
    WIN32_LEAN_AND_MEAN NOMINMAX LKG_SUBPIXEL_PRESENTER_TESTS)
  file(SHA256 "${CMAKE_CURRENT_SOURCE_DIR}/../Packages/com.zabaglione.lookingglass.viewweave.bridge/Runtime/Common/Resources/ViewWeaveSubpixelSampling.hlsl"
    LKG_SUBPIXEL_COMMON_HLSL_SHA256)
  message(STATUS "Manual subpixel shared HLSL SHA256: ${LKG_SUBPIXEL_COMMON_HLSL_SHA256}")
  target_include_directories(lookingglass_manual_subpixel_presenter_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
  target_link_libraries(lookingglass_manual_subpixel_presenter_test PRIVATE d3d11 dxgi user32)
  lkg_add_manual_subpixel_presenter_shaders(lookingglass_manual_subpixel_presenter_test)
  add_test(NAME lookingglass_manual_subpixel_presenter_test COMMAND lookingglass_manual_subpixel_presenter_test)
  set_tests_properties(lookingglass_manual_subpixel_presenter_test PROPERTIES TIMEOUT 60)

  # External Unity AsyncGPUReadback captures are supplied explicitly; this is
  # a command-line GPU oracle and is intentionally not registered with CTest.
  add_executable(lookingglass_unity_quilt_orientation_oracle
    platform/windows/manual_subpixel_presenter.cpp
    common/subpixel_calibration.cpp
    tests/unity_quilt_orientation_oracle.cpp)
  target_compile_features(lookingglass_unity_quilt_orientation_oracle PRIVATE cxx_std_17)
  target_compile_definitions(lookingglass_unity_quilt_orientation_oracle PRIVATE
    WIN32_LEAN_AND_MEAN NOMINMAX _WIN32_WINNT=0x0A00 WINVER=0x0A00
    LKG_SUBPIXEL_PRESENTER_TESTS)
  target_include_directories(lookingglass_unity_quilt_orientation_oracle PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}")
  target_link_libraries(lookingglass_unity_quilt_orientation_oracle PRIVATE d3d11 dxgi user32)
  lkg_add_manual_subpixel_presenter_shaders(lookingglass_unity_quilt_orientation_oracle)

  if (NOT LKG_TRANSPORT_TESTS_ONLY)
    add_library(lkg_fake_static_stdio_rebind SHARED tests/fake_static_stdio_rebind.cpp)
    target_compile_features(lkg_fake_static_stdio_rebind PRIVATE cxx_std_17)
    target_compile_definitions(lkg_fake_static_stdio_rebind PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
    if (MSVC)
      set_property(TARGET lkg_fake_static_stdio_rebind PROPERTY
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    endif()
    add_executable(lookingglass_stdio_load_guard_test
      platform/windows/stdio_load_guard.cpp
      tests/stdio_load_guard_test.cpp)
    target_compile_features(lookingglass_stdio_load_guard_test PRIVATE cxx_std_17)
    target_compile_definitions(lookingglass_stdio_load_guard_test PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
    target_include_directories(lookingglass_stdio_load_guard_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
    add_dependencies(lookingglass_stdio_load_guard_test lookingglass_bridge_shim lkg_fake_static_stdio_rebind)
    add_test(NAME lookingglass_stdio_load_guard_test
      COMMAND lookingglass_stdio_load_guard_test
        "$<TARGET_FILE:lkg_fake_static_stdio_rebind>"
        "$<TARGET_FILE:lookingglass_bridge_shim>")
    set_tests_properties(lookingglass_stdio_load_guard_test PROPERTIES TIMEOUT 15 RUN_SERIAL TRUE)
  endif()

  if (LKG_D3D12_SHARED_PROBE)
    add_executable(lookingglass_d3d12_d3d11_shared_probe tests/d3d12_d3d11_shared_probe.cpp)
    target_compile_features(lookingglass_d3d12_d3d11_shared_probe PRIVATE cxx_std_17)
    target_compile_definitions(lookingglass_d3d12_d3d11_shared_probe PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
    target_link_libraries(lookingglass_d3d12_d3d11_shared_probe PRIVATE d3d12 d3d11 dxgi)
    add_test(NAME lookingglass_d3d12_d3d11_shared_probe COMMAND lookingglass_d3d12_d3d11_shared_probe)
    set_tests_properties(lookingglass_d3d12_d3d11_shared_probe PROPERTIES TIMEOUT 10)
  endif()

  add_executable(lookingglass_worker_transport_test
    ${LKG_TRANSPORT_SOURCE}
    common/bridge_worker_common.cpp
    common/bridge_metadata.cpp
    common/subpixel_calibration.cpp
    platform/windows/bridge_subpixel_metadata.cpp
    platform/windows/manual_subpixel_presenter.cpp
    tests/worker_transport_test_hooks.h
    tests/worker_transport_test.cpp)
  target_compile_features(lookingglass_worker_transport_test PRIVATE cxx_std_17)
  target_include_directories(lookingglass_worker_transport_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
  target_compile_definitions(lookingglass_worker_transport_test PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX LKG_WORKER_TRANSPORT_TESTING LKG_SUBPIXEL_PRESENTER_TESTS)
  target_link_libraries(lookingglass_worker_transport_test PRIVATE d3d11 d3d12 dxgi user32 winhttp)
  lkg_add_stable_sdk_input(lookingglass_worker_transport_test)
  lkg_add_manual_subpixel_presenter_shaders(lookingglass_worker_transport_test)
  add_test(NAME lookingglass_worker_transport_test COMMAND lookingglass_worker_transport_test)
  set_tests_properties(lookingglass_worker_transport_test PROPERTIES TIMEOUT 60)
  add_test(NAME lookingglass_worker_cleanup_exception_test
    COMMAND lookingglass_worker_transport_test --cleanup-exception-only)
  set_tests_properties(lookingglass_worker_cleanup_exception_test PROPERTIES TIMEOUT 30)
  add_test(NAME lookingglass_worker_host_initialize_failure_test
    COMMAND lookingglass_worker_transport_test --host-initialize-failure-only)
  set_tests_properties(lookingglass_worker_host_initialize_failure_test PROPERTIES TIMEOUT 30)
  add_test(NAME lookingglass_worker_host_uninitialize_failure_test
    COMMAND lookingglass_worker_transport_test --host-uninitialize-failure-only)
  set_tests_properties(lookingglass_worker_host_uninitialize_failure_test PROPERTIES TIMEOUT 30)
  add_test(NAME lookingglass_persistent_d3d11_device_test
    COMMAND lookingglass_worker_transport_test --persistent-d3d11-device-only)
  set_tests_properties(lookingglass_persistent_d3d11_device_test PROPERTIES TIMEOUT 30)
  add_test(NAME lookingglass_worker_preview_pause_test
    COMMAND lookingglass_worker_transport_test --preview-pause-only)
  set_tests_properties(lookingglass_worker_preview_pause_test PROPERTIES TIMEOUT 30)
  add_test(NAME lookingglass_worker_preview_lease_test
    COMMAND lookingglass_worker_transport_test --preview-lease-only)
  set_tests_properties(lookingglass_worker_preview_lease_test PROPERTIES TIMEOUT 30)
  add_executable(lookingglass_bridge_static_present_test
    tests/bridge_static_present_test.cpp
    common/bridge_metadata.cpp
    platform/windows/stdio_load_guard.cpp)
  target_compile_features(lookingglass_bridge_static_present_test PRIVATE cxx_std_17)
  target_include_directories(lookingglass_bridge_static_present_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}"
    "${UNITY_PLUGIN_API_DIR}")
  target_compile_definitions(lookingglass_bridge_static_present_test PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
  target_link_libraries(lookingglass_bridge_static_present_test PRIVATE d3d11 dxgi user32)
  add_test(NAME lookingglass_bridge_static_present_test COMMAND lookingglass_bridge_static_present_test)
  set_tests_properties(lookingglass_bridge_static_present_test PROPERTIES TIMEOUT 10)
  lkg_add_stable_sdk_input_test(lookingglass_stable_sdk_input_d3d11_test)

  if (NOT LKG_TRANSPORT_TESTS_ONLY)
    add_library(lkg_fake_bridge_runtime SHARED tests/fake_bridge_runtime.cpp)
    add_library(lkg_fake_bridge_fault_runtime SHARED tests/fake_bridge_fault_runtime.cpp)
    add_library(lkg_fake_bridge_shutdown_runtime SHARED tests/fake_bridge_shutdown_runtime.cpp)
    add_library(lkg_fake_bridge_missing SHARED tests/fake_bridge_missing.cpp)
    target_compile_features(lkg_fake_bridge_shutdown_runtime PRIVATE cxx_std_17)
    target_include_directories(lkg_fake_bridge_shutdown_runtime PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
    target_compile_definitions(lkg_fake_bridge_fault_runtime PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
    target_compile_definitions(lkg_fake_bridge_shutdown_runtime PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
    target_link_libraries(lkg_fake_bridge_fault_runtime PRIVATE d3d11 dxgi)
    target_link_libraries(lkg_fake_bridge_shutdown_runtime PRIVATE d3d11 dxgi)
    add_executable(lookingglass_bridge_loader_lifecycle_test tests/bridge_loader_lifecycle_test.cpp)
    target_compile_features(lookingglass_bridge_loader_lifecycle_test PRIVATE cxx_std_17)
    target_compile_definitions(lookingglass_bridge_loader_lifecycle_test PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
    target_include_directories(lookingglass_bridge_loader_lifecycle_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
    target_link_libraries(lookingglass_bridge_loader_lifecycle_test PRIVATE user32)
    add_dependencies(lookingglass_bridge_loader_lifecycle_test lookingglass_bridge_shim lkg_fake_bridge_runtime lkg_fake_bridge_missing)
    add_custom_command(TARGET lkg_fake_bridge_runtime POST_BUILD
      COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "$<TARGET_FILE:lkg_fake_bridge_runtime>"
        "$<TARGET_FILE_DIR:lkg_fake_bridge_runtime>/lkg_fake_bridge_other.dll")
    add_test(NAME lookingglass_bridge_loader_lifecycle_test
      COMMAND lookingglass_bridge_loader_lifecycle_test
        "$<TARGET_FILE:lookingglass_bridge_shim>"
        "$<TARGET_FILE:lkg_fake_bridge_runtime>"
        "$<TARGET_FILE_DIR:lkg_fake_bridge_runtime>/lkg_fake_bridge_other.dll"
        "$<TARGET_FILE:lkg_fake_bridge_missing>")
    set_tests_properties(lookingglass_bridge_loader_lifecycle_test PROPERTIES TIMEOUT 10)

    add_executable(lookingglass_bridge_shutdown_contract_test tests/bridge_shutdown_contract_test.cpp)
    target_compile_features(lookingglass_bridge_shutdown_contract_test PRIVATE cxx_std_17)
    target_include_directories(lookingglass_bridge_shutdown_contract_test PRIVATE
      "${CMAKE_CURRENT_SOURCE_DIR}"
      "${UNITY_PLUGIN_API_DIR}")
    target_compile_definitions(lookingglass_bridge_shutdown_contract_test PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
    target_link_libraries(lookingglass_bridge_shutdown_contract_test PRIVATE d3d11 dxgi user32)
    add_dependencies(lookingglass_bridge_shutdown_contract_test
      lookingglass_bridge_shim lookingglass_bridge_worker_test_shim lkg_fake_bridge_shutdown_runtime)
    add_test(NAME lookingglass_bridge_shutdown_contract_test
      COMMAND lookingglass_bridge_shutdown_contract_test
        "$<TARGET_FILE:lookingglass_bridge_shim>"
        "$<TARGET_FILE:lkg_fake_bridge_shutdown_runtime>"
        "$<TARGET_FILE:lookingglass_bridge_worker_test_shim>")
    set_tests_properties(lookingglass_bridge_shutdown_contract_test PROPERTIES TIMEOUT 60 RUN_SERIAL TRUE)
    add_test(NAME lookingglass_bridge_shutdown_exception_contract_test
      COMMAND lookingglass_bridge_shutdown_contract_test
        "$<TARGET_FILE:lookingglass_bridge_shim>"
        "$<TARGET_FILE:lkg_fake_bridge_shutdown_runtime>"
        "$<TARGET_FILE:lookingglass_bridge_worker_test_shim>"
        "--uninitialize-exception")
    set_tests_properties(lookingglass_bridge_shutdown_exception_contract_test PROPERTIES TIMEOUT 60 RUN_SERIAL TRUE)
  endif()
endif()

