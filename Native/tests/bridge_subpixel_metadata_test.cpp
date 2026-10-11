#include <winsock2.h>
#include <ws2tcpip.h>

#include "platform/windows/bridge_subpixel_metadata.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cstdint>
#include <string>
#include <thread>
#include <vector>

static void Check(bool value, const char* message) {
    if (value) return;
    std::fprintf(stderr, "FAIL %s\n", message);
    std::exit(1);
}

static LkgWorkerSubpixelCalibrationV1 Expected() {
    LkgWorkerSubpixelCalibrationV1 value{};
    value.structSize = sizeof(value);
    value.version = 1;
    value.displayIndex = 3;
    value.nativeWidth = 2560;
    value.nativeHeight = 1600;
    value.center = 0.5f;
    value.pitch = 50.0f;
    value.slope = -7.0f;
    value.dpi = 254.0f;
    value.flipX = 1.0f;
    value.viewCone = 40.0f;
    value.fringe = 0.0f;
    value.invertedViews = 1;
    value.cellPatternMode = 3;
    value.cellCount = 4;
    value.windowX = 1920;
    value.windowY = 0;
    value.redIndex = 0;
    value.blueIndex = 2;
    return value;
}

static std::string Response() {
    return R"({"status":"Completion","payload":{"display":{"index":3,"windowCoords":{"x":1920,"y":0},"calibration":{"screenW":2560,"screenH":1600,"center":0.5,"pitch":50,"slope":-7,"DPI":254,"flipImageX":1,"invView":1,"viewCone":40,"fringe":0,"CellPatternMode":3,"subpixelCells":[{"ROffsetX":0.1,"ROffsetY":0.2,"GOffsetX":0.3,"GOffsetY":0.4,"BOffsetX":0.5,"BOffsetY":0.6},{"ROffsetX":-0.1,"ROffsetY":-0.2,"GOffsetX":-0.3,"GOffsetY":-0.4,"BOffsetX":-0.5,"BOffsetY":-0.6},{"ROffsetX":0.01,"ROffsetY":0.02,"GOffsetX":0.03,"GOffsetY":0.04,"BOffsetX":0.05,"BOffsetY":0.06},{"ROffsetX":-0.01,"ROffsetY":-0.02,"GOffsetX":-0.03,"GOffsetY":-0.04,"BOffsetX":-0.05,"BOffsetY":-0.06}]}}}})";
}

enum class FakeServerMode { Success, AvailableFailure, ExitFailure, SlowDrip };

static bool SendAll(SOCKET socket, const char* data, size_t size) {
    while (size) {
        const int sent = send(socket, data, static_cast<int>(std::min<size_t>(size, 8192)), 0);
        if (sent <= 0) return false;
        data += sent;
        size -= static_cast<size_t>(sent);
    }
    return true;
}

static bool ReadHttpRequest(SOCKET socket, std::string& route) {
    std::string bytes;
    char buffer[2048];
    size_t headerEnd = std::string::npos;
    size_t bodyLength = 0;
    while (bytes.size() < 16384) {
        const int received = recv(socket, buffer, sizeof(buffer), 0);
        if (received <= 0) return false;
        bytes.append(buffer, static_cast<size_t>(received));
        headerEnd = bytes.find("\r\n\r\n");
        if (headerEnd != std::string::npos) {
            const size_t firstEnd = bytes.find("\r\n");
            if (firstEnd == std::string::npos) return false;
            const size_t firstSpace = bytes.find(' ');
            const size_t secondSpace = bytes.find(' ', firstSpace + 1);
            if (firstSpace == std::string::npos || secondSpace == std::string::npos) return false;
            route = bytes.substr(firstSpace + 1, secondSpace - firstSpace - 1);
            const std::string headers = bytes.substr(0, headerEnd);
            const size_t lengthAt = headers.find("Content-Length:");
            if (lengthAt != std::string::npos) bodyLength = static_cast<size_t>(std::strtoul(headers.c_str() + lengthAt + 15, nullptr, 10));
            if (bytes.size() >= headerEnd + 4 + bodyLength) return true;
        }
    }
    return false;
}

