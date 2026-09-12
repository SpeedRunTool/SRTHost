// UTF-16 <-> UTF-8 conversion, for turning the wide strings the Win32 APIs and the startup blob use
// into the UTF-8 the log file holds. Off the render path only - it allocates.

#ifndef SRTOVERLAY_TEXT_H
#define SRTOVERLAY_TEXT_H

#include <windows.h>

#include <string>
#include <string_view>

namespace srtoverlay::core
{
    inline std::string Narrow(std::wstring_view wide)
    {
        if (wide.empty())
            return {};

        const int needed = WideCharToMultiByte(CP_UTF8, 0, wide.data(), static_cast<int>(wide.size()),
                                                nullptr, 0, nullptr, nullptr);
        if (needed <= 0)
            return {};

        std::string out(static_cast<size_t>(needed), '\0');
        WideCharToMultiByte(CP_UTF8, 0, wide.data(), static_cast<int>(wide.size()),
                            out.data(), needed, nullptr, nullptr);
        return out;
    }
}

#endif // SRTOVERLAY_TEXT_H
