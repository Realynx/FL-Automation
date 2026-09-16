#include "fl_window_factory.h"
#include "version_scanner.h"
#include <array>
#include <cstdio>
#include <cstring>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

#pragma comment(lib, "user32.lib")

// Production layout lookup must not be needed when this fixture injects its verified profile.
const FlWindowLayout* sig_windowLayout() { return nullptr; }

namespace {
int checks = 0;
void expect(bool value, const char* message) {
    ++checks;
    if (!value) throw std::runtime_error(message);
}
template<typename T, size_t N>
void write(std::array<unsigned char, N>& storage, size_t offset, T value) {
    std::memcpy(storage.data() + offset, &value, sizeof(value));
}
template<typename T, size_t N>
T read(const std::array<unsigned char, N>& storage, size_t offset) {
    T value{};
    std::memcpy(&value, storage.data() + offset, sizeof(value));
    return value;
}
enum class Operation : uintptr_t { Create = 1, SetCaption, SetControlVisible, SetVisible, GetHandle, Free, State, InnerRect };
void* address(Operation value) { return reinterpret_cast<void*>(static_cast<uintptr_t>(value)); }
constexpr FlWindowLayout layout{0x798, 0x760, 0x768, 0x310, 0x6AC, 0x6C8};
const FlWindowLayout* profile() { return &layout; }

struct Fixture {
    static Fixture* current;
    std::array<unsigned char, 0x500> klass{};
    std::array<unsigned char, 0x798> form{};
    std::array<unsigned char, 0x540> caption{};
    std::array<unsigned char, 0x80> maximize{};
    FlWindowFactory factory{resolve, call, mainWindow, profile};
    NativeWindowSurface surface;
    HWND host = nullptr, title = nullptr, child = nullptr;
    LONG_PTR originalHostStyle = 0;
    void* touch = nullptr;
    bool missingCaption = false, wrongCaptionParent = false, invalidInnerRect = false;
    bool missingCaptionHandle = false, missingInnerMethod = false;
    int innerCalls = 0, freeCalls = 0;
    std::vector<std::wstring> captions;

