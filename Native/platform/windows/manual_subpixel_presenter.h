#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <d3d11.h>
#include <dxgi.h>

#include <cstdint>
#include <string>
#include <vector>

#include "../../bridge_worker_transport.h"

namespace lkg::subpixel {

enum class ManualPresentResult {
    Accepted,
    Occluded,
    Failed
};

// D3D11 manual subpixel presenter. Every method must be called on the worker
// thread that owns the device and HWND.
class ManualSubpixelPresenter final {
public:
    ManualSubpixelPresenter() = default;
    ManualSubpixelPresenter(const ManualSubpixelPresenter&) = delete;
    ManualSubpixelPresenter& operator=(const ManualSubpixelPresenter&) = delete;
    ~ManualSubpixelPresenter();

    bool Create(ID3D11Device* device,
                const LkgWorkerSubpixelCalibrationV1& calibration,
                const LkgWorkerSubpixelCellV1 cells[4],
                uint32_t mode,
                bool linearFlag,
                float displayAspect,
                std::string* error);

    ManualPresentResult Present(ID3D11Texture2D* quilt,
                                uint32_t columns,
                                uint32_t rows,
                                bool sourceIsSrgb,
                                float aspect,
                                float zoom,
                                std::string* error);

    // Returns false only for a device/query failure. Pending work is reported
    // separately by HasPendingGpuWork().
    bool PollGpuCompletion(std::string* error);
    // Pumps and validates native-window events and physical monitor geometry.
    // It is valid while the output is hidden or a preview lease is parked.
    bool PollWindowState(std::string* error);
    bool HasPendingGpuWork() const noexcept;
    uint32_t RenderCompleted() const noexcept;
    uint32_t PresentAccepted() const noexcept;

    bool SetVisible(bool visible, std::string* error);

    // Close releases native resources only when the event query proves that
    // all submitted GPU work has retired. False requires the caller to retain
    // this object and its resources.
    bool Close(std::string* error);

#if defined(LKG_SUBPIXEL_PRESENTER_TESTS)
    bool CreateOffscreenForTest(ID3D11Device* device,
                                const LkgWorkerSubpixelCalibrationV1& calibration,
                                const LkgWorkerSubpixelCellV1 cells[4],
                                uint32_t mode,
                                bool linearFlag,
                                float displayAspect,
                                std::string* error);
    bool ReadbackLastPresentedRGBA8(std::vector<uint8_t>& pixels,
                                    uint32_t* width,
                                    uint32_t* height,
                                    std::string* error);
#endif

private:
    struct State;
    bool CreateCore(ID3D11Device* device,
                    const LkgWorkerSubpixelCalibrationV1& calibration,
                    const LkgWorkerSubpixelCellV1 cells[4],
                    uint32_t mode,
                    bool linearFlag,
                    float displayAspect,
                    bool offscreenTest,
                    std::string* error);
    State* state_ = nullptr;
    uint32_t renderCompleted_ = 0;
    uint32_t presentAccepted_ = 0;
};

} // namespace lkg::subpixel
