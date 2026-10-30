// Standalone Windows x64 compatibility probe. It is deliberately not part of the WPF app.
// Build against the pinned LibRaw 0.22.3 Win64 SDK, never an embedded RAW thumbnail.
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <Psapi.h>
#include "libraw/libraw.h"
#include "libraw/libraw_version.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <limits>
#include <locale>
#include <memory>
#include <optional>
#include <sstream>
#include <stdexcept>
#include <string>
#include <system_error>
#include <utility>
#include <vector>

static_assert(LIBRAW_VERSION == LIBRAW_MAKE_VERSION(0, 22, 3), "Use the pinned LibRaw 0.22.3 SDK.");
static_assert(sizeof(void*) == 8, "Build this probe for Windows x64.");

namespace {
namespace fs = std::filesystem;
using Clock = std::chrono::steady_clock;
constexpr int ProbeFailure = 10001;

std::string Utf8(const std::wstring& value)
{
    if (value.empty()) return {};
    if (value.size() > static_cast<size_t>((std::numeric_limits<int>::max)()))
        throw std::runtime_error("A Unicode value is too long.");
    const int length = static_cast<int>(value.size());
    const int bytes = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), length,
        nullptr, 0, nullptr, nullptr);
    if (bytes <= 0) throw std::runtime_error("Cannot encode a Unicode path as UTF-8.");
    std::string result(static_cast<size_t>(bytes), '\0');
    if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), length,
        result.data(), bytes, nullptr, nullptr) != bytes)
        throw std::runtime_error("Cannot encode a Unicode path as UTF-8.");
    return result;
}

// LibRaw camera identifiers are normally ASCII. Preserve valid UTF-8 identifiers;
// transcode legacy Windows bytes rather than putting invalid UTF-8 into the report.
std::string SafeText(const std::string& value)
{
    if (value.empty()) return {};
    const int length = static_cast<int>(value.size());
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), length, nullptr, 0) > 0)
        return value;
    const int count = MultiByteToWideChar(CP_ACP, 0, value.data(), length, nullptr, 0);
    if (count <= 0) return "[unavailable text encoding]";
    std::wstring wide(static_cast<size_t>(count), L'\0');
    if (MultiByteToWideChar(CP_ACP, 0, value.data(), length, wide.data(), count) != count)
        return "[unavailable text encoding]";
    return Utf8(wide);
}

std::string Quote(const std::string& value)
{
    std::ostringstream out;
    out << '"';
    for (const unsigned char character : SafeText(value))
    {
        switch (character)
        {
        case '"': out << "\\\""; break;
        case '\\': out << "\\\\"; break;
        case '\b': out << "\\b"; break;
        case '\f': out << "\\f"; break;
        case '\n': out << "\\n"; break;
        case '\r': out << "\\r"; break;
        case '\t': out << "\\t"; break;
        default:
            if (character < 0x20)
                out << "\\u00" << std::hex << std::setw(2) << std::setfill('0') << static_cast<unsigned>(character);
            else out << static_cast<char>(character);
        }
    }
    out << '"';
    return out.str();
}

class JsonObject
{
public:
    void Raw(const std::string& name, const std::string& value) { fields_.emplace_back(name, value); }
    void String(const std::string& name, const std::string& value) { Raw(name, Quote(value)); }
    void NullableString(const std::string& name, const std::string& value)
    { Raw(name, value.empty() ? "null" : Quote(value)); }
    void Boolean(const std::string& name, bool value) { Raw(name, value ? "true" : "false"); }
    template<typename T> void Number(const std::string& name, T value)
    {
        std::ostringstream out;
        out.imbue(std::locale::classic());
        out << std::setprecision(17) << value;
        Raw(name, out.str());
    }
    template<typename T> void OptionalNumber(const std::string& name, const std::optional<T>& value)
    { if (value) Number(name, *value); else Raw(name, "null"); }
    void PositiveNumber(const std::string& name, double value)
    { if (std::isfinite(value) && value > 0) Number(name, value); else Raw(name, "null"); }
    std::string Render() const
    {
        std::ostringstream out;
        out << '{';
        for (size_t index = 0; index < fields_.size(); ++index)
        {
            if (index) out << ',';
            out << '\n' << Quote(fields_[index].first) << ':' << fields_[index].second;
        }
        out << "\n}";
        return out.str();
    }
private:
    std::vector<std::pair<std::string, std::string>> fields_;
};

