#include <cstdio>
#include <stdexcept>
#include <string>
#include <vector>

extern "C" __declspec(dllimport) int FlBridge_CommandAlloc(const char*, char**);
extern "C" __declspec(dllimport) void FlBridge_FreeResponse(char*);
extern "C" __declspec(dllimport) int FlBridge_Command(const char*, char*, int);
extern "C" __declspec(dllimport) void BridgeStop();

namespace {
int checks = 0;
void expect(bool condition, const char* message)
{
    ++checks;
    if (!condition) throw std::runtime_error(message);
}

std::string command(const char* request)
{
    char* response = nullptr;
    int length = FlBridge_CommandAlloc(request, &response);
    expect(length >= 0 && (length == 0 || response), "owned response returned");
    std::string result(response ? response : "", static_cast<size_t>(length));
    FlBridge_FreeResponse(response);
    return result;
}
}

int main()
{
    int result = 0;
    try {
        expect(command("ping") == "pong", "owned export preserves protocol response");
        expect(command("resolve sym:MissingTestSymbol") == "err:unknown-sym:MissingTestSymbol",
               "symbol query reports unknown name");
        expect(command("resolve sym:DynArrayTypeInfo") == "err:unresolved:DynArrayTypeInfo",
               "symbol query refuses unloaded engine address");
        expect(command("resolve 123456") == "err:usage resolve sym:NAME", "symbol query requires a name");
        char* output = reinterpret_cast<char*>(1);
        expect(FlBridge_CommandAlloc(nullptr, &output) == -1 && output == nullptr,
               "invalid request clears response ownership");
        expect(FlBridge_CommandAlloc("ping", nullptr) == -1, "null response pointer refused");
        FlBridge_FreeResponse(nullptr);
        char legacy[4]{};
        expect(FlBridge_Command("ping", legacy, sizeof(legacy)) == 4 && std::string(legacy, 4) == "pong",
               "legacy export remains compatible");
        const std::string longName(70000, 'x');
        const std::string request = "resolve sym:" + longName;
        const std::string expected = "err:unknown-sym:" + longName;
        expect(command(request.c_str()) == expected, "owned response preserves reply larger than 64 KiB");
        std::vector<char> shortBuffer(65536);
        int fullLength = FlBridge_Command(request.c_str(), shortBuffer.data(), (int)shortBuffer.size());
        expect(fullLength == (int)expected.size() &&
               std::string(shortBuffer.data(), shortBuffer.size()) == expected.substr(0, shortBuffer.size()),
               "legacy response reports full size while copying only caller capacity");
        BridgeStop();
        expect(command("ping") == "err:bridge-stopping", "shutdown refuses further commands and hook installation");
        std::printf("Native owned-response transport: %d checks passed.\n", checks);
    } catch (const std::exception& e) {
        std::fprintf(stderr, "Native transport failed after %d checks: %s\n", checks, e.what());
        result = 1;
    }
    BridgeStop();
    return result;
}
