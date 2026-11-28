#include "Splash.h"

#include <d2d1.h>
#include <dwmapi.h>
#include <dwrite_1.h>
#include <shellscalingapi.h>
#include <wrl/client.h>

#include <algorithm>
#include <cmath>
#include <string_view>

using Microsoft::WRL::ComPtr;

namespace
{
    constexpr float CardWidth = 560.0f;
    constexpr float CardHeight = 320.0f;
    constexpr float CardRadius = 12.0f;
    constexpr float MarkWidth = 110.0f;
    constexpr float MarkHeight = 100.0f;
    constexpr float MarkNudge = 8.0f;
    constexpr float MarkRise = 6.0f;
    constexpr float ShardDrift = 2.8f;
    constexpr float BrandGap = 26.0f;
    constexpr float LineGap = 8.0f;
    constexpr float TitleRise = 8.0f;
    constexpr float TitleSize = 30.0f;
    constexpr float SubtitleSize = 12.0f;
    constexpr float TitleSpacing = 0.32f;
    constexpr float SubtitleSpacing = 0.42f;
    constexpr float StrokeWidth = 10.0f;
    constexpr float MiterLimit = 4.0f;
    constexpr float LayoutRoom = 4096.0f;
    constexpr float BaseDpi = 96.0f;
    constexpr float WorkAreaShare = 0.9f;
    constexpr UINT32 TopColor = 0x183D66;
    constexpr UINT32 BottomColor = 0x08111E;
    constexpr UINT32 SubtitleColor = 0xC4D6E8;
    constexpr UINT32 MarkColor = 0xFFFFFF;
    constexpr DWORD IdleWaitMs = 16;
    constexpr double FlushFloorMs = 1.0;
    constexpr BYTE Opaque = 255;
    constexpr wchar_t ClassName[] = L"NiskalaUtilitiesSplash";
    constexpr wchar_t WindowTitle[] = L"Niskala Utilities";
    constexpr wchar_t Locale[] = L"en-us";
    constexpr std::wstring_view TitleText = L"NISKALA";
    constexpr std::wstring_view SubtitleText = L"UTILITIES";

    double Clamp01(double value)
    {
        return std::clamp(value, 0.0, 1.0);
    }

    float MonitorScale(HMONITOR monitor, HWND window)
    {
        UINT dpiX = 0;
        UINT dpiY = 0;
        if (SUCCEEDED(GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &dpiX, &dpiY)) && dpiX != 0)
        {
            return static_cast<float>(dpiX) / BaseDpi;
        }

