// sigscan.cpp — runtime signature-scanning subsystem (see sigscan.h + re/version-portability-design.md).
//
// FAIL-SAFE: refuse, never guess. Uniqueness is mandatory (0 or >1 matches both FAIL). A per-version
// hardcoded fallback is used ONLY on a known FL version, and only after a byte self-check when a
// signature is also present. Every raw memory read is SEH-guarded (POD-only __try frames — MSVC C2712).

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <psapi.h>
#include "sigscan.h"

#include <stdio.h>
#include <stdarg.h>
#include <string.h>
#include <string>
#include <vector>

#pragma comment(lib, "psapi.lib")
#pragma comment(lib, "version.lib")

static const uint64_t SIG_GHIDRA_BASE = 0x400000ULL;

// ================================ logging (same file/format as the bridge) ================================
static void sig_logline(const char* s)
{
    char path[MAX_PATH]; DWORD n = GetTempPathA(MAX_PATH, path);
    if (n == 0 || n > MAX_PATH - 32) return;
    strcat_s(path, MAX_PATH, "fruitylink-bridge.log");
    FILE* f = NULL; if (fopen_s(&f, path, "a") == 0 && f) { fprintf(f, "%s\n", s); fclose(f); }
}
static void sig_logf(const char* fmt, ...)
{
    char buf[512]; va_list ap; va_start(ap, fmt);
    _vsnprintf_s(buf, sizeof(buf), _TRUNCATE, fmt, ap); va_end(ap);
    sig_logline(buf);
}

