// Minimal stand-in for XMRig's backend/cpu/Cpu.h, providing only the ICpuInfo
// surface that crypto/randomx actually calls (arch/vendor + a handful of feature
// bits used to pick the JIT assembly path and AES/argon2 impl). The full XMRig
// ICpuInfo pulls in hwloc, Algorithm and CpuThreads; randomx needs none of that.
#ifndef XMRIG_CPU_STUB_H
#define XMRIG_CPU_STUB_H

#include <cstddef>
#include <cstdint>

#include "crypto/common/Assembly.h"

namespace xmrig {

class ICpuInfo
{
public:
    enum Vendor : uint32_t {
        VENDOR_UNKNOWN,
        VENDOR_INTEL,
        VENDOR_AMD
    };

    enum Arch : uint32_t {
        ARCH_UNKNOWN,
        ARCH_ZEN,
        ARCH_ZEN_PLUS,
        ARCH_ZEN2,
        ARCH_ZEN3,
        ARCH_ZEN4,
        ARCH_ZEN5
    };

    virtual ~ICpuInfo() = default;

    virtual Arch arch() const              = 0;
    virtual Vendor vendor() const          = 0;
    virtual size_t cores() const           = 0;
    virtual size_t threads() const         = 0;
    virtual bool hasAES() const            = 0;
    virtual bool hasAVX() const            = 0;
    virtual bool hasAVX2() const           = 0;
    virtual bool hasBMI2() const           = 0;
    virtual bool hasXOP() const            = 0;
    virtual bool hasRISCV_Vector() const   = 0;
    virtual bool jccErratum() const        = 0;

    // Which hand-written CryptoNight mainloop to JIT. GhostRider's CnHash::fn
    // resolves this per hash; randomx ignores it.
    virtual Assembly::Id assembly() const  = 0;
};

class Cpu
{
public:
    static ICpuInfo *info();

    inline static Assembly::Id assembly(Assembly::Id hint)
    {
        return hint == Assembly::AUTO ? Cpu::info()->assembly() : hint;
    }
};

} // namespace xmrig

#endif
