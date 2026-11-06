#pragma once
// 交叉链接只需这几个公开 Foundation 声明，不包含或替代 Foundation 的实现。
// 本机 macOS 构建采用 Xcode SDK；此小型导入表只供无 Xcode 的 Zig 交叉构建。
// API: developer.apple.com/documentation/foundation/nsfilemanager
#import <objc/NSObject.h>
@class NSError, NSURL;
@interface NSString : NSObject
+ (instancetype)stringWithUTF8String:(const char*)bytes;
@property (readonly) const char* UTF8String;
- (BOOL)isEqualToString:(NSString*)value;
@end
@interface NSURL : NSObject
+ (instancetype)fileURLWithPath:(NSString*)path;
@end
@interface NSError : NSObject
@property (readonly) NSString* localizedDescription;
@property (readonly) NSString* domain;
@property (readonly) NSInteger code;
@end
@interface NSFileManager : NSObject
+ (instancetype)defaultManager;
- (BOOL)trashItemAtURL:(NSURL*)url resultingItemURL:(NSURL**)result error:(NSError**)error;
@end
@interface NSException : NSObject
@property (readonly) NSString* reason;
@end
extern NSString* const NSCocoaErrorDomain;
// NSCocoaError.h 的公开错误码；仅明确不支持才允许回退，不能把其他错误当作授权。
static constexpr NSInteger NSFeatureUnsupportedError = 3328;
