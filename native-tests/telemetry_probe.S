/* Self-authored synthetic probes. The .byte blocks are OUR replacement
 * instructions, checked against the public profile by test_telemetry_profile.py.
 * The surrounding setup and cleanup below are test stubs, not vendor code.
 * INT3 padding makes an incorrect fall-through fail instead of passing silently.
 */
.intel_syntax noprefix
.text
.globl check_discard_patch
.seh_proc check_discard_patch
check_discard_patch:
 push rbx
 .seh_pushreg rbx
 push rsi
 .seh_pushreg rsi
 sub rsp, 0x58
 .seh_stackalloc 0x58
 .seh_endprologue
 mov qword ptr [rsp+0x50], rcx
 mov esi, 0x12345678
 mov rbx, -1
.Ldiscard:
.byte 0x48,0x8b,0x5c,0x24,0x50,0x33,0xf6,0xe9,0x62,0x00,0x00,0x00,0x90
 .fill 0x6e - (. - .Ldiscard), 1, 0xcc
.Ldiscard_cleanup:
 xor eax,eax
 cmp rbx,rcx
 jne .Ldiscard_done
 test rsi,rsi
 jne .Ldiscard_done
 mov eax,1
.Ldiscard_done:
 add rsp,0x58
 pop rsi
 pop rbx
 ret
.seh_endproc

.globl check_typed_patch
.seh_proc check_typed_patch
check_typed_patch:
 push r14
 .seh_pushreg r14
 .seh_endprologue
 mov r14,-1
.Ltyped:
.byte 0x45,0x33,0xf6,0xe9,0xd1,0x03,0x00,0x00,0x90,0x90,0x90,0x90,0x90,0x90
 .fill 0x3d9 - (. - .Ltyped), 1, 0xcc
.Ltyped_cleanup:
 mov rax,r14
 pop r14
 ret
.seh_endproc

.globl check_false_patch
check_false_patch:
.byte 0x33,0xc0,0xc3

.globl check_void_patch
check_void_patch:
.byte 0xc3
