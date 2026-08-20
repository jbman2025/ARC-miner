// csd_fused_check.cpp — validate the CSD SYCL sha256d kernel against a CPU
// reference on randomized 84-byte headers. Gate: the kernel does not ship until
// this passes. Also checks the well-known SHA-256 test vectors on the host code.
#include <sycl/sycl.hpp>
#include "csd_sha256d.hpp"
#include <cstdio>
#include <cstring>
#include <random>
#include <array>

// ---- host reference sha256 / sha256d ----
static const uint32_t HK[64] = {
    0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
    0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
    0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
    0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
    0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
    0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
    0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
    0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2};
static inline uint32_t rr(uint32_t x,int n){return (x>>n)|(x<<(32-n));}
static void hblock(uint32_t st[8], const uint8_t* p){
    uint32_t w[64];
    for(int i=0;i<16;++i) w[i]=(uint32_t(p[4*i])<<24)|(uint32_t(p[4*i+1])<<16)|(uint32_t(p[4*i+2])<<8)|p[4*i+3];
    for(int i=16;i<64;++i){uint32_t s0=rr(w[i-15],7)^rr(w[i-15],18)^(w[i-15]>>3);uint32_t s1=rr(w[i-2],17)^rr(w[i-2],19)^(w[i-2]>>10);w[i]=w[i-16]+s0+w[i-7]+s1;}
    uint32_t a=st[0],b=st[1],c=st[2],d=st[3],e=st[4],f=st[5],g=st[6],h=st[7];
    for(int i=0;i<64;++i){uint32_t S1=rr(e,6)^rr(e,11)^rr(e,25);uint32_t ch=(e&f)^(~e&g);uint32_t t1=h+S1+ch+HK[i]+w[i];uint32_t S0=rr(a,2)^rr(a,13)^rr(a,22);uint32_t mj=(a&b)^(a&c)^(b&c);uint32_t t2=S0+mj;h=g;g=f;f=e;e=d+t1;d=c;c=b;b=a;a=t1+t2;}
    st[0]+=a;st[1]+=b;st[2]+=c;st[3]+=d;st[4]+=e;st[5]+=f;st[6]+=g;st[7]+=h;
}
static void hsha(const uint8_t* m, size_t len, uint8_t out[32]){
    uint32_t st[8]={0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19};
    std::vector<uint8_t> v(m,m+len); v.push_back(0x80); while(v.size()%64!=56) v.push_back(0);
    uint64_t bl=uint64_t(len)*8; for(int i=7;i>=0;--i) v.push_back(uint8_t(bl>>(8*i)));
    for(size_t o=0;o<v.size();o+=64) hblock(st,v.data()+o);
    for(int i=0;i<8;++i){out[4*i]=st[i]>>24;out[4*i+1]=st[i]>>16;out[4*i+2]=st[i]>>8;out[4*i+3]=st[i];}
}
static void hsha_d(const uint8_t* m,size_t len,uint8_t o[32]){uint8_t t[32];hsha(m,len,t);hsha(t,32,o);}

int main(){
    sycl::queue q{sycl::gpu_selector_v, sycl::property::queue::in_order{}};
    std::printf("device: %s\n", q.get_device().get_info<sycl::info::device::name>().c_str());
    int fails=0, checks=0;
    auto rep=[&](bool ok,const char* w){ ++checks; if(!ok){++fails; std::printf("FAIL  %s\n",w);} else std::printf("PASS  %s\n",w); };

    // host SHA-256 sanity: "abc"
    { uint8_t o[32]; hsha((const uint8_t*)"abc",3,o);
      const char* want="ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
      char got[65]; for(int i=0;i<32;++i) sprintf(got+2*i,"%02x",o[i]);
      rep(std::string(got)==want, "host sha256(\"abc\") == NIST vector"); }

    // Randomized 84-byte headers: GPU kernel vs host sha256d, and the target gate.
    std::mt19937 rng(0xC5D);
    uint32_t* found=sycl::malloc_shared<uint32_t>(64,q);
    uint32_t* cnt=sycl::malloc_shared<uint32_t>(1,q);
    int mism=0, gate_mism=0;
    for(int trial=0; trial<64; ++trial){
        uint8_t hdr[84]; for(int i=0;i<84;++i) hdr[i]=rng()&0xff;
        uint32_t nonce = rng();
        hdr[80]=nonce; hdr[81]=nonce>>8; hdr[82]=nonce>>16; hdr[83]=nonce>>24; // nonce LE in header
        // host digest of this exact header
        uint8_t hd[32]; hsha_d(hdr,84,hd);
        // build kernel inputs: midstate over hdr[0..64], tail words 16..19, W[4]=bswap(nonce_le)=BE read
        uint32_t mid[8]={0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19};
        hblock(mid,hdr);
        auto be=[&](const uint8_t* p){ return (uint32_t(p[0])<<24)|(uint32_t(p[1])<<16)|(uint32_t(p[2])<<8)|p[3]; };
        std::array<uint32_t,8> midA; for(int i=0;i<8;++i) midA[i]=mid[i];
        std::array<uint32_t,5> tail{ be(hdr+64), be(hdr+68), be(hdr+72), be(hdr+76), 0 };
        uint32_t wnonce = be(hdr+80); // big-endian read of the header nonce bytes = kernel W[4]
        // Target set so ONLY this nonce's digest passes: target = host digest (BE words).
        std::array<uint32_t,8> tgt; for(int i=0;i<8;++i) tgt[i]=be(hd+4*i);
        // search a tiny window [wnonce, wnonce+1): must find exactly this nonce.
        cnt[0]=0;
        csd::run_search(q, midA, tail, tgt, wnonce, 1, found, 64, cnt);
        if(cnt[0]!=1 || found[0]!=wnonce) ++mism;
        // gate check: a target one-below the digest must REJECT it.
        std::array<uint32_t,8> tgt2=tgt; // subtract 1 (borrow) from the 256-bit value
        for(int i=7;i>=0;--i){ if(tgt2[i]--!=0) break; }
        cnt[0]=0; csd::run_search(q, midA, tail, tgt2, wnonce, 1, found, 64, cnt);
        if(cnt[0]!=0) ++gate_mism;
    }
    rep(mism==0, "GPU sha256d == host over 64 random headers (exact-target hit)");
    rep(gate_mism==0, "GPU gate rejects digest > target (64 random headers)");

    sycl::free(found,q); sycl::free(cnt,q);
    std::printf(fails? "\nFUSED CHECK FAILED (%d/%d)\n" : "\nFUSED CHECK OK (%d/%d checks passed)\n", checks-fails, checks);
    return fails?1:0;
}
