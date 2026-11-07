#pragma once
#include <stdint.h>

// 显式 UTF-8 C ABI，禁止跨边界传 C++ 类、std::string 或平台宽字符。
#ifdef _WIN32
# define BPR_API extern "C" __declspec(dllexport)
# define BPR_CALL __cdecl
#else
# define BPR_API extern "C" __attribute__((visibility("default")))
# define BPR_CALL
#endif
using Cancel = int(BPR_CALL*)();

// 所有目标都是 64 位；布局由编译端静态断言和托管 ABI 验证共同保护。
struct RawInfo {
    int width, height, orientation;
    double iso, shutter, aperture, focal;
    int64_t timestamp;
    char camera[128];
    char captured_at[32];
};
struct RawInfoV2 { RawInfo basic; double exposure_bias; int has_exposure_bias; };
struct RawImage { void* handle; void* pixels; int width, height, stride, bytes; };
struct FileIdentity { uint64_t device; uint64_t node; };

BPR_API int BPR_CALL bpr_get_abi_version() noexcept;
BPR_API int BPR_CALL bpr_read_info_utf8(const char*, Cancel, RawInfoV2*) noexcept;
BPR_API int BPR_CALL bpr_decode_utf8(const char*, int, Cancel, RawImage*) noexcept;
BPR_API void BPR_CALL bpr_release(void*) noexcept;
BPR_API const char* BPR_CALL bpr_error(int) noexcept;
BPR_API int BPR_CALL bpp_get_abi_version() noexcept;
BPR_API int BPR_CALL bpp_file_identity(intptr_t, FileIdentity*) noexcept;
// 返回 1=已入回收站，2=系统明确不支持，0=其他失败；失败不能被当作允许永久删除。
BPR_API int BPR_CALL bpp_trash_utf8(const char*, char*, int) noexcept;
