using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Cryptography;
using Windows.Win32.Security.WinTrust;

namespace Niskala.Interop.Security;

public enum SignatureCheck
{
    NotTrusted,
    MicrosoftSigned,
    OtherSigner,
}

public static class WinTrustNative
{
    private const CERT_CHAIN_POLICY_FLAGS ProductRoots = 0;
    private const CERT_CHAIN_POLICY_FLAGS ApplicationRoot = CERT_CHAIN_POLICY_FLAGS.MICROSOFT_ROOT_CERT_CHAIN_POLICY_CHECK_APPLICATION_ROOT_FLAG;

    public static SignatureCheck CheckFile(string path) => Check(path, null);

    public static SignatureCheck CheckFile(string path, SafeFileHandle file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return Check(path, file);
    }

    private static unsafe SignatureCheck Check(string path, SafeFileHandle? handle)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Guid action = PInvoke.WINTRUST_ACTION_GENERIC_VERIFY_V2;
        fixed (char* file = path)
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)sizeof(WINTRUST_FILE_INFO),
                pcwszFilePath = file,
                hFile = handle is null ? HANDLE.Null : (HANDLE)handle.DangerousGetHandle(),
            };

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(WINTRUST_DATA),
                dwUIChoice = WINTRUST_DATA_UICHOICE.WTD_UI_NONE,
                fdwRevocationChecks = WINTRUST_DATA_REVOCATION_CHECKS.WTD_REVOKE_NONE,
                dwUnionChoice = WINTRUST_DATA_UNION_CHOICE.WTD_CHOICE_FILE,
                dwStateAction = WINTRUST_DATA_STATE_ACTION.WTD_STATEACTION_VERIFY,
                dwProvFlags = WINTRUST_DATA_PROVIDER_FLAGS.WTD_CACHE_ONLY_URL_RETRIEVAL | WINTRUST_DATA_PROVIDER_FLAGS.WTD_REVOCATION_CHECK_NONE,
            };
            data.pFile = &fileInfo;

            try
            {
                if (PInvoke.WinVerifyTrust(HWND.Null, &action, &data) != 0)
                {
                    return SignatureCheck.NotTrusted;
                }

                CRYPT_PROVIDER_DATA* provider = PInvoke.WTHelperProvDataFromStateData(data.hWVTStateData);
                CRYPT_PROVIDER_SGNR* signer = provider is null ? null : PInvoke.WTHelperGetProvSignerFromChain(provider, 0, false, 0);
                if (signer is null || signer->pChainContext is null)
                {
                    return SignatureCheck.OtherSigner;
                }

                return HasMicrosoftRoot(signer->pChainContext, ProductRoots) || HasMicrosoftRoot(signer->pChainContext, ApplicationRoot)
                    ? SignatureCheck.MicrosoftSigned
                    : SignatureCheck.OtherSigner;
            }
            finally
            {
                data.dwStateAction = WINTRUST_DATA_STATE_ACTION.WTD_STATEACTION_CLOSE;
                PInvoke.WinVerifyTrust(HWND.Null, &action, &data);
            }
        }
    }

    private static unsafe bool HasMicrosoftRoot(CERT_CHAIN_CONTEXT* chain, CERT_CHAIN_POLICY_FLAGS flags)
    {
        var policy = new CERT_CHAIN_POLICY_PARA { cbSize = (uint)sizeof(CERT_CHAIN_POLICY_PARA), dwFlags = flags };
        var status = new CERT_CHAIN_POLICY_STATUS { cbSize = (uint)sizeof(CERT_CHAIN_POLICY_STATUS) };
        return PInvoke.CertVerifyCertificateChainPolicy(PInvoke.CERT_CHAIN_POLICY_MICROSOFT_ROOT, chain, &policy, &status) && status.dwError == 0;
    }
}
