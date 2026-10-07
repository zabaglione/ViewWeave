cmake_minimum_required(VERSION 3.20)
project(lookingglass_bridge_shim LANGUAGES CXX)

include(CTest)

if (APPLE)
  enable_language(OBJCXX)
endif()

option(LKG_TRANSPORT_TESTS_ONLY "Build only the stubbed worker transport test" OFF)
option(LKG_D3D12_SHARED_PROBE "Build the independent D3D12 to D3D11 shared resource probe" OFF)

if (NOT LKG_TRANSPORT_TESTS_ONLY)
  if (WIN32)
    add_library(lookingglass_bridge_shim SHARED bridge_shim.cpp bridge_worker_transport.cpp bridge_worker_common.cpp)
  elseif(APPLE)
    add_library(lookingglass_bridge_shim SHARED
      bridge_shim.cpp
      bridge_worker_transport.cpp
      bridge_worker_common.cpp
      platform/macos/bridge_metal_api_contract.cpp
      platform/macos/bridge_metal_api.mm
      platform/macos/metal_submission_lifetime.cpp
      platform/macos/unity_metal_producer.mm)
  else()
    add_library(lookingglass_bridge_shim SHARED
      platform/unsupported/bridge_shim_unsupported.cpp
      bridge_worker_transport.cpp
      bridge_worker_common.cpp)
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
    target_link_libraries(lookingglass_bridge_shim PRIVATE user32 d3d11 d3d12 dxgi)
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
    set_source_files_properties(bridge_shim.cpp PROPERTIES COMPILE_FLAGS "-fobjc-exceptions")
    set_source_files_properties(
      platform/macos/bridge_metal_api.mm
      platform/macos/unity_metal_producer.mm
      PROPERTIES COMPILE_FLAGS "-fno-objc-arc")
    find_library(METAL_FRAMEWORK Metal REQUIRED)
    find_library(FOUNDATION_FRAMEWORK Foundation REQUIRED)
    find_library(IOSURFACE_FRAMEWORK IOSurface REQUIRED)
    target_link_libraries(lookingglass_bridge_shim PRIVATE
      ${CMAKE_DL_LIBS}
      ${METAL_FRAMEWORK}
      ${FOUNDATION_FRAMEWORK}
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
    bridge_worker_common.cpp
    tests/portable_common_test.cpp)
  target_compile_features(lookingglass_worker_common_test PRIVATE cxx_std_17)
  target_include_directories(lookingglass_worker_common_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
  add_test(NAME lookingglass_worker_common_test COMMAND lookingglass_worker_common_test)

  if (NOT WIN32 AND NOT APPLE AND NOT LKG_TRANSPORT_TESTS_ONLY)
    add_executable(lookingglass_unsupported_shim_abi_test tests/unsupported_shim_abi_test.cpp)
    target_compile_features(lookingglass_unsupported_shim_abi_test PRIVATE cxx_std_17)
    target_link_libraries(lookingglass_unsupported_shim_abi_test PRIVATE ${CMAKE_DL_LIBS})
    add_test(NAME lookingglass_unsupported_shim_abi_test
      COMMAND lookingglass_unsupported_shim_abi_test "$<TARGET_FILE:lookingglass_bridge_shim>")
  endif()

  if (APPLE)
    add_executable(lookingglass_metal_api_contract_test
      platform/macos/bridge_metal_api_contract.cpp
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
  if (LKG_D3D12_SHARED_PROBE)
    add_executable(lookingglass_d3d12_d3d11_shared_probe tests/d3d12_d3d11_shared_probe.cpp)
    target_compile_features(lookingglass_d3d12_d3d11_shared_probe PRIVATE cxx_std_17)
    target_compile_definitions(lookingglass_d3d12_d3d11_shared_probe PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
    target_link_libraries(lookingglass_d3d12_d3d11_shared_probe PRIVATE d3d12 d3d11 dxgi)
    add_test(NAME lookingglass_d3d12_d3d11_shared_probe COMMAND lookingglass_d3d12_d3d11_shared_probe)
    set_tests_properties(lookingglass_d3d12_d3d11_shared_probe PROPERTIES TIMEOUT 10)
  endif()

  add_executable(lookingglass_worker_transport_test
    bridge_worker_transport.cpp
    bridge_worker_common.cpp
    tests/worker_transport_test.cpp)
  target_compile_features(lookingglass_worker_transport_test PRIVATE cxx_std_17)
  target_include_directories(lookingglass_worker_transport_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
  target_compile_definitions(lookingglass_worker_transport_test PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX LKG_WORKER_TRANSPORT_TESTING)
  target_link_libraries(lookingglass_worker_transport_test PRIVATE d3d11 d3d12 dxgi user32)
  add_test(NAME lookingglass_worker_transport_test COMMAND lookingglass_worker_transport_test)
  set_tests_properties(lookingglass_worker_transport_test PROPERTIES TIMEOUT 60)

  if (NOT LKG_TRANSPORT_TESTS_ONLY)
    add_library(lkg_fake_bridge_runtime SHARED tests/fake_bridge_runtime.cpp)
    add_library(lkg_fake_bridge_fault_runtime SHARED tests/fake_bridge_fault_runtime.cpp)
    add_library(lkg_fake_bridge_shutdown_runtime SHARED tests/fake_bridge_shutdown_runtime.cpp)
    add_library(lkg_fake_bridge_missing SHARED tests/fake_bridge_missing.cpp)
    target_compile_definitions(lkg_fake_bridge_fault_runtime PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
    target_compile_definitions(lkg_fake_bridge_shutdown_runtime PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
    target_link_libraries(lkg_fake_bridge_fault_runtime PRIVATE d3d11 dxgi)
    target_link_libraries(lkg_fake_bridge_shutdown_runtime PRIVATE d3d11 dxgi)
    add_executable(lookingglass_bridge_loader_lifecycle_test tests/bridge_loader_lifecycle_test.cpp)
    target_compile_features(lookingglass_bridge_loader_lifecycle_test PRIVATE cxx_std_17)
    target_compile_definitions(lookingglass_bridge_loader_lifecycle_test PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
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
    add_dependencies(lookingglass_bridge_shutdown_contract_test lookingglass_bridge_shim lkg_fake_bridge_shutdown_runtime)
    add_test(NAME lookingglass_bridge_shutdown_contract_test
      COMMAND lookingglass_bridge_shutdown_contract_test
        "$<TARGET_FILE:lookingglass_bridge_shim>"
        "$<TARGET_FILE:lkg_fake_bridge_shutdown_runtime>")
    set_tests_properties(lookingglass_bridge_shutdown_contract_test PROPERTIES TIMEOUT 60 RUN_SERIAL TRUE)
  endif()
endif()

