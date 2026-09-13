#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <atomic>
#include <cstdio>
#include <future>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

extern "C" __declspec(dllimport) int FlBridge_CommandAlloc(const char*, char**);
extern "C" __declspec(dllimport) void FlBridge_FreeResponse(char*);
extern "C" __declspec(dllimport) void BridgeStop();

namespace {
constexpr UINT CreateMain = WM_APP + 10;
constexpr UINT FixturePing = WM_APP + 11;
constexpr UINT DestroyMain = WM_APP + 12;
std::atomic<HWND> mainWindow{nullptr};
HANDLE changed = nullptr;

void expect(bool condition, const char* message)
{
    if (!condition) throw std::runtime_error(message);
}

std::string command(const std::string& request)
{
    char* response = nullptr;
    int size = FlBridge_CommandAlloc(request.c_str(), &response);
    expect(size >= 0, "native command returned an invalid response size");
    std::string result(response ? response : "", static_cast<size_t>(size));
    FlBridge_FreeResponse(response);
    return result;
}

LRESULT CALLBACK fixtureProc(HWND window, UINT message, WPARAM w, LPARAM l)
{
    if (message == FixturePing) return 0x1234;
    if (message == WM_NCDESTROY && mainWindow == window) mainWindow = nullptr;
    return DefWindowProcW(window, message, w, l);
}

void windowThread(std::promise<DWORD> started)
{
    WNDCLASSW cls{};
    cls.hInstance = GetModuleHandleW(nullptr);
    cls.lpfnWndProc = fixtureProc;
    cls.lpszClassName = L"UnrelatedPluginWindow";
    RegisterClassW(&cls);
    // A titled decoy must never become the engine's dispatch target.
    HWND decoy = CreateWindowExW(0, cls.lpszClassName, L"Plugin startup", WS_OVERLAPPED,
        0, 0, 1, 1, nullptr, nullptr, cls.hInstance, nullptr);
    cls.lpszClassName = L"TFruityLoopsMainForm";
    RegisterClassW(&cls);
    started.set_value(GetCurrentThreadId());
    MSG message;
    while (GetMessageW(&message, nullptr, 0, 0) > 0) {
        if (message.message == CreateMain) {
            mainWindow = CreateWindowExW(0, cls.lpszClassName, L"", WS_OVERLAPPED,
                0, 0, 1, 1, nullptr, nullptr, cls.hInstance, nullptr);
            SetEvent(changed);
        } else if (message.message == DestroyMain) {
            DestroyWindow(mainWindow);
            SetEvent(changed); // destruction and WM_NCDESTROY forwarding have fully returned
        } else { TranslateMessage(&message); DispatchMessageW(&message); }
    }
    if (mainWindow) DestroyWindow(mainWindow);
    DestroyWindow(decoy);
}

ULONG_PTR returnInput(ULONG_PTR input) { return input; }

void createMain(DWORD thread)
{
    ResetEvent(changed);
    PostThreadMessageW(thread, CreateMain, 0, 0);
    expect(WaitForSingleObject(changed, 5000) == WAIT_OBJECT_0 && mainWindow, "hidden main window creation failed");
    expect(!IsWindowVisible(mainWindow), "fixture main window must remain hidden");
}

void checkConcurrentCalls(DWORD uiThread)
{
    std::atomic<int> failures{0};
    HANDLE begin = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    std::vector<std::thread> callers;
    for (int worker = 0; worker < 16; ++worker) callers.emplace_back([&, worker] {
        WaitForSingleObject(begin, INFINITE);
        for (int iteration = 0; iteration < 100; ++iteration) {
            unsigned long long value = (static_cast<unsigned long long>(worker + 1) << 32) | iteration;
            char request[128], expected[64];
            sprintf_s(request, "callabs %llx %llx", reinterpret_cast<unsigned long long>(&returnInput), value);
            sprintf_s(expected, "\"ret\":\"0x%llx\"", value);
            if (command(request).find(expected) == std::string::npos) ++failures;
            if (SendMessageW(mainWindow, FixturePing, 0, 0) != 0x1234) ++failures;
        }
    });
    SetEvent(begin);
    for (auto& caller : callers) caller.join();
    CloseHandle(begin);
    expect(failures == 0, "concurrent calls corrupted their arguments/results or original window procedure");
    std::string probe = command("apctest");
    expect(probe.find("\"ret\":" + std::to_string(uiThread)) != std::string::npos, "call did not execute on the main window's thread");
}
}

int main()
{
    int result = 0;
    changed = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    std::promise<DWORD> started;
    auto identity = started.get_future();
    std::thread ui(windowThread, std::move(started));
    DWORD thread = identity.get();
    try {
        expect(command("apctest") == "err:no-mainwindow", "unrelated startup window was selected");
        expect(command("fl_ready") == "0", "a loaded bridge was reported as initialized FL");
        createMain(thread);
        checkConcurrentCalls(thread);
        ResetEvent(changed);
        PostThreadMessageW(thread, DestroyMain, 0, 0);
        expect(WaitForSingleObject(changed, 5000) == WAIT_OBJECT_0, "window destruction did not complete");
        expect(command("apctest") == "err:no-mainwindow", "destroyed main-window handle was retained");
        createMain(thread);
        checkConcurrentCalls(thread);
        BridgeStop();
        expect(SendMessageW(mainWindow, FixturePing, 0, 0) == 0x1234, "original procedure was not restored on stop");
        std::puts("Native window dispatch: hidden-window discovery, 3200 concurrent calls, recreation and teardown passed.");
    } catch (const std::exception& error) { std::fprintf(stderr, "%s\n", error.what()); result = 1; }
    BridgeStop();
    PostThreadMessageW(thread, WM_QUIT, 0, 0);
    ui.join();
    CloseHandle(changed);
    return result;
}
