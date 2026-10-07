#pragma once

#include <array>
#include <cstdint>
#include <mutex>

namespace lkg::metal {

constexpr uint32_t kSubmissionSlotCount = 3;
constexpr uint32_t kMetalPixelFormatRgba8Unorm = 70;
constexpr uint32_t kMetalPixelFormatBgra8Unorm = 80;

struct TextureConfiguration {
    uint32_t width = 0;
    uint32_t height = 0;
    uint32_t pixelFormat = 0;
    uint32_t columns = 0;
    uint32_t rows = 0;
};

bool IsValidBeginConfiguration(const TextureConfiguration& configuration) noexcept;
bool AreDevicesCompatible(void* textureDevice, void* unityDevice) noexcept;
bool IsCompatibleSubmitConfiguration(const TextureConfiguration& expected,
    uint32_t width, uint32_t height, uint32_t pixelFormat,
    uint32_t columns, uint32_t rows) noexcept;

class SourceLease final {
public:
    using ReferenceCallback = void (*)(void*);

    static SourceLease* Acquire(void* source, ReferenceCallback retain, ReferenceCallback release) noexcept;
    ~SourceLease();
    SourceLease(const SourceLease&) = delete;
    SourceLease& operator=(const SourceLease&) = delete;

    void* Get() const noexcept { return source_; }

private:
    SourceLease(void* source, ReferenceCallback release) noexcept : source_(source), release_(release) {}
    void* source_;
    ReferenceCallback release_;
};

enum class SlotState : uint32_t {
    Free = 0,
    ProducerPending = 1,
    ProducerComplete = 2,
    BridgeCallInProgress = 3,
    BridgeSubmitted = 4,
    ConsumerUnknownRetained = 5,
    ProducerFailed = 6,
    BridgeCallFailedRetained = 7
};

struct SlotSnapshot {
    SlotState state = SlotState::Free;
    uint64_t generation = 0;
    void* retainedSource = nullptr;
};

class OneShotGate final {
public:
    bool TryQueue() noexcept;
    bool Consume(bool valid) noexcept;
    void Cancel() noexcept;

private:
    enum class State : uint32_t { Idle, Queued, Consumed, Cancelled };
    std::mutex mutex_;
    State state_ = State::Idle;
};

// This model intentionally never retires an accepted Bridge texture. The SDK
// exposes no consumer completion or release contract in the inspected header.
class SubmissionLifetime final {
public:
    bool TryReserve(uint64_t generation, void* retainedSource, uint32_t* slotIndex) noexcept;
    bool BeginNextBridgeCall(uint32_t* slotIndex, uint64_t* generation) noexcept;
    bool CompleteProducer(uint32_t slotIndex, uint64_t generation, bool success) noexcept;
    bool BeginBridgeCall(uint32_t slotIndex, uint64_t generation) noexcept;
    bool MarkBridgeSubmitted(uint32_t slotIndex, uint64_t generation) noexcept;
    bool MarkBridgeCallFailedRetained(uint32_t slotIndex, uint64_t generation) noexcept;
    bool MarkConsumerUnknown(uint32_t slotIndex, uint64_t generation) noexcept;
    void CloseAdmission() noexcept;

    bool IsAccepting() const noexcept;
    uint32_t RetainedCount() const noexcept;
    SlotSnapshot Snapshot(uint32_t slotIndex) const noexcept;

private:
    mutable std::mutex mutex_;
    std::array<SlotSnapshot, kSubmissionSlotCount> slots_{};
    bool accepting_ = true;
};

}
