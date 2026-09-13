#include "native_window_host.h"
#include <commctrl.h>
#include <algorithm>
#include <iomanip>
#include <sstream>
#include <vector>

#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "user32.lib")

namespace {
constexpr UINT_PTR subclassId = 0x464C5748;
class ScopedPaintClip {
public:
    ScopedPaintClip(HDC dc, const RECT& bounds) : dc_(dc), saved_(SaveDC(dc)) {
        if (saved_ && ExcludeClipRect(dc_, bounds.left, bounds.top, bounds.right, bounds.bottom) == ERROR) {
            RestoreDC(dc_, saved_);
            saved_ = 0;
        }
    }
    ~ScopedPaintClip() { if (saved_) RestoreDC(dc_, saved_); }
private:
    HDC dc_;
    int saved_;
};
std::string failure(const char* reason, bool exists = false) {
    return std::string("{\"ok\":0,\"exists\":") + (exists ? "1" : "0") + ",\"reason\":\"" + reason + "\"}";
}
bool hex(const std::string& text, uint64_t& value) {
    if (text.empty() || text.size() > 18 || text[0] == '-' || text[0] == '+') return false;
    std::istringstream input(text);
    input >> std::hex >> value;
    return !input.fail() && input.eof();
}
bool number(const std::string& text, int& value) {
    std::istringstream input(text);
    input >> value;
    return !input.fail() && input.eof();
}
bool caption(const std::string& encoded, std::wstring& value) {
    if (encoded.empty() || encoded.size() > 2048 || encoded.size() % 2) return false;
    std::string utf8;
    for (size_t i = 0; i < encoded.size(); i += 2) {
        uint64_t byte = 0;
        if (!hex(encoded.substr(i, 2), byte) || !byte) return false;
        utf8.push_back(static_cast<char>(byte));
    }
    int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8.data(), (int)utf8.size(), nullptr, 0);
    if (length <= 0 || length > 256) return false;
    value.resize(length);
    return MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8.data(), (int)utf8.size(), value.data(), length) == length;
}
bool sameProcess(HWND window) {
    DWORD process = 0;
    return IsWindow(window) && GetWindowThreadProcessId(window, &process) && process == GetCurrentProcessId();
}
void positionIfChanged(HWND window, HWND parent, const RECT& desired, bool asynchronous) {
    RECT current{};
    if (!GetWindowRect(window, &current)) return;
    SetLastError(ERROR_SUCCESS);
    if (!MapWindowPoints(HWND_DESKTOP, parent, reinterpret_cast<POINT*>(&current), 2) &&
        GetLastError() != ERROR_SUCCESS) return;
    if (EqualRect(&current, &desired)) return;
    SetWindowPos(window, nullptr, desired.left, desired.top, desired.right - desired.left,
        desired.bottom - desired.top, SWP_NOACTIVATE | SWP_NOZORDER | (asynchronous ? SWP_ASYNCWINDOWPOS : 0));
}
bool parseCreate(const std::vector<std::string>& args, NativeWindowSpec& spec) {
    uint64_t child = 0;
    int show = 0;
    if (args.size() != 9 || !hex(args[1], spec.id) || !spec.id || !hex(args[2], child) || !child ||
        !number(args[3], show) || (show != 0 && show != 1)) return false;
    spec.child = reinterpret_cast<HWND>(child);
    spec.show = show != 0;
    if (!number(args[4], spec.width) || !number(args[5], spec.height) ||
        !number(args[6], spec.minWidth) || !number(args[7], spec.minHeight) || !caption(args[8], spec.caption)) return false;
    return spec.minWidth >= 1 && spec.minHeight >= 1 && spec.width >= spec.minWidth && spec.height >= spec.minHeight &&
        spec.width <= 20000 && spec.height <= 20000;
}
}

struct NativeWindowRegistry::Record {
    NativeWindowRegistry* owner;
    NativeWindowSpec spec;
    NativeWindowSurface surface;
    bool bound = false;
    bool legacy = false;
    bool releaseFailed = false;
    UINT requestedDpi = 96;
};

