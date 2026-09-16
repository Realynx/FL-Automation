#include "delphi_classref.h"
#include "version_scanner.h"
#include <cstring>

namespace {
bool inImage(const FlScanContext& context, uint64_t address, uint64_t length)
{
    const auto base = reinterpret_cast<uint64_t>(context.image);
    return address >= base && address - base <= context.imageSize &&
           length <= context.imageSize - (address - base);
}

bool readWord(const FlScanContext& context, uint64_t address, uint64_t& value)
{
    if (!inImage(context, address, sizeof(value))) return false;
    memcpy(&value, reinterpret_cast<const void*>(address), sizeof(value));
    return true;
}

bool readPointer(const FlScanContext& context, uint64_t address, uint64_t pointerBase, uint64_t& mapped)
{
    uint64_t stored = 0;
    if (!readWord(context, address, stored) || stored < pointerBase || stored - pointerBase >= context.imageSize)
        return false;
    mapped = reinterpret_cast<uint64_t>(context.image) + stored - pointerBase;
    return true;
}

bool executable(const FlScanContext& context, uint64_t address)
{
    for (int index = 0; index < context.rangeCount; ++index) {
        const auto& range = context.ranges[index];
        if (address >= reinterpret_cast<uint64_t>(range.begin) &&
            address < reinterpret_cast<uint64_t>(range.end)) return true;
    }
    return false;
}

bool rangesWithinImage(const FlScanContext& context)
{
    for (int index = 0; index < context.rangeCount; ++index) {
        const auto begin = reinterpret_cast<uint64_t>(context.ranges[index].begin);
        const auto end = reinterpret_cast<uint64_t>(context.ranges[index].end);
        if (begin > end || !inImage(context, begin, end - begin)) return false;
    }
    return true;
}

bool validClassIdentity(const FlScanContext& context, uint64_t candidate, uint64_t pointerBase,
                        const unsigned char* encodedName, size_t nameLength)
{
    if (!inImage(context, candidate, 0x1C0) || candidate - reinterpret_cast<uint64_t>(context.image) < 0xC8)
        return false;
    uint64_t self = 0, size = 0, typeInfo = 0;
    if (!readPointer(context, candidate - 0xC8, pointerBase, self) || self != candidate ||
        !readWord(context, candidate - 0x80, size) || size < 0x100 || size > 0x10000 ||
        !readPointer(context, candidate - 0xA8, pointerBase, typeInfo) ||
        !inImage(context, typeInfo, nameLength + 2)) return false;
    const auto* typeBytes = reinterpret_cast<const unsigned char*>(typeInfo);
    return typeBytes[0] == 7 && memcmp(typeBytes + 1, encodedName, nameLength + 1) == 0;
}

bool validParent(const FlScanContext& context, uint64_t candidate, uint64_t pointerBase)
{
    uint64_t parentSlot = 0, parent = 0, parentSelf = 0;
    return readPointer(context, candidate - 0x78, pointerBase, parentSlot) &&
           readPointer(context, parentSlot, pointerBase, parent) &&
           parent - reinterpret_cast<uint64_t>(context.image) >= 0xC8 &&
           readPointer(context, parent - 0xC8, pointerBase, parentSelf) && parentSelf == parent;
}

bool validMethods(const FlScanContext& context, uint64_t candidate, uint64_t pointerBase)
{
    const int methodOffsets[] = { -0x20, 0, 0x138, 0x188, 0x1B8 };
    for (const int offset : methodOffsets) {
        uint64_t method = 0;
        if (!readPointer(context, candidate + offset, pointerBase, method) || !executable(context, method))
            return false;
    }
    return true;
}

bool validMetaclass(const FlScanContext& context, uint64_t candidate, uint64_t pointerBase,
                    const unsigned char* encodedName, size_t nameLength)
{
    return validClassIdentity(context, candidate, pointerBase, encodedName, nameLength) &&
           validParent(context, candidate, pointerBase) && validMethods(context, candidate, pointerBase);
}

ResolveStatus findClass(const FlScanContext& context, const unsigned char* encodedName, size_t nameLength,
                        uint64_t pointerBase, uint64_t* outAddress)
{
    uint64_t found = 0;
    for (int index = 0; index < context.rangeCount; ++index) {
        const auto& names = context.ranges[index];
        const auto* cursor = names.begin;
        while (cursor < names.end && static_cast<size_t>(names.end - cursor) >= nameLength + 1) {
            cursor = static_cast<const unsigned char*>(memchr(cursor, encodedName[0], names.end - cursor));
            if (!cursor || static_cast<size_t>(names.end - cursor) < nameLength + 1) break;
            if (memcmp(cursor, encodedName, nameLength + 1) == 0) {
                const uint64_t encodedPointer = pointerBase + (cursor - reinterpret_cast<const unsigned char*>(context.image));
                for (int pointers = 0; pointers < context.rangeCount; ++pointers) {
                    const auto& range = context.ranges[pointers];
                    const auto end = reinterpret_cast<uint64_t>(range.end);
                    for (uint64_t address = (reinterpret_cast<uint64_t>(range.begin) + 7) & ~7ULL;
                         address <= end && end - address >= 8; address += 8) {
                        uint64_t value = 0;
                        if (!readWord(context, address, value) || value != encodedPointer) continue;
                        if (end - address < 0x88 || !validMetaclass(context, address + 0x88, pointerBase, encodedName, nameLength))
                            continue;
                        const auto candidate = address + 0x88;
                        if (found && found != candidate) return RS_Ambiguous;
                        found = candidate;
                    }
                }
            }
            ++cursor;
        }
    }
    *outAddress = found;
    return found ? RS_Ok : RS_NotFound;
}
}

ResolveStatus resolveDelphiClassRef(const FlScanContext& context, const char* className,
                                   uint64_t* outAddress, uint64_t pointerBase)
{
    if (!outAddress) return RS_SelfCheckFail;
    *outAddress = 0;
    if (!context.image || !context.imageSize) return RS_NoModule;
    const auto base = reinterpret_cast<uint64_t>(context.image);
    if (!className || !context.ranges || context.rangeCount <= 0 || context.rangeCount > 96 ||
        context.imageSize > UINT64_MAX - base) return RS_SelfCheckFail;
    if (!pointerBase) pointerBase = context.pointerBase ? context.pointerBase : base;
    if (context.imageSize > UINT64_MAX - pointerBase) return RS_SelfCheckFail;
    __try {
        const size_t length = strnlen_s(className, 256);
        if (!length || length > 255) return RS_SelfCheckFail;
        unsigned char encoded[256]{};
        encoded[0] = static_cast<unsigned char>(length);
        memcpy(encoded + 1, className, length);
        if (!rangesWithinImage(context)) return RS_SelfCheckFail;
        return findClass(context, encoded, length, pointerBase, outAddress);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *outAddress = 0;
        return RS_SelfCheckFail;
    }
}
