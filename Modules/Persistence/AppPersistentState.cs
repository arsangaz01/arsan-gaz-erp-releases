using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ArsanGazERP.Modules.Persistence;

public sealed class AppPersistentState
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;
    private StateModel _state = new();

    public AppPersistentState()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArsanGazERP");
        Directory.CreateDirectory(folder);
        _filePath = Path.Combine(folder, "app-state.json");
        Load();
    }

    public string? ExcelFilePath =>
        !string.IsNullOrWhiteSpace(_state.ExcelFilePath) && File.Exists(_state.ExcelFilePath)
            ? _state.ExcelFilePath
            : null;

    public string? LastPage => _state.LastPage;
    public string? AccountHomeId => _state.AccountHomeId;
    public bool UserExplicitlySignedOut => _state.UserExplicitlySignedOut;

    public async Task SaveExcelPathAsync(string? path)
    {
        _state.ExcelFilePath = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        await SaveAsync().ConfigureAwait(false);
    }

    public async Task SaveLastPageAsync(string? page)
    {
        _state.LastPage = page;
        await SaveAsync().ConfigureAwait(false);
    }

    public async Task SaveAccountAsync(string? homeAccountId)
    {
        _state.AccountHomeId = homeAccountId;
        _state.UserExplicitlySignedOut = false;
        await SaveAsync().ConfigureAwait(false);
    }

    public async Task MarkSignedOutAsync()
    {
        _state.AccountHomeId = null;
        _state.UserExplicitlySignedOut = true;
        await SaveAsync().ConfigureAwait(false);
    }

    public async Task ResetAsync()
    {
        _state = new StateModel();
        await SaveAsync().ConfigureAwait(false);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
                _state = JsonSerializer.Deserialize<StateModel>(File.ReadAllText(_filePath), JsonOptions) ?? new();
        }
        catch
        {
            _state = new StateModel();
        }
    }

    private async Task SaveAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var temp = _filePath + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(_state, JsonOptions)).ConfigureAwait(false);
            File.Move(temp, _filePath, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class StateModel
    {
        public string? ExcelFilePath { get; set; }
        public string? LastPage { get; set; }
        public string? AccountHomeId { get; set; }
        public bool UserExplicitlySignedOut { get; set; }
    }
}
