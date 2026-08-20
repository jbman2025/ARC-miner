/* Stub for XMRig's base/io/log/Log.h.
 *
 * ghostrider.cpp only logs the three selected CryptoNight variants per job
 * (LOG_INFO under `verbose`). The shim always passes verbose=false, and we do
 * not vendor XMRig's logging stack, so these degrade to no-ops. Kept as a
 * header stub so the vendored ghostrider.cpp needs no edits.
 */
#ifndef ARC_COMPAT_LOG_H
#define ARC_COMPAT_LOG_H

#define LOG_INFO(...)  do {} while (0)
#define LOG_WARN(...)  do {} while (0)
#define LOG_ERR(...)   do {} while (0)
#define LOG_DEBUG(...) do {} while (0)
#define LOG_VERBOSE(...) do {} while (0)

#endif
