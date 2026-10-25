/* MIUI+ 2.5.5.643: genuine Android UPDATE_MAIN_MODE on the existing channel.
 * No CRT, no globals, no injected threads, no account/authorization changes.
 * All calls are fixed by the offline PE builder as image-relative REL32.
 */
.intel_syntax noprefix
.text

.extern native_show
.extern native_allocate
.extern native_free
.extern native_pipe_singleton
.extern native_post_business
.extern native_time_iat

.p2align 4, 0x90
.globl background_close
.seh_proc background_close
background_close:
    push rbx
    .seh_pushreg rbx
    sub rsp, 32
    .seh_stackalloc 32
    .seh_endprologue
    mov rbx, rcx                    /* SharedWindowFrame, not MainWindow base */
    cmp DWORD PTR [rcx + 0x18], 0   /* native screen_id */
    jne .Lclose_child
    mov ecx, 1
    mov edx, 18
    call send_control_message      /* unlink audio first; avoids frozen-visible on failure */
    test al, al
    jz .Lclose_done
    mov ecx, 1
    mov edx, 12
    call send_control_message      /* pause video only after audio request was queued */
    test al, al                    /* false: retain visible window for retry */
    jz .Lclose_done
    mov rcx, rbx
    xor edx, edx
    call native_show
    jmp .Lclose_done
.Lclose_child:
    mov rcx, rbx                   /* preserve previous hide-only child behavior */
    xor edx, edx
    call native_show               /* mode11 is main-screen-only */
.Lclose_done:
    add rsp, 32
    pop rbx
    ret
.seh_endproc

.p2align 4, 0x90
.globl background_resume_show
.seh_proc background_resume_show
background_resume_show:
    push rbx
    .seh_pushreg rbx
    sub rsp, 32
    .seh_stackalloc 32
    .seh_endprologue
    mov rbx, rcx
    cmp DWORD PTR [rcx + 0x18], 0
    jne .Lresume_show
    xor ecx, ecx
    mov edx, 12
    call send_control_message      /* UPDATE_MAIN_MODE(false), before Show */
    test al, al
    jz .Lresume_done               /* keep hidden for retry if pipe unavailable */
.Lresume_show:
    mov rcx, rbx
    mov edx, 1
    call native_show               /* original visible path/activation remains */
.Lresume_done:
    add rsp, 32
    pop rbx
    ret
.seh_endproc

/* bool send_control_message(bool background, unsigned wire_type)
 * Message ABI: +0 uint8 type; +8 owned data pointer; +16 uint32 length.
 * Framing: wire_type, bodyLength uint32LE, protobuf body.
 * Type12: session_id(1,uint64), action(3,varint=11), background_mode(11,bool).
 * Type18: session_id(1,uint64), event(2,varint=32), v_bool(oneof field6).
 * Only audio=true is used by close; resume deliberately does not send false,
 * because that would force-link audio without a proven prior mute preference.
 * screen_id=0 and app_id="" are their native proto3 defaults.
 * Session timestamp matches original producer's _Xtime_get_ticks()/10000.
 * Maximum packet = 5 + 1 + 10 + 2 + 2 = 20 bytes; allocate 32.
 * PostCmd owns/frees data only on true; false is released here exactly once.
 */
/* Fixed 15-byte padding: GNU as versions choose different multi-byte NOPs.
 * Preserve the reviewed 0x90 sender offset and embedded byte-exact image. */
.byte 0x0f,0x1f,0x40,0x00,0x66,0x66,0x2e,0x0f,0x1f,0x84,0x00,0x00,0x00,0x00,0x00
.p2align 4, 0x90
.globl send_control_message
.seh_proc send_control_message
send_control_message:
    push rbx
    .seh_pushreg rbx
    push rsi
    .seh_pushreg rsi
    push rdi
    .seh_pushreg rdi
    sub rsp, 64
    .seh_stackalloc 64
    .seh_endprologue
    mov esi, ecx
    mov edi, edx
    mov ecx, 32
    call native_allocate
    mov rbx, rax
    call QWORD PTR [rip + native_time_iat]
    xor edx, edx
    mov r8d, 10000
    div r8                         /* live timestamps are nonnegative */
    mov BYTE PTR [rbx], dil
    lea r9, [rbx + 5]
    mov BYTE PTR [r9], 8
    inc r9
.Lvarint:
    mov edx, eax
    and edx, 127
    shr rax, 7
    jz .Lvarint_last
    or edx, 128
    mov BYTE PTR [r9], dl
    inc r9
    jmp .Lvarint
.Lvarint_last:
    mov BYTE PTR [r9], dl
    inc r9
    cmp edi, 12
    jne .Laudio_fields
    mov WORD PTR [r9], 0x0b18
    add r9, 2
    mov BYTE PTR [r9], 0x58
    jmp .Lbool_field
.Laudio_fields:
    mov WORD PTR [r9], 0x2010
    add r9, 2
    mov BYTE PTR [r9], 0x30
.Lbool_field:
    test esi, esi
    setne BYTE PTR [r9 + 1]
    add r9, 2
    sub r9, rbx
    mov r8d, r9d
    sub r8d, 5
    mov DWORD PTR [rbx + 1], r8d
    mov QWORD PTR [rsp + 32], rdi  /* zero padding as well as type */
    mov QWORD PTR [rsp + 40], rbx
    mov QWORD PTR [rsp + 48], r9
    call native_pipe_singleton
    mov rcx, rax
    lea rdx, [rsp + 32]
    call native_post_business
    test al, al
    jnz .Lsend_success
    mov rcx, rbx
    call native_free
    xor eax, eax
    jmp .Lsend_done
.Lsend_success:
    mov eax, 1
.Lsend_done:
    add rsp, 64
    pop rdi
    pop rsi
    pop rbx
    ret
.seh_endproc
