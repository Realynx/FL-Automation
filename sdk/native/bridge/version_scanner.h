// Version selection belongs at bridge composition, never in plugin consumers.
#pragma once
#include "sigscan.h"
#include <memory>

struct FlFileVersion {
    WORD major = 0, minor = 0, patch = 0, build = 0;
    std::string text() const;
};

// Only layouts verified on the exact engine build may be supplied by a scanner.
struct FlWindowLayout {
    unsigned instanceSize, captionOffset, maximizeOffset, clientRectVtableOffset;
    unsigned constraintsOffset, effectiveMinOffset;
};

struct FlMenuLayout {
    int mainMenuOffset, toolbarMenuOffset;
};

struct FlTimelineLayout {
    unsigned markerManagerOffset, markerStride, markerTickOffset, markerNameOffset;
};

struct FlAutomationLayout {
    unsigned channelTypeOffset, channelContainerOffset;
};

struct FlMixerLayout {
    unsigned trackStride, nameOffset, typeOffset, enabledOffset, soloOffset;
    unsigned sendTableOffset, effectSlotsOffset;
    unsigned sendStride, sendLevelOffset, sendActiveOffset, effectSlotStride;
    unsigned effectIndexOffset, effectNameOffset, effectLoadVtableOffset;
};

struct FlScanContext {
    HMODULE image;
    uint64_t imageSize;
    const ExecRange* ranges;
    int rangeCount;
    // Live images contain relocated pointers; static inspection uses the preferred PE ImageBase.
    uint64_t pointerBase = 0;
};

class IFlSignatureScanner {
public:
    virtual ~IFlSignatureScanner() = default;
    virtual const char* name() const = 0;
    virtual bool supported() const = 0;
    virtual FlVersion fallbackVersion() const = 0;
    virtual const FlWindowLayout* windowLayout() const = 0;
    virtual const FlMixerLayout* mixerLayout() const = 0;
    virtual const FlMenuLayout* menuLayout() const { return nullptr; }
    virtual const FlTimelineLayout* timelineLayout() const { return nullptr; }
    virtual const FlAutomationLayout* automationLayout() const { return nullptr; }
    virtual bool legacyBrowserSupported() const { return false; }
    virtual unsigned mixerTrackStride() const = 0; // 0 means unavailable, never a default stride
    virtual void resolve(SymEntry& entry, const FlScanContext& context) const = 0;
};

FlFileVersion readFlFileVersion(const wchar_t* path);
FlFileVersion readFlModuleVersion(HMODULE module);
std::unique_ptr<IFlSignatureScanner> createFlSignatureScanner(FlFileVersion version);
// Constructor-style injection for production, static inspection, and test doubles alike.
void resolveSymbols(const IFlSignatureScanner& scanner, const FlScanContext& context,
                    SymEntry* entries, int count);

const FlWindowLayout* sig_windowLayout();
const FlMenuLayout* sig_menuLayout();
const FlMixerLayout* sig_mixerLayout();
const FlAutomationLayout* sig_automationLayout();
bool sig_legacyBrowserSupported();
std::string sig_inspectImage(HMODULE imageBase, uint64_t imageSize, FlFileVersion version);
