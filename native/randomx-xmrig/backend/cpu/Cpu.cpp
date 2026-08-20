// CPUID-based implementation of the minimal ICpuInfo used by crypto/randomx.
// Enough to drive the correct JIT/AES path on x86-64 (the RandomX hot loop only
// cares about vendor/arch + AES/AVX2/BMI2/XOP and the Intel jcc erratum flag).
#include "backend/cpu/Cpu.h"

#include <thread>
#include <cstring>

#if defined(_MSC_VER)
#   include <intrin.h>
static inline void cpuid(uint32_t leaf, uint32_t sub, uint32_t out[4]) {
    int r[4];
    __cpuidex(r, (int)leaf, (int)sub);
    out[0] = (uint32_t)r[0]; out[1] = (uint32_t)r[1]; out[2] = (uint32_t)r[2]; out[3] = (uint32_t)r[3];
}
#else
#   include <cpuid.h>
static inline void cpuid(uint32_t leaf, uint32_t sub, uint32_t out[4]) {
    __cpuid_count(leaf, sub, out[0], out[1], out[2], out[3]);
}
#endif

namespace xmrig {

class BasicCpuInfo : public ICpuInfo
{
public:
    BasicCpuInfo()
    {
        uint32_t r[4];

        // Vendor string (leaf 0, EBX,EDX,ECX).
        cpuid(0, 0, r);
        char v[13] = {0};
        std::memcpy(v + 0, &r[1], 4);
        std::memcpy(v + 4, &r[3], 4);
        std::memcpy(v + 8, &r[2], 4);
        if      (std::memcmp(v, "AuthenticAMD", 12) == 0) m_vendor = VENDOR_AMD;
        else if (std::memcmp(v, "GenuineIntel", 12) == 0) m_vendor = VENDOR_INTEL;

        // Family/model (leaf 1).
        cpuid(1, 0, r);
        const uint32_t eax    = r[0];
        const uint32_t ecx    = r[2];
        uint32_t family       = (eax >> 8) & 0xF;
        uint32_t model        = (eax >> 4) & 0xF;
        const uint32_t extFam = (eax >> 20) & 0xFF;
        const uint32_t extMod = (eax >> 16) & 0xF;
        if (family == 0xF) family += extFam;
        if (family == 0x6 || family == 0xF) model += (extMod << 4);

        m_aes = (ecx & (1u << 25)) != 0;
        m_avx = (ecx & (1u << 28)) != 0;

        // Leaf 7: AVX2 (EBX bit 5), BMI2 (EBX bit 8).
        cpuid(7, 0, r);
        m_avx2 = (r[1] & (1u << 5)) != 0;
        m_bmi2 = (r[1] & (1u << 8)) != 0;

        // Extended leaf: XOP (0x80000001 ECX bit 11, AMD only).
        cpuid(0x80000000, 0, r);
        if (r[0] >= 0x80000001) {
            cpuid(0x80000001, 0, r);
            m_xop = (r[2] & (1u << 11)) != 0;
        }

        // Map AMD family/model → Zen arch (drives the Ryzen JIT assembly path).
        if (m_vendor == VENDOR_AMD) {
            m_assembly = (family >= 0x17) ? Assembly::RYZEN : Assembly::BULLDOZER;
            if (family == 0x17) {
                m_arch = (model <= 0x1F) ? ARCH_ZEN : (model <= 0x2F ? ARCH_ZEN_PLUS : ARCH_ZEN2);
            }
            else if (family == 0x19) {
                // Zen4 = models 0x10-0x1F and 0x60-0x7F; everything else in 19h is Zen3.
                m_arch = ((model >= 0x10 && model <= 0x1F) || (model >= 0x60 && model <= 0x7F)) ? ARCH_ZEN4 : ARCH_ZEN3;
            }
            else if (family >= 0x1A) {
                m_arch = ARCH_ZEN5;
            }
        }
        else if (m_vendor == VENDOR_INTEL) {
            m_assembly = Assembly::INTEL;
        }

        if (m_vendor == VENDOR_INTEL && family == 0x6) {
            // Intel jcc erratum: Skylake/Kaby/Coffee/Comet/Cascade/Cooper Lake families.
            static const uint32_t erratumModels[] = {
                0x4E, 0x5E, 0x8E, 0x9E, 0xA5, 0xA6, 0x66, 0x55, 0x6A, 0x6C, 0x7D, 0x7E
            };
            for (uint32_t em : erratumModels) {
                if (model == em) { m_jccErratum = true; break; }
            }
        }

        m_threads = std::thread::hardware_concurrency();
        if (m_threads == 0) m_threads = 1;
        // Assume SMT on Zen/Intel-HT parts; only used for dataset-init thread math.
        m_cores = m_threads > 1 ? m_threads / 2 : 1;
    }

    Arch arch() const override            { return m_arch; }
    Vendor vendor() const override        { return m_vendor; }
    size_t cores() const override         { return m_cores; }
    size_t threads() const override       { return m_threads; }
    bool hasAES() const override          { return m_aes; }
    bool hasAVX() const override          { return m_avx; }
    bool hasAVX2() const override         { return m_avx2; }
    bool hasBMI2() const override         { return m_bmi2; }
    bool hasXOP() const override          { return m_xop; }
    bool hasRISCV_Vector() const override { return false; }
    bool jccErratum() const override      { return m_jccErratum; }
    Assembly::Id assembly() const override { return m_assembly; }

private:
    Assembly::Id m_assembly = Assembly::NONE;
    Arch m_arch       = ARCH_UNKNOWN;
    Vendor m_vendor   = VENDOR_UNKNOWN;
    size_t m_cores    = 1;
    size_t m_threads  = 1;
    bool m_aes        = false;
    bool m_avx        = false;
    bool m_avx2       = false;
    bool m_bmi2       = false;
    bool m_xop        = false;
    bool m_jccErratum = false;
};

ICpuInfo *Cpu::info()
{
    static BasicCpuInfo instance;
    return &instance;
}

} // namespace xmrig
