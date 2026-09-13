#pragma once
#include <windows.h>
#include <cstdint>
#include <map>
#include <memory>
#include <string>

struct NativeWindowSpec {
    uint64_t id = 0;
    HWND child = nullptr;
    bool show = false;
    int width = 900, height = 640, minWidth = 320, minHeight = 240;
    std::wstring caption = L"FL Plugin";
};

struct NativeWindowSurface {
    void* form = nullptr;
    HWND host = nullptr;
    HWND content = nullptr;
    const char* className = "";
    bool destructionAttempted = false;
};

// FL-specific creation is injected; registry tests use ordinary hidden Win32 fixture windows.
class INativeWindowFactory {
public:
    virtual ~INativeWindowFactory() = default;
    virtual bool create(const NativeWindowSpec&, NativeWindowSurface&, std::string& reason) = 0;
    virtual bool bounds(const NativeWindowSurface&, RECT&) = 0;
    virtual bool visible(const NativeWindowSurface&, bool show, bool activate) = 0;
    virtual bool state(const NativeWindowSurface&, bool maximize) = 0;
    virtual bool minimum(const NativeWindowSurface&, int, int) { return true; }
    virtual bool destroy(NativeWindowSurface&) = 0;
};

// All methods, including the subclass callback, run on the window-owner thread.
// Each bridge command carries its own request/result; there is no global pending operation.
class NativeWindowRegistry {
public:
    explicit NativeWindowRegistry(INativeWindowFactory& factory) : factory_(factory) {}
    std::string execute(const std::string& command);
    void closeDetached();
    size_t size() const { return records_.size(); }
private:
    struct Record;
    INativeWindowFactory& factory_;
    std::map<uint64_t, std::shared_ptr<Record>> records_;
    std::string create(const NativeWindowSpec&, bool legacy);
    std::string bind(uint64_t, HWND);
    std::string close(uint64_t);
    std::string show(uint64_t, bool, bool);
    std::string status(uint64_t);
    void fit(const std::shared_ptr<Record>&);
    bool foreignPaintBounds(const Record&, RECT&) const;
    LRESULT paint(const std::shared_ptr<Record>&, HWND, UINT, WPARAM, LPARAM);
    static LRESULT CALLBACK subclass(HWND, UINT, WPARAM, LPARAM, UINT_PTR, DWORD_PTR);
};
