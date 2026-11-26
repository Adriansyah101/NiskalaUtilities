#include "Tracker.h"

#include "EventProperties.h"
#include "Providers.h"

#include <tlhelp32.h>

#include <algorithm>
#include <cwchar>
#include <limits>
#include <string_view>

namespace niskala
{
    namespace
    {
        constexpr USHORT ProcessStartId = 1;
        constexpr USHORT ProcessStopId = 2;
        constexpr USHORT FileCreateId = 12;
        constexpr USHORT FileWriteId = 16;
        constexpr USHORT FileDeletePathId = 26;
        constexpr USHORT FileRenamePathId = 27;
        constexpr USHORT FileCreateNewFileId = 30;
        constexpr USHORT KeyCreateId = 1;
        constexpr USHORT KeyOpenId = 2;
        constexpr USHORT KeyDeleteId = 3;
        constexpr USHORT ValueSetId = 5;
        constexpr USHORT ValueDeleteId = 6;

        constexpr uint64_t FileDirectoryFile = 0x1;
        constexpr uint64_t FileOverwrite = 4;
        constexpr uint64_t FileOverwriteIf = 5;
        constexpr int CreateDispositionShift = 24;
        constexpr uint64_t CreatedNewKey = 1;

        constexpr uint32_t SystemProcess = 4;
        constexpr uint32_t NoProcess = 0xFFFFFFFF;
        constexpr int64_t Always = (std::numeric_limits<int64_t>::min)();
        constexpr int64_t Forever = (std::numeric_limits<int64_t>::max)();
        constexpr int64_t PendingWindow = 15LL * 10'000'000;
        constexpr size_t PendingLimit = 50'000;
        constexpr size_t NameLimit = 500'000;
        constexpr std::wstring_view RegistryPrefix = L"\\REGISTRY\\";

        class ExclusiveLock
        {
        public:
            explicit ExclusiveLock(SRWLOCK& lock) noexcept : lock_(lock) { AcquireSRWLockExclusive(&lock_); }
            ~ExclusiveLock() { ReleaseSRWLockExclusive(&lock_); }
            ExclusiveLock(const ExclusiveLock&) = delete;
            ExclusiveLock& operator=(const ExclusiveLock&) = delete;

        private:
            SRWLOCK& lock_;
        };

        class SharedLock
        {
        public:
            explicit SharedLock(SRWLOCK& lock) noexcept : lock_(lock) { AcquireSRWLockShared(&lock_); }
            ~SharedLock() { ReleaseSRWLockShared(&lock_); }
            SharedLock(const SharedLock&) = delete;
            SharedLock& operator=(const SharedLock&) = delete;

        private:
            SRWLOCK& lock_;
        };

        bool IsRegistryPath(const std::wstring& path) noexcept
        {
            return path.size() > RegistryPrefix.size() && _wcsnicmp(path.c_str(), RegistryPrefix.data(), RegistryPrefix.size()) == 0;
        }

        std::wstring Combine(const std::wstring& base, const std::wstring& relative)
        {
            if (!relative.empty() && relative.front() == L'\\')
            {
                return relative;
            }

            if (base.empty() || relative.empty())
            {
                return base.empty() ? relative : base;
            }

            return base + L'\\' + relative;
        }

        NiskalaEtwEvent MakeEvent(uint32_t kind, const EVENT_RECORD& record, const std::wstring& path, const wchar_t* name = L"", uint32_t flags = 0) noexcept
        {
            NiskalaEtwEvent event{};
            event.Kind = kind;
            event.ProcessId = record.EventHeader.ProcessId;
            event.Flags = flags;
            event.Timestamp = record.EventHeader.TimeStamp.QuadPart;
            event.Path = path.c_str();
            event.Name = name;
            return event;
        }
    }

    void Tracker::Seed()
    {
        HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE)
        {
            return;
        }

        std::vector<uint32_t> running;
        PROCESSENTRY32W entry{};
        entry.dwSize = sizeof entry;
        for (BOOL more = Process32FirstW(snapshot, &entry); more; more = Process32NextW(snapshot, &entry))
        {
            running.push_back(entry.th32ProcessID);
        }

        CloseHandle(snapshot);

        ExclusiveLock guard(lock_);
        for (uint32_t processId : running)
        {
            if (!tracked_.contains(processId))
            {
                others_.try_emplace(processId, Lifetime{Always, Forever});
            }
        }