// ================================ signature table (STARTER) ================================
// ADDITIVE + fail-safe by design: only verified rows, and all patterns are "" (fallback-only) for now —
// the real signatures come from a separate Ghidra pass. ghidra[] is indexed by FlVersion; the 2025
// column comes from the hardcoded inventory (Delphi RTL is stable-placed; the two FL fns are the 2025
// addresses). On a 2025 build these resolve via fallback; on any unknown version they REFUSE
// (RS_VersionLocked). Nothing here changes existing behavior — no call site references these yet.
static SymEntry g_syms[] = {
    // name / pattern (IDA sig) / kind / dispOff / instrEnd / dispSize / dataDelta / ghidra[Unknown,2025,2026] / addr / status
    { "FLui_CreateFormFromClassRef", "48 83 EC 28 48 8B 05 ?? ?? ?? ?? 48 8B 00 49 89 C8 49 89 D1", SK_Function, 0, 0, 0, 0, { 0, 0x10C2AA0ULL, 0x11C2870ULL }, 0, RS_Unresolved },
    { "FLwp_SetFormCaption",         "57 56 53 48 83 EC 20 48 89 CB 48 89 D6 48 8B 8B 10 01 00 00", SK_Function, 0, 0, 0, 0, { 0, 0x841690ULL,  0x869370ULL }, 0, RS_Unresolved },
    { "FLwp_SetVisible",             "57 56 53 48 83 EC 20 48 89 CB 40 89 D6 48 0F B6 83 6C 06 00 00", SK_Function, 0, 0, 0, 0, { 0, 0x833EC0ULL,  0x85BBA0ULL }, 0, RS_Unresolved },
    { "FLui_ZOrderRefresh",          "56 53 48 83 EC 28 48 89 CB 48 89 D9 66 BA CE FF E8 ?? ?? ?? ?? 48 89 C6 48 89 D9 B2 01", SK_Function, 0, 0, 0, 0, { 0, 0x5D0EA0ULL,  0x60A780ULL }, 0, RS_Unresolved },
    { "FLui_WP_GetHandle",           "53 48 83 EC 20 48 89 CB 48 89 D9 E8 ?? ?? ?? ?? 48 8B 83 5C 04 00 00", SK_Function, 0, 0, 0, 0, { 0, 0x5DDF70ULL,  0x617850ULL }, 0, RS_Unresolved },
    { "FLwp_SetWindowState",         "53 48 83 EC 20 89 D3 38 99 C2 04 00 00 74 5B", SK_Function, 0, 0, 0, 0, { 0, 0x836600ULL,  0x85E2E0ULL }, 0, RS_Unresolved },
    { "FLui_DockLayout",             "56 53 48 83 EC 28 48 89 CB 89 54 24 48 8B 44 24 48 89 83 D2 06 00 00", SK_Function, 0, 0, 0, 0, { 0, 0x7E6170ULL,  0x808160ULL }, 0, RS_Unresolved },
    { "FLui_Focusable",              "56 53 48 83 EC 28 48 89 CB 40 89 D6 40 38 B3 7C 03 00 00", SK_Function, 0, 0, 0, 0, { 0, 0x5DE3E0ULL,  0x617CC0ULL }, 0, RS_Unresolved },
    { "FLwp_Render",                 "57 56 53 48 83 EC 20 48 89 CB 48 89 D9 E8 ?? ?? ?? ?? 48 8B 0D ?? ?? ?? ?? 48 0F B6 93 C3 05 00 00", SK_Function, 0, 0, 0, 0, { 0, 0x77ADB0ULL,  0x79AD70ULL }, 0, RS_Unresolved },
    { "FLui_WP_SetAlign",            "53 48 83 EC 30 88 54 24 48 48 0F B6 81 B3 00 00 00", SK_Function, 0, 0, 0, 0, { 0, 0x5CEEF0ULL,  0x6087D0ULL }, 0, RS_Unresolved },
    { "FLwp_SetterA",                "48 83 EC 28 38 91 AC 00 00 00 74 28 88 91 AC 00 00 00", SK_Function, 0, 0, 0, 0, { 0, 0x5D0D90ULL,  0x60A670ULL }, 0, RS_Unresolved },
    { "FLwp_SetterB",                "48 83 EC 28 38 91 AB 00 00 00 74 28 88 91 AB 00 00 00", SK_Function, 0, 0, 0, 0, { 0, 0x5D0C50ULL,  0x60A530ULL }, 0, RS_Unresolved },
    { "FLwp_CreateButtonControl",    "56 53 48 83 EC 28 48 8B 0D ?? ?? ?? ?? B2 01 4D 33 C0 E8 ?? ?? ?? ?? 48 89 C3 48 89 D9 33 D2 E8 ?? ?? ?? ?? 48 89 D9 48 8B 05 ?? ?? ?? ??", SK_Function, 0, 0, 0, 0, { 0, 0xF0DDB0ULL,  0xFF11E0ULL }, 0, RS_Unresolved },
    { "FLwp_SetButtonCaption",       "55 48 83 EC 40 48 8B EC 48 89 4D 28 48 89 55 30 48 C7 45 38 ?? ?? ?? ?? 90 48 8B 4D 28 48 8D 55 38 E8 ?? ?? ?? ?? 48 8B 4D 38 48 8B 55 30 E8 ?? ?? ?? ?? 85 C0 74 15 48 8B 4D 30", SK_Function, 0, 0, 0, 0, { 0, 0x5D0AE0ULL,  0x60A3C0ULL }, 0, RS_Unresolved },
    { "FLwp_SetControlValue",        "56 53 48 83 EC 28 48 89 CB 39 93 C4 00 00 00", SK_Function, 0, 0, 0, 0, { 0, 0x5D0D10ULL,  0x60A5F0ULL }, 0, RS_Unresolved },
    { "FLbrz_AddTabClone",           "55 41 55 57 56 53 48 81 EC B0 00 00 00 48 8B EC 48 89 4D 78", SK_Function, 0, 0, 0, 0, { 0, 0x9AC910ULL,  0xA7CC20ULL }, 0, RS_Unresolved },
    { "Delphi_UStrAsg",              "41 55 57 56 53 48 83 EC 28 48 89 CB 49 89 D5 4C 89 EE 48 85 F6", SK_Function, 0, 0, 0, 0, { 0, 0x4133F0ULL,  0x4133F0ULL }, 0, RS_Unresolved },
    { "FLmenu_CreateItem",           "55 57 56 48 83 EC 50 48 8B EC 48 89 4D 28 89 55 34 4C 89 CE", SK_Function, 0, 0, 0, 0, { 0, 0x70E1A0ULL,  0x72D670ULL }, 0, RS_Unresolved },
    { "FL_ChildCount",               "48 8B 81 B0 00 00 00 48 85 C0 75 04 33 C0", SK_Function, 0, 0, 0, 0, { 0, 0x81DDA0ULL,  0x83FDF0ULL }, 0, RS_Unresolved },
    { "FL_ChildAt",                  "56 53 48 83 EC 28 48 89 CB 89 D6 48 83 BB B0 00 00 00 00", SK_Function, 0, 0, 0, 0, { 0, 0x81DDC0ULL,  0x83FE10ULL }, 0, RS_Unresolved },
    { "FL_FreeObj",                  "53 48 83 EC 20 48 85 C9 74 0E 48 89 C8 B2 01", SK_Function, 0, 0, 0, 0, { 0, 0x40FAA0ULL,  0x40FAA0ULL }, 0, RS_Unresolved },
    { "FL_ListRemoveAt",             "53 48 83 EC 20 83 69 10 01 8B 41 10 3B D0", SK_Function, 0, 0, 0, 0, { 0, 0x64F4D0ULL,  0x663B10ULL }, 0, RS_Unresolved },
    { "FLui_SetStatusHint",          "53 48 83 EC 20 4D 33 C0 E8 ?? ?? ?? ?? 84 C0", SK_Function, 0, 0, 0, 0, { 0, 0x10EC870ULL, 0x11EB580ULL }, 0, RS_Unresolved },
    { "FormShortCut",                "41 55 57 56 53 48 83 EC 28 48 89 CB 48 89 D6 4C 89 C7 80 BB C5 04 00 00 00", SK_Function, 0, 0, 0, 0, { 0, 0x114DE10ULL, 0x1248140ULL }, 0, RS_Unresolved },
    { "FormKeyDown",                 "55 53 48 81 EC B8 00 00 00 48 8B EC 48 89 4D 58 48 89 55 60 4C 89 45 68", SK_Function, 0, 0, 0, 0, { 0, 0x10C9920ULL, 0x11C9980ULL }, 0, RS_Unresolved },
    { "FLgl_GlobalCommandDispatch",  "55 57 56 53 48 81 EC 68 01 00 00 48 8B EC 89 4D 40 89 55 44", SK_Function, 0, 0, 0, 0, { 0, 0xEF7B20ULL,  0xFD8800ULL }, 0, RS_Unresolved },
    { "FLtr_SeekToSongTick",         "56 53 48 83 EC 48 66 0F ?? ?? ?? ?? 66 0F ?? ?? ?? ?? 66 0F 29 C6", SK_Function, 0, 0, 0, 0, { 0, 0x10E3470ULL, 0x11E1860ULL }, 0, RS_Unresolved },
    { "FL_DispatchCommand",          "55 56 53 48 81 EC 90 05 00 00 48 8B EC 48 C7 45 50 ?? ?? ?? ??", SK_Function, 0, 0, 0, 0, { 0, 0xF53FE0ULL,  0x1041320ULL }, 0, RS_Unresolved },
    { "FLpat_RebuildPattern",        "55 41 56 41 55 57 56 53 48 83 EC 68 48 8B EC 89 4D 20 88 55 27", SK_Function, 0, 0, 0, 0, { 0, 0x11D4140ULL, 0x12CF620ULL }, 0, RS_Unresolved },
    { "FLpat_NotifyChanged",         "56 53 48 83 EC 28 48 8B 05 ?? ?? ?? ?? 48 83 38 00 74 4C 48 8B 05 ?? ?? ?? ??", SK_Function, 0, 0, 0, 0, { 0, 0xF53D30ULL,  0x1041070ULL }, 0, RS_Unresolved },
    { "FLui_RefreshEditors",         "57 56 53 48 83 EC 20 33 C0 48 8B 0D ?? ?? ?? ?? 8B 79 10 83 EF 01 89 C3 3B DF 7F 33", SK_Function, 0, 0, 0, 0, { 0, 0xD421C0ULL,  0xE43D70ULL }, 0, RS_Unresolved },
    { "FLcr_RefreshRack",            "56 53 48 83 EC 28 48 8B 05 ?? ?? ?? ?? 8B 08", SK_Function, 0, 0, 0, 0, { 0, 0x107EAD0ULL, 0x11804A0ULL }, 0, RS_Unresolved },
    { "FLpat_GetNoteRecorder",       "57 56 53 48 83 EC 20 89 CB 40 89 D6 48 8D 05 ?? ?? ?? ?? 48 63 CB 48 C1 E1 03 48 8D 0C 49 48 8D 3C C8 48 ?? ?? ?? ?? 75 3C", SK_Function, 0, 0, 0, 0, { 0, 0x11D4080ULL, 0x12CF560ULL }, 0, RS_Unresolved },
    { "FLpat_GetParamRecorder",      "57 56 53 48 83 EC 20 89 CB 40 89 D6 48 8D 05 ?? ?? ?? ?? 48 63 CB 48 C1 E1 03 48 8D 0C 49 48 8D 3C C8 48 ?? ?? ?? ?? 75 3F", SK_Function, 0, 0, 0, 0, { 0, 0x11D4000ULL, 0x12CF4E0ULL }, 0, RS_Unresolved },
    { "FLpat_RecordNoteOn",          "55 41 56 41 55 57 56 53 48 83 EC 48 48 8B EC 48 89 CB 89 D6 44 89 C7 45 89 CD 48 89 D9", SK_Function, 0, 0, 0, 0, { 0, 0xF6D740ULL,  0x105ABC0ULL }, 0, RS_Unresolved },
    { "FLpat_RecordNoteOff",         "41 56 41 55 57 56 53 48 83 EC 20 48 89 CB 89 D6 44 89 C7 44 8B 6B 14 41 83 ED 01 45 8B F1", SK_Function, 0, 0, 0, 0, { 0, 0xF6D880ULL,  0x105AD00ULL }, 0, RS_Unresolved },
    { "FLpat_SetCurrentPattern",     "55 53 48 83 EC 68 48 8B EC 48 89 6D 28 89 8D ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ??", SK_Function, 0, 0, 0, 0, { 0, 0xCBB300ULL,  0xDCE120ULL }, 0, RS_Unresolved },
    { "FLpat_IsPatternEmpty",        "48 8D 05 ?? ?? ?? ?? 48 63 C9 48 C1 E1 03 48 8D 0C 49 48 8D 0C C8", SK_Function, 0, 0, 0, 0, { 0, 0x11DB510ULL, 0x12D6BF0ULL }, 0, RS_Unresolved },
    { "FLpat_NoteArrayClear",        "55 53 48 83 EC 38 48 8B EC 48 89 CB C7 43 14 00 00 00 00 48 89 5D 28 48 8B 45 28 48 63 40 14 48 3D 80 00 00 00 7F 05 B8 80 00 00 00", SK_Function, 0, 0, 0, 0, { 0, 0x11E0930ULL, 0x12DCC40ULL }, 0, RS_Unresolved },
    { "FLpat_SetPatternName",        "56 53 48 83 EC 28 89 CB 48 8D 05 ?? ?? ?? ??", SK_Function, 0, 0, 0, 0, { 0, 0x11D3960ULL, 0x12CEE40ULL }, 0, RS_Unresolved },
    { "FLcr_ChannelListGetItem",     "8B 41 10 83 E8 01 48 63 C0 4C 63 C2 4D 85 C0", SK_Function, 0, 0, 0, 0, { 0, 0xF00F80ULL,  0xFE4090ULL }, 0, RS_Unresolved },
    { "FLcr_SelectOneChannel",       "57 56 53 48 83 EC 20 89 CB 48 8B 05 ?? ?? ?? ?? 48 8B 00 8B 40 10", SK_Function, 0, 0, 0, 0, { 0, 0x10E3EB0ULL, 0x11E2300ULL }, 0, RS_Unresolved },
    { "FLcr_ApplyChannelSolo",       "55 53 48 83 EC 38 48 8B EC 48 89 CB 48 C7 43 60 FE FF FF FF 48 8B 05 ?? ?? ?? ?? 48 8B 00 48 8B 40 08 48 89 C1 48 8B 00 FF 90 A0 00 00 00 84 C0 74 54 8B 4B 18 48 83 7B 28 00", SK_Function, 0, 0, 0, 0, { 0, 0xE012F0ULL,  0xD5A3C0ULL }, 0, RS_Unresolved },
    { "FLcr_InsertChannel",          "41 55 57 56 53 48 83 EC 28 89 CB 40 89 D6 44 89 C7 48 8B 05 ?? ?? ?? ??", SK_Function, 0, 0, 0, 0, { 0, 0xF215E0ULL,  0xFFF2F0ULL }, 0, RS_Unresolved },
    { "FLcr_GetEventIDName",         "55 56 53 48 81 EC 60 01 00 00 48 8B EC 89 55 34", SK_Function, 0, 0, 0, 0, { 0, 0xF5CA00ULL,  0x1049E60ULL }, 0, RS_Unresolved },
    { "Delphi_DynArraySetLength",    "55 53 48 83 EC 28 48 8B EC 4C 89 4D 58 48 89 4D 40", SK_Function, 0, 0, 0, 0, { 0, 0x417FC0ULL,  0x417FC0ULL }, 0, RS_Unresolved },
    { "FLac_DeletePoint",            "41 55 57 56 53 48 83 EC 28 48 89 CB 41 89 D5 48 89 D9 44 89 EA 48 8B 03 FF 50 30 84 C0", SK_Function, 0, 0, 0, 0, { 0, 0xB30AD0ULL,  0xB97E30ULL }, 0, RS_Unresolved },
    { "FLmx_RefreshRouting",         "53 48 83 EC 20 48 89 CB E8 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 8B 08 BA 1A 00 00 00 41 B0 01", SK_Function, 0, 0, 0, 0, { 0, 0x11A5D20ULL, 0x12A0D50ULL }, 0, RS_Unresolved },
    { "FLpl_SetCurrentArrangement",  "55 56 53 48 83 EC 60 48 8B EC 89 4D 34 48 C7 45 50 ?? ?? ?? ??", SK_Function, 0, 0, 0, 0, { 0, 0x11FC880ULL, 0x12F8BC0ULL }, 0, RS_Unresolved },
    { "FLpl_SetTrackNameColor",      "55 53 48 83 EC 48 48 8B EC 4C 89 45 28 44 89 4D 34", SK_Function, 0, 0, 0, 0, { 0, 0x11E7940ULL, 0x12E3C40ULL }, 0, RS_Unresolved },
    { "FLpl_SetTrackSolo",           "55 41 55 57 56 53 48 83 EC 50 48 8B EC 48 89 4D 30 89 55 3C 44 89 C3 44 89 CE 48 8B 4D 30", SK_Function, 0, 0, 0, 0, { 0, 0x11E9810ULL, 0x12E5B10ULL }, 0, RS_Unresolved },
    { "FLpl_SetTrackSelection",      "53 48 83 EC 20 4D 0F B6 C0 41 83 F8 05 7F 51", SK_Function, 0, 0, 0, 0, { 0, 0x11E9C30ULL, 0x12E5F30ULL }, 0, RS_Unresolved },
    { "FLpl_RecountActiveClips",     "57 56 53 48 83 EC 20 48 89 CB C7 43 48 00 00 00 00", SK_Function, 0, 0, 0, 0, { 0, 0xF6E180ULL,  0x105B600ULL }, 0, RS_Unresolved },
    { "FLpl_SetClipSourceRange",     "55 56 53 48 83 EC 60 48 8B EC 66 44 ?? ?? ?? ?? 66 ?? ?? ?? ?? 66 ?? ?? ?? ?? 48 89 CB 66 0F 29 CE 66 0F 29 D7 F2 0F 10 05 ?? ?? ?? ??", SK_Function, 0, 0, 0, 0, { 0, 0xF71A70ULL,  0x105EF70ULL }, 0, RS_Unresolved },
    { "FLpr_OpenProject",            "55 53 48 83 EC 68 48 8B EC 48 C7 45 20 ?? ?? ?? ?? 48 C7 45 28 ?? ?? ?? ?? 48 C7 45 30 ?? ?? ?? ?? 48 C7 45 58 ?? ?? ?? ??", SK_Function, 0, 0, 0, 0, { 0, 0x10D50C0ULL, 0x11D52C0ULL }, 0, RS_Unresolved },
    { "FLpr_SetProjectPath",         "55 56 53 B8 A0 0B 00 00 48 2D 00 10 00 00", SK_Function, 0, 0, 0, 0, { 0, 0x10D2C90ULL, 0xF50CE0ULL }, 0, RS_Unresolved },
    { "FLpr_WriteFlpFile",           "55 53 48 81 EC 98 00 00 00 48 8B EC 48 89 4D 20 48 C7 45 38 ?? ?? ?? ??", SK_Function, 0, 0, 0, 0, { 0, 0x10D5A60ULL, 0x11D5C60ULL }, 0, RS_Unresolved },
    { "FL_AutoIncrementFileName",    "55 56 53 48 83 EC 70 48 8B EC 48 89 4D 30 48 89 55 38 48 C7 45 40 ?? ?? ?? ?? 48 C7 45 48 ?? ?? ?? ?? 48 C7 45 68 ?? ?? ?? ??", SK_Function, 0, 0, 0, 0, { 0, 0x7F7800ULL,  0x819880ULL }, 0, RS_Unresolved },
    { "FLpl_SetTimeSelection",       "55 41 55 57 56 53 48 83 EC 30 48 8B EC 48 89 CB 89 D6 44 89 C7 45 89 CD", SK_Function, 0, 0, 0, 0, { 0, 0xD41E60ULL,  0xE43A10ULL }, 0, RS_Unresolved },
    { "FLtr_AddTimelineMarkerCore",  "55 48 83 EC 40 48 8B EC 48 89 4D 30 89 55 38", SK_Function, 0, 0, 0, 0, { 0, 0xD523C0ULL,  0xE54090ULL }, 0, RS_Unresolved },
    { "FLar_AddArrangement",         "55 41 55 57 56 53 48 83 EC 30 48 8B EC 88 4D 22", SK_Function, 0, 0, 0, 0, { 0, 0x11FABC0ULL, 0x12F6F00ULL }, 0, RS_Unresolved },
    { "FLar_SetName",                "55 48 83 EC 30 48 8B EC 89 4D 2C 48 89 55 48", SK_Function, 0, 0, 0, 0, { 0, 0x11FB0D0ULL, 0x12F7410ULL }, 0, RS_Unresolved },
    { "FLar_CopyInto",               "55 48 83 EC 30 48 8B EC 48 8B 05 ?? ?? ?? ?? 48 63 C9 48 8B 04 C8", SK_Function, 0, 0, 0, 0, { 0, 0x11FB420ULL, 0x12F7760ULL }, 0, RS_Unresolved },
    { "FLar_GetName",                "53 48 83 EC 20 48 89 CB 48 89 D9 48 8B 05 ?? ?? ?? ?? 48 63 D2", SK_Function, 0, 0, 0, 0, { 0, 0x11FB160ULL, 0x12F74A0ULL }, 0, RS_Unresolved },
    { "FLar_Delete",                 "55 57 56 53 48 83 EC 38 48 8B EC 89 CB 40 89 D6", SK_Function, 0, 0, 0, 0, { 0, 0x11FB1C0ULL, 0x12F7500ULL }, 0, RS_Unresolved },
    { "TQuickEdit_ctor",             "55 53 48 83 EC 38 48 8B EC 48 89 6D 28 48 89 4D 50 88 55 58 4C 89 45 60 80 7D 58 00 74 12 48 8B 4D 50 48 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 89 45 50 90 48 8B 4D 50 33 D2 4C 8B 45 60 E8 ?? ?? ?? ?? 48 8B 4D 50 BA 03 00 00 00", SK_Function, 0, 0, 0, 0, { 0, 0x74C400ULL,  0x76BF30ULL }, 0, RS_Unresolved },
    { "MainFormPtr",                 "48 8B 0D ?? ?? ?? ?? 48 8B 09 89 C2 41 B8 00 00 00 40", SK_DataRef,  3, 7, 0, 0, { 0, 0x14A8750ULL, 0x15D8968ULL }, 0, RS_Unresolved },
    { "ToolbarFormPtr",              "48 8B 05 ?? ?? ?? ?? 48 8B 00 4C 8B A8 58 08 00 00", SK_DataRef,  3, 7, 0, 0, { 0, 0x14AA4C8ULL, 0x15DA830ULL }, 0, RS_Unresolved },
    { "MainBrowserPtr",              "48 8B 0D ?? ?? ?? ?? 48 8B 55 28 E8 ?? ?? ?? ?? 84 C0", SK_DataRef,  3, 7, 0, 0, { 0, 0x157FFB8ULL, 0x16B2968ULL }, 0, RS_Unresolved },
    { "StatusHintStr",               "48 8B 15 ?? ?? ?? ?? E8 ?? ?? ?? ?? 85 C0 75 1E 48 8B 05 ?? ?? ?? ??", SK_DataRef,  3, 7, 0, 0, { 0, 0x15817D0ULL, 0x16B41D8ULL }, 0, RS_Unresolved },
    { "BusyCounter",                 "83 3D ?? ?? ?? ?? 00 75 78 C7 44 24 28 ?? ?? ?? ??", SK_DataRef,  2, 7, 0, 0, { 0, 0x14BDBACULL, 0x15EE0C4ULL }, 0, RS_Unresolved },
    { "PPQ",                         "48 8B 05 ?? ?? ?? ?? 8B 08 B8 AB AA AA 2A F7 E9 C1 FA 02", SK_DataRef,  3, 7, 0, 0, { 0, 0x14A79F8ULL, 0x15D7B40ULL }, 0, RS_Unresolved },
    { "CurPatternIdx",               "48 8B 0D ?? ?? ?? ?? 48 63 09 48 C1 E1 03 48 8D 0C 49 48 ?? ?? ?? ?? 48 8D 55 38 48 8B 05 ?? ?? ?? ??", SK_DataRef,  3, 7, 0, 0, { 0, 0x14AB580ULL, 0x15DB998ULL }, 0, RS_Unresolved },
    { "PatternArray",                "48 8B 05 ?? ?? ?? ?? 48 8B 0D ?? ?? ?? ?? 48 63 09 48 C1 E1 03 48 8D 0C 49 8B 44 C8 48", SK_DataRef,  3, 7, 0, 0, { 0, 0x14AA0C8ULL, 0x15DA418ULL }, 0, RS_Unresolved },
    { "ChannelList",                 "48 8B 05 ?? ?? ?? ?? 48 83 38 00 0F 84 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 80 38 00 0F 85 ?? ?? ?? ??", SK_DataRef,  3, 7, 0, 0, { 0, 0x14A98D8ULL, 0x15D9B90ULL }, 0, RS_Unresolved },
    { "AutoLinkRegistry",            "48 8B 05 ?? ?? ?? ?? 48 8B 08 E8 ?? ?? ?? ?? 90 48 8D 4D 30 E8 ?? ?? ?? ?? 48 8D 4D 70", SK_DataRef,  3, 7, 0, 0, { 0, 0x14A81B8ULL, 0x15D8368ULL }, 0, RS_Unresolved },
    { "CurArrangementIdx",           "39 1D ?? ?? ?? ?? 75 18 83 FB 01 7C 0C 8B CB 83 E9 01", SK_DataRef,  2, 6, 0, 0, { 0, 0x149E8B4ULL, 0x15CE02CULL }, 0, RS_Unresolved },
    { "ProjectObject",               "48 8B 0D ?? ?? ?? ?? 33 D2 E8 ?? ?? ?? ?? 90 48 8D 4D 50", SK_DataRef,  3, 7, 0, 0, { 0, 0x1581200ULL, 0x16B3C08ULL }, 0, RS_Unresolved },
    { "ProjectPath",                 "48 8B 15 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 8D ?? ?? ?? ?? 48 8B 15 ?? ?? ?? ?? E8 ?? ?? ?? ??", SK_DataRef,  3, 7, 0, 0, { 0, 0x1581298ULL, 0x16B3C98ULL }, 0, RS_Unresolved },
    { "ProjectTitle",                "48 8B 15 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 83 BD 90 ?? ?? ?? ??", SK_DataRef,  3, 7, 0, 0, { 0, 0x15812A0ULL, 0x16B3CA0ULL }, 0, RS_Unresolved },
    { "SongPatternMode",             "48 8B 05 ?? ?? ?? ?? 83 38 01 75 7D E8 ?? ?? ?? ??", SK_DataRef,  3, 7, 0, 0, { 0, 0x14A8670ULL, 0x15D8860ULL }, 0, RS_Unresolved },
    { "PatternNameArrayBase",        "48 8D 05 ?? ?? ?? ?? 8B 54 24 28 83 EA 01 48 63 D2", SK_DataRef,  3, 7, 0, 0, { 0, 0x1803B68ULL, 0x1936CE8ULL }, 0, RS_Unresolved },
    { "NoteRecorderArrayBase",       "48 8D 0D ?? ?? ?? ?? 48 8B D0 48 C1 E2 03 48 8D 14 52 48 ?? ?? ?? ?? 0F 84 ?? ?? ?? ??", SK_DataRef,  3, 7, 0, 0, { 0, 0x1803B90ULL, 0x1936D10ULL }, 0, RS_Unresolved },
    // Readiness-gate song/transport object (flIsReady tempo-WRITE target A). Double-deref slot -> real global -> live song obj.
    { "ReadySongObj",                "48 8B 05 ?? ?? ?? ?? 48 8B 00 80 78 3C 00 75 39 48 8B 05 ?? ?? ?? ??", SK_DataRef,  3, 7, 0, 0, { 0, 0x14A9F40ULL, 0x15DA278ULL }, 0, RS_Unresolved },
    // ---- refined data globals (dataglobals2 pass; anchor+delta where noted) ----
    { "PlayStatePtr",                "48 8B 05 ?? ?? ?? ?? 83 38 01 0F 85 ?? ?? ?? ?? 48 8B 45 38", SK_DataRef,  3, 7, 0, 0, { 0, 0x14A81C0ULL, 0x15D8370ULL }, 0, RS_Unresolved },
    { "MixerTrackArray",             "48 8B 05 ?? ?? ?? ?? 48 8B 55 28 48 63 92 5C 01 00 00", SK_DataRef,  3, 7, 0, 0, { 0, 0x14A7EB0ULL, 0x15D8018ULL }, 0, RS_Unresolved },
    { "MixerTrackCount",             "48 8B 05 ?? ?? ?? ?? 8B 8D ?? ?? ?? ?? 3B 08 7C 38", SK_DataRef,  3, 7, 0, 0, { 0, 0x14A9850ULL, 0x15D9B08ULL }, 0, RS_Unresolved },
    { "RoutingMgr",                  "48 8B 05 ?? ?? ?? ?? 48 8B 08 E8 ?? ?? ?? ?? 8B 8D ?? ?? ?? ?? E8 ?? ?? ?? ?? 8B 85 ?? ?? ?? ??", SK_DataRef,  3, 7, 0, 0, { 0, 0x14A99A0ULL, 0x15D9C68ULL }, 0, RS_Unresolved },
    { "SongObject",                  "48 8B 05 ?? ?? ?? ?? 48 8B 00 48 8B 80 04 03 00 00 48 8B B8 B4 00 00 00 48 89 F8 F3 0F 10 40 0C", SK_DataRef,  3, 7, 0, 0, { 0, 0x14AAB88ULL, 0x15DAF40ULL }, 0, RS_Unresolved },
    { "ProjectController",           "48 8B 05 ?? ?? ?? ?? 48 8B 00 48 83 78 08 00 75 1C", SK_DataRef,  3, 7, 0, 0, { 0, 0x14ABCA8ULL, 0x15DC110ULL }, 0, RS_Unresolved },
    { "LoadInProgress",              "48 8B 05 ?? ?? ?? ?? 80 38 00 0F 85 ?? ?? ?? ?? 48 0F ?? ?? ?? ?? 22 05 ?? ?? ?? ?? 3A 05 ?? ?? ?? ??", SK_DataRef,  3, 7, 0, 0, { 0, 0x14A8748ULL, 0x15D8960ULL }, 0, RS_Unresolved },
    { "QuickEditVMT",                "48 8B 15 ?? ?? ?? ?? E8 ?? ?? ?? ?? 84 C0 74 38 90", SK_DataRef,  3, 7, 0, 200, { 0, 0x7466B8ULL, 0x7661E8ULL }, 0, RS_Unresolved },
    { "DynArrayTypeInfo",            "4C 8B 05 ?? ?? ?? ?? 4D 33 C9 48 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 45 30 48 8B 40 30 48 85 C0", SK_DataRef,  3, 7, 0, 8, { 0, 0xB2C678ULL, 0xB93928ULL }, 0, RS_Unresolved },
    // ---- window-host embed classRef (load-bearing; window-host wire path opts in via symAddr("HostClassRef")) ----
    // FALLBACK-ONLY (per-version hardcoded, no cross-version pattern): this is a Delphi classRef embedded in
    // const .text, not reachable by a clean single-instr RIP xref. 2026 addr recovered via VMT-slot
    // disambiguation (2026-07-09): the TScriptDialog-unique virtual override `paint` (2025 0xcf46f0 → 2026
    // 0xd9a030, VMT slot 0x1b8) occurs in exactly ONE 2026 VMT slot (0xd99530) → VMT_2026 0xd99360 → classRef
    // = VMT+0x18 = 0xd99378. Verified at 6 offsets + the classRef self-check (*(0xcf3888)=0x5df850 ⟷
    // *(0xd99378)=0x619130, same method cross-version). Refuses only on UNKNOWN versions (fail-safe).
    { "HostClassRef",                "", SK_DataRef, 0, 0, 0, 0, { 0, 0xCF3888ULL, 0xD99378ULL }, 0, RS_Unresolved },
};
static const int g_symCount = (int)(sizeof(g_syms) / sizeof(g_syms[0]));

