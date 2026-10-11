# Discover FXC consistently, including when a shader-only test target is the
# first caller and Visual Studio has not populated the Windows SDK environment.
function(lkg_find_fxc)
  if (NOT WIN32)
    message(FATAL_ERROR "The Windows SDK FXC compiler is available only on Windows")
  endif()

  if (LKG_FXC_EXECUTABLE AND NOT EXISTS "${LKG_FXC_EXECUTABLE}")
    message(FATAL_ERROR "LKG_FXC_EXECUTABLE was set but does not exist: ${LKG_FXC_EXECUTABLE}")
  endif()

  if (NOT LKG_FXC_EXECUTABLE)
    set(_lkg_fxc_hints)
    if (CMAKE_VS_WINDOWS_TARGET_PLATFORM_VERSION AND DEFINED ENV{WindowsSdkDir})
      list(APPEND _lkg_fxc_hints
        "$ENV{WindowsSdkDir}/bin/${CMAKE_VS_WINDOWS_TARGET_PLATFORM_VERSION}/x64"
        "$ENV{WindowsSdkDir}/bin/${CMAKE_VS_WINDOWS_TARGET_PLATFORM_VERSION}/x86")
    endif()
    if (DEFINED ENV{WindowsSdkDir} AND DEFINED ENV{WindowsSDKVersion})
      list(APPEND _lkg_fxc_hints
        "$ENV{WindowsSdkDir}/bin/$ENV{WindowsSDKVersion}/x64"
        "$ENV{WindowsSdkDir}/bin/$ENV{WindowsSDKVersion}/x86")
    endif()

    # Visual Studio generators do not always inherit WindowsSdkDir. Read the
    # Windows Kits root directly, then discover installed SDK versions rather
    # than maintaining a list of hardcoded releases.
    foreach(_lkg_registry_key
        "[HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows Kits\\Installed Roots;KitsRoot10]"
        "[HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows Kits\\Installed Roots;KitsRoot10]")
      get_filename_component(_lkg_kits_root "${_lkg_registry_key}" ABSOLUTE)
      if (EXISTS "${_lkg_kits_root}/bin")
        file(GLOB _lkg_fxc_dirs LIST_DIRECTORIES TRUE "${_lkg_kits_root}/bin/*")
        if (CMAKE_VS_WINDOWS_TARGET_PLATFORM_VERSION)
          list(INSERT _lkg_fxc_hints 0
            "${_lkg_kits_root}/bin/${CMAKE_VS_WINDOWS_TARGET_PLATFORM_VERSION}/x64"
            "${_lkg_kits_root}/bin/${CMAKE_VS_WINDOWS_TARGET_PLATFORM_VERSION}/x86")
        endif()
        foreach(_lkg_sdk_bin IN LISTS _lkg_fxc_dirs)
          list(APPEND _lkg_fxc_hints "${_lkg_sdk_bin}/x64" "${_lkg_sdk_bin}/x86")
        endforeach()
      endif()
    endforeach()

    find_program(LKG_FXC_EXECUTABLE
      NAMES fxc.exe fxc
      HINTS ${_lkg_fxc_hints})
  endif()
  if (NOT LKG_FXC_EXECUTABLE)
    message(FATAL_ERROR
      "FXC was not found. Install a Windows SDK containing fxc.exe, set LKG_FXC_EXECUTABLE explicitly, or set WindowsSdkDir.")
  endif()
endfunction()

# Attach the stable D3D11 input implementation and compile its shaders with
# the Windows SDK FXC compiler. Shader bytecode is embedded in generated
# headers; the runtime DLL has no D3DCompile dependency.
function(lkg_add_stable_sdk_input TARGET_NAME)
  lkg_find_fxc()

  set(shader_source "${CMAKE_CURRENT_FUNCTION_LIST_DIR}/platform/windows/stable_sdk_input_d3d11.hlsl")
  set(shader_output_dir "${CMAKE_CURRENT_BINARY_DIR}/${TARGET_NAME}_stable_sdk_input_shaders")
  set(vertex_header "${shader_output_dir}/stable_sdk_input_d3d11_vertex_shader.h")
  set(pixel_header "${shader_output_dir}/stable_sdk_input_d3d11_pixel_shader.h")

  add_custom_command(
    OUTPUT "${vertex_header}"
    COMMAND "${CMAKE_COMMAND}" -E make_directory "${shader_output_dir}"
    COMMAND "${LKG_FXC_EXECUTABLE}" /nologo /T vs_4_0 /E stable_sdk_input_vs
      /Fh "${vertex_header}" /Vn g_stable_sdk_input_vertex_shader "${shader_source}"
    DEPENDS "${shader_source}"
    VERBATIM)
  add_custom_command(
    OUTPUT "${pixel_header}"
    COMMAND "${CMAKE_COMMAND}" -E make_directory "${shader_output_dir}"
    COMMAND "${LKG_FXC_EXECUTABLE}" /nologo /T ps_4_0 /E stable_sdk_input_ps
      /Fh "${pixel_header}" /Vn g_stable_sdk_input_pixel_shader "${shader_source}"
    DEPENDS "${shader_source}"
    VERBATIM)

  target_sources(${TARGET_NAME} PRIVATE
    "${CMAKE_CURRENT_FUNCTION_LIST_DIR}/platform/windows/stable_sdk_input_d3d11.cpp"
    "${CMAKE_CURRENT_FUNCTION_LIST_DIR}/platform/windows/stable_sdk_input_d3d11.h"
    "${shader_source}"
    "${vertex_header}"
    "${pixel_header}")
  set_source_files_properties("${shader_source}" PROPERTIES HEADER_FILE_ONLY TRUE)
  target_include_directories(${TARGET_NAME} PRIVATE "${shader_output_dir}")
