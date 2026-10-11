#include "bridge_subpixel_metadata.h"
#include "../../common/subpixel_calibration.h"

#include <windows.h>
#include <winhttp.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <string_view>
#include <utility>
#include <vector>

namespace lkg::subpixel {
namespace {

enum class JsonKind { Null, Boolean, Number, String, Object, Array };
struct JsonValue {
    JsonKind kind = JsonKind::Null;
    bool boolean = false;
    std::string text;
    std::vector<std::pair<std::string, JsonValue>> object;
    std::vector<JsonValue> array;
};

class JsonParser final {
public:
    JsonParser(const char* bytes, size_t length) : begin_(bytes), cursor_(bytes), end_(bytes + length) {}

    bool Parse(JsonValue& result) {
        SkipSpace();
        if (!ReadValue(result, 0)) return false;
        SkipSpace();
        return cursor_ == end_;
    }

private:
    static constexpr size_t kMaximumNodes = 100000;
    static constexpr unsigned kMaximumDepth = 32;

    void SkipSpace() {
        while (cursor_ < end_ && (*cursor_ == ' ' || *cursor_ == '\t' || *cursor_ == '\r' || *cursor_ == '\n')) ++cursor_;
    }

    bool ReadValue(JsonValue& value, unsigned depth) {
        if (depth > kMaximumDepth || ++nodes_ > kMaximumNodes || cursor_ >= end_) return false;
        SkipSpace();
        if (cursor_ >= end_) return false;
        if (*cursor_ == '{') return ReadObject(value, depth + 1);
        if (*cursor_ == '[') return ReadArray(value, depth + 1);
        if (*cursor_ == '"') {
            value.kind = JsonKind::String;
            return ReadString(value.text);
        }
        if (*cursor_ == 't' && ReadLiteral("true")) { value.kind = JsonKind::Boolean; value.boolean = true; return true; }
        if (*cursor_ == 'f' && ReadLiteral("false")) { value.kind = JsonKind::Boolean; value.boolean = false; return true; }
        if (*cursor_ == 'n' && ReadLiteral("null")) { value.kind = JsonKind::Null; return true; }
        return ReadNumber(value);
    }

    bool ReadObject(JsonValue& value, unsigned depth) {
        ++cursor_;
        value.kind = JsonKind::Object;
        SkipSpace();
        if (cursor_ < end_ && *cursor_ == '}') { ++cursor_; return true; }
        for (;;) {
            SkipSpace();
            std::string key;
            if (cursor_ >= end_ || *cursor_ != '"' || !ReadString(key)) return false;
            for (const auto& entry : value.object) if (entry.first == key) return false;
            SkipSpace();
            if (cursor_ >= end_ || *cursor_++ != ':') return false;
            SkipSpace();
            JsonValue child;
            if (!ReadValue(child, depth)) return false;
            value.object.emplace_back(std::move(key), std::move(child));
            SkipSpace();
            if (cursor_ >= end_) return false;
            if (*cursor_ == '}') { ++cursor_; return true; }
            if (*cursor_++ != ',') return false;
        }
    }

    bool ReadArray(JsonValue& value, unsigned depth) {
        ++cursor_;
        value.kind = JsonKind::Array;
        SkipSpace();
        if (cursor_ < end_ && *cursor_ == ']') { ++cursor_; return true; }
        for (;;) {
            JsonValue child;
            if (!ReadValue(child, depth)) return false;
            value.array.emplace_back(std::move(child));
            SkipSpace();
            if (cursor_ >= end_) return false;
            if (*cursor_ == ']') { ++cursor_; return true; }
            if (*cursor_++ != ',') return false;
        }
    }

    static void AppendUtf8(std::string& output, uint32_t codepoint) {
        if (codepoint <= 0x7f) output.push_back(static_cast<char>(codepoint));
        else if (codepoint <= 0x7ff) {
            output.push_back(static_cast<char>(0xc0 | (codepoint >> 6)));
            output.push_back(static_cast<char>(0x80 | (codepoint & 0x3f)));
        } else if (codepoint <= 0xffff) {
            output.push_back(static_cast<char>(0xe0 | (codepoint >> 12)));
            output.push_back(static_cast<char>(0x80 | ((codepoint >> 6) & 0x3f)));
            output.push_back(static_cast<char>(0x80 | (codepoint & 0x3f)));
        } else {
            output.push_back(static_cast<char>(0xf0 | (codepoint >> 18)));
            output.push_back(static_cast<char>(0x80 | ((codepoint >> 12) & 0x3f)));
            output.push_back(static_cast<char>(0x80 | ((codepoint >> 6) & 0x3f)));
            output.push_back(static_cast<char>(0x80 | (codepoint & 0x3f)));
        }
    }

