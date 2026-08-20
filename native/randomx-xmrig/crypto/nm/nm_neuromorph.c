#if defined(__GNUC__) && !defined(__clang__)
#pragma GCC optimize("no-tree-vectorize")
#endif
#include "nm_neuromorph.h"
#include "nm_sha256.h"
#include <stdlib.h>
#include <string.h>
#include <math.h>

enum { opIADD, opIMUL, opIMULH, opIXOR, opIROTR, opINEG, opFADD, opFMUL,
       opFDIV, opFSQRT, opLOAD, opSTORE, opCBRANCH, opAESR, opXDOM };

static inline uint64_t le64(const uint8_t *b){
    uint64_t v=0; for(int i=0;i<8;i++) v|=(uint64_t)b[i]<<(8*i); return v;
}
static inline void put64(uint8_t *b, uint64_t v){ for(int i=0;i<8;i++) b[i]=(uint8_t)(v>>(8*i)); }
static inline uint32_t le32(const uint8_t *b){
    return (uint32_t)b[0]|((uint32_t)b[1]<<8)|((uint32_t)b[2]<<16)|((uint32_t)b[3]<<24);
}
static inline double bits2d(uint64_t x){ double d; memcpy(&d,&x,8); return d; }
static inline uint64_t d2bits(double d){ uint64_t x; memcpy(&x,&d,8); return x; }

static inline double normFloat(uint64_t x){
    uint64_t b = ((uint64_t)1023<<52) | (x & 0x000FFFFFFFFFFFFFULL);
    return bits2d(b);
}

static inline uint64_t rotl64(uint64_t x, int k){
    unsigned s = ((unsigned)k) & 63u;
    if (s==0) return x;
    return (x<<s) | (x>>(64-s));
}
/* ARC PATCH 1/2 (MSVC): upstream uses `unsigned __int128`, a GCC/Clang
 * extension. MSVC has no 128-bit integer type; __umulh is its intrinsic for the
 * high half of a 64x64 multiply. Same value, no behaviour change. */
#if defined(_MSC_VER) && !defined(__GNUC__)
#include <intrin.h>
static inline uint64_t mulhi64(uint64_t a, uint64_t b){
    return __umulh(a, b);
}
#else
static inline uint64_t mulhi64(uint64_t a, uint64_t b){
    return (uint64_t)(((unsigned __int128)a * (unsigned __int128)b) >> 64);
}
#endif

static int alloc_thread_buffers(nm_ctx *c){
    c->scratch = (uint64_t*)malloc((size_t)NM_SCRATCH_WORDS*8);
    c->prog    = (nm_instr*)malloc((size_t)NM_PROG_MAX*sizeof(nm_instr));
    c->taken   = (uint8_t*)malloc(NM_PROG_MAX);
    c->owns_scratch = 1;
    return (c->scratch && c->prog && c->taken) ? 0 : -1;
}

int nm_ctx_init(nm_ctx *c){
    memset(c, 0, sizeof *c);
    if(alloc_thread_buffers(c)) return -1;
    c->dataset = (uint64_t*)malloc((size_t)NM_DATASET_WORDS*8);
    if(!c->dataset) return -1;
    c->owns_dataset  = 1;
    c->dataset_valid = 0;
    return 0;
}

int nm_ctx_init_shared(nm_ctx *c){
    memset(c, 0, sizeof *c);
    if(alloc_thread_buffers(c)) return -1;
    c->dataset       = NULL;
    c->owns_dataset  = 0;
    c->dataset_valid = 0;
    return 0;
}

void nm_ctx_attach_scratch(nm_ctx *c, void *scratch){
    if(c->owns_scratch) free(c->scratch);
    c->scratch = (uint64_t*)scratch;
    c->owns_scratch = 0;
}

void nm_ctx_free(nm_ctx *c){
    if(c->owns_scratch) free(c->scratch);
    free(c->prog); free(c->taken);
    if(c->owns_dataset) free(c->dataset);
    memset(c,0,sizeof *c);
}

void nm_build_dataset(uint64_t *dataset, const uint8_t dataset_key[16]){
    nm_aes128 a; nm_aes128_expand(&a, dataset_key);
    uint8_t stage[128]={0}, outs[128];
    for(uint32_t i=0;i<NM_DATASET_WORDS;i+=16){
        for(int j=0;j<8;j++) put64(stage+16*j, (uint64_t)(i+2*j));
        nm_aes128_encrypt8(&a, stage, outs);
        for(int j=0;j<8;j++){ dataset[i+2*j]=le64(outs+16*j); dataset[i+2*j+1]=le64(outs+16*j+8); }
    }
}