static void SendHttpResponse(SOCKET socket, const std::string& body, bool slow) {
    const std::string headers = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " +
        std::to_string(body.size()) + "\r\nConnection: close\r\n\r\n";
    if (!SendAll(socket, headers.data(), headers.size())) return;
    if (!slow) { SendAll(socket, body.data(), body.size()); return; }
    for (char ch : body) {
        fd_set readSet;
        FD_ZERO(&readSet);
        FD_SET(socket, &readSet);
        timeval wait{ 0, 0 };
        if (select(0, &readSet, nullptr, nullptr, &wait) > 0) {
            char peek = 0;
            if (recv(socket, &peek, 1, MSG_PEEK) == 0) return;
        }
        if (!SendAll(socket, &ch, 1)) return;
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
}

class FakeMetadataServer final {
public:
    explicit FakeMetadataServer(FakeServerMode mode) : mode_(mode) {
        listener_ = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (listener_ == INVALID_SOCKET) return;
        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        address.sin_port = 0;
        if (bind(listener_, reinterpret_cast<sockaddr*>(&address), sizeof(address)) != 0 || listen(listener_, 4) != 0) {
            closesocket(listener_);
            listener_ = INVALID_SOCKET;
            return;
        }
        int addressSize = sizeof(address);
        if (getsockname(listener_, reinterpret_cast<sockaddr*>(&address), &addressSize) != 0) {
            closesocket(listener_);
            listener_ = INVALID_SOCKET;
            return;
        }
        port_ = ntohs(address.sin_port);
        thread_ = std::thread([this] { Serve(); });
    }

    ~FakeMetadataServer() {
        if (listener_ != INVALID_SOCKET) { shutdown(listener_, SD_BOTH); closesocket(listener_); }
        if (thread_.joinable()) thread_.join();
    }

    bool Valid() const { return listener_ != INVALID_SOCKET && port_ != 0; }
    uint16_t Port() const { return port_; }
    int RouteCount() const { return routes_.load(); }
    bool SawAllRoutes() const { return routes_.load() == 3; }

private:
    void Serve() {
        for (int requestIndex = 0; requestIndex < 3; ++requestIndex) {
            SOCKET client = accept(listener_, nullptr, nullptr);
            if (client == INVALID_SOCKET) return;
            std::string route;
            if (!ReadHttpRequest(client, route)) { closesocket(client); return; }
            ++routes_;
            if (route == "/enter_orchestration") {
                SendHttpResponse(client, "{\"status\":\"Completion\",\"payload\":\"test-token\"}", false);
            } else if (route == "/available_output_devices") {
                if (mode_ == FakeServerMode::AvailableFailure)
                    SendHttpResponse(client, "{\"status\":\"Error\"}", false);
                else
                    SendHttpResponse(client, Response(), mode_ == FakeServerMode::SlowDrip);
            } else if (route == "/exit_orchestration") {
                const bool fail = mode_ == FakeServerMode::ExitFailure;
                SendHttpResponse(client, fail ? "{\"status\":\"Error\"}" : "{\"status\":\"Completion\"}", false);
            } else {
                SendHttpResponse(client, "{\"status\":\"Error\"}", false);
            }
            shutdown(client, SD_BOTH);
            closesocket(client);
        }
    }

    FakeServerMode mode_;
    SOCKET listener_ = INVALID_SOCKET;
    uint16_t port_ = 0;
    std::atomic<int> routes_{ 0 };
    std::thread thread_;
};

static void CheckLoopbackRead(FakeServerMode mode, uint32_t timeoutMilliseconds, bool expectedResult,
    const char* label) {
    FakeMetadataServer server(mode);
    Check(server.Valid(), "loopback metadata server starts");
    const LkgWorkerSubpixelCalibrationV1 expected = Expected();
    LkgWorkerSubpixelCellV1 cells[4]{};
    std::string error;
    const auto start = std::chrono::steady_clock::now();
    const bool result = lkg::subpixel::ReadBridgeCellTable(expected, cells, error, server.Port(), timeoutMilliseconds);
    const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - start).count();
    if (result != expectedResult) {
        std::fprintf(stderr, "DIAG loopback mode=%u expected=%d actual=%d elapsed_ms=%lld routes=%d reason=",
            static_cast<unsigned>(mode), expectedResult ? 1 : 0, result ? 1 : 0,
            static_cast<long long>(elapsed), server.RouteCount());
        for (unsigned char ch : error)
            std::fputc(ch >= 32 && ch <= 126 ? ch : '?', stderr);
        std::fputc('\n', stderr);
    }
    Check(result == expectedResult, label);
    Check(server.SawAllRoutes(), "metadata reader enters and exits orchestration on every data outcome");
    if (mode == FakeServerMode::SlowDrip) {
        Check(!result && elapsed < 1500, "slow-drip response is bounded by the overall request deadline");
        Check(error.find("deadline") != std::string::npos || error.find("read") != std::string::npos,
            "slow-drip failure preserves an ASCII transport reason");
    }
}