    bool ReadHex4(uint32_t& value) {
        if (end_ - cursor_ < 4) return false;
        value = 0;
        for (int i = 0; i < 4; ++i) {
            const char digit = *cursor_++;
            uint32_t nibble = 0;
            if (digit >= '0' && digit <= '9') nibble = static_cast<uint32_t>(digit - '0');
            else if (digit >= 'a' && digit <= 'f') nibble = static_cast<uint32_t>(digit - 'a' + 10);
            else if (digit >= 'A' && digit <= 'F') nibble = static_cast<uint32_t>(digit - 'A' + 10);
            else return false;
            value = (value << 4) | nibble;
        }
        return true;
    }

    bool ReadString(std::string& output) {
        if (cursor_ >= end_ || *cursor_++ != '"') return false;
        output.clear();
        while (cursor_ < end_) {
            const unsigned char ch = static_cast<unsigned char>(*cursor_++);
            if (ch == '"') return true;
            if (ch < 0x20) return false;
            if (ch != '\\') { output.push_back(static_cast<char>(ch)); continue; }
            if (cursor_ >= end_) return false;
            const char escaped = *cursor_++;
            switch (escaped) {
            case '"': output.push_back('"'); break;
            case '\\': output.push_back('\\'); break;
            case '/': output.push_back('/'); break;
            case 'b': output.push_back('\b'); break;
            case 'f': output.push_back('\f'); break;
            case 'n': output.push_back('\n'); break;
            case 'r': output.push_back('\r'); break;
            case 't': output.push_back('\t'); break;
            case 'u': {
                uint32_t codepoint = 0;
                if (!ReadHex4(codepoint)) return false;
                if (codepoint >= 0xd800 && codepoint <= 0xdbff) {
                    if (end_ - cursor_ < 6 || cursor_[0] != '\\' || cursor_[1] != 'u') return false;
                    cursor_ += 2;
                    uint32_t low = 0;
                    if (!ReadHex4(low) || low < 0xdc00 || low > 0xdfff) return false;
                    codepoint = 0x10000 + ((codepoint - 0xd800) << 10) + (low - 0xdc00);
                } else if (codepoint >= 0xdc00 && codepoint <= 0xdfff) return false;
                AppendUtf8(output, codepoint);
                break;
            }
            default: return false;
            }
        }
        return false;
    }

    bool ReadLiteral(const char* literal) {
        const size_t length = std::strlen(literal);
        if (static_cast<size_t>(end_ - cursor_) < length || std::memcmp(cursor_, literal, length) != 0) return false;
        cursor_ += length;
        return true;
    }

    bool ReadNumber(JsonValue& value) {
        const char* start = cursor_;
        if (cursor_ < end_ && *cursor_ == '-') ++cursor_;
        if (cursor_ >= end_) return false;
        if (*cursor_ == '0') ++cursor_;
        else {
            if (*cursor_ < '1' || *cursor_ > '9') return false;
            while (cursor_ < end_ && *cursor_ >= '0' && *cursor_ <= '9') ++cursor_;
        }
        if (cursor_ < end_ && *cursor_ == '.') {
            ++cursor_;
            if (cursor_ >= end_ || *cursor_ < '0' || *cursor_ > '9') return false;
            while (cursor_ < end_ && *cursor_ >= '0' && *cursor_ <= '9') ++cursor_;
        }
        if (cursor_ < end_ && (*cursor_ == 'e' || *cursor_ == 'E')) {
            ++cursor_;
            if (cursor_ < end_ && (*cursor_ == '+' || *cursor_ == '-')) ++cursor_;
            if (cursor_ >= end_ || *cursor_ < '0' || *cursor_ > '9') return false;
            while (cursor_ < end_ && *cursor_ >= '0' && *cursor_ <= '9') ++cursor_;
        }
        value.kind = JsonKind::Number;
        value.text.assign(start, cursor_);
        return true;
    }

