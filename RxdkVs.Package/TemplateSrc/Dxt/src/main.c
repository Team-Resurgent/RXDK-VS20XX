// An Xbox debug-monitor extension (DXT) that draws an FPS / memory / temperature
// overlay using the NV2A PVIDEO hardware video overlay.
//
// Ported from RXDK-FPS (https://github.com/Team-Resurgent/RXDK-FPS), itself a
// port of JayFoxRox's xbox-fps-overlay. Unlike a title, a DXT is not an XBE: it
// is a raw flat PE loaded by xbdm.dll from E:\dxt at debug-monitor init and
// entered at DxtEntry. It runs in the system process, kernel-mode, so it calls
// the kernel (xboxkrnl.exe) and the debug monitor (xbdm.dll) directly -- no
// XAPI, no D3D, no CRT startup.
//
// Build: links libxbdm.lib (Dm* by ordinal) + libkernel.lib (xboxkrnl.exe by
// ordinal) + libc (sprintf/str*) + libxapi (heap object only -- sprintf's MSVC
// format-string fallback can call malloc), then imagebld /DXT flattens it into
// a .dxt. Deploy copies it to E:\dxt and warm-reboots so xbdm loads it.
//
// NOTE: the FPS/memory overlay only draws while a Direct3D title is running (it
// reads that title's "frames" performance counter) -- on the dashboard it stays
// idle. Launch a game after the reboot to see it.

#include <xboxkrnl/xboxkrnl.h>

// The kernel APIs (Mm*/Ps*/Hal*/Ke*/Dbg*) and KernelMode come from RXDK's own
// <xboxkrnl/*> headers above -- no hand-rolled "undocumented" declarations. xbdm.h
// still needs a little Win32 glue (WINAPI/SUCCEEDED/MAX_PATH/LPBOOL); we can't get
// it from <windef.h>/<winerror.h> here because those pull the mingw <winnt.h>,
// which collides with the standalone kernel types a DXT is built against.
#ifndef WINAPI
#define WINAPI __stdcall
#endif
#ifndef SUCCEEDED
#define SUCCEEDED(hr) (((HRESULT)(hr)) >= 0)
#endif
#ifndef MAX_PATH
#define MAX_PATH 260
#endif
#ifndef LPBOOL
typedef BOOL *LPBOOL;
#endif

#include <xbdm.h>
#include <stdio.h>
#include <string.h>

#include "defines.h"
#include "font.h"

// Xbox uses 4 KiB pages (not defined by the kernel headers).
#ifndef PAGE_SHIFT
#define PAGE_SHIFT 12
#endif

// SMBus register constants (from RXDK-FPS Undocumented.h).
#define PIC_ADDRESS 0x20
#define CPU_TEMP    0x09
#define MB_TEMP     0x0A

// HalReadSMBusByte(addr, cmd, &out) -> byte read (ReadWordValue = FALSE).
#define HalReadSMBusByte(SlaveAddress, CommandCode, DataValue) \
    HalReadSMBusValue((UCHAR)(SlaveAddress), (UCHAR)(CommandCode), FALSE, (DataValue))

static LONGLONG lastFrame = 0;
static int gotIsXbox16 = 0;
static int isXbox16 = 0;

static int IsXbox16(void)
{
    if (!gotIsXbox16) {
        ULONG temp;
        char ver[6];
        gotIsXbox16 = 1;
        HalReadSMBusByte(0x20, 0x01, &temp);
        ver[0] = (char)temp;
        HalReadSMBusByte(0x20, 0x01, &temp);
        ver[1] = (char)temp;
        HalReadSMBusByte(0x20, 0x01, &temp);
        ver[2] = (char)temp;
        ver[3] = 0;
        ver[4] = 0;
        ver[5] = 0;
        isXbox16 = (strcmp(ver, "P2L") == 0);
    }
    return isXbox16;
}

static int GetCpuTemp(void)
{
    if (IsXbox16()) {
        ULONG cpu = 0, cpudec = 0;
        HalReadSMBusByte(0x4C << 1, 0x01, &cpu);
        HalReadSMBusByte(0x4C << 1, 0x10, &cpudec);
        return (int)(unsigned char)(cpu + cpudec / 256);
    }

    ULONG cpuTemp = 0;
    HalReadSMBusByte(PIC_ADDRESS, CPU_TEMP, &cpuTemp);
    return (int)cpuTemp;
}

static int GetMbTemp(void)
{
    ULONG tempMb = 0;
    HalReadSMBusByte(PIC_ADDRESS, MB_TEMP, &tempMb);
    return IsXbox16() ? (int)((tempMb * 4) / 5) : (int)tempMb;
}

