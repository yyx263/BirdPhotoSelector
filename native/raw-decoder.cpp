// Small ABI boundary around the unmodified, pinned LibRaw 0.22.3 DLL.
// Always decodes sensor pixels; embedded JPEG/thumbnail APIs are never called.
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include "libraw/libraw.h"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstddef>
#include <cstring>
#include <ctime>
#include <limits>
#include <memory>
#include <new>
#include <vector>

static_assert(LIBRAW_VERSION == LIBRAW_MAKE_VERSION(0, 22, 3), "Pinned SDK required.");
static_assert(sizeof(void*) == 8, "Windows x64 required.");

namespace {
constexpr int InvalidArgument = -200001;
constexpr int UnexpectedFailure = -200002;
constexpr int InvalidOutput = -200003;
constexpr int WrongVersion = -200004;
using Cancel = int(__cdecl*)();

struct RawInfo {
    int width, height, orientation;
    double iso, shutter, aperture, focal;
    std::int64_t timestamp;
    char camera[128];
    char captured_at[32];
};
// Keep the v1 ABI intact for older applications. Managed v2 uses the new entry point.
struct RawInfoV2 {
    RawInfo basic;
    double exposure_bias;
    int has_exposure_bias;
};
static_assert(sizeof(RawInfo) == 216 && offsetof(RawInfoV2, exposure_bias) == 216 &&
              offsetof(RawInfoV2, has_exposure_bias) == 224 && sizeof(RawInfoV2) == 232,
              "RAW metadata ABI must match the managed sequential layout.");
struct ExposureBiasMetadata {
    double value = 0.;
    int priority = 0; // 0: missing, 1: TIFF IFD, 2: actual EXIF IFD.
};

void ExposureBiasTag(void* context, int tag, int type, int length, unsigned int order,
                     void* input, INT64) noexcept {
    // ExposureBiasValue is EXIF 0x9204, SRATIONAL[1]. Do not use LibRaw's
    // BaselineExposure/ExposureCalibrationShift/FlashEC or processing brightness.
    // TIFF callbacks add (ifd + 1) << 20; exclude all maker-note tag namespaces.
    if (!context || !input || (static_cast<unsigned>(tag) & 0xfffffU) != 0x9204U ||
        type != 10 || length != 1 || (order != 0x4949 && order != 0x4d4d)) return;
    auto& result = *static_cast<ExposureBiasMetadata*>(context);
    const int priority = tag == 0x9204 ? 2 : 1;
    if (result.priority >= priority) return;
    auto* stream = static_cast<LibRaw_abstract_datastream*>(input);
    INT64 position = -1;
    try {
        position = stream->tell();
        if (position < 0) return;
        unsigned char bytes[8];
        const bool read = stream->read(bytes, 1, sizeof(bytes)) == sizeof(bytes);
        if (stream->seek(position, SEEK_SET) != 0 || !read) return;
        position = -1;
        auto word = [&](int offset) {
            std::uint32_t value = 0;
            for (int i = 0; i < 4; ++i)
                value = (value << 8) | bytes[offset + (order == 0x4949 ? 3 - i : i)];
            std::int32_t signed_value;
            std::memcpy(&signed_value, &value, sizeof(value));
            return signed_value;
        };
        const auto numerator = word(0), denominator = word(4);
        if (denominator == 0) return;
        const double value = static_cast<double>(numerator) / denominator;
        if (std::isfinite(value)) { result.value = value; result.priority = priority; }
    }
    catch (...) {
        // Optional metadata must not abort a valid RAW. Restore parser position even
        // when a short/corrupt datastream throws during the callback.
        if (position >= 0) { try { stream->seek(position, SEEK_SET); } catch (...) {} }
    }
}
struct RawImage {
    void* handle;
    void* pixels;
    int width, height, stride, bytes;
};
struct ImageOwner {
    libraw_processed_image_t* image = nullptr;
    std::vector<unsigned char> resized;
    ~ImageOwner() { if (image) LibRaw::dcraw_clear_mem(image); }
};
int Progress(void* context, enum LibRaw_progress, int, int) noexcept {
    auto cancel = static_cast<Cancel*>(context);
    return *cancel && (*cancel)() ? 1 : 0;
}
bool Cancelled(Cancel cancel) noexcept { return cancel && cancel(); }
void Configure(LibRaw& raw, Cancel* cancel) {
    auto& p = raw.imgdata.params;
    p.use_camera_wb = 1; p.use_auto_wb = 0; p.output_color = 1;
    p.output_bps = 8; p.half_size = 0; p.user_qual = 3;
    p.no_interpolation = 0; p.four_color_rgb = 0;
    p.user_flip = -1; // LibRaw applies the camera orientation once to RGB output.
    raw.set_progress_handler(Progress, cancel);
}
int Open(LibRaw& raw, const wchar_t* path, Cancel cancel) {
    if (!path || !*path) return InvalidArgument;
    if (Cancelled(cancel)) return LIBRAW_CANCELLED_BY_CALLBACK;
    if (LibRaw::versionNumber() != LIBRAW_MAKE_VERSION(0, 22, 3)) return WrongVersion;
    return raw.open_file(path); // Unicode, read-only LibRaw file stream.
}
}

