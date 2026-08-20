#include "nm_params.h"
#include "nm_sha256.h"
#include <string.h>

static uint16_t le16(const uint8_t *b){ return (uint16_t)b[0] | ((uint16_t)b[1] << 8); }
static uint64_t le64(const uint8_t *b){
    uint64_t v = 0;
    for (int i = 0; i < 8; i++) v |= (uint64_t)b[i] << (8 * i);
    return v;
}

static void hash_tagged(const char *prefix, const uint8_t seed[32], uint8_t out[32]){
    nm_sha256_ctx c; nm_sha256_init(&c);
    nm_sha256_update(&c, (const uint8_t*)prefix, strlen(prefix));
    nm_sha256_update(&c, seed, 32);
    nm_sha256_final(&c, out);
}

void nm_derive_params(const uint8_t seed[32], nm_params *p){
    uint8_t h[32];
    hash_tagged("nm-params|", seed, h);

    p->prog_size  = 384 + (int)(le16(&h[0]) % 385);
    p->loops      = 32 + (int)(h[2] % 33);
    p->branch_mask = (uint64_t)0xFF << (h[3] % 24);
    p->rot_salt   = le64(&h[4]);
    memcpy(p->aes_key, &h[12], 16);

    uint8_t dk[32];
    hash_tagged("nm-dataset|", seed, dk);
    memcpy(p->dataset_key, dk, 16);

    uint8_t wh[32];
    hash_tagged("nm-weights|", seed, wh);

    int weights[NM_NUM_OPS];
    int total = 0;
    for (int i = 0; i < NM_NUM_OPS; i++){
        weights[i] = 1 + (int)(wh[i] % 8);
        total += weights[i];
    }
    int idx = 0;
    for (int op = 0; op < NM_NUM_OPS; op++){
        int n = weights[op] * 256 / total;
        if (op == NM_NUM_OPS - 1) n = 256 - idx;
        for (int j = 0; j < n && idx < 256; j++)
            p->op_table[idx++] = (uint8_t)op;
    }
    while (idx < 256){
        p->op_table[idx] = (uint8_t)(wh[idx % 32] % NM_NUM_OPS);
        idx++;
    }
}
