cmake_minimum_required(VERSION 3.20)
project(FlClrHost CXX)

# Locate libnethost.lib (static) + the CLR host headers in the installed .NET host pack.
# We prefer the static lib so no nethost.dll has to be shipped into FL's directory.
set(_packroot "$ENV{ProgramFiles}/dotnet/packs/Microsoft.NETCore.App.Host.win-x64")
file(GLOB _packvers LIST_DIRECTORIES true "${_packroot}/*")
set(NETHOST_LIB "")
# Pick the highest version directory that contains libnethost.lib.
list(SORT _packvers)
foreach(_v ${_packvers})
    if (EXISTS "${_v}/runtimes/win-x64/native/libnethost.lib")
        set(NETHOST_LIB "${_v}/runtimes/win-x64/native/libnethost.lib")
    endif()
endforeach()
if (NETHOST_LIB STREQUAL "")
    message(FATAL_ERROR "libnethost.lib not found under ${_packroot}. Install the .NET runtime/SDK.")
endif()
message(STATUS "Using libnethost: ${NETHOST_LIB}")

add_library(FlClrHost SHARED src/clrhost.cpp)

# libnethost.lib is built against the STATIC CRT (/MT). Match it or the link fails with LNK2038.
set_target_properties(FlClrHost PROPERTIES
    OUTPUT_NAME "FlClrHost"
    CXX_STANDARD 17
    CXX_STANDARD_REQUIRED ON
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# NETHOST_USE_AS_STATIC: consume libnethost as a static lib (no __declspec(dllimport)).
target_compile_definitions(FlClrHost PRIVATE NETHOST_USE_AS_STATIC _CRT_SECURE_NO_WARNINGS WIN32_LEAN_AND_MEAN)
target_include_directories(FlClrHost PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/src")
target_link_libraries(FlClrHost PRIVATE "${NETHOST_LIB}")

if (MSVC)
    target_compile_options(FlClrHost PRIVATE /W3 /EHsc)
endif()