std::string NativeWindowRegistry::create(const NativeWindowSpec& spec, bool legacy) {
    if (records_.count(spec.id)) return failure("id-already-exists", true);
    if (!sameProcess(spec.child)) return failure("invalid-child-window");
    if (records_.size() >= 64) return failure("window-limit");
    for (const auto& item : records_) {
        if (item.second->spec.child == spec.child) return failure("child-already-reserved");
    }
    auto record = std::make_shared<Record>();
    record->owner = this;
    record->spec = spec;
    record->legacy = legacy;
    record->requestedDpi = GetDpiForWindow(spec.child);
    if (!record->requestedDpi) record->requestedDpi = 96;
    std::string reason;
    const bool created = factory_.create(spec, record->surface, reason);
    if (record->surface.host == spec.child || record->surface.content == spec.child)
        return failure("native-window-already-owned");
    for (const auto& existing : records_) {
        const auto& surface = existing.second->surface;
        if ((record->surface.host && (record->surface.host == surface.host || record->surface.host == surface.content)) ||
            (record->surface.content && (record->surface.content == surface.content || record->surface.content == surface.host)))
            return failure("native-window-already-owned"); // Never destroy a borrowed factory result.
    }
    if (!created) {
        const bool released = factory_.destroy(record->surface);
        if (!released) { record->releaseFailed = true; records_[spec.id] = record; }
        return failure(reason.empty() ? "native-create-failed" : reason.c_str(), !released);
    }
    if (GetWindowThreadProcessId(record->surface.host, nullptr) != GetCurrentThreadId() ||
        GetWindowThreadProcessId(record->surface.content, nullptr) != GetCurrentThreadId() ||
        GetParent(record->surface.content) != record->surface.host) {
        const bool released = factory_.destroy(record->surface);
        if (!released) { record->releaseFailed = true; records_[spec.id] = record; }
        return failure("invalid-native-window", !released);
    }
    records_[spec.id] = record;
    if (!SetWindowSubclass(record->surface.host, subclass, subclassId, reinterpret_cast<DWORD_PTR>(record.get()))) {
        const bool released = factory_.destroy(record->surface);
        if (released) records_.erase(spec.id);
        else record->releaseFailed = true;
        return failure("native-subclass-failed", !released);
    }
    fit(record); // Positions only our same-thread container. Child stays untouched until bind.
    if (legacy && spec.show) factory_.visible(record->surface, true, false);
    return status(spec.id);
}

std::string NativeWindowRegistry::bind(uint64_t id, HWND child) {
    const auto found = records_.find(id);
    if (found == records_.end()) return failure("unknown-window");
    const auto record = found->second;
    if (record->releaseFailed) return failure("native-release-failed", true);
    if (record->spec.child != child || !sameProcess(child) || GetParent(child) != record->surface.content)
        return failure("child-not-parented", true);
    record->bound = true;
    fit(record);
    if (record->spec.show) factory_.visible(record->surface, true, false);
    return status(id);
}

std::string NativeWindowRegistry::close(uint64_t id) {
    const auto found = records_.find(id);
    if (found == records_.end()) return "{\"ok\":1,\"exists\":0}";
    const auto record = found->second;
    if (IsWindow(record->spec.child) && GetParent(record->spec.child) == record->surface.content)
        return failure("child-still-parented", true);
    if (!factory_.destroy(record->surface)) {
        record->releaseFailed = true;
        return failure("native-destruction-unconfirmed", true);
    }
    records_.erase(id);
    return "{\"ok\":1,\"exists\":0}";
}

std::string NativeWindowRegistry::show(uint64_t id, bool visible, bool activate) {
    const auto found = records_.find(id);
    if (found == records_.end()) return failure("unknown-window");
    auto record = found->second;
    if (record->releaseFailed) return failure("native-release-failed", true);
    if (record->legacy && !record->bound && GetParent(record->spec.child) == record->surface.content) record->bound = true;
    if (visible && !record->bound) return failure("child-not-bound", true);
    fit(record);
    if (!factory_.visible(record->surface, visible, activate)) return failure("visibility-failed", true);
    return status(id);
}

std::string NativeWindowRegistry::status(uint64_t id) {
    const auto found = records_.find(id);
    if (found == records_.end()) return "{\"ok\":0,\"exists\":0,\"active\":0}";
    const auto record = found->second;
    RECT bounds{};
    GetClientRect(record->surface.content, &bounds);
    std::ostringstream json;
    json << "{\"ok\":1,\"exists\":1,\"id\":\"" << std::hex << id << "\",\"host\":\"0x"
        << reinterpret_cast<uintptr_t>(record->surface.host) << "\",\"content\":\"0x"
        << reinterpret_cast<uintptr_t>(record->surface.content) << "\",\"child\":\"0x"
        << reinterpret_cast<uintptr_t>(record->spec.child) << std::dec << "\",\"class\":\"" << record->surface.className
        << "\",\"active\":" << record->bound << ",\"visible\":" << (IsWindowVisible(record->surface.host) ? 1 : 0)
        << ",\"releaseFailed\":" << record->releaseFailed
        << ",\"cx\":0,\"cy\":0,\"cw\":" << bounds.right << ",\"ch\":" << bounds.bottom << "}";
    return json.str();
}

