#include "native_window_host.h"
#include "delphi_string.h"
#include <commctrl.h>
#include <cstdio>
#include <map>
#include <sstream>
#include <stdexcept>
#include <thread>
#include <utility>
#include <vector>

namespace {
int checks = 0;
void expect(bool value, const char* message) {
    ++checks;
    if (!value) throw std::runtime_error(message);
}
bool succeeded(const std::string& result) { return result.find("\"ok\":1") != std::string::npos; }
std::string hex(uint64_t value) { std::ostringstream result; result << std::hex << value; return result.str(); }
std::string handle(HWND value) { return hex(reinterpret_cast<uintptr_t>(value)); }

struct FakeFactory final : INativeWindowFactory {
    std::map<uint64_t, NativeWindowSurface> surfaces;
    std::map<uint64_t, NativeWindowSpec> specifications;
    std::vector<bool> visibility;
    int destroyed = 0;
    bool failCreate = false;
    bool failDestroy = false;
    bool invalidParent = false;
    bool swapBorrowedRoles = false;
    uint64_t reuseId = 0;
    bool lastActivation = false;
    bool lastMaximized = false;
    const wchar_t* hostClass = L"STATIC";
    void* hostParameter = nullptr;
    bool privateDesktop = false;

    bool create(const NativeWindowSpec& spec, NativeWindowSurface& surface, std::string& reason) override {
        if (reuseId) {
            surface = surfaces.at(reuseId);
            if (swapBorrowedRoles) std::swap(surface.host, surface.content);
            return true;
        }
        surface.host = CreateWindowExW(privateDesktop ? WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW : 0,
            hostClass, spec.caption.c_str(), WS_POPUP | (privateDesktop ? WS_VISIBLE : 0),
            -30000, -30000, spec.width + 8, spec.height + 28, nullptr, nullptr, GetModuleHandleW(nullptr), hostParameter);
        surface.content = CreateWindowExW(0, L"STATIC", L"content", WS_CHILD,
            0, 0, spec.width, spec.height, surface.host, nullptr, GetModuleHandleW(nullptr), nullptr);
        if (invalidParent) SetParent(surface.content, nullptr);
        surface.className = "FixtureFlFrame";
        surfaces[spec.id] = surface;
        specifications[spec.id] = spec;
        expect(IsWindow(surface.host) && IsWindow(surface.content), "hidden fixture creation failed");
        expect(privateDesktop || !IsWindowVisible(surface.host), "fixture must not show a native window");
        if (failCreate) { reason = "fixture-create-failure"; return false; }
        return true;
    }
    bool bounds(const NativeWindowSurface& surface, RECT& area) override {
        if (!GetClientRect(surface.host, &area)) return false;
        area.left += 4; area.top += 24; area.right -= 4; area.bottom -= 4;
        return true;
    }
    bool visible(const NativeWindowSurface&, bool show, bool activate) override {
        // Exercise registry routing without opening or activating any desktop surface.
        visibility.push_back(show);
        lastActivation = activate;
        return true;
    }
    bool state(const NativeWindowSurface&, bool maximize) override { lastMaximized = maximize; return true; }
    bool destroy(NativeWindowSurface& surface) override {
        if (failDestroy) return false;
        if (IsWindow(surface.host)) {
            if (!DestroyWindow(surface.host)) return false;
            ++destroyed;
        }
        surface = {};
        return true;
    }
};

struct Fixture {
    FakeFactory factory;
    NativeWindowRegistry registry{factory};
    std::vector<HWND> children;
    HWND child() {
        HWND result = CreateWindowExW(0, L"STATIC", L"child", WS_POPUP, -31000, -31000,
            101, 111, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
        expect(IsWindow(result), "hidden child creation failed");
        children.push_back(result);
        return result;
    }
    std::string create(uint64_t id, HWND child, const std::string& caption = "464C20506C7567696E", int minimum = 64) {
        return registry.execute("winhost_create " + hex(id) + " " + handle(child) + " 0 640 480 "
            + std::to_string(minimum) + " " + std::to_string(minimum) + " " + caption);
    }
    void parent(HWND child, uint64_t id) {
        SetWindowLongPtrW(child, GWL_STYLE, WS_CHILD);
        SetParent(child, factory.surfaces.at(id).content);
        expect(GetParent(child) == factory.surfaces.at(id).content, "fixture parenting failed");
    }
    void detach(HWND child) {
        if (!IsWindow(child)) return;
        SetParent(child, nullptr);
        SetWindowLongPtrW(child, GWL_STYLE, WS_POPUP);
    }
    ~Fixture() {
        factory.failDestroy = false;
        for (HWND child : children) detach(child);
        registry.closeDetached();
        for (HWND child : children) if (IsWindow(child)) DestroyWindow(child);
        for (auto& entry : factory.surfaces) {
            if (IsWindow(entry.second.content)) DestroyWindow(entry.second.content);
            if (IsWindow(entry.second.host)) DestroyWindow(entry.second.host);
        }
    }
};

struct PositionCounter {
    HWND window;
    int count = 0;
    explicit PositionCounter(HWND value) : window(value) {
        expect(SetWindowSubclass(window, callback, 1, reinterpret_cast<DWORD_PTR>(this)), "position counter subclass failed");
    }
    ~PositionCounter() { if (IsWindow(window)) RemoveWindowSubclass(window, callback, 1); }
    static LRESULT CALLBACK callback(HWND window, UINT message, WPARAM w, LPARAM l, UINT_PTR, DWORD_PTR data) {
        if (message == WM_WINDOWPOSCHANGING) ++reinterpret_cast<PositionCounter*>(data)->count;
        return DefSubclassProc(window, message, w, l);
    }
};

// FL's buffered painter captures the update region before BeginPaint and paints its
// native child controls into the same parent surface. A plain STATIC parent cannot
// detect the regression where excluding every child also makes the title invisible.
struct BufferedPainter {
    static constexpr COLORREF untouched = RGB(13, 17, 19);
    static constexpr COLORREF title = RGB(220, 40, 50);
    static constexpr COLORREF button = RGB(30, 210, 80);
    static constexpr COLORREF body = RGB(40, 70, 220);
    HDC dc = CreateCompatibleDC(nullptr);
    HBITMAP bitmap = nullptr;
    HGDIOBJ previous = nullptr;
    HRGN captured = CreateRectRgn(0, 0, 0, 0);
    int paintCalls = 0, eraseCalls = 0;
    WPARAM lastPaintParameter = 0;
    bool regionRead = false, paintBegan = false;