void nm_ctx_set_params(nm_ctx *c, const uint8_t seed[32]){
    nm_derive_params(seed, &c->params);
    nm_aes128_expand(&c->aes, c->params.aes_key);
}

void nm_ctx_attach_dataset(nm_ctx *c, uint64_t *dataset, const uint8_t key[16]){
    c->dataset = dataset;
    memcpy(c->dataset_key, key, 16);
    c->dataset_valid = 1;
}

void nm_ctx_set_seed(nm_ctx *c, const uint8_t seed[32]){
    nm_ctx_set_params(c, seed);
    if(c->owns_dataset &&
       (!c->dataset_valid || memcmp(c->dataset_key, c->params.dataset_key, 16)!=0)){
        nm_build_dataset(c->dataset, c->params.dataset_key);
        memcpy(c->dataset_key, c->params.dataset_key, 16);
        c->dataset_valid = 1;
    }
}

static void fill_scratch(nm_ctx *c, const uint8_t seed[32]){
    uint8_t key[32], tmp[33];
    memcpy(tmp, seed, 32); tmp[32]=0x53;
    nm_sha256(tmp, 33, key);
    nm_aes128 a; nm_aes128_expand(&a, key);
    uint8_t stage[128], outs[128];
    for(int j=0;j<8;j++) memcpy(stage+16*j+8, key+24, 8);
    for(uint32_t i=0;i<NM_SCRATCH_WORDS;i+=16){
        for(int j=0;j<8;j++) put64(stage+16*j, (uint64_t)(i+2*j));
        nm_aes128_encrypt8(&a, stage, outs);
        for(int j=0;j<8;j++){ c->scratch[i+2*j]=le64(outs+16*j); c->scratch[i+2*j+1]=le64(outs+16*j+8); }
    }
}

static void gen_program(nm_ctx *c, const uint8_t seed[32]){
    uint8_t key[32], tmp[33];
    memcpy(tmp, seed, 32); tmp[32]=0x50;
    nm_sha256(tmp, 33, key);
    nm_aes128 a; nm_aes128_expand(&a, key);
    int progSize = c->params.prog_size;
    uint8_t stream[NM_PROG_MAX*8];
    uint8_t in[16], out[16];
    memcpy(in, key+16, 16);
    int slen=0, need=progSize*8;
    while(slen < need){
        nm_aes128_encrypt(&a, in, out);
        memcpy(stream+slen, out, 16); slen+=16;
        memcpy(in, out, 16);
        put64(in, le64(in)+1);
    }
    for(int i=0;i<progSize;i++){
        const uint8_t *b = stream + i*8;
        c->prog[i].op  = c->params.op_table[b[0]];
        c->prog[i].dst = b[1];
        c->prog[i].src = b[2];
        c->prog[i].imm = le32(b+4);
    }
}

