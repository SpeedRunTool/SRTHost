// The contract between the injected C++ shim and whatever injects and feeds it.
//
// This is a plain C header on purpose: the C++ shim includes it directly, and the C# side
// (SRTOverlay.Injection, SRTOverlay.Protocol, and an overlay plugin) mirrors it field for field.
// The 2026-09-11 decision in the plan's section 11 ("Language: C++ shim") switches the startup blob
// from JSON to this packed struct precisely so there is one authoritative layout both languages agree
// on rather than a serializer on each side that can drift.
//
// The shim refuses a blob whose SRT_OVERLAY_PROTOCOL_VERSION it does not recognise (an overlay plugin
// and the shim ship in the same install, so a mismatch means something was substituted), and every
// message below is read raw off a pipe by a second process, so a silent layout change is a silent wire
// break. The check machinery stays, but the number does NOT increment during the pre-release spike:
// nothing has shipped, the shim and every plugin are built together from this repo, and there is no
// released peer to stay compatible with - so shapes here change freely and the version holds at 1. It
// starts incrementing only once the shim ships and an out-of-tree plugin could inject a stale blob.

#ifndef SRTOVERLAY_PROTOCOL_H
#define SRTOVERLAY_PROTOCOL_H

#include <stdint.h>

#ifdef __cplusplus
extern "C"
{
#endif

// ---- Versioning ----

// Version of the startup blob and of the runner-to-shim message set. Held at 1 through the pre-release
// spike (see the file header): the wire shape has changed several times as the spike progressed - the
// pipe message set, then the input/state messages and the two hotkey bytes - but with no shipped peer
// to break, incrementing would be churn. Bump this only once the shim has been released.
#define SRT_OVERLAY_PROTOCOL_VERSION 1

// ---- Exports (stable across releases: reputation attaches to names that persist) ----

// Start export, called on a remote thread once LoadLibraryW has returned. Takes a pointer to a
// SrtOverlayStartupOptions in the target's memory; returns an SrtOverlayStartResult as the thread's
// exit code.
#define SRT_OVERLAY_EXPORT_START "SrtOverlayStart"

// Detach export: releases hooks and resources and stops the shim's threads. Unlike the C# NativeAOT
// shim this supersedes, the C++ module CAN then be unloaded - the injector calls FreeLibrary after
// this returns (see SrtOverlayStop's remarks), and the owner watchdog self-unloads via
// FreeLibraryAndExitThread. Genuine unload/reload in a live process is the driving reason for C++.
#define SRT_OVERLAY_EXPORT_STOP "SrtOverlayStop"

// A do-nothing export. Calling it is enough to enter the module with no shim logic running, so a
// memory scan taken around it attributes anything to entry rather than to start. A native DLL has no
// managed runtime to bootstrap, so unlike the NativeAOT shim this is expected to add nothing at all -
// which is the point worth being able to demonstrate.
#define SRT_OVERLAY_EXPORT_PROBE "SrtOverlayProbe"

// Base file names, without extension. One signed binary per architecture, both graphics backends
// inside, shared by every overlay plugin.
#define SRT_OVERLAY_SHIM64 L"SRTOverlay64"
#define SRT_OVERLAY_SHIM32 L"SRTOverlay32"

// ---- Start-export result, returned as the remote thread's exit code ----

// A small set of nameable numbers rather than an HRESULT: the injector reads it through
// GetExitCodeThread, and a value it can name in a log line is worth more than one it looks up.
typedef enum SrtOverlayStartResult
{
    SrtOverlayStartResult_Success = 0,          // started; the overlay owns its threads from here
    SrtOverlayStartResult_BadArgument = 1,      // the startup blob was absent, or malformed
    SrtOverlayStartResult_ProtocolMismatch = 2, // the blob states a protocol version this shim is not
    SrtOverlayStartResult_WrongProcess = 3,     // this process is not the one the blob named
    SrtOverlayStartResult_AlreadyRunning = 4,   // already started in this process; the call did nothing
    SrtOverlayStartResult_Failed = 5,           // something failed where nothing may; the log has it
} SrtOverlayStartResult;

// ---- Hotkeys ----

// The shim owns two independent states, both bound to a hotkey (section 11, "Input"): VISIBLE - the
// quad is composited, the default for a HUD - and INTERACTIVE, which implies visible and swallows all
// mouse and keyboard input from the game, forwarding it to the consumer instead. These are the virtual-
// key codes used when the startup blob leaves a hotkey at 0. F7 matches the RE9 reference's toggle-UI
// key, so the muscle memory carries over.
#define SRT_OVERLAY_DEFAULT_VISIBLE_HOTKEY 0x76     // VK_F7
#define SRT_OVERLAY_DEFAULT_INTERACTIVE_HOTKEY 0x77 // VK_F8

// ---- Startup blob ----
//
// Fixed-size UTF-16 (wchar_t) buffers rather than pointers, because the whole struct is copied into
// the target with one WriteProcessMemory and read back with no fix-ups. UTF-16 matches the Win32 W
// APIs the shim calls and the C# side's char. The buffers are generous but bounded; the injector must
// null-terminate within them.

#define SRT_OVERLAY_MAX_PROCESS 64  // a base file name, e.g. "re9.exe"
#define SRT_OVERLAY_MAX_SESSION 128 // an identifier that names the shared surface
#define SRT_OVERLAY_MAX_PIPE 128    // a pipe name without the \\.\pipe\ prefix
#define SRT_OVERLAY_MAX_LOGPATH 520 // a full path (> MAX_PATH: long paths are enabled)

#pragma pack(push, 1)
typedef struct SrtOverlayStartupOptions
{
    // Must equal SRT_OVERLAY_PROTOCOL_VERSION or the shim refuses to start. First field so a
    // version-only reader can check it without knowing the rest of the layout.
    int32_t protocolVersion;

    // Process id of the runner that injected this shim. The shim waits on it and detaches (and
    // unloads) itself when it exits, so an orphaned detour can never be left in a game with nothing
    // alive to remove it. Zero means nobody owns it.
    int32_t ownerProcessId;

    // 1 if overlayBrightness overrides the per-format default; 0 to use the default.
    uint8_t hasBrightness;

    // 1 to force PQ (ST 2084) encoding, for an HDR10 10-bit buffer a format check cannot tell from a
    // 10-bit SDR one; 0 leaves the per-format default.
    uint8_t forcePq;

    // Virtual-key code that toggles VISIBLE, and the one that toggles INTERACTIVE; 0 picks the
    // SRT_OVERLAY_DEFAULT_* above. One byte each because every VK code fits in one. These were the
    // reserved bytes padding overlayBrightness to four through v2, which is why the layout is unchanged.
    uint8_t visibleHotkey;
    uint8_t interactiveHotkey;

    // Composite brightness override (valid only when hasBrightness is 1). For an HDR back buffer this
    // scales the SDR overlay towards the display's reference white.
    float overlayBrightness;

    // Base name of the process the injector believes it is in, with extension - "re9.exe". Checked
    // case-insensitively against the running module before anything else happens, so a mis-aimed
    // injection stops itself. Required, and never empty.
    wchar_t targetProcess[SRT_OVERLAY_MAX_PROCESS];

    // Names the shared surface (the shared textures and fence). Empty means the shim installs its hooks
    // and observes the game but composites nothing - the notepad control case.
    wchar_t sessionId[SRT_OVERLAY_MAX_SESSION];

    // Pipe back to the overlay plugin's runner, without the \\.\pipe\ prefix. Empty means "do not
    // connect": the shim hooks, but has no consumer to composite for or to forward input to.
    wchar_t pipeName[SRT_OVERLAY_MAX_PIPE];

    // Full path of the log file the shim writes. Passed in rather than derived, because the shim's
    // working directory is the game's and dropping a log into a game's install folder is a habit to
    // break. Empty disables file logging.
    wchar_t logPath[SRT_OVERLAY_MAX_LOGPATH];
} SrtOverlayStartupOptions;
#pragma pack(pop)

#ifdef __cplusplus
// The one number the C++ shim and the C# mirror (OverlayStartupOptions.BlobSize) must agree on. If a
// field is added or a buffer resized on one side and not the other, the wire breaks silently; this
// stops the C++ side from building until they match again. wchar_t is 2 bytes on Windows, so the sum
// is 16 (scalars) + 128 + 256 + 256 + 1040 (the four fixed buffers) = 1696.
static_assert(sizeof(SrtOverlayStartupOptions) == 1696,
              "SrtOverlayStartupOptions must stay 1696 bytes to match OverlayStartupOptions.BlobSize (C#).");
#endif

// ---- Runner-to-shim pipe messages ----
//
// The shim and the overlay plugin's runner talk over a dedicated named pipe - the plugin creates the
// server, the shim connects as the client, and the pipe name is the startup blob's `pipeName`. The
// framing is SRTHost.Ipc's 16-byte FrameHeader reused byte for byte (see SRTHost.Ipc/FrameHeader.cs
// and the C++ mirror in SRTOverlay.Core's IpcFrame.h): each message is one frame with
// channel = Control, codec = Blittable, and the message kind below carried in the header's
// messageKind field. This is a separate pipe and a separate protocol from the router-to-runner one -
// it only borrows the framing, so these kinds are independent of SRTHost.Ipc's ControlMessageKind.
//
// The surface messages (step 5) replace the step-3 shared-memory control blocks: the shim sends a
// SrtOverlaySurfaceRequest, and the consumer replies with a SrtOverlaySurfacePublish every completed
// frame. The textures and the fence themselves still cross by name (SRT_OVERLAY_SURFACE_COUNT shared
// textures plus one shared fence, opened with OpenSharedHandleByName - see OverlaySharedNames.h),
// because CreateSharedHandle taking a name needs no PROCESS_DUP_HANDLE on the game.
//
// The input messages (step 6) flow shim -> consumer only. The shim owns the visible/interactive state
// and the hotkeys that change it; the consumer is told the state and, while interactive, receives every
// mouse and keyboard event the shim swallowed from the game. There is deliberately no per-widget hit-
// testing and so no frame-lag race: while interactive everything goes to the consumer, while not
// nothing does. The consumer draws its own cursor.

#define SRT_OVERLAY_SURFACE_COUNT 2 // two textures the consumer alternates between

// Overlay pipe message kinds, carried in the reused FrameHeader.messageKind. A separate vocabulary
// from the router's ControlMessageKind on purpose: this is a different pipe and a different protocol.
typedef enum SrtOverlayMessageKind
{
    SrtOverlayMessage_None = 0,

    // shim -> consumer: the surface the shim wants, and the adapter it must be created on. Sent once
    // after the compositor initialises, and again after each ResizeBuffers renegotiation.
    SrtOverlayMessage_SurfaceRequest = 1,

    // consumer -> shim: which shared texture holds the newest complete frame, and the fence value that
    // guards it. Sent every frame the consumer completes.
    SrtOverlayMessage_SurfacePublish = 2,

    // shim -> consumer: a SrtOverlayInputBatchHeader followed by `count` SrtOverlayInputEvent. Only sent
    // while interactive; the payload length is exactly header + count * event.
    SrtOverlayMessage_InputBatch = 16,

    // shim -> consumer: a SrtOverlayState. Sent once after the surface request on every connection,
    // and again whenever visible, interactive or the game window's focus changes.
    SrtOverlayMessage_OverlayState = 17,
} SrtOverlayMessageKind;

// What one SrtOverlayInputEvent describes, and so what its generic fields mean. Legacy window messages
// and raw input are reported as distinct types rather than merged, because a game may receive either or
// both (raw input registered with RIDEV_NOLEGACY suppresses the legacy messages entirely) and only the
// consumer can choose a policy - the shim stays policy-free.
typedef enum SrtOverlayInputType
{
    SrtOverlayInput_None = 0,
    SrtOverlayInput_MouseMove = 1,   // x, y: pointer position in surface pixels
    SrtOverlayInput_MouseButton = 2, // code: SrtOverlayMouseButton; value: 1 down, 0 up; x, y: position
    SrtOverlayInput_MouseWheel = 3,  // code: 0 vertical, 1 horizontal; value: delta (WHEEL_DELTA = 120 a notch); x, y
    SrtOverlayInput_Key = 4,         // code: virtual-key code; value: 1 down, 0 up; x: scan code; flags: REPEAT, EXTENDED
    SrtOverlayInput_Char = 5,        // code: one UTF-16 code unit, as WM_CHAR delivers it
    SrtOverlayInput_RawMouse = 6,    // x, y: motion (deltas unless ABSOLUTE); code: RAWMOUSE usButtonFlags; value: signed usButtonData
    SrtOverlayInput_RawKey = 7,      // code: virtual-key code; value: 1 down, 0 up; x: make code; flags: EXTENDED
    SrtOverlayInput_Focus = 8,       // value: 1 the game window gained focus, 0 it lost it (release held keys)
} SrtOverlayInputType;

typedef enum SrtOverlayMouseButton
{
    SrtOverlayMouseButton_Left = 0,
    SrtOverlayMouseButton_Right = 1,
    SrtOverlayMouseButton_Middle = 2,
    SrtOverlayMouseButton_X1 = 3,
    SrtOverlayMouseButton_X2 = 4,
} SrtOverlayMouseButton;

// SrtOverlayInputEvent.flags. The modifier bits are the state when the event was captured.
#define SRT_OVERLAY_INPUT_FLAG_SHIFT 0x0001
#define SRT_OVERLAY_INPUT_FLAG_CONTROL 0x0002
#define SRT_OVERLAY_INPUT_FLAG_ALT 0x0004
#define SRT_OVERLAY_INPUT_FLAG_REPEAT 0x0008   // a key auto-repeat, not a fresh press
#define SRT_OVERLAY_INPUT_FLAG_EXTENDED 0x0010 // an extended key: right Ctrl/Alt, the arrows, numpad Enter
#define SRT_OVERLAY_INPUT_FLAG_ABSOLUTE 0x0020 // RawMouse x/y are absolute (MOUSE_MOVE_ABSOLUTE), not deltas

// Most events one InputBatch frame carries. The shim sends several frames if more are queued.
#define SRT_OVERLAY_MAX_INPUT_BATCH 64

#pragma pack(push, 1)

// shim -> consumer. The surface the shim wants, and the adapter it must be on. The adapter LUID is
// split into two 32-bit fields so the layout is unambiguous across the boundary; adapter matching is
// mandatory because a shared handle will not open across adapters.
typedef struct SrtOverlaySurfaceRequest
{
    uint32_t width;          // back buffer width, pixels
    uint32_t height;         // back buffer height, pixels
    int32_t format;          // the DXGI_FORMAT the surface should use
    uint32_t adapterLuidLow; // low 32 bits of the game device's adapter LUID
    int32_t adapterLuidHigh; // high 32 bits
    uint32_t surfaceCount;   // textures the consumer must create; equals SRT_OVERLAY_SURFACE_COUNT
} SrtOverlaySurfaceRequest;

// consumer -> shim. Which texture holds the newest frame, and the fence value its queue will signal
// when that texture is done. The shim issues a queue-side Wait on that value before its own list
// samples the texture, so the GPU orders the two processes' work without the render thread blocking.
typedef struct SrtOverlaySurfacePublish
{
    uint32_t latestIndex; // index of the newest complete texture
    uint32_t alive;       // non-zero while the consumer is still producing
    uint64_t fenceValue;  // signalled on the shared fence when latestIndex is finished rendering
} SrtOverlaySurfacePublish;

// shim -> consumer. One captured input event. Generic fields whose meaning depends on `type` (see
// SrtOverlayInputType) keep every event one fixed size, so a batch is a plain array.
typedef struct SrtOverlayInputEvent
{
    uint16_t type;   // SrtOverlayInputType
    uint16_t flags;  // SRT_OVERLAY_INPUT_FLAG_*
    uint32_t code;   // per type: virtual-key, mouse button, wheel axis, UTF-16 unit, raw button flags
    int32_t x;       // per type: surface pixel x, raw delta x, or scan/make code
    int32_t y;       // per type: surface pixel y, raw delta y
    int32_t value;   // per type: 1 down / 0 up, wheel delta, focus
    uint32_t timeMs; // GetMessageTime() of the window message that carried it
} SrtOverlayInputEvent;

// shim -> consumer. Leads every InputBatch payload.
typedef struct SrtOverlayInputBatchHeader
{
    uint32_t count;   // SrtOverlayInputEvent records that follow, at most SRT_OVERLAY_MAX_INPUT_BATCH
    uint32_t dropped; // events lost to a full queue since the previous batch; non-zero means resync held keys
} SrtOverlayInputBatchHeader;

// shim -> consumer. The shim-owned overlay state.
typedef struct SrtOverlayState
{
    uint8_t visible;       // 1 while the quad is composited
    uint8_t interactive;   // 1 while input is swallowed from the game and forwarded; implies visible
    uint8_t windowFocused; // 1 while the game window has keyboard focus
    uint8_t reserved;      // 0
    int32_t cursorX;       // the OS cursor in surface pixels when the state changed, to seat a software cursor
    int32_t cursorY;
} SrtOverlayState;

#pragma pack(pop)

#ifdef __cplusplus
static_assert(sizeof(SrtOverlaySurfaceRequest) == 24,
              "SrtOverlaySurfaceRequest must stay 24 bytes to match SurfaceRequest (C#).");
static_assert(sizeof(SrtOverlaySurfacePublish) == 16,
              "SrtOverlaySurfacePublish must stay 16 bytes to match SurfacePublish (C#).");
static_assert(sizeof(SrtOverlayInputEvent) == 24,
              "SrtOverlayInputEvent must stay 24 bytes to match InputEvent (C#).");
static_assert(sizeof(SrtOverlayInputBatchHeader) == 8,
              "SrtOverlayInputBatchHeader must stay 8 bytes to match InputBatchHeader (C#).");
static_assert(sizeof(SrtOverlayState) == 12,
              "SrtOverlayState must stay 12 bytes to match OverlayState (C#).");
#endif

#ifdef __cplusplus
} // extern "C"
#endif

#endif // SRTOVERLAY_PROTOCOL_H
