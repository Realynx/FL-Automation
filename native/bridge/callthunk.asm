; FlBridge XMM-capable call thunk (x64).
; Loads the first 4 args into BOTH the GP regs (RCX/RDX/R8/R9) AND XMM0-3, so a single
; thunk works for any mix of integer/float parameters without per-signature typing.
; Captures both the RAX return and the XMM0 return (for float/double-returning engine fns).
;
;   extern "C" ULONG_PTR fl_call_xmm(void* fn, ULONG_PTR* args /*>=4 slots*/, double* xmm0out);
;     rcx = fn, rdx = args, r8 = xmm0out  ->  returns RAX; *xmm0out = XMM0 (raw 8 bytes)

.code
fl_call_xmm PROC
    push rbx
    push rsi
    push rdi
    sub  rsp, 20h              ; 32-byte shadow space (keeps 16-byte alignment at the call)
    mov  rbx, rcx             ; fn
    mov  rsi, rdx             ; args
    mov  rdi, r8              ; xmm0out
    mov  rcx, [rsi]          ; arg0 -> rcx + xmm0
    movq xmm0, rcx
    mov  rdx, [rsi+8]       ; arg1 -> rdx + xmm1
    movq xmm1, rdx
    mov  r8,  [rsi+16]     ; arg2 -> r8  + xmm2
    movq xmm2, r8
    mov  r9,  [rsi+24]    ; arg3 -> r9  + xmm3
    movq xmm3, r9
    call rbx
    movq qword ptr [rdi], xmm0 ; capture XMM0 return
    add  rsp, 20h
    pop  rdi
    pop  rsi
    pop  rbx
    ret
fl_call_xmm ENDP
END