static bool      g_symsResolved = false;
static FlVersion g_flVersion    = FLV_Unknown;

// ================================ pattern parsing ================================
static int sig_hexNib(char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

// Parse an IDA-style string ("48 8B 05 ? ? ? ? E8") into bytes+mask and choose an anchor byte — a rare
// fixed byte (skipping the common 0x00/0x48/0xCC/0xFF) for memchr acceleration. Falls back to the first
// fixed byte if none are "rare". Returns false if empty or all-wildcard (no anchorable fixed byte).
bool parsePattern(const char* ida, Pattern& out)
{
    out.len = 0; out.anchorIdx = -1; out.anchorVal = 0; out.valid = false;
    if (!ida) return false;
    const char* p = ida;
    while (*p && out.len < 96) {
        while (*p == ' ' || *p == '\t') p++;
        if (!*p) break;
        if (*p == '?') {
            out.bytes[out.len] = 0; out.mask[out.len] = false; out.len++;
            p++; if (*p == '?') p++;                 // accept "?" or "??"
        } else {
            int hi = sig_hexNib(p[0]); if (hi < 0) return false;
            int lo = sig_hexNib(p[1]); if (lo < 0) return false;
            out.bytes[out.len] = (unsigned char)((hi << 4) | lo);
            out.mask[out.len] = true; out.len++;
            p += 2;
        }
    }
    if (out.len == 0) return false;
    // anchor = first fixed byte that is NOT a common/high-frequency value
    for (int i = 0; i < out.len; i++) {
        if (!out.mask[i]) continue;
        unsigned char b = out.bytes[i];
        if (b != 0x00 && b != 0x48 && b != 0xCC && b != 0xFF) { out.anchorIdx = i; out.anchorVal = b; break; }
    }
    if (out.anchorIdx < 0) {                          // none rare — take the first fixed byte
        for (int i = 0; i < out.len; i++) if (out.mask[i]) { out.anchorIdx = i; out.anchorVal = out.bytes[i]; break; }
    }
    if (out.anchorIdx < 0) return false;              // no fixed byte at all → not anchorable
    out.valid = true;
    return true;
}

// ================================ PE section walk ================================
// HMODULE == image base. Collect every section that is MEM_EXECUTE + CNT_CODE. POD-only + SEH-guarded so
// a malformed/partial header can never fault the resolver. Returns the number of ranges written.
int getExecRanges(HMODULE mod, ExecRange* out, int maxOut)
{
    int count = 0;
    __try {
        const unsigned char* base = (const unsigned char*)mod;
        const IMAGE_DOS_HEADER* dos = (const IMAGE_DOS_HEADER*)base;
        if (dos->e_magic != IMAGE_DOS_SIGNATURE) return 0;
        const IMAGE_NT_HEADERS64* nt = (const IMAGE_NT_HEADERS64*)(base + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE) return 0;
        const IMAGE_SECTION_HEADER* sec = IMAGE_FIRST_SECTION(nt);
        int n = nt->FileHeader.NumberOfSections;
        for (int i = 0; i < n && count < maxOut; i++) {
            DWORD ch = sec[i].Characteristics;
            if ((ch & IMAGE_SCN_MEM_EXECUTE) && (ch & IMAGE_SCN_CNT_CODE)) {
                DWORD vsize = sec[i].Misc.VirtualSize;
                if (vsize == 0) vsize = sec[i].SizeOfRawData;
                if (vsize == 0) continue;
                out[count].begin = base + sec[i].VirtualAddress;
                out[count].end   = out[count].begin + vsize;
                count++;
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
    return count;
}

// ================================ scanning ================================
// Masked compare of the whole pattern at p. Caller guarantees [p, p+len) is inside a code range.
bool matchAt(const Pattern& pat, const unsigned char* p)
{
    for (int i = 0; i < pat.len; i++) if (pat.mask[i] && pat.bytes[i] != p[i]) return false;
    return true;
}

// Scan one range for the pattern using memchr on the anchor byte. Increments *count (capped: it stops as
// soon as *count reaches 2 — the caller only needs "unique or not") and records the first hit. POD-only +
// SEH-guarded.
static void scanRange(const ExecRange& r, const Pattern& pat, uint64_t* firstAddr, int* count)
{
    __try {
        int L = pat.len, ai = pat.anchorIdx;
        if (!r.begin || !r.end || r.end < r.begin + L) return;
        const unsigned char* qMin = r.begin + ai;
        const unsigned char* qMax = r.end - L + ai;         // inclusive last anchor position
        const unsigned char* p = qMin;
        while (p <= qMax) {
            const unsigned char* q = (const unsigned char*)memchr(p, pat.anchorVal, (size_t)(qMax - p + 1));
            if (!q) break;
            const unsigned char* S = q - ai;                // candidate pattern start
            if (matchAt(pat, S)) {
                if (*count == 0) *firstAddr = (uint64_t)S;
                (*count)++;
                if (*count >= 2) return;                    // early-out: ambiguity already proven
            }
            p = q + 1;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { /* leave count as-is */ }
}

// Resolve to a UNIQUE match address across all ranges. 0 hits = NotFound, >1 = Ambiguous (both FAIL).
ResolveStatus resolveUnique(const Pattern& pat, const ExecRange* ranges, int nRanges, uint64_t* outAddr)
{
    if (!pat.valid || nRanges <= 0) return RS_NotFound;
    uint64_t first = 0; int count = 0;
    for (int i = 0; i < nRanges && count < 2; i++) scanRange(ranges[i], pat, &first, &count);
    if (count == 0) return RS_NotFound;
    if (count > 1)  return RS_Ambiguous;
    *outAddr = first;
    return RS_Ok;
}

// RIP-relative data global: target = (matchStart + instrEnd) + (int32)disp32, disp32 @ matchStart+dispOff.
ResolveStatus resolveDataRef(const Pattern& pat, const ExecRange* ranges, int nRanges,
                             int dispOff, int instrEnd, int dataDelta, uint64_t* outAddr)
{
    uint64_t matchStart = 0;
    ResolveStatus st = resolveUnique(pat, ranges, nRanges, &matchStart);
    if (st != RS_Ok) return st;
    bool ok = false; uint64_t target = 0;
    __try {
        int32_t disp = *(const int32_t*)(matchStart + (uint64_t)dispOff);
        target = (matchStart + (uint64_t)instrEnd) + (int64_t)disp + (int64_t)dataDelta;
        ok = true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { ok = false; }
    if (!ok) return RS_SelfCheckFail;
    *outAddr = target;
    return RS_Ok;
}

// Struct/vtable displacement: returns the sign-extended ModRM displacement as an OFFSET (not an address).
ResolveStatus resolveVtableSlot(const Pattern& pat, const ExecRange* ranges, int nRanges,
                                int dispOff, int dispSize, int64_t* outOffset)
{
    uint64_t matchStart = 0;
    ResolveStatus st = resolveUnique(pat, ranges, nRanges, &matchStart);
    if (st != RS_Ok) return st;
    bool ok = false; int64_t off = 0;
    __try {
        if (dispSize == 1)      off = (int64_t)*(const int8_t*)(matchStart + (uint64_t)dispOff);
        else                    off = (int64_t)*(const int32_t*)(matchStart + (uint64_t)dispOff);
        ok = true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { ok = false; }
    if (!ok) return RS_SelfCheckFail;
    *outOffset = off;
    return RS_Ok;
}

// ================================ version detect ================================
// Authoritative signal = the FileVersion resource major number (25 → 2025, 26 → 2026). Image size is a
// secondary SANITY signal only (logged on mismatch, never overriding — do NOT assume newer=smaller).
FlVersion detectFlVersion(HMODULE mod)
{
    char path[MAX_PATH];
    if (GetModuleFileNameA(mod, path, MAX_PATH) == 0) return FLV_Unknown;

    FlVersion ver = FLV_Unknown; WORD major = 0;
    DWORD handle = 0;
    DWORD sz = GetFileVersionInfoSizeA(path, &handle);
    if (sz) {
        std::vector<unsigned char> buf(sz);
        if (GetFileVersionInfoA(path, 0, sz, buf.data())) {
            VS_FIXEDFILEINFO* ffi = NULL; UINT len = 0;
            if (VerQueryValueA(buf.data(), "\\", (void**)&ffi, &len) && ffi) {
                major = HIWORD(ffi->dwFileVersionMS);
                if      (major == 25) ver = FLV_2025_25_2_5;
                else if (major == 26) ver = FLV_2026_26_1_0;
            }
        }
    }

    // secondary image-size sanity (2025 ≈ 51MB, 2026 ≈ 22MB) — log only, never override.
    MODULEINFO mi{};
    if (GetModuleInformation(GetCurrentProcess(), mod, &mi, sizeof(mi))) {
        double mb = (double)mi.SizeOfImage / (1024.0 * 1024.0);
        if (ver == FLV_2025_25_2_5 && mb < 30.0)
            sig_logf("sigscan: WARN version=2025 but image=%.1fMB (expected ~51MB)", mb);
        if (ver == FLV_2026_26_1_0 && mb > 40.0)
            sig_logf("sigscan: WARN version=2026 but image=%.1fMB (expected ~22MB)", mb);
    }
    sig_logf("sigscan: detectFlVersion major=%u -> ver=%d", (unsigned)major, (int)ver);
    return ver;
}

// ================================ fallback + self-check ================================
// mod + (ghidra[ver] - 0x400000). Returns 0 (refuse) on an unknown version or an unknown-for-this-version
// address — a fallback is NEVER trusted on FLV_Unknown.
uint64_t fallbackAddr(const SymEntry& e, HMODULE mod, FlVersion ver)
{
    if (ver <= FLV_Unknown || ver >= FLV_COUNT) return 0;
    uint64_t g = e.ghidra[ver];
    if (!g) return 0;
    return (uint64_t)mod + (g - SIG_GHIDRA_BASE);
}

// SEH-guarded masked byte compare of the pattern at addr (used to self-check a fallback / a match).
bool verifyBytes(uint64_t addr, const Pattern& pat)
{
    bool ok = false;
    __try {
        const unsigned char* p = (const unsigned char*)addr;
        ok = true;
        for (int i = 0; i < pat.len; i++) {
            if (pat.mask[i] && pat.bytes[i] != p[i]) { ok = false; break; }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { ok = false; }
    return ok;
}

// ================================ resolve-all (once, at init) ================================
const char* sig_statusStr(ResolveStatus s)
{
    switch (s) {
        case RS_Ok:            return "ok";
        case RS_NotFound:      return "not-found";
        case RS_Ambiguous:     return "ambiguous";
        case RS_VersionLocked: return "version-locked";
        case RS_SelfCheckFail: return "self-check-fail";
        case RS_NoModule:      return "no-module";
        default:               return "unresolved";
    }
}

void sig_resolveAll()
{
    if (g_symsResolved) return;
    HMODULE mod = GetModuleHandleA("FLEngine_x64.dll");
    if (!mod) return;                                    // FL engine not loaded yet → retry on next call
    g_symsResolved = true;                               // commit: the module is present

    FlVersion ver = detectFlVersion(mod);
    g_flVersion = ver;

    ExecRange ranges[16];
    int nr = getExecRanges(mod, ranges, 16);
    sig_logf("sigscan: FLEngine=%p ver=%d execRanges=%d syms=%d", (void*)mod, (int)ver, nr, g_symCount);

    int okN = 0, failN = 0;
    for (int i = 0; i < g_symCount; i++) {
        SymEntry& e = g_syms[i];

        // 1) try the signature (if any).
        ResolveStatus sigStatus = RS_Unresolved;
        uint64_t      sigAddr   = 0;
        Pattern       pat;       pat.valid = false;
        bool          havePat   = (e.pattern && e.pattern[0]);
        if (havePat) {
            if (parsePattern(e.pattern, pat) && nr > 0) {
                if (e.kind == SK_Function) {
                    sigStatus = resolveUnique(pat, ranges, nr, &sigAddr);
                } else if (e.kind == SK_DataRef) {
                    sigStatus = resolveDataRef(pat, ranges, nr, e.dispOff, e.instrEnd, e.dataDelta, &sigAddr);
                } else { // SK_VtableSlot
                    int64_t off = 0;
                    sigStatus = resolveVtableSlot(pat, ranges, nr, e.dispOff, e.dispSize, &off);
                    sigAddr = (uint64_t)off;
                }
            } else {
                sigStatus = RS_NotFound;                 // unparsable / no ranges
            }
        }

        // 2) per-version hardcoded fallback (0 = refuse: unknown version or unknown address).
        uint64_t fbAddr = fallbackAddr(e, mod, ver);

        // 3) decide — signature wins; fallback only when trustworthy.
        if (sigStatus == RS_Ok) {
            e.addr = sigAddr; e.status = RS_Ok;
            if (fbAddr && e.kind != SK_VtableSlot && fbAddr != sigAddr)
                sig_logf("sigscan: DRIFT %s sig=0x%llx fb=0x%llx (using sig)",
                         e.name, (unsigned long long)sigAddr, (unsigned long long)fbAddr);
        } else if (fbAddr) {
            if (havePat && e.kind == SK_Function) {
                // We have a signature but it didn't resolve uniquely — only trust the fallback if its
                // bytes still match the signature (self-check); otherwise REFUSE (never guess).
                if (verifyBytes(fbAddr, pat)) { e.addr = fbAddr; e.status = RS_Ok; }
                else { e.addr = 0; e.status = RS_SelfCheckFail; }
            } else {
                // fallback-only entry (no signature yet) — the fallback is the only info; trust it (it is
                // version-keyed and only produced on a known version).
                e.addr = fbAddr; e.status = RS_Ok;
            }
        } else {
            e.addr = 0;
            e.status = (ver == FLV_Unknown) ? RS_VersionLocked
                     : (sigStatus != RS_Unresolved ? sigStatus : RS_NotFound);
        }

        if (e.status == RS_Ok) {
            okN++;
        } else {
            failN++;
            sig_logf("sigscan: UNRESOLVED %s (%s)", e.name, sig_statusStr(e.status));
        }
    }
    sig_logf("sigscan: resolved ok=%d fail=%d ver=%d", okN, failN, (int)ver);
}

// ================================ lookup + diagnostics ================================
SymEntry* sig_findSym(const char* name)
{
    if (!name) return NULL;
    for (int i = 0; i < g_symCount; i++)
        if (g_syms[i].name && strcmp(g_syms[i].name, name) == 0) return &g_syms[i];
    return NULL;
}

uint64_t sig_addr(const char* name)
{
    SymEntry* e = sig_findSym(name);
    return (e && e->status == RS_Ok) ? e->addr : 0;
}

// Reverse lookup for the LEGACY hex wire path: the C# side hardcodes 2025 Ghidra addresses. Given one,
// return its RESOLVED (version-correct) runtime address if that address is a known symbol's 2025 slot,
// else 0. This makes the entire un-migrated hardcoded-hex call surface version-correct without editing
// every call site — a table hit yields the right address on ANY resolved version.
uint64_t sig_addrByGhidra2025(uint64_t ghidra25)
{
    for (int i = 0; i < g_symCount; i++) {
        SymEntry& e = g_syms[i];
        if (e.ghidra[FLV_2025_25_2_5] == ghidra25 && e.status == RS_Ok && e.addr) return e.addr;
    }
    return 0;
}

FlVersion sig_version() { return g_flVersion; }

std::string sig_symsJson()
{
    int ok = 0, fail = 0;
    for (int i = 0; i < g_symCount; i++) (g_syms[i].status == RS_Ok ? ok : fail)++;

    std::string s = "{\"ver\":" + std::to_string((int)g_flVersion)
                  + ",\"ok\":" + std::to_string(ok)
                  + ",\"fail\":" + std::to_string(fail)
                  + ",\"unresolved\":[";
    bool first = true;
    for (int i = 0; i < g_symCount; i++) {
        if (g_syms[i].status == RS_Ok) continue;
        if (!first) s += ",";
        first = false;
        s += "{\"name\":\"";
        s += (g_syms[i].name ? g_syms[i].name : "");
        s += "\",\"why\":\"";
        s += sig_statusStr(g_syms[i].status);
        s += "\"}";
    }
    s += "]}";
    return s;
}
