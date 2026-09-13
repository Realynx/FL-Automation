#pragma once
#include "native_window_host.h"

using NativeSymbolResolver = void* (*)(const char*);
using NativeGuardedCall = ULONG_PTR (*)(void*, ULONG_PTR*, int, bool*);
struct FlWindowLayout;

class FlWindowFactory final : public INativeWindowFactory {
public:
    FlWindowFactory(NativeSymbolResolver symbols, NativeGuardedCall call, HWND (*mainWindow)(),
        const FlWindowLayout* (*layoutProvider)() = nullptr)
        : symbols_(symbols), call_(call), mainWindow_(mainWindow), layoutProvider_(layoutProvider) {}
    bool create(const NativeWindowSpec&, NativeWindowSurface&, std::string&) override;
    bool bounds(const NativeWindowSurface&, RECT&) override;
    bool visible(const NativeWindowSurface&, bool, bool) override;
    bool state(const NativeWindowSurface&, bool) override;
    bool minimum(const NativeWindowSurface&, int width, int height) override;
    bool destroy(NativeWindowSurface&) override;
private:
    NativeSymbolResolver symbols_;
    NativeGuardedCall call_;
    HWND (*mainWindow_)();
    const FlWindowLayout* (*layoutProvider_)();
    std::string geometryFailure_;
    const FlWindowLayout* layout() const;
    bool createCore(const NativeWindowSpec&, NativeWindowSurface&, std::string&);
    bool nativeClientRect(const NativeWindowSurface&, RECT&);
    bool invoke(const char*, std::initializer_list<ULONG_PTR>, ULONG_PTR* result = nullptr);
};