struct CallbackState
{
    std::atomic<bool> demosaicReached{ false };
    std::atomic<bool> cancellationTriggered{ false };
    std::atomic<std::uint64_t> count{ 0 };
    std::array<std::atomic<bool>, 31> stages{};
    bool cancelAtUnpack = false;
    CallbackState()
    { for (auto& stage : stages) stage.store(false, std::memory_order_relaxed); }
};

int Progress(void* context, enum LibRaw_progress stage, int, int) noexcept
{
    auto& state = *static_cast<CallbackState*>(context);
    state.count.fetch_add(1, std::memory_order_relaxed);
    const auto flags = static_cast<std::uint32_t>(stage);
    for (size_t bit = 0; bit < state.stages.size(); ++bit)
        if (flags & (std::uint32_t{ 1 } << bit)) state.stages[bit].store(true, std::memory_order_relaxed);
    if (stage == LIBRAW_PROGRESS_INTERPOLATE)
        state.demosaicReached.store(true, std::memory_order_relaxed);
    // LOAD_RAW is the native unpack stage; the SDK has no separate UNPACK enum.
    if (state.cancelAtUnpack && stage == LIBRAW_PROGRESS_LOAD_RAW)
    {
        state.cancellationTriggered.store(true, std::memory_order_relaxed);
        return 1;
    }
    return 0;
}

struct Timings
{
    std::optional<double> open, unpack, process, makeImage, preview, detail;
    double total = 0;
};

struct Timer
{
    std::optional<double>& elapsed;
    Clock::time_point start = Clock::now();
    ~Timer() { elapsed = std::chrono::duration<double, std::milli>(Clock::now() - start).count(); }
};

struct Report
{
    fs::path input, report, preview, detail;
    std::string status = "Failed", failureStage = "Arguments", errorMessage, errorDomain = "Probe";
    int errorCode = ProbeFailure;
    std::optional<JsonObject> metadata, sensor, output, settings;
    JsonObject previewInfo, detailInfo;
    bool previewWritten = false, detailWritten = false;
    std::optional<std::uint64_t> baselineWorkingSet, peakWorkingSet;
    std::optional<unsigned long> baselineMemoryError, memoryError;
    std::optional<int> decoderErrorCount;
    Timings timings;
    CallbackState callback;
};

class DecodeError : public std::runtime_error
{
public:
    explicit DecodeError(int value) : std::runtime_error(LibRaw::strerror(value)), code(value) { }
    int code;
};

void Check(int code) { if (code != LIBRAW_SUCCESS) throw DecodeError(code); }

template<size_t Size> std::string CameraText(const char (&value)[Size])
{
    std::string result(value, std::find(value, value + Size, '\0'));
    while (!result.empty() && (result.back() == ' ' || result.back() == '\t')) result.pop_back();
    return result;
}

