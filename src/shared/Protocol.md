# 共享内存结构体定义
# 跨 C# / C++ / Python 共享
# C# 侧用 StructLayout(LayoutKind.Sequential) 映射

BUFFER_SIZE = 64        # 环形缓冲区容量
MAX_TEXT_LEN = 256      # 单条文本最大长度

# 文本状态
TEXT_STATUS_PENDING = 0     # 待翻译
TEXT_STATUS_TRANSLATING = 1 # 翻译中
TEXT_STATUS_DONE = 2        # 翻译完成
TEXT_STATUS_FAILED = 3      # 翻译失败，用原文
