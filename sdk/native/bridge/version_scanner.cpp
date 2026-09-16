#include "version_scanner.h"
#include "delphi_classref.h"
#include <cstring>
#include <vector>

#pragma comment(lib, "version.lib")

std::string FlFileVersion::text() const
{
    return std::to_string(major) + "." + std::to_string(minor) + "." +
           std::to_string(patch) + "." + std::to_string(build);
}

FlVersion knownFlVersion(WORD major, WORD minor, WORD patch, WORD build)
{
    if (major == 25 && minor == 2 && patch == 5 && build == 5319) return FLV_2025_25_2_5;
    if (major == 26 && minor == 1 && patch == 0 && build == 5530) return FLV_2026_26_1_0;
    return FLV_Unknown;
}

FlFileVersion readFlFileVersion(const wchar_t* path)
{
    if (!path) return {};
    DWORD ignored = 0;
    const DWORD size = GetFileVersionInfoSizeW(path, &ignored);
    if (!size) return {};
    std::vector<unsigned char> buffer(size);
    if (!GetFileVersionInfoW(path, 0, size, buffer.data())) return {};
    VS_FIXEDFILEINFO* info = nullptr;
    UINT length = 0;
    if (!VerQueryValueW(buffer.data(), L"\\", reinterpret_cast<void**>(&info), &length) ||
        !info || length < sizeof(*info) || info->dwSignature != 0xFEEF04BD) return {};
    return {HIWORD(info->dwFileVersionMS), LOWORD(info->dwFileVersionMS),
            HIWORD(info->dwFileVersionLS), LOWORD(info->dwFileVersionLS)};
}

FlFileVersion readFlModuleVersion(HMODULE module)
{
    if (!module) return {};
    wchar_t path[32768];
    const DWORD length = GetModuleFileNameW(module, path, _countof(path));
    return length && length < _countof(path) ? readFlFileVersion(path) : FlFileVersion{};
}

FlVersion detectFlFileVersion(const wchar_t* path)
{
    const auto version = readFlFileVersion(path);
    return knownFlVersion(version.major, version.minor, version.patch, version.build);
}

FlVersion detectFlVersion(HMODULE module)
{
    const auto version = readFlModuleVersion(module);
    return knownFlVersion(version.major, version.minor, version.patch, version.build);
}

