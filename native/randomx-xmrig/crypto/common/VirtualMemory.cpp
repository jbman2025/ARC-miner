// Win32 implementation of the minimal VirtualMemory helpers used by crypto/randomx.
// Large-page allocation needs the SeLockMemoryPrivilege, which we enable on first
// use (same requirement as the previous RandomX backend's huge-page path).
#include "crypto/common/VirtualMemory.h"

#if defined(_WIN32)

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

namespace {

// Enable SeLockMemoryPrivilege for this process (idempotent). Without it, large
// page VirtualAlloc fails and the caller falls back to normal pages.
bool enableLockMemoryPrivilege()
{
    static int cached = -1;   // -1 unknown, 0 failed, 1 ok
    if (cached >= 0) return cached == 1;

    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &token)) {
        cached = 0; return false;
    }

    TOKEN_PRIVILEGES tp{};
    tp.PrivilegeCount = 1;
    tp.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
    bool ok = false;
    if (LookupPrivilegeValueW(nullptr, L"SeLockMemoryPrivilege", &tp.Privileges[0].Luid)) {
        AdjustTokenPrivileges(token, FALSE, &tp, 0, nullptr, nullptr);
        ok = (GetLastError() == ERROR_SUCCESS);
    }
    CloseHandle(token);
    cached = ok ? 1 : 0;
    return ok;
}

} // namespace

namespace xmrig {

size_t VirtualMemory::hugePageSize()
{
    return GetLargePageMinimum();
}

void *VirtualMemory::allocateExecutableMemory(size_t size, bool hugePages)
{
    if (hugePages) {
        const size_t hp = GetLargePageMinimum();
        if (hp && enableLockMemoryPrivilege()) {
            const size_t rounded = ((size - 1) / hp + 1) * hp;
            void *mem = VirtualAlloc(nullptr, rounded, MEM_COMMIT | MEM_RESERVE | MEM_LARGE_PAGES, PAGE_EXECUTE_READWRITE);
            if (mem) return mem;
        }
    }
    // Normal executable pages (also the fallback if large pages are unavailable).
    return VirtualAlloc(nullptr, size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
}

void *VirtualMemory::allocateLargePagesMemory(size_t size)
{
    const size_t hp = GetLargePageMinimum();
    if (!hp || !enableLockMemoryPrivilege()) return nullptr;

    const size_t rounded = ((size - 1) / hp + 1) * hp;
    return VirtualAlloc(nullptr, rounded, MEM_COMMIT | MEM_RESERVE | MEM_LARGE_PAGES, PAGE_READWRITE);
}

void VirtualMemory::freeLargePagesMemory(void *p, size_t)
{
    if (p) VirtualFree(p, 0, MEM_RELEASE);
}

void VirtualMemory::flushInstructionCache(void *p, size_t size)
{
    ::FlushInstructionCache(GetCurrentProcess(), p, size);
}

bool VirtualMemory::protectRW(void *p, size_t size)
{
    DWORD oldp;
    return VirtualProtect(p, size, PAGE_READWRITE, &oldp) != 0;
}

bool VirtualMemory::protectRWX(void *p, size_t size)
{
    DWORD oldp;
    return VirtualProtect(p, size, PAGE_EXECUTE_READWRITE, &oldp) != 0;
}

bool VirtualMemory::protectRX(void *p, size_t size)
{
    DWORD oldp;
    return VirtualProtect(p, size, PAGE_EXECUTE_READ, &oldp) != 0;
}

} // namespace xmrig

#else // ---- POSIX (Linux) ----------------------------------------------------

#include <sys/mman.h>
#include <unistd.h>
#include <cstdlib>
#include <unordered_map>
#include <mutex>

namespace {

// Track the exact mmap length behind each pointer we hand out, so free() can
// munmap precisely (huge and normal mappings need the length back).
std::unordered_map<void*, size_t>& mmapLengths() { static std::unordered_map<void*, size_t> m; return m; }
std::mutex& mmapMutex() { static std::mutex m; return m; }

constexpr size_t kHugePageSize = 2u * 1024u * 1024u;   // x86-64 default hugepage

size_t roundUp(size_t v, size_t a) { return ((v - 1) / a + 1) * a; }

} // namespace

namespace xmrig {

size_t VirtualMemory::hugePageSize()
{
    return kHugePageSize;
}

void *VirtualMemory::allocateExecutableMemory(size_t size, bool hugePages)
{
    const int prot = PROT_READ | PROT_WRITE | PROT_EXEC;
    void *mem = MAP_FAILED;
    if (hugePages) {
        const size_t rounded = roundUp(size, kHugePageSize);
        mem = mmap(nullptr, rounded, prot, MAP_PRIVATE | MAP_ANONYMOUS | MAP_HUGETLB, -1, 0);
        if (mem != MAP_FAILED) {
            std::lock_guard<std::mutex> lk(mmapMutex()); mmapLengths()[mem] = rounded; return mem;
        }
    }
    const size_t rounded = roundUp(size, (size_t)sysconf(_SC_PAGESIZE));
    mem = mmap(nullptr, rounded, prot, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (mem == MAP_FAILED) return nullptr;
    std::lock_guard<std::mutex> lk(mmapMutex()); mmapLengths()[mem] = rounded; return mem;
}

void *VirtualMemory::allocateLargePagesMemory(size_t size)
{
    const size_t rounded = roundUp(size, kHugePageSize);
    void *mem = mmap(nullptr, rounded, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS | MAP_HUGETLB, -1, 0);
    if (mem == MAP_FAILED) return nullptr;   // caller falls back to normal pages
    std::lock_guard<std::mutex> lk(mmapMutex()); mmapLengths()[mem] = rounded; return mem;
}

void VirtualMemory::freeLargePagesMemory(void *p, size_t)
{
    if (!p) return;
    size_t len = 0;
    { std::lock_guard<std::mutex> lk(mmapMutex()); auto it = mmapLengths().find(p); if (it != mmapLengths().end()) { len = it->second; mmapLengths().erase(it); } }
    if (len) munmap(p, len);
}

void VirtualMemory::flushInstructionCache(void *p, size_t size)
{
    __builtin___clear_cache(reinterpret_cast<char*>(p), reinterpret_cast<char*>(p) + size);
}

namespace {
// mprotect requires a page-aligned address and page-multiple length (unlike
// Win32 VirtualProtect, which the JIT relies on by passing unaligned code ptrs).
// Align the start down and extend the length to cover the requested range.
bool protectRange(void *p, size_t size, int prot)
{
    const size_t pageSize = (size_t)sysconf(_SC_PAGESIZE);
    const uintptr_t start = reinterpret_cast<uintptr_t>(p);
    const uintptr_t aligned = start & ~(uintptr_t)(pageSize - 1);
    size_t len = size + (start - aligned);
    len = roundUp(len, pageSize);
    return mprotect(reinterpret_cast<void*>(aligned), len, prot) == 0;
}
} // namespace

bool VirtualMemory::protectRW(void *p, size_t size)  { return protectRange(p, size, PROT_READ | PROT_WRITE); }
bool VirtualMemory::protectRWX(void *p, size_t size) { return protectRange(p, size, PROT_READ | PROT_WRITE | PROT_EXEC); }
bool VirtualMemory::protectRX(void *p, size_t size)  { return protectRange(p, size, PROT_READ | PROT_EXEC); }

} // namespace xmrig

#endif // _WIN32 / POSIX
