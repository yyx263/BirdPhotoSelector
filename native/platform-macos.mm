#include "bridge-api.h"
#import <Foundation/Foundation.h>
#include <cstdio>

// Foundation 自行选择当前卷的系统回收站。权限或其他 IO 错误返回失败，不能降级成永久删除。
BPR_API int BPR_CALL bpp_trash_utf8(const char* path, char* error, int capacity) noexcept {
    @autoreleasepool {
        if (!path || !error || capacity <= 0) return 0;
        error[0] = 0;
        @try {
            NSString* name = [NSString stringWithUTF8String:path];
            if (!name) { std::snprintf(error, capacity, "Invalid UTF-8 path"); return 0; }
            NSError* failure = nil;
            if ([[NSFileManager defaultManager] trashItemAtURL:[NSURL fileURLWithPath:name]
                                             resultingItemURL:nullptr error:&failure]) return 1;
            std::snprintf(error, capacity, "%s", failure.localizedDescription.UTF8String ?: "Trash operation failed");
            return [failure.domain isEqualToString:NSCocoaErrorDomain] && failure.code == NSFeatureUnsupportedError ? 2 : 0;
        } @catch (NSException* failure) {
            std::snprintf(error, capacity, "%s", failure.reason.UTF8String ?: "Foundation exception");
            return 0;
        }
    }
}