int main() {
    WSADATA winsock{};
    Check(WSAStartup(MAKEWORD(2, 2), &winsock) == 0, "Winsock initializes for loopback tests");
    const LkgWorkerSubpixelCalibrationV1 expected = Expected();
    LkgWorkerSubpixelCellV1 cells[4]{};
    std::string error;
    std::string json = Response();
    Check(lkg::subpixel::ParseBridgeCellResponse(json.data(), json.size(), expected, cells, error),
        "matching metadata is accepted");
    Check(std::abs(cells[0].redX - 0.1f) < 1e-6f && std::abs(cells[3].blueY + 0.06f) < 1e-6f,
        "cell offsets are copied exactly");

    std::string mismatch = json;
    const size_t coordinate = mismatch.find("1920");
    Check(coordinate != std::string::npos, "coordinate fixture exists");
    mismatch.replace(coordinate, 4, "1921");
    Check(!lkg::subpixel::ParseBridgeCellResponse(mismatch.data(), mismatch.size(), expected, cells, error),
        "mismatched display coordinates are rejected");

    std::string oversizedIndex = json;
    const size_t index = oversizedIndex.find("\"index\":3");
    Check(index != std::string::npos, "display index fixture exists");
    oversizedIndex.replace(index, std::strlen("\"index\":3"), "\"index\":9223372036854775808");
    Check(!lkg::subpixel::ParseBridgeCellResponse(oversizedIndex.data(), oversizedIndex.size(), expected, cells, error),
        "positive 2^63 integer is rejected without out-of-range conversion");

    const std::string duplicate = R"({"status":"Completion","status":"Completion"})";
    Check(!lkg::subpixel::ParseBridgeCellResponse(duplicate.data(), duplicate.size(), expected, cells, error),
        "duplicate JSON keys are rejected");
    const std::string nanValue = R"({"status":"Completion","payload":{"display":{"index":3,"windowCoords":{"x":1920,"y":0},"calibration":{"screenW":2560,"screenH":1600,"center":NaN}}}})";
    Check(!lkg::subpixel::ParseBridgeCellResponse(nanValue.data(), nanValue.size(), expected, cells, error),
        "non-JSON non-finite numbers are rejected");
    const std::string truncated = R"({"status":"Completion","payload":)";
    Check(!lkg::subpixel::ParseBridgeCellResponse(truncated.data(), truncated.size(), expected, cells, error),
        "truncated JSON is rejected");
    Check(!lkg::subpixel::ParseBridgeCellResponse(json.data(), lkg::subpixel::kMaximumMetadataBytes + 1,
        expected, cells, error), "oversized metadata is rejected before parsing");
    CheckLoopbackRead(FakeServerMode::Success, 1000, true, "loopback metadata success path completes");
    CheckLoopbackRead(FakeServerMode::AvailableFailure, 1000, false, "loopback data failure is reported");
    CheckLoopbackRead(FakeServerMode::ExitFailure, 1000, false, "loopback orchestration exit failure is reported");
    CheckLoopbackRead(FakeServerMode::SlowDrip, 300, false, "loopback slow-drip failure is reported");
    WSACleanup();
    std::puts("bridge_subpixel_metadata_test pass");
    return 0;
}
