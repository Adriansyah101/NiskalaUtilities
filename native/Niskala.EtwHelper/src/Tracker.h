#pragma once

#include "NiskalaEtw.h"

#include <windows.h>
#include <evntcons.h>

#include <cstdint>
#include <deque>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace niskala
{
    class EventProperties;

    class Tracker
    {
    public:
        Tracker(NiskalaEtwCallback callback, void* context) noexcept : callback_(callback), context_(context) {}

        Tracker(const Tracker&) = delete;
        Tracker& operator=(const Tracker&) = delete;

        void Seed();
        void Track(uint32_t processId);
        void OnEvent(const EVENT_RECORD& record);
        void Finish(NiskalaEtwStats& stats);

    private:
        struct Lifetime
        {
            int64_t Start;
            int64_t Stop;

            [[nodiscard]] bool Covers(int64_t time) const noexcept { return time >= Start && time <= Stop; }
        };

        struct PendingEvent
        {
            EVENT_HEADER Header;
            ETW_BUFFER_CONTEXT Buffer;
            std::vector<uint8_t> Data;
        };

        struct OpenFile
        {
            std::wstring Name;
            bool Written;
        };

        enum class Owner
        {
            Tracked,
            Other,
            Unknown,
        };

        Owner Classify(uint32_t processId, int64_t time) const;
        void OnProcessStart(const EVENT_RECORD& record);
        void OnProcessStop(const EVENT_RECORD& record);
        void Hold(const EVENT_RECORD& record);
        void Release(uint32_t processId, bool tracked);
        void ReplayRequested();
        void Dispatch(const EVENT_RECORD& record);
        void OnFile(const EVENT_RECORD& record);
        void OnRegistry(const EVENT_RECORD& record);
        std::wstring KeyName(uint64_t keyObject, const std::wstring& reported);
        std::wstring OpenedName(const EventProperties& properties) const;
        void Remember(uint64_t keyObject, const std::wstring& path);
        void Forward(const NiskalaEtwEvent& event);

        static uint64_t FileKey(uint32_t processId, uint64_t fileObject) noexcept { return fileObject ^ (static_cast<uint64_t>(processId) << 48); }

        NiskalaEtwCallback callback_;
        void* context_;

        mutable SRWLOCK lock_ = SRWLOCK_INIT;
        std::unordered_map<uint32_t, Lifetime> tracked_;
        std::unordered_map<uint32_t, Lifetime> others_;
        std::deque<PendingEvent> pending_;
        std::unordered_set<uint32_t> requested_;
        uint32_t pendingDropped_ = 0;

        std::unordered_map<uint64_t, OpenFile> openFiles_;
        std::unordered_map<uint64_t, std::wstring> keyNames_;
        uint64_t seen_ = 0;
        uint64_t forwarded_ = 0;
        uint32_t unnamed_ = 0;
    };
}
