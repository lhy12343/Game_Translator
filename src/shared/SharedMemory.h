#pragma once
#include <cstdint>

// ============================================================
// 共享内存结构体 - 跨语言共享 (C++ / C# / Python)
// C# 侧用 [StructLayout(LayoutKind.Sequential)] 映射
// Python 侧用 ctypes.Structure 映射
// ============================================================

#define GT_BUFFER_SIZE 64       // 环形缓冲区容量
#define GT_MAX_TEXT_LEN 256     // 单条文本最大长度

// 文本状态
#define GT_STATUS_PENDING 0
#define GT_STATUS_TRANSLATING 1
#define GT_STATUS_DONE 2
#define GT_STATUS_FAILED 3

#pragma pack(push, 1)
struct SharedBuffer {
    // === 生产者: GameHook 写入 ===
    volatile uint32_t write_index;                        // 写入位置 (原子操作)
    struct {
        char text[GT_MAX_TEXT_LEN];                       // 原文 (UTF-8)
        uint32_t length;                                  // 文本长度
        uint64_t hash;                                    // FNV-1a hash (去重)
        volatile uint32_t status;                         // 翻译状态 (原子操作)
        char translated[GT_MAX_TEXT_LEN];                 // 译文 (UTF-8)
        uint32_t translated_length;                       // 译文长度
    } slots[GT_BUFFER_SIZE];

    // === 消费者: Translator 读取/写入 ===
    volatile uint32_t read_index;                         // 已读取位置 (原子操作)

    // === 统计 ===
    volatile uint64_t total_texts;                        // 总文本数
    volatile uint64_t cache_hits;                         // 缓存命中数
};
#pragma pack(pop)

// 同步策略:
// - 单生产者(GameHook)单消费者(Translator)模型
// - write_index / read_index / status 使用 InterlockedCompareExchange 原子操作
// - 读写前后加 _ReadWriteBarrier() / MemoryBarrier() 防止 CPU 乱序
// - 满队时丢弃最旧文本 (游戏文本时效性强)
