// sigscan.h — FlBridge runtime signature-scanning subsystem (FL version portability).
//
// Resolves FL Studio addresses at RUNTIME by byte-signature scanning the loaded FLEngine_x64.dll,
// so ONE bridge binary can support multiple FL versions (25.2.5 / 26.1.0) instead of the hardcoded
// `base + (ghidra - 0x400000)` rebase. Design: re/version-portability-design.md.
//
// FAIL-SAFE by construction: a wrong address is an uncatchable AV inside FL, so every path here
// REFUSES rather than guesses — 0 matches = NotFound, >1 = Ambiguous (never pick one), a fallback is
// never trusted on an unknown FL version, and all raw reads are SEH-guarded. This subsystem is
// ADDITIVE: nothing resolves through it until a call site opts in via a `sym:NAME` wire token; the
// legacy hex-address path is untouched.
#pragma once

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdint>
#include <string>

// ---- FL versions we know how to key a fallback for ----------------------------------------------
// (Ground truth 2026-07-09: 2025 = 25.2.5.5319, 2026 = 26.1.0.5530.) FLV_Unknown is index 0 so the
// per-symbol ghidra[] fallback table can be indexed directly by version; a fallback on FLV_Unknown is
// never trusted.
enum FlVersion { FLV_Unknown = 0, FLV_2025_25_2_5, FLV_2026_26_1_0, FLV_COUNT };

// How a symbol's address is recovered from its signature match.
//   SK_Function   — the match start IS the target (a function prologue).
//   SK_DataRef    — the match references a data global; RIP-relative decode yields the target.
//   SK_VtableSlot — the match encodes a struct/vtable displacement; decode yields the OFFSET (not addr).
enum SymKind { SK_Function, SK_DataRef, SK_VtableSlot };

enum ResolveStatus {
    RS_Unresolved = 0, // not yet attempted
    RS_Ok,             // resolved (addr is valid; for SK_VtableSlot addr holds the offset)
    RS_NotFound,       // signature matched 0 times
    RS_Ambiguous,      // signature matched >1 times — refused
    RS_VersionLocked,  // unknown FL version + no signature — refused
    RS_SelfCheckFail,  // resolved address failed the byte self-check — refused
    RS_NoModule        // FLEngine_x64.dll not loaded
};

// ---- compiled signature ----
// An IDA-style pattern ("48 8B 05 ? ? ? ? E8") compiled to bytes + a match mask, plus an anchor: a
// single RARE fixed byte (not 0x00/0x48/0xCC/0xFF) used to memchr-accelerate the scan.
struct Pattern {
    unsigned char bytes[96];  // fixed byte values (0 where wildcard)
    bool          mask[96];   // true = must match, false = wildcard (?)
    int           len;        // number of tokens
    int           anchorIdx;  // index of the anchor byte within the pattern (-1 = none)
    unsigned char anchorVal;  // value of the anchor byte
    bool          valid;
};

// An executable code range of the loaded module (absolute pointers into the mapped image).
struct ExecRange { const unsigned char* begin; const unsigned char* end; };

// One resolvable symbol. `ghidra[]` is the per-version hardcoded fallback (image base 0x400000; 0 =
// unknown for that version). `addr` + `status` are filled by sig_resolveAll().
struct SymEntry {
    const char*   name;
    const char*   pattern;   // IDA-style signature; "" = fallback-only (no signature yet)
    SymKind       kind;
    int           dispOff;   // RIP/ModRM displacement offset within the match (DataRef/VtableSlot)
    int           instrEnd;  // bytes from match start to end of the referencing instruction (DataRef)
    int           dispSize;  // displacement size in bytes (VtableSlot: 1 or 4)
    int           dataDelta; // fixed struct offset added to a resolved DataRef target (anchor+delta; 0 = none)
    uint64_t      ghidra[FLV_COUNT];
    uint64_t      addr;      // resolved absolute address (or offset for SK_VtableSlot); 0 if unresolved
    ResolveStatus status;
};

// ---- core primitives (exposed for completeness / reuse; the wire path uses the helpers below) ----
bool          parsePattern(const char* ida, Pattern& out);
int           getExecRanges(HMODULE mod, ExecRange* out, int maxOut);
bool          matchAt(const Pattern& pat, const unsigned char* p);
ResolveStatus resolveUnique(const Pattern& pat, const ExecRange* ranges, int nRanges, uint64_t* outAddr);
ResolveStatus resolveDataRef(const Pattern& pat, const ExecRange* ranges, int nRanges,
                             int dispOff, int instrEnd, int dataDelta, uint64_t* outAddr);
ResolveStatus resolveVtableSlot(const Pattern& pat, const ExecRange* ranges, int nRanges,
                                int dispOff, int dispSize, int64_t* outOffset);
FlVersion     detectFlVersion(HMODULE mod);
uint64_t      fallbackAddr(const SymEntry& e, HMODULE mod, FlVersion ver);
bool          verifyBytes(uint64_t addr, const Pattern& pat);

// ---- public entry points used by the bridge wire dispatch ----
// Resolve every symbol in the table ONCE (guarded). No-op until FLEngine_x64.dll is loaded, so it is
// safe (and cheap) to call repeatedly — the first call after FL is up does the work.
void          sig_resolveAll();
// Lookup by name; NULL if the name isn't in the table.
SymEntry*     sig_findSym(const char* name);
// Resolved absolute address (or vtable offset) for a name, or 0 if not resolved / unknown.
uint64_t      sig_addr(const char* name);
// Reverse lookup for the legacy hex wire path: a 2025 Ghidra address -> its resolved runtime address,
// or 0 if it isn't a known symbol. Lets hardcoded-hex call sites become version-correct without migration.
uint64_t      sig_addrByGhidra2025(uint64_t ghidra25);
// The FL version detected at resolve time (valid after sig_resolveAll()).
FlVersion     sig_version();
// Diagnostic JSON for the `syms` wire command: {"ver":N,"ok":N,"fail":M,"unresolved":[{"name","why"}]}.
std::string   sig_symsJson();
// Human-readable status word (for logs / JSON).
const char*   sig_statusStr(ResolveStatus s);

// Convenience: resolved absolute address by name, mirroring rb()/SYM("name"). 0 if unresolved.
#define SYM(n) sig_addr(n)
