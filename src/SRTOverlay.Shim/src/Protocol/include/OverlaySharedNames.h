// The names the shared textures and fence are published under, built the same way on both sides so a
// shim and a consumer given the same session id agree without a second contract. Mirrors the C#
// OverlaySharedSurface.*Name(session). C++-only (std::wstring), so it sits beside the C protocol
// header rather than in it.
//
// Since spike step 5 the surface request and publish cross over the pipe (SrtOverlaySurfaceRequest /
// SrtOverlaySurfacePublish in OverlayProtocol.h); only the textures and the fence still cross by
// name, because CreateSharedHandle taking a name needs no PROCESS_DUP_HANDLE on the game.
//
// All names are prefixed Local\ so they live in the caller's session namespace: the consumer and the
// game run as the same user, and a Global\ name would need a privilege neither has reason to hold.

#ifndef SRTOVERLAY_SHARED_NAMES_H
#define SRTOVERLAY_SHARED_NAMES_H

#include <string>

#include "OverlayProtocol.h"

namespace srtoverlay::protocol
{
    inline std::wstring TextureName(const std::wstring& session, int index)
    {
        return L"Local\\SRTOverlayTex-" + session + L"-" + std::to_wstring(index);
    }

    inline std::wstring FenceName(const std::wstring& session)
    {
        return L"Local\\SRTOverlayFence-" + session;
    }
}

#endif // SRTOVERLAY_SHARED_NAMES_H