void CaptureMetadata(Report& report, const LibRaw& raw)
{
    JsonObject metadata;
    metadata.NullableString("Make", CameraText(raw.imgdata.idata.make));
    metadata.NullableString("Model", CameraText(raw.imgdata.idata.model));
    const auto& other = raw.imgdata.other;
    metadata.PositiveNumber("Iso", other.iso_speed);
    metadata.PositiveNumber("ShutterSeconds", other.shutter);
    metadata.PositiveNumber("ApertureFNumber", other.aperture);
    metadata.PositiveNumber("FocalLengthMillimeters", other.focal_len);
    if (other.timestamp > 0)
    {
        metadata.Number("TimestampUnixSeconds", static_cast<std::int64_t>(other.timestamp));
        std::tm calendar{};
        std::array<char, 32> timestamp{};
        if (gmtime_s(&calendar, &other.timestamp) == 0 &&
            std::strftime(timestamp.data(), timestamp.size(), "%Y-%m-%dT%H:%M:%S", &calendar))
            metadata.String("CapturedAt", timestamp.data());
        else metadata.Raw("CapturedAt", "null");
        metadata.String("CapturedAtSource", "LibRaw timestamp; original EXIF timezone is unavailable, no timezone is inferred");
    }
    else
    {
        metadata.Raw("TimestampUnixSeconds", "null");
        metadata.Raw("CapturedAt", "null");
        metadata.Raw("CapturedAtSource", "null");
    }
    metadata.Raw("CapturedAtTimeZone", "null");
    report.metadata = std::move(metadata);

    const auto& sizes = raw.imgdata.sizes;
    JsonObject sensor;
    sensor.PositiveNumber("RawWidth", sizes.raw_width);
    sensor.PositiveNumber("RawHeight", sizes.raw_height);
    sensor.PositiveNumber("ActiveWidth", sizes.width);
    sensor.PositiveNumber("ActiveHeight", sizes.height);
    sensor.Number("TopMargin", sizes.top_margin);
    sensor.Number("LeftMargin", sizes.left_margin);
    const int right = static_cast<int>(sizes.raw_width) - sizes.left_margin - sizes.width;
    const int bottom = static_cast<int>(sizes.raw_height) - sizes.top_margin - sizes.height;
    if (sizes.raw_width && sizes.width && right >= 0) sensor.Number("RightMargin", right);
    else sensor.Raw("RightMargin", "null");
    if (sizes.raw_height && sizes.height && bottom >= 0) sensor.Number("BottomMargin", bottom);
    else sensor.Raw("BottomMargin", "null");
    sensor.String("RightBottomMarginSource", "derived from raw size minus top/left margin and active size");
    sensor.Number("Flip", sizes.flip);
    sensor.PositiveNumber("PixelAspectRatio", sizes.pixel_aspect);
    sensor.PositiveNumber("RawCount", raw.imgdata.idata.raw_count);
    sensor.Number("Filters", raw.imgdata.idata.filters);
    sensor.Boolean("IsFoveon", raw.imgdata.idata.is_foveon != 0);
    sensor.NullableString("CfaDescription", CameraText(raw.imgdata.idata.cdesc));
    std::ostringstream crops;
    crops << '[';
    for (size_t index = 0; index < 2; ++index)
    {
        if (index) crops << ',';
        const auto& crop = sizes.raw_inset_crops[index];
        if (!crop.cwidth || !crop.cheight) { crops << "null"; continue; }
        JsonObject inset;
        if (crop.cleft == 0xffff) inset.Raw("Left", "null"); else inset.Number("Left", crop.cleft);
        if (crop.ctop == 0xffff) inset.Raw("Top", "null"); else inset.Number("Top", crop.ctop);
        inset.Number("Width", crop.cwidth); inset.Number("Height", crop.cheight);
        crops << inset.Render();
    }
    crops << ']';
    sensor.Raw("RawInsetCrops", crops.str());
    report.sensor = std::move(sensor);
}

void Configure(Report& report, LibRaw& raw)
{
    auto& parameters = raw.imgdata.params;
    parameters.use_camera_wb = 1;
    parameters.use_auto_wb = 0;
    parameters.output_color = 1;
    parameters.output_bps = 8;
    parameters.half_size = 0;
    parameters.user_qual = 3;
    parameters.no_interpolation = 0;
    parameters.four_color_rgb = 0;
    parameters.user_flip = -1; // Use the camera orientation interpreted by LibRaw.
    JsonObject settings;
    settings.Number("UseCameraWhiteBalance", parameters.use_camera_wb);
    settings.Number("UseAutoWhiteBalance", parameters.use_auto_wb);
    settings.Number("OutputColor", parameters.output_color);
    settings.String("ColorSpace", "sRGB");
    settings.Number("OutputBits", parameters.output_bps);
    settings.Number("HalfSize", parameters.half_size);
    settings.Number("UserQuality", parameters.user_qual);
    settings.String("DemosaicAlgorithm", "AHD (user_qual=3)");
    settings.Number("NoInterpolation", parameters.no_interpolation);
    settings.Number("NoAutoBright", parameters.no_auto_bright);
    settings.Number("UserFlip", parameters.user_flip);
    settings.Number("GammaPower", parameters.gamm[0]);
    settings.Number("GammaSlope", parameters.gamm[1]);
    report.settings = std::move(settings);
}

bool SamePath(const fs::path& first, const fs::path& second)
{
    std::error_code error;
    if (fs::equivalent(first, second, error) && !error) return true;
    const auto a = fs::weakly_canonical(first).wstring();
    const auto b = fs::weakly_canonical(second).wstring();
    return CompareStringOrdinal(a.c_str(), -1, b.c_str(), -1, TRUE) == CSTR_EQUAL;
}

