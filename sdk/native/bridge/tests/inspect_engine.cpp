// Read-only binary diagnostic. File sections are copied into private, non-executable memory.
// No LoadLibrary, DLL entry point, FL process, imported function, or production file is touched.
#include "version_scanner.h"
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <stdexcept>
#include <vector>

namespace {
template<class T> T read(const std::vector<unsigned char>& bytes, size_t offset)
{
    if (offset > bytes.size() || sizeof(T) > bytes.size() - offset)
        throw std::runtime_error("Truncated PE structure");
    T value;
    memcpy(&value, bytes.data() + offset, sizeof(value));
    return value;
}

std::vector<unsigned char> imageFromFile(const wchar_t* path)
{
    std::ifstream file(std::filesystem::path(path), std::ios::binary | std::ios::ate);
    if (!file) throw std::runtime_error("Could not read engine file");
    auto length = file.tellg();
    if (length <= 0 || length > 512 * 1024 * 1024) throw std::runtime_error("Invalid engine file length");
    std::vector<unsigned char> bytes(static_cast<size_t>(length));
    file.seekg(0);
    if (!file.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(bytes.size())))
        throw std::runtime_error("Incomplete engine file read");
    auto dos = read<IMAGE_DOS_HEADER>(bytes, 0);
    if (dos.e_magic != IMAGE_DOS_SIGNATURE || dos.e_lfanew <= 0) throw std::runtime_error("Invalid DOS header");
    auto nt = read<IMAGE_NT_HEADERS64>(bytes, static_cast<size_t>(dos.e_lfanew));
    if (nt.Signature != IMAGE_NT_SIGNATURE || nt.OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC)
        throw std::runtime_error("Engine is not a PE64 image");
    const size_t size = nt.OptionalHeader.SizeOfImage;
    const size_t headers = nt.OptionalHeader.SizeOfHeaders;
    if (!size || size > 512 * 1024 * 1024 || headers > size || headers > bytes.size())
        throw std::runtime_error("Invalid image/header size");
    std::vector<unsigned char> image(size);
    memcpy(image.data(), bytes.data(), headers);
    const size_t sectionStart = static_cast<size_t>(dos.e_lfanew) + sizeof(DWORD) +
                               sizeof(IMAGE_FILE_HEADER) + nt.FileHeader.SizeOfOptionalHeader;
    for (unsigned i = 0; i < nt.FileHeader.NumberOfSections; ++i) {
        auto section = read<IMAGE_SECTION_HEADER>(bytes, sectionStart + i * sizeof(IMAGE_SECTION_HEADER));
        if (!section.SizeOfRawData) continue;
        if (section.PointerToRawData > bytes.size() ||
            section.SizeOfRawData > bytes.size() - section.PointerToRawData ||
            section.VirtualAddress > image.size() ||
            section.SizeOfRawData > image.size() - section.VirtualAddress)
            throw std::runtime_error("Section lies outside file or mapped image");
        memcpy(image.data() + section.VirtualAddress, bytes.data() + section.PointerToRawData, section.SizeOfRawData);
    }
    return image;
}
}

int inspectEngineFile(const wchar_t* path)
{
    try {
        auto image = imageFromFile(path);
        auto report = sig_inspectImage(reinterpret_cast<HMODULE>(image.data()), image.size(), readFlFileVersion(path));
        std::printf("%s\n", report.c_str());
        return 0;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "Engine inspection failed: %s\n", error.what());
        return 1;
    }
}