    BufferedPainter() {
        BITMAPINFO info{};
        info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = 648;
        info.bmiHeader.biHeight = -508;
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        void* pixels = nullptr;
        bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &pixels, nullptr, 0);
        expect(dc && bitmap && captured, "offscreen paint fixture allocation failed");
        previous = SelectObject(dc, bitmap);
        WNDCLASSW klass{};
        klass.lpfnWndProc = callback;
        klass.hInstance = GetModuleHandleW(nullptr);
        klass.lpszClassName = L"FruityLinkBufferedPaintFixture";
        expect(RegisterClassW(&klass) || GetLastError() == ERROR_CLASS_ALREADY_EXISTS,
            "buffered paint fixture class registration failed");
        reset();
    }
    ~BufferedPainter() {
        SelectObject(dc, previous);
        DeleteObject(bitmap);
        DeleteObject(captured);
        DeleteDC(dc);
    }
    static void fill(HDC target, RECT rectangle, COLORREF color) {
        HBRUSH brush = CreateSolidBrush(color);
        FillRect(target, &rectangle, brush);
        DeleteObject(brush);
    }
    void reset() {
        SelectClipRgn(dc, nullptr);
        fill(dc, RECT{0, 0, 648, 508}, untouched);
        paintCalls = eraseCalls = 0;
        regionRead = paintBegan = false;
    }
    static void draw(HDC target) {
        fill(target, RECT{0, 0, 648, 24}, title);
        fill(target, RECT{600, 4, 620, 20}, button);
        fill(target, RECT{624, 4, 644, 20}, button);
        fill(target, RECT{4, 24, 644, 504}, body);
    }
    static LRESULT CALLBACK callback(HWND window, UINT message, WPARAM w, LPARAM l) {
        if (message == WM_NCCREATE) {
            auto create = reinterpret_cast<CREATESTRUCTW*>(l);
            SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(create->lpCreateParams));
        }
        auto self = reinterpret_cast<BufferedPainter*>(GetWindowLongPtrW(window, GWLP_USERDATA));
        if (self && message == WM_PAINT) {
            ++self->paintCalls;
            self->lastPaintParameter = w;
            if (w) draw(reinterpret_cast<HDC>(w));
            else {
                self->regionRead = GetUpdateRgn(window, self->captured, FALSE) != ERROR;
                const int saved = SaveDC(self->dc);
                SelectClipRgn(self->dc, self->captured);
                PAINTSTRUCT paint{};
                self->paintBegan = BeginPaint(window, &paint) != nullptr;
                draw(self->dc);
                EndPaint(window, &paint);
                RestoreDC(self->dc, saved);
            }
            return 37; // The registry must preserve the original painter's return value.
        }
        if (self && message == WM_ERASEBKGND && w) {
            ++self->eraseCalls;
            draw(reinterpret_cast<HDC>(w));
            return 1;
        }
        return DefWindowProcW(window, message, w, l);
    }
};