        std::erase_if(pending_, [this](const PendingEvent& event) { return Classify(event.Header.ProcessId, event.Header.TimeStamp.QuadPart) == Owner::Other; });
    }

    void Tracker::Track(uint32_t processId)
    {
        ExclusiveLock guard(lock_);
        tracked_[processId] = Lifetime{Always, Forever};
        others_.erase(processId);
        requested_.insert(processId);
    }

    void Tracker::OnEvent(const EVENT_RECORD& record)
    {
        ++seen_;
        ReplayRequested();

        const GUID& provider = record.EventHeader.ProviderId;
        if (IsEqualGUID(provider, ProcessProvider))
        {
            switch (record.EventHeader.EventDescriptor.Id)
            {
            case ProcessStartId:
                OnProcessStart(record);
                break;
            case ProcessStopId:
                OnProcessStop(record);
                break;
            default:
                break;
            }

            return;
        }

        uint32_t processId = record.EventHeader.ProcessId;
        if (processId == 0 || processId == SystemProcess || processId == NoProcess)
        {
            return;
        }

        Owner owner;
        {
            ExclusiveLock guard(lock_);
            owner = Classify(processId, record.EventHeader.TimeStamp.QuadPart);
            if (owner == Owner::Unknown)
            {
                Hold(record);
            }
        }

        if (owner == Owner::Tracked)
        {
            Dispatch(record);
        }
    }

    void Tracker::Finish(NiskalaEtwStats& stats)
    {
        ReplayRequested();
        ExclusiveLock guard(lock_);
        stats.EventsSeen = seen_;
        stats.EventsForwarded = forwarded_;
        stats.PendingDropped = pendingDropped_ + static_cast<uint32_t>(pending_.size());
        stats.UnnamedEvents = unnamed_;
        pending_.clear();
    }

    Tracker::Owner Tracker::Classify(uint32_t processId, int64_t time) const
    {
        if (auto it = tracked_.find(processId); it != tracked_.end() && it->second.Covers(time))
        {
            return Owner::Tracked;
        }

        if (auto it = others_.find(processId); it != others_.end() && it->second.Covers(time))
        {
            return Owner::Other;
        }

        return Owner::Unknown;
    }

    void Tracker::Hold(const EVENT_RECORD& record)
    {
        int64_t newest = record.EventHeader.TimeStamp.QuadPart;
        while (!pending_.empty() && (pending_.size() >= PendingLimit || pending_.front().Header.TimeStamp.QuadPart < newest - PendingWindow))
        {
            pending_.pop_front();
            ++pendingDropped_;
        }

        const auto* data = static_cast<const uint8_t*>(record.UserData);
        pending_.push_back(PendingEvent{record.EventHeader, record.BufferContext, std::vector<uint8_t>(data, data + record.UserDataLength)});
    }

    void Tracker::OnProcessStart(const EVENT_RECORD& record)
    {
        EventProperties properties(record);
        auto processId = static_cast<uint32_t>(properties.IntegerOr(L"ProcessID", 0));
        auto parentId = static_cast<uint32_t>(properties.IntegerOr(L"ParentProcessID", 0));
        if (processId == 0)
        {
            return;
        }

        int64_t time = record.EventHeader.TimeStamp.QuadPart;
        bool parentTracked;
        bool alreadyTracked;
        {
            SharedLock guard(lock_);
            parentTracked = Classify(parentId, time) == Owner::Tracked;
            auto it = tracked_.find(processId);
            alreadyTracked = it != tracked_.end() && it->second.Stop == Forever;
        }

        std::wstring image = properties.Text(L"ImageName");
        NiskalaEtwEvent event = MakeEvent(NISKALA_ETW_PROCESS_START, record, image, L"", parentTracked ? NISKALA_ETW_FLAG_PARENT_TRACKED : 0);
        event.ProcessId = processId;
        event.ParentProcessId = parentId;
        bool track = callback_(context_, &event) != 0 || parentTracked || alreadyTracked;
        if (track)
        {
            ++forwarded_;
        }

        {
            ExclusiveLock guard(lock_);
            if (track)
            {
                auto [it, added] = tracked_.try_emplace(processId, Lifetime{time, Forever});
                if (!added && it->second.Stop != Forever)
                {
                    it->second = Lifetime{time, Forever};
                }

                others_.erase(processId);
            }
            else
            {
                others_[processId] = Lifetime{time, Forever};
                tracked_.erase(processId);
            }
        }

        Release(processId, track);
    }

    void Tracker::OnProcessStop(const EVENT_RECORD& record)
    {
        EventProperties properties(record);
        auto processId = static_cast<uint32_t>(properties.IntegerOr(L"ProcessID", 0));
        int64_t time = record.EventHeader.TimeStamp.QuadPart;
        bool wasTracked = false;
        {
            ExclusiveLock guard(lock_);
            if (auto it = tracked_.find(processId); it != tracked_.end() && it->second.Covers(time))
            {
                it->second.Stop = time;
                wasTracked = true;
            }
            else if (auto other = others_.find(processId); other != others_.end() && other->second.Covers(time))
            {
                other->second.Stop = time;
            }
        }

        if (wasTracked)
        {
            std::wstring none;
            NiskalaEtwEvent event = MakeEvent(NISKALA_ETW_PROCESS_STOP, record, none);
            event.ProcessId = processId;
            Forward(event);
        }
    }

    void Tracker::Release(uint32_t processId, bool tracked)
    {
        std::vector<PendingEvent> released;
        {
            ExclusiveLock guard(lock_);
            for (auto it = pending_.begin(); it != pending_.end();)
            {
                if (it->Header.ProcessId != processId || Classify(processId, it->Header.TimeStamp.QuadPart) == Owner::Unknown)
                {
                    ++it;
                    continue;
                }

                if (tracked)
                {
                    released.push_back(std::move(*it));
                }

                it = pending_.erase(it);
            }
        }

        std::ranges::sort(released, {}, [](const PendingEvent& event) { return event.Header.TimeStamp.QuadPart; });
        for (PendingEvent& event : released)
        {
            EVENT_RECORD record{};
            record.EventHeader = event.Header;
            record.BufferContext = event.Buffer;
            record.UserDataLength = static_cast<USHORT>(event.Data.size());
            record.UserData = event.Data.data();
            Dispatch(record);
        }
    }

    void Tracker::ReplayRequested()
    {
        std::vector<uint32_t> requested;
        {
            ExclusiveLock guard(lock_);
            requested.assign(requested_.begin(), requested_.end());
            requested_.clear();
        }

        for (uint32_t processId : requested)
        {
            Release(processId, true);
        }
    }

    void Tracker::Dispatch(const EVENT_RECORD& record)
    {
        if (IsEqualGUID(record.EventHeader.ProviderId, FileProvider))
        {
            OnFile(record);
        }
        else if (IsEqualGUID(record.EventHeader.ProviderId, RegistryProvider))
        {
            OnRegistry(record);
        }
    }

    void Tracker::OnFile(const EVENT_RECORD& record)
    {
        EventProperties properties(record);
        uint32_t processId = record.EventHeader.ProcessId;
        switch (record.EventHeader.EventDescriptor.Id)
        {
        case FileCreateId:
        {
            std::wstring name = properties.Text(L"FileName");
            if (name.empty())
            {
                return;
            }

            uint64_t options = properties.IntegerOr(L"CreateOptions", 0);
            uint64_t disposition = (options >> CreateDispositionShift) & 0xFF;
            bool truncates = disposition == FileOverwrite || disposition == FileOverwriteIf;
            if (openFiles_.size() >= NameLimit)
            {
                openFiles_.clear();
            }

            openFiles_[FileKey(processId, properties.IntegerOr(L"FileObject", 0))] = OpenFile{name, truncates};
            if (truncates)
            {
                Forward(MakeEvent(NISKALA_ETW_FILE_WRITTEN, record, name));
            }

            return;
        }
        case FileCreateNewFileId:
        {
            std::wstring name = properties.Text(L"FileName");
            if (!name.empty())
            {
                uint64_t options = properties.IntegerOr(L"CreateOptions", 0);
                Forward(MakeEvent(NISKALA_ETW_FILE_CREATED, record, name, L"", (options & FileDirectoryFile) != 0 ? NISKALA_ETW_FLAG_DIRECTORY : 0));
            }

            return;
        }
        case FileWriteId:
        {
            auto it = openFiles_.find(FileKey(processId, properties.IntegerOr(L"FileObject", 0)));
            if (it != openFiles_.end() && !it->second.Written)
            {
                it->second.Written = true;
                Forward(MakeEvent(NISKALA_ETW_FILE_WRITTEN, record, it->second.Name));
            }

            return;
        }
        case FileDeletePathId:
        {
            std::wstring path = properties.Text(L"FilePath");
            if (!path.empty())
            {
                Forward(MakeEvent(NISKALA_ETW_FILE_DELETED, record, path));
            }

            return;
        }
        case FileRenamePathId:
        {
            std::wstring path = properties.Text(L"FilePath");
            auto it = openFiles_.find(FileKey(processId, properties.IntegerOr(L"FileObject", 0)));
            std::wstring opened = it == openFiles_.end() ? std::wstring() : it->second.Name;
            if (!path.empty())
            {
                Forward(MakeEvent(NISKALA_ETW_FILE_RENAMED, record, opened, path.c_str()));
            }

            return;
        }
        default:
            return;
        }
    }

    void Tracker::OnRegistry(const EVENT_RECORD& record)
    {
        EventProperties properties(record);
        if (properties.IntegerOr(L"Status", 1) != 0)
        {
            return;
        }

        uint64_t keyObject = properties.IntegerOr(L"KeyObject", 0);
        switch (record.EventHeader.EventDescriptor.Id)
        {
        case KeyCreateId:
        {
            std::wstring path = OpenedName(properties);
            if (!IsRegistryPath(path))
            {
                ++unnamed_;
                return;
            }

            Remember(keyObject, path);
            if (properties.IntegerOr(L"Disposition", 0) == CreatedNewKey)
            {
                Forward(MakeEvent(NISKALA_ETW_KEY_CREATED, record, path, L"", NISKALA_ETW_FLAG_NEW_KEY));
            }

            return;
        }
        case KeyOpenId:
        {
            std::wstring path = OpenedName(properties);
            if (IsRegistryPath(path))
            {
                Remember(keyObject, path);
            }

            return;
        }
        case KeyDeleteId:
        {
            std::wstring path = KeyName(keyObject, properties.Text(L"KeyName"));
            if (!path.empty())
            {
                keyNames_.erase(keyObject);
                Forward(MakeEvent(NISKALA_ETW_KEY_DELETED, record, path));
            }

            return;
        }
        case ValueSetId:
        {
            std::wstring path = KeyName(keyObject, properties.Text(L"KeyName"));
            if (path.empty())
            {
                return;
            }

            std::wstring value = properties.Text(L"ValueName");
            NiskalaEtwEvent event = MakeEvent(NISKALA_ETW_VALUE_SET, record, path, value.c_str());
            event.ValueType = static_cast<uint32_t>(properties.IntegerOr(L"Type", 0));
            std::vector<uint8_t> previous;
            if (properties.Has(L"PreviousDataType"))
            {
                previous = properties.Bytes(L"PreviousData");
                event.Flags |= NISKALA_ETW_FLAG_PREVIOUS;
                event.PreviousType = static_cast<uint32_t>(properties.IntegerOr(L"PreviousDataType", 0));
                event.PreviousSize = static_cast<uint32_t>(properties.IntegerOr(L"PreviousDataSize", 0));
                event.PreviousCaptured = static_cast<uint32_t>((std::min)(properties.IntegerOr(L"PreviousDataCapturedSize", 0), static_cast<uint64_t>(previous.size())));
                event.PreviousData = previous.data();
            }

            Forward(event);
            return;
        }
        case ValueDeleteId:
        {
            std::wstring path = KeyName(keyObject, properties.Text(L"KeyName"));
            if (!path.empty())
            {
                std::wstring value = properties.Text(L"ValueName");
                Forward(MakeEvent(NISKALA_ETW_VALUE_DELETED, record, path, value.c_str()));
            }

            return;
        }
        default:
            return;
        }
    }

    std::wstring Tracker::KeyName(uint64_t keyObject, const std::wstring& reported)
    {
        if (IsRegistryPath(reported))
        {
            return reported;
        }

        if (auto it = keyNames_.find(keyObject); it != keyNames_.end())
        {
            return it->second;
        }

        ++unnamed_;
        return {};
    }

    std::wstring Tracker::OpenedName(const EventProperties& properties) const
    {
        std::wstring base = properties.Text(L"BaseName");
        if (!IsRegistryPath(base))
        {
            uint64_t baseObject = properties.IntegerOr(L"BaseObject", 0);
            auto it = baseObject == 0 ? keyNames_.end() : keyNames_.find(baseObject);
            base = it == keyNames_.end() ? std::wstring() : it->second;
        }

        return Combine(base, properties.Text(L"RelativeName"));
    }

    void Tracker::Remember(uint64_t keyObject, const std::wstring& path)
    {
        if (keyNames_.size() >= NameLimit)
        {
            keyNames_.clear();
        }

        keyNames_[keyObject] = path;
    }

    void Tracker::Forward(const NiskalaEtwEvent& event)
    {
        ++forwarded_;
        callback_(context_, &event);
    }
}
