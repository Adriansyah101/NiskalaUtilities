#pragma once

#include <stdint.h>

#ifdef NISKALA_ETW_EXPORTS
#define NISKALA_ETW_API __declspec(dllexport)
#else
#define NISKALA_ETW_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define NISKALA_ETW_PROCESS_START 1u
#define NISKALA_ETW_PROCESS_STOP 2u
#define NISKALA_ETW_FILE_CREATED 10u
#define NISKALA_ETW_FILE_WRITTEN 11u
#define NISKALA_ETW_FILE_DELETED 12u
#define NISKALA_ETW_FILE_RENAMED 13u
#define NISKALA_ETW_KEY_CREATED 20u
#define NISKALA_ETW_KEY_DELETED 21u
#define NISKALA_ETW_VALUE_SET 22u
#define NISKALA_ETW_VALUE_DELETED 23u

#define NISKALA_ETW_FLAG_DIRECTORY 0x1u
#define NISKALA_ETW_FLAG_PARENT_TRACKED 0x2u
#define NISKALA_ETW_FLAG_NEW_KEY 0x4u
#define NISKALA_ETW_FLAG_PREVIOUS 0x8u

typedef struct NiskalaEtwEvent
{
    uint32_t Kind;
    uint32_t ProcessId;
    uint32_t ParentProcessId;
    uint32_t Flags;
    int64_t Timestamp;
    const wchar_t* Path;
    const wchar_t* Name;
    uint32_t ValueType;
    uint32_t PreviousType;
    uint32_t PreviousSize;
    uint32_t PreviousCaptured;
    const uint8_t* PreviousData;
} NiskalaEtwEvent;

typedef struct NiskalaEtwStats
{
    uint64_t EventsSeen;
    uint64_t EventsForwarded;
    uint32_t EventsLost;
    uint32_t BuffersLost;
    uint32_t PendingDropped;
    uint32_t UnnamedEvents;
} NiskalaEtwStats;

typedef int32_t(__stdcall* NiskalaEtwCallback)(void* context, const NiskalaEtwEvent* event);

typedef struct NiskalaEtwSession NiskalaEtwSession;

NISKALA_ETW_API uint32_t __stdcall NiskalaEtwStart(NiskalaEtwCallback callback, void* context, NiskalaEtwSession** session);

NISKALA_ETW_API uint32_t __stdcall NiskalaEtwTrack(NiskalaEtwSession* session, uint32_t processId);

NISKALA_ETW_API uint32_t __stdcall NiskalaEtwStop(NiskalaEtwSession* session, NiskalaEtwStats* stats);

#ifdef __cplusplus
}
#endif
