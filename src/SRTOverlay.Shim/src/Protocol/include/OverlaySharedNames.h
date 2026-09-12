// The names the shared-surface handshake uses, built the same way on both sides so a shim and a
// producer given the same session id agree without a second contract. Mirrors the C# spec's
// OverlaySharedSurface.*Name(session). C++-only (std::wstring), so it sits beside the C protocol
// header rather than in it.
//
// All names are prefixed Local\ so they live in the caller's session namespace: the producer and the
// game run as the same user, and a Global\ name would need a privilege neither has reason to hold.

#ifndef SRTOVERLAY_SHARED_NAMES_H
#define SRTOVERLAY_SHARED_NAMES_H

#include <string>

#include "OverlayProtocol.h"

namespace srtoverlay::protocol
{
    inline std::wstring RequestName(const std::wstring& session)
    {
        return L"Local\\SRTOverlayRequest-" + session;
    }

    inline std::wstring PublishName(const std::wstring& session)
    {
        return L"Local\\SRTOverlayPublish-" + session;
    }

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
