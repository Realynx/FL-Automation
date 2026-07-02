cmake_minimum_required(VERSION 3.20)
project(FruityLinkBootstrap)

# FruityLink in-process bootstrap chain (replaces DLL injection):
#   VersionProxy -> version.dll  (transparent forwarder, dropped into FL's program dir)
#   CLRHost      -> FlClrHost.dll (CoreCLR host that boots FruityLink.Host.dll in-process)
#
# Build (x64, to match FL Studio):
#   cmake -S bootstrap -B bootstrap/build -A x64
#   cmake --build bootstrap/build --config Release
# Outputs:
#   bootstrap/build/VersionProxy/Release/version.dll
#   bootstrap/build/CLRHost/Release/FlClrHost.dll

add_subdirectory(VersionProxy)
add_subdirectory(CLRHost)
