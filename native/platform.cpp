#include "bridge-api.h"
#ifdef _WIN32
# define NOMINMAX
# define WIN32_LEAN_AND_MEAN
# include <Windows.h>
#else
# include <sys/stat.h>
# include <unistd.h>
#endif

// 系统桥接无 LibRaw 依赖，文件操作不要求加载图像模型或 RAW 库。
BPR_API int BPR_CALL bpp_get_abi_version() noexcept { return 1; }

// 使用调用方已经打开的句柄获取实体身份，避免路径被替换带来的竞争；不取得句柄所有权。
BPR_API int BPR_CALL bpp_file_identity(intptr_t handle, FileIdentity* output) noexcept {
    if (!output) return 0;
    *output = {};
#ifdef _WIN32
    BY_HANDLE_FILE_INFORMATION info{};
    if (!GetFileInformationByHandle(reinterpret_cast<HANDLE>(handle), &info)) return 0;
    output->device = info.dwVolumeSerialNumber;
    output->node = (uint64_t(info.nFileIndexHigh) << 32) | info.nFileIndexLow;
#else
    struct stat info{};
    if (fstat(static_cast<int>(handle), &info) != 0) return 0;
    output->device = static_cast<uint64_t>(info.st_dev);
    output->node = static_cast<uint64_t>(info.st_ino);
#endif
    return output->node != 0 ? 1 : 0;
}
#ifndef __APPLE__
// Windows 和 Linux 分别由托管 Windows shell / GIO 实现回收站；此入口只在 macOS 使用。
BPR_API int BPR_CALL bpp_trash_utf8(const char*, char*, int) noexcept { return 2; }
#endif
