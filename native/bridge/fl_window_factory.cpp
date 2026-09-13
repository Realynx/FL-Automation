#include "fl_window_factory.h"
#include "version_scanner.h"
#include "delphi_string.h"
#include <algorithm>
#include <cstdio>

namespace {
void traceGeometry(const char* message) {
    wchar_t path[MAX_PATH]{};
    const DWORD length = GetTempPathW(MAX_PATH, path);
    if (!length || length > MAX_PATH - 32) return;
    wcscat_s(path, L"fruitylink-bridge.log");
    FILE* file = nullptr;
    if (_wfopen_s(&file, path, L"a") != 0 || !file) return;
    SYSTEMTIME time{};
    GetLocalTime(&time);
    std::fprintf(file, "[winhost pid=%lu %02u:%02u:%02u.%03u] %s\n", GetCurrentProcessId(),
        time.wHour, time.wMinute, time.wSecond, time.wMilliseconds, message);
    std::fclose(file);
}

thread_local HWND suppressedActivation = nullptr;
LRESULT CALLBACK activationHook(int code, WPARAM window, LPARAM detail) {
    if (code == HCBT_ACTIVATE && reinterpret_cast<HWND>(window) == suppressedActivation) return 1;
    return CallNextHookEx(nullptr, code, window, detail);
}
class ActivationGuard {
public:
    ActivationGuard(HWND window, bool suppress) : previous_(suppressedActivation) {
        if (!suppress) return;
        suppressedActivation = window;
        hook_ = SetWindowsHookExW(WH_CBT, activationHook, nullptr, GetCurrentThreadId());
    }
    ~ActivationGuard() { if (hook_) UnhookWindowsHookEx(hook_); suppressedActivation = previous_; }
    bool installed() const { return hook_ != nullptr; }
private:
    HWND previous_;
    HHOOK hook_ = nullptr;
};
bool readField(void* object, unsigned offset, void** value) {
    __try { *value = *reinterpret_cast<void**>(static_cast<char*>(object) + offset); return *value != nullptr; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
bool validClass(void* klass, unsigned size) {
    __try { return *reinterpret_cast<uint64_t*>(static_cast<char*>(klass) - 0x80) == size; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
bool setLimits(void* form, const FlWindowLayout& layout, int width, int height) {
    __try {
        auto limits = reinterpret_cast<int*>(static_cast<char*>(form) + layout.constraintsOffset);
        limits[0] = width; limits[1] = height; limits[2] = 20000; limits[3] = 20000;
        auto effective = reinterpret_cast<int*>(static_cast<char*>(form) + layout.effectiveMinOffset);
        effective[0] = width; effective[1] = height;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
void place(HWND window, HWND main, const NativeWindowSpec& spec, const RECT& content) {
    RECT client{}, outer{};
    GetClientRect(window, &client);
    GetWindowRect(window, &outer);
    MONITORINFO monitor{sizeof(monitor)};
    if (!GetMonitorInfoW(MonitorFromWindow(main, MONITOR_DEFAULTTONEAREST), &monitor)) return;
    int extraWidth = (outer.right - outer.left) - (content.right - content.left);
    int extraHeight = (outer.bottom - outer.top) - (content.bottom - content.top);
    int width = (std::min)(spec.width + extraWidth, int(monitor.rcWork.right - monitor.rcWork.left));
    int height = (std::min)(spec.height + extraHeight, int(monitor.rcWork.bottom - monitor.rcWork.top));
    RECT anchor = monitor.rcWork;
    if (IsWindow(main)) GetWindowRect(main, &anchor);
    const int cascade = int(spec.id % 5) * GetSystemMetrics(SM_CYCAPTION);
    int x = (anchor.left + anchor.right - width) / 2 + cascade;
    int y = (anchor.top + anchor.bottom - height) / 2 + cascade;
    x = (std::clamp)(x, int(monitor.rcWork.left), int(monitor.rcWork.right) - width);
    y = (std::clamp)(y, int(monitor.rcWork.top), int(monitor.rcWork.bottom) - height);
    SetWindowPos(window, nullptr, x, y, width, height, SWP_NOACTIVATE | SWP_NOZORDER);
}
}

bool FlWindowFactory::invoke(const char* symbol, std::initializer_list<ULONG_PTR> values, ULONG_PTR* result) {
    void* function = symbols_(symbol);
    if (!function || values.size() > 8) return false;
    ULONG_PTR args[8]{};
    std::copy(values.begin(), values.end(), args);
    bool ok = false;
    auto value = call_(function, args, (int)values.size(), &ok);
    if (result) *result = value;
    return ok;
}

const FlWindowLayout* FlWindowFactory::layout() const {
    return layoutProvider_ ? layoutProvider_() : sig_windowLayout();
}

bool FlWindowFactory::create(const NativeWindowSpec& spec, NativeWindowSurface& surface, std::string& reason) {
    const bool result = createCore(spec, surface, reason);
    RECT bounds{};
    GetClientRect(surface.content, &bounds);
    char log[640];
    sprintf_s(log, "create ok=%d id=%llx class=TFLBaseVectorForm host=%p content=%p hostStyle=0x%08lx contentStyle=0x%08lx client=%ldx%ld reason=%s",
        result ? 1 : 0, static_cast<unsigned long long>(spec.id), surface.host, surface.content,
        static_cast<unsigned long>(GetWindowLongPtrW(surface.host, GWL_STYLE)),
        static_cast<unsigned long>(GetWindowLongPtrW(surface.content, GWL_STYLE)),
        bounds.right, bounds.bottom, reason.c_str());
    traceGeometry(log);
    return result;
}

bool FlWindowFactory::createCore(const NativeWindowSpec& spec, NativeWindowSurface& surface, std::string& reason) {
    const auto layout = this->layout();
    auto klass = symbols_("NativeWindowClassRef");
    reason = "unverified-native-window-layout";
    if (!layout || !klass || !validClass(klass, layout->instanceSize)) return false;
    const char* required[] = {"FLui_CreateFormFromClassRef", "FLwp_SetButtonCaption",
        "FLui_ControlSetVisible", "FLwp_SetVisible", "FLui_WP_GetHandle", "FL_FreeObj", "FLwp_SetWindowState"};
    for (auto symbol : required) if (!symbols_(symbol)) { reason = "missing-native-window-symbol"; return false; }
    HMODULE pinned = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&setLimits), &pinned)) { reason = "bridge-lifetime-pin-failed"; return false; }
    reason = "native-form-construction-failed";
    if (!invoke("FLui_CreateFormFromClassRef", {reinterpret_cast<ULONG_PTR>(klass), reinterpret_cast<ULONG_PTR>(&surface.form)}) || !surface.form) return false;
    surface.className = "TFLBaseVectorForm";
    void* captionControl = nullptr;
    void* maximize = nullptr;
    if (!readField(surface.form, layout->captionOffset, &captionControl) ||
        !readField(surface.form, layout->maximizeOffset, &maximize)) { reason = "native-caption-missing"; return false; }
    DelphiString title(spec.caption);
    const auto form = reinterpret_cast<ULONG_PTR>(surface.form);
    const auto text = reinterpret_cast<ULONG_PTR>(title.data());
    if (!invoke("FLwp_SetButtonCaption", {form, text}) ||
        !invoke("FLwp_SetButtonCaption", {reinterpret_cast<ULONG_PTR>(captionControl), text}) ||
        !setLimits(surface.form, *layout, spec.minWidth, spec.minHeight) ||
        !invoke("FLui_ControlSetVisible", {reinterpret_cast<ULONG_PTR>(maximize), 1})) return false;
    ULONG_PTR handle = 0;
    if (!invoke("FLui_WP_GetHandle", {form}, &handle)) return false;
    surface.host = reinterpret_cast<HWND>(handle);
    if (!IsWindow(surface.host)) return false;
    // Preserve FL's styles: it paints its native caption/buttons through the parent DC.
    // The registry excludes only our foreign content from the parent paint region.
    // Keep VCL's Visible field and Windows visibility in agreement; creation never activates FL.
    if (!invoke("FLwp_SetVisible", {form, 0})) return false;
    RECT content{};
    if (!bounds(surface, content)) { reason = geometryFailure_; return false; }
    NativeWindowSpec scaled = spec;
    const UINT sourceDpi = GetDpiForWindow(spec.child), targetDpi = GetDpiForWindow(surface.host);
    if (sourceDpi && targetDpi) {
        scaled.width = MulDiv(spec.width, targetDpi, sourceDpi);
        scaled.height = MulDiv(spec.height, targetDpi, sourceDpi);
        scaled.minWidth = MulDiv(spec.minWidth, targetDpi, sourceDpi);
        scaled.minHeight = MulDiv(spec.minHeight, targetDpi, sourceDpi);
    }
    if (!minimum(surface, (std::max)(1, scaled.minWidth), (std::max)(1, scaled.minHeight))) {
        reason = geometryFailure_.empty() ? "native-size-constraints-failed" : geometryFailure_;
        return false;
    }
    place(surface.host, mainWindow_(), scaled, content);
    if (!bounds(surface, content)) { reason = geometryFailure_; return false; }
    surface.content = CreateWindowExW(0, L"STATIC", L"", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
        content.left, content.top, content.right - content.left, content.bottom - content.top,
        surface.host, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!surface.content) { reason = "content-window-construction-failed"; return false; }
    reason.clear();
    return true;
}

bool FlWindowFactory::nativeClientRect(const NativeWindowSurface& surface, RECT& rectangle) {
    const auto profile = layout();
    void* vtable = nullptr;
    void* method = nullptr;
    if (!profile || !readField(surface.form, 0, &vtable) ||
        !readField(vtable, profile->clientRectVtableOffset, &method)) return false;
    ULONG_PTR args[] = {reinterpret_cast<ULONG_PTR>(surface.form), reinterpret_cast<ULONG_PTR>(&rectangle)};
    bool ok = false;
    call_(method, args, 2, &ok);
    return ok;
}

bool FlWindowFactory::bounds(const NativeWindowSurface& surface, RECT& content) {
    const auto profile = layout();
    RECT client{}, inner{}, title{};
    ULONG_PTR captionHandle = 0;
    void* caption = nullptr;
    const char* failure = nullptr;
    if (!profile || !surface.form || !GetClientRect(surface.host, &client)) failure = "native-host-client-unavailable";
    else if (!nativeClientRect(surface, inner)) failure = "native-client-rectangle-call-failed";
    else if (!readField(surface.form, profile->captionOffset, &caption)) failure = "native-caption-object-unavailable";
    else if (!invoke("FLui_WP_GetHandle", {reinterpret_cast<ULONG_PTR>(caption)}, &captionHandle) ||
        !GetWindowRect(reinterpret_cast<HWND>(captionHandle), &title)) failure = "native-caption-handle-unavailable";
    else if (!IsChild(surface.host, reinterpret_cast<HWND>(captionHandle))) failure = "native-caption-not-child";
    else {
        SetLastError(ERROR_SUCCESS);
        const int mapped = MapWindowPoints(HWND_DESKTOP, surface.host, reinterpret_cast<POINT*>(&title), 2);
        if (!mapped && GetLastError() != ERROR_SUCCESS) failure = "native-caption-mapping-failed";
        else if (!IntersectRect(&content, &client, &inner)) failure = "native-client-rectangle-empty";
        else {
            content.top = (std::max)(content.top, title.bottom);
            if (title.bottom <= client.top || title.top >= client.bottom ||
                content.right <= content.left || content.bottom <= content.top) failure = "native-caption-content-empty";
        }
    }
    if (!failure) { geometryFailure_.clear(); return true; }
    geometryFailure_ = failure;
    char log[640];
    sprintf_s(log, "geometry failure=%s host=%p caption=%p client=[%ld,%ld,%ld,%ld] native=[%ld,%ld,%ld,%ld] title=[%ld,%ld,%ld,%ld]",
        failure, surface.host, reinterpret_cast<HWND>(captionHandle), client.left, client.top, client.right, client.bottom,
        inner.left, inner.top, inner.right, inner.bottom, title.left, title.top, title.right, title.bottom);
    traceGeometry(log);
    return false;
}

bool FlWindowFactory::visible(const NativeWindowSurface& surface, bool show, bool activate) {
    if (!IsWindow(surface.host)) return false;
    // FL's visibility setter may activate before returning. Veto that activation for quiet shows,
    // rather than trying to restore somebody else's foreground window afterwards.
    ActivationGuard activation(surface.host, show && !activate);
    if (show && !activate && !activation.installed()) return false;
    if (!invoke("FLwp_SetVisible", {reinterpret_cast<ULONG_PTR>(surface.form), show ? 1ULL : 0ULL})) return false;
    ShowWindow(surface.host, show ? (activate ? SW_SHOW : SW_SHOWNOACTIVATE) : SW_HIDE);
    if (show && activate) SetForegroundWindow(surface.host);
    return true;
}

bool FlWindowFactory::state(const NativeWindowSurface& surface, bool maximize) {
    const int state = maximize ? (IsZoomed(surface.host) ? 0 : 2) : 1;
    return invoke("FLwp_SetWindowState", {reinterpret_cast<ULONG_PTR>(surface.form), ULONG_PTR(state)});
}

bool FlWindowFactory::minimum(const NativeWindowSurface& surface, int width, int height) {
    const auto layout = this->layout();
    RECT content{}, client{};
    if (!layout || !bounds(surface, content) || !GetClientRect(surface.host, &client)) return false;
    return setLimits(surface.form, *layout, width + client.right - (content.right - content.left),
        height + client.bottom - (content.bottom - content.top));
}

bool FlWindowFactory::destroy(NativeWindowSurface& surface) {
    if (surface.destructionAttempted) return !IsWindow(surface.host);
    // Keep the HWND values local: WM_NCDESTROY can clear the registry's surface reentrantly.
    const HWND host = surface.host;
    if (surface.content && IsWindow(surface.content) && !DestroyWindow(surface.content)) return false;
    surface.destructionAttempted = true;
    if (surface.form && !invoke("FL_FreeObj", {reinterpret_cast<ULONG_PTR>(surface.form)})) return false;
    if (IsWindow(host)) return false;
    surface = {};
    return true;
}
