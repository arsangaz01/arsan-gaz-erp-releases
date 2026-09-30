using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace ArsanGazERP.Modules.Persistence;

public sealed class Microsoft365PersistentSessionService
{
    public const string ClientId = "3eaf1f8a-fbc8-40f0-9759-e134542606bf";
    public const string TenantId = "55b4c8c7-e0a8-46aa-953e-959f63fc7d91";

    private static readonly string[] DefaultScopes =
    {
        "User.Read", "Mail.Read", "Mail.Send", "Calendars.ReadWrite",
        "Files.ReadWrite.All", "Contacts.Read"
    };

    private readonly AppPersistentState _state;
    private readonly IPublicClientApplication _app;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private MsalCacheHelper? _cacheHelper;
    private bool _cacheReady;

    public Microsoft365PersistentSessionService(AppPersistentState state)
    {
        _state = state;
        _app = PublicClientApplicationBuilder.Create(ClientId)
            .WithTenantId(TenantId)
            .WithDefaultRedirectUri()
            .Build();
    }

    public IPublicClientApplication Client => _app;

    public async Task InitializeAsync()
    {
        if (_cacheReady) return;
        await _initGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cacheReady) return;
            var cacheFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ArsanGazERP", "Identity");
            Directory.CreateDirectory(cacheFolder);
            var storage = new StorageCreationPropertiesBuilder("msal-cache.bin3", cacheFolder).Build();
            _cacheHelper = await MsalCacheHelper.CreateAsync(storage).ConfigureAwait(false);
            _cacheHelper.VerifyPersistence();
            _cacheHelper.RegisterCache(_app.UserTokenCache);
            _cacheReady = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    public async Task<AuthenticationResult?> TrySignInSilentlyAsync(IEnumerable<string>? scopes = null)
    {
        await InitializeAsync().ConfigureAwait(false);
        if (_state.UserExplicitlySignedOut) return null;

        var accounts = (await _app.GetAccountsAsync().ConfigureAwait(false)).ToList();
        var account = accounts.FirstOrDefault(x => x.HomeAccountId?.Identifier == _state.AccountHomeId)
                      ?? accounts.FirstOrDefault();
        if (account is null) return null;

        try
        {
            var result = await _app.AcquireTokenSilent(scopes ?? DefaultScopes, account)
                .ExecuteAsync().ConfigureAwait(false);
            await _state.SaveAccountAsync(result.Account.HomeAccountId.Identifier).ConfigureAwait(false);
            return result;
        }
        catch (MsalUiRequiredException)
        {
            return null;
        }
    }

    public async Task<AuthenticationResult> SignInInteractiveAsync(IEnumerable<string>? scopes = null)
    {
        await InitializeAsync().ConfigureAwait(false);
        var result = await _app.AcquireTokenInteractive(scopes ?? DefaultScopes)
            .WithPrompt(Prompt.SelectAccount)
            .ExecuteAsync().ConfigureAwait(false);
        await _state.SaveAccountAsync(result.Account.HomeAccountId.Identifier).ConfigureAwait(false);
        return result;
    }

    public async Task<AuthenticationResult> GetTokenAsync(IEnumerable<string>? scopes = null)
    {
        return await TrySignInSilentlyAsync(scopes).ConfigureAwait(false)
               ?? await SignInInteractiveAsync(scopes).ConfigureAwait(false);
    }

    public async Task SignOutAsync()
    {
        await InitializeAsync().ConfigureAwait(false);
        foreach (var account in await _app.GetAccountsAsync().ConfigureAwait(false))
            await _app.RemoveAsync(account).ConfigureAwait(false);
        await _state.MarkSignedOutAsync().ConfigureAwait(false);
    }
}