void ValidatePaths(const Report& report)
{
    const std::array<fs::path, 4> paths{ report.input, report.report, report.preview, report.detail };
    for (size_t first = 0; first < paths.size(); ++first)
        for (size_t second = first + 1; second < paths.size(); ++second)
            if (SamePath(paths[first], paths[second]))
                throw std::runtime_error("Input RAW, JSON report and both previews must be distinct files; the RAW is never overwritten.");
}

void EnsureParent(const fs::path& path)
{
    if (!path.parent_path().empty()) fs::create_directories(path.parent_path());
}

void Put32(std::array<unsigned char, 54>& header, size_t offset, std::uint32_t value)
{
    for (size_t byte = 0; byte < 4; ++byte)
        header[offset + byte] = static_cast<unsigned char>((value >> (8 * byte)) & 0xff);
}

struct Crop { unsigned x, y, width, height; };

void WriteBmp(const fs::path& path, const libraw_processed_image_t& image, Crop crop,
    unsigned width, unsigned height)
{
    if (!width || !height || !crop.width || !crop.height || crop.x > image.width || crop.y > image.height ||
        crop.width > image.width - crop.x || crop.height > image.height - crop.y)
        throw std::runtime_error("Preview crop dimensions are outside the decoded RGB image.");
    const std::uint64_t stride64 = (std::uint64_t{ width } * 3 + 3) & ~std::uint64_t{ 3 };
    const std::uint64_t bytes = stride64 * height;
    if (bytes > (std::numeric_limits<std::uint32_t>::max)() - 54)
        throw std::runtime_error("Preview BMP is too large.");
    const auto stride = static_cast<size_t>(stride64);
    std::array<unsigned char, 54> header{};
    header[0] = 'B'; header[1] = 'M';
    Put32(header, 2, static_cast<std::uint32_t>(54 + bytes));
    Put32(header, 10, 54); Put32(header, 14, 40);
    Put32(header, 18, width); Put32(header, 22, height);
    header[26] = 1; header[28] = 24;
    Put32(header, 34, static_cast<std::uint32_t>(bytes));
    EnsureParent(path);
    std::ofstream output(path, std::ios::binary | std::ios::trunc);
    if (!output) throw std::runtime_error("Cannot create a BMP preview.");
    output.write(reinterpret_cast<const char*>(header.data()), static_cast<std::streamsize>(header.size()));
    std::vector<unsigned char> row(stride, 0);
    const bool direct = width == crop.width && height == crop.height;
    auto sample = [&](unsigned x, unsigned y, unsigned channel) -> double
    {
        const size_t offset = (static_cast<size_t>(y) * image.width + x) * image.colors + channel;
        return image.data[offset];
    };
    // BMP is bottom-up; LibRaw's processed RGB buffer is top-down and already oriented.
    for (unsigned line = height; line > 0; --line)
    {
        const unsigned y = line - 1;
        for (unsigned x = 0; x < width; ++x)
        {
            if (direct)
            {
                const size_t offset = (static_cast<size_t>(crop.y + y) * image.width + crop.x + x) * image.colors;
                row[x * 3] = image.data[offset + 2];
                row[x * 3 + 1] = image.data[offset + 1];
                row[x * 3 + 2] = image.data[offset];
            }
            else
            {
                const double sx = std::clamp(crop.x + (x + .5) * crop.width / width - .5,
                    static_cast<double>(crop.x), static_cast<double>(crop.x + crop.width - 1));
                const double sy = std::clamp(crop.y + (y + .5) * crop.height / height - .5,
                    static_cast<double>(crop.y), static_cast<double>(crop.y + crop.height - 1));
                const auto x0 = static_cast<unsigned>(std::floor(sx));
                const auto y0 = static_cast<unsigned>(std::floor(sy));
                const auto x1 = (std::min)(x0 + 1, crop.x + crop.width - 1);
                const auto y1 = (std::min)(y0 + 1, crop.y + crop.height - 1);
                const double dx = sx - x0, dy = sy - y0;
                for (unsigned channel = 0; channel < 3; ++channel)
                {
                    const double top = sample(x0, y0, channel) * (1 - dx) + sample(x1, y0, channel) * dx;
                    const double bottom = sample(x0, y1, channel) * (1 - dx) + sample(x1, y1, channel) * dx;
                    row[x * 3 + (2 - channel)] = static_cast<unsigned char>(
                        std::clamp(std::lround(top * (1 - dy) + bottom * dy), 0L, 255L));
                }
            }
        }
        output.write(reinterpret_cast<const char*>(row.data()), static_cast<std::streamsize>(row.size()));
        if (!output) throw std::runtime_error("Cannot write a BMP preview.");
    }
    output.flush();
    if (!output) throw std::runtime_error("Cannot flush a BMP preview.");
    output.close();
    if (!output) throw std::runtime_error("Cannot close a BMP preview.");
}

