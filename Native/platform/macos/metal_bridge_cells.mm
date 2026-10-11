#import <Foundation/Foundation.h>
#include "metal_bridge_cells.h"
#include "metal_calibration.h"
#include <cmath>
#include <limits>

namespace {
constexpr size_t MaxResponseBytes = 1024 * 1024;
}

@interface LkgCellMetadataReceiver : NSObject <NSURLSessionDataDelegate>
{
@public
    NSMutableData* body;
    NSString* failure;
    dispatch_semaphore_t done;
}
@end
@implementation LkgCellMetadataReceiver
- (id)init {
    if ((self = [super init])) { body = [[NSMutableData alloc] init]; done = dispatch_semaphore_create(0); }
    return self;
}
- (void)dealloc { [body release]; [failure release]; dispatch_release(done); [super dealloc]; }
- (void)URLSession:(NSURLSession*)session task:(NSURLSessionTask*)task
    willPerformHTTPRedirection:(NSHTTPURLResponse*)response newRequest:(NSURLRequest*)request
    completionHandler:(void (^)(NSURLRequest*))completion { completion(nil); }
- (void)URLSession:(NSURLSession*)session dataTask:(NSURLSessionDataTask*)task
    didReceiveResponse:(NSURLResponse*)response completionHandler:(void (^)(NSURLSessionResponseDisposition))completion {
    if (![response isKindOfClass:[NSHTTPURLResponse class]] || [(NSHTTPURLResponse*)response statusCode] != 200 ||
        response.expectedContentLength > static_cast<int64_t>(MaxResponseBytes)) {
        failure = [@"Bridge metadata HTTP response was rejected" copy]; completion(NSURLSessionResponseCancel);
    } else completion(NSURLSessionResponseAllow);
}
- (void)URLSession:(NSURLSession*)session dataTask:(NSURLSessionDataTask*)task didReceiveData:(NSData*)data {
    if (data.length > MaxResponseBytes - body.length) {
        if (!failure) failure = [@"Bridge metadata JSON exceeds the size limit" copy]; [task cancel];
    } else [body appendData:data];
}
- (void)URLSession:(NSURLSession*)session task:(NSURLSessionTask*)task didCompleteWithError:(NSError*)error {
    if (error && !failure) failure = [@"Bridge metadata HTTP request failed" copy];
    dispatch_semaphore_signal(done);
}
@end

