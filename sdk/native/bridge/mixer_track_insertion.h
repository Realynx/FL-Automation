#pragma once
#include "version_scanner.h"

class IMixerTrackInsertionBackend {
public:
    virtual ~IMixerTrackInsertionBackend() = default;
    virtual bool available() const = 0;
    virtual bool count(int& value) const = 0;
    virtual bool type(int index, int& value) const = 0;
    virtual bool insert(int index, unsigned flags) = 0;
};

// Run entirely on FL's UI thread: count validation, insertion, and result verification form one command.
std::string executeMixerTrackInsertion(const std::string& request, IMixerTrackInsertionBackend& backend);

class FlMixerTrackInsertionBackend final : public IMixerTrackInsertionBackend {
public:
    using SymbolLookup = void* (*)(const char*);
    using GuardedCall = ULONG_PTR (*)(void*, ULONG_PTR*, int, bool*);
    using LayoutLookup = const FlMixerLayout* (*)();
    FlMixerTrackInsertionBackend(SymbolLookup symbol, GuardedCall call, LayoutLookup layout)
        : symbol_(symbol), call_(call), layout_(layout) {}
    bool available() const override;
    bool count(int& value) const override;
    bool type(int index, int& value) const override;
    bool insert(int index, unsigned flags) override;
private:
    SymbolLookup symbol_;
    GuardedCall call_;
    LayoutLookup layout_;
};
