// The contract between the injected C++ shim and whatever injects and feeds it.
//
// This is a plain C header on purpose: the C++ shim includes it directly, and the C# side
// (SRTOverlay.Injection, SRTOverlay.Protocol, and an overlay plugin) mirrors it field for field.
// The 2026-09-11 decision in the plan's section 11 ("Language: C++ shim") switches the startup blob
// from JSON to this packed struct precisely so there is one authoritative layout both languages agree
// on rather than a serializer on each side that can drift.
//
// NOTHING here may change shape without bumping SRT_OVERLAY_PROTOCOL_VERSION. The shim refuses a blob
// whose version it does not recognise (an overlay plugin and the shim ship in the same install, so a
// mismatch means something was substituted), and the shared-surface blocks below are read raw out of a
// file mapping by a second process, so a silent layout change is a silent wire break.

#ifndef SRTOVERLAY_PROTOCOL_H
#define SRTOVERLAY_PROTOCOL_H

#include <stdint.h>

#ifdef __cplusplus
extern "C"
{
#endif

// ---- Versioning ----

// Version of the startup blob and of the runner-to-shim message set. Bump on any layout change.
//
// v2 (2026-09-12, spike step 5) added the runner-to-shim pipe message set below and retired the
// step-3 shared-memory control blocks. The startup blob layout is unchanged from v1; the bump marks
// that this protocol carries the pipe messages, which the version comment has always said it covers.
#define SRT_OVERLAY_PROTOCOL_VERSION 2

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

    uint8_t reserved0;
    uint8_t reserved1;

    // Composite brightness override (valid only when hasBrightness is 1). For an HDR back buffer this
    // scales the SDR overlay towards the display's reference white.
    float overlayBrightness;

    // Base name of the process the injector believes it is in, with extension - "re9.exe". Checked
    // case-insensitively against the running module before anything else happens, so a mis-aimed
    // injection stops itself. Required, and never empty.
    wchar_t targetProcess[SRT_OVERLAY_MAX_PROCESS];

    // Names the shared surface (request/publish blocks and the shared handles). Empty means the shim
    // installs its hooks and observes the game but composites nothing - the notepad control case.
    wchar_t sessionId[SRT_OVERLAY_MAX_SESSION];

    // Pipe back to the overlay plugin's runner, without the \\.\pipe\ prefix. Empty means "do not
    // connect", which is what the injection spike passes today (step 5 wires it).
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

// ---- Runner-to-shim pipe messages (spike step 5) ----
//
// The shim and the overlay plugin's runner talk over a dedicated named pipe - the plugin creates the
// server, the shim connects as the client, and the pipe name is the startup blob's `pipeName`. The
// framing is SRTHost.Ipc's 16-byte FrameHeader reused byte for byte (see SRTHost.Ipc/FrameHeader.cs
// and the C++ mirror in SRTOverlay.Core's IpcFrame.h): each message is one frame with
// channel = Control, codec = Blittable, and the message kind below carried in the header's
// messageKind field. This is a separate pipe and a separate protocol from the router-to-runner one -
// it only borrows the framing, so these kinds are independent of SRTHost.Ipc's ControlMessageKind.
//
// These two messages replace the step-3 shared-memory control blocks: the shim sends a
// SrtOverlaySurfaceRequest, and the consumer replies with a SrtOverlaySurfacePublish every completed
// frame. The textures and the fence themselves still cross by name (SRT_OVERLAY_SURFACE_COUNT shared
// textures plus one shared fence, opened with OpenSharedHandleByName - see OverlaySharedNames.h),
// because CreateSharedHandle taking a name needs no PROCESS_DUP_HANDLE on the game.

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

    // Reserved for spike step 6 (input and lifetime); not yet on the wire:
    //   SrtOverlayMessage_InputBatch = 16,   // shim -> consumer: batched mouse/keyboard while interactive
    //   SrtOverlayMessage_OverlayState = 17, // shim -> consumer: visible/interactive state changes
} SrtOverlayMessageKind;

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

#pragma pack(pop)

#ifdef __cplusplus
static_assert(sizeof(SrtOverlaySurfaceRequest) == 24,
              "SrtOverlaySurfaceRequest must stay 24 bytes to match SurfaceRequest (C#).");
static_assert(sizeof(SrtOverlaySurfacePublish) == 16,
              "SrtOverlaySurfacePublish must stay 16 bytes to match SurfacePublish (C#).");
#endif

#ifdef __cplusplus
} // extern "C"
#endif

#endif // SRTOVERLAY_PROTOCOL_H
