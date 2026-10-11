#pragma once

#include <windows.h>
#include <stdint.h>

// Test-only controls for the Windows worker's real source-copy retirement path.
// A held query delays GetData observation; releasing it lets the real D3D11
// EVENT query establish completion.
enum LkgWorkerTestPresenterEarlyResult {
    LKG_WORKER_TEST_PRESENTER_NORMAL = 0,
    LKG_WORKER_TEST_PRESENTER_FAILED = 1,
    LKG_WORKER_TEST_PRESENTER_OCCLUDED = 2
};

enum LkgWorkerTestSourceCopyStateFlag {
    LKG_WORKER_TEST_SLOT_IN_USE = 1u,
    LKG_WORKER_TEST_SOURCE_UNPROVEN = 2u,
    LKG_WORKER_TEST_SOURCE_COPY_COMPLETED = 4u,
    LKG_WORKER_TEST_SOURCE_MUTEX_HELD = 8u,
    LKG_WORKER_TEST_SLOT_QUARANTINED = 16u,
    LKG_WORKER_TEST_D3D12_OWNERSHIP_UNPROVEN = 32u,
    LKG_WORKER_TEST_SOURCE_COPY_QUERY_ISSUED = 64u
};
