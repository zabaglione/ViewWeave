#include "platform/macos/metal_display.h"
#include <dlfcn.h>
#include <array>
#include <cstring>
#include <iostream>
#include <stdexcept>
void Require(bool ok, const char* error) { if (!ok) throw std::runtime_error(error); }
int main(int argc, char** argv) {
    try {
        Require(argc == 3, "Expected shim and fake metadata runtime paths");
        void* shim = dlopen(argv[1],RTLD_NOW | RTLD_LOCAL);
        Require(shim != nullptr,"Shim could not be loaded");
        auto open = reinterpret_cast<uint64_t(*)(const char*,const char*,uint32_t,LkgMetalDisplayInfoV2*)>(dlsym(shim,"lkg_metal_display_open_v2"));
        auto legacy = reinterpret_cast<uint64_t(*)(const char*,const char*,uint32_t,LkgMetalDisplayInfo*)>(dlsym(shim,"lkg_metal_display_open"));
        auto error = reinterpret_cast<const char*(*)()>(dlsym(shim,"lkg_metal_display_error"));
        Require(open && legacy && error,"Metal exports missing");
        LkgMetalDisplayInfoV2 c{}; c.structSize = sizeof(c); c.apiVersion = 2;
        for (uint32_t ordinal = 0; ordinal < 2; ++ordinal) {
            Require(open(argv[2],"Synthetic Metal metadata validation",ordinal,&c) == 0,"Synthetic display unexpectedly matched a real screen");
            Require(std::strstr(error(),"does not match an extended macOS screen"),error());
            Require(c.calibration.display.quilt_columns == 8 && c.calibration.display.quilt_rows == 6,"Quilt metadata was lost");
            Require(c.redIndex == (ordinal == 1 ? 2 : 0) && c.calibration.flipX == (ordinal == 1 ? 1 : 0),"Channel/flip metadata was lost");
        }
        Require(open(argv[2],"Unsupported pattern validation",2,&c) == 0,"Unknown mode accepted");
        Require(std::strstr(error(),"Unsupported calibration: subpixel"),error());
        LkgMetalDisplayInfo old{};
        Require(legacy(argv[2],"Legacy ABI validation",1,&old) == 0,"Old shader accepted BGR/flip");
        Require(std::strstr(error(),"requires Metal display API v2"),error());
        std::array<uint32_t,19> shortBuffer{}; shortBuffer.fill(0xa5a5a5a5);
        shortBuffer[0] = 60; shortBuffer[1] = 2; auto original = shortBuffer;
        Require(open(argv[2],"Short buffer validation",0,reinterpret_cast<LkgMetalDisplayInfoV2*>(shortBuffer.data())) == 0,"Short ABI accepted");
        Require(shortBuffer == original,"ABI guard modified the caller's short buffer");
        Require(std::strstr(error(),"ABI mismatch"),error());
        dlclose(shim);
        std::cout << "Metal display metadata passed: no serial symbol, generic RGB/BGR/flip, unknown-mode rejection, legacy and short-buffer guards\n";
    } catch (const std::exception& e) { std::cerr << e.what() << '\n'; return 1; }
    return 0;
}
