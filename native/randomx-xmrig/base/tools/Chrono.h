// Minimal stand-in for XMRig's base/tools/Chrono.h. crypto/randomx only uses
// Chrono::highResolutionMSecs() (in an AES self-timing path we don't exercise).
#ifndef XMRIG_CHRONO_STUB_H
#define XMRIG_CHRONO_STUB_H

#include <cstdint>
#include <chrono>

namespace xmrig {

class Chrono
{
public:
    static inline double highResolutionMSecs()
    {
        using namespace std::chrono;
        return static_cast<double>(duration_cast<nanoseconds>(high_resolution_clock::now().time_since_epoch()).count()) / 1e6;
    }

    static inline uint64_t steadyMSecs()
    {
        using namespace std::chrono;
        return static_cast<uint64_t>(duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count());
    }
};

} // namespace xmrig

#endif
