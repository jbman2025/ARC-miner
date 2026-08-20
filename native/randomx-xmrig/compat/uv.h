/* Stub <uv.h> for the ghostrider_capi shim.
 *
 * XMRig's crypto/ghostrider/ghostrider.cpp includes <uv.h> unconditionally, but
 * every uv_* use sits inside `#ifdef XMRIG_FEATURE_HWLOC` (the helper-thread
 * scheduler). We build without HWLOC, so nothing here is ever referenced — this
 * header exists only so the vendored ghostrider.cpp stays byte-identical to
 * upstream instead of being patched.
 */
#ifndef ARC_COMPAT_UV_H
#define ARC_COMPAT_UV_H
#endif
