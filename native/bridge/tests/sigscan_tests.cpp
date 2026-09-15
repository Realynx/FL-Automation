#include "sigscan.h"
#include "version_scanner.h"
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <stdexcept>
#include <string>

int inspectEngineFile(const wchar_t* path);
int runDelphiClassRefTests();

namespace {
int checks = 0;
void expect(bool condition, const char* message)
{
    ++checks;
    if (!condition) throw std::runtime_error(message);
}

Pattern pattern(const char* text)
{
    Pattern result{};
    expect(parsePattern(text, result), "test pattern parses");
    return result;
}

// Globals are inside the loaded test image, permitting real module-boundary checks without FL.
unsigned char fixture[] = { 0x31, 0xD2, 0xC3, 0xCC, 0x31, 0xD2, 0xC3, 0xCC };
unsigned char reference[16]{};
unsigned char rangeReference[96]{};

SymEntry symbol(const char* signature, SymKind kind = SK_Function)
{
    SymEntry e{};
    e.name = "fixture";
    e.pattern = signature;
    e.kind = kind;
    const uint64_t address = 0x400000 + (uint64_t)fixture - (uint64_t)GetModuleHandleW(nullptr);
    e.ghidra[FLV_2025_25_2_5] = address;
    e.ghidra[FLV_2026_26_1_0] = address;
    return e;
}

struct RestoreSymbol {
    SymEntry* entry;
    SymEntry saved;
    explicit RestoreSymbol(const char* name) : entry(const_cast<SymEntry*>(sig_findSym(name))), saved(*entry) {}
    ~RestoreSymbol() { *entry = saved; }
};

void testPatternParsing()
{
    Pattern p{};
    expect(!parsePattern(nullptr, p), "null pattern refused");
    expect(!parsePattern("", p), "empty pattern refused");
    expect(!parsePattern("? ??", p), "all-wildcard pattern refused");
    expect(!parsePattern("4", p), "truncated hex token refused");
    expect(!parsePattern("48ZZ", p), "invalid hex token refused");
    expect(!parsePattern("48??", p), "missing delimiter refused");
    expect(!parsePattern("???", p), "malformed wildcard refused");
    std::string maximum;
    for (int i = 0; i < 96; ++i) maximum += "48 ";
    expect(parsePattern(maximum.c_str(), p) && p.len == 96, "maximum length accepted");
    maximum += "31";
    expect(!parsePattern(maximum.c_str(), p), "overlong pattern refused instead of truncated");
    expect(!verifyBytes((uint64_t)fixture, p), "invalid pattern cannot pass byte self-check");
    expect(parsePattern("48\t8b ? ?? C3", p) && p.len == 5, "mixed-case wildcard pattern accepted");
}

void testUniqueness()
{
    const Pattern p = pattern("31 D2 C3");
    const ExecRange one{fixture, fixture + 3};
    uint64_t address = 123;
    expect(resolveUnique(p, &one, 1, &address) == RS_Ok && address == (uint64_t)fixture,
           "match ending exactly at range boundary resolves");
    const ExecRange both{fixture, fixture + sizeof(fixture)};
    expect(resolveUnique(p, &both, 1, &address) == RS_Ambiguous && address == 0,
           "duplicate match refused and output cleared");
    const ExecRange split[] = {{fixture, fixture + 3}, {fixture + 4, fixture + 7}};
    expect(resolveUnique(p, split, 2, &address) == RS_Ambiguous,
           "uniqueness spans every executable section");
    const ExecRange shortRange{fixture, fixture + 2};
    expect(resolveUnique(p, &shortRange, 1, &address) == RS_NotFound && address == 0,
           "short range does not overread");

    SYSTEM_INFO system{};
    GetSystemInfo(&system);
    const size_t pageSize = system.dwPageSize;
    auto* pages = static_cast<unsigned char*>(VirtualAlloc(nullptr, pageSize * 2,
                                                          MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    expect(pages != nullptr, "fault fixture allocated");
    memcpy(pages, fixture, 3);
    DWORD oldProtection = 0;
    const bool protectedPage = VirtualProtect(pages + pageSize, pageSize, PAGE_NOACCESS, &oldProtection) != 0;
    if (!protectedPage) { VirtualFree(pages, 0, MEM_RELEASE); throw std::runtime_error("protect fixture"); }
    const ExecRange incomplete{pages, pages + pageSize * 2};
    const ResolveStatus status = resolveUnique(p, &incomplete, 1, &address);
    VirtualFree(pages, 0, MEM_RELEASE);
    expect(status == RS_SelfCheckFail && address == 0,
           "read fault after first hit must not become a unique match");
}

struct PublishedFixture {
    PublishedFixture() { sig_testSetResolved(true); }
    ~PublishedFixture() { sig_testSetResolved(false); }
};

void testVersionsAndLegacyMapping()
{
    PublishedFixture publication;
    expect(knownFlVersion(25, 2, 5, 5319) == FLV_2025_25_2_5, "verified 2025 build identified");
    expect(knownFlVersion(26, 1, 0, 5530) == FLV_2026_26_1_0, "verified 2026 build identified");
    expect(knownFlVersion(24, 2, 5, 5319) == FLV_Unknown, "2024 not accepted");
    expect(knownFlVersion(25, 2, 6, 5319) == FLV_Unknown, "unverified patch not accepted");
    expect(knownFlVersion(26, 1, 0, 5531) == FLV_Unknown, "unverified build not accepted");
    const HMODULE mod = GetModuleHandleW(nullptr);
    expect(sig_legacyAddr(mod, FLV_Unknown, 0x400001) == 0, "unknown version cannot rebase");
    expect(sig_legacyAddr(mod, FLV_2026_26_1_0, 0x400001) == 0, "2026 cannot rebase unmapped address");
    expect(sig_legacyAddr(mod, FLV_2025_25_2_5, 0x400001) == (uint64_t)mod + 1,
           "original build retains in-image legacy address support");
    expect(sig_legacyAddr(mod, FLV_2025_25_2_5, 0x3FFFFF) == 0, "rebase underflow refused");
    expect(sig_legacyAddr(mod, FLV_2025_25_2_5, UINT64_MAX) == 0, "out-of-image rebase refused");

    RestoreSymbol leaf("FLpl_GetCurrentArrangement");
    leaf.entry->status = RS_Ambiguous;
    leaf.entry->addr = 0;
    expect(sig_legacyAddr(mod, FLV_2025_25_2_5, leaf.entry->ghidra[FLV_2025_25_2_5]) == 0,
           "failed known symbol never falls through to 2025 rebase");
    leaf.entry->status = RS_Ok;
    leaf.entry->addr = (uint64_t)fixture;
    expect(sig_legacyAddr(mod, FLV_2026_26_1_0, leaf.entry->ghidra[FLV_2025_25_2_5]) == (uint64_t)fixture,
           "legacy function address uses resolved runtime address on 2026");

    RestoreSymbol recent("RecentProjectsArray");
    recent.entry->status = RS_Ok;
    recent.entry->addr = (uint64_t)fixture;
    const uint64_t recentBase = recent.entry->ghidra[FLV_2025_25_2_5];
    expect(sig_addrByGhidra2025(recentBase + 48 * 8) == (uint64_t)fixture + 48 * 8,
           "last recent-project entry maps relative to resolved base");
    expect(sig_addrByGhidra2025(recentBase + 49 * 8) == 0, "recent-project array extent is exclusive");

    RestoreSymbol notes("NoteRecorderArrayBase");
    RestoreSymbol names("PatternNameArrayBase");
    notes.entry->status = RS_Ok;
    notes.entry->addr = (uint64_t)fixture;
    names.entry->status = RS_NotFound;
    names.entry->addr = 0;
    const uint64_t namesBase = names.entry->ghidra[FLV_2025_25_2_5];
    expect(sig_addrByGhidra2025(namesBase) == 0, "failed exact array base cannot borrow overlapping array");
    names.entry->status = RS_Ok;
    names.entry->addr = (uint64_t)fixture;
    notes.entry->status = RS_NotFound;
    notes.entry->addr = 0;
    expect(sig_addrByGhidra2025(notes.entry->ghidra[FLV_2025_25_2_5] + 0xC0) == 0,
           "failed nearest array cannot borrow overlapping array for indexed access");
}

void testResolutionPolicy()
{
    const HMODULE mod = GetModuleHandleW(nullptr);
    const ExecRange one{fixture, fixture + 3};
    const ExecRange both{fixture, fixture + sizeof(fixture)};
    SymEntry e = symbol("31 D2 C3");
    resolveSymbol(e, mod, FLV_2025_25_2_5, &one, 1);
    expect(e.status == RS_Ok && e.addr == (uint64_t)fixture, "signature and known address agree");
    resolveSymbol(e, mod, FLV_2025_25_2_5, &both, 1);
    expect(e.status == RS_Ambiguous && e.addr == 0, "matching fallback cannot override ambiguity");
    e.pattern = "??";
    resolveSymbol(e, mod, FLV_2025_25_2_5, &one, 1);
    expect(e.status == RS_SelfCheckFail && e.addr == 0, "invalid signature cannot trust fallback");
    e.pattern = "31 D2 C3";
    ++e.ghidra[FLV_2025_25_2_5];
    resolveSymbol(e, mod, FLV_2025_25_2_5, &one, 1);
    expect(e.status == RS_SelfCheckFail && e.addr == 0, "known-build signature drift refused");
    resolveSymbol(e, mod, FLV_Unknown, &one, 1);
    expect(e.status == RS_Ok, "unknown build can use uniquely resolved function signature");
    e.pattern = "";
    resolveSymbol(e, mod, FLV_Unknown, &one, 1);
    expect(e.status == RS_VersionLocked && e.addr == 0, "unknown build cannot use fallback-only symbol");
    resolveSymbol(e, mod, FLV_2026_26_1_0, &one, 1);
    expect(e.status == RS_Ok && e.addr == (uint64_t)fixture, "verified build can use explicit fallback-only symbol");
}

void testDisplacementChecks()
{
    reference[0] = 0x48; reference[1] = 0x8D; reference[2] = 0x05;
    const int32_t displacement = (int32_t)((intptr_t)fixture - (intptr_t)(reference + 7));
    memcpy(reference + 3, &displacement, sizeof(displacement));
    const ExecRange range{reference, reference + 7};
    Pattern p = pattern("48 8D 05 ?? ?? ?? ??");
    uint64_t address = 123;
    expect(resolveDataRef(p, &range, 1, 3, 7, 0, &address) == RS_Ok && address == (uint64_t)fixture,
           "RIP displacement resolves data target");
    expect(resolveDataRef(p, &range, 1, -1, 7, 0, &address) == RS_SelfCheckFail && address == 0,
           "negative displacement position refused");
    expect(resolveDataRef(p, &range, 1, 4, 7, 0, &address) == RS_SelfCheckFail,
           "displacement past matched instruction refused");
    int64_t offset = 123;
    expect(resolveVtableSlot(p, &range, 1, 3, 2, &offset) == RS_SelfCheckFail && offset == 0,
           "invalid vtable displacement width refused");
    SymEntry e = symbol("48 8D 05 ?? ?? ?? ??", SK_DataRef);
    e.dispOff = 3; e.instrEnd = 7;
    resolveSymbol(e, GetModuleHandleW(nullptr), FLV_Unknown, &range, 1);
    expect(e.status == RS_Ok && e.addr == (uint64_t)fixture, "in-module data pointer accepted");
    e.dataDelta = INT32_MAX;
    resolveSymbol(e, GetModuleHandleW(nullptr), FLV_Unknown, &range, 1);
    expect(e.status == RS_SelfCheckFail && e.addr == 0, "out-of-module derived data pointer refused");
}

void testScannerSelection()
{
    const ExecRange one{fixture, fixture + 3};
    const FlScanContext context{GetModuleHandleW(nullptr), UINT32_MAX, &one, 1};
    for (const FlFileVersion version : {FlFileVersion{}, {24, 2, 5, 5319}, {27, 1, 0, 5530}}) {
        auto scanner = createFlSignatureScanner(version);
        SymEntry entry = symbol("31 D2 C3");
        scanner->resolve(entry, context);
        expect(!scanner->supported() && entry.status == RS_VersionLocked && entry.addr == 0,
               "unsupported family denies even a unique matching signature");
        expect(!scanner->windowLayout() && !scanner->mixerTrackStride(), "unsupported family has no layouts");
    }
    auto old = createFlSignatureScanner({25, 2, 5, 5319});
    expect(strcmp(old->name(), "fl-2025") == 0 && old->fallbackVersion() == FLV_2025_25_2_5,
           "2025 scanner selected from full file identity");
    expect(old->windowLayout()->captionOffset == 0x760 && old->mixerTrackStride() == 0x1474,
           "exact 2025 layout is supplied by scanner");
    auto recorded = createFlSignatureScanner({26, 1, 0, 5530});
    expect(!recorded->windowLayout() && recorded->mixerTrackStride() == 0x1478,
           "uninspected older native form remains unavailable");
    auto current = createFlSignatureScanner({26, 1, 3, 5570});
    expect(strcmp(current->name(), "fl-2026") == 0 && current->fallbackVersion() == FLV_Unknown,
           "current 2026 patch selects family without inheriting old addresses");
    expect(current->windowLayout()->instanceSize == 0x798 && current->mixerTrackStride() == 0x1478,
           "installed current native base form and mixer layouts independently verified");
    expect(old->mixerLayout()->sendTableOffset == 0x2e4 && old->mixerLayout()->effectSlotsOffset == 0x1324,
           "2025 routing and effects use the checked 2025 layout");
    expect(current->mixerLayout()->sendTableOffset == 0x394 && current->mixerLayout()->effectSlotsOffset == 0x134c,
           "2026 routing and effects use the independently recovered offsets");
    expect(!recorded->mixerLayout(), "old 2026 address fallback does not approve an unchecked mixer layout");
    expect(old->timelineLayout()->markerManagerOffset == 0xd5c &&
           current->timelineLayout()->markerManagerOffset == 0xd84,
           "timeline marker managers use independently verified version offsets");
    expect(old->timelineLayout()->markerStride == 0x34 && current->timelineLayout()->markerNameOffset == 8,
           "timeline records retain verified Delphi array structure");
    expect(!recorded->timelineLayout(), "uninspected timeline layout remains unavailable");
    expect(old->legacyBrowserSupported() && !current->legacyBrowserSupported() && !recorded->legacyBrowserSupported(),
           "legacy browser widgets are gated independently from native form embedding");
    expect(old->menuLayout()->mainMenuOffset == 0x760 && current->menuLayout()->toolbarMenuOffset == 0x878,
           "plugin menu fields match the independently inspected published field tables");
    for (const FlFileVersion version : {FlFileVersion{25, 2, 6, 5400}, {26, 1, 3, 5571}}) {
        auto scanner = createFlSignatureScanner(version);
        SymEntry entry = symbol("31 D2 C3");
        scanner->resolve(entry, context);
        expect(scanner->supported() && entry.status == RS_Ok, "supported family update can use unique signatures");
        expect(!scanner->windowLayout() && !scanner->mixerTrackStride(), "unverified patch cannot inherit layouts");
        expect(!scanner->menuLayout() && !scanner->legacyBrowserSupported(), "unverified patch cannot write UI fields");
        expect(!scanner->timelineLayout(), "unverified patch cannot inherit timeline layout");
        entry.pattern = "";
        scanner->resolve(entry, context);
        expect(entry.status == RS_VersionLocked && !entry.addr, "unverified patch cannot inherit fallback");
    }
    SymEntry entry = symbol("");
    old->resolve(entry, {context.image, context.imageSize, nullptr, 0});
    expect(entry.status == RS_SelfCheckFail && !entry.addr, "incomplete image refuses fallback-only entry");
}

void testTransportRangeSymbols()
{
    for (const char* name : {"TransportRangeStart", "TransportRangeEnd"}) {
        const auto* definition = sig_findSym(name);
        expect(definition && definition->kind == SK_DataRef, "actual transport range has a catalogued data reference");
        auto entry = *definition;
        const Pattern p = pattern(entry.pattern);
        memcpy(rangeReference, p.bytes, p.len);
        const int displacement = static_cast<int>((intptr_t)fixture - (intptr_t)(rangeReference + entry.instrEnd));
        memcpy(rangeReference + entry.dispOff, &displacement, sizeof(displacement));
        const ExecRange range{rangeReference, rangeReference + p.len};
        auto scanner = createFlSignatureScanner({26, 1, 3, 5570});
        scanner->resolve(entry, {GetModuleHandleW(nullptr), UINT32_MAX, &range, 1});
        expect(entry.status == RS_Ok && entry.addr == (uint64_t)fixture,
               "production range recipe decodes its own RIP displacement without fallback");
        expect(entry.dataDelta == 0 && entry.ghidra[FLV_2026_26_1_0] == 0,
               "transport range never borrows an unverified 2026 address");
    }
}

unsigned char armReference[96]{};

// Both arm symbols share one anchor (FL's armTrack callback tail): the CALL rel32 must decode to the
// setter thunk and the CMP disp32 must be returned verbatim as the armed-byte offset.
void testArmTrackSymbols()
{
    const auto* setter = sig_findSym("FLmx_SetTrackArmed");
    const auto* armed = sig_findSym("MixerTrackArmedOffset");
    expect(setter && setter->kind == SK_DataRef && armed && armed->kind == SK_VtableSlot,
           "arm setter and armed-byte offset are catalogued with their decode kinds");
    expect(std::strcmp(setter->pattern, armed->pattern) == 0, "arm symbols share the armTrack callback anchor");
    const Pattern p = pattern(setter->pattern);
    expect(p.len == 68 && setter->dispOff == 64 && setter->instrEnd == 68 && armed->dispOff == 52 && armed->dispSize == 4,
           "arm anchor decodes the trailing CALL and the CMP displacement");
    memcpy(armReference, p.bytes, p.len);
    const int32_t relative = static_cast<int32_t>((intptr_t)fixture - (intptr_t)(armReference + setter->instrEnd));
    memcpy(armReference + setter->dispOff, &relative, sizeof(relative));
    const int32_t offset = 0x1470;
    memcpy(armReference + armed->dispOff, &offset, sizeof(offset));
    const ExecRange range{armReference, armReference + p.len};
    auto scanner = createFlSignatureScanner({26, 1, 3, 5570});
    auto setterEntry = *setter;
    scanner->resolve(setterEntry, {GetModuleHandleW(nullptr), UINT32_MAX, &range, 1});
    expect(setterEntry.status == RS_Ok && setterEntry.addr == (uint64_t)fixture,
           "arm setter thunk decodes from the callback's CALL rel32 without fallback");
    auto armedEntry = *armed;
    scanner->resolve(armedEntry, {GetModuleHandleW(nullptr), UINT32_MAX, &range, 1});
    expect(armedEntry.status == RS_Ok && armedEntry.addr == 0x1470,
           "armed-byte offset decodes from the callback's CMP displacement");
    expect(armed->ghidra[FLV_2025_25_2_5] == 0 && armed->ghidra[FLV_2026_26_1_0] == 0 && setter->ghidra[FLV_2026_26_1_0] == 0,
           "offset symbol has no address fallback and the setter never borrows an unverified 2026 address");
}

class FixtureScanner final : public IFlSignatureScanner {
public:
    const char* name() const override { return "fixture"; }
    bool supported() const override { return true; }
    FlVersion fallbackVersion() const override { return FLV_Unknown; }
    const FlWindowLayout* windowLayout() const override { return nullptr; }
    const FlMixerLayout* mixerLayout() const override { return nullptr; }
    unsigned mixerTrackStride() const override { return 0; }
    void resolve(SymEntry& entry, const FlScanContext& context) const override
    {
        entry.addr = (uint64_t)context.image + 42;
        entry.status = RS_Ok;
    }
};

void testInjectedScanner()
{
    const auto automation25 = createFlSignatureScanner({25, 2, 5, 5319});
    const auto automation26 = createFlSignatureScanner({26, 1, 3, 5570});
    expect(automation25->automationLayout() && automation25->automationLayout()->channelContainerOffset == 0x390,
           "2025 automation offsets require exact inspected build");
    expect(automation26->automationLayout() && automation26->automationLayout()->channelTypeOffset == 0x160 &&
           automation26->automationLayout()->channelContainerOffset == 0x360,
           "2026 automation type and container offsets are versioned together");
    for (const FlFileVersion version : {FlFileVersion{24, 2, 5, 5319}, {25, 2, 5, 5320}, {26, 1, 0, 5530}, {27, 1, 3, 5570}})
        expect(!createFlSignatureScanner(version)->automationLayout(), "unknown automation layout never inherits offsets");
    SymEntry entries[]{symbol(""), symbol("")};
    const FlScanContext context{GetModuleHandleW(nullptr), 1024, nullptr, 0};
    FixtureScanner scanner;
    resolveSymbols(scanner, context, entries, _countof(entries));
    expect(entries[0].addr == (uint64_t)context.image + 42 && entries[1].addr == entries[0].addr,
           "resolution orchestration consumes injected scanner without version logic");
    const auto unsupported = sig_inspectImage(context.image, 1024, FlFileVersion{24, 0, 0, 0});
    expect(unsupported.find("\"complete\":true") != std::string::npos &&
           unsupported.find("\"supported\":false") != std::string::npos &&
           unsupported.find("\"ok\":0") != std::string::npos,
           "completed unsupported scan reports authoritative total failure");
    expect(sig_findSym("HostClassRef")->status == RS_Unresolved,
           "pending readers receive immutable unresolved definitions");
    expect(sig_addrByGhidra2025(0xCF3888) == 0 &&
           sig_legacyAddr(context.image, FLV_2025_25_2_5, 0x400001) == 0,
           "legacy readers cannot bypass incomplete publication");
    expect(sig_symsJson().find("\"complete\":false") != std::string::npos,
           "absent engine reports pending scan separately from total failure");
}
}

int wmain(int argc, wchar_t** argv)
{
    if (argc == 3 && std::wcscmp(argv[1], L"--inspect-engine") == 0) {
        return inspectEngineFile(argv[2]);
    }
    try {
        testPatternParsing();
        testUniqueness();
        testVersionsAndLegacyMapping();
        testResolutionPolicy();
        testDisplacementChecks();
        testScannerSelection();
        testTransportRangeSymbols();
        testArmTrackSymbols();
        testInjectedScanner();
        checks += runDelphiClassRefTests();
        std::printf("Native signature resolution: %d checks passed.\n", checks);
        return 0;
    } catch (const std::exception& e) {
        std::fprintf(stderr, "Native signature resolution failed after %d checks: %s\n", checks, e.what());
        return 1;
    }
}