void localVisibility(HWND window, bool visible) {
    LONG_PTR style = GetWindowLongPtrW(window, GWL_STYLE);
    SetWindowLongPtrW(window, GWL_STYLE, visible ? style | WS_VISIBLE : style & ~WS_VISIBLE);
}

void expectPaintPixels(BufferedPainter& painter, bool bodyProtected) {
    expect(GetPixel(painter.dc, 8, 8) == BufferedPainter::title &&
        GetPixel(painter.dc, 300, 8) == BufferedPainter::title &&
        GetPixel(painter.dc, 590, 8) == BufferedPainter::title,
        "native title strip was excluded from the shared parent paint surface");
    expect(GetPixel(painter.dc, 610, 12) == BufferedPainter::button &&
        GetPixel(painter.dc, 630, 12) == BufferedPainter::button,
        "native caption buttons were excluded from the shared parent paint surface");
    expect(GetPixel(painter.dc, 8, 28) == (bodyProtected ? BufferedPainter::untouched : BufferedPainter::body) &&
        GetPixel(painter.dc, 320, 240) == (bodyProtected ? BufferedPainter::untouched : BufferedPainter::body),
        "foreign content paint exclusion did not match the bound visible child");
}

void nativeChromeAndForeignBodyPaintIndependently() {
    BufferedPainter painter;
    Fixture fixture;
    fixture.factory.hostClass = L"FruityLinkBufferedPaintFixture";
    fixture.factory.hostParameter = &painter;
    fixture.factory.privateDesktop = true;
    HWND child = fixture.child();
    expect(succeeded(fixture.create(1, child)), "paint fixture create failed");
    const auto surface = fixture.factory.surfaces.at(1);
    HWND caption = CreateWindowExW(0, L"STATIC", L"native caption", WS_CHILD | WS_VISIBLE,
        0, 0, 648, 24, surface.host, nullptr, GetModuleHandleW(nullptr), nullptr);
    HWND button = CreateWindowExW(0, L"STATIC", L"native button", WS_CHILD | WS_VISIBLE,
        600, 4, 20, 16, caption, nullptr, GetModuleHandleW(nullptr), nullptr);
    expect(IsWindow(caption) && IsWindow(button), "native caption/button fixture creation failed");
    fixture.parent(child, 1);
    localVisibility(surface.content, true);
    localVisibility(child, true);
    expect(IsWindowVisible(surface.host) && IsWindowVisible(child) && IsWindowVisible(caption),
        "private desktop fixture needs real visible regions for native update-region painting");
    auto normalPaint = [&](bool bodyProtected) {
        painter.reset();
        expect(InvalidateRect(surface.host, nullptr, FALSE), "fixture invalidation failed");
        expect(GetUpdateRect(surface.host, nullptr, FALSE), "private desktop fixture did not acquire an update region");
        expect(SendMessageW(surface.host, WM_PAINT, 0, 0) == 37, "normal native paint return was replaced");
        expect(painter.paintCalls == 1 && painter.lastPaintParameter == 0,
            "registry replaced or repeated the native WM_PAINT message");
        expect(painter.regionRead && painter.paintBegan, "native update-region/BeginPaint lifecycle was bypassed");
        expect(PtInRegion(painter.captured, 8, 8) && PtInRegion(painter.captured, 610, 12),
            "caption or button was validated before the native buffered painter read its update region");
        expect((PtInRegion(painter.captured, 320, 240) != FALSE) == !bodyProtected,
            "normal paint validated the wrong portion of the update region");
        expectPaintPixels(painter, bodyProtected);
        expect(!GetUpdateRect(surface.host, nullptr, FALSE), "normal native paint left a recurring update region");
    };
    normalPaint(false); // Merely reserving or parenting a child is not an active paint lease.
    expect(succeeded(fixture.registry.execute("winhost_bind 1 " + handle(child))), "paint fixture bind failed");
    normalPaint(true);
    painter.reset();
    expect(SendMessageW(surface.host, WM_PAINT, 0, 0) == 37 && painter.paintCalls == 1,
        "an internal paint with no update region did not reach the native owner exactly once");
    expect(!PtInRegion(painter.captured, 8, 8) && GetPixel(painter.dc, 8, 8) == BufferedPainter::untouched,
        "an internal paint fabricated a new caption update region");
    for (UINT message : {WM_PAINT, WM_ERASEBKGND}) {
        painter.reset();
        HRGN originalClip = CreateRectRgn(2, 2, 646, 506);
        HRGN restoredClip = CreateRectRgn(0, 0, 0, 0);
        expect(originalClip && restoredClip, "supplied DC clip fixture allocation failed");
        SelectClipRgn(painter.dc, originalClip);
        const LRESULT result = SendMessageW(surface.host, message, reinterpret_cast<WPARAM>(painter.dc), 0);
        expect(result == (message == WM_PAINT ? 37 : 1), "supplied DC painter return was replaced");
        expect(painter.paintCalls + painter.eraseCalls == 1, "supplied DC painter was not called exactly once");
        expectPaintPixels(painter, true);
        expect(GetClipRgn(painter.dc, restoredClip) == 1 && EqualRgn(originalClip, restoredClip),
            "paint clipping leaked into the caller's DC state");
        SelectClipRgn(painter.dc, nullptr); // GetPixel itself obeys the DC clip region.
        expect(GetPixel(painter.dc, 0, 0) == BufferedPainter::untouched,
            "narrow paint clipping expanded the caller's original clip region");
        DeleteObject(originalClip);
        DeleteObject(restoredClip);
    }
    for (HWND hidden : {child, surface.content}) {
        localVisibility(hidden, false);
        normalPaint(false);
        localVisibility(hidden, true);
    }
    fixture.detach(child);
    normalPaint(false);
}