void Decode(Report& report)
{
    if (LibRaw::versionNumber() != LIBRAW_MAKE_VERSION(0, 22, 3))
        throw std::runtime_error("The loaded DLL is not LibRaw 0.22.3.");
    // Measure the loaded probe's baseline before constructing LibRaw or opening a RAW.
    PROCESS_MEMORY_COUNTERS_EX baseline{};
    baseline.cb = sizeof(baseline);
    if (GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&baseline), sizeof(baseline)))
        report.baselineWorkingSet = static_cast<std::uint64_t>(baseline.WorkingSetSize);
    else report.baselineMemoryError = GetLastError();
    LibRaw raw;
    struct CaptureErrors
    {
        LibRaw& raw;
        Report& report;
        ~CaptureErrors() { report.decoderErrorCount = raw.error_count(); }
    } errors{ raw, report };
    Configure(report, raw);
    raw.set_progress_handler(Progress, &report.callback);
    report.failureStage = "Open";
    { Timer timer{ report.timings.open }; Check(raw.open_file(report.input.c_str())); }
    CaptureMetadata(report, raw);
    report.failureStage = "Unpack";
    { Timer timer{ report.timings.unpack }; Check(raw.unpack()); }
    if (report.callback.cancelAtUnpack)
        throw std::runtime_error("Unpack finished without an observable LOAD_RAW cancellation callback; cancellation was not verified.");
    report.failureStage = "Process";
    { Timer timer{ report.timings.process }; Check(raw.dcraw_process()); }
    report.failureStage = "MakeMemImage";
    std::unique_ptr<libraw_processed_image_t, decltype(&LibRaw::dcraw_clear_mem)> image(nullptr, LibRaw::dcraw_clear_mem);
    {
        Timer timer{ report.timings.makeImage };
        int code = LIBRAW_SUCCESS;
        image.reset(raw.dcraw_make_mem_image(&code));
        Check(code);
        if (!image) throw std::runtime_error("LibRaw returned a null processed image without an error code.");
    }
    JsonObject output;
    output.Number("Width", image->width); output.Number("Height", image->height);
    output.Number("Channels", image->colors); output.Number("Bits", image->bits);
    output.Number("Bytes", image->data_size); output.String("ColorSpace", "sRGB");
    output.Boolean("DefaultLibRawOrientation", raw.imgdata.params.user_flip == -1);
    output.Raw("ActualAppliedOrientation", "null");
    output.String("OrientationHandling", "LibRaw default camera orientation is enabled (user_flip=-1); dimensions describe the dcraw_make_mem_image output. The probe applies no extra rotation; the exact transform is not independently measured.");
    report.output = std::move(output);
    if (image->type != LIBRAW_IMAGE_BITMAP || image->bits != 8 || image->colors != 3 ||
        !image->width || !image->height)
        throw std::runtime_error("Expected a nonempty 8-bit three-channel processed RGB bitmap; the actual format is in Output.");
    const auto expected = std::uint64_t{ image->width } * image->height * image->colors;
    if (expected != image->data_size)
        throw std::runtime_error("LibRaw RGB dimensions and allocated output byte count do not agree.");
    report.output->Number("RgbOutputBytes", expected);
    report.failureStage = "Preview";
    const double scale = (std::min)(1.0, 1600.0 / (std::max)(image->width, image->height));
    const auto width = (std::max)(1U, static_cast<unsigned>(std::lround(image->width * scale)));
    const auto height = (std::max)(1U, static_cast<unsigned>(std::lround(image->height * scale)));
    {
        Timer timer{ report.timings.preview };
        WriteBmp(report.preview, *image, { 0, 0, image->width, image->height }, width, height);
    }
    report.previewWritten = true;
    report.previewInfo.Number("Width", width); report.previewInfo.Number("Height", height);
    report.previewInfo.String("Resampling", "bilinear after full-resolution RAW processing");
    report.failureStage = "DetailPreview";
    const unsigned detailWidth = (std::min)(1000U, static_cast<unsigned>(image->width));
    const unsigned detailHeight = (std::min)(1000U, static_cast<unsigned>(image->height));
    const Crop detail{ (image->width - detailWidth) / 2, (image->height - detailHeight) / 2, detailWidth, detailHeight };
    {
        Timer timer{ report.timings.detail };
        WriteBmp(report.detail, *image, detail, detail.width, detail.height);
    }
    report.detailWritten = true;
    report.detailInfo.Number("Width", detail.width); report.detailInfo.Number("Height", detail.height);
    report.detailInfo.Number("X", detail.x); report.detailInfo.Number("Y", detail.y);
    report.detailInfo.String("Resampling", "none; one decoded pixel per BMP pixel (100%)");
    report.status = "Success";
    report.errorCode = LIBRAW_SUCCESS;
    report.errorDomain.clear(); report.errorMessage.clear(); report.failureStage.clear();
}

