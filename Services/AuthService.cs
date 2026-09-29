using System.Linq;
using System.Threading.Tasks;
using ArsanGazERP.Models;
using Microsoft.Identity.Client;

namespace ArsanGazERP.Services;

public sealed class AuthService
{
    private readonly IPublicClientApplication _client;

    public AuthService()
    {
        _client = PublicClientApplicationBuilder
            .Create(AppSettings.ClientId)
            .WithAuthority("https://login.microsoftonline.com/common")
            .WithRedirectUri(AppSettings.RedirectUri)
            .Build();
    }

    public async Task<AuthenticationResult> SignInAsync(bool forceConsent = false)
    {
        IAccount? account = (await _client.GetAccountsAsync()).FirstOrDefault();

        if (!forceConsent && account != null)
        {
            try
            {
                return await _client
                    .AcquireTokenSilent(AppSettings.GraphScopes, account)
                    .ExecuteAsync();
            }
            catch (MsalUiRequiredException)
            {
            }
        }

        AcquireTokenInteractiveParameterBuilder request = _client
            .AcquireTokenInteractive(AppSettings.GraphScopes)
            .WithUseEmbeddedWebView(false)
            .WithPrompt(forceConsent ? Prompt.Consent : Prompt.SelectAccount);

        return await request.ExecuteAsync();
    }

    public async Task<string> GetAccessTokenAsync()
    {
        IAccount? account = (await _client.GetAccountsAsync()).FirstOrDefault();

        if (account == null)
        {
            return (await SignInAsync()).AccessToken;
        }

        try
        {
            AuthenticationResult result = await _client
                .AcquireTokenSilent(AppSettings.GraphScopes, account)
                .ExecuteAsync();
            return result.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            return (await SignInAsync()).AccessToken;
        }
    }

    public async Task SignOutAsync()
    {
        foreach (IAccount account in await _client.GetAccountsAsync())
        {
            await _client.RemoveAsync(account);
        }
    }
}
