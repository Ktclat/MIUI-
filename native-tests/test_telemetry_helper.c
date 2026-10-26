/* Self-authored Win64 probes for our telemetry suppression instructions.
 * Links only telemetry_probe.S and the C runtime. No vendor program or library
 * is loaded, and no device, network, UI or clipboard operation is performed.
 * These checks cover register setup, branch destinations and return values;
 * they do not execute the vendor cleanup routines or prove runtime behavior.
 */
#include <stdint.h>
#include <stdio.h>

#if !defined(_WIN64) || !defined(__x86_64__)
#error "These probes require an x86-64 Windows compiler."
#endif

extern int check_discard_patch(uintptr_t value);
extern uintptr_t check_typed_patch(void);
extern int check_false_patch(void);
extern void check_void_patch(void);

int main(void) {
    const uintptr_t values[] = {
        0, 1, 0x7fffffffu, 0x80000000u, 0x100000000ull,
        0x7fffffffffffffffull, 0xffffffffffffffffull
    };
    unsigned checks = 0;
    for (unsigned i = 0; i < sizeof(values) / sizeof(values[0]); i++) {
        if (check_discard_patch(values[i]) != 1) {
            fprintf(stderr, "Discard probe failed at sample %u.\n", i);
            return 10;
        }
        checks++;
    }
    for (unsigned i = 0; i < 100; i++) {
        if (check_typed_patch() != 0) {
            fputs("Typed cleanup probe failed.\n", stderr);
            return 30;
        }
        if (check_false_patch() != 0) {
            fputs("False-return probe failed.\n", stderr);
            return 31;
        }
        check_void_patch();
        checks += 3;
    }
    printf("{\"nativeSyntheticChecks\":%u,\"vendorCodeExecuted\":false}\n", checks);
    return 0;
}