endfunction()

function(lkg_add_stable_sdk_input_test TARGET_NAME)
  if (NOT WIN32)
    message(FATAL_ERROR "The stable D3D11 SDK input test requires Windows WARP")
  endif()
  add_executable(${TARGET_NAME}
    "${CMAKE_CURRENT_FUNCTION_LIST_DIR}/tests/stable_sdk_input_d3d11_test.cpp")
  target_compile_features(${TARGET_NAME} PRIVATE cxx_std_17)
  target_compile_definitions(${TARGET_NAME} PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
  target_include_directories(${TARGET_NAME} PRIVATE "${CMAKE_CURRENT_FUNCTION_LIST_DIR}")
  target_link_libraries(${TARGET_NAME} PRIVATE d3d11 dxgi)
  lkg_add_stable_sdk_input(${TARGET_NAME})
  add_test(NAME ${TARGET_NAME} COMMAND ${TARGET_NAME})
  set_tests_properties(${TARGET_NAME} PROPERTIES TIMEOUT 60)
endfunction()

function(lkg_add_manual_subpixel_presenter_shaders TARGET_NAME)
  lkg_find_fxc()

  set(shader_source "${CMAKE_CURRENT_FUNCTION_LIST_DIR}/platform/windows/manual_subpixel_presenter.hlsl")
  set(shared_shader "${CMAKE_CURRENT_FUNCTION_LIST_DIR}/../Packages/com.zabaglione.lookingglass.viewweave.bridge/Runtime/Common/Resources/ViewWeaveSubpixelSampling.hlsl")
  if (NOT EXISTS "${shared_shader}")
    message(FATAL_ERROR "Shared subpixel shader source was not found: ${shared_shader}")
  endif()
  set(shader_output_dir "${CMAKE_CURRENT_BINARY_DIR}/${TARGET_NAME}_manual_subpixel_shaders")
  set(vertex_header "${shader_output_dir}/manual_subpixel_presenter_vertex_shader.h")
  set(pixel_header "${shader_output_dir}/manual_subpixel_presenter_pixel_shader.h")
  add_custom_command(
    OUTPUT "${vertex_header}"
    COMMAND "${CMAKE_COMMAND}" -E make_directory "${shader_output_dir}"
    COMMAND "${LKG_FXC_EXECUTABLE}" /nologo /T vs_4_0 /E ManualSubpixelVS
      /Fh "${vertex_header}" /Vn g_manual_subpixel_presenter_vertex_shader "${shader_source}"
    DEPENDS "${shader_source}" "${shared_shader}"
    VERBATIM)
  add_custom_command(
    OUTPUT "${pixel_header}"
    COMMAND "${CMAKE_COMMAND}" -E make_directory "${shader_output_dir}"
    COMMAND "${LKG_FXC_EXECUTABLE}" /nologo /T ps_4_0 /E ManualSubpixelPS
      /Fh "${pixel_header}" /Vn g_manual_subpixel_presenter_pixel_shader "${shader_source}"
    DEPENDS "${shader_source}" "${shared_shader}"
    VERBATIM)
  target_sources(${TARGET_NAME} PRIVATE "${vertex_header}" "${pixel_header}" "${shader_source}" "${shared_shader}")
  set_source_files_properties("${shader_source}" "${shared_shader}" PROPERTIES HEADER_FILE_ONLY TRUE)
  target_include_directories(${TARGET_NAME} PRIVATE "${shader_output_dir}")
endfunction()
