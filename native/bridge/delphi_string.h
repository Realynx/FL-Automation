#pragma once
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

// Delphi UnicodeString header. A constant refcount (-1) makes callees copy on assignment;
// the byte buffer stays alive across every call, including an exception unwind.
class DelphiString {
public:
    explicit DelphiString(const std::wstring& text) : storage_(12 + (text.size() + 1) * sizeof(wchar_t)) {
        const uint16_t encoding[] = {1200, 2};
        const int32_t metadata[] = {-1, static_cast<int32_t>(text.size())};
        std::memcpy(storage_.data(), encoding, 4);
        std::memcpy(storage_.data() + 4, metadata, 8);
        std::memcpy(storage_.data() + 12, text.c_str(), (text.size() + 1) * sizeof(wchar_t));
    }
    void* data() { return storage_.data() + 12; }
private:
    std::vector<unsigned char> storage_;
};
