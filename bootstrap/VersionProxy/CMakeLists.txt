cmake_minimum_required(VERSION 3.20)
project(VersionProxy C ASM_MASM)

# Transparent version.dll proxy. Output name MUST be exactly "version.dll".
add_library(VersionProxy SHARED dllmain.c versionASMStubs.asm versionStubExports.h)

set_target_properties(VersionProxy PROPERTIES
    OUTPUT_NAME "version"
    PREFIX ""
    SUFFIX ".dll")

target_compile_definitions(VersionProxy PRIVATE _CRT_SECURE_NO_WARNINGS WIN32_LEAN_AND_MEAN)
target_link_libraries(VersionProxy PRIVATE kernel32 user32)

if (MSVC)
    target_compile_options(VersionProxy PRIVATE /W3)
endif()
