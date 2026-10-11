#pragma once

#include "bridge_worker_transport.h"

#include <cstddef>
#include <string>

namespace lkg::subpixel {

constexpr size_t kMaximumMetadataBytes = 1024 * 1024;

bool ParseBridgeCellResponse(const char* json, size_t bytes,
    const LkgWorkerSubpixelCalibrationV1& expected,
    LkgWorkerSubpixelCellV1 cells[4], std::string& error);

bool ReadBridgeCellTable(const LkgWorkerSubpixelCalibrationV1& expected,
    LkgWorkerSubpixelCellV1 cells[4], std::string& error, uint16_t port = 33334,
    uint32_t timeoutMilliseconds = 10000);

} // namespace lkg::subpixel
