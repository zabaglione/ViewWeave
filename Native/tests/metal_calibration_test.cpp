#include "platform/macos/metal_calibration.h"
#include <iostream>
#include <limits>
#include <cstring>
#include <stdexcept>

namespace {
LkgMetalDisplayInfo Fixture(uint32_t width, uint32_t height, int columns, int rows) {
    LkgMetalDisplayInfo c{};
    c.display = {7, width, height, static_cast<float>(width) / height, 40, 4096, 4096, columns, rows};
    c.center = 0.17f; c.pitch = 45; c.slope = -7.5f; c.dpi = 300;
    return c;
}
void Require(bool ok, const char* message) { if (!ok) throw std::runtime_error(message); }
void Reject(LkgMetalDisplayInfo c, int mode = 0, int cells = 0, int red = 0, int blue = 2) {
    Require(lkg::metal::ValidateCalibration(c, mode, cells, 0, red, blue) != nullptr, "Invalid calibration was accepted");
}
}
int main() {
    try {
        // Synthetic profiles span portrait/landscape/8K dimensions and non-Go grids.
        // No model serial is passed into the acceptance decision.
        for (auto c : {Fixture(1440,2560,11,6), Fixture(1536,2048,8,6),
            Fixture(2560,1600,5,9), Fixture(3840,2160,8,6), Fixture(7680,4320,8,9)}) {
            for (int flip = 0; flip <= 1; ++flip) for (int invert = 0; invert <= 1; ++invert)
            for (int red : {0,2}) {
                c.flipX = static_cast<float>(flip); c.invertedViews = invert;
                Require(!lkg::metal::ValidateCalibration(c,0,0,0,red,2-red), "Generic profile was rejected");
            }
        }
        auto c = Fixture(2560,1600,5,9);
        Reject(c,1); Reject(c,0,1); Reject(c,0,-1); Reject(c,0,0,1,2); Reject(c,0,0,0,0);
        LkgMetalSubpixelCell cells[4] = {
            {-0.31f,-0.28f,0.28f,-0.28f,0,0.28f},
            {-0.31f,0.28f,0.28f,0.28f,0,-0.28f},
            {-0.22f,0.14f,0.34f,-0.23f,0.04f,0.31f},
            {0.19f,-0.31f,-0.34f,0.23f,-0.08f,0.11f}
        };
        c.invertedViews = 1;
        for (int mode = 0; mode <= 4; ++mode)
            Require(!lkg::metal::ValidateCalibration(c,mode,4,0,0,2,cells), "Valid cell pattern rejected");
        Require(lkg::metal::ValidateCalibration(c,5,4,0,0,2,cells), "Unknown mode accepted");
        Require(lkg::metal::ValidateCalibration(c,3,3,0,0,2,cells), "Missing addressable cell accepted");
        Require(lkg::metal::ValidateCalibration(c,0,5,0,0,2,cells), "Oversized cell table accepted");
        cells[1].greenY = std::numeric_limits<float>::quiet_NaN();
        Require(lkg::metal::ValidateCalibration(c,2,2,0,0,2,cells), "Nonfinite cell offset accepted");
        cells[1].greenY = std::numeric_limits<float>::max();
        cells[1].greenX = std::numeric_limits<float>::max();
        auto overflow = c; overflow.dpi = 1;
        Require(lkg::metal::ValidateCalibration(overflow,2,2,0,0,2,cells), "Cell phase overflow accepted");
        auto intermediateOverflow = Fixture(1,1,1,1);
        intermediateOverflow.pitch = 0.001f; intermediateOverflow.slope = 1;
        intermediateOverflow.dpi = 1; intermediateOverflow.invertedViews = 1;
        LkgMetalSubpixelCell extremeCell{2e38f,2e38f,0,0,0,0};
        Require(lkg::metal::ValidateCalibration(intermediateOverflow,0,1,0,0,2,&extremeCell),
            "Cell coordinate overflow before pitch multiplication accepted");
        auto unsupportedDirection = c; unsupportedDirection.invertedViews = 0;
        Require(lkg::metal::ValidateCalibration(unsupportedDirection,2,2,0,0,2,cells), "Unproven cell view direction accepted");
        auto bad = c; bad.display.quilt_columns = std::numeric_limits<int>::max(); Reject(bad);
        bad = c; bad.display.quilt_width = 4; Reject(bad);
        bad = c; bad.display.quilt_rows = 0; Reject(bad);
        bad = c; bad.display.width = 0; Reject(bad);
        bad = c; bad.display.height = 16385; Reject(bad);
        bad = c; bad.display.viewcone = 180; Reject(bad);
        bad = c; bad.display.aspect = std::numeric_limits<float>::quiet_NaN(); Reject(bad);
        bad = c; bad.slope = 0; Reject(bad);
        bad = c; bad.dpi = -1; Reject(bad);
        bad = c; bad.pitch = std::numeric_limits<float>::max(); Reject(bad);
        bad = c; bad.slope = std::numeric_limits<float>::max(); Reject(bad);
        bad = Fixture(1440,2560,11,6); bad.pitch = 2e35f; bad.slope = 0.1f; bad.dpi = 1; Reject(bad);
        bad = c; bad.center = std::numeric_limits<float>::infinity(); Reject(bad);
        bad = c; bad.flipX = 0.5f; Reject(bad);
        bad = c; bad.invertedViews = 2; Reject(bad);
        Require(lkg::metal::ValidateCalibration(c,0,0,std::numeric_limits<float>::quiet_NaN(),0,2), "Invalid fringe was accepted");
        LkgMetalDisplayInfoV3 info{}; info.structSize = sizeof(info); info.apiVersion = 3;
        Require(lkg::metal::IsDisplayInfoV3HeaderValid(&info), "Valid ABI header rejected");
        info.structSize = 76; Require(!lkg::metal::IsDisplayInfoV3HeaderValid(&info), "Short ABI buffer accepted");
        info.structSize = sizeof(info); info.apiVersion = 1;
        Require(!lkg::metal::IsDisplayInfoV3HeaderValid(&info), "Old ABI version accepted");
        Require(!lkg::metal::IsDisplayInfoV3HeaderValid(nullptr), "Null ABI accepted");
        std::cout << "Metal calibration validation passed: 5 synthetic profiles, 5 cell modes, invalid values and ABI guards\n";
    } catch (const std::exception& e) { std::cerr << e.what() << '\n'; return 1; }
    return 0;
}
