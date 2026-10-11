#include "bridge_worker_transport.h"
#include <dlfcn.h>
#include <chrono>
#include <thread>
#include <stdexcept>
#include <iostream>
#include <cmath>
static void Require(bool ok, const char* reason) { if (!ok) throw std::runtime_error(reason); }
int main(int argc, char** argv) {
    try {
        Require(argc >= 3, "Expected shim and fake runtime");
        void* module = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
        Require(module != nullptr, "Shim load failed");
        auto begin = reinterpret_cast<void*(*)(const char*,const char*)>(dlsym(module,"lkg_worker_begin_metadata_v1"));
        auto close = reinterpret_cast<int32_t(*)(void*)>(dlsym(module,"lkg_worker_close"));
        auto end = reinterpret_cast<int32_t(*)(void*)>(dlsym(module,"lkg_worker_end_metadata_v1"));
        auto get = reinterpret_cast<int32_t(*)(void*,LkgWorkerDisplaySnapshotStatusV1*,LkgWorkerDisplaySnapshotItemV1*,uint32_t,uint32_t*)>(dlsym(module,"lkg_worker_get_display_snapshot_v1"));
        Require(begin && close && end && get, "Metadata ABI exports missing");
        auto run = [&](const char* name, bool cancel, bool fault) {
            void* handle = begin(argv[2],name); Require(handle != nullptr,"Begin failed");
            Require(begin(argv[2],"duplicate") == nullptr,"Double ownership accepted");
            Require(end(handle) == -3,"End accepted without Close");
            if (cancel) { Require(close(handle) == 0 && close(handle) == 0,"Close must be idempotent"); }
            LkgWorkerDisplaySnapshotStatusV1 status{}; status.structSize = sizeof(status); status.version = 1;
            LkgWorkerDisplaySnapshotItemV1 items[256]{}; uint32_t count = 0;
            auto deadline = std::chrono::steady_clock::now()+std::chrono::seconds(5);
            do {
                Require(get(handle,&status,items,256,&count) == 0,"Snapshot ABI failed");
                if (!status.pending) break;
                std::this_thread::sleep_for(std::chrono::milliseconds(5));
            } while (std::chrono::steady_clock::now() < deadline);
            Require(!status.pending,"Query did not finish");
            if (!fault) {
                Require(status.result == 0 && count == 2 && status.cleanupFinished == 1,"Snapshot success/cleanup missing");
                Require(items[0].displayOrdinal == 0 && items[0].profile.index == 17,"Ordinal and index confused");
                Require(std::fabs(items[0].profile.aspect-9.0f/16) < 1e-6 &&
                    std::fabs(items[1].profile.aspect-16.0f/9) < 1e-6,"Portrait/landscape lost");
                Require(items[0].physical.valid == 1 && items[0].physical.pixelHeight == 2560,"Physical metadata lost");
            } else Require(status.result != 0 && count == 0,"Failed query published geometry");
            Require(close(handle) == 0 && close(handle) == 0,"Close failed");
            if (argc > 3) {
                Require(status.cleanupFinished == 0 && end(handle) == -3,"Unproven shutdown released ownership");
                Require(begin(argv[2],"retry") == nullptr,"Shutdown failure allowed retry");
            } else Require(end(handle) == 0,"Proven cleanup did not release");
        };
        if (argc > 3) run("shutdown-failure",false,true);
        else {
            run("normal",false,false); run("slow-cancel",true,false);
            run("init-failure",false,true); run("query-failure",false,true); run("restart",false,false);
            run("dimensions-failure",false,true); run("cone-failure",false,true); run("quilt-failure",false,true);
        }
        std::cout << "macOS metadata-only worker contract passed\n";
    } catch (const std::exception& e) { std::cerr << e.what() << '\n'; return 1; }
    return 0;
}
