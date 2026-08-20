// Minimal stand-in for XMRig's crypto/common/VirtualMemory.h, exposing only the
// static allocation/protection helpers that crypto/randomx calls. Backed by a
// small Win32 implementation (VirtualMemory.cpp).
#ifndef XMRIG_VIRTUALMEMORY_STUB_H
#define XMRIG_VIRTUALMEMORY_STUB_H

#include <cstddef>
#include <cstdint>

namespace xmrig {

class VirtualMemory
{
public:
    static void *allocateExecutableMemory(size_t size, bool hugePages);
    static void *allocateLargePagesMemory(size_t size);
    static void freeLargePagesMemory(void *p, size_t size);

    static void flushInstructionCache(void *p, size_t size);
    static inline void flushInstructionCache(void *p1, void *p2)
    {
        flushInstructionCache(p1, static_cast<uint8_t *>(p2) - static_cast<uint8_t *>(p1));
    }

    static bool protectRW(void *p, size_t size);
    static bool protectRWX(void *p, size_t size);
    static bool protectRX(void *p, size_t size);

    static size_t hugePageSize();

    static inline constexpr size_t align(size_t pos, size_t alignment = 2u * 1024u * 1024u)
    {
        return ((pos - 1) / alignment + 1) * alignment;
    }
};

} // namespace xmrig

#endif