namespace {
// Delphi published-field tables independently agree on both installed builds.
const FlMenuLayout verifiedMenuLayout{0x760, 0x878};
const FlWindowLayout verifiedWindowLayout{0x798, 0x760, 0x768, 0x310, 0x6ac, 0x6c8};
// The scanning algorithm is shared. Each family owns its eligibility and verified layout policy.
class PatternSignatureScanner : public IFlSignatureScanner {
public:
    explicit PatternSignatureScanner(FlFileVersion version)
        : version_(version), fallback_(knownFlVersion(version.major, version.minor, version.patch, version.build)) {}
    bool supported() const override { return true; }
    FlVersion fallbackVersion() const override { return fallback_; }
    void resolve(SymEntry& entry, const FlScanContext& context) const override
    {
        // A malformed image must not recover a fallback-only symbol from an incomplete section scan.
        if (!context.image || !context.imageSize || !context.ranges || context.rangeCount <= 0) {
            entry.addr = 0;
            entry.status = RS_SelfCheckFail;
            return;
        }
        if (entry.name && std::strcmp(entry.name, "HostClassRef") == 0) {
            resolveHostClass(entry, context, "TScriptDialog");
            return;
        }
        if (entry.name && std::strcmp(entry.name, "NativeWindowClassRef") == 0) {
            resolveHostClass(entry, context, "TFLBaseVectorForm");
            return;
        }
        resolveSymbol(entry, context.image, fallback_, context.ranges, context.rangeCount, context.imageSize);
    }
protected:
    void resolveHostClass(SymEntry& entry, const FlScanContext& context, const char* name) const
    {
        entry.addr = 0;
        entry.status = resolveDelphiClassRef(context, name, &entry.addr);
        const uint64_t recorded = entry.ghidra[fallback_];
        if (entry.status == RS_Ok && recorded &&
            (recorded < 0x400000 || entry.addr - (uint64_t)context.image != recorded - 0x400000)) {
            entry.addr = 0;
            entry.status = RS_SelfCheckFail;
        }
    }
    const FlFileVersion version_;
    const FlVersion fallback_;
};

class Fl2025SignatureScanner final : public PatternSignatureScanner {
public:
    using PatternSignatureScanner::PatternSignatureScanner;
    const char* name() const override { return "fl-2025"; }
    const FlAutomationLayout* automationLayout() const override
    {
        static const FlAutomationLayout layout{0x190, 0x390};
        return fallback_ == FLV_2025_25_2_5 ? &layout : nullptr;
    }
    const FlTimelineLayout* timelineLayout() const override
    {
        static const FlTimelineLayout layout{0xd5c, 0x34, 0, 8};
        return fallback_ == FLV_2025_25_2_5 ? &layout : nullptr;
    }
    const FlMenuLayout* menuLayout() const override
    {
        return fallback_ == FLV_2025_25_2_5 ? &verifiedMenuLayout : nullptr;
    }
    bool legacyBrowserSupported() const override { return fallback_ == FLV_2025_25_2_5; }
    const FlWindowLayout* windowLayout() const override
    {
        return fallback_ == FLV_2025_25_2_5 ? &verifiedWindowLayout : nullptr;
    }
    unsigned mixerTrackStride() const override
    {
        return fallback_ == FLV_2025_25_2_5 ? 0x1474 : 0;
    }
    const FlMixerLayout* mixerLayout() const override
    {
        // Ghidra evidence: docs/fl-version-analysis-2026-09-12.md.
        static const FlMixerLayout layout{
            0x1474, 0x0c, 0x08, 0x18, 0x1a, 0x2e4, 0x1324, 8, 0, 4, 8, 0x64, 0x58, 0xf0
        };
        return fallback_ == FLV_2025_25_2_5 ? &layout : nullptr;
    }
};

class Fl2026SignatureScanner final : public PatternSignatureScanner {
public:
    using PatternSignatureScanner::PatternSignatureScanner;
    const char* name() const override { return "fl-2026"; }
    const FlAutomationLayout* automationLayout() const override
    {
        static const FlAutomationLayout layout{0x160, 0x360};
        return isInspectedBuild() ? &layout : nullptr;
    }
    const FlTimelineLayout* timelineLayout() const override
    {
        static const FlTimelineLayout layout{0xd84, 0x34, 0, 8};
        return isInspectedBuild() ? &layout : nullptr;
    }
    const FlMenuLayout* menuLayout() const override
    {
        return isInspectedBuild() ? &verifiedMenuLayout : nullptr;
    }
    const FlWindowLayout* windowLayout() const override
    {
        return isInspectedBuild() ? &verifiedWindowLayout : nullptr;
    }
    unsigned mixerTrackStride() const override
    {
        // 26.1.3.5570 independently checked: IMUL 0x28f followed by scale 8 = 0x1478.
        // This does not approve its old fallback addresses or window layout.
        return fallback_ == FLV_2026_26_1_0 || isInspectedBuild() ? 0x1478 : 0;
    }
    const FlMixerLayout* mixerLayout() const override
    {
        static const FlMixerLayout layout{
            0x1478, 0x0c, 0x08, 0x18, 0x1a, 0x394, 0x134c, 8, 0, 4, 8, 0x64, 0x58, 0xf0
        };
        // Only this installed patch has the full layout independently checked. The older recorded
        // 26.1.0 address set is not evidence for its send-table or effect-slot offsets.
        return isInspectedBuild() ? &layout : nullptr;
    }
private:
    bool isInspectedBuild() const
    {
        return version_.minor == 1 && version_.patch == 3 && version_.build == 5570;
    }
};

class UnsupportedSignatureScanner final : public IFlSignatureScanner {
public:
    const char* name() const override { return "unsupported"; }
    bool supported() const override { return false; }
    FlVersion fallbackVersion() const override { return FLV_Unknown; }
    const FlWindowLayout* windowLayout() const override { return nullptr; }
    const FlMixerLayout* mixerLayout() const override { return nullptr; }
    unsigned mixerTrackStride() const override { return 0; }
    void resolve(SymEntry& entry, const FlScanContext&) const override
    {
        entry.addr = 0;
        entry.status = RS_VersionLocked;
    }
};
}

std::unique_ptr<IFlSignatureScanner> createFlSignatureScanner(FlFileVersion version)
{
    switch (version.major) {
        case 25: return std::make_unique<Fl2025SignatureScanner>(version);
        case 26: return std::make_unique<Fl2026SignatureScanner>(version);
        default: return std::make_unique<UnsupportedSignatureScanner>();
    }
}

void resolveSymbols(const IFlSignatureScanner& scanner, const FlScanContext& context,
                    SymEntry* entries, int count)
{
    for (int i = 0; i < count; ++i) scanner.resolve(entries[i], context);
}