std::string RenderReport(const Report& report)
{
    JsonObject result;
    result.Number("SchemaVersion", 1);
    result.String("LibRawVersion", LibRaw::version());
    result.Number("LibRawVersionNumber", LibRaw::versionNumber());
    result.String("InputPath", Utf8(report.input.wstring()));
    result.String("ReportPath", Utf8(report.report.wstring()));
    result.String("PreviewPath", Utf8(report.preview.wstring()));
    result.String("DetailPreviewPath", Utf8(report.detail.wstring()));
    result.String("Status", report.status);
    result.NullableString("FailureStage", report.failureStage);
    result.Number("ErrorCode", report.errorCode);
    result.NullableString("ErrorDomain", report.errorDomain);
    result.NullableString("ErrorMessage", report.errorMessage);
    result.Raw("Metadata", report.metadata ? report.metadata->Render() : "null");
    result.Raw("Sensor", report.sensor ? report.sensor->Render() : "null");
    result.Raw("Output", report.output ? report.output->Render() : "null");
    result.Raw("DecodeSettings", report.settings ? report.settings->Render() : "null");
    JsonObject timings;
    timings.OptionalNumber("Open", report.timings.open);
    timings.OptionalNumber("Unpack", report.timings.unpack);
    timings.OptionalNumber("Process", report.timings.process);
    timings.OptionalNumber("MakeMemImage", report.timings.makeImage);
    timings.OptionalNumber("Preview", report.timings.preview);
    timings.OptionalNumber("DetailPreview", report.timings.detail);
    timings.Number("Total", report.timings.total);
    result.Raw("TimingsMs", timings.Render());
    result.OptionalNumber("BaselineWorkingSetBytes", report.baselineWorkingSet);
    result.OptionalNumber("BaselineMemoryMeasurementError", report.baselineMemoryError);
    result.OptionalNumber("PeakWorkingSetBytes", report.peakWorkingSet);
    result.OptionalNumber("MemoryMeasurementError", report.memoryError);
    result.OptionalNumber("DecoderErrorCount", report.decoderErrorCount);
    result.Boolean("DemosaicReached", report.callback.demosaicReached.load(std::memory_order_relaxed));
    result.Number("CallbackCount", report.callback.count.load(std::memory_order_relaxed));
    result.Boolean("CancelAtUnpack", report.callback.cancelAtUnpack);
    result.Boolean("CancellationCallbackTriggered", report.callback.cancellationTriggered.load(std::memory_order_relaxed));
    result.Boolean("UsesEmbeddedPreview", false);
    result.String("DecodePipeline", "open_file > unpack > dcraw_process > dcraw_make_mem_image");
    const std::array<const char*, 31> stageNames{ "OPEN", "IDENTIFY", "SIZE_ADJUST", "LOAD_RAW", "RAW2_IMAGE",
        "REMOVE_ZEROES", "BAD_PIXELS", "DARK_FRAME", "FOVEON_INTERPOLATE", "SCALE_COLORS", "PRE_INTERPOLATE",
        "INTERPOLATE", "MIX_GREEN", "MEDIAN_FILTER", "HIGHLIGHTS", "FUJI_ROTATE", "FLIP", "APPLY_PROFILE",
        "CONVERT_RGB", "STRETCH", "STAGE20", "STAGE21", "STAGE22", "STAGE23", "STAGE24", "STAGE25",
        "STAGE26", "STAGE27", "THUMB_LOAD", "TRESERVED1", "TRESERVED2" };
    std::ostringstream stages;
    stages << '[';
    bool first = true;
    for (size_t bit = 0; bit < stageNames.size(); ++bit)
        if (report.callback.stages[bit].load(std::memory_order_relaxed))
        {
            if (!first) stages << ',';
            first = false; stages << Quote(stageNames[bit]);
        }
    stages << ']';
    result.Raw("ProgressStages", stages.str());
    JsonObject preview = report.previewInfo;
    preview.Boolean("Written", report.previewWritten);
    JsonObject detail = report.detailInfo;
    detail.Boolean("Written", report.detailWritten);
    result.Raw("Preview", preview.Render());
    result.Raw("DetailPreview", detail.Render());
    return result.Render() + '\n';
}
} // namespace