    const char* begin_;
    const char* cursor_;
    const char* end_;
    size_t nodes_ = 0;
};

const JsonValue* Find(const JsonValue& value, std::string_view key) {
    if (value.kind != JsonKind::Object) return nullptr;
    for (const auto& entry : value.object) if (entry.first == key) return &entry.second;
    return nullptr;
}

const JsonValue* Unwrap(const JsonValue* value) {
    for (unsigned depth = 0; value && depth < 8; ++depth) {
        const JsonValue* nested = Find(*value, "value");
        if (!nested) break;
        value = nested;
    }
    return value;
}

bool ParseNumber(const JsonValue* value, double& number) {
    value = Unwrap(value);
    if (!value || value->kind != JsonKind::Number || value->text.empty()) return false;
    char* end = nullptr;
    number = std::strtod(value->text.c_str(), &end);
    return end == value->text.c_str() + value->text.size() && std::isfinite(number);
}

bool ParseInteger(const JsonValue* value, int64_t& number) {
    double parsed = 0.0;
    if (!ParseNumber(value, parsed) || std::trunc(parsed) != parsed ||
        parsed < -std::ldexp(1.0, 63) || parsed >= std::ldexp(1.0, 63)) return false;
    number = static_cast<int64_t>(parsed);
    return static_cast<double>(number) == parsed;
}

bool SameFloat(const JsonValue& object, std::string_view key, float expected) {
    double parsed = 0.0;
    return ParseNumber(Find(object, key), parsed) && std::abs(parsed) <= std::numeric_limits<float>::max() &&
        static_cast<float>(parsed) == expected;
}

bool ParseNestedJson(const JsonValue* value, JsonValue& output) {
    value = Unwrap(value);
    if (!value) return false;
    if (value->kind == JsonKind::Object) { output = *value; return true; }
    if (value->kind != JsonKind::String || value->text.size() > kMaximumMetadataBytes) return false;
    JsonParser parser(value->text.data(), value->text.size());
    return parser.Parse(output) && output.kind == JsonKind::Object;
}

bool ValidateCalibration(const LkgWorkerSubpixelCalibrationV1& calibration,
    const LkgWorkerSubpixelCellV1 cells[4], std::string& error) {
    LkgCommonSubpixelCalibration common{};
    common.width = calibration.nativeWidth;
    common.height = calibration.nativeHeight;
    common.center = calibration.center;
    common.pitch = calibration.pitch;
    common.slope = calibration.slope;
    common.dpi = calibration.dpi;
    common.flipX = calibration.flipX;
    common.viewCone = calibration.viewCone;
    common.fringe = calibration.fringe;
    common.invertedViews = calibration.invertedViews;
    common.cellPatternMode = calibration.cellPatternMode;
    common.cellCount = calibration.cellCount;
    common.redIndex = calibration.redIndex;
    common.blueIndex = calibration.blueIndex;
    LkgCommonSubpixelCell converted[4]{};
    if (cells) for (size_t i = 0; i < 4; ++i) {
        converted[i] = { cells[i].redX, cells[i].redY, cells[i].greenX, cells[i].greenY,
            cells[i].blueX, cells[i].blueY };
    }
    if (const char* invalid = lkg_validate_subpixel_calibration(&common,
            LKG_COMMON_SUBPIXEL_AUTOMATIC, cells ? converted : nullptr)) {
        error = invalid;
        return false;
    }
    return true;
}

bool ParseResponse(const char* bytes, size_t length, const LkgWorkerSubpixelCalibrationV1& expected,
    LkgWorkerSubpixelCellV1 cells[4], std::string& error) {
    JsonValue response;
    JsonParser parser(bytes, length);
    if (!parser.Parse(response) || response.kind != JsonKind::Object) {
        error = "Bridge cell response JSON is malformed";
        return false;
    }
    const JsonValue* status = Unwrap(Find(response, "status"));
    if (!status || status->kind != JsonKind::String || status->text != "Completion") {
        error = "Bridge cell response is incomplete";
        return false;
    }
    const JsonValue* displayMap = Unwrap(Find(response, "payload"));
    if (!displayMap || displayMap->kind != JsonKind::Object || displayMap->object.size() > 256) {
        error = "Bridge cell response has an invalid display map";
        return false;
    }
    const JsonValue* selected = nullptr;
    for (const auto& entry : displayMap->object) {
        const JsonValue* display = Unwrap(&entry.second);
        int64_t index = -1;
        if (!display || display->kind != JsonKind::Object || !ParseInteger(Find(*display, "index"), index)) continue;
        if (index != expected.displayIndex) continue;
        if (selected) { error = "Bridge cell response has duplicate display indices"; return false; }
        selected = display;
    }
    if (!selected) { error = "Bridge cell response does not contain the selected display"; return false; }
    int64_t x = 0, y = 0;
    const JsonValue* coordinates = Unwrap(Find(*selected, "windowCoords"));
    if (!coordinates || coordinates->kind != JsonKind::Object ||
        !ParseInteger(Find(*coordinates, "x"), x) || !ParseInteger(Find(*coordinates, "y"), y) ||
        x != expected.windowX || y != expected.windowY) {
        error = "Bridge cell response coordinates do not match the selected display";
        return false;
    }
    JsonValue calibration;
    if (!ParseNestedJson(Find(*selected, "calibration"), calibration)) {
        error = "Bridge cell calibration JSON is missing or malformed";
        return false;
    }
    bool same = SameFloat(calibration, "screenW", static_cast<float>(expected.nativeWidth)) &&
        SameFloat(calibration, "screenH", static_cast<float>(expected.nativeHeight)) &&
        SameFloat(calibration, "center", expected.center) && SameFloat(calibration, "pitch", expected.pitch) &&
        SameFloat(calibration, "slope", expected.slope) && SameFloat(calibration, "DPI", expected.dpi) &&
        SameFloat(calibration, "flipImageX", expected.flipX) &&
        SameFloat(calibration, "invView", static_cast<float>(expected.invertedViews)) &&
        SameFloat(calibration, "viewCone", expected.viewCone);
    bool hasMode = false;
    for (std::string_view key : { std::string_view("CellPatternMode"), std::string_view("cellPatternMode") }) {
        const JsonValue* value = Find(calibration, key);
        if (!value) continue;
        hasMode = true;
        int64_t mode = -1;
        same = same && ParseInteger(value, mode) && mode == expected.cellPatternMode;
    }
    if (!hasMode) same = same && expected.cellPatternMode == 0 && expected.cellCount == 0;
    const JsonValue* cellArray = Unwrap(Find(calibration, "subpixelCells"));
    if (!cellArray && expected.cellCount == 0) {
        static const JsonValue emptyArray{ JsonKind::Array };
        cellArray = &emptyArray;
    }
    if (!same || !cellArray || cellArray->kind != JsonKind::Array ||
        cellArray->array.size() != static_cast<size_t>(expected.cellCount) || expected.cellCount < 0 || expected.cellCount > 4) {
        error = "Bridge cell JSON does not match the selected native calibration";
        return false;
    }
    LkgWorkerSubpixelCellV1 parsed[4]{};
    const std::string_view fields[6] = { "ROffsetX", "ROffsetY", "GOffsetX", "GOffsetY", "BOffsetX", "BOffsetY" };
    for (size_t cellIndex = 0; cellIndex < cellArray->array.size(); ++cellIndex) {
        const JsonValue* item = Unwrap(&cellArray->array[cellIndex]);
        if (!item || item->kind != JsonKind::Object) { error = "Bridge subpixel cell must be an object"; return false; }
        float values[6]{};
        for (size_t field = 0; field < 6; ++field) {
            double value = 0.0;
            if (!ParseNumber(Find(*item, fields[field]), value) || std::abs(value) > std::numeric_limits<float>::max()) {
                error = "Bridge subpixel cell offset is missing, nonnumeric or out of range";
                return false;
            }
            values[field] = static_cast<float>(value);
        }
        parsed[cellIndex] = { values[0], values[1], values[2], values[3], values[4], values[5] };
    }
    if (!ValidateCalibration(expected, parsed, error)) return false;
    for (int i = 0; i < 4; ++i) cells[i] = parsed[i];
    return true;
}

struct WinHandle {
    HINTERNET value = nullptr;
    ~WinHandle() { if (value) WinHttpCloseHandle(value); }
};

bool SetRemainingTimeouts(HINTERNET session, ULONGLONG deadline, std::string& error) {
    const ULONGLONG now = GetTickCount64();
    if (now >= deadline) { error = "Bridge metadata operation exceeded its deadline"; return false; }
    const DWORD remaining = static_cast<DWORD>(std::min<ULONGLONG>(deadline - now, 2000));
    if (!WinHttpSetTimeouts(session, static_cast<int>(remaining), static_cast<int>(remaining),
            static_cast<int>(remaining), static_cast<int>(remaining))) {
        error = "Bridge metadata HTTP timeout could not be updated";
        return false;
    }
    return true;
}

bool HttpPut(HINTERNET session, HINTERNET connection, const wchar_t* route, const std::string& body,
    ULONGLONG deadline,
    std::string& responseBody, std::string& error) {
    WinHandle request;
    request.value = WinHttpOpenRequest(connection, L"PUT", route, nullptr, WINHTTP_NO_REFERER,
        WINHTTP_DEFAULT_ACCEPT_TYPES, 0);
    if (!request.value) { error = "Bridge metadata HTTP request could not be created"; return false; }
    DWORD redirectPolicy = WINHTTP_OPTION_REDIRECT_POLICY_NEVER;
    if (!WinHttpSetOption(request.value, WINHTTP_OPTION_REDIRECT_POLICY, &redirectPolicy, sizeof(redirectPolicy))) {
        error = "Bridge metadata redirect protection could not be configured";
        return false;
    }
    const wchar_t headers[] = L"Content-Type: application/json\r\n";
    if (!SetRemainingTimeouts(session, deadline, error)) return false;
    if (!WinHttpSendRequest(request.value, headers, static_cast<DWORD>(-1L),
            const_cast<char*>(body.data()), static_cast<DWORD>(body.size()), static_cast<DWORD>(body.size()), 0)) {
        char message[96]{};
        std::snprintf(message, sizeof(message), "Bridge metadata HTTP send failed win32=%lu",
            static_cast<unsigned long>(GetLastError()));
        error = message;
        return false;
    }
    if (!SetRemainingTimeouts(session, deadline, error)) return false;
    if (!WinHttpReceiveResponse(request.value, nullptr)) {
        char message[96]{};
        std::snprintf(message, sizeof(message), "Bridge metadata HTTP receive failed win32=%lu",
            static_cast<unsigned long>(GetLastError()));
        error = message;
        return false;
    }
    if (GetTickCount64() >= deadline) {
        error = "Bridge metadata HTTP request exceeded its deadline";
        return false;
    }
    DWORD status = 0;
    DWORD statusSize = sizeof(status);
    if (!WinHttpQueryHeaders(request.value, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
            WINHTTP_HEADER_NAME_BY_INDEX, &status, &statusSize, WINHTTP_NO_HEADER_INDEX) || status != 200) {
        error = "Bridge metadata HTTP response was rejected";
        return false;
    }
    DWORD contentLength = 0;
    DWORD contentLengthSize = sizeof(contentLength);
    if (WinHttpQueryHeaders(request.value, WINHTTP_QUERY_CONTENT_LENGTH | WINHTTP_QUERY_FLAG_NUMBER,
            WINHTTP_HEADER_NAME_BY_INDEX, &contentLength, &contentLengthSize, WINHTTP_NO_HEADER_INDEX) &&
        contentLength > kMaximumMetadataBytes) {
        error = "Bridge metadata JSON exceeds the size limit";
        return false;
    }
    responseBody.clear();
    for (;;) {
        if (!SetRemainingTimeouts(session, deadline, error)) return false;
        DWORD available = 0;
        if (!WinHttpQueryDataAvailable(request.value, &available)) { error = "Bridge metadata response read failed"; return false; }
        if (available == 0) break;
        if (available > kMaximumMetadataBytes - responseBody.size()) {
            error = "Bridge metadata JSON exceeds the size limit";
            return false;
        }
        const size_t previous = responseBody.size();
        responseBody.resize(previous + available);
        DWORD read = 0;
        if (!SetRemainingTimeouts(session, deadline, error) ||
            !WinHttpReadData(request.value, responseBody.data() + previous, available, &read) || read == 0 ||
            GetTickCount64() >= deadline) {
            if (error.empty()) error = "Bridge metadata response exceeded its deadline";
            return false;
        }
        responseBody.resize(previous + read);
    }
    return true;
}

bool ParseCompletion(const std::string& bytes, JsonValue& value) {
    JsonParser parser(bytes.data(), bytes.size());
    if (!parser.Parse(value) || value.kind != JsonKind::Object) return false;
    const JsonValue* status = Unwrap(Find(value, "status"));
    return status && status->kind == JsonKind::String && status->text == "Completion";
}

bool JsonQuote(const std::string& value, std::string& output) {
    output.clear();
    output.push_back('"');
    for (unsigned char ch : value) {
        if (ch < 0x20 || ch >= 0x7f) return false;
        if (ch == '"' || ch == '\\') output.push_back('\\');
        output.push_back(static_cast<char>(ch));
    }
    output.push_back('"');
    return true;
}

} // namespace

bool ParseBridgeCellResponse(const char* json, size_t bytes, const LkgWorkerSubpixelCalibrationV1& expected,
    LkgWorkerSubpixelCellV1 cells[4], std::string& error) {
    error.clear();
    if (cells) for (int i = 0; i < 4; ++i) cells[i] = {};
    if (!json || !cells || bytes > kMaximumMetadataBytes) {
        error = "Bridge cell JSON exceeds the size limit or is missing";
        return false;
    }
    try { return ParseResponse(json, bytes, expected, cells, error); }
    catch (...) { error = "Bridge cell JSON parser failed"; return false; }
}

bool ReadBridgeCellTable(const LkgWorkerSubpixelCalibrationV1& expected, LkgWorkerSubpixelCellV1 cells[4],
    std::string& error, uint16_t port, uint32_t timeoutMilliseconds) {
    error.clear();
    if (!cells || port == 0 || timeoutMilliseconds == 0) { error = "Bridge metadata request arguments are invalid"; return false; }
    for (int i = 0; i < 4; ++i) cells[i] = {};
    WinHandle session;
    session.value = WinHttpOpen(L"ViewWeave Windows metadata/1", WINHTTP_ACCESS_TYPE_NO_PROXY,
        WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session.value) { error = "Bridge metadata HTTP session could not be created"; return false; }
    const int initialTimeout = static_cast<int>(std::min<uint32_t>(timeoutMilliseconds, 2000));
    int timeouts[4] = { initialTimeout, initialTimeout, initialTimeout, initialTimeout };
    if (!WinHttpSetTimeouts(session.value, timeouts[0], timeouts[1], timeouts[2], timeouts[3])) {
        error = "Bridge metadata HTTP timeout could not be configured";
        return false;
    }
    WinHandle connection;
    connection.value = WinHttpConnect(session.value, L"127.0.0.1", port, 0);
    if (!connection.value) { error = "Bridge metadata service is unavailable on loopback"; return false; }
    static std::atomic<uint64_t> serial{ 1 };
    const std::string name = "ViewWeave Windows metadata " + std::to_string(GetCurrentProcessId()) + "-" +
        std::to_string(GetCurrentThreadId()) + "-" + std::to_string(serial.fetch_add(1, std::memory_order_relaxed));
    const ULONGLONG deadline = GetTickCount64() + timeoutMilliseconds;
    std::string quotedName;
    if (!JsonQuote(name, quotedName)) { error = "Bridge metadata orchestration name could not be encoded"; return false; }
    std::string responseText;
    if (!HttpPut(session.value, connection.value, L"enter_orchestration", "{\"name\":" + quotedName + "}",
            deadline, responseText, error)) return false;
    JsonValue entered;
    if (!ParseCompletion(responseText, entered)) { error = "Bridge metadata orchestration response is invalid"; return false; }
    const JsonValue* tokenValue = Unwrap(Find(entered, "payload"));
    if (!tokenValue || tokenValue->kind != JsonKind::String || tokenValue->text.empty() || tokenValue->text.size() > 512) {
        error = "Bridge did not return a valid metadata orchestration";
        return false;
    }
    const std::string token = tokenValue->text;
    std::string quotedToken;
    if (!JsonQuote(token, quotedToken)) { error = "Bridge metadata orchestration token is invalid"; return false; }
    bool success = false;
    std::string requestError;
    const bool fetched = HttpPut(session.value, connection.value, L"available_output_devices",
        "{\"orchestration\":" + quotedToken + "}", deadline, responseText, requestError);
    if (fetched) success = ParseBridgeCellResponse(responseText.data(), responseText.size(), expected, cells, requestError);
    std::string exitResponse;
    std::string exitError;
    const ULONGLONG cleanupDeadline = GetTickCount64() + std::min<uint32_t>(timeoutMilliseconds, 2000);
    const bool exited = HttpPut(session.value, connection.value, L"exit_orchestration",
        "{\"orchestration\":" + quotedToken + "}", cleanupDeadline, exitResponse, exitError) && [&] {
            JsonValue completion;
            return ParseCompletion(exitResponse, completion);
        }();
    if (!exited) {
        error = "Bridge metadata orchestration cleanup failed";
        return false;
    }
    if (!success) { error = requestError.empty() ? "Bridge cell metadata request failed" : requestError; return false; }
    return true;
}

} // namespace lkg::subpixel
