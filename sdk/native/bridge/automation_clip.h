#pragma once
#include "version_scanner.h"
#include <array>
#include <vector>

struct AutomationPoint {
    double time = 0;
    float value = 0, tension = 0;
    unsigned curve = 0;
    std::array<unsigned char, 0x20> original{};
};
struct AutomationState {
    int channel = -1;
    unsigned sourceEvent = 0;
    uintptr_t envelope = 0;
    double maximumTime = 0;
    std::vector<AutomationPoint> points;
};

class IAutomationBackend {
public:
    virtual ~IAutomationBackend() = default;
    virtual bool available() const = 0;
    virtual bool read(int channel, AutomationState& state, std::string& reason) const = 0;
    virtual bool replace(const AutomationState& state, const std::vector<AutomationPoint>& points) = 0;
    virtual bool create(unsigned targetEvent, AutomationState& state) = 0;
};

// Parsing, validation, reads and mutation all run in one owned FL main-thread request.
std::string executeAutomationCommand(const std::string& request, IAutomationBackend& backend);

class FlAutomationBackend final : public IAutomationBackend {
public:
    using SymbolLookup = void* (*)(const char*);
    using GuardedCall = ULONG_PTR (*)(void*, ULONG_PTR*, int, bool*);
    using LayoutLookup = const FlAutomationLayout* (*)();
    FlAutomationBackend(SymbolLookup symbols, GuardedCall call, LayoutLookup layout)
        : symbols_(symbols), call_(call), layout_(layout) {}
    bool available() const override;
    bool read(int channel, AutomationState& state, std::string& reason) const override;
    bool replace(const AutomationState& state, const std::vector<AutomationPoint>& points) override;
    bool create(unsigned targetEvent, AutomationState& state) override;
private:
    bool channelList(uintptr_t& items, int& count) const;
    bool channelObject(int index, uintptr_t& channel) const;
    SymbolLookup symbols_;
    GuardedCall call_;
    LayoutLookup layout_;
};
