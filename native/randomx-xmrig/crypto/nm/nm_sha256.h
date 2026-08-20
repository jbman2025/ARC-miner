#ifndef NM_SHA256_H
#define NM_SHA256_H
#include <stdint.h>
#include <string.h>

typedef struct { uint32_t s[8]; uint64_t len; uint8_t buf[64]; size_t n; } nm_sha256_ctx;

static const uint32_t NM_K256[64] = {
0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2};

static inline uint32_t nm_ror(uint32_t x, int n){ return (x>>n)|(x<<(32-n)); }

static void nm_sha256_block(nm_sha256_ctx*c, const uint8_t*p){
    uint32_t w[64];
    for(int i=0;i<16;i++) w[i]=(p[i*4]<<24)|(p[i*4+1]<<16)|(p[i*4+2]<<8)|p[i*4+3];
    for(int i=16;i<64;i++){
        uint32_t s0=nm_ror(w[i-15],7)^nm_ror(w[i-15],18)^(w[i-15]>>3);
        uint32_t s1=nm_ror(w[i-2],17)^nm_ror(w[i-2],19)^(w[i-2]>>10);
        w[i]=w[i-16]+s0+w[i-7]+s1;
    }
    uint32_t a=c->s[0],b=c->s[1],cc=c->s[2],d=c->s[3],e=c->s[4],f=c->s[5],g=c->s[6],h=c->s[7];
    for(int i=0;i<64;i++){
        uint32_t S1=nm_ror(e,6)^nm_ror(e,11)^nm_ror(e,25);
        uint32_t ch=(e&f)^((~e)&g);
        uint32_t t1=h+S1+ch+NM_K256[i]+w[i];
        uint32_t S0=nm_ror(a,2)^nm_ror(a,13)^nm_ror(a,22);
        uint32_t maj=(a&b)^(a&cc)^(b&cc);
        uint32_t t2=S0+maj;
        h=g;g=f;f=e;e=d+t1;d=cc;cc=b;b=a;a=t1+t2;
    }
    c->s[0]+=a;c->s[1]+=b;c->s[2]+=cc;c->s[3]+=d;c->s[4]+=e;c->s[5]+=f;c->s[6]+=g;c->s[7]+=h;
}

static void nm_sha256_init(nm_sha256_ctx*c){
    c->s[0]=0x6a09e667;c->s[1]=0xbb67ae85;c->s[2]=0x3c6ef372;c->s[3]=0xa54ff53a;
    c->s[4]=0x510e527f;c->s[5]=0x9b05688c;c->s[6]=0x1f83d9ab;c->s[7]=0x5be0cd19;
    c->len=0;c->n=0;
}
static void nm_sha256_update(nm_sha256_ctx*c, const uint8_t*p, size_t len){
    c->len+=len;
    while(len){
        size_t k=64-c->n; if(k>len)k=len;
        memcpy(c->buf+c->n,p,k); c->n+=k; p+=k; len-=k;
        if(c->n==64){ nm_sha256_block(c,c->buf); c->n=0; }
    }
}
static void nm_sha256_final(nm_sha256_ctx*c, uint8_t out[32]){
    uint64_t bits=c->len*8;
    uint8_t pad=0x80; nm_sha256_update(c,&pad,1);
    uint8_t z=0; while(c->n!=56) nm_sha256_update(c,&z,1);
    uint8_t L[8]; for(int i=0;i<8;i++) L[i]=(uint8_t)(bits>>(56-8*i));
    nm_sha256_update(c,L,8);
    for(int i=0;i<8;i++){ out[i*4]=c->s[i]>>24;out[i*4+1]=c->s[i]>>16;out[i*4+2]=c->s[i]>>8;out[i*4+3]=c->s[i]; }
}

static void nm_sha256(const uint8_t*p, size_t len, uint8_t out[32]){
    nm_sha256_ctx c; nm_sha256_init(&c); nm_sha256_update(&c,p,len); nm_sha256_final(&c,out);
}
#endif
