#include "automation_clip.h"
#include <algorithm>
#include <cmath>
#include <cstring>
#include <iomanip>
#include <locale>
#include <sstream>

namespace {
constexpr size_t MaximumPoints = 4000;

std::string failure(const std::string& reason, bool changed = false)
{
    return "{\"ok\":0,\"reason\":\"" + reason + "\",\"mayHaveChanged\":" +
        (changed ? "true" : "false") + "}";
}

template<class T> bool readAt(uintptr_t address, T& value)
{
    if (address < 0x10000) return false;
    __try { value = *reinterpret_cast<const T*>(address); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

bool codeInEngine(uintptr_t address)
{
    MEMORY_BASIC_INFORMATION memory{};
    if (!VirtualQuery(reinterpret_cast<void*>(address), &memory, sizeof(memory))) return false;
    const DWORD execute = PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
    return memory.AllocationBase == GetModuleHandleW(L"FLEngine_x64.dll") &&
        memory.State == MEM_COMMIT && (memory.Protect & execute) && !(memory.Protect & PAGE_GUARD);
}

bool validPoint(const AutomationPoint& point, double maximum)
{
    return std::isfinite(point.time) && point.time >= 0 && point.time <= maximum &&
        std::isfinite(point.value) && point.value >= 0 && point.value <= 1 &&
        std::isfinite(point.tension) && point.tension >= -1 && point.tension <= 1 && point.curve <= 255;
}

bool readPoints(uintptr_t array, std::vector<AutomationPoint>& points, double maximum)
{
    if (!array) return true;
    int64_t count = 0;
    if (!readAt(array - 8, count) || count < 0 || count > MaximumPoints) return false;
    double time = 0;
    for (int64_t i = 0; i < count; ++i) {
        struct StoredPoint { float delta, value, tension; unsigned char curve; } raw{};
        if (!readAt(array + static_cast<uintptr_t>(i) * 0x20, raw) ||
            !std::isfinite(raw.delta) || raw.delta < 0) return false;
        time += raw.delta;
        AutomationPoint point{time, raw.value, raw.tension, raw.curve};
        if (!validPoint(point, maximum) || !readAt(array + static_cast<uintptr_t>(i) * 0x20, point.original)) return false;
        points.push_back(point);
    }
    return true;
}

std::string stateJson(const AutomationState& state)
{
    std::ostringstream json;
    json.imbue(std::locale::classic());
    json << std::setprecision(17) << "{\"ok\":1,\"channel\":" << state.channel
         << ",\"sourceEventId\":" << state.sourceEvent << ",\"points\":[";
    for (size_t i = 0; i < state.points.size(); ++i) {
        const auto& point = state.points[i];
        if (i) json << ',';
        json << "{\"index\":" << i << ",\"timeBeats\":" << point.time << ",\"value\":" << point.value
             << ",\"tension\":" << point.tension << ",\"curve\":" << point.curve << '}';
    }
    return json.str() + "]}";
}

bool atEnd(std::istringstream& input) { input >> std::ws; return input.eof(); }

bool parsePoint(std::istringstream& input, AutomationPoint& point)
{
    double value = 0, tension = 0;
    if (!(input >> point.time >> value >> tension >> point.curve)) return false;
    point.value = static_cast<float>(value);
    point.tension = static_cast<float>(tension);
    return std::isfinite(value) && std::isfinite(tension);
}

bool validateReplacement(const std::vector<AutomationPoint>& points, double maximum)
{
    if (points.size() < 2 || points.size() > MaximumPoints || points.front().time != 0) return false;
    double previous = -1;
    for (const auto& point : points) {
        const float delta = static_cast<float>(point.time - (std::max)(0.0, previous));
        if (!validPoint(point, maximum) || point.curve != 0 || point.time <= previous ||
            !std::isfinite(delta) || (previous >= 0 && delta <= 0)) return false;
        previous = point.time;
    }
    return true;
}

bool parseReplacement(std::istringstream& input, std::vector<AutomationPoint>& points, double maximum)
{
    unsigned count = 0;
    if (!(input >> count) || count < 2 || count > MaximumPoints) return false;
    for (unsigned i = 0; i < count; ++i) {
        AutomationPoint point{};
        if (!parsePoint(input, point)) return false;
        points.push_back(point);
    }
    return atEnd(input) && validateReplacement(points, maximum);
}

bool storedTimesFit(const std::vector<AutomationPoint>& points, double maximum)
{
    double previous = 0, reconstructed = 0;
    for (const auto& point : points) {
        const float delta = static_cast<float>(point.time - previous);
        if (!std::isfinite(delta) || delta < 0) return false;
        reconstructed += delta;
        if (!std::isfinite(reconstructed) || reconstructed > maximum) return false;
        previous = point.time;
    }
    return true;
}

bool editPoints(const std::string& operation, std::istringstream& input,
                const AutomationState& state, std::vector<AutomationPoint>& points)
{
    if (operation == "automation_set") return parseReplacement(input, points, state.maximumTime);
    points = state.points;
    if (operation == "automation_add") {
        AutomationPoint point{};
        if (points.size() >= MaximumPoints || !parsePoint(input, point) || !atEnd(input) ||
            point.curve != 0 || !validPoint(point, state.maximumTime)) return false;
        points.insert(std::upper_bound(points.begin(), points.end(), point.time,
            [](double time, const AutomationPoint& other) { return time < other.time; }), point);
        return true;
    }
    int index = -1;
    if (operation != "automation_delete" || !(input >> index) || !atEnd(input) ||
        index <= 0 || static_cast<size_t>(index) >= points.size() - 1) return false;
    points.erase(points.begin() + index);
    return true;
}

bool storePoints(uintptr_t address, const AutomationPoint* points, size_t count)
{
    __try {
        double previous = 0;
        for (size_t i = 0; i < count; ++i) {
            auto* record = reinterpret_cast<unsigned char*>(address + i * 0x20);
            // FL's own insert/delete copies complete surviving records. Preserve point metadata;
            // newly supplied points begin with zeroed records and recompute rebuilds coefficients.
            std::memcpy(record, points[i].original.data(), 0x20);
            auto* values = reinterpret_cast<float*>(record);
            values[0] = static_cast<float>(points[i].time - previous);
            values[1] = points[i].value;
            values[2] = points[i].tension;
            record[0xc] = static_cast<unsigned char>(points[i].curve);
            previous = points[i].time;
        }
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
}

std::string executeAutomationCommand(const std::string& request, IAutomationBackend& backend)
{
    std::istringstream input(request);
    input.imbue(std::locale::classic());
    std::string operation;
    input >> operation;
    if (!backend.available()) return failure("automation-unavailable-for-build");
    AutomationState state{};
    if (operation == "automation_create") {
        uint64_t target = 0;
        if (!(input >> target) || target > UINT32_MAX || !atEnd(input)) return failure("invalid-target");
        if (!backend.create(static_cast<unsigned>(target), state)) return failure("creation-unconfirmed", true);
        return stateJson(state);
    }
    int channel = -1;
    if (!(input >> channel) || channel < 0) return failure("invalid-channel");
    std::string reason;
    if (!backend.read(channel, state, reason)) return failure(reason);
    if (operation == "automation_read") return atEnd(input) ? stateJson(state) : failure("invalid-request");
    if (state.points.size() < 2) return failure("invalid-existing-point-count");
    std::vector<AutomationPoint> points;
    if (!editPoints(operation, input, state, points) || !storedTimesFit(points, state.maximumTime))
        return failure("invalid-points-or-protected-endpoint");
    if (!backend.replace(state, points)) return failure("point-write-unconfirmed", true);
    AutomationState after{};
    if (!backend.read(channel, after, reason) || after.points.size() != points.size())
        return failure("point-readback-unconfirmed", true);
    return stateJson(after);
}

bool FlAutomationBackend::available() const
{
    return layout_() && symbols_("ChannelList") && symbols_("FLac_CreateForEvent") &&
        symbols_("DynArrayTypeInfo") && symbols_("Delphi_DynArraySetLength");
}

bool FlAutomationBackend::channelList(uintptr_t& items, int& count) const
{
    uintptr_t slot = 0, list = 0;
    return readAt(reinterpret_cast<uintptr_t>(symbols_("ChannelList")), slot) && readAt(slot, list) &&
        readAt(list + 8, items) && readAt(list + 0x10, count) &&
        count >= 0 && count <= 4096 && (items || count == 0);
}

bool FlAutomationBackend::channelObject(int index, uintptr_t& channel) const
{
    uintptr_t items = 0;
    int count = 0;
    return channelList(items, count) && index >= 0 && index < count &&
        readAt(items + static_cast<uintptr_t>(index) * 8, channel) && channel;
}

bool FlAutomationBackend::read(int index, AutomationState& state, std::string& reason) const
{
    state = {};
    reason = "invalid-automation-channel";
    uintptr_t channel = 0, container = 0, array = 0;
    int kind = -1, mode = -1;
    const auto* layout = layout_();
    if (!layout || !channelObject(index, channel)) return false;
    if (!readAt(channel + layout->channelTypeOffset, kind) || kind != 5) {
        reason = "channel-is-not-an-automation-clip";
        return false;
    }
    state.channel = index;
    if (!readAt(channel + 0x9c, state.sourceEvent) || state.sourceEvent >= 0x10000000 || (state.sourceEvent & 0xffff) ||
        !readAt(channel + layout->channelContainerOffset, container) ||
        !readAt(container + 0x10, state.envelope) || !readAt(state.envelope + 0x58, mode) || mode != 4 ||
        !readAt(state.envelope + 0x68, state.maximumTime) ||
        !std::isfinite(state.maximumTime) || state.maximumTime <= 0 ||
        !readAt(state.envelope + 0x28, array)) return false;
    reason = "invalid-automation-point-data";
    return readPoints(array, state.points, state.maximumTime);
}

bool FlAutomationBackend::replace(const AutomationState& state, const std::vector<AutomationPoint>& points)
{
    uintptr_t table = 0, recompute = 0;
    if (!readAt(state.envelope, table) || !readAt(table + 0x40, recompute) || !codeInEngine(recompute)) return false;
    ULONG_PTR arguments[]{state.envelope + 0x28, reinterpret_cast<ULONG_PTR>(symbols_("DynArrayTypeInfo")),
                          1, points.size()};
    bool ok = false;
    call_(symbols_("Delphi_DynArraySetLength"), arguments, 4, &ok);
    uintptr_t array = 0;
    int64_t count = 0;
    if (!ok || !readAt(state.envelope + 0x28, array) || !readAt(array - 8, count) ||
        count != static_cast<int64_t>(points.size()) || !storePoints(array, points.data(), points.size())) return false;
    arguments[0] = state.envelope;
    call_(reinterpret_cast<void*>(recompute), arguments, 1, &ok);
    return ok;
}

bool FlAutomationBackend::create(unsigned target, AutomationState& state)
{
    uintptr_t items = 0;
    int before = 0, after = 0;
    if (!channelList(items, before) || before >= 4096) return false;
    ULONG_PTR arguments[]{target, 0, 0, 0, 1};
    bool ok = false;
    uintptr_t channel = call_(symbols_("FLac_CreateForEvent"), arguments, 5, &ok);
    if (!ok || !channel || !channelList(items, after) || after != before + 1) return false;
    for (int i = 0; i < after; ++i) {
        uintptr_t candidate = 0;
        if (!readAt(items + static_cast<uintptr_t>(i) * 8, candidate)) return false;
        if (candidate != channel) continue;
        std::string reason;
        return read(i, state, reason) && state.points.size() == 2;
    }
    return false;
}