void NativeWindowRegistry::fit(const std::shared_ptr<Record>& record) {
    if (record->releaseFailed) return;
    RECT area{};
    if (!factory_.bounds(record->surface, area) || area.right <= area.left || area.bottom <= area.top) return;
    positionIfChanged(record->surface.content, record->surface.host, area, false);
    if (record->legacy && GetParent(record->spec.child) == record->surface.content) record->bound = true;
    if (!record->bound || !IsWindow(record->spec.child) || GetParent(record->spec.child) != record->surface.content) return;
    const RECT childArea{0, 0, area.right - area.left, area.bottom - area.top};
    positionIfChanged(record->spec.child, record->surface.content, childArea, true);
}

bool NativeWindowRegistry::foreignPaintBounds(const Record& record, RECT& bounds) const {
    const auto& surface = record.surface;
    if (!record.bound || record.releaseFailed || !IsWindow(record.spec.child) ||
        GetParent(record.spec.child) != surface.content || GetParent(surface.content) != surface.host) return false;
    if (!(GetWindowLongPtrW(surface.content, GWL_STYLE) & WS_VISIBLE) ||
        !(GetWindowLongPtrW(record.spec.child, GWL_STYLE) & WS_VISIBLE)) return false;
    RECT content{}, client{};
    if (!GetWindowRect(surface.content, &content) || !GetClientRect(surface.host, &client)) return false;
    SetLastError(ERROR_SUCCESS);
    if (!MapWindowPoints(HWND_DESKTOP, surface.host, reinterpret_cast<POINT*>(&content), 2) &&
        GetLastError() != ERROR_SUCCESS) return false;
    return IntersectRect(&bounds, &content, &client) != FALSE;
}

LRESULT NativeWindowRegistry::paint(const std::shared_ptr<Record>& record, HWND window, UINT message, WPARAM w, LPARAM l) {
    RECT bounds{};
    if (!foreignPaintBounds(*record, bounds)) return DefSubclassProc(window, message, w, l);
    if (w) {
        // FL's verified TWMPaint.DC path accepts an existing DC; WM_ERASEBKGND also supplies one.
        ScopedPaintClip clip(reinterpret_cast<HDC>(w), bounds);
        return DefSubclassProc(window, message, w, l);
    }
    if (message == WM_PAINT) {
        // QuickPaint captures GetUpdateRgn before BeginPaint for its buffered native controls.
        // Let FL own that lifecycle; validate only the area already owned by our foreign HWND.
        ValidateRect(window, &bounds);
    }
    return DefSubclassProc(window, message, w, l);
}