namespace {
id Unwrap(id value) {
    return [value isKindOfClass:[NSDictionary class]] ? [(NSDictionary*)value objectForKey:@"value"] : value;
}
bool Number(id value, double& result) {
    value = Unwrap(value);
    if (![value isKindOfClass:[NSNumber class]] || CFGetTypeID((CFTypeRef)value) == CFBooleanGetTypeID()) return false;
    result = [value doubleValue];
    return std::isfinite(result);
}
bool Same(NSDictionary* object, NSString* key, float expected) {
    double number = 0;
    return Number(object[key],number) && std::abs(number) <= std::numeric_limits<float>::max() &&
        static_cast<float>(number) == expected;
}
NSDictionary* Decode(NSData* data) {
    if (!data || data.length > MaxResponseBytes) return nil;
    id value = [NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
    return [value isKindOfClass:[NSDictionary class]] ? value : nil;
}
NSDictionary* Put(NSString* endpoint, NSDictionary* body, uint16_t port, std::string& error) {
    NSData* requestBody = [NSJSONSerialization dataWithJSONObject:body options:0 error:nil];
    NSMutableURLRequest* request = [NSMutableURLRequest requestWithURL:
        [NSURL URLWithString:[[NSString stringWithFormat:@"http://127.0.0.1:%u/",port] stringByAppendingString:endpoint]]];
    request.HTTPMethod = @"PUT"; request.HTTPBody = requestBody; request.timeoutInterval = 3;
    [request setValue:@"application/json" forHTTPHeaderField:@"Content-Type"];
    NSURLSessionConfiguration* config = [NSURLSessionConfiguration ephemeralSessionConfiguration];
    config.connectionProxyDictionary = @{};
    config.timeoutIntervalForResource = 4;
    NSOperationQueue* queue = [[NSOperationQueue alloc] init]; queue.maxConcurrentOperationCount = 1;
    LkgCellMetadataReceiver* receiver = [[LkgCellMetadataReceiver alloc] init];
    NSURLSession* session = [NSURLSession sessionWithConfiguration:config delegate:receiver delegateQueue:queue];
    NSURLSessionDataTask* task = [session dataTaskWithRequest:request];
    [task resume];
    bool finished = dispatch_semaphore_wait(receiver->done,dispatch_time(DISPATCH_TIME_NOW,5 * NSEC_PER_SEC)) == 0;
    NSDictionary* result = finished && !receiver->failure ? Decode(receiver->body) : nil;
    if (!finished) error = "Bridge metadata HTTP request timed out";
    else if (receiver->failure) error = receiver->failure.UTF8String;
    else if (!result || ![result[@"status"] isEqual:@"Completion"])
        error = "Bridge metadata JSON response is invalid or incomplete";
    [session invalidateAndCancel]; [receiver release]; [queue release];
    return error.empty() ? result : nil;
}
bool Parse(NSDictionary* response, LkgMetalDisplayInfoV3& profile, long x, long y, std::string& error) {
    if (!response || ![response[@"status"] isEqual:@"Completion"]) {
        error = "Bridge cell response is invalid or incomplete"; return false;
    }
    id payload = response[@"payload"];
    id displays = Unwrap(payload);
    if (![displays isKindOfClass:[NSDictionary class]] || [displays count] > 256) {
        error = "Bridge cell response has an invalid display map"; return false;
    }
    NSDictionary* selected = nil;
    for (id wrapped in [(NSDictionary*)displays allValues]) {
        id display = Unwrap(wrapped); double index = -1;
        if (![display isKindOfClass:[NSDictionary class]] || !Number(display[@"index"],index) ||
            index != profile.calibration.display.index) continue;
        if (selected) { error = "Bridge cell response has duplicate display indices"; return false; }
        selected = display;
    }
    id rawCalibration = Unwrap(selected[@"calibration"]);
    NSDictionary* calibration = [rawCalibration isKindOfClass:[NSString class]] ?
        Decode([(NSString*)rawCalibration dataUsingEncoding:NSUTF8StringEncoding]) :
        [rawCalibration isKindOfClass:[NSDictionary class]] ? rawCalibration : nil;
    id coordinates = Unwrap(selected[@"windowCoords"]);
    double actualX = 0, actualY = 0;
    const auto& c = profile.calibration;
    bool same = calibration && [coordinates isKindOfClass:[NSDictionary class]] &&
        Number(coordinates[@"x"],actualX) && Number(coordinates[@"y"],actualY) && actualX == x && actualY == y &&
        Same(calibration,@"screenW",c.display.width) && Same(calibration,@"screenH",c.display.height) &&
        Same(calibration,@"center",c.center) && Same(calibration,@"pitch",c.pitch) && Same(calibration,@"slope",c.slope) &&
        Same(calibration,@"DPI",c.dpi) && Same(calibration,@"flipImageX",c.flipX) &&
        Same(calibration,@"invView",c.invertedViews) && Same(calibration,@"viewCone",c.display.viewcone);
    // Both spellings occur in vendor APIs. Any supplied value must match the SDK.
    bool hasMode = false;
    for (NSString* key in @[@"CellPatternMode",@"cellPatternMode"]) {
        if (!calibration[key]) continue;
        hasMode = true;
        double mode = -1;
        same = same && Number(calibration[key],mode) && mode == profile.cellPatternMode;
    }
    if (!hasMode) same = same && profile.cellPatternMode == 0 && profile.cellCount == 0;
    id cells = calibration[@"subpixelCells"];
    if (!cells && profile.cellCount == 0) cells = @[];
    if (!same || ![cells isKindOfClass:[NSArray class]] || [cells count] != profile.cellCount) {
        error = "Bridge cell JSON does not match the selected native calibration"; return false;
    }
    if (const char* invalid = lkg::metal::ValidateCellPattern(profile.cellPatternMode,profile.cellCount)) {
        error = invalid; return false;
    }
    LkgMetalSubpixelCell parsed[4]{};
    for (NSUInteger i = 0; i < [cells count]; ++i) {
        id cell = cells[i];
        if (![cell isKindOfClass:[NSDictionary class]]) { error = "Bridge subpixel cell must be an object"; return false; }
        const NSArray* keys = @[@"ROffsetX",@"ROffsetY",@"GOffsetX",@"GOffsetY",@"BOffsetX",@"BOffsetY"];
        float values[6]{};
        for (NSUInteger field = 0; field < 6; ++field) {
            double value = 0;
            if (!Number(cell[keys[field]],value) || std::abs(value) > std::numeric_limits<float>::max()) {
                error = "Bridge subpixel cell offset is missing, nonnumeric or out of range"; return false;
            }
            values[field] = static_cast<float>(value);
        }
        parsed[i] = {values[0],values[1],values[2],values[3],values[4],values[5]};
    }
    if (const char* invalid = lkg::metal::ValidateCalibration(c,profile.cellPatternMode,profile.cellCount,
        0,profile.redIndex,profile.blueIndex,parsed)) { error = invalid; return false; }
    for (int i = 0; i < 4; ++i) profile.cells[i] = parsed[i];
    return true;
}
}

namespace lkg::metal {
bool ParseBridgeCellResponse(const char* json, size_t bytes, LkgMetalDisplayInfoV3& profile,
    long x, long y, std::string& error) {
    @autoreleasepool {
        error.clear();
        if (!json || bytes > MaxResponseBytes) { error = "Bridge cell JSON exceeds the size limit or is missing"; return false; }
        return Parse(Decode([NSData dataWithBytes:json length:bytes]),profile,x,y,error);
    }
}
bool ReadBridgeCellTable(LkgMetalDisplayInfoV3& profile, long x, long y, std::string& error, uint16_t port) {
    @autoreleasepool {
        error.clear();
        NSString* name = [@"ViewWeave Metal metadata " stringByAppendingString:NSUUID.UUID.UUIDString];
        NSDictionary* entered = Put(@"enter_orchestration",@{@"name":name},port,error);
        id token = Unwrap(entered[@"payload"]);
        if (![token isKindOfClass:[NSString class]] || [token length] == 0 || [token length] > 512) {
            if (error.empty()) error = "Bridge did not return a valid metadata orchestration";
            return false;
        }
        bool success = false;
        @try {
            NSDictionary* response = Put(@"available_output_devices",@{@"orchestration":token},port,error);
            if (response) success = Parse(response,profile,x,y,error);
        } @finally {
            std::string exitError;
            if (!Put(@"exit_orchestration",@{@"orchestration":token},port,exitError)) {
                success = false; error = "Bridge metadata orchestration cleanup failed";
            }
        }
        return success;
    }
}
}