void paintOnPrivateDesktop() {
    const HDESK original = GetThreadDesktop(GetCurrentThreadId());
    const std::wstring name = L"FruityLinkPaintFixture-" + std::to_wstring(GetCurrentProcessId());
    HDESK desktop = CreateDesktopW(name.c_str(), nullptr, nullptr, 0, GENERIC_ALL, nullptr);
    expect(desktop != nullptr, "private fixture desktop creation failed");
    std::exception_ptr failure;
    std::thread worker([&] {
        try {
            expect(SetThreadDesktop(desktop), "fixture thread could not enter its private desktop");
            expect(GetThreadDesktop(GetCurrentThreadId()) != original, "paint fixture remained on the caller desktop");
            // Never SwitchDesktop: Windows maintains real update regions here, but
            // the native caption and toolkit fixtures cannot appear to the user.
            nativeChromeAndForeignBodyPaintIndependently();
        } catch (...) { failure = std::current_exception(); }
    });
    worker.join();
    expect(CloseDesktop(desktop), "private fixture desktop did not close after its windows were released");
    if (failure) std::rethrow_exception(failure);
}

void unchangedGeometryDoesNotRepositionChildren() {
    Fixture fixture;
    HWND child = fixture.child();
    expect(succeeded(fixture.create(1, child)), "movement fixture create failed");
    fixture.parent(child, 1);
    expect(succeeded(fixture.registry.execute("winhost_bind 1 " + handle(child))), "movement fixture bind failed");
    const auto surface = fixture.factory.surfaces.at(1);
    PositionCounter contentMoves(surface.content), childMoves(child);
    for (int index = 0; index < 10; ++index) {
        SetWindowPos(surface.host, nullptr, -29000 + index, -29000, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        WINDOWPOS duplicate{surface.host, nullptr, -29000 + index, -29000, 648, 508,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE};
        SendMessageW(surface.host, WM_WINDOWPOSCHANGED, 0, reinterpret_cast<LPARAM>(&duplicate));
    }
    expect(contentMoves.count == 0 && childMoves.count == 0, "pure host movement redundantly repositioned embedded content");
    SetWindowPos(surface.host, nullptr, 0, 0, 808, 628, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    expect(contentMoves.count == 1 && childMoves.count == 1, "one host resize must fit each child exactly once");
    SendMessageW(surface.host, WM_SIZE, 0, MAKELPARAM(808, 628));
    WINDOWPOS duplicate{surface.host, nullptr, 0, 0, 808, 628, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE};
    SendMessageW(surface.host, WM_WINDOWPOSCHANGED, 0, reinterpret_cast<LPARAM>(&duplicate));
    expect(contentMoves.count == 1 && childMoves.count == 1, "duplicate host notifications repositioned unchanged content");
    const DWORD before = static_cast<DWORD>(GetWindowLongPtrW(surface.host, GWL_STYLE));
    STYLESTRUCT style{before, before & ~static_cast<DWORD>(WS_CLIPCHILDREN | WS_CLIPSIBLINGS)};
    SendMessageW(surface.host, WM_STYLECHANGING, static_cast<WPARAM>(GWL_STYLE), reinterpret_cast<LPARAM>(&style));
    expect(style.styleNew == (before & ~static_cast<DWORD>(WS_CLIPCHILDREN | WS_CLIPSIBLINGS)),
        "registry forced clipping over parent-painted native chrome");
    expect((style.styleNew & WS_POPUP) == (before & WS_POPUP), "registry rewrote unrelated native style bits");
}

void isolationAndBind() {
    Fixture fixture;
    HWND first = fixture.child(), second = fixture.child();
    RECT original{}; GetWindowRect(first, &original);
    expect(succeeded(fixture.create(0xA1, first)), "first create failed");
    expect(succeeded(fixture.create(0xB2, second)), "second create failed");
    expect(fixture.registry.size() == 2, "independent IDs were collapsed into one host");
    const auto a = fixture.factory.surfaces.at(0xA1), b = fixture.factory.surfaces.at(0xB2);
    expect(a.host != b.host && a.content != b.content, "windows share native content");
    fixture.factory.reuseId = 0xA1;
    expect(!succeeded(fixture.create(0xC3, fixture.child())), "borrowed native surface accepted");
    expect(IsWindow(a.host) && IsWindow(a.content) && fixture.factory.destroyed == 0 && fixture.registry.size() == 2,
        "rejecting a borrowed surface destroyed its original owner");
    fixture.factory.swapBorrowedRoles = true;
    expect(!succeeded(fixture.create(0xC3, fixture.child())), "cross-role borrowed native surface accepted");
    expect(IsWindow(a.host) && IsWindow(a.content) && fixture.factory.destroyed == 0 && fixture.registry.size() == 2,
        "rejecting cross-role reuse destroyed its original owner's surface");
    fixture.factory.reuseId = 0;
    fixture.factory.swapBorrowedRoles = false;
    RECT after{}; GetWindowRect(first, &after);
    expect(EqualRect(&original, &after), "unbound child was moved or resized");
    expect(GetParent(first) == nullptr, "create reparented the foreign toolkit child");
    expect(!succeeded(fixture.create(0xA1, second)), "duplicate window ID accepted");
    expect(!succeeded(fixture.create(0xC3, first)), "one child reserved by multiple IDs");
    expect(!succeeded(fixture.registry.execute("winhost_bind a1 " + handle(first))), "unparented child was bound");
    fixture.parent(first, 0xB2);
    expect(!succeeded(fixture.registry.execute("winhost_bind a1 " + handle(first))), "wrong host parent accepted");
    fixture.parent(first, 0xA1);
    expect(succeeded(fixture.registry.execute("winhost_bind a1 " + handle(first))), "correct child did not bind");
    expect(!succeeded(fixture.registry.execute("winhost_bind b2 " + handle(first))), "wrong reserved child accepted");
    GetWindowRect(first, &after);
    expect(after.right - after.left == 640 && after.bottom - after.top == 480, "bound child did not fill its content");
    GetWindowRect(second, &after);
    expect(after.right - after.left == 101 && after.bottom - after.top == 111, "another session's unbound child changed");
    SetWindowPos(a.host, nullptr, 0, 0, 808, 628, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    GetWindowRect(first, &after);
    expect(after.right - after.left == 800 && after.bottom - after.top == 600, "host resize did not resize its bound child");
    GetWindowRect(second, &after);
    expect(after.right - after.left == 101 && after.bottom - after.top == 111, "host resize reached another session's child");
    auto list = fixture.registry.execute("winhost_list");
    expect(list.find("\"id\":\"a1\"") != std::string::npos && list.find("\"id\":\"b2\"") != std::string::npos,
        "list lost independent IDs");
}

void closeAndVisibility() {
    Fixture fixture;
    HWND child = fixture.child();
    expect(succeeded(fixture.create(1, child, "464C20506C7567696E", 320)), "create failed");
    expect(!succeeded(fixture.registry.execute("winhost_show 1 1 0")), "unbound child was shown");
    fixture.parent(child, 1);
    expect(succeeded(fixture.registry.execute("winhost_bind 1 " + handle(child))), "bind failed");
    expect(succeeded(fixture.registry.execute("winhost_show 1 1 1")) && fixture.factory.lastActivation, "explicit activation was lost");
    HWND host = fixture.factory.surfaces.at(1).host;
    MINMAXINFO limits{};
    SendMessageW(host, WM_GETMINMAXINFO, 0, reinterpret_cast<LPARAM>(&limits));
    expect(limits.ptMinTrackSize.x >= 328 && limits.ptMinTrackSize.y >= 348,
        "minimum native frame size did not include requested content and chrome");
    SendMessageW(host, WM_CLOSE, 0, 0);
    expect(IsWindow(host) && fixture.registry.size() == 1 && !fixture.factory.visibility.back(), "WM_CLOSE must hide, not destroy");
    const UINT notification = RegisterWindowMessageW(L"FruityLink.Window.UserHidden.v1");
    MSG posted{};
    expect(notification && PeekMessageW(&posted, child, notification, notification, PM_REMOVE), "user close did not notify child");
    expect(posted.wParam == reinterpret_cast<WPARAM>(host) &&
        posted.lParam == reinterpret_cast<LPARAM>(fixture.factory.surfaces.at(1).content), "close notification lost window identity");
    SendMessageW(host, WM_SYSCOMMAND, SC_CLOSE, 0);
    expect(IsWindow(host) && !fixture.factory.visibility.back(), "system close must hide");
    expect(PeekMessageW(&posted, child, notification, notification, PM_REMOVE), "system close did not notify child");
    expect(!succeeded(fixture.registry.execute("winhost_close 1")), "close destroyed a parented toolkit child");
    expect(IsWindow(child) && fixture.factory.destroyed == 0, "failed close changed ownership");
    fixture.registry.closeDetached();
    expect(fixture.registry.size() == 1 && IsWindow(child), "shutdown detached or destroyed a bound child");
    expect(!PeekMessageW(&posted, child, notification, notification, PM_REMOVE), "shutdown must not replace the user's visibility preference");
    fixture.detach(child);
    expect(succeeded(fixture.registry.execute("winhost_close 1")), "detached close failed");
    expect(fixture.registry.size() == 0 && IsWindow(child) && !IsWindow(host), "close did not release only the native frame");
    expect(succeeded(fixture.registry.execute("winhost_close 1")), "missing close is not idempotent");
}

void unicodeAndProtocol() {
    Fixture fixture;
    HWND child = fixture.child();
    expect(succeeded(fixture.create(0x10, child, "464C20F09F8EB920E99FB3", 1)), "default minimum1 or Unicode caption was rejected");
    expect(fixture.factory.specifications.at(0x10).caption == L"FL \U0001F3B9 \u97F3", "UTF-8 caption was corrupted");
    expect(succeeded(fixture.registry.execute("winhost_close 10")), "unicode fixture close failed");
    expect(succeeded(fixture.registry.execute("winhost_create 11 " + handle(child) + " 0 20000 480 1 1 41")),
        "managed maximum width20000 was rejected");
    expect(succeeded(fixture.registry.execute("winhost_close 11")), "maximum width fixture close failed");
    const std::vector<std::string> invalid = {
        "", "winhost_create", "winhost_create 0 " + handle(child) + " 0 640 480 64 64 41",
        "winhost_create 1 " + handle(child) + " 0 640 480 0 64 41",
        "winhost_create 1 " + handle(child) + " 0 640 480 64 64 00",
        "winhost_create 1 " + handle(child) + " 0 640 480 64 64 FF",
        "winhost_create 1 " + handle(child) + " 0 640 480 64 64 123",
        "winhost_create 1 " + handle(child) + " 0 20001 480 64 64 41",
        "winhost_create -1 " + handle(child) + " 0 640 480 64 64 41",
        "winhost_create 1 0 0 640 480 64 64 41", "winhost_show 1 2", "winhost_bind 1 nope",
        "winhost_close bad-id", "winhost_close 1 extra", "winhost_list extra", "unrelated_command"
    };
    for (const auto& command : invalid) expect(!succeeded(fixture.registry.execute(command)), "invalid protocol accepted");
    expect(fixture.registry.size() == 0, "invalid request leaked a native reservation");
}

void destructionAndFailure() {
    Fixture fixture;
    HWND child = fixture.child();
    fixture.factory.failCreate = true;
    expect(!succeeded(fixture.create(1, child)), "factory failure reported success");
    expect(fixture.registry.size() == 0 && fixture.factory.destroyed == 1, "partial create was not rolled back");
    fixture.factory.failCreate = false;
    expect(succeeded(fixture.create(2, child)), "recovery create failed");
    DestroyWindow(fixture.factory.surfaces.at(2).host);
    expect(fixture.registry.size() == 0, "WM_NCDESTROY left stale registry entry");
    expect(succeeded(fixture.registry.execute("winhost_close 2")), "destroyed window close did not succeed");
    expect(succeeded(fixture.create(2, child)), "destroyed ID could not be reused");
    HWND host = fixture.factory.surfaces.at(2).host;
    fixture.factory.failDestroy = true;
    expect(!succeeded(fixture.registry.execute("winhost_close 2")), "failed native destroy reported release");
    expect(fixture.registry.size() == 1 && IsWindow(host), "failed destroy dropped the callback's owning record");
    SendMessageW(host, WM_SIZE, 0, MAKELPARAM(640, 480));
    expect(fixture.registry.size() == 1, "failed destroy left a dangling subclass");
    fixture.factory.failDestroy = false;
    expect(succeeded(fixture.registry.execute("winhost_close 2")), "failed destroy could not be retried");
    fixture.factory.failCreate = fixture.factory.failDestroy = true;
    expect(!succeeded(fixture.create(3, child)), "partial create failure reported success");
    expect(fixture.registry.size() == 1, "failed partial-create cleanup lost its native reservation");
    fixture.factory.failDestroy = false;
    expect(succeeded(fixture.registry.execute("winhost_close 3")), "partial-create cleanup could not be retried");
}

void legacyCommands() {
    Fixture fixture;
    HWND child = fixture.child();
    expect(succeeded(fixture.registry.execute("winhost_embed " + handle(child) + " 0")), "legacy embed failed");
    expect(fixture.registry.size() == 1, "legacy slot not registered");
    fixture.parent(child, 0);
    expect(succeeded(fixture.registry.execute("winhost_show 1")), "legacy show did not bind its parented child");
    expect(succeeded(fixture.registry.execute("winhost_max")) && fixture.factory.lastMaximized, "legacy maximize failed");
    expect(succeeded(fixture.registry.execute("winhost_min")) && !fixture.factory.lastMaximized, "legacy minimize failed");
    expect(fixture.registry.execute("winhost_status").find("\"active\":1") != std::string::npos, "legacy status lost active state");
    expect(!succeeded(fixture.registry.execute("winhost_close")), "legacy close destroyed a parented child");
    fixture.detach(child);
    expect(succeeded(fixture.registry.execute("winhost_close")), "legacy detached close failed");
}

void incompleteSurfaceQuarantine() {
    Fixture fixture;
    fixture.factory.invalidParent = fixture.factory.failDestroy = true;
    HWND child = fixture.child();
    expect(!succeeded(fixture.create(1, child)), "invalid factory surface reported success");
    expect(fixture.registry.size() == 1, "unconfirmed invalid-surface cleanup was forgotten");
    expect(fixture.registry.execute("winhost_status 1").find("\"releaseFailed\":1") != std::string::npos,
        "incomplete surface was retained without quarantine");
    expect(!succeeded(fixture.registry.execute("winhost_show 1 0 0")) && fixture.factory.visibility.empty(),
        "quarantined incomplete surface reached a factory visibility operation");
    fixture.factory.failDestroy = false;
    expect(succeeded(fixture.registry.execute("winhost_close 1")), "incomplete surface cleanup could not be retried");
}

void delphiUnicodeStrings() {
    static_assert(sizeof(wchar_t) == 2, "fixture requires Windows UTF-16 wchar_t");
    const std::wstring text = L"FL \U0001F3B9 \u97F3";
    DelphiString caption(text);
    const auto* data = static_cast<const unsigned char*>(caption.data());
    uint16_t codePage = 0, elementSize = 0;
    int32_t referenceCount = 0, length = 0;
    std::memcpy(&codePage, data - 12, sizeof(codePage));
    std::memcpy(&elementSize, data - 10, sizeof(elementSize));
    std::memcpy(&referenceCount, data - 8, sizeof(referenceCount));
    std::memcpy(&length, data - 4, sizeof(length));
    expect(codePage == 1200 && elementSize == 2, "Delphi caption header is not UTF-16");
    expect(referenceCount == -1, "Delphi caption must use a copied constant-string reference count");
    expect(length == 7 && length == static_cast<int32_t>(text.size()), "Delphi length is not measured in UTF-16 code units");
    expect(std::memcmp(data, text.c_str(), (text.size() + 1) * sizeof(wchar_t)) == 0,
        "Delphi caption lost a surrogate pair, non-ASCII character, or terminator");
    DelphiString empty(L"");
    const auto* emptyData = static_cast<const unsigned char*>(empty.data());
    std::memcpy(&length, emptyData - 4, sizeof(length));
    uint16_t terminator = 1;
    std::memcpy(&terminator, emptyData, sizeof(terminator));
    expect(length == 0 && terminator == 0, "empty Delphi string is not terminated with zero length");
}
}

int main() {
    try {
        isolationAndBind(); closeAndVisibility(); unicodeAndProtocol(); destructionAndFailure(); legacyCommands();
        incompleteSurfaceQuarantine(); delphiUnicodeStrings();
        unchangedGeometryDoesNotRepositionChildren();
        paintOnPrivateDesktop();
        std::printf("Native window registry: %d checks passed; no fixture windows appeared on the interactive desktop.\n", checks);
        return 0;
    } catch (const std::exception& error) { std::fprintf(stderr, "Native window registry: %s\n", error.what()); return 1; }
}
