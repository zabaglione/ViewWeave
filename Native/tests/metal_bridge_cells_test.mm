#import <Foundation/Foundation.h>
#include "platform/macos/metal_bridge_cells.h"
#include <iostream>
#include <stdexcept>
#include <cstring>
#include <algorithm>
#include <atomic>
#include <cerrno>
#include <cctype>
#include <cmath>
#include <functional>
#include <sstream>
#include <string>
#include <thread>
#include <utility>
#include <vector>
#include <poll.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <arpa/inet.h>
#include <netinet/in.h>
#include <unistd.h>

namespace {
void Require(bool condition, const char* error) { if (!condition) throw std::runtime_error(error); }
LkgMetalDisplayInfoV3 Profile(int mode, int count, int flip = 0) {
    LkgMetalDisplayInfoV3 p{}; p.structSize = sizeof(p); p.apiVersion = 3;
    p.calibration.display = {7,17,23,17.f/23,40,4096,4096,8,6};
    p.calibration.center = 0.17f; p.calibration.pitch = 45; p.calibration.slope = -7.5f;
    p.calibration.dpi = 300; p.calibration.flipX = flip; p.calibration.invertedViews = 1;
    p.redIndex = 0; p.blueIndex = 2; p.cellPatternMode = mode; p.cellCount = count;
    return p;
}
NSDictionary* Wrap(id value) { return @{@"value":value}; }
NSMutableDictionary* Calibration(const LkgMetalDisplayInfoV3& p) {
    const auto& c = p.calibration;
    NSMutableArray* cells = [NSMutableArray array];
    for (int i = 0; i < std::min(4,p.cellCount); ++i)
        [cells addObject:@{@"ROffsetX":@(-0.31f+i*0.01f),@"ROffsetY":@(-0.28f+i*0.02f),
            @"GOffsetX":@(0.28f),@"GOffsetY":@(0.19f),@"BOffsetX":@(0.05f),@"BOffsetY":@(0.29f)}];
    return [@{@"screenW":Wrap(@(c.display.width)),@"screenH":Wrap(@(c.display.height)),
        @"center":Wrap(@(c.center)),@"pitch":Wrap(@(c.pitch)),@"slope":Wrap(@(c.slope)),@"DPI":Wrap(@(c.dpi)),
        @"flipImageX":Wrap(@(c.flipX)),@"invView":Wrap(@(c.invertedViews)),@"viewCone":Wrap(@(c.display.viewcone)),
        @"CellPatternMode":Wrap(@(p.cellPatternMode)),@"subpixelCells":cells} mutableCopy];
}
std::string Response(const LkgMetalDisplayInfoV3& p, NSDictionary* calibration, bool stringForm = false,
    bool duplicate = false) {
    id value = calibration;
    if (stringForm) {
        NSData* data = [NSJSONSerialization dataWithJSONObject:calibration options:0 error:nil];
        value = [[[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding] autorelease];
    }
    NSDictionary* display = Wrap(@{@"index":Wrap(@(p.calibration.display.index)),
        @"windowCoords":Wrap(@{@"x":@123456,@"y":@-654321}),@"calibration":Wrap(value)});
    NSDictionary* devices = duplicate ? @{@"first":display,@"duplicate":display} : @{@"first":display};
    NSData* data = [NSJSONSerialization dataWithJSONObject:@{@"status":@"Completion",@"payload":Wrap(devices)}
        options:0 error:nil];
    return std::string(static_cast<const char*>(data.bytes),data.length);
}
struct HttpRequest {
    std::string method;
    std::string path;
    std::string headers;
    std::string body;
};
struct HttpReply { int status; std::string body; };
std::string Lower(std::string value) {
    std::transform(value.begin(),value.end(),value.begin(),[](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return value;
}
bool ReadRequest(int socket, HttpRequest& request, std::string& error) {
    constexpr size_t MaxRequestBytes = 16 * 1024;
    std::string bytes;
    size_t headerEnd = std::string::npos;
    while (headerEnd == std::string::npos) {
        char chunk[2048]; ssize_t count = recv(socket,chunk,sizeof(chunk),0);
        if (count <= 0) { error = "HTTP request header read failed"; return false; }
        bytes.append(chunk,static_cast<size_t>(count));
        if (bytes.size() > MaxRequestBytes) { error = "HTTP request exceeded test limit"; return false; }
        headerEnd = bytes.find("\r\n\r\n");
    }
    request.headers = bytes.substr(0,headerEnd);
    std::istringstream headerStream(request.headers);
    std::string version;
    if (!(headerStream >> request.method >> request.path >> version)) { error = "HTTP request line was invalid"; return false; }
    std::string lowerHeaders = Lower(request.headers);
    size_t lengthAt = lowerHeaders.find("\r\ncontent-length:");
    if (lengthAt == std::string::npos) { error = "HTTP request lacked content length"; return false; }
    lengthAt += std::strlen("\r\ncontent-length:");
    while (lengthAt < lowerHeaders.size() && std::isspace(static_cast<unsigned char>(lowerHeaders[lengthAt]))) ++lengthAt;
    size_t lengthEnd = lowerHeaders.find("\r\n",lengthAt);
    std::string lengthText = lowerHeaders.substr(lengthAt,lengthEnd == std::string::npos ? std::string::npos : lengthEnd-lengthAt);
    size_t bodyLength = 0;
    try { bodyLength = static_cast<size_t>(std::stoull(lengthText)); }
    catch (...) { error = "HTTP request content length was invalid"; return false; }
    if (bodyLength > MaxRequestBytes) { error = "HTTP request body exceeded test limit"; return false; }
    size_t bodyStart = headerEnd + 4;
    while (bytes.size() < bodyStart + bodyLength) {
        char chunk[2048]; ssize_t count = recv(socket,chunk,sizeof(chunk),0);
        if (count <= 0) { error = "HTTP request body read failed"; return false; }
        bytes.append(chunk,static_cast<size_t>(count));
        if (bytes.size() > MaxRequestBytes) { error = "HTTP request exceeded test limit"; return false; }
    }
    request.body = bytes.substr(bodyStart,bodyLength);
    return true;
}
bool SendAll(int socket, const char* data, size_t size) {
    size_t sent = 0;
    while (sent < size) {
        ssize_t count = send(socket,data+sent,size-sent,0);
        if (count < 0 && errno == EINTR) continue;
        if (count <= 0) return false;
        sent += static_cast<size_t>(count);
    }
    return true;
}
void SendReply(int socket, const HttpReply& reply) {
    const char* reason = reply.status == 200 ? "OK" : reply.status == 503 ? "Service Unavailable" : "Not Found";
    std::string header = "HTTP/1.1 " + std::to_string(reply.status) + " " + reason +
        "\r\nContent-Type: application/json\r\nContent-Length: " + std::to_string(reply.body.size()) +
        "\r\nConnection: close\r\n\r\n";
    if (!SendAll(socket,header.data(),header.size())) return;
    (void)SendAll(socket,reply.body.data(),reply.body.size());
}
class LoopbackHttpServer {
public:
    using Handler = std::function<HttpReply(const HttpRequest&)>;
    LoopbackHttpServer(size_t requestCount, Handler handler) : expectedRequests(requestCount), handler(std::move(handler)) {
        listener = ::socket(AF_INET,SOCK_STREAM,0);
        if (listener < 0) throw std::runtime_error("Unable to create test HTTP socket");
        sockaddr_in address{}; address.sin_family = AF_INET; address.sin_addr.s_addr = htonl(INADDR_LOOPBACK); address.sin_port = 0;
        if (bind(listener,reinterpret_cast<sockaddr*>(&address),sizeof(address)) != 0 || listen(listener,4) != 0) {
            close(listener); listener = -1; throw std::runtime_error("Unable to bind test HTTP server");
        }
        socklen_t addressLength = sizeof(address);
        if (getsockname(listener,reinterpret_cast<sockaddr*>(&address),&addressLength) != 0) {
            close(listener); listener = -1; throw std::runtime_error("Unable to read test HTTP port");
        }
        port = ntohs(address.sin_port);
        worker = std::thread([this] { Run(); });
    }
    ~LoopbackHttpServer() {
        stopping = true;
        if (listener >= 0) { shutdown(listener,SHUT_RDWR); close(listener); listener = -1; }
        Join();
    }
    uint16_t Port() const { return port; }
    void Join() { if (worker.joinable()) worker.join(); }
    const std::vector<HttpRequest>& Requests() const { return requests; }
    const std::string& Error() const { return serverError; }
private:
    void Run() {
        for (size_t i = 0; i < expectedRequests && !stopping; ++i) {
            pollfd readiness{listener,POLLIN,0};
            int ready = poll(&readiness,1,10000);
            if (ready <= 0) { if (!stopping) serverError = "Timed out waiting for HTTP request"; return; }
            int client = accept(listener,nullptr,nullptr);
            if (client < 0) { if (!stopping) serverError = "Unable to accept test HTTP request"; return; }
#ifdef SO_NOSIGPIPE
            int noPipe = 1; setsockopt(client,SOL_SOCKET,SO_NOSIGPIPE,&noPipe,sizeof(noPipe));
#endif
            timeval timeout{4,0};
            setsockopt(client,SOL_SOCKET,SO_RCVTIMEO,&timeout,sizeof(timeout));
            setsockopt(client,SOL_SOCKET,SO_SNDTIMEO,&timeout,sizeof(timeout));
            HttpRequest request;
            if (!ReadRequest(client,request,serverError)) { close(client); return; }
            requests.push_back(request);
            try { SendReply(client,handler(request)); }
            catch (...) { serverError = "HTTP test handler failed"; close(client); return; }
            shutdown(client,SHUT_RDWR); close(client);
        }
    }
    int listener = -1;
    uint16_t port = 0;
    size_t expectedRequests;
    Handler handler;
    std::atomic<bool> stopping{false};
    std::thread worker;
    std::vector<HttpRequest> requests;
    std::string serverError;
};
std::string EnterResponse() { return R"({"status":"Completion","payload":{"value":"fixture-orchestration-token"}})"; }
std::string ExitResponse() { return R"({"status":"Completion","payload":{"value":"fixture-orchestration-token"}})"; }
enum class HttpScenario { Success, RetrievalFailure, InvalidResponse, OversizedResponse, CleanupFailure };
LoopbackHttpServer::Handler ScenarioHandler(HttpScenario scenario, std::string calibrationResponse) {
    return [scenario,calibrationResponse=std::move(calibrationResponse)](const HttpRequest& request) -> HttpReply {
        if (request.method != "PUT") return {404,R"({"status":"Failure"})"};
        if (request.path == "/enter_orchestration") return {200,EnterResponse()};
        if (request.path == "/available_output_devices") {
            if (scenario == HttpScenario::RetrievalFailure) return {503,R"({"status":"Failure"})"};
            if (scenario == HttpScenario::InvalidResponse) return {200,"{invalid-json"};
            if (scenario == HttpScenario::OversizedResponse) return {200,std::string(1024*1024+1,'x')};
            return {200,calibrationResponse};
        }
        if (request.path == "/exit_orchestration") {
            if (scenario == HttpScenario::CleanupFailure) return {503,R"({"status":"Failure"})"};
            return {200,ExitResponse()};
        }
        return {404,R"({"status":"Failure"})"};
    };
}
NSDictionary* RequestJson(const HttpRequest& request) {
    NSData* data = [NSData dataWithBytes:request.body.data() length:request.body.size()];
    id value = [NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
    return [value isKindOfClass:[NSDictionary class]] ? value : nil;
}
void CheckHttpProtocol(LoopbackHttpServer& server) {
    server.Join();
    Require(server.Error().empty(),"Loopback HTTP server failed");
    const auto& requests = server.Requests();
    Require(requests.size() == 3,"Expected enter, device query and exit requests");
    const char* paths[] = {"/enter_orchestration","/available_output_devices","/exit_orchestration"};
    for (size_t i = 0; i < requests.size(); ++i) {
        Require(requests[i].method == "PUT" && requests[i].path == paths[i],"Unexpected Bridge HTTP method or endpoint order");
        Require(Lower(requests[i].headers).find("content-type: application/json") != std::string::npos,
            "Bridge HTTP request omitted JSON content type");
    }
    NSDictionary* enter = RequestJson(requests[0]);
    NSDictionary* available = RequestJson(requests[1]);
    NSDictionary* exit = RequestJson(requests[2]);
    NSString* name = enter[@"name"];
    NSString* queryToken = available[@"orchestration"];
    NSString* cleanupToken = exit[@"orchestration"];
    Require([name isKindOfClass:[NSString class]] && [name hasPrefix:@"ViewWeave Metal metadata "],
        "Temporary orchestration name was not sent");
    Require([name canBeConvertedToEncoding:NSASCIIStringEncoding],"Temporary orchestration name was not ASCII");
    Require([queryToken isEqualToString:@"fixture-orchestration-token"],"Device query did not use returned orchestration token");
    Require([cleanupToken isEqualToString:queryToken],"Orchestration cleanup did not reuse the returned token");
}
bool SameCell(const LkgMetalSubpixelCell& a, const LkgMetalSubpixelCell& b) {
    return a.redX==b.redX && a.redY==b.redY && a.greenX==b.greenX && a.greenY==b.greenY &&
        a.blueX==b.blueX && a.blueY==b.blueY;
}
void SeedCells(LkgMetalDisplayInfoV3& p) {
    p.cells[0] = {91,92,93,94,95,96}; p.cells[1] = {-91,-92,-93,-94,-95,-96};
}
void TestHttpScenario(HttpScenario scenario) {
    auto p = Profile(3,4); SeedCells(p); LkgMetalSubpixelCell before[4]; std::copy(p.cells,p.cells+4,before);
    auto cal = Calibration(p); std::string body = Response(p,cal,true); [cal release];
    LoopbackHttpServer server(3,ScenarioHandler(scenario,std::move(body)));
    std::string error;
    bool result = lkg::metal::ReadBridgeCellTable(p,123456,-654321,error,server.Port());
    CheckHttpProtocol(server);
    if (scenario == HttpScenario::Success) {
        if (!result) std::cerr << "Bridge HTTP read error: " << error << '\n';
        Require(result && error.empty(),"Bridge HTTP read failed for valid response");
        Require(p.cellPatternMode == 3 && p.cellCount == 4,"Bridge HTTP read changed profile mode or count");
        for (int i = 0; i < 4; ++i)
            Require(std::abs(p.cells[i].redX - (-0.31f+i*0.01f)) < 1e-6f && p.cells[i].greenY == 0.19f &&
                p.cells[i].blueY == 0.29f,"Bridge HTTP read did not copy the expected cell table");
    } else {
        Require(!result && !error.empty(),"Bridge HTTP read unexpectedly accepted a failed response");
        Require(p.cellPatternMode == 3 && p.cellCount == 4,"Failed Bridge HTTP read modified profile mode or count");
        if (scenario == HttpScenario::CleanupFailure) {
            Require(error.find("cleanup failed") != std::string::npos,"Cleanup failure was not reported");
            for (int i = 0; i < 4; ++i)
                Require(std::abs(p.cells[i].redX - (-0.31f+i*0.01f)) < 1e-6f && p.cells[i].greenY == 0.19f,
                    "Cleanup failure left a partial cell table");
        } else {
            for (int i = 0; i < 4; ++i) Require(SameCell(p.cells[i],before[i]),"Failed Bridge HTTP read modified caller cells");
        }
    }
}
bool Parse(const std::string& json, LkgMetalDisplayInfoV3& p, long x = 123456) {
    std::string error;
    return lkg::metal::ParseBridgeCellResponse(json.data(),json.size(),p,x,-654321,error);
}
}
int main() {
    @autoreleasepool {
        try {
            TestHttpScenario(HttpScenario::Success);
            TestHttpScenario(HttpScenario::RetrievalFailure);
            TestHttpScenario(HttpScenario::InvalidResponse);
            TestHttpScenario(HttpScenario::OversizedResponse);
            TestHttpScenario(HttpScenario::CleanupFailure);
            for (int mode = 0; mode <= 4; ++mode) for (bool stringForm : {false,true}) for (int flip : {0,1}) {
                int count = mode == 0 ? 1 : mode == 3 ? 4 : 2;
                auto p = Profile(mode,count,flip);
                auto cal = Calibration(p);
                auto json = Response(p,cal,stringForm); [cal release];
                Require(Parse(json,p),"Valid Bridge cell JSON rejected");
                for (int i = 0; i < count; ++i)
                    Require(std::abs(p.cells[i].redX - (-0.31f+i*0.01f)) < 1e-6f && p.cells[i].greenY == 0.19f,
                        "Raw pixel offsets were changed or reordered");
            }
            auto p = Profile(2,2); auto cal = Calibration(p); auto good = Response(p,cal);
            NSMutableDictionary* alias = [cal mutableCopy];
            alias[@"cellPatternMode"] = alias[@"CellPatternMode"]; [alias removeObjectForKey:@"CellPatternMode"];
            Require(Parse(Response(p,alias),p),"Lowercase mode alias rejected");
            alias[@"CellPatternMode"] = @3;
            Require(!Parse(Response(p,alias),p),"Conflicting mode spellings accepted");
            [alias removeObjectForKey:@"CellPatternMode"]; [alias removeObjectForKey:@"cellPatternMode"];
            Require(!Parse(Response(p,alias),p),"Missing mode accepted for a cell table"); [alias release];
            Require(!Parse(good,p,0),"Mismatched display position accepted");
            Require(!Parse(Response(p,cal,false,true),p),"Duplicate native display index accepted");
            auto changed = p; changed.calibration.pitch += 1;
            Require(!Parse(good,changed),"Mismatched native calibration accepted");
            auto tooFew = Profile(3,3); auto tooFewCal = Calibration(tooFew);
            Require(!Parse(Response(tooFew,tooFewCal),tooFew),"Too few addressable cells accepted"); [tooFewCal release];
            auto unknown = Profile(9,2); auto unknownCal = Calibration(unknown);
            Require(!Parse(Response(unknown,unknownCal),unknown),"Unknown mode accepted"); [unknownCal release];
            auto oversized = Profile(0,5); auto oversizedCal = Calibration(oversized);
            Require(!Parse(Response(oversized,oversizedCal),oversized),"Oversized cell table accepted"); [oversizedCal release];
            NSMutableDictionary* first = [cal[@"subpixelCells"][0] mutableCopy]; [first removeObjectForKey:@"GOffsetY"];
            cal[@"subpixelCells"] = @[first,cal[@"subpixelCells"][1]]; [first release];
            p.cells[0].redX = 9;
            Require(!Parse(Response(p,cal),p) && p.cells[0].redX == 9,"Incomplete JSON modified the caller's cells");
            first = [cal[@"subpixelCells"][0] mutableCopy]; first[@"GOffsetY"] = @YES;
            cal[@"subpixelCells"] = @[first,cal[@"subpixelCells"][1]]; [first release];
            Require(!Parse(Response(p,cal),p),"Boolean offset accepted as a number"); [cal release];
            std::string error;
            Require(!lkg::metal::ParseBridgeCellResponse("x",1024*1024+1,p,0,0,error),"Oversized body accepted");
            Require(!Parse("[]",p) && !Parse("{",p),"Invalid JSON accepted");
            auto classic = Profile(0,0); auto classicCal = Calibration(classic);
            classicCal[@"cellPatternMode"] = @3;
            Require(!Parse(Response(classic,classicCal),classic),"Mismatched lowercase mode accepted");
            [classicCal removeObjectForKey:@"cellPatternMode"];
            [classicCal removeObjectForKey:@"CellPatternMode"]; [classicCal removeObjectForKey:@"subpixelCells"];
            Require(Parse(Response(classic,classicCal,true),classic),"Legacy JSON omission with native zero mode/count rejected");
            auto nonzero = classic; nonzero.cellCount = 1;
            Require(!Parse(Response(classic,classicCal,true),nonzero),"Missing cells accepted for native nonzero count");
            [classicCal release];
            std::cout << "Bridge cell parser and HTTP tests passed: protocol, cleanup, response and size guards, 20 mode/format/flip cases\n";
        } catch (const std::exception& e) { std::cerr << e.what() << '\n'; return 1; }
    }
    return 0;
}