void nm_hash(nm_ctx *c, const uint8_t *header, uint64_t height, uint8_t out[32]){
    const nm_params *p = &c->params;
    uint8_t seed[32], tmp[10+NM_HEADER_LEN];
    memcpy(tmp, "nm-seed|", 8);
    memcpy(tmp+8, header, NM_HEADER_LEN);
    nm_sha256(tmp, 8+NM_HEADER_LEN, seed);

    int useDS = height >= NM_DATASET_HEIGHT;

    fill_scratch(c, seed);
    gen_program(c, seed);

    uint64_t r[16]; double f[8];
    for(int i=0;i<4;i++)  r[i]=le64(seed+i*8);
    for(int i=4;i<16;i++) r[i]=c->scratch[i]^p->rot_salt;
    for(int i=0;i<8;i++)  f[i]=normFloat(c->scratch[16+i]);

    uint8_t aesIn[16], aesOut[16];
    int progSize=p->prog_size;
    uint64_t branchMask=p->branch_mask, rotSalt=p->rot_salt;

    for(int loop=0; loop<p->loops; loop++){
        memset(c->taken, 0, progSize);
        int pc=0;
        while(pc < progSize){
            nm_instr *ins=&c->prog[pc];
            int d = ins->dst & 15, s = ins->src & 15;
            uint32_t imm = ins->imm;
            switch(ins->op){
            case opIADD:  r[d] += r[s] + (uint64_t)imm; break;
            case opIMUL:  r[d] *= r[s] | 1ULL; break;
            case opIMULH: r[d] = mulhi64(r[d], r[s]) ^ (uint64_t)imm; break;
            case opIXOR:  r[d] ^= r[s] + rotSalt; break;
            case opIROTR: r[d] = rotl64(r[d], -(int)((r[s]^(uint64_t)imm)&63)); break;
            case opINEG:  r[d] = ~r[d] + (uint64_t)imm; break;
            case opFADD:  f[d&7] = f[d&7] + f[s&7]; break;
            case opFMUL:  f[d&7] = f[d&7] * f[s&7]; break;
            case opFDIV:  f[d&7] = f[d&7] / normFloat(d2bits(f[s&7])); break;
            case opFSQRT: f[d&7] = sqrt(fabs(f[d&7])); break;
            case opLOAD: {
                uint64_t addr=(r[s]+(uint64_t)imm)&NM_SCRATCH_MASK;
                r[d] ^= c->scratch[addr>>3];
            } break;
            case opSTORE: {
                uint64_t addr=(r[d]+(uint64_t)imm)&NM_SCRATCH_MASK;
                c->scratch[addr>>3] ^= r[s] + (uint64_t)loop;
            } break;
            case opCBRANCH:
                if(((r[d]+(uint64_t)imm)&branchMask)==0 && c->taken[pc]<8){
                    c->taken[pc]++;
                    int back=(int)(imm%31)+1;
                    pc-=back; if(pc<0) pc=0;
                    continue;
                }
                break;
            case opAESR: {
                uint64_t addr=(r[s]+(uint64_t)imm)&NM_SCRATCH_MASK&~(uint64_t)15;
                uint64_t w=addr>>3;
                put64(aesIn,   c->scratch[w]);
                put64(aesIn+8, c->scratch[w+1]);
                nm_aes128_encrypt(&c->aes, aesIn, aesOut);
                c->scratch[w]   = le64(aesOut);
                c->scratch[w+1] = le64(aesOut+8);
                r[d] ^= c->scratch[w];
            } break;
            case opXDOM:
                if((imm&1)==0) r[d] ^= d2bits(f[s&7]);
                else           f[d&7] = f[d&7] * normFloat(r[s]);
                break;
            }
            if(ins->op>=opFADD && ins->op<=opFSQRT){
                double v=f[d&7];
                if(isnan(v)||isinf(v)||v==0.0) f[d&7]=normFloat(r[d]|1ULL);
            }
            pc++;
        }
        if(useDS){
            uint64_t addr=(r[1]^rotSalt)&NM_DATASET_MASK;
            for(int k=0;k<NM_DATASET_READS_PER_LOOP;k++){
                uint64_t v=c->dataset[addr>>3];
                r[k&15]^=v;
                addr=(v + r[(k+1)&15] + (uint64_t)loop)&NM_DATASET_MASK;
            }
        }
        uint64_t base = (((r[0] ^ (uint64_t)loop*0x9E3779B97F4A7C15ULL) & NM_SCRATCH_MASK) >> 3);
        for(int i=0;i<16;i++) c->scratch[(base+(uint64_t)i)%NM_SCRATCH_WORDS] ^= r[i];
        for(int i=0;i<8;i++)  r[i+8] ^= d2bits(f[i]);
    }

    uint64_t fold[8]={0,0,0,0,0,0,0,0};
    for(uint32_t i=0;i<NM_SCRATCH_WORDS;i+=8)
        for(int j=0;j<8;j++) fold[j]^=c->scratch[i+j];

    uint8_t buf[4+32+16*8+8*8+8*8]; int n=0;
    memcpy(buf+n,"NMv1",4); n+=4;
    memcpy(buf+n,seed,32); n+=32;
    for(int i=0;i<16;i++){ put64(buf+n,r[i]); n+=8; }
    for(int i=0;i<8;i++){ put64(buf+n,d2bits(f[i])); n+=8; }
    for(int i=0;i<8;i++){ put64(buf+n,fold[i]); n+=8; }
    nm_sha256(buf, n, out);
}
