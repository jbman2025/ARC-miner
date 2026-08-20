#ifndef NM_NEUROMORPH_H
#define NM_NEUROMORPH_H
#include <stdint.h>
#include "nm_params.h"
#include "nm_aes.h"

#ifdef __cplusplus
extern "C" {
#endif

#define NM_SCRATCH_BYTES (2u << 20)
#define NM_SCRATCH_WORDS (NM_SCRATCH_BYTES / 8)
#define NM_SCRATCH_MASK  ((uint64_t)(NM_SCRATCH_BYTES - 8))
#define NM_DATASET_BYTES (64u << 20)
#define NM_DATASET_WORDS (NM_DATASET_BYTES / 8)
#define NM_DATASET_MASK  ((uint64_t)(NM_DATASET_BYTES - 8))
#define NM_DATASET_READS_PER_LOOP 64
#define NM_DATASET_HEIGHT 240
#define NM_EPOCH_LENGTH 4096
#define NM_PROG_MAX 768
#define NM_NONCE_OFFSET 116
#define NM_HEADER_LEN 124

typedef struct { uint8_t op, dst, src; uint32_t imm; } nm_instr;

typedef struct {
    nm_params params;
    nm_aes128 aes;
    uint64_t *scratch;
    nm_instr *prog;
    uint8_t  *taken;
    uint64_t *dataset;
    uint8_t   dataset_key[16];
    int       dataset_valid;
    int       owns_dataset;
    int       owns_scratch;
} nm_ctx;

int  nm_ctx_init(nm_ctx *c);

int  nm_ctx_init_shared(nm_ctx *c);

void nm_ctx_free(nm_ctx *c);

void nm_ctx_set_seed(nm_ctx *c, const uint8_t seed[32]);

void nm_ctx_set_params(nm_ctx *c, const uint8_t seed[32]);

void nm_ctx_attach_dataset(nm_ctx *c, uint64_t *dataset, const uint8_t key[16]);

void nm_ctx_attach_scratch(nm_ctx *c, void *scratch);

void nm_build_dataset(uint64_t *dataset, const uint8_t dataset_key[16]);

void nm_hash(nm_ctx *c, const uint8_t *header, uint64_t height, uint8_t out[32]);

#ifdef __cplusplus
}
#endif

#endif