int wmain(int argc, wchar_t* argv[])
{
    if (argc < 4)
    {
        std::wcerr << L"Usage: RawCompatibilityProbe.exe inputRaw report.json preview.bmp [--cancel-at-unpack]\n";
        return 64;
    }
    const auto start = Clock::now();
    Report report;
    bool safeToWriteReport = false;
    try
    {
        report.input = fs::absolute(fs::path(argv[1])).lexically_normal();
        report.report = fs::absolute(fs::path(argv[2])).lexically_normal();
        report.preview = fs::absolute(fs::path(argv[3])).lexically_normal();
        report.detail = report.preview.parent_path() / (report.preview.stem().wstring() + L"-detail.bmp");
        ValidatePaths(report);
        safeToWriteReport = true;
        if (argc > 5 || (argc == 5 && std::wstring(argv[4]) != L"--cancel-at-unpack"))
            throw std::runtime_error("Unknown arguments; the only optional flag is --cancel-at-unpack.");
        report.callback.cancelAtUnpack = argc == 5;
        Decode(report);
    }
    catch (const DecodeError& error)
    {
        report.status = error.code == LIBRAW_CANCELLED_BY_CALLBACK ? "Cancelled" : "Failed";
        report.errorDomain = "LibRaw";
        report.errorCode = error.code; report.errorMessage = error.what();
    }
    catch (const std::bad_alloc& error)
    {
        report.status = "Failed"; report.errorDomain = "Probe";
        report.errorCode = LIBRAW_UNSUFFICIENT_MEMORY; report.errorMessage = error.what();
    }
    catch (const std::exception& error)
    {
        report.status = "Failed"; report.errorDomain = "Probe";
        report.errorCode = ProbeFailure; report.errorMessage = error.what();
    }
    catch (...)
    {
        report.status = "Failed"; report.errorDomain = "Probe";
        report.errorCode = ProbeFailure; report.errorMessage = "Unexpected native exception.";
    }
    report.timings.total = std::chrono::duration<double, std::milli>(Clock::now() - start).count();
    PROCESS_MEMORY_COUNTERS_EX memory{};
    memory.cb = sizeof(memory);
    if (GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&memory), sizeof(memory)))
        report.peakWorkingSet = static_cast<std::uint64_t>(memory.PeakWorkingSetSize);
    else report.memoryError = GetLastError();
    if (!safeToWriteReport)
    {
        std::cerr << "No report was written because output paths are unsafe: " << report.errorMessage << '\n';
        return 1;
    }
    try
    {
        EnsureParent(report.report);
        std::ofstream output(report.report, std::ios::binary | std::ios::trunc);
        if (!output) throw std::runtime_error("Cannot create the JSON report.");
        output << RenderReport(report);
        output.flush();
        if (!output) throw std::runtime_error("Cannot write the JSON report.");
        output.close();
        if (!output) throw std::runtime_error("Cannot close the JSON report.");
    }
    catch (const std::exception& error)
    {
        std::cerr << "Report output failed: " << error.what() << '\n';
        return 1;
    }
    std::cout << report.status << '\n';
    return report.status == "Success" ? 0 : report.status == "Cancelled" ? 2 : 1;
}