#define API extern "C" __declspec(dllexport)

API const char* __cdecl bpr_error(int code) noexcept {
    switch (code) {
    case InvalidArgument: return "Invalid RAW decoder argument";
    case UnexpectedFailure: return "Unexpected RAW decoder failure";
    case InvalidOutput: return "RAW decoder did not return a valid RGB image";
    case WrongVersion: return "LibRaw 0.22.3 runtime is required";
    default: return libraw_strerror(code);
    }
}

API void __cdecl bpr_release(void* handle) noexcept { delete static_cast<ImageOwner*>(handle); }

static int ReadInfoCore(const wchar_t* path, Cancel cancel, RawInfo* info,
                        ExposureBiasMetadata* exposure) noexcept {
    if (!info) return InvalidArgument;
    *info = {};
    try {
        LibRaw raw;
        Configure(raw, &cancel);
        if (exposure) raw.set_exifparser_handler(ExposureBiasTag, exposure);
        int status = Open(raw, path, cancel);
        if (status != LIBRAW_SUCCESS) return status;
        status = raw.adjust_sizes_info_only(); // Metadata only; no unpack or demosaic.
        if (status != LIBRAW_SUCCESS) return status;
        if (Cancelled(cancel)) return LIBRAW_CANCELLED_BY_CALLBACK;
        info->width = raw.imgdata.sizes.iwidth; info->height = raw.imgdata.sizes.iheight;
        const int flip = raw.imgdata.sizes.flip;
        constexpr int exif[] = { 1, 2, 4, 3, 5, 8, 6, 7 };
        info->orientation = flip >= 0 && flip < 8 ? exif[flip] : 1;
        const auto& other = raw.imgdata.other;
        info->iso = other.iso_speed; info->shutter = other.shutter;
        info->aperture = other.aperture; info->focal = other.focal_len;
        info->timestamp = static_cast<std::int64_t>(other.timestamp);
        strncpy_s(info->camera, raw.imgdata.idata.model, _TRUNCATE);
        // LibRaw creates this timestamp with mktime in the process timezone.
        // Reverse with the same runtime's localtime to preserve the original
        // EXIF wall-clock fields, without assigning the photograph a timezone.
        if (other.timestamp > 0) {
            std::tm calendar{};
            if (localtime_s(&calendar, &other.timestamp) == 0)
                std::strftime(info->captured_at, sizeof(info->captured_at), "%Y-%m-%d %H:%M:%S", &calendar);
        }
        return info->width > 0 && info->height > 0 ? LIBRAW_SUCCESS : InvalidOutput;
    }
    catch (const std::bad_alloc&) { return LIBRAW_UNSUFFICIENT_MEMORY; }
    catch (...) { return UnexpectedFailure; }
}

API int __cdecl bpr_read_info(const wchar_t* path, Cancel cancel, RawInfo* info) noexcept {
    return ReadInfoCore(path, cancel, info, nullptr);
}

