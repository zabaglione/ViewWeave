#include "../platform/macos/metal_submission_lifetime.h"

#include <cstdio>

using lkg::metal::SlotState;
using lkg::metal::SubmissionLifetime;

namespace {

struct FakeTexture {
    uint32_t references = 1;
    bool destroyed = false;
};

void RetainFake(void* object) {
    ++static_cast<FakeTexture*>(object)->references;
}

void ReleaseFake(void* object) {
    FakeTexture* texture = static_cast<FakeTexture*>(object);
    if (--texture->references == 0) texture->destroyed = true;
}

}

int main() {
    if (!lkg::metal::IsValidBeginConfiguration({7680, 4320, lkg::metal::kMetalPixelFormatBgra8Unorm, 8, 6})) return 1;
    if (lkg::metal::IsValidBeginConfiguration({0, 4320, lkg::metal::kMetalPixelFormatBgra8Unorm, 8, 6})) return 2;
    if (lkg::metal::IsValidBeginConfiguration({7680, 4320, 999, 8, 6})) return 3;
    if (lkg::metal::IsValidBeginConfiguration({7680, 4320, lkg::metal::kMetalPixelFormatBgra8Unorm, 16, 16})) return 4;
    const lkg::metal::TextureConfiguration expected{7680, 4320, lkg::metal::kMetalPixelFormatBgra8Unorm, 8, 6};
    if (!lkg::metal::IsCompatibleSubmitConfiguration(expected, 7680, 4320,
        lkg::metal::kMetalPixelFormatBgra8Unorm, 8, 6)) return 5;
    if (lkg::metal::IsCompatibleSubmitConfiguration(expected, 3840, 2160,
        lkg::metal::kMetalPixelFormatBgra8Unorm, 8, 6)) return 6;
    if (lkg::metal::IsCompatibleSubmitConfiguration(expected, 7680, 4320,
        lkg::metal::kMetalPixelFormatRgba8Unorm, 8, 6)) return 7;
    if (lkg::metal::IsCompatibleSubmitConfiguration(expected, 7680, 4320,
        lkg::metal::kMetalPixelFormatBgra8Unorm, 6, 8)) return 8;

    SubmissionLifetime lifetime;
    uint32_t slots[4] = {};
    FakeTexture fakeTexture;
    lkg::metal::SourceLease* leases[lkg::metal::kSubmissionSlotCount]{};

    for (uint32_t index = 0; index < lkg::metal::kSubmissionSlotCount; ++index) {
        leases[index] = lkg::metal::SourceLease::Acquire(&fakeTexture, RetainFake, ReleaseFake);
        if (leases[index] == nullptr || !lifetime.TryReserve(index + 1, leases[index], &slots[index])) return 9;
        if (lifetime.Snapshot(slots[index]).retainedSource != leases[index] || leases[index]->Get() != &fakeTexture) return 10;
        if (lifetime.Snapshot(slots[index]).state != SlotState::ProducerPending) return 11;
        if (index == 0) {
            ReleaseFake(&fakeTexture); // Caller releases before the render event runs.
            if (fakeTexture.destroyed || fakeTexture.references != 1) return 12;
        }
        if (!lifetime.CompleteProducer(slots[index], index + 1, true)) return 13;
        if (!lifetime.BeginBridgeCall(slots[index], index + 1)) return 14;
        if (!lifetime.MarkBridgeSubmitted(slots[index], index + 1)) return 15;
        if (!lifetime.MarkConsumerUnknown(slots[index], index + 1)) return 16;
    }

    if (fakeTexture.destroyed || fakeTexture.references != 3) return 17;
    if (lifetime.RetainedCount() != lkg::metal::kSubmissionSlotCount) return 18;
    if (lifetime.TryReserve(4, leases[0], &slots[3])) return 19;
    if (lifetime.Snapshot(slots[0]).state != SlotState::ConsumerUnknownRetained) return 20;

    lifetime.CloseAdmission();
    if (lifetime.IsAccepting()) return 21;
    if (lifetime.TryReserve(5, leases[1], &slots[3])) return 22;
    if (lifetime.RetainedCount() != lkg::metal::kSubmissionSlotCount) return 23;

    SubmissionLifetime delayed;
    uint32_t delayedSlot = 0;
    if (!delayed.TryReserve(9, leases[2], &delayedSlot)) return 24;
    if (delayed.CompleteProducer(delayedSlot, 8, true)) return 25;
    delayed.CloseAdmission();
    if (!delayed.CompleteProducer(delayedSlot, 9, true)) return 26;
    if (delayed.BeginBridgeCall(delayedSlot, 9)) return 27;
    if (delayed.Snapshot(delayedSlot).state != SlotState::ProducerComplete) return 28;

    SubmissionLifetime failedProducer;
    uint32_t failedSlot = 0;
    if (!failedProducer.TryReserve(11, leases[0], &failedSlot)) return 29;
    if (!failedProducer.CompleteProducer(failedSlot, 11, false)) return 30;
    if (failedProducer.Snapshot(failedSlot).state != SlotState::ProducerFailed) return 31;
    if (failedProducer.BeginBridgeCall(failedSlot, 11)) return 32;

    SubmissionLifetime failedBridge;
    uint32_t failedBridgeSlot = 0;
    if (!failedBridge.TryReserve(12, leases[1], &failedBridgeSlot)) return 33;
    if (!failedBridge.CompleteProducer(failedBridgeSlot, 12, true)) return 34;
    if (!failedBridge.BeginBridgeCall(failedBridgeSlot, 12)) return 35;
    if (!failedBridge.MarkBridgeCallFailedRetained(failedBridgeSlot, 12)) return 36;
    if (failedBridge.Snapshot(failedBridgeSlot).state != SlotState::BridgeCallFailedRetained) return 37;

    SubmissionLifetime pump;
    uint32_t pumpSlots[lkg::metal::kSubmissionSlotCount]{};
    for (uint32_t index = 0; index < lkg::metal::kSubmissionSlotCount; ++index) {
        if (!pump.TryReserve(21 + index, leases[index], &pumpSlots[index])) return 38;
    }
    if (pump.TryReserve(24, leases[0], &slots[3])) return 39;
    for (uint32_t index = 0; index < lkg::metal::kSubmissionSlotCount; ++index) {
        if (!pump.CompleteProducer(pumpSlots[index], 21 + index, true)) return 40;
    }
    uint32_t bridgeCalls = 0;
    uint32_t pumpSlot = 0;
    uint64_t pumpGeneration = 0;
    while (pump.BeginNextBridgeCall(&pumpSlot, &pumpGeneration)) {
        if (!pump.MarkBridgeSubmitted(pumpSlot, pumpGeneration) || !pump.MarkConsumerUnknown(pumpSlot, pumpGeneration)) return 41;
        ++bridgeCalls;
    }
    if (bridgeCalls != lkg::metal::kSubmissionSlotCount) return 42;

    lkg::metal::BeginLifecycle cancelledBegin;
    lkg::metal::BeginTicket cancelledTicket{};
    if (!cancelledBegin.Queue(31, &cancelledTicket)) return 43;
    if (!cancelledBegin.Start(cancelledTicket, 31, true)) return 44;
    uint32_t fakeSdkCalls = 0;
    bool runtimePublished = false;
    // Model an SDK initializer that overlaps close/device reset. The external
    // call runs with no BeginLifecycle lock held, then cancellation is observed
    // before its result can be published.
    ++fakeSdkCalls;
    cancelledBegin.Cancel();
    const lkg::metal::BeginFinish cancelledResult = cancelledBegin.Finish(cancelledTicket, 32, false, true);
    runtimePublished = cancelledResult == lkg::metal::BeginFinish::Published;
    if (fakeSdkCalls != 1 || runtimePublished || cancelledResult != lkg::metal::BeginFinish::Cancelled) return 45;
    if (cancelledBegin.Start(cancelledTicket, 32, true)) return 46;

    lkg::metal::BeginLifecycle validBegin;
    lkg::metal::BeginTicket validTicket{};
    if (!validBegin.Queue(41, &validTicket) || !validBegin.Start(validTicket, 41, true)) return 47;
    if (validBegin.Finish(validTicket, 42, true, true) != lkg::metal::BeginFinish::Cancelled) return 48;
    if (validBegin.Finish(validTicket, 41, true, true) != lkg::metal::BeginFinish::Cancelled) return 49;

    lkg::metal::BeginLifecycle successfulBegin;
    lkg::metal::BeginTicket successfulTicket{};
    if (!successfulBegin.Queue(51, &successfulTicket) || !successfulBegin.Start(successfulTicket, 51, true)) return 50;
    if (!successfulBegin.IsExecuting(successfulTicket, 51, true)) return 51;
    if (successfulBegin.Finish(successfulTicket, 51, true, true) != lkg::metal::BeginFinish::Published) return 52;
    if (successfulBegin.Finish(successfulTicket, 51, true, true) != lkg::metal::BeginFinish::Cancelled) return 53;

    for (lkg::metal::SourceLease* lease : leases) delete lease;
    if (!fakeTexture.destroyed || fakeTexture.references != 0) return 54;

    std::puts("metal_submission_lifetime: PASS");
    return 0;
}
