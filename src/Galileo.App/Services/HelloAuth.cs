using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Security.Credentials.UI;
using WinRT;

namespace Galileo.Services;

/// <summary>
/// Windows Hello (biometrics / PIN) confirmation. Desktop apps must route the consent
/// prompt through IUserConsentVerifierInterop so it parents to our window.
/// </summary>
public static class HelloAuth
{
    [ComImport, Guid("39E050C3-4E74-441A-8DC0-B81104DF949C"),
     InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    private interface IUserConsentVerifierInterop
    {
        IAsyncOperation<UserConsentVerificationResult> RequestVerificationForWindowAsync(
            IntPtr appWindow,
            [MarshalAs(UnmanagedType.HString)] string message,
            in Guid riid);
    }

    /// <summary>
    /// Returns true/false if Hello ran (verified or not), or null if Hello isn't available
    /// on this device so the caller can fall back to its own confirmation.
    /// </summary>
    public static async Task<bool?> VerifyAsync(IntPtr hwnd, string message)
    {
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            if (availability.ToString() != "Available") return null; // enum value 0 == Available

            var factory = ActivationFactory.Get("Windows.Security.Credentials.UI.UserConsentVerifier");
            var interop = factory.AsInterface<IUserConsentVerifierInterop>();
            var iid = GuidGenerator.CreateIID(typeof(IAsyncOperation<UserConsentVerificationResult>));
            var result = await interop.RequestVerificationForWindowAsync(hwnd, message, iid);
            return result == UserConsentVerificationResult.Verified;
        }
        catch
        {
            return null; // treat any failure as "unavailable" → caller decides
        }
    }
}
