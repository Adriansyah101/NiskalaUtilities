#include "NiskalaEtw.h"

#include "Providers.h"
#include "Tracker.h"

#include <windows.h>
#include <evntrace.h>

#include <cwchar>
#include <iterator>
#include <memory>
#include <string>
#include <utility>
#include <vector>

namespace
{
    using niskala::FileProvider;
    using niskala::ProcessProvider;
    using niskala::RegistryProvider;

    constexpr ULONGLONG ProcessKeywords = 0x10;
    constexpr ULONGLONG FileKeywords = 0x80 | 0x200 | 0x400 | 0x800 | 0x1000;
    constexpr ULONGLONG RegistryKeywords = 0x100 | 0x200 | 0x1000 | 0x2000 | 0x4000;

    constexpr wchar_t SessionPrefix[] = L"Niskala Tracker ";
    constexpr ULONG MaxSessionName = 1024;
    constexpr ULONG MaxSessions = 64;
    constexpr DWORD StopTimeoutMs = 15'000;
    constexpr int LiveAttempts = 40;
    constexpr DWORD LiveIntervalMs = 250;

    std::vector<uint8_t> NewProperties()
    {
        std::vector<uint8_t> buffer(sizeof(EVENT_TRACE_PROPERTIES) + MaxSessionName * sizeof(wchar_t));
        auto* properties = reinterpret_cast<EVENT_TRACE_PROPERTIES*>(buffer.data());
        properties->Wnode.BufferSize = static_cast<ULONG>(buffer.size());
        properties->LoggerNameOffset = sizeof(EVENT_TRACE_PROPERTIES);
        return buffer;
    }

    bool IsRunning(DWORD processId)
    {
        HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, processId);
        if (process == nullptr)
        {
            return GetLastError() != ERROR_INVALID_PARAMETER;
        }

        DWORD code = 0;
        bool running = GetExitCodeProcess(process, &code) && code == STILL_ACTIVE;
        CloseHandle(process);
        return running;
    }

    void StopOrphanedSessions()
    {
        std::vector<std::vector<uint8_t>> buffers(MaxSessions);
        std::vector<EVENT_TRACE_PROPERTIES*> sessions(MaxSessions);
        for (ULONG i = 0; i < MaxSessions; i++)
        {
            buffers[i] = NewProperties();
            sessions[i] = reinterpret_cast<EVENT_TRACE_PROPERTIES*>(buffers[i].data());
        }

        ULONG count = 0;
        if (QueryAllTracesW(sessions.data(), MaxSessions, &count) != ERROR_SUCCESS && count == 0)
        {
            return;
        }

        constexpr size_t prefixLength = std::size(SessionPrefix) - 1;
        for (ULONG i = 0; i < count; i++)
        {
            const auto* name = reinterpret_cast<const wchar_t*>(buffers[i].data() + sessions[i]->LoggerNameOffset);
            if (wcsncmp(name, SessionPrefix, prefixLength) != 0)
            {
                continue;
            }

            DWORD owner = wcstoul(name + prefixLength, nullptr, 10);
            if (owner != 0 && !IsRunning(owner))
            {
                std::vector<uint8_t> stop = NewProperties();
                ControlTraceW(0, name, reinterpret_cast<EVENT_TRACE_PROPERTIES*>(stop.data()), EVENT_TRACE_CONTROL_STOP);
            }
        }
    }
}

struct NiskalaEtwSession
{
    NiskalaEtwSession(NiskalaEtwCallback callback, void* context) : Tracker(callback, context) {}

    niskala::Tracker Tracker;
    std::wstring Name;
    std::vector<uint8_t> Properties = NewProperties();
    TRACEHANDLE Control = 0;
    PROCESSTRACE_HANDLE Consumer = INVALID_PROCESSTRACE_HANDLE;
    HANDLE Thread = nullptr;
    HANDLE Live = nullptr;

    EVENT_TRACE_PROPERTIES* Props() noexcept { return reinterpret_cast<EVENT_TRACE_PROPERTIES*>(Properties.data()); }

    ~NiskalaEtwSession()
    {
        if (Control != 0)
        {
            ControlTraceW(Control, nullptr, Props(), EVENT_TRACE_CONTROL_STOP);
        }

        if (Thread != nullptr)
        {
            if (WaitForSingleObject(Thread, StopTimeoutMs) != WAIT_OBJECT_0 && Consumer != INVALID_PROCESSTRACE_HANDLE)
            {
                CloseTrace(Consumer);
                Consumer = INVALID_PROCESSTRACE_HANDLE;
                WaitForSingleObject(Thread, StopTimeoutMs);
            }

            CloseHandle(Thread);
        }

        if (Consumer != INVALID_PROCESSTRACE_HANDLE)
        {
            CloseTrace(Consumer);
        }

        if (Live != nullptr)
        {
            CloseHandle(Live);
        }
    }
};

namespace
{
    void WINAPI OnEventRecord(PEVENT_RECORD record)
    {
        static_cast<NiskalaEtwSession*>(record->UserContext)->Tracker.OnEvent(*record);
    }

