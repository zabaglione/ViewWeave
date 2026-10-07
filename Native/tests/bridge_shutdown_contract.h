#pragma once

#include <stdint.h>

enum LkgFakeBridgeEventType : uint32_t {
    LKG_FAKE_EVENT_INITIALIZE = 1,
    LKG_FAKE_EVENT_UNREGISTER_TEXTURE = 2,
    LKG_FAKE_EVENT_RELEASE_TEXTURE = 3,
    LKG_FAKE_EVENT_RELEASE_DEVICE = 4,
    LKG_FAKE_EVENT_UNINITIALIZE = 5,
    LKG_FAKE_EVENT_DRAW_ENTER = 6,
    LKG_FAKE_EVENT_DRAW_EXIT = 7,
    LKG_FAKE_EVENT_REGISTER_TEXTURE = 8,
    LKG_FAKE_EVENT_CREATE_TEXTURE = 9,
    LKG_FAKE_EVENT_INSTANCE_WINDOW = 10,
    LKG_FAKE_EVENT_SHOW_WINDOW = 11
};

struct LkgFakeBridgeEvent {
    uint64_t sequence;
    uint64_t threadId;
    uint64_t generation;
    uint64_t pointerValue;
    uint32_t type;
    int32_t result;
};

typedef void (__cdecl *LkgFakeBridgeResetFn)();
typedef void (__cdecl *LkgFakeBridgeSetDrawModeFn)(int32_t failDraw, int32_t blockDraw);
typedef int32_t (__cdecl *LkgFakeBridgeWaitDrawEnteredFn)(uint32_t timeoutMilliseconds);
typedef void (__cdecl *LkgFakeBridgeReleaseDrawFn)();
typedef uint32_t (__cdecl *LkgFakeBridgeGetEventsFn)(LkgFakeBridgeEvent* events, uint32_t capacity);
