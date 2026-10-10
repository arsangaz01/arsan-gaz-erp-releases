using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArsanGazERP.Models;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace ArsanGazERP.Services;

public sealed class AuthService
{
    // Shared with AgentWatcher and CampaignTransport so one sign-in survives restarts until sign-out.
    private static readonly Lazy<Task<IPublicClientApplication>> SharedClient = new(CreateClientAsync);

    private static async Task<IPublicClientApplication> CreateClientAsync()
    {
        IPublicClientApplication client = PublicClientApplicationBuilder
            .Create(AppSettings.ClientId)
            .WithAuthority(AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount)
            .WithRedirectUri(AppSettings.RedirectUri)
            .Build();
        string cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArsanGazERP", "Auth");
        Directory.CreateDirectory(cacheDirectory);
        MsalCacheHelper cache = await MsalCacheHelper.CreateAsync(
            new StorageCreationPropertiesBuilder("msalcache.bin3", cacheDirectory).Build());
        cache.RegisterCache(client.UserTokenCache);
        return client;
    }

    private static Task<IPublicClientApplication> ClientAsync() => SharedClient.Value;

    public async Task<AuthenticationResult> SignInAsync(bool forceConsent = false)
    {
        IPublicClientApplication client = await ClientAsync();
        IAccount? account = (await client.GetAccountsAsync()).FirstOrDefault();

        if (!forceConsent && account != null)
        {
            try
            {
                return await client
                    .AcquireTokenSilent(AppSettings.GraphScopes, account)
                    .ExecuteAsync();
            }
            catch (MsalUiRequiredException)
            {
            }
        }

        AcquireTokenInteractiveParameterBuilder request = client
            .AcquireTokenInteractive(AppSettings.GraphScopes)
            .WithUseEmbeddedWebView(false);
        request = forceConsent
            ? request.WithPrompt(Prompt.Consent)
            : account != null
                ? request.WithAccount(account)
                : request.WithPrompt(Prompt.SelectAccount);

        return await request.ExecuteAsync();
    }

    public async Task<string> GetAccessTokenAsync()
    {
        IPublicClientApplication client = await ClientAsync();
        IAccount? account = (await client.GetAccountsAsync()).FirstOrDefault();

        if (account == null)
        {
            return (await SignInAsync()).AccessToken;
        }

        try
        {
            AuthenticationResult result = await client
                .AcquireTokenSilent(AppSettings.GraphScopes, account)
                .ExecuteAsync();
            return result.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            return (await SignInAsync()).AccessToken;
        }
    }

    public async Task<string> GetCachedAccessTokenAsync()
    {
        IPublicClientApplication client = await ClientAsync();
        IAccount? account = (await client.GetAccountsAsync()).FirstOrDefault();
        if (account is null)
        {
            throw new InvalidOperationException("Microsoft 365 oturumu açık değil.");
        }

        AuthenticationResult result = await client
            .AcquireTokenSilent(AppSettings.GraphScopes, account)
            .ExecuteAsync();
        return result.AccessToken;
    }

    public async Task<string?> GetCachedAccountNameAsync()
    {
        IPublicClientApplication client = await ClientAsync();
        IAccount? account = (await client.GetAccountsAsync()).FirstOrDefault();
        return account?.Username;
    }

    public async Task SignOutAsync()
    {
        IPublicClientApplication client = await ClientAsync();
        foreach (IAccount account in await client.GetAccountsAsync())
        {
            await client.RemoveAsync(account);
        }
    }
}
