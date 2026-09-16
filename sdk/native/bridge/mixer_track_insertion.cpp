#include "mixer_track_insertion.h"
#include <charconv>

namespace {
constexpr int CurrentTrack = 501;
constexpr int MaximumCount = 502;

std::string failure(const char* reason, bool mayHaveChanged = false)
{
    return std::string("{\"ok\":0,\"reason\":\"") + reason + "\",\"mayHaveChanged\":"
        + (mayHaveChanged ? "true" : "false") + "}";
}

template<class T> bool readAt(const void* address, T& value)
{
    __try { value = *static_cast<const T*>(address); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

bool parseAfter(const std::string& request, int& after)
{
    constexpr const char* prefix = "mixer_add ";
    if (request.compare(0, 10, prefix) != 0 || request.size() <= 10) return false;
    const char* begin = request.data() + 10;
    const char* end = request.data() + request.size();
    const auto parsed = std::from_chars(begin, end, after);
    return parsed.ec == std::errc{} && parsed.ptr == end && after >= -1;
}
}

std::string executeMixerTrackInsertion(const std::string& request, IMixerTrackInsertionBackend& backend)
{
    int after = 0;
    if (!parseAfter(request, after)) return failure("invalid-after-track");
    if (!backend.available()) return failure("mixer-insertion-unavailable");
    int before = 0;
    if (!backend.count(before) || before < 3 || before > MaximumCount) return failure("invalid-mixer-count");
    if (before == MaximumCount) return failure("mixer-capacity-reached");
    const int predecessor = after == -1 ? before - 2 : after;
    if (predecessor < 0 || predecessor > before - 2) return failure("invalid-after-track");
    int kind = -1, currentKind = -1;
    if (!backend.type(predecessor, kind) || kind != (predecessor == 0 ? 0 : 1) ||
        !backend.type(CurrentTrack, currentKind) || currentKind != 2) return failure("invalid-mixer-topology");
    const int index = predecessor + 1;
    // These are FL's own AddAfter flags: undo + select + after; explicit placement inherits docking.
    if (!backend.insert(index, after == -1 ? 0x0b : 0x0f)) return failure("native-insertion-fault", true);
    int actual = 0;
    if (!backend.count(actual) || actual != before + 1 || !backend.type(index, kind) || kind != 1)
        return failure("native-insertion-unconfirmed", true);
    return "{\"ok\":1,\"index\":" + std::to_string(index) + ",\"count\":" + std::to_string(actual) + "}";
}

bool FlMixerTrackInsertionBackend::available() const
{
    return layout_() && symbol_("FLmx_InsertTracks") && symbol_("MixerTrackCount") && symbol_("MixerTrackArray");
}

bool FlMixerTrackInsertionBackend::count(int& value) const
{
    uintptr_t pointer = 0;
    return readAt(symbol_("MixerTrackCount"), pointer) && pointer && readAt(reinterpret_cast<void*>(pointer), value);
}

bool FlMixerTrackInsertionBackend::type(int index, int& value) const
{
    const auto* layout = layout_();
    uintptr_t pointer = 0;
    if (!layout || index < 0 || index > CurrentTrack || !readAt(symbol_("MixerTrackArray"), pointer) || !pointer)
        return false;
    return readAt(reinterpret_cast<void*>(pointer + static_cast<uintptr_t>(index) * layout->trackStride + layout->typeOffset), value);
}

bool FlMixerTrackInsertionBackend::insert(int index, unsigned flags)
{
    ULONG_PTR args[]{static_cast<ULONG_PTR>(index), 1, flags};
    bool ok = false;
    call_(symbol_("FLmx_InsertTracks"), args, 3, &ok);
    return ok;
}
