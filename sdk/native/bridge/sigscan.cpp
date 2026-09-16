// sigscan.cpp — runtime signature-scanning subsystem (see sigscan.h + re/version-portability-design.md).
//
// FAIL-SAFE: refuse, never guess. Uniqueness is mandatory (0 or >1 matches both FAIL). A per-version
// hardcoded fallback is used ONLY on a known FL version, and only after a byte self-check when a
// signature is also present. Every raw memory read is SEH-guarded (POD-only __try frames — MSVC C2712).

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <psapi.h>
#include "sigscan.h"
#include "version_scanner.h"

#include <stdio.h>
#include <stdarg.h>
#include <string.h>
#include <string>
#include <vector>
#include <atomic>
#include <mutex>

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

// ================================ signature table ================================
// ghidra[] contains verified addresses for the exact binaries identified by FlVersion. Patterns
// resolve across builds; fallback-only entries remain unavailable on unverified binaries.
static const SymEntry g_symbolDefinitions[] = {
    { "NativeWindowClassRef", "", SK_DataRef, 0, 0, 0, 0, { 0, 0x721BA0ULL, 0 }, 0, RS_Unresolved },
    { "FLui_ControlSetVisible", "57 56 53 48 83 EC 20 48 89 CB 40 89 D6 40 38 B3 A9 00 00 00 74 5F", SK_Function, 0, 0, 0, 0, { 0, 0x5D08C0ULL, 0 }, 0, RS_Unresolved },
    // name / pattern (IDA sig) / kind / dispOff / instrEnd / dispSize / dataDelta / ghidra[Unknown,2025,2026] / addr / status
    { "FLui_CreateFormFromClassRef", "48 83 EC 28 48 8B 05 ?? ?? ?? ?? 48 8B 00 49 89 C8 49 89 D1", SK_Function, 0, 0, 0, 0, { 0, 0x10C2AA0ULL, 0x11C2870ULL }, 0, RS_Unresolved },
    // RTTI identifies TApplication.Title, NOT a form caption. The old name remains lookup-only below.
    // Use FLwp_SetButtonCaption (TControl.Caption, Delphi UnicodeString) for form/control captions.
    { "FLapp_SetTitle",             "57 56 53 48 83 EC 20 48 89 CB 48 89 D6 48 8B 8B 10 01 00 00", SK_Function, 0, 0, 0, 0, { 0, 0x841690ULL,  0x869370ULL }, 0, RS_Unresolved },
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
    { "FLac_CreateForEvent",         "55 53 48 81 EC 58 01 00 00 48 8B EC 48 C7 45 30 00 00 00 00 48 C7 45 38 00 00 00 00 48 C7 45 40", SK_Function, 0, 0, 0, 0, { 0, 0x108A1A0ULL, 0 }, 0, RS_Unresolved },
    { "FLmx_RefreshRouting",         "53 48 83 EC 20 48 89 CB E8 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 8B 08 BA 1A 00 00 00 41 B0 01", SK_Function, 0, 0, 0, 0, { 0, 0x11A5D20ULL, 0x12A0D50ULL }, 0, RS_Unresolved },
    { "FLpl_SetCurrentArrangement",  "55 56 53 48 83 EC 60 48 8B EC 89 4D 34 48 C7 45 50 ?? ?? ?? ??", SK_Function, 0, 0, 0, 0, { 0, 0x11FC880ULL, 0x12F8BC0ULL }, 0, RS_Unresolved },
    // FLpl_GetCurrentArrangement — leaf `return *(arrArray + curIdx*8)`; EVERY playlist/clip tool calls it
    // (PlaylistRootAsync). Was the one function genuinely missing from this table → list_clips/list_playlist_
    // tracks refused on 2026. 2026 addr 0x12DF5D0 (RE 2026-07-10). Unique sig in both images (leaf, no prologue).
    { "FLpl_GetCurrentArrangement",  "48 8B 05 ?? ?? ?? ?? 48 63 0D ?? ?? ?? ?? 48 8B 04 C8 C3", SK_Function, 0, 0, 0, 0, { 0, 0x11E32C0ULL, 0x12DF5D0ULL }, 0, RS_Unresolved },
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
    // Recent-projects MRU array base (static array of 49 Delphi-string ptrs, stride 8; read by list_recent_
    // projects as base+i*8 → also listed in kArrays[] for the range-aware reverse lookup). LEA target; the
    // FIRST `LEA RCX` in FLproj_SetProjectPath. 2026 base 0x16B3D20 (RE 2026-07-10).
    { "RecentProjectsArray",         "48 8D 0D ?? ?? ?? ?? 48 8B 15 ?? ?? ?? ?? E8 ?? ?? ?? ?? B9 04 00 00 00 33 D2 E8 ?? ?? ?? ??", SK_DataRef,  3, 7, 0, 0, { 0, 0x1581320ULL, 0x16B3D20ULL }, 0, RS_Unresolved },
    { "ProjectController",           "48 8B 05 ?? ?? ?? ?? 48 8B 00 48 83 78 08 00 75 1C", SK_DataRef,  3, 7, 0, 0, { 0, 0x14ABCA8ULL, 0x15DC110ULL }, 0, RS_Unresolved },
    { "LoadInProgress",              "48 8B 05 ?? ?? ?? ?? 80 38 00 0F 85 ?? ?? ?? ?? 48 0F ?? ?? ?? ?? 22 05 ?? ?? ?? ?? 3A 05 ?? ?? ?? ??", SK_DataRef,  3, 7, 0, 0, { 0, 0x14A8748ULL, 0x15D8960ULL }, 0, RS_Unresolved },
    { "QuickEditVMT",                "48 8B 15 ?? ?? ?? ?? E8 ?? ?? ?? ?? 84 C0 74 38 90", SK_DataRef,  3, 7, 0, 200, { 0, 0x7466B8ULL, 0x7661E8ULL }, 0, RS_Unresolved },
    { "DynArrayTypeInfo",            "4C 8B 05 ?? ?? ?? ?? 4D 33 C9 48 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 45 30 48 8B 40 30 48 85 C0", SK_DataRef,  3, 7, 0, 8, { 0, 0xB2C678ULL, 0xB93928ULL }, 0, RS_Unresolved },
    // ---- window-host embed classRef (load-bearing; window-host wire path opts in via symAddr("HostClassRef")) ----
    // The selected scanner recovers this through validated Delphi class/VMT metadata, not a prologue.
    // These recorded addresses provide an additional agreement check on exact known builds.
    { "HostClassRef",                "", SK_DataRef, 0, 0, 0, 0, { 0, 0xCF3888ULL, 0xD99378ULL }, 0, RS_Unresolved },
    // Legacy consumers formerly bypassed the catalog on 2025; list every alias so failures on other
    // builds are visible to capability diagnostics. Unverified recipes stay unavailable.
    { "FLmx_SetRouteActiveCore", "55 41 55 57 56 53 48 83 EC 70 48 8B EC 48 89 4D 38 89 55 ?? 44 89 45 ?? 44 89 4D ?? 48 C7 45 68 00 00 00 00 48 C7 45 60 00 00 00 00 48 C7 45 58 00 00 00 00 90 8B 4D ?? 8B 55 ?? E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ??", SK_Function, 0, 0, 0, 0, { 0, 0x11A67F0ULL, 0 }, 0, RS_Unresolved },
    { "FLpl_RepaintPlaylist", "57 56 53 48 83 EC 20 48 89 CB 48 8B B3 ?? 0D 00 00 48 8B 83 18 08 00 00 48 85 C0 74 ?? 48 89 F1 8B 80 C0 03 00 00", SK_Function, 0, 0, 0, 0, { 0, 0xDA40C0ULL, 0 }, 0, RS_Unresolved },
    { "FLpl_SetClipMuted", "84 D2 74 ?? 80 49 13 20 EB ?? 80 61 13 DF C3 CC 55 56 53 48 83 EC 60 48 8B EC 66 44 0F 7F 45 50 66 0F 7F 7D 40 66 0F 7F 75 30 48 89 CB", SK_Function, 0, 0, 0, 0, { 0, 0xF71A60ULL, 0 }, 0, RS_Unresolved },
    { "SongArrangement", "48 8B 0D ?? ?? ?? ?? 48 8B 09 89 C2 48 8B 45 40 44 8B 40 30 41 B1 01 C6 44 24 20 01 E8 ?? ?? ?? ?? 90 48 8B 4D 70 48 8B 55 58 E8 ?? ?? ?? ??", SK_DataRef, 3, 7, 0, 0, { 0, 0x14ABA80ULL, 0 }, 0, RS_Unresolved },
    { "FLpr_SaveProjectToFlp", "55 53 48 81 EC ?? 00 00 00 48 8B EC 48 C7 45 30 00 00 00 00 48 C7 45 68 00 00 00 00 48 C7 45 60 00 00 00 00 48 C7 45 58 00 00 00 00 48 C7 45 50 00 00 00 00 48 C7 45 38 00 00 00 00 48 C7 45 70 00 00 00 00 48 C7 45 78 00 00 00 00 48 C7 85 88 00 00 00 00 00 00 00", SK_Function, 0, 0, 0, 0, { 0, 0x10D6190ULL, 0 }, 0, RS_Unresolved },
    { "FLpl_GetArrangementCount", "48 8B 05 ?? ?? ?? ?? 48 85 C0 74 ?? 48 8B 40 F8 C3", SK_Function, 0, 0, 0, 0, { 0, 0x11FB1A0ULL, 0 }, 0, RS_Unresolved },
    { "FLui_MarkKbCapture",          "", SK_Function, 0, 0, 0, 0, { 0, 0x802820ULL, 0 }, 0, RS_Unresolved },
    { "FLbrz_SelectTabById",         "", SK_Function, 0, 0, 0, 0, { 0, 0x9AC590ULL, 0 }, 0, RS_Unresolved },
    { "TQuickEdit_SetText",          "", SK_Function, 0, 0, 0, 0, { 0, 0x74C260ULL, 0 }, 0, RS_Unresolved },
    { "TQuickEdit_Refresh",          "", SK_Function, 0, 0, 0, 0, { 0, 0x74BB70ULL, 0 }, 0, RS_Unresolved },
    { "LoadingFlag",                "", SK_DataRef, 0, 0, 0, 0, { 0, 0x157F667ULL, 0 }, 0, RS_Unresolved },
    // SelectionRefresh snapshots the real loop bounds independently of the toolbar seek domain.
    { "TransportRangeStart", "57 56 53 48 83 EC 30 48 8B 05 ?? ?? ?? ?? 8B 18 48 8B 05 ?? ?? ?? ?? 8B 30 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? C7 00 00 00 00 00", SK_DataRef, 10, 14, 0, 0, { 0, 0x14A95F8ULL, 0 }, 0, RS_Unresolved },
    { "TransportRangeEnd", "57 56 53 48 83 EC 30 48 8B 05 ?? ?? ?? ?? 8B 18 48 8B 05 ?? ?? ?? ?? 8B 30 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? C7 00 00 00 00 00", SK_DataRef, 19, 23, 0, 0, { 0, 0x14ABB38ULL, 0 }, 0, RS_Unresolved },
    { "FLmx_InsertTracks", "55 41 56 41 55 57 56 53 48 83 EC 58 48 8B EC 89 4D 24 89 55 28 48 C7 45 30 00 00 00 00 48 C7 45 38 00 00 00 00 48 C7 45 40 00 00 00 00 48 C7 45 48 00 00 00 00 44 88 85 A0 00 00 00 90 B8 F6 01 00 00", SK_Function, 0, 0, 0, 0, { 0, 0x11A7B30ULL, 0 }, 0, RS_Unresolved },
    // Mixer disk-recording arm (live per-insert capture). Anchor: the main-thread callback behind FL's own
    // scripting `mixer.armTrack` (PyMethodDef 0x1518F58 in 26.1.3.5570 / 0x1407240 in 25.2.5.5319; callback
    // 0xD610E0 / 0xE06970). Its tail loads the track (value == -1 means toggle), compares the armed byte at
    // trackStruct + <disp32> (0x1470 / 0x145C; the same field mixer.isTrackArmed reads) and calls the setter
    // thunk with (RCX = trackStruct, DL = armed, R8 = 0), which forwards to the recorder object at +0x158 /
    // +0x1460. The CALL rel32 decodes to that thunk; the CMP disp32 is the armed-byte offset. Both are unique
    // on both installed binaries (analysis/verified-symbols-arm-2026-09-14.json).
    { "FLmx_SetTrackArmed", "48 8B 43 20 48 83 F8 FF 75 3C 48 8B 43 18 48 8B 0D ?? ?? ?? ?? 48 8B D0 48 69 D2 ?? ?? ?? ?? 48 8D 0C ?? 48 8B 15 ?? ?? ?? ?? 48 69 C0 ?? ?? ?? ?? 80 BC ?? ?? 14 00 00 00 0F 94 C2 4D 33 C0 E8 ?? ?? ?? ??", SK_DataRef, 64, 68, 0, 0, { 0, 0x11C59D0ULL, 0 }, 0, RS_Unresolved },
    { "MixerTrackArmedOffset", "48 8B 43 20 48 83 F8 FF 75 3C 48 8B 43 18 48 8B 0D ?? ?? ?? ?? 48 8B D0 48 69 D2 ?? ?? ?? ?? 48 8D 0C ?? 48 8B 15 ?? ?? ?? ?? 48 69 C0 ?? ?? ?? ?? 80 BC ?? ?? 14 00 00 00 0F 94 C2 4D 33 C0 E8 ?? ?? ?? ??", SK_VtableSlot, 52, 0, 4, 0, { 0, 0, 0 }, 0, RS_Unresolved },
};
static const int g_symCount = (int)(sizeof(g_symbolDefinitions) / sizeof(g_symbolDefinitions[0]));
static std::vector<SymEntry> g_syms(g_symbolDefinitions, g_symbolDefinitions + g_symCount);