static void UpdateFramebuffer(unsigned char *framebuffer)
{
    HANDLE h;
    DM_COUNTDATA count_data;
    LONGLONG frame, delta;
    MM_STATISTICS mem;
    ULONG usedMb, totalMb;
    char message[256];
    int x, i, width, height;
    unsigned long base;

    HRESULT hr = DmOpenPerformanceCounter("frames", &h);
    if (!SUCCEEDED(hr)) {
        return;
    }

    // Read counter, then release the handle.
    hr = DmQueryPerformanceCounterHandle(h, 0x11, &count_data);
    DmClosePerformanceCounter(h);

    // Get frame info, but avoid frame 0.
    frame = count_data.CountValue.QuadPart;
    if (frame == 0) {
        return;
    }

    // Bootstrap the counter on first pass.
    if (lastFrame == 0) {
        lastFrame = frame;
        return;
    }

    delta = frame - lastFrame;
    lastFrame = frame;

    for (i = 0; i < WIDTH * HEIGHT; i++) {
        framebuffer[i * 2 + 0] = 0x00;
        framebuffer[i * 2 + 1] = 0x7f;
    }

    // GlobalMemoryStatus is a thin wrapper over the kernel; call it directly so
    // the DXT stays free of XAPI.
    memset(&mem, 0, sizeof(mem));
    mem.Length = sizeof(MM_STATISTICS);
    MmQueryStatistics(&mem);
    totalMb = (mem.TotalPhysicalPages << PAGE_SHIFT) / (1024 * 1024);
    usedMb = ((mem.TotalPhysicalPages - mem.AvailablePages) << PAGE_SHIFT) / (1024 * 1024);

    if (delta > 99999) {
        sprintf(message, "RXDK FPS");
    } else {
        sprintf(message, "FPS: %i MEM: %iMB/%iMB CPU: %i MB: %i",
            (int)delta, (int)usedMb, (int)totalMb, GetCpuTemp(), GetMbTemp());
    }

    x = 0;
    for (i = 0; i < (int)strlen(message); i++) {
        int symbol, symbol_col, symbol_row, symbol_offset, y, dx;
        int gap = WIDTH - x;
        if (gap < 8) {
            break;
        }

        symbol = message[i] - ' ';
        if (symbol >= 96) {
            symbol = 0;
        }

        symbol_col = (symbol & 0xf);
        symbol_row = (symbol >> 4) & 0xf;
        symbol_offset = symbol_col + ((symbol_row << 4) * 13);

        for (y = 0; y < 13; y++) {
            unsigned char *fb_row = &framebuffer[(y * PITCH) + (x << 1)];
            const unsigned char *font_row = &FontImageData[(y << 4) + symbol_offset];
            for (dx = 0; dx < 8; dx++) {
                fb_row[dx << 1] = ((font_row[0] >> (7 - dx)) & 0x1) * 0xFF;
            }
        }

        x += 8;
    }

    width = x - 1;
    height = HEIGHT;

    // Program the NV2A PVIDEO overlay to display the framebuffer.
    base = 0xFD000000 + NV_PVIDEO;

    *(volatile unsigned long *)(base + NV_PVIDEO_STOP) = 0;
    *(volatile unsigned long *)(base + NV_PVIDEO_INTR_EN) = 0;
    *(volatile unsigned long *)(base + NV_PVIDEO_INTR) = *(volatile unsigned long *)(base + NV_PVIDEO_INTR);

    *(volatile unsigned long *)(base + NV_PVIDEO_LUMINANCE) = (0x0 << 16) | 0x1000;
    *(volatile unsigned long *)(base + NV_PVIDEO_CHROMINANCE) = (0x0 << 16) | 0x1000;

    *(volatile unsigned long *)(base + NV_PVIDEO_BASE) = 0x00000000;
    *(volatile unsigned long *)(base + NV_PVIDEO_LIMIT) = 0xFFFFFFFF;
    *(volatile unsigned long *)(base + NV_PVIDEO_OFFSET) = (unsigned long)MmGetPhysicalAddress(framebuffer);

    *(volatile unsigned long *)(base + NV_PVIDEO_POINT_IN) = (0 << 16) | 0;
    *(volatile unsigned long *)(base + NV_PVIDEO_SIZE_IN) = (HEIGHT << 16) | width;

    *(volatile unsigned long *)(base + NV_PVIDEO_POINT_OUT) = (OUT_Y << 16) | OUT_X;
    *(volatile unsigned long *)(base + NV_PVIDEO_SIZE_OUT) = (height << 16) | width;

    *(volatile unsigned long *)(base + NV_PVIDEO_DS_DX) = (width << 20) / width;
    *(volatile unsigned long *)(base + NV_PVIDEO_DT_DY) = (height << 20) / height;

    *(volatile unsigned long *)(base + NV_PVIDEO_FORMAT) =
        NV_PVIDEO_FORMAT_MATRIX | (NV_PVIDEO_FORMAT_COLOR_LE_CR8YB8CB8YA8 << 16) | PITCH;

    *(volatile unsigned long *)(base + NV_PVIDEO_BUFFER) = NV_PVIDEO_BUFFER_0_USE;

    // Flush caches so the NV2A sees the freshly written framebuffer.
    __asm__ __volatile__("wbinvd");
}

// Worker thread: repaint the overlay once per second. Passed to
// PsCreateSystemThreadEx as the SystemRoutine (StartContext = framebuffer).
static void STDCALL Process(PKSTART_ROUTINE StartRoutine, PVOID StartContext)
{
    unsigned char *framebuffer = (unsigned char *)StartContext;
    LARGE_INTEGER interval;
    (void)StartRoutine;

    for (;;) {
        UpdateFramebuffer(framebuffer);
        interval.QuadPart = -10000000LL; // 1 second, relative
        KeDelayExecutionThread(KernelMode, FALSE, &interval);
    }
}

// DXT entry point. xbdm calls this (via its CallDxtEntry asm thunk) once the
// image is loaded and relocated, passing a BOOL* the extension sets to request
// immediate unload. We spin up the overlay worker and stay resident.
void DxtEntry(unsigned long *pfUnload)
{
    PVOID framebuffer;
    HANDLE handle;
    NTSTATUS status;

    DbgPrint("dxt-fps: DxtEntry, starting overlay thread\n");

    framebuffer = MmAllocateContiguousMemory(PITCH * HEIGHT);
    status = PsCreateSystemThreadEx(&handle, 0, 8192, 0, NULL, NULL, framebuffer,
        FALSE, FALSE, Process);

    // Stay loaded only if the worker thread was created successfully.
    *pfUnload = (status >= 0) ? 0 : 1;
}
