cmake_minimum_required(VERSION 3.20)
project(FlBridge CXX)

# MASM thunk (callthunk.asm) provides the XMM-capable call path.
enable_language(ASM_MASM)

# Native injected dev bridge for FruityLink. Build x64 to match FL Studio.
# sigscan.cpp = runtime signature-scanning subsystem (FL version portability; see sigscan.h).
add_library(FlBridge SHARED dllmain.cpp sigscan.cpp version_scanner.cpp delphi_classref.cpp
    native_window_host.cpp fl_window_factory.cpp mixer_track_insertion.cpp automation_clip.cpp callthunk.asm)

set_target_properties(FlBridge PROPERTIES
    OUTPUT_NAME "FlBridge"
    CXX_STANDARD 17
    CXX_STANDARD_REQUIRED ON)

target_compile_definitions(FlBridge PRIVATE _CRT_SECURE_NO_WARNINGS UNICODE _UNICODE)

# DEBUG-pipe gating: the external named-pipe server + flprobe plugin debug commands are compiled in
# ONLY when this is ON (dev/staging) OR when building the Debug config (_DEBUG, handled in source). A
# plain Release build with FRUITYLINK_DEBUG=OFF ships NO debug pipe. build-and-stage.ps1 turns this ON
# for the dev path; a production build leaves it OFF (the default).
option(FRUITYLINK_DEBUG "Compile the external debug named-pipe server + debug plugin commands" OFF)
if (FRUITYLINK_DEBUG)
    target_compile_definitions(FlBridge PRIVATE FRUITYLINK_DEBUG)
endif()

if (MSVC)
    target_compile_options(FlBridge PRIVATE "$<$<COMPILE_LANGUAGE:CXX>:/W3;/EHsc>")
endif()

include(CTest)
if (BUILD_TESTING)
    add_executable(FlBridgeAutomationTests tests/automation_clip_tests.cpp automation_clip.cpp)
    target_include_directories(FlBridgeAutomationTests PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
    target_compile_features(FlBridgeAutomationTests PRIVATE cxx_std_17)
    target_compile_definitions(FlBridgeAutomationTests PRIVATE UNICODE _UNICODE)
    if (MSVC)
        target_compile_options(FlBridgeAutomationTests PRIVATE /W3 /EHsc)
    endif()
    add_test(NAME native_automation_clips COMMAND FlBridgeAutomationTests)
    add_executable(FlBridgeMixerInsertionTests tests/mixer_track_insertion_tests.cpp mixer_track_insertion.cpp)
    target_include_directories(FlBridgeMixerInsertionTests PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
    target_compile_features(FlBridgeMixerInsertionTests PRIVATE cxx_std_17)
    target_compile_definitions(FlBridgeMixerInsertionTests PRIVATE UNICODE _UNICODE)
    if (MSVC)
        target_compile_options(FlBridgeMixerInsertionTests PRIVATE /W3 /EHsc)
    endif()
    add_test(NAME native_mixer_track_insertion COMMAND FlBridgeMixerInsertionTests)
    add_executable(FlBridgeWindowFactoryTests tests/fl_window_factory_tests.cpp fl_window_factory.cpp)
    target_include_directories(FlBridgeWindowFactoryTests PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
    target_compile_features(FlBridgeWindowFactoryTests PRIVATE cxx_std_17)
    target_compile_definitions(FlBridgeWindowFactoryTests PRIVATE UNICODE _UNICODE _CRT_SECURE_NO_WARNINGS)
    if (MSVC)
        target_compile_options(FlBridgeWindowFactoryTests PRIVATE /W3 /EHsc)
    endif()
    add_test(NAME native_window_factory_geometry COMMAND FlBridgeWindowFactoryTests)
    set_tests_properties(native_window_factory_geometry PROPERTIES TIMEOUT 30)
    add_executable(FlBridgeNativeWindowTests tests/native_window_host_tests.cpp native_window_host.cpp)
    target_include_directories(FlBridgeNativeWindowTests PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
    target_compile_features(FlBridgeNativeWindowTests PRIVATE cxx_std_17)
    target_compile_definitions(FlBridgeNativeWindowTests PRIVATE UNICODE _UNICODE)
    add_test(NAME native_window_registry COMMAND FlBridgeNativeWindowTests)
    set_tests_properties(native_window_registry PROPERTIES TIMEOUT 30)
    add_executable(FlBridgeSigscanTests tests/sigscan_tests.cpp tests/inspect_engine.cpp
        tests/delphi_classref_tests.cpp sigscan.cpp version_scanner.cpp delphi_classref.cpp)
    target_include_directories(FlBridgeSigscanTests PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
    target_compile_features(FlBridgeSigscanTests PRIVATE cxx_std_17)
    target_compile_definitions(FlBridgeSigscanTests PRIVATE _CRT_SECURE_NO_WARNINGS UNICODE _UNICODE FRUITYLINK_SCANNER_TESTS)
    if (MSVC)
        target_compile_options(FlBridgeSigscanTests PRIVATE /W3 /EHsc)
    endif()
    add_test(NAME native_signature_resolution COMMAND FlBridgeSigscanTests)
    add_test(NAME native_managed_symbol_coverage COMMAND ${CMAKE_COMMAND}
        -DBRIDGE_SOURCE=${CMAKE_CURRENT_SOURCE_DIR} -P ${CMAKE_CURRENT_SOURCE_DIR}/tests/check_symbol_coverage.cmake)
    add_executable(FlBridgeTransportTests tests/transport_tests.cpp)
    target_compile_features(FlBridgeTransportTests PRIVATE cxx_std_17)
    target_link_libraries(FlBridgeTransportTests PRIVATE FlBridge)
    add_test(NAME native_owned_response_transport COMMAND FlBridgeTransportTests)
    add_executable(FlBridgeWindowDispatchTests tests/window_dispatch_tests.cpp)
    target_compile_features(FlBridgeWindowDispatchTests PRIVATE cxx_std_17)
    target_link_libraries(FlBridgeWindowDispatchTests PRIVATE FlBridge)
    add_test(NAME native_window_dispatch_concurrency COMMAND FlBridgeWindowDispatchTests)
    set_tests_properties(native_window_dispatch_concurrency PROPERTIES TIMEOUT 30)
endif()
