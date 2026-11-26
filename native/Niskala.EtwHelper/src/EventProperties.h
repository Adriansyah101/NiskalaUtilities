#pragma once

#include <windows.h>
#include <evntcons.h>

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace niskala
{
    class EventProperties
    {
    public:
        explicit EventProperties(const EVENT_RECORD& record) noexcept : record_(record) {}

        [[nodiscard]] bool Has(const wchar_t* name) const;
        [[nodiscard]] std::optional<uint64_t> Integer(const wchar_t* name) const;
        [[nodiscard]] uint64_t IntegerOr(const wchar_t* name, uint64_t fallback) const;
        [[nodiscard]] std::wstring Text(const wchar_t* name) const;
        [[nodiscard]] std::vector<uint8_t> Bytes(const wchar_t* name) const;

    private:
        bool Read(const wchar_t* name, std::vector<uint8_t>& buffer) const;

        const EVENT_RECORD& record_;
    };
}
