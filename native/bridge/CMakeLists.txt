cmake_minimum_required(VERSION 3.20)
project(FlBridge CXX)

# MASM thunk (callthunk.asm) provides the XMM-capable call path.
enable_language(ASM_MASM)

# Native injected dev bridge for FruityLink. Build x64 to match FL Studio.
# sigscan.cpp = runtime signature-scanning subsystem (FL version portability; see sigscan.h).
add_library(FlBridge SHARED dllmain.cpp sigscan.cpp callthunk.asm)

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
    target_compile_options(FlBridge PRIVATE /W3 /EHsc)
endif()
