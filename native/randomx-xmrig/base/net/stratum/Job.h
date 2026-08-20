// Minimal stand-in for XMRig's base/net/stratum/Job.h. crypto/randomx/randomx.cpp
// references only Job::kMaxBlobSize (to size a stack buffer in the unused
// randomx_calculate_commitment path). The full Job class is not needed here.
#ifndef XMRIG_JOB_STUB_H
#define XMRIG_JOB_STUB_H

#include <cstddef>

namespace xmrig {

class Job
{
public:
    static constexpr const size_t kMaxBlobSize = 408;
    static constexpr const size_t kMaxSeedSize = 32;
};

} // namespace xmrig

#endif