static std::atomic<bool> g_symsResolved{false};
static std::mutex g_resolveMutex;
static FlFileVersion g_fileVersion{};
static std::unique_ptr<IFlSignatureScanner> g_scanner;

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
    while (*p) {
        while (*p == ' ' || *p == '\t') p++;
        if (!*p) break;
        if (out.len == 96) return false; // never silently truncate a signature
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
        if (*p && *p != ' ' && *p != '\t') return false;
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
static uint64_t loadedImageSize(HMODULE mod)
{
    MODULEINFO mi{};
    return mod && GetModuleInformation(GetCurrentProcess(), mod, &mi, sizeof(mi)) ? mi.SizeOfImage : 0;
}

int getExecRanges(HMODULE mod, ExecRange* out, int maxOut, uint64_t imageSize)
{
    if (!mod || !out || maxOut <= 0) return 0;
    if (!imageSize) imageSize = loadedImageSize(mod);
    if (imageSize < sizeof(IMAGE_DOS_HEADER)) return 0;
    int count = 0;
    __try {
        const unsigned char* base = (const unsigned char*)mod;
        const IMAGE_DOS_HEADER* dos = (const IMAGE_DOS_HEADER*)base;
        if (dos->e_magic != IMAGE_DOS_SIGNATURE) return 0;
        if (dos->e_lfanew <= 0 || imageSize < sizeof(IMAGE_NT_HEADERS64) ||
            (DWORD)dos->e_lfanew > imageSize - sizeof(IMAGE_NT_HEADERS64)) return 0;
        const IMAGE_NT_HEADERS64* nt = (const IMAGE_NT_HEADERS64*)(base + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC)
            return 0;
        const IMAGE_SECTION_HEADER* sec = IMAGE_FIRST_SECTION(nt);
        int n = nt->FileHeader.NumberOfSections;
        size_t sectionOffset = (const unsigned char*)sec - base;
        if (sectionOffset > imageSize || (size_t)n > (imageSize - sectionOffset) / sizeof(*sec))
            return 0;
        for (int i = 0; i < n; i++) {
            DWORD ch = sec[i].Characteristics;
            if ((ch & IMAGE_SCN_MEM_EXECUTE) && (ch & IMAGE_SCN_CNT_CODE)) {
                DWORD vsize = sec[i].Misc.VirtualSize;
                if (vsize == 0) vsize = sec[i].SizeOfRawData;
                if (vsize == 0) continue;
                if (count == maxOut || sec[i].VirtualAddress >= imageSize ||
                    vsize > imageSize - sec[i].VirtualAddress) return 0; // incomplete scan is unsafe
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
static bool scanRange(const ExecRange& r, const Pattern& pat, uint64_t* firstAddr, int* count)
{
    __try {
        int L = pat.len, ai = pat.anchorIdx;
        if (!r.begin || !r.end || r.end < r.begin) return false;
        if ((size_t)(r.end - r.begin) < (size_t)L) return true;
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
                if (*count >= 2) return true;               // early-out: ambiguity already proven
            }
            p = q + 1;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    return true;
}

// Resolve to a UNIQUE match address across all ranges. 0 hits = NotFound, >1 = Ambiguous (both FAIL).
ResolveStatus resolveUnique(const Pattern& pat, const ExecRange* ranges, int nRanges, uint64_t* outAddr)
{
    if (!outAddr) return RS_SelfCheckFail;
    *outAddr = 0;
    if (!pat.valid || !ranges || nRanges <= 0) return RS_NotFound;
    uint64_t first = 0; int count = 0;
    for (int i = 0; i < nRanges && count < 2; i++)
        if (!scanRange(ranges[i], pat, &first, &count)) return RS_SelfCheckFail;
    if (count == 0) return RS_NotFound;
    if (count > 1)  return RS_Ambiguous;
    *outAddr = first;
    return RS_Ok;
}

// RIP-relative data global: target = (matchStart + instrEnd) + (int32)disp32, disp32 @ matchStart+dispOff.
ResolveStatus resolveDataRef(const Pattern& pat, const ExecRange* ranges, int nRanges,
                             int dispOff, int instrEnd, int dataDelta, uint64_t* outAddr)
{
    if (!outAddr) return RS_SelfCheckFail;
    *outAddr = 0;
    if (dispOff < 0 || instrEnd < dispOff + 4 || instrEnd > pat.len || dispOff > pat.len - 4)
        return RS_SelfCheckFail;
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
    if (!outOffset) return RS_SelfCheckFail;
    *outOffset = 0;
    if ((dispSize != 1 && dispSize != 4) || dispOff < 0 || dispOff > pat.len - dispSize)
        return RS_SelfCheckFail;
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

// ================================ fallback + self-check ================================
// mod + (ghidra[ver] - 0x400000). Returns 0 (refuse) on an unknown version or an unknown-for-this-version
// address — a fallback is NEVER trusted on FLV_Unknown.
static uint64_t fallbackInImage(const SymEntry& e, HMODULE mod, FlVersion ver, uint64_t imageSize)
{
    if (!mod || ver <= FLV_Unknown || ver >= FLV_COUNT) return 0;
    uint64_t g = e.ghidra[ver];
    if (g < SIG_GHIDRA_BASE) return 0;
    if (g - SIG_GHIDRA_BASE >= imageSize) return 0;
    return (uint64_t)mod + (g - SIG_GHIDRA_BASE);
}

uint64_t fallbackAddr(const SymEntry& e, HMODULE mod, FlVersion ver)
{
    return fallbackInImage(e, mod, ver, loadedImageSize(mod));
}

// SEH-guarded masked byte compare of the pattern at addr (used to self-check a fallback / a match).
bool verifyBytes(uint64_t addr, const Pattern& pat)
{
    if (!addr || !pat.valid || pat.len <= 0) return false;
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

void resolveSymbol(SymEntry& e, HMODULE mod, FlVersion ver, const ExecRange* ranges, int nRanges, uint64_t imageSize)
{
    if (!imageSize) imageSize = loadedImageSize(mod);
    e.addr = 0;
    ResolveStatus sigStatus = RS_Unresolved;
    uint64_t      sigAddr   = 0;
    Pattern       pat{};
    bool          havePat   = (e.pattern && e.pattern[0]);
    if (havePat) {
        if (parsePattern(e.pattern, pat)) {
            if (e.kind == SK_Function) {
                sigStatus = resolveUnique(pat, ranges, nRanges, &sigAddr);
            } else if (e.kind == SK_DataRef) {
                sigStatus = resolveDataRef(pat, ranges, nRanges, e.dispOff, e.instrEnd, e.dataDelta, &sigAddr);
            } else { // SK_VtableSlot
                int64_t off = 0;
                sigStatus = resolveVtableSlot(pat, ranges, nRanges, e.dispOff, e.dispSize, &off);
                sigAddr = (uint64_t)off;
            }
        } else {
            sigStatus = RS_SelfCheckFail;
        }
    }

    uint64_t fbAddr = fallbackInImage(e, mod, ver, imageSize);
    if (sigStatus == RS_Ok) {
        // On an exact known binary, disagreement with the verified address means the signature
        // matched the wrong function/global. A unique match alone does not prove identity.
        if (fbAddr && e.kind != SK_VtableSlot && fbAddr != sigAddr) {
            sig_logf("sigscan: DRIFT %s sig=0x%llx fb=0x%llx (refused)",
                     e.name, (unsigned long long)sigAddr, (unsigned long long)fbAddr);
            e.status = RS_SelfCheckFail;
            return;
        }
        // RIP displacements and anchor deltas must still land inside the FL image.
        if (e.kind == SK_DataRef && (sigAddr < (uint64_t)mod ||
            sigAddr - (uint64_t)mod >= imageSize)) {
            e.status = RS_SelfCheckFail;
            return;
        }
        e.addr = sigAddr; e.status = RS_Ok;
    } else if (havePat) {
        // Never turn an ambiguous, incomplete or invalid scan into success through a fallback.
        e.status = sigStatus;
    } else if (fbAddr) {
        e.addr = fbAddr; e.status = RS_Ok;
    } else {
        e.status = (ver == FLV_Unknown) ? RS_VersionLocked : RS_NotFound;
    }
}

void sig_resolveAll()
{
    if (g_symsResolved.load(std::memory_order_acquire)) return;
    std::lock_guard<std::mutex> lock(g_resolveMutex);
    if (g_symsResolved.load(std::memory_order_relaxed)) return;
    HMODULE mod = GetModuleHandleA("FLEngine_x64.dll");
    if (!mod) return; // no commit: retry once FL has loaded the engine

    const FlFileVersion version = readFlModuleVersion(mod);
    auto scanner = createFlSignatureScanner(version);
    const uint64_t imageSize = loadedImageSize(mod);
    ExecRange ranges[96];
    const int nr = getExecRanges(mod, ranges, _countof(ranges), imageSize);
    const FlScanContext context{mod, imageSize, ranges, nr};
    sig_logf("sigscan: FLEngine=%p file=%s scanner=%s execRanges=%d syms=%d",
             (void*)mod, version.text().c_str(), scanner->name(), nr, g_symCount);
    resolveSymbols(*scanner, context, g_syms.data(), g_symCount);

    int okN = 0, failN = 0;
    for (int i = 0; i < g_symCount; i++) {
        SymEntry& e = g_syms[i];

        if (e.status == RS_Ok) {
            okN++;
        } else {
            failN++;
            sig_logf("sigscan: UNRESOLVED %s (%s)", e.name, sig_statusStr(e.status));
        }
    }
    g_fileVersion = version;
    g_scanner = std::move(scanner);
    g_symsResolved.store(true, std::memory_order_release); // publish only the complete table
    sig_logf("sigscan: resolved ok=%d fail=%d scanner=%s", okN, failN, g_scanner->name());
}

// ================================ lookup + diagnostics ================================
const SymEntry* sig_findSym(const char* name)
{
    if (!name) return nullptr;
    // Compatibility for existing diagnostic clients; catalogue metadata carries the corrected name.
    if (strcmp(name, "FLwp_SetFormCaption") == 0) name = "FLapp_SetTitle";
    // Before publication return immutable unresolved definitions, never a partly filled live row.
    const SymEntry* entries = g_symsResolved.load(std::memory_order_acquire)
        ? g_syms.data() : g_symbolDefinitions;
    for (int i = 0; i < g_symCount; i++)
        if (entries[i].name && strcmp(entries[i].name, name) == 0) return &entries[i];
    return nullptr;
}

#ifdef FRUITYLINK_SCANNER_TESTS
// Standalone tests seed symbol outcomes without loading FL. Never compiled into the bridge DLL.
void sig_testSetResolved(bool complete) { g_symsResolved.store(complete, std::memory_order_release); }
#endif

uint64_t sig_addr(const char* name)
{
    sig_resolveAll();
    if (!g_symsResolved.load(std::memory_order_acquire)) return 0;
    const SymEntry* e = sig_findSym(name);
    return (e && e->status == RS_Ok) ? e->addr : 0;
}

// Reverse lookup for the LEGACY hex wire path: the C# side hardcodes 2025 Ghidra addresses. Given one,
// return its RESOLVED (version-correct) runtime address if that address is a known symbol's 2025 slot,
// else 0. This makes the entire un-migrated hardcoded-hex call surface version-correct without editing
// every call site — a table hit yields the right address on ANY resolved version.
static const SymEntry* findLegacySymbol(uint64_t ghidra25, uint64_t* offset)
{
    *offset = 0;
    // 1) EXACT symbol match — functions, data-global bases, and any read that offsets a RESOLVED runtime
    //    pointer (mixer/pattern arrays resolve the base here, then do their index math in runtime space).
    for (int i = 0; i < g_symCount; i++) {
        SymEntry& e = g_syms[i];
        if (e.ghidra[FLV_2025_25_2_5] == ghidra25) return &e;
    }
    // 2) INSIDE a known static ARRAY. The C# reads FL's per-pattern static arrays by sending a COMPUTED
    //    ghidra address (base + patIdx*0xC0) — e.g. GetNotes/ListPatterns read the note-recorder + name
    //    arrays this way — which never exactly matches a symbol base, so step 1 misses it and the address
    //    would (correctly) be refused as unmapped on 2026. These arrays have an IDENTICAL element stride
    //    across versions, so the offset carries over verbatim: map any address within an array's extent to
    //    resolved_base + (queried - base_2025). Pick the CLOSEST base <= queried (tightest delta; also
    //    correct when co-located arrays' extents overlap, since they share the same cross-version shift).
    static const struct { const char* name; uint64_t extent; } kArrays[] = {
        { "NoteRecorderArrayBase", 0xC0ULL * 1024 },   // per-pattern note-recorder ptr array (patterns 1..999)
        { "PatternNameArrayBase",  0xC0ULL * 1024 },   // per-pattern name-string ptr array (same struct block)
        { "RecentProjectsArray",   8ULL * 49 },        // recent-projects MRU (49 entries, stride 8)
    };
    const SymEntry* best = nullptr;
    uint64_t bestBase = 0;
    for (size_t k = 0; k < sizeof(kArrays) / sizeof(kArrays[0]); k++) {
        const SymEntry* e = sig_findSym(kArrays[k].name);
        if (!e) continue;
        uint64_t b = e->ghidra[FLV_2025_25_2_5];
        if (ghidra25 >= b && ghidra25 - b < kArrays[k].extent && b > bestBase) {
            bestBase = b;
            best = e;
            *offset = ghidra25 - b;
        }
    }
    return best;
}

uint64_t sig_addrByGhidra2025(uint64_t ghidra25)
{
    if (!g_symsResolved.load(std::memory_order_acquire)) return 0;
    uint64_t offset = 0;
    const SymEntry* e = findLegacySymbol(ghidra25, &offset);
    return e && e->status == RS_Ok && e->addr ? e->addr + offset : 0;
}

uint64_t sig_legacyAddr(HMODULE mod, FlVersion ver, uint64_t ghidra25)
{
    if (!mod || !g_symsResolved.load(std::memory_order_acquire)) return 0;
    uint64_t offset = 0;
    const SymEntry* e = findLegacySymbol(ghidra25, &offset);
    if (e) return e->status == RS_Ok && e->addr ? e->addr + offset : 0;
    if (ver != FLV_2025_25_2_5) return 0;
    SymEntry legacy{};
    legacy.ghidra[FLV_2025_25_2_5] = ghidra25;
    return fallbackAddr(legacy, mod, ver);
}

FlVersion sig_version()
{
    return g_symsResolved.load(std::memory_order_acquire) ? g_scanner->fallbackVersion() : FLV_Unknown;
}

const FlWindowLayout* sig_windowLayout()
{
    return g_symsResolved.load(std::memory_order_acquire) ? g_scanner->windowLayout() : nullptr;
}

const FlMenuLayout* sig_menuLayout()
{
    return g_symsResolved.load(std::memory_order_acquire) ? g_scanner->menuLayout() : nullptr;
}

bool sig_legacyBrowserSupported()
{
    return g_symsResolved.load(std::memory_order_acquire) && g_scanner->legacyBrowserSupported();
}

static std::string mixerLayoutJson(const FlMixerLayout* layout)
{
    if (!layout) return "null";
    const struct { const char* name; unsigned value; } fields[] = {
        {"trackStride", layout->trackStride}, {"nameOffset", layout->nameOffset},
        {"typeOffset", layout->typeOffset}, {"enabledOffset", layout->enabledOffset},
        {"soloOffset", layout->soloOffset}, {"sendTableOffset", layout->sendTableOffset},
        {"effectSlotsOffset", layout->effectSlotsOffset}, {"sendStride", layout->sendStride},
        {"sendLevelOffset", layout->sendLevelOffset}, {"sendActiveOffset", layout->sendActiveOffset},
        {"effectSlotStride", layout->effectSlotStride}, {"effectIndexOffset", layout->effectIndexOffset},
        {"effectNameOffset", layout->effectNameOffset}, {"effectLoadVtableOffset", layout->effectLoadVtableOffset}
    };
    std::string json = "{";
    for (const auto& field : fields) {
        if (json.size() > 1) json += ",";
        json += "\"" + std::string(field.name) + "\":" + std::to_string(field.value);
    }
    return json + "}";
}

static std::string timelineLayoutJson(const FlTimelineLayout* layout)
{
    if (!layout) return "null";
    return "{\"markerManagerOffset\":" + std::to_string(layout->markerManagerOffset)
        + ",\"markerStride\":" + std::to_string(layout->markerStride)
        + ",\"markerTickOffset\":" + std::to_string(layout->markerTickOffset)
        + ",\"markerNameOffset\":" + std::to_string(layout->markerNameOffset) + "}";
}

const FlMixerLayout* sig_mixerLayout()
{
    return g_symsResolved.load(std::memory_order_acquire) ? g_scanner->mixerLayout() : nullptr;
}

const FlAutomationLayout* sig_automationLayout()
{
    return g_symsResolved.load(std::memory_order_acquire) ? g_scanner->automationLayout() : nullptr;
}

static std::string scannerJson(FlFileVersion version, const IFlSignatureScanner& scanner)
{
    return "\"ver\":" + std::to_string((int)scanner.fallbackVersion())
         + ",\"fileVersion\":\"" + version.text() + "\""
         + ",\"scanner\":\"" + scanner.name() + "\""
         + ",\"supported\":" + (scanner.supported() ? "true" : "false")
         + ",\"complete\":true"
         + ",\"mixerTrackStride\":" + (scanner.mixerTrackStride()
             ? std::to_string(scanner.mixerTrackStride()) : "null")
         + ",\"windowEmbedding\":" + (scanner.windowLayout() ? "true" : "false")
         + ",\"legacyBrowserUi\":" + (scanner.legacyBrowserSupported() ? "true" : "false")
         + ",\"pluginMenu\":" + (scanner.menuLayout() ? "true" : "false")
         + ",\"mixerLayout\":" + mixerLayoutJson(scanner.mixerLayout())
         + ",\"timelineLayout\":" + timelineLayoutJson(scanner.timelineLayout())
         + ",\"automationClips\":" + (scanner.automationLayout() ? "true" : "false");
}

static std::string symsJson(const SymEntry* entries, int count, FlFileVersion version,
                            const IFlSignatureScanner& scanner)
{
    int ok = 0, fail = 0;
    for (int i = 0; i < count; i++) (entries[i].status == RS_Ok ? ok : fail)++;
    std::string s = "{" + scannerJson(version, scanner)
                  + ",\"ok\":" + std::to_string(ok)
                  + ",\"fail\":" + std::to_string(fail)
                  + ",\"unresolved\":[";
    bool first = true;
    for (int i = 0; i < count; i++) {
        if (entries[i].status == RS_Ok) continue;
        if (!first) s += ",";
        first = false;
        s += "{\"name\":\"";
        s += (entries[i].name ? entries[i].name : "");
        s += "\",\"why\":\"";
        s += sig_statusStr(entries[i].status);
        s += "\"}";
    }
    s += "]}";
    return s;
}

std::string sig_symsJson()
{
    sig_resolveAll();
    // No module means all entries are still unresolved. Do not inspect partially published entries.
    if (!g_symsResolved.load(std::memory_order_acquire))
        return "{\"ver\":0,\"ok\":0,\"fail\":0,\"complete\":false,\"unresolved\":[]}";
    return symsJson(g_syms.data(), g_symCount, g_fileVersion, *g_scanner);
}

std::string sig_inspectImage(HMODULE imageBase, uint64_t imageSize, FlFileVersion version)
{
    auto scanner = createFlSignatureScanner(version);
    ExecRange ranges[96];
    const int count = getExecRanges(imageBase, ranges, _countof(ranges), imageSize);
    // getExecRanges validates the headers before we read ImageBase in the private file image.
    uint64_t pointerBase = 0;
    if (count > 0) {
        const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(imageBase);
        const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>((const unsigned char*)imageBase + dos->e_lfanew);
        pointerBase = nt->OptionalHeader.ImageBase;
    }
    const FlScanContext context{imageBase, imageSize, ranges, count, pointerBase};
    std::vector<SymEntry> entries(g_symbolDefinitions, g_symbolDefinitions + g_symCount);
    resolveSymbols(*scanner, context, entries.data(), (int)entries.size());
    return symsJson(entries.data(), (int)entries.size(), version, *scanner);
}
