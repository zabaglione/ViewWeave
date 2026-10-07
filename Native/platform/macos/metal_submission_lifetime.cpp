#include "metal_submission_lifetime.h"

#include <new>

namespace lkg::metal {

bool IsValidBeginConfiguration(const TextureConfiguration& configuration) noexcept {
    constexpr uint32_t maxTextureDimension = 16384;
    constexpr uint32_t maxGridDimension = 16;
    constexpr uint32_t maxViewCount = 64;
    const bool supportedFormat = configuration.pixelFormat == kMetalPixelFormatRgba8Unorm ||
        configuration.pixelFormat == kMetalPixelFormatBgra8Unorm;
    return supportedFormat && configuration.width > 0 && configuration.height > 0 &&
        configuration.width <= maxTextureDimension && configuration.height <= maxTextureDimension &&
        configuration.columns > 0 && configuration.rows > 0 &&
        configuration.columns <= maxGridDimension && configuration.rows <= maxGridDimension &&
        configuration.columns * configuration.rows <= maxViewCount &&
        configuration.width % configuration.columns == 0 && configuration.height % configuration.rows == 0;
}

bool AreDevicesCompatible(void* textureDevice, void* unityDevice) noexcept {
    return textureDevice != nullptr && unityDevice != nullptr && textureDevice == unityDevice;
}

bool IsCompatibleSubmitConfiguration(const TextureConfiguration& expected,
    uint32_t width, uint32_t height, uint32_t pixelFormat,
    uint32_t columns, uint32_t rows) noexcept {
    return width == expected.width && height == expected.height && pixelFormat == expected.pixelFormat &&
        columns == expected.columns && rows == expected.rows;
}

SourceLease* SourceLease::Acquire(void* source, ReferenceCallback retain, ReferenceCallback release) noexcept {
    if (source == nullptr || retain == nullptr || release == nullptr) return nullptr;
    retain(source);
    SourceLease* lease = new (std::nothrow) SourceLease(source, release);
    if (lease == nullptr) release(source);
    return lease;
}

SourceLease::~SourceLease() {
    if (source_ != nullptr && release_ != nullptr) release_(source_);
}

bool OneShotGate::TryQueue() noexcept {
    std::lock_guard<std::mutex> lock(mutex_);
    if (state_ != State::Idle) return false;
    state_ = State::Queued;
    return true;
}

bool OneShotGate::Consume(bool valid) noexcept {
    std::lock_guard<std::mutex> lock(mutex_);
    if (state_ != State::Queued) return false;
    state_ = valid ? State::Consumed : State::Cancelled;
    return valid;
}

void OneShotGate::Cancel() noexcept {
    std::lock_guard<std::mutex> lock(mutex_);
    if (state_ == State::Queued) state_ = State::Cancelled;
}

bool BeginLifecycle::Queue(uint64_t generation, BeginTicket* ticket) noexcept {
    if (generation == 0 || ticket == nullptr) return false;
    std::lock_guard<std::mutex> lock(mutex_);
    if (state_ != State::Idle) return false;
    ticket_ = {generation, nextSerial_++};
    if (nextSerial_ == 0) nextSerial_ = 1;
    state_ = State::Queued;
    *ticket = ticket_;
    return true;
}

bool BeginLifecycle::Start(const BeginTicket& ticket, uint64_t currentGeneration, bool pluginActive) noexcept {
    std::lock_guard<std::mutex> lock(mutex_);
    if (state_ != State::Queued || ticket.serial == 0 || ticket.serial != ticket_.serial ||
        ticket.generation != ticket_.generation) return false;
    if (!pluginActive || currentGeneration != ticket_.generation) {
        state_ = State::Cancelled;
        return false;
    }
    state_ = State::Executing;
    return true;
}

bool BeginLifecycle::IsExecuting(const BeginTicket& ticket, uint64_t currentGeneration,
    bool pluginActive) const noexcept {
    std::lock_guard<std::mutex> lock(mutex_);
    return state_ == State::Executing && ticket.serial != 0 && ticket.serial == ticket_.serial &&
        ticket.generation == ticket_.generation && currentGeneration == ticket_.generation && pluginActive;
}

BeginFinish BeginLifecycle::Finish(const BeginTicket& ticket, uint64_t currentGeneration,
    bool pluginActive, bool success) noexcept {
    std::lock_guard<std::mutex> lock(mutex_);
    if (state_ != State::Executing || ticket.serial == 0 || ticket.serial != ticket_.serial ||
        ticket.generation != ticket_.generation) return BeginFinish::Cancelled;
    if (!pluginActive || currentGeneration != ticket_.generation) {
        state_ = State::Cancelled;
        return BeginFinish::Cancelled;
    }
    state_ = success ? State::Published : State::Failed;
    return success ? BeginFinish::Published : BeginFinish::Failed;
}

void BeginLifecycle::Cancel() noexcept {
    std::lock_guard<std::mutex> lock(mutex_);
    if (state_ == State::Queued || state_ == State::Executing) state_ = State::Cancelled;
}

bool SubmissionLifetime::TryReserve(uint64_t generation, void* retainedSource, uint32_t* slotIndex) noexcept {
    std::lock_guard<std::mutex> lock(mutex_);
    if (!accepting_ || generation == 0 || retainedSource == nullptr || slotIndex == nullptr) return false;
    for (uint32_t index = 0; index < slots_.size(); ++index) {
        if (slots_[index].state == SlotState::Free) {
            slots_[index] = {SlotState::ProducerPending, generation, retainedSource};
            *slotIndex = index;
            return true;
        }
    }
    return false;
}

bool SubmissionLifetime::BeginNextBridgeCall(uint32_t* slotIndex, uint64_t* generation) noexcept {
    std::lock_guard<std::mutex> lock(mutex_);
    if (!accepting_ || slotIndex == nullptr || generation == nullptr) return false;
    for (uint32_t index = 0; index < slots_.size(); ++index) {
        SlotSnapshot& slot = slots_[index];
        if (slot.state == SlotState::ProducerComplete) {
            slot.state = SlotState::BridgeCallInProgress;
            *slotIndex = index;
            *generation = slot.generation;
            return true;
        }
    }
    return false;
}

bool SubmissionLifetime::CompleteProducer(uint32_t slotIndex, uint64_t generation, bool success) noexcept {
    std::lock_guard<std::mutex> lock(mutex_);
    if (slotIndex >= slots_.size()) return false;
    SlotSnapshot& slot = slots_[slotIndex];
    if (slot.state != SlotState::ProducerPending || slot.generation != generation) return false;
    slot.state = success ? SlotState::ProducerComplete : SlotState::ProducerFailed;
    return true;
}

bool SubmissionLifetime::BeginBridgeCall(uint32_t slotIndex, uint64_t generation) noexcept {
    std::lock_guard<std::mutex> lock(mutex_);
    if (!accepting_) return false;
    if (slotIndex >= slots_.size()) return false;
    SlotSnapshot& slot = slots_[slotIndex];
    if (slot.state != SlotState::ProducerComplete || slot.generation != generation) return false;
    slot.state = SlotState::BridgeCallInProgress;
    return true;
}

bool SubmissionLifetime::MarkBridgeSubmitted(uint32_t slotIndex, uint64_t generation) noexcept {
    std::lock_guard<std::mutex> lock(mutex_);
    if (slotIndex >= slots_.size()) return false;
    SlotSnapshot& slot = slots_[slotIndex];
    if (slot.state != SlotState::BridgeCallInProgress || slot.generation != generation) return false;
    slot.state = SlotState::BridgeSubmitted;
    return true;
}

bool SubmissionLifetime::MarkConsumerUnknown(uint32_t slotIndex, uint64_t generation) noexcept {
    std::lock_guard<std::mutex> lock(mutex_);
    if (slotIndex >= slots_.size()) return false;
    SlotSnapshot& slot = slots_[slotIndex];
    if (slot.state != SlotState::BridgeSubmitted || slot.generation != generation) return false;
    slot.state = SlotState::ConsumerUnknownRetained;
    return true;
}

bool SubmissionLifetime::MarkBridgeCallFailedRetained(uint32_t slotIndex, uint64_t generation) noexcept {
    std::lock_guard<std::mutex> lock(mutex_);
    if (slotIndex >= slots_.size()) return false;
    SlotSnapshot& slot = slots_[slotIndex];
    if (slot.state != SlotState::BridgeCallInProgress || slot.generation != generation) return false;
    slot.state = SlotState::BridgeCallFailedRetained;
    return true;
}

void SubmissionLifetime::CloseAdmission() noexcept {
    std::lock_guard<std::mutex> lock(mutex_);
    accepting_ = false;
}

bool SubmissionLifetime::IsAccepting() const noexcept {
    std::lock_guard<std::mutex> lock(mutex_);
    return accepting_;
}

uint32_t SubmissionLifetime::RetainedCount() const noexcept {
    std::lock_guard<std::mutex> lock(mutex_);
    uint32_t count = 0;
    for (const SlotSnapshot& slot : slots_) {
        if (slot.state != SlotState::Free) ++count;
    }
    return count;
}

SlotSnapshot SubmissionLifetime::Snapshot(uint32_t slotIndex) const noexcept {
    std::lock_guard<std::mutex> lock(mutex_);
    if (slotIndex >= slots_.size()) return {};
    return slots_[slotIndex];
}

}
