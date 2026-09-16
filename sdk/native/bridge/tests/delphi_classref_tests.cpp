#include "delphi_classref.h"
#include "version_scanner.h"
#include <array>
#include <cstdio>
#include <cstring>
#include <stdexcept>

namespace {
int checks = 0;
void expect(bool condition, const char* message)
{
    ++checks;
    if (!condition) throw std::runtime_error(message);
}

struct Fixture {
    alignas(8) std::array<unsigned char, 0x4000> image{};
    ExecRange range{image.data(), image.data() + 0x3000};
    FlScanContext context{reinterpret_cast<HMODULE>(image.data()), image.size(), &range, 1};
    uint64_t pointerBase;

    explicit Fixture(bool unrelocated = false)
        : pointerBase(unrelocated ? 0x400000 : reinterpret_cast<uint64_t>(image.data()))
    {
        const unsigned char name[] = { 13, 'T', 'S', 'c', 'r', 'i', 'p', 't', 'D', 'i', 'a', 'l', 'o', 'g' };
        memcpy(image.data() + 0x2000, name, sizeof(name));
        image[0x2100] = 7;
        memcpy(image.data() + 0x2101, name, sizeof(name));
        pointer(0x600 - 0xC8, 0x600);
        pointer(0x500, 0x600);
        makeClass(0xA00);
    }

    void word(size_t offset, uint64_t value) { memcpy(image.data() + offset, &value, sizeof(value)); }
    void pointer(size_t offset, uint64_t target) { word(offset, pointerBase + target); }
    void makeClass(size_t offset)
    {
        pointer(offset - 0xC8, offset);
        pointer(offset - 0xA8, 0x2100);
        pointer(offset - 0x88, 0x2000);
        word(offset - 0x80, 0x7F0);
        pointer(offset - 0x78, 0x500);
        const int methods[] = {-0x20, 0, 0x138, 0x188, 0x1B8};
        for (int method : methods) pointer(offset + method, 0x2800);
    }
    ResolveStatus resolve(uint64_t& result, uint64_t encodedBase = 0)
    {
        return resolveDelphiClassRef(context, "TScriptDialog", &result, encodedBase);
    }
};

void testSuccess()
{
    Fixture live;
    uint64_t address = 0;
    expect(live.resolve(address) == RS_Ok && address == reinterpret_cast<uint64_t>(live.image.data()) + 0xA00,
           "Live relocated classref resolves to mapped address");
    Fixture disk(true);
    expect(disk.resolve(address, 0x400000) == RS_Ok && address == reinterpret_cast<uint64_t>(disk.image.data()) + 0xA00,
           "Unrelocated PE pointer base resolves to private buffer address");
    expect(disk.resolve(address) == RS_NotFound && address == 0, "Wrong pointer encoding is refused");
    live.makeClass(0x1400);
    expect(live.resolve(address) == RS_Ambiguous && address == 0, "Duplicate valid classes are refused");
}

void testMalformedVmt()
{
    uint64_t address = 0;
    Fixture self;
    self.word(0xA00 - 0xC8, 0);
    expect(self.resolve(address) == RS_NotFound, "Missing VMT self pointer is refused");
    Fixture parent;
    parent.word(0x600 - 0xC8, 0);
    expect(parent.resolve(address) == RS_NotFound, "Invalid parent metaclass is refused");
    Fixture size;
    size.word(0xA00 - 0x80, 0x10001);
    expect(size.resolve(address) == RS_NotFound, "Implausible instance size is refused");
    Fixture method;
    method.pointer(0xA00 + 0x1B8, 0x3500);
    expect(method.resolve(address) == RS_NotFound, "Non-executable VMT method is refused");
    Fixture rtti;
    rtti.image[0x2100] = 6;
    expect(rtti.resolve(address) == RS_NotFound, "Non-class RTTI is refused");
    rtti.image[0x2100] = 7;
    rtti.image[0x2102] = 'X';
    expect(rtti.resolve(address) == RS_NotFound, "Disagreeing RTTI class name is refused");
    Fixture name;
    name.image[0x2001] = 'X';
    expect(name.resolve(address) == RS_NotFound, "Wrong short-string class name is refused");
}

void testBounds()
{
    uint64_t address = 123;
    Fixture fixture;
    fixture.range.end = fixture.image.data() + fixture.image.size() + 1;
    expect(fixture.resolve(address) == RS_SelfCheckFail && !address, "Out-of-image range is refused");
    fixture.range.end = fixture.image.data();
    fixture.range.begin = fixture.image.data() + 1;
    expect(fixture.resolve(address) == RS_SelfCheckFail, "Reversed range is refused");
    Fixture missing;
    missing.context.image = nullptr;
    expect(missing.resolve(address) == RS_NoModule, "Missing image is refused");
    Fixture invalid;
    expect(resolveDelphiClassRef(invalid.context, "", &address) == RS_SelfCheckFail, "Empty class name is refused");
    expect(resolveDelphiClassRef(invalid.context, "TScriptDialog", nullptr) == RS_SelfCheckFail,
           "Missing output is refused");
    expect(invalid.resolve(address, UINT64_MAX - 1) == RS_SelfCheckFail, "Pointer-base overflow is refused");
}
}

int runDelphiClassRefTests()
{
    testSuccess();
    testMalformedVmt();
    testBounds();
    std::printf("Delphi classref: %d checks passed\n", checks);
    return checks;
}
