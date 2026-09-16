#include "mixer_track_insertion.h"
#include <cstdio>
#include <cstring>
#include <stdexcept>
#include <vector>

namespace {
int checks = 0;
void expect(bool value, const char* message) { ++checks; if (!value) throw std::runtime_error(message); }

FlMixerLayout layout{0x1478, 12, 8, 24, 26, 0x394, 0x134c, 8, 0, 4, 8, 0x64, 0x58, 0xf0};
std::vector<unsigned char> tracks(502 * 0x1478);
uintptr_t arrayPointer;
int nativeCount;
uintptr_t countPointer;
bool supported, resolved, callOk, grow;
int calls, inserted;
unsigned insertedFlags;
void setType(int index, int type) { memcpy(tracks.data() + index * layout.trackStride + layout.typeOffset, &type, 4); }
const FlMixerLayout* getLayout() { return supported ? &layout : nullptr; }
void* symbol(const char* name)
{
    if (strcmp(name, "FLmx_InsertTracks") == 0) return resolved ? reinterpret_cast<void*>(1) : nullptr;
    if (strcmp(name, "MixerTrackCount") == 0) return &countPointer;
    if (strcmp(name, "MixerTrackArray") == 0) return &arrayPointer;
    return nullptr;
}
ULONG_PTR call(void* function, ULONG_PTR* args, int count, bool* ok)
{
    expect(function == reinterpret_cast<void*>(1), "wrong insertion symbol");
    expect(count == 3 && args[1] == 1, "insertion must request exactly one track through three scalar arguments");
    ++calls;
    inserted = static_cast<int>(args[0]); insertedFlags = static_cast<unsigned>(args[2]);
    if (grow) { ++nativeCount; setType(inserted, 1); }
    *ok = callOk;
    return 0;
}
void reset(int count = 18)
{
    nativeCount = count; countPointer = reinterpret_cast<uintptr_t>(&nativeCount);
    arrayPointer = reinterpret_cast<uintptr_t>(tracks.data());
    supported = resolved = callOk = grow = true; calls = 0; inserted = -1; insertedFlags = 0;
    for (int i = 0; i < 502; ++i) setType(i, i == 0 ? 0 : i == 501 ? 2 : 1);
}
std::string run(const std::string& request)
{
    FlMixerTrackInsertionBackend backend(symbol, call, getLayout);
    return executeMixerTrackInsertion(request, backend);
}
void contains(const std::string& result, const char* text) { expect(result.find(text) != std::string::npos, text); }

void testPlacement()
{
    for (unsigned stride : {0x1474U, 0x1478U}) {
        layout.trackStride = stride;
        for (int after : {-1, 0, 1, 16}) {
            reset();
            const auto result = run("mixer_add " + std::to_string(after));
            contains(result, "\"ok\":1"); contains(result, "\"count\":19");
            const int expected = after == -1 ? 17 : after + 1;
            expect(inserted == expected && calls == 1, "wrong placement or repeated mutation");
            expect(insertedFlags == (after == -1 ? 0x0bU : 0x0fU), "native AddAfter flags differ");
            contains(result, ("\"index\":" + std::to_string(expected)).c_str());
            int current = 0; FlMixerTrackInsertionBackend backend(symbol, call, getLayout);
            expect(backend.type(501, current) && current == 2, "Current physical identity changed");
        }
    }
    reset(501); contains(run("mixer_add -1"), "\"index\":500"); expect(nativeCount == 502, "last ordinary track missing");
}
void testRefusals()
{
    for (const char* request : {"mixer_add", "mixer_add ", "mixer_add -2", "mixer_add 17", "mixer_add 501", "mixer_add 2147483648", "mixer_add 1 extra"}) {
        reset(); contains(run(request), "\"ok\":0"); expect(calls == 0, "invalid input reached native mutation");
    }
    for (int count : {0, 2, 503, 502}) {
        reset(count); contains(run("mixer_add -1"), "\"ok\":0"); expect(calls == 0, "invalid/full count mutated");
    }
    reset(); supported = false; contains(run("mixer_add -1"), "mixer-insertion-unavailable"); expect(calls == 0, "unsupported layout mutated");
    reset(); resolved = false; contains(run("mixer_add -1"), "mixer-insertion-unavailable"); expect(calls == 0, "missing symbol mutated");
    reset(); countPointer = 0; contains(run("mixer_add -1"), "invalid-mixer-count"); expect(calls == 0, "null count mutated");
    reset(); setType(501, 1); contains(run("mixer_add -1"), "invalid-mixer-topology"); expect(calls == 0, "incorrect Current identity mutated");
    reset(); arrayPointer = 0; contains(run("mixer_add -1"), "invalid-mixer-topology"); expect(calls == 0, "null track array mutated");
}
void testAmbiguousResults()
{
    reset(); callOk = false;
    auto result = run("mixer_add -1"); contains(result, "native-insertion-fault"); contains(result, "\"mayHaveChanged\":true");
    expect(calls == 1 && nativeCount == 19, "faulted insertion was retried or falsely rolled back");
    reset(); grow = false;
    result = run("mixer_add -1"); contains(result, "native-insertion-unconfirmed"); contains(result, "\"mayHaveChanged\":true");
    expect(calls == 1, "unconfirmed insertion retried");
}
}

int main()
{
    try { testPlacement(); testRefusals(); testAmbiguousResults(); std::printf("Mixer insertion: %d checks passed.\n", checks); return 0; }
    catch (const std::exception& error) { std::fprintf(stderr, "Mixer insertion failed: %s\n", error.what()); return 1; }
}
