#pragma once

#include <windows.h>

namespace Niskala
{
    inline constexpr wchar_t SplashReadyEvent[] = L"Local\\NiskalaUtilities.SplashReady";

    struct SplashTiming
    {
        double FadeIn = 140.0;
        double MarkStart = 0.0;
        double MarkLength = 420.0;
        double ShardStart = 120.0;
        double ShardLength = 400.0;
        double TitleStart = 160.0;
        double TitleLength = 440.0;
        double SubtitleStart = 240.0;
        double SubtitleLength = 440.0;
        double MinimumShown = 800.0;
        double FadeOut = 200.0;
        double GiveUp = 30000.0;
    };

    void RunSplash(HANDLE ready, HANDLE process);
}
