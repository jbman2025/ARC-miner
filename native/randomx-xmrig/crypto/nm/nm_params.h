#ifndef NM_PARAMS_H
#define NM_PARAMS_H
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define NM_NUM_OPS 15

typedef struct {
    int      prog_size;
    int      loops;
    uint64_t branch_mask;
    uint64_t rot_salt;
    uint8_t  aes_key[16];
    uint8_t  dataset_key[16];
    uint8_t  op_table[256];
} nm_params;

void nm_derive_params(const uint8_t seed[32], nm_params *p);

#ifdef __cplusplus
}
#endif

#endif