        const UINT dpi = GetDpiForWindow(window);
        return static_cast<float>(dpi == 0 ? USER_DEFAULT_SCREEN_DPI : dpi) / BaseDpi;
    }

    float FitScale(float scale, const RECT& work)
    {
        const float roomX = static_cast<float>(work.right - work.left) * WorkAreaShare / CardWidth;
        const float roomY = static_cast<float>(work.bottom - work.top) * WorkAreaShare / CardHeight;
        return std::max(0.5f, std::min({scale, roomX, roomY}));
    }

    double EaseOut(double t)
    {
        const double rest = 1.0 - Clamp01(t);
        return 1.0 - (rest * rest * rest);
    }

    double EaseInOut(double t)
    {
        t = Clamp01(t);
        return t < 0.5 ? 4.0 * t * t * t : 1.0 - (std::pow((-2.0 * t) + 2.0, 3.0) / 2.0);
    }

    double Eased(double now, double start, double length)
    {
        return length <= 0.0 ? 1.0 : EaseOut((now - start) / length);
    }

    class Clock
    {
    public:
        Clock()
        {
            QueryPerformanceFrequency(&_frequency);
            QueryPerformanceCounter(&_start);
        }

        double Now() const
        {
            LARGE_INTEGER now{};
            QueryPerformanceCounter(&now);
            return static_cast<double>(now.QuadPart - _start.QuadPart) * 1000.0 / static_cast<double>(_frequency.QuadPart);
        }

    private:
        LARGE_INTEGER _frequency{};
        LARGE_INTEGER _start{};
    };

    struct Frame
    {
        float Mark = 0.0f;
        float Shard = 0.0f;
        float Title = 0.0f;
        float Subtitle = 0.0f;

        bool operator==(const Frame&) const = default;
    };

    struct Text
    {
        ComPtr<IDWriteTextLayout> Layout;
        float Width = 0.0f;
        float Height = 0.0f;
    };

    LRESULT CALLBACK SplashProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
    {
        if (message == WM_MOUSEACTIVATE)
        {
            return MA_NOACTIVATE;
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }

    class Splash
    {
    public:
        Splash() = default;
        Splash(const Splash&) = delete;
        Splash& operator=(const Splash&) = delete;

        ~Splash()
        {
            if (_window != nullptr)
            {
                DestroyWindow(_window);
            }

            if (_dc != nullptr)
            {
                SelectObject(_dc, _previous);
                DeleteDC(_dc);
            }

            if (_bitmap != nullptr)
            {
                DeleteObject(_bitmap);
            }
        }

        bool Create()
        {
            const HINSTANCE instance = GetModuleHandleW(nullptr);
            WNDCLASSEXW type{};
            type.cbSize = sizeof(type);
            type.lpfnWndProc = SplashProc;
            type.hInstance = instance;
            type.hCursor = LoadCursorW(nullptr, IDC_APPSTARTING);
            type.lpszClassName = ClassName;
            if (RegisterClassExW(&type) == 0 && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
            {
                return false;
            }

            POINT cursor{};
            const HMONITOR monitor = GetCursorPos(&cursor) ? MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST) : MonitorFromPoint(POINT{0, 0}, MONITOR_DEFAULTTOPRIMARY);
            MONITORINFO info{};
            info.cbSize = sizeof(info);
            if (!GetMonitorInfoW(monitor, &info))
            {
                return false;
            }

            _window = CreateWindowExW(
                WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                ClassName,
                WindowTitle,
                WS_POPUP,
                info.rcWork.left,
                info.rcWork.top,
                1,
                1,
                nullptr,
                nullptr,
                instance,
                nullptr);
            if (_window == nullptr)
            {
                return false;
            }

            _scale = FitScale(MonitorScale(monitor, _window), info.rcWork);
            _size.cx = static_cast<LONG>(std::lround(CardWidth * _scale));
            _size.cy = static_cast<LONG>(std::lround(CardHeight * _scale));
            _position.x = info.rcWork.left + ((info.rcWork.right - info.rcWork.left - _size.cx) / 2);
            _position.y = info.rcWork.top + ((info.rcWork.bottom - info.rcWork.top - _size.cy) / 2);

            BITMAPINFO bitmap{};
            bitmap.bmiHeader.biSize = sizeof(bitmap.bmiHeader);
            bitmap.bmiHeader.biWidth = _size.cx;
            bitmap.bmiHeader.biHeight = -_size.cy;
            bitmap.bmiHeader.biPlanes = 1;
            bitmap.bmiHeader.biBitCount = 32;
            bitmap.bmiHeader.biCompression = BI_RGB;
            void* bits = nullptr;
            _bitmap = CreateDIBSection(nullptr, &bitmap, DIB_RGB_COLORS, &bits, nullptr, 0);
            _dc = CreateCompatibleDC(nullptr);
            if (_bitmap == nullptr || _dc == nullptr)
            {
                return false;
            }

            _previous = SelectObject(_dc, _bitmap);
            return CreateResources();
        }

        void Run(HANDLE ready, HANDLE process)
        {
            const Niskala::SplashTiming timing;
            Frame shown{-1.0f, -1.0f, -1.0f, -1.0f};
            int alphaShown = -1;
            bool signalled = false;
            double fadeFrom = -1.0;
            Render(Frame{});
            Present(0);
            ShowWindow(_window, SW_SHOWNOACTIVATE);
            const Clock clock;
            for (;;)
            {
                MSG message{};
                while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
                {
                    if (message.message == WM_QUIT)
                    {
                        return;
                    }

                    TranslateMessage(&message);
                    DispatchMessageW(&message);
                }

                KeepOnTop();
                const double now = clock.Now();
                signalled = signalled || WaitForSingleObject(ready, 0) == WAIT_OBJECT_0;
                const bool exited = WaitForSingleObject(process, 0) == WAIT_OBJECT_0;
                if (fadeFrom < 0.0 && ((signalled && now >= timing.MinimumShown) || exited || now >= timing.GiveUp))
                {
                    fadeFrom = now;
                }

                const Frame frame{
                    static_cast<float>(Eased(now, timing.MarkStart, timing.MarkLength)),
                    static_cast<float>(Eased(now, timing.ShardStart, timing.ShardLength)),
                    static_cast<float>(Eased(now, timing.TitleStart, timing.TitleLength)),
                    static_cast<float>(Eased(now, timing.SubtitleStart, timing.SubtitleLength)),
                };
                double alpha = EaseOut(now / timing.FadeIn);
                if (fadeFrom >= 0.0)
                {
                    const double t = (now - fadeFrom) / timing.FadeOut;
                    if (t >= 1.0)
                    {
                        Present(0);
                        return;
                    }

                    alpha *= 1.0 - EaseInOut(t);
                }

                const int level = static_cast<int>(std::lround(alpha * Opaque));
                const bool redraw = !(frame == shown);
                const bool changed = redraw || level != alphaShown;
                if (redraw)
                {
                    Render(frame);
                    shown = frame;
                }

                if (changed)
                {
                    Present(static_cast<BYTE>(level));
                    alphaShown = level;
                }

                if (changed || now < timing.FadeIn || fadeFrom >= 0.0)
                {
                    const double before = clock.Now();
                    if (FAILED(DwmFlush()) || clock.Now() - before < FlushFloorMs)
                    {
                        Sleep(1);
                    }

                    continue;
                }

                HANDLE handles[2] = {signalled ? process : ready, process};
                const DWORD count = signalled ? 1 : 2;
                const DWORD woke = MsgWaitForMultipleObjects(count, handles, FALSE, IdleWaitMs, QS_ALLINPUT);
                if (!signalled && woke == WAIT_OBJECT_0)
                {
                    signalled = true;
                }
            }
        }

    private:
        bool CreateResources()
        {
            if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, _factory.GetAddressOf()))
                || FAILED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), reinterpret_cast<IUnknown**>(_write.GetAddressOf()))))
            {
                return false;
            }

            const D2D1_RENDER_TARGET_PROPERTIES properties = D2D1::RenderTargetProperties(
                D2D1_RENDER_TARGET_TYPE_DEFAULT,
                D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED),
                BaseDpi,
                BaseDpi);
            if (FAILED(_factory->CreateDCRenderTarget(&properties, _target.GetAddressOf())))
            {
                return false;
            }

            const RECT bounds{0, 0, _size.cx, _size.cy};
            if (FAILED(_target->BindDC(_dc, &bounds)))
            {
                return false;
            }

            _target->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
            const D2D1_GRADIENT_STOP stops[] = {
                {0.0f, D2D1::ColorF(TopColor)},
                {1.0f, D2D1::ColorF(BottomColor)},
            };
            ComPtr<ID2D1GradientStopCollection> collection;
            if (FAILED(_target->CreateGradientStopCollection(stops, 2, collection.GetAddressOf()))
                || FAILED(_target->CreateLinearGradientBrush(
                    D2D1::LinearGradientBrushProperties(D2D1::Point2F(0.0f, 0.0f), D2D1::Point2F(0.0f, CardHeight)),
                    collection.Get(),
                    _card.GetAddressOf()))
                || FAILED(_target->CreateSolidColorBrush(D2D1::ColorF(MarkColor), _white.GetAddressOf()))
                || FAILED(_target->CreateSolidColorBrush(D2D1::ColorF(SubtitleColor), _muted.GetAddressOf())))
            {
                return false;
            }

            const D2D1_STROKE_STYLE_PROPERTIES stroke = D2D1::StrokeStyleProperties(
                D2D1_CAP_STYLE_FLAT, D2D1_CAP_STYLE_FLAT, D2D1_CAP_STYLE_FLAT, D2D1_LINE_JOIN_MITER, MiterLimit);
            if (FAILED(_factory->CreateStrokeStyle(&stroke, nullptr, 0, _stroke.GetAddressOf())))
            {
                return false;
            }

            const D2D1_POINT_2F outline[] = {
                {89, 9}, {89, 5}, {70, 5}, {70, 61}, {18, 5}, {5, 5}, {5, 95}, {24, 95}, {24, 40}, {75, 95}, {89, 95}, {89, 44},
            };
            const D2D1_POINT_2F upperTip[] = {{84, 9}, {94, 9}, {84, 19}};
            const D2D1_POINT_2F lowerTip[] = {{94, 34}, {94, 44}, {84, 44}};
            const D2D1_POINT_2F shard[] = {{95, 24}, {109, 10}, {109, 22}, {95, 36}};
            return Path(outline, false, _outline)
                && Path(upperTip, true, _upperTip)
                && Path(lowerTip, true, _lowerTip)
                && Path(shard, true, _shard)
                && Layout(TitleText, L"Segoe UI Variable Display", DWRITE_FONT_WEIGHT_SEMI_LIGHT, TitleSize, TitleSpacing, _title)
                && Layout(SubtitleText, L"Segoe UI Variable Text", DWRITE_FONT_WEIGHT_SEMI_BOLD, SubtitleSize, SubtitleSpacing, _subtitle);
        }

        template <std::size_t N>
        bool Path(const D2D1_POINT_2F (&points)[N], bool closed, ComPtr<ID2D1PathGeometry>& geometry)
        {
            ComPtr<ID2D1GeometrySink> sink;
            if (FAILED(_factory->CreatePathGeometry(geometry.GetAddressOf())) || FAILED(geometry->Open(sink.GetAddressOf())))
            {
                return false;
            }

            sink->BeginFigure(points[0], closed ? D2D1_FIGURE_BEGIN_FILLED : D2D1_FIGURE_BEGIN_HOLLOW);
            sink->AddLines(points + 1, static_cast<UINT32>(N - 1));
            sink->EndFigure(closed ? D2D1_FIGURE_END_CLOSED : D2D1_FIGURE_END_OPEN);
            return SUCCEEDED(sink->Close());
        }

        bool Layout(std::wstring_view text, const wchar_t* family, DWRITE_FONT_WEIGHT weight, float size, float spacing, Text& result)
        {
            ComPtr<IDWriteFontCollection> fonts;
            UINT32 index = 0;
            BOOL exists = FALSE;
            if (SUCCEEDED(_write->GetSystemFontCollection(fonts.GetAddressOf())))
            {
                fonts->FindFamilyName(family, &index, &exists);
            }

            ComPtr<IDWriteTextFormat> format;
            if (FAILED(_write->CreateTextFormat(
                    exists ? family : L"Segoe UI",
                    nullptr,
                    weight,
                    DWRITE_FONT_STYLE_NORMAL,
                    DWRITE_FONT_STRETCH_NORMAL,
                    size,
                    Locale,
                    format.GetAddressOf())))
            {
                return false;
            }

            format->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
            const UINT32 length = static_cast<UINT32>(text.size());
            if (FAILED(_write->CreateTextLayout(text.data(), length, format.Get(), LayoutRoom, LayoutRoom, result.Layout.GetAddressOf())))
            {
                return false;
            }

            const float tracking = spacing * size;
            ComPtr<IDWriteTextLayout1> spaced;
            const bool trailing = SUCCEEDED(result.Layout.As(&spaced))
                && SUCCEEDED(spaced->SetCharacterSpacing(0.0f, tracking, 0.0f, DWRITE_TEXT_RANGE{0, length}));
            DWRITE_TEXT_METRICS metrics{};
            if (FAILED(result.Layout->GetMetrics(&metrics)))
            {
                return false;
            }

            result.Width = metrics.width - (trailing ? tracking : 0.0f);
            result.Height = metrics.height;
            return true;
        }

        void Render(const Frame& frame)
        {
            const D2D1::Matrix3x2F scale = D2D1::Matrix3x2F::Scale(_scale, _scale);
            const float total = MarkHeight + BrandGap + _title.Height + LineGap + _subtitle.Height;
            const float top = (CardHeight - total) / 2.0f;
            const float markX = ((CardWidth - MarkWidth) / 2.0f) + MarkNudge;
            const float titleY = top + MarkHeight + BrandGap;
            const float subtitleY = titleY + _title.Height + LineGap;

            _target->BeginDraw();
            _target->SetTransform(scale);
            _target->Clear(D2D1::ColorF(0, 0.0f));
            _target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(0.0f, 0.0f, CardWidth, CardHeight), CardRadius, CardRadius), _card.Get());

            _target->SetTransform(D2D1::Matrix3x2F::Translation(markX, top + (MarkRise * (1.0f - frame.Mark))) * scale);
            _white->SetOpacity(frame.Mark);
            _target->DrawGeometry(_outline.Get(), _white.Get(), StrokeWidth, _stroke.Get());
            _target->FillGeometry(_upperTip.Get(), _white.Get());
            _target->FillGeometry(_lowerTip.Get(), _white.Get());

            const float drift = ShardDrift * (1.0f - frame.Shard);
            _target->SetTransform(D2D1::Matrix3x2F::Translation(markX - drift, top + (MarkRise * (1.0f - frame.Mark)) + drift) * scale);
            _white->SetOpacity(frame.Mark * frame.Shard);
            _target->FillGeometry(_shard.Get(), _white.Get());

            Draw(_title, titleY + (TitleRise * (1.0f - frame.Title)), frame.Title, _white.Get(), scale);
            Draw(_subtitle, subtitleY + (TitleRise * (1.0f - frame.Subtitle)), frame.Subtitle, _muted.Get(), scale);
            _target->EndDraw();
        }

        void Draw(const Text& text, float y, float opacity, ID2D1SolidColorBrush* brush, const D2D1::Matrix3x2F& scale)
        {
            _target->SetTransform(D2D1::Matrix3x2F::Translation((CardWidth - text.Width) / 2.0f, y) * scale);
            brush->SetOpacity(opacity);
            _target->DrawTextLayout(D2D1::Point2F(0.0f, 0.0f), text.Layout.Get(), brush, D2D1_DRAW_TEXT_OPTIONS_NONE);
        }

        void KeepOnTop()
        {
            if (GetWindow(_window, GW_HWNDPREV) != nullptr)
            {
                SetWindowPos(_window, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            }
        }

        void Present(BYTE alpha)
        {
            POINT source{0, 0};
            BLENDFUNCTION blend{AC_SRC_OVER, 0, alpha, AC_SRC_ALPHA};
            UpdateLayeredWindow(_window, nullptr, &_position, &_size, _dc, &source, 0, &blend, ULW_ALPHA);
        }

        HWND _window = nullptr;
        HDC _dc = nullptr;
        HBITMAP _bitmap = nullptr;
        HGDIOBJ _previous = nullptr;
        SIZE _size{};
        POINT _position{};
        float _scale = 1.0f;
        ComPtr<ID2D1Factory> _factory;
        ComPtr<IDWriteFactory> _write;
        ComPtr<ID2D1DCRenderTarget> _target;
        ComPtr<ID2D1LinearGradientBrush> _card;
        ComPtr<ID2D1SolidColorBrush> _white;
        ComPtr<ID2D1SolidColorBrush> _muted;
        ComPtr<ID2D1StrokeStyle> _stroke;
        ComPtr<ID2D1PathGeometry> _outline;
        ComPtr<ID2D1PathGeometry> _upperTip;
        ComPtr<ID2D1PathGeometry> _lowerTip;
        ComPtr<ID2D1PathGeometry> _shard;
        Text _title;
        Text _subtitle;
    };
}

void Niskala::RunSplash(HANDLE ready, HANDLE process)
{
    Splash splash;
    if (splash.Create())
    {
        splash.Run(ready, process);
    }
}