    ULONG WINAPI OnBuffer(PEVENT_TRACE_LOGFILEW log)
    {
        SetEvent(static_cast<NiskalaEtwSession*>(log->Context)->Live);
        return TRUE;
    }

    DWORD WINAPI Consume(void* parameter)
    {
        auto* session = static_cast<NiskalaEtwSession*>(parameter);
        return ProcessTrace(&session->Consumer, 1, nullptr, nullptr);
    }

    ULONG Enable(TRACEHANDLE control, const GUID& provider, ULONGLONG keywords)
    {
        return EnableTraceEx2(control, &provider, EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_VERBOSE, keywords, 0, 0, nullptr);
    }

    ULONG Open(NiskalaEtwSession& session)
    {
        session.Name = SessionPrefix + std::to_wstring(GetCurrentProcessId());
        session.Live = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (session.Live == nullptr)
        {
            return GetLastError();
        }

        EVENT_TRACE_PROPERTIES* properties = session.Props();
        properties->Wnode.Flags = WNODE_FLAG_TRACED_GUID;
        properties->Wnode.ClientContext = 1;
        properties->LogFileMode = EVENT_TRACE_REAL_TIME_MODE;
        properties->BufferSize = 256;
        properties->MinimumBuffers = 64;
        properties->MaximumBuffers = 512;
        properties->FlushTimer = 1;

        ULONG error = StartTraceW(&session.Control, session.Name.c_str(), properties);
        if (error != ERROR_SUCCESS)
        {
            session.Control = 0;
            return error;
        }

        const std::pair<const GUID*, ULONGLONG> providers[] = {{&ProcessProvider, ProcessKeywords}, {&FileProvider, FileKeywords}, {&RegistryProvider, RegistryKeywords}};
        for (auto [provider, keywords] : providers)
        {
            error = Enable(session.Control, *provider, keywords);
            if (error != ERROR_SUCCESS)
            {
                return error;
            }
        }

        EVENT_TRACE_LOGFILEW log{};
        log.LoggerName = session.Name.data();
        log.ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD;
        log.EventRecordCallback = OnEventRecord;
        log.BufferCallback = OnBuffer;
        log.Context = &session;
        session.Consumer = OpenTraceW(&log);
        if (session.Consumer == INVALID_PROCESSTRACE_HANDLE)
        {
            return GetLastError();
        }

        session.Thread = CreateThread(nullptr, 0, Consume, &session, 0, nullptr);
        if (session.Thread == nullptr)
        {
            return GetLastError();
        }

        for (int attempt = 0; attempt < LiveAttempts; attempt++)
        {
            ControlTraceW(session.Control, nullptr, session.Props(), EVENT_TRACE_CONTROL_FLUSH);
            if (WaitForSingleObject(session.Live, LiveIntervalMs) == WAIT_OBJECT_0)
            {
                session.Tracker.Seed();
                return ERROR_SUCCESS;
            }
        }

        return ERROR_TIMEOUT;
    }
}

extern "C" NISKALA_ETW_API uint32_t __stdcall NiskalaEtwStart(NiskalaEtwCallback callback, void* context, NiskalaEtwSession** session)
{
    if (callback == nullptr || session == nullptr)
    {
        return ERROR_INVALID_PARAMETER;
    }

    *session = nullptr;
    StopOrphanedSessions();
    auto created = std::make_unique<NiskalaEtwSession>(callback, context);
    ULONG error = Open(*created);
    if (error != ERROR_SUCCESS)
    {
        return error;
    }

    *session = created.release();
    return ERROR_SUCCESS;
}

extern "C" NISKALA_ETW_API uint32_t __stdcall NiskalaEtwTrack(NiskalaEtwSession* session, uint32_t processId)
{
    if (session == nullptr || processId == 0)
    {
        return ERROR_INVALID_PARAMETER;
    }

    session->Tracker.Track(processId);
    return ERROR_SUCCESS;
}

extern "C" NISKALA_ETW_API uint32_t __stdcall NiskalaEtwStop(NiskalaEtwSession* session, NiskalaEtwStats* stats)
{
    if (session == nullptr)
    {
        return ERROR_INVALID_PARAMETER;
    }

    std::unique_ptr<NiskalaEtwSession> owned(session);
    ULONG error = ControlTraceW(owned->Control, nullptr, owned->Props(), EVENT_TRACE_CONTROL_STOP);
    owned->Control = 0;
    NiskalaEtwStats result{};
    result.EventsLost = owned->Props()->EventsLost;
    result.BuffersLost = owned->Props()->RealTimeBuffersLost + owned->Props()->LogBuffersLost;
    if (WaitForSingleObject(owned->Thread, StopTimeoutMs) != WAIT_OBJECT_0)
    {
        CloseTrace(owned->Consumer);
        owned->Consumer = INVALID_PROCESSTRACE_HANDLE;
        WaitForSingleObject(owned->Thread, StopTimeoutMs);
    }

    owned->Tracker.Finish(result);
    if (stats != nullptr)
    {
        *stats = result;
    }

    return error;
}
