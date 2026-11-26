#include <windows.h>
#include <shellapi.h>

#include <algorithm>
#include <string>
#include <string_view>

namespace
{
    constexpr std::wstring_view Product = L"Niskala Utilities";
    constexpr std::wstring_view Program = L"lib\\Niskala.exe";

    std::wstring ExecutableFolder()
    {
        std::wstring path(MAX_PATH, L'\0');
        for (;;)
        {
            const DWORD length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
            if (length == 0)
            {
                return {};
            }

            if (length < path.size())
            {
                path.resize(length);
                return path.substr(0, path.find_last_of(L'\\') + 1);
            }

            path.resize(path.size() * 2);
        }
    }

    std::wstring_view Arguments()
    {
        std::wstring_view line = GetCommandLineW();
        bool quoted = false;
        std::size_t index = 0;
        for (; index < line.size(); ++index)
        {
            if (line[index] == L'"')
            {
                quoted = !quoted;
            }
            else if (!quoted && (line[index] == L' ' || line[index] == L'\t'))
            {
                break;
            }
        }

        line.remove_prefix(index);
        line.remove_prefix(std::min(line.find_first_not_of(L" \t"), line.size()));
        return line;
    }

    void ReportFailure(DWORD error, const std::wstring& program)
    {
        wchar_t* reason = nullptr;
        FormatMessageW(
            FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
            nullptr,
            error,
            0,
            reinterpret_cast<wchar_t*>(&reason),
            0,
            nullptr);
        const std::wstring text = program + L"\n\n" + (reason != nullptr ? reason : L"");
        LocalFree(reason);
        MessageBoxW(nullptr, text.c_str(), Product.data(), MB_OK | MB_ICONERROR);
    }
}

int WINAPI wWinMain(_In_ HINSTANCE, _In_opt_ HINSTANCE, _In_ PWSTR, _In_ int show)
{
    const std::wstring program = ExecutableFolder() + std::wstring(Program);
    const std::wstring arguments(Arguments());

    SHELLEXECUTEINFOW info{};
    info.cbSize = sizeof(info);
    info.fMask = SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI;
    info.lpFile = program.c_str();
    info.lpParameters = arguments.empty() ? nullptr : arguments.c_str();
    info.nShow = show;
    if (ShellExecuteExW(&info))
    {
        return 0;
    }

    const DWORD error = GetLastError();
    if (error != ERROR_CANCELLED)
    {
        ReportFailure(error, program);
    }

    return static_cast<int>(error);
}