LRESULT CALLBACK NativeWindowRegistry::subclass(HWND window, UINT message, WPARAM w, LPARAM l, UINT_PTR, DWORD_PTR data) {
    auto raw = reinterpret_cast<Record*>(data);
    auto& registry = *raw->owner;
    const auto found = registry.records_.find(raw->spec.id);
    if (found == registry.records_.end()) return DefSubclassProc(window, message, w, l);
    const auto record = found->second; // Nested native destruction cannot invalidate this callback.
    if (message == WM_CLOSE || (message == WM_SYSCOMMAND && (w & 0xfff0) == SC_CLOSE)) {
        if (record->releaseFailed) ShowWindow(window, SW_HIDE);
        else registry.factory_.visible(record->surface, false, false);
        return 0;
    }
    if (message == WM_NCDESTROY) {
        RemoveWindowSubclass(window, subclass, subclassId);
        registry.records_.erase(record->spec.id);
        record->surface.host = record->surface.content = nullptr;
        return DefSubclassProc(window, message, w, l);
    }
    if (message == WM_PAINT || message == WM_ERASEBKGND) return registry.paint(record, window, message, w, l);
    const auto result = DefSubclassProc(window, message, w, l);
    if (!IsWindow(window) || record->releaseFailed) return result;
    if (message == WM_DPICHANGED) {
        const UINT dpi = GetDpiForWindow(window);
        registry.factory_.minimum(record->surface,
            (std::max)(1, MulDiv(record->spec.minWidth, dpi ? dpi : 96, record->requestedDpi)),
            (std::max)(1, MulDiv(record->spec.minHeight, dpi ? dpi : 96, record->requestedDpi)));
    }
    const bool sizeChanged = message == WM_WINDOWPOSCHANGED && l &&
        !(reinterpret_cast<WINDOWPOS*>(l)->flags & SWP_NOSIZE);
    if (message == WM_SIZE || sizeChanged || message == WM_DPICHANGED) registry.fit(record);
    if (message == WM_GETMINMAXINFO) {
        auto limits = reinterpret_cast<MINMAXINFO*>(l);
        RECT outer{}, client{}, content{};
        if (GetWindowRect(window, &outer) && GetClientRect(window, &client) && registry.factory_.bounds(record->surface, content)) {
            const int extraX = (outer.right - outer.left) - (content.right - content.left);
            const int extraY = (outer.bottom - outer.top) - (content.bottom - content.top);
            const UINT dpi = GetDpiForWindow(window);
            const int minimumWidth = MulDiv(record->spec.minWidth, dpi ? dpi : 96, record->requestedDpi);
            const int minimumHeight = MulDiv(record->spec.minHeight, dpi ? dpi : 96, record->requestedDpi);
            limits->ptMinTrackSize.x = (std::max)(limits->ptMinTrackSize.x, LONG(minimumWidth + extraX));
            limits->ptMinTrackSize.y = (std::max)(limits->ptMinTrackSize.y, LONG(minimumHeight + extraY));
        }
    }
    return result;
}

void NativeWindowRegistry::closeDetached() {
    std::vector<uint64_t> ids;
    for (const auto& entry : records_) ids.push_back(entry.first);
    for (auto id : ids) {
        show(id, false, false);
        close(id); // A bound child remains owned by its UI thread; never detach it here.
    }
}

std::string NativeWindowRegistry::execute(const std::string& command) {
    if (command.size() > 4096) return failure("command-too-long");
    std::istringstream stream(command);
    std::vector<std::string> args;
    for (std::string item; stream >> item;) args.push_back(item);
    if (args.empty()) return failure("invalid-command");
    if (args[0] == "winhost_create") {
        NativeWindowSpec spec;
        return parseCreate(args, spec) ? create(spec, false) : failure("invalid-create-arguments");
    }
    if (args[0] == "winhost_embed") {
        NativeWindowSpec spec;
        uint64_t child = 0;
        int visible = 1;
        if (args.size() < 2 || args.size() > 3 || !hex(args[1], child) ||
            (args.size() == 3 && (!number(args[2], visible) || visible < 0 || visible > 1))) return failure("invalid-embed-arguments");
        spec.child = reinterpret_cast<HWND>(child);
        spec.show = visible != 0;
        return create(spec, true);
    }
    if (args[0] == "winhost_list" && args.size() == 1) {
        std::string json = "{\"ok\":1,\"windows\":[";
        for (const auto& entry : records_) {
            if (json.back() != '[') json += ',';
            json += status(entry.first);
        }
        return json + "]}";
    }
    uint64_t id = 0;
    if (args[0] == "winhost_show") {
        size_t index = args.size() == 2 ? 1 : 2;
        int visible = 0, activate = 0;
        if (args.size() < 2 || args.size() > 4 || (index == 2 && !hex(args[1], id)) ||
            !number(args[index], visible) || visible < 0 || visible > 1 ||
            (args.size() == 4 && (!number(args[3], activate) || activate < 0 || activate > 1))) return failure("invalid-show-arguments");
        return show(id, visible != 0, activate != 0);
    }
    if (args.size() > 1 && !hex(args[1], id)) return failure("invalid-window-id");
    if (args[0] == "winhost_bind" && args.size() == 3) {
        uint64_t child = 0;
        return hex(args[2], child) ? bind(id, reinterpret_cast<HWND>(child)) : failure("invalid-child-window");
    }
    if (args.size() > 2) return failure("invalid-arguments");
    if (args[0] == "winhost_status") return status(id);
    if (args[0] == "winhost_close") return close(id);
    if (args[0] == "winhost_min" || args[0] == "winhost_max") {
        auto found = records_.find(id);
        if (found == records_.end()) return failure("unknown-window");
        if (found->second->releaseFailed) return failure("native-release-failed", true);
        return factory_.state(found->second->surface, args[0] == "winhost_max") ? status(id) : failure("state-failed", true);
    }
    return failure("unsupported-window-command");
}