    Fixture() {
        current = this;
        touch = VirtualAlloc(nullptr, 4096, MEM_RESERVE | MEM_COMMIT, PAGE_NOACCESS);
        expect(touch != nullptr, "no-access Touch fixture allocation failed");
        write(klass, 0x100 - 0x80, uint64_t{0x798});
        write(klass, 0x100 + 0x310, address(Operation::InnerRect));
        write(form, 0, klass.data() + 0x100);
        write(form, 0x11C, touch); // TControl.Touch is deliberately not readable as control geometry.
        child = CreateWindowExW(0, L"STATIC", L"toolkit fixture", WS_POPUP,
            -30000, -30000, 100, 100, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
        expect(IsWindow(child) && !IsWindowVisible(child), "hidden toolkit fixture creation failed");
    }
    ~Fixture() {
        factory.destroy(surface);
        if (IsWindow(title)) DestroyWindow(title);
        if (IsWindow(host)) DestroyWindow(host);
        if (IsWindow(child)) DestroyWindow(child);
        if (touch) VirtualFree(touch, 0, MEM_RELEASE);
        current = nullptr;
    }
    NativeWindowSpec spec() const {
        NativeWindowSpec value;
        value.id = 1;
        value.child = child;
        value.width = 640; value.height = 400;
        value.minWidth = 320; value.minHeight = 200;
        value.caption = L"FL \U0001F3B9 \u97F3";
        return value;
    }
    static HWND mainWindow() { return current->child; }
    static void* resolve(const char* symbol) {
        if (std::strcmp(symbol, "NativeWindowClassRef") == 0) return current->klass.data() + 0x100;
        const std::pair<const char*, Operation> symbols[] = {
            {"FLui_CreateFormFromClassRef", Operation::Create}, {"FLwp_SetButtonCaption", Operation::SetCaption},
            {"FLui_ControlSetVisible", Operation::SetControlVisible}, {"FLwp_SetVisible", Operation::SetVisible},
            {"FLui_WP_GetHandle", Operation::GetHandle}, {"FL_FreeObj", Operation::Free},
            {"FLwp_SetWindowState", Operation::State}
        };
        for (const auto& entry : symbols) if (std::strcmp(symbol, entry.first) == 0) return address(entry.second);
        return nullptr;
    }
    void createForm(ULONG_PTR* args, int count) {
        expect(count == 2 && args[0] == reinterpret_cast<ULONG_PTR>(klass.data() + 0x100), "wrong form construction ABI");
        host = CreateWindowExW(0, L"STATIC", L"native fixture", WS_POPUP,
            -30000, -30000, 640, 480, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
        originalHostStyle = GetWindowLongPtrW(host, GWL_STYLE);
        title = CreateWindowExW(0, L"STATIC", L"caption fixture", WS_CHILD,
            4, 4, 600, 24, host, nullptr, GetModuleHandleW(nullptr), nullptr);
        expect(IsWindow(host) && IsWindow(title), "hidden native fixture creation failed");
        if (wrongCaptionParent) SetParent(title, child);
        write(form, 0x760, missingCaption ? nullptr : caption.data());
        write(form, 0x768, maximize.data());
        if (missingInnerMethod) write(klass, 0x100 + 0x310, static_cast<void*>(nullptr));
        *reinterpret_cast<void**>(args[1]) = form.data();
    }
    static ULONG_PTR call(void* target, ULONG_PTR* args, int count, bool* ok) {
        auto& f = *current;
        *ok = true;
        switch (static_cast<Operation>(reinterpret_cast<uintptr_t>(target))) {
        case Operation::Create: f.createForm(args, count); return 0;
        case Operation::GetHandle:
            expect(count == 1, "wrong HWND getter ABI");
            if (args[0] == reinterpret_cast<ULONG_PTR>(f.form.data())) return reinterpret_cast<ULONG_PTR>(f.host);
            if (args[0] == reinterpret_cast<ULONG_PTR>(f.caption.data()))
                return f.missingCaptionHandle ? 0 : reinterpret_cast<ULONG_PTR>(f.title);
            *ok = false; return 0;
        case Operation::SetCaption:
            expect(count == 2, "wrong caption setter ABI");
            f.captions.emplace_back(reinterpret_cast<const wchar_t*>(args[1])); return 0;
        case Operation::SetVisible:
            expect(count == 2 && args[1] == 0, "construction tried to show the native fixture"); return 0;
        case Operation::SetControlVisible: return 0;
        case Operation::State: return 0;
        case Operation::InnerRect: {
            expect(count == 2 && args[0] == reinterpret_cast<ULONG_PTR>(f.form.data()), "wrong native client rectangle ABI");
            ++f.innerCalls;
            RECT value{};
            GetClientRect(f.host, &value);
            value.left += 4; value.top += 4; value.right -= 4; value.bottom -= 4;
            if (f.invalidInnerRect) value.right = value.left;
            *reinterpret_cast<RECT*>(args[1]) = value;
            return 0;
        }
        case Operation::Free:
            expect(count == 1 && args[0] == reinterpret_cast<ULONG_PTR>(f.form.data()), "wrong form release ABI");
            ++f.freeCalls;
            if (IsWindow(f.host)) DestroyWindow(f.host);
            return 0;
        }
        *ok = false;
        return 0;
    }
};
Fixture* Fixture::current = nullptr;

void realFactoryIgnoresTouchAndPreservesContentGeometry() {
    Fixture fixture;
    std::string reason;
    const auto spec = fixture.spec();
    expect(fixture.factory.create(spec, fixture.surface, reason), "real factory rejected valid caption/client geometry with unreadable Touch");
    expect(reason.empty(), "successful create retained an error diagnostic");
    expect(fixture.innerCalls > 0, "real factory did not query the native client rectangle");
    expect(!IsWindowVisible(fixture.surface.host) && !IsWindowVisible(fixture.surface.content), "factory displayed a test window");
    const LONG_PTR style = GetWindowLongPtrW(fixture.surface.host, GWL_STYLE);
    expect(style == fixture.originalHostStyle, "factory changed native parent styles needed for caption painting");
    const LONG_PTR contentStyle = GetWindowLongPtrW(fixture.surface.content, GWL_STYLE);
    expect((contentStyle & (WS_CLIPCHILDREN | WS_CLIPSIBLINGS)) == (WS_CLIPCHILDREN | WS_CLIPSIBLINGS),
        "foreign content container lost its own child/sibling clipping");
    expect(GetParent(fixture.surface.content) == fixture.surface.host, "content container has the wrong native parent");
    expect(GetParent(fixture.child) == nullptr, "factory reparented the toolkit child before binding");
    RECT content{};
    expect(GetClientRect(fixture.surface.content, &content), "content rectangle unavailable");
    expect(content.right == spec.width && content.bottom == spec.height, "preferred content dimensions included native chrome");
    RECT bounds{};
    expect(fixture.factory.bounds(fixture.surface, bounds), "created native frame lost its content bounds");
    expect(bounds.left == 4 && bounds.top == 28 && bounds.right - bounds.left == 640 && bounds.bottom - bounds.top == 400,
        "native border/caption intersection changed content geometry");
    expect(fixture.captions.size() == 2 && fixture.captions[0] == spec.caption && fixture.captions[1] == spec.caption,
        "form and native caption did not receive matching Unicode text");
    expect(read<int>(fixture.form, 0x6AC) == 328 && read<int>(fixture.form, 0x6B0) == 232,
        "native constraints omitted the real caption or border");
    expect(read<void*>(fixture.form, 0x11C) == fixture.touch, "factory changed the Touch helper");
    expect(fixture.factory.destroy(fixture.surface) && fixture.freeCalls == 1, "factory did not release only its native form");
    expect(IsWindow(fixture.child), "native teardown destroyed the independent toolkit child");
}

void invalidGeometryRefusesCreation() {
    for (int scenario = 0; scenario < 5; ++scenario) {
        Fixture fixture;
        fixture.missingCaption = scenario == 0;
        fixture.missingCaptionHandle = scenario == 1;
        fixture.wrongCaptionParent = scenario == 2;
        fixture.invalidInnerRect = scenario == 3;
        fixture.missingInnerMethod = scenario == 4;
        std::string reason;
        expect(!fixture.factory.create(fixture.spec(), fixture.surface, reason), "invalid native geometry reported successful creation");
        expect(!reason.empty() && reason != "native-form-construction-failed", "geometry failure did not identify its stage");
        expect(!IsWindowVisible(fixture.host), "failed creation displayed a native fixture");
        expect(fixture.factory.destroy(fixture.surface), "failed native creation could not be cleaned up");
        expect(IsWindow(fixture.child), "failed native cleanup destroyed the independent toolkit child");
    }
}
}

int main() {
    try {
        realFactoryIgnoresTouchAndPreservesContentGeometry();
        invalidGeometryRefusesCreation();
        std::printf("Real FL window factory: %d checks passed; fixture windows remained hidden.\n", checks);
        return 0;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "Real FL window factory: %s\n", error.what());
        return 1;
    }
}
