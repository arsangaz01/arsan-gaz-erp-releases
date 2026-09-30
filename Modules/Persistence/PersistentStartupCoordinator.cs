using System.Threading.Tasks;
using Microsoft.Identity.Client;

namespace ArsanGazERP.Modules.Persistence;

public sealed record StartupRestoreResult(
    bool Microsoft365Connected,
    string? ExcelFilePath,
    string? LastPage,
    IAccount? Account);

public sealed class PersistentStartupCoordinator
{
    private readonly Microsoft365PersistentSessionService _session;
    private readonly AppPersistentState _state;

    public PersistentStartupCoordinator(
        Microsoft365PersistentSessionService session,
        AppPersistentState state)
    {
        _session = session;
        _state = state;
    }

    public async Task<StartupRestoreResult> RestoreAsync()
    {
        var auth = await _session.TrySignInSilentlyAsync().ConfigureAwait(false);
        return new StartupRestoreResult(
            auth is not null,
            _state.ExcelFilePath,
            _state.LastPage,
            auth?.Account);
    }
}
