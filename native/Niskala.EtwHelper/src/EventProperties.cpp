#include "EventProperties.h"

#include <tdh.h>

#include <cstring>

namespace niskala
{
    bool EventProperties::Read(const wchar_t* name, std::vector<uint8_t>& buffer) const
    {
        PROPERTY_DATA_DESCRIPTOR descriptor{};
        descriptor.PropertyName = reinterpret_cast<ULONGLONG>(name);
        descriptor.ArrayIndex = ULONG_MAX;

        auto* record = const_cast<EVENT_RECORD*>(&record_);
        ULONG size = 0;
        if (TdhGetPropertySize(record, 0, nullptr, 1, &descriptor, &size) != ERROR_SUCCESS)
        {
            return false;
        }

        buffer.resize(size);
        return size == 0 || TdhGetProperty(record, 0, nullptr, 1, &descriptor, size, buffer.data()) == ERROR_SUCCESS;
    }

    bool EventProperties::Has(const wchar_t* name) const
    {
        std::vector<uint8_t> buffer;
        return Read(name, buffer);
    }

    std::optional<uint64_t> EventProperties::Integer(const wchar_t* name) const
    {
        std::vector<uint8_t> buffer;
        if (!Read(name, buffer))
        {
            return std::nullopt;
        }

        switch (buffer.size())
        {
        case 1:
            return buffer[0];
        case 2:
        {
            uint16_t value;
            std::memcpy(&value, buffer.data(), sizeof value);
            return value;
        }
        case 4:
        {
            uint32_t value;
            std::memcpy(&value, buffer.data(), sizeof value);
            return value;
        }
        case 8:
        {
            uint64_t value;
            std::memcpy(&value, buffer.data(), sizeof value);
            return value;
        }
        default:
            return std::nullopt;
        }
    }

    uint64_t EventProperties::IntegerOr(const wchar_t* name, uint64_t fallback) const
    {
        return Integer(name).value_or(fallback);
    }

    std::wstring EventProperties::Text(const wchar_t* name) const
    {
        std::vector<uint8_t> buffer;
        if (!Read(name, buffer) || buffer.size() < sizeof(wchar_t))
        {
            return {};
        }

        std::wstring text(buffer.size() / sizeof(wchar_t), L'\0');
        std::memcpy(text.data(), buffer.data(), text.size() * sizeof(wchar_t));
        text.resize(wcsnlen(text.c_str(), text.size()));
        return text;
    }

    std::vector<uint8_t> EventProperties::Bytes(const wchar_t* name) const
    {
        std::vector<uint8_t> buffer;
        return Read(name, buffer) ? buffer : std::vector<uint8_t>{};
    }
}
