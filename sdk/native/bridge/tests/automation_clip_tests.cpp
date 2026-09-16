#include "automation_clip.h"
#include <array>
#include <cmath>
#include <cstring>
#include <iostream>
#include <limits>

namespace {
int checks = 0, failures = 0;
void check(bool condition, const char* name)
{
    ++checks;
    if (!condition) { ++failures; std::cerr << name << '\n'; }
}
bool contains(const std::string& text, const char* value) { return text.find(value) != std::string::npos; }

struct FakeBackend : IAutomationBackend {
    bool enabled = true, readable = true, writable = true, createOk = true;
    int writes = 0, creates = 0;
    unsigned target = 0;
    // Both inspected constructors set maximum time to INT_MAX beats; the initial curve ends at4.
    AutomationState state{2, 0x70000, 0, 2147483647.0, {{0, .25f, 0, 0}, {4, .75f, 0, 0}}};
    bool available() const override { return enabled; }
    bool read(int, AutomationState& output, std::string& reason) const override
    {
        reason = "invalid-automation-point-data";
        output = state;
        return readable;
    }
    bool replace(const AutomationState&, const std::vector<AutomationPoint>& points) override
    {
        ++writes;
        if (writable) state.points = points;
        return writable;
    }
    bool create(unsigned value, AutomationState& output) override
    {
        ++creates; target = value; output = state; return createOk;
    }
};

void commandTests()
{
    FakeBackend backend;
    check(contains(executeAutomationCommand("automation_read 2", backend), "\"sourceEventId\":458752"), "source event is persistent ID");
    check(backend.writes == 0, "read is read-only");
    check(contains(executeAutomationCommand("automation_create 1879056320", backend), "\"ok\":1"), "create accepted");
    check(backend.target == 1879056320 && backend.creates == 1, "target preserves unsigned event");
    backend.state.points[0].original[0xd] = 0x5a;
    backend.state.points[1].original[0xe] = 0xa4;
    check(contains(executeAutomationCommand("automation_add 2 2 0.5 0 0", backend), "\"ok\":1"), "middle point added");
    check(backend.state.points.size() == 3 && backend.state.points[1].time == 2, "point sorted in absolute time");
    check(backend.state.points[0].original[0xd] == 0x5a && backend.state.points[2].original[0xe] == 0xa4,
          "add preserves surviving point metadata");
    check(contains(executeAutomationCommand("automation_delete 2 1", backend), "\"ok\":1"), "interior point deleted");
    check(backend.state.points.size() == 2, "two endpoints retained");
    check(backend.state.points[0].original[0xd] == 0x5a && backend.state.points[1].original[0xe] == 0xa4,
          "delete preserves surviving point metadata");
    const char* rejected[] = {
        "automation_create 4294967296", "automation_create -1", "automation_create 1 extra",
        "automation_read -1", "automation_read 2 extra", "automation_add 2 nan 0.5 0 0",
        "automation_add 2 -1 0.5 0 0", "automation_add 2 2 1.1 0 0", "automation_add 2 2 0.5 2 0",
        "automation_add 2 2 0.5 0 4", "automation_delete 2 0", "automation_delete 2 1",
        "automation_delete 2 -1", "automation_delete 2 2147483647", "automation_set 2 4001", "automation_set 2 0",
        "automation_set 2 2 1 0.5 0 0 4 0.5 0 0", "automation_set 2 2 0 0.5 0 0 0 0.5 0 0",
        "automation_set 2 2 0 0.5 0 0 4 0.5 0 1", "automation_set 2 2 0 0.5 0 0 4 0.5 0 0 extra",
        "automation_set 2 2 0 0.5 0 0 2147483647 0.5 0 0"
    };
    int writes = backend.writes, creates = backend.creates;
    for (auto request : rejected) check(contains(executeAutomationCommand(request, backend), "\"ok\":0"), request);
    check(backend.writes == writes && backend.creates == creates, "invalid requests never mutate");
    check(contains(executeAutomationCommand("automation_set 2 3 0 0.2 0 0 8 0.7 0 0 16 0.2 0 0", backend), "\"ok\":1"), "four-beat initial curve can extend to sixteen beats");
    check(backend.state.points.size() == 3 && backend.state.points.back().time == 16, "whole curve replaced");
    check(backend.state.points[0].original[0xd] == 0 && backend.state.points.back().original[0xe] == 0,
          "explicit replacement initializes new point metadata");
    backend.readable = false; writes = backend.writes;
    check(contains(executeAutomationCommand("automation_add 2 2 0.5 0 0", backend), "invalid-automation-point-data"), "malformed existing data rejected");
    check(backend.writes == writes, "malformed count does not reset to zero");
    backend.readable = true; backend.writable = false;
    check(contains(executeAutomationCommand("automation_add 2 2 0.5 0 0", backend), "\"mayHaveChanged\":true"), "uncertain writes report partial change");
    backend.enabled = false; creates = backend.creates;
    check(contains(executeAutomationCommand("automation_create 0", backend), "unavailable"), "unsupported build refused");
    check(backend.creates == creates, "unsupported build never calls creator");
}

std::array<unsigned char, 0x800> channelMemory{};
std::array<unsigned char, 0x100> containerMemory{}, envelopeMemory{}, listMemory{};
std::array<unsigned char, 0x80> pointMemory{};
uintptr_t channelPointer = 0, listPointer = 0, listSlot = 0;
FlAutomationLayout activeLayout{0x190, 0x390};
template<class T> void put(void* data, size_t offset, T value) { std::memcpy(static_cast<unsigned char*>(data) + offset, &value, sizeof(value)); }
void* symbol(const char* name)
{
    return std::strcmp(name, "ChannelList") == 0 ? static_cast<void*>(&listSlot) : reinterpret_cast<void*>(0x10000);
}
const FlAutomationLayout* layout() { return &activeLayout; }
ULONG_PTR neverCall(void*, ULONG_PTR*, int, bool*) { check(false, "read fixture called engine"); return 0; }

void prepareMemory(unsigned typeOffset, unsigned containerOffset)
{
    activeLayout = {typeOffset, containerOffset};
    channelMemory.fill(0); containerMemory.fill(0); envelopeMemory.fill(0); listMemory.fill(0); pointMemory.fill(0);
    channelPointer = reinterpret_cast<uintptr_t>(channelMemory.data());
    listPointer = reinterpret_cast<uintptr_t>(listMemory.data()); listSlot = reinterpret_cast<uintptr_t>(&listPointer);
    put(listMemory.data(), 8, reinterpret_cast<uintptr_t>(&channelPointer)); put(listMemory.data(), 0x10, 1);
    put(channelMemory.data(), typeOffset, 5); put(channelMemory.data(), 0x9c, 0x90000u);
    put(channelMemory.data(), containerOffset, reinterpret_cast<uintptr_t>(containerMemory.data()));
    put(containerMemory.data(), 0x10, reinterpret_cast<uintptr_t>(envelopeMemory.data()));
    put(envelopeMemory.data(), 0x58, 4); put(envelopeMemory.data(), 0x68, 10000.0);
    put(envelopeMemory.data(), 0x28, reinterpret_cast<uintptr_t>(pointMemory.data() + 8));
    put(pointMemory.data(), 0, int64_t{2}); put(pointMemory.data(), 12, .5f);
    put(pointMemory.data(), 40, 4.0f); put(pointMemory.data(), 44, .5f);
}

void memoryTests(unsigned type, unsigned container)
{
    prepareMemory(type, container);
    FlAutomationBackend backend(symbol, neverCall, layout);
    AutomationState state{}; std::string reason;
    check(backend.read(0, state, reason), "verified channel layout reads");
    check(state.sourceEvent == 0x90000 && state.points.size() == 2 && state.points.back().time == 4, "mode and array decoded");
    check(!backend.read(1, state, reason), "channel bounds before pointer access");
    put(channelMemory.data(), type, 0);
    check(!backend.read(0, state, reason) && reason == "channel-is-not-an-automation-clip", "sampler refused before container access");
    put(channelMemory.data(), type, 5);
    for (int64_t count : {-1LL, 4001LL, 0x100000002LL}) {
        put(pointMemory.data(), 0, count); state = {};
        check(!backend.read(0, state, reason), "full 64-bit malformed count rejected");
    }
    put(pointMemory.data(), 0, int64_t{2}); put(pointMemory.data(), 40, -1.0f); state = {};
    check(!backend.read(0, state, reason), "negative delta rejected");
    put(pointMemory.data(), 40, 4.0f); put(pointMemory.data(), 44, std::numeric_limits<float>::quiet_NaN()); state = {};
    check(!backend.read(0, state, reason), "nonfinite value rejected");
    put(pointMemory.data(), 44, .5f); put(envelopeMemory.data(), 0x58, 5); state = {};
    check(!backend.read(0, state, reason), "secondary LFO envelope mode rejected");
    put(envelopeMemory.data(), 0x58, 4); put(channelMemory.data(), 0x9c, 0x90001u);
    check(!backend.read(0, state, reason), "non-base source event rejected");
}
}

int main()
{
    commandTests(); memoryTests(0x190, 0x390); memoryTests(0x160, 0x360);
    std::cout << checks << " automation checks, " << failures << " failures\n";
    return failures ? 1 : 0;
}
