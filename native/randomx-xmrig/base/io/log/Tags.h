/* Stub for XMRig's base/io/log/Tags.h — see Log.h. Only Tags::cpu() is used,
 * and only as a LOG_INFO argument, which our Log.h compiles away.
 */
#ifndef ARC_COMPAT_TAGS_H
#define ARC_COMPAT_TAGS_H

namespace xmrig {

class Tags
{
public:
    static inline const char* cpu() { return "cpu"; }
};

} // namespace xmrig

#endif