API int __cdecl bpr_read_info_v2(const wchar_t* path, Cancel cancel, RawInfoV2* info) noexcept {
    if (!info) return InvalidArgument;
    *info = {};
    ExposureBiasMetadata exposure;
    const int status = ReadInfoCore(path, cancel, &info->basic, &exposure);
    if (status == LIBRAW_SUCCESS && exposure.priority != 0) {
        info->exposure_bias = exposure.value;
        info->has_exposure_bias = 1;
    }
    return status;
}

API int __cdecl bpr_decode(const wchar_t* path, int max_width, Cancel cancel, RawImage* output) noexcept {
    if (!output || max_width < 0) return InvalidArgument;
    *output = {};
    try {
        auto owner = std::make_unique<ImageOwner>();
        {
            LibRaw raw;
            Configure(raw, &cancel);
            int status = Open(raw, path, cancel);
            if (status != LIBRAW_SUCCESS) return status;
            if (Cancelled(cancel)) return LIBRAW_CANCELLED_BY_CALLBACK;
            status = raw.unpack();
            if (status != LIBRAW_SUCCESS) return status;
            if (Cancelled(cancel)) return LIBRAW_CANCELLED_BY_CALLBACK;
            status = raw.dcraw_process();
            if (status != LIBRAW_SUCCESS) return status;
            if (Cancelled(cancel)) return LIBRAW_CANCELLED_BY_CALLBACK;
            owner->image = raw.dcraw_make_mem_image(&status);
            if (status != LIBRAW_SUCCESS) return status;
            if (!owner->image) return InvalidOutput;
        } // Release unpack/demosaic buffers before allocating a WPF output frame.
        const auto& source = *owner->image;
        const std::uint64_t required = std::uint64_t(source.width) * source.height * source.colors;
        if (source.type != LIBRAW_IMAGE_BITMAP || source.bits != 8 ||
            (source.colors != 3 && source.colors != 1) || !source.width || !source.height ||
            required > source.data_size || required > INT_MAX) return InvalidOutput;
        int width = source.width, height = source.height;
        if (max_width > 0 && width > max_width) {
            height = std::max(1, static_cast<int>(std::lround(static_cast<double>(height) * max_width / width)));
            width = max_width;
        }
        const std::uint64_t bytes = std::uint64_t(width) * height * 3;
        if (bytes > INT_MAX) return InvalidOutput;
        if (width != source.width || source.colors == 1) {
            owner->resized.resize(static_cast<size_t>(bytes));
            for (int y = 0; y < height; ++y) {
                if (Cancelled(cancel)) return LIBRAW_CANCELLED_BY_CALLBACK;
                const double sy = std::clamp((y + .5) * source.height / height - .5, 0., double(source.height - 1));
                const int y0 = static_cast<int>(sy), y1 = std::min(y0 + 1, int(source.height - 1));
                const double dy = sy - y0;
                for (int x = 0; x < width; ++x) {
                    const double sx = std::clamp((x + .5) * source.width / width - .5, 0., double(source.width - 1));
                    const int x0 = static_cast<int>(sx), x1 = std::min(x0 + 1, int(source.width - 1));
                    const double dx = sx - x0;
                    for (int c = 0; c < 3; ++c) {
                        const int channel = source.colors == 1 ? 0 : c;
                        auto sample = [&](int xx, int yy) { return source.data[(size_t(yy) * source.width + xx) * source.colors + channel]; };
                        const double a = sample(x0, y0) * (1 - dx) + sample(x1, y0) * dx;
                        const double b = sample(x0, y1) * (1 - dx) + sample(x1, y1) * dx;
                        owner->resized[(size_t(y) * width + x) * 3 + c] = static_cast<unsigned char>(std::clamp(std::lround(a * (1 - dy) + b * dy), 0L, 255L));
                    }
                }
            }
            LibRaw::dcraw_clear_mem(owner->image); owner->image = nullptr;
        }
        if (Cancelled(cancel)) return LIBRAW_CANCELLED_BY_CALLBACK;
        output->pixels = owner->image ? owner->image->data : owner->resized.data();
        output->width = width; output->height = height;
        output->stride = width * 3; output->bytes = static_cast<int>(bytes);
        output->handle = owner.release();
        return LIBRAW_SUCCESS;
    }
    catch (const std::bad_alloc&) { return LIBRAW_UNSUFFICIENT_MEMORY; }
    catch (...) { return UnexpectedFailure; }
}
