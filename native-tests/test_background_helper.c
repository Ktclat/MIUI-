/* Standalone test of OUR assembler, linked ONLY against in-memory stubs.
 * Never links, loads, launches or opens a vendor binary/device/window/clipboard.
 */
#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

extern int send_control_message(int background, unsigned wire_type);
extern void background_close(void *frame);
extern void background_resume_show(void *frame);
static uint64_t ticks;
static int fail_post, allocs, frees, posts, shows, show_value;
static void *show_frame;
static unsigned char captured[4][32];
static size_t captured_length[4];
static int fake_pipe;

static uint64_t fake_ticks(void) { return ticks; }
uint64_t (*native_time_iat)(void) = fake_ticks;
void *native_allocate(size_t size) {
    assert(size == 32); ++allocs;
    void *p = malloc(size); assert(p); memset(p, 0xcc, size); return p;
}
void native_free(void *p) { assert(p); ++frees; free(p); }
void *native_pipe_singleton(void) { return &fake_pipe; }
int native_post_business(void *pipe, unsigned char *msg) {
    void *buffer; uint32_t len;
    assert(pipe == &fake_pipe);
    assert(msg[0] == 12 || msg[0] == 18);
    for (int i=1;i<8;i++) assert(msg[i] == 0);
    memcpy(&buffer, msg+8, 8); memcpy(&len,msg+16,4);
    assert(posts < 4 && len <= sizeof(captured[0]));
    for (int i=20;i<24;i++) assert(msg[i] == 0);
    memcpy(captured[posts],buffer,len); captured_length[posts]=len; ++posts;
    if (fail_post == posts) return 0;  /* native early failure: no free */
    native_free(buffer);     /* mirrors native success ownership */
    return 1;
}
void native_show(void *frame, int visible) {
    show_frame=frame;show_value=visible;++shows;
}

static void reset(void) {
    fail_post=allocs=frees=posts=shows=show_value=0;
    memset(captured_length,0,sizeof(captured_length));memset(captured,0,sizeof(captured));
    show_frame=NULL;
}
static uint64_t read_varint(const unsigned char *b,size_t len,size_t *at) {
    uint64_t v=0;
    for(unsigned shift=0;shift<70;shift+=7) {
        assert(*at<len);unsigned c=b[(*at)++];
        if(shift==63)assert(c<=1);
        v|=(uint64_t)(c&127)<<shift;
        if(!(c&128))return v;
    }
    assert(0);return 0;
}
static void validate_wire(unsigned index,int bg,unsigned type) {
    const unsigned char *buf=captured[index];size_t len=captured_length[index];
    assert(len>=11 && len<=20);
    assert(buf[0]==type);
    uint32_t body;memcpy(&body,buf+1,4);
    assert(body==len-5);
    size_t at=5;
    assert(buf[at++]==8);
    assert(read_varint(buf,len,&at)==ticks/10000);
    if(type==12)assert(buf[at++]==0x18 && buf[at++]==11 && buf[at++]==0x58);
    else assert(buf[at++]==0x10 && buf[at++]==32 && buf[at++]==0x30);
    assert(buf[at++]==(bg!=0));
    assert(at==len);
    assert(allocs==frees && allocs==posts);
}
int main(void) {
    static const uint64_t times[]={0,10000,1270000,1280000,163830000,163840000,
        17000000000000000ULL,9223372036854775807ULL};
    unsigned cases=0;
    for(unsigned i=0;i<sizeof(times)/sizeof(times[0]);i++)
      for(unsigned type=12;type<=18;type+=6)
      for(int bg=0;bg<2;bg++)for(int failure=0;failure<2;failure++) {
        reset();ticks=times[i];fail_post=failure;
        assert(send_control_message(bg,type)==!failure);validate_wire(0,bg,type);++cases;
      }
    unsigned char frame[128]={0};
    for(int failure=0;failure<3;failure++) {
        reset();ticks=17000000000000000ULL;fail_post=failure;
        background_close(frame);validate_wire(0,1,18);
        assert(posts==(failure==1?1:2));
        if(posts==2)validate_wire(1,1,12);
        assert(shows==!failure);
        if(!failure)assert(show_frame==frame && show_value==0);
        ++cases;
    }
    for(int failure=0;failure<2;failure++) {
        reset();fail_post=failure;
        background_resume_show(frame);validate_wire(0,0,12);
        assert(posts==1 && shows==!failure); /* never auto-relink audio */
        if(!failure)assert(show_frame==frame && show_value==1);
        ++cases;
    }
    uint32_t id=7;memcpy(frame+0x18,&id,4);
    reset();background_close(frame);
    assert(posts==0 && allocs==0 && shows==1);
    assert(show_frame==frame && show_value==0);++cases;
    reset();background_resume_show(frame);
    assert(posts==0 && allocs==0 && shows==1);
    assert(show_frame==frame && show_value==1);++cases;
    printf("PASS: %u standalone helper cases; stub-only, no vendor/device/UI execution\n",cases);
    return 0;
}
