using ArsanTupTakip.Api;
using ArsanTupTakip.Database;
using ArsanTupTakip.Models;
using ArsanTupTakip.Services;

namespace ArsanTupTakip.Tests;

// Request keys (a resent scan is applied once) and, with a PostgreSQL test database, optimistic concurrency on
// parallel updates of the same cylinder and vehicle.
internal static class ConcurrencyTests
{
    internal const string TestDatabaseVariable = "TUPTAKIP_TEST_PG_CONNECTION";

    internal static void Run()
    {
        RequestKeysAreClaimedOnce(new MemoryRequestKeyStore());
        string? connection = Environment.GetEnvironmentVariable(TestDatabaseVariable);
        if (string.IsNullOrWhiteSpace(connection))
        {
            Console.WriteLine($"ATLANDI: PostgreSQL eşzamanlılık testleri ({TestDatabaseVariable} tanımlı değil).");
            Console.WriteLine("PASS: istek anahtarları (bellek).");
            return;
        }

        Environment.SetEnvironmentVariable(TupTakipDatabase.ConnectionVariable, connection);
        TupTakipDatabase.EnsureSchemaAsync().GetAwaiter().GetResult();
        RequestKeysAreClaimedOnce(new PostgresRequestKeyStore());
        PermissionOverridesRoundTrip();
        ParallelCylinderUpdates().GetAwaiter().GetResult();
        StaleVehicleUpdateIsRefused().GetAwaiter().GetResult();
        Console.WriteLine("PASS: istek anahtarları, yetki istisnaları tablosu ve aynı kayda eşzamanlı güncellemeler (PostgreSQL).");
    }

    private static void RequestKeysAreClaimedOnce(IRequestKeyStore store)
    {
        int user = Random.Shared.Next(100_000, 900_000);
        string key = "/api/tup/hareket|" + Guid.NewGuid().ToString("N");
        ClaimResult[] results = [.. Enumerable.Range(0, 12).AsParallel().Select(_ => store.Claim(user, key).Result)];
        Check(results.Count(r => r == ClaimResult.Claimed) == 1 && results.Count(r => r == ClaimResult.InProgress) == 11,
            "Aynı anda gelen aynı istek anahtarını yalnızca bir istek işleyebilmeli.");

        store.Complete(user, key, new StoredResponse(200, "application/json", """{"message":"ok"}"""));
        var (result, response) = store.Claim(user, key);
        Check(result == ClaimResult.Completed && response?.Body == """{"message":"ok"}""", "Tamamlanan istek tekrarında ilk cevap dönmeli.");
        Check(store.Claim(user + 1, key).Result == ClaimResult.Claimed, "İstek anahtarları kullanıcıya özel olmalı.");

        string failed = key + "-hata";
        Check(store.Claim(user, failed).Result == ClaimResult.Claimed, "Yeni anahtar alınabilmeli.");
        store.Release(user, failed);
        Check(store.Claim(user, failed).Result == ClaimResult.Claimed, "Başarısız istek anahtarı bırakınca düzeltilmiş tekrar işlenebilmeli.");
    }

    private static void PermissionOverridesRoundTrip()
    {
        PostgresPermissionOverrideStore store = new();
        int user = Random.Shared.Next(100_000, 900_000);
        store.Replace(user, new Dictionary<string, bool> { [Perm.MoveScrap] = false, [Perm.AuditLog] = true }, "test");
        store.Replace(user, new Dictionary<string, bool> { [Perm.MoveScrap] = false }, "test");
        Dictionary<string, bool> loaded = store.LoadAll()[user];
        Check(loaded.Count == 1 && !loaded[Perm.MoveScrap], "Yetki istisnaları PostgreSQL'de saklanıp aynen okunmalı.");
        store.Replace(user, new Dictionary<string, bool>(), "test");
    }

    private static async Task ParallelCylinderUpdates()
    {
        CrudService crud = new();
        string serial = "ESZ-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        Cylinder cylinder = await crud.RegisterCylinderAsync(serial, null, "Oksijen", 10, "Litre", true, null, "eşzamanlılık testi");
        uint version = (await crud.FindCylinderByCodeAsync(serial))!.Version;

        // Two users opened the same cylinder (same version) and save different movements at the same moment.
        Task<string?>[] both =
        [
            crud.MoveCylinderAsync(cylinder.Id, CylinderStatus.Filling, null, "kullanıcı A", expectedVersion: version),
            crud.MoveCylinderAsync(cylinder.Id, CylinderStatus.Service, null, "kullanıcı B", expectedVersion: version)
        ];
        int saved = 0;
        int refused = 0;
        foreach (Task<string?> task in both)
        {
            try
            {
                await task;
                saved++;
            }
            catch (RecordChangedException exception)
            {
                Check(exception.Message == "Bu kayıt başka bir kullanıcı tarafından değiştirildi, yenileyip tekrar deneyin.", "Çakışma mesajı Türkçe ve açık olmalı.");
                refused++;
            }
        }

        Check(saved == 1 && refused == 1, $"Aynı sürümü güncelleyen iki kullanıcıdan yalnızca biri kaydetmeli (kaydedilen {saved}).");
        Check(await MovementCount(cylinder.Id, "kullanıcı") == 1, "Reddedilen güncelleme hareket kaydı bırakmamalı.");

        // Many parallel saves without a version: each one is either recorded completely or refused, never lost.
        int before = await MovementCount(cylinder.Id);
        CylinderStatus[] targets = [CylinderStatus.Warehouse, CylinderStatus.Filling, CylinderStatus.Service, CylinderStatus.Warehouse, CylinderStatus.Filling, CylinderStatus.Service];
        int[] outcomes = await Task.WhenAll(targets.Select(async (target, i) =>
        {
            try
            {
                await crud.MoveCylinderAsync(cylinder.Id, target, null, "paralel " + i);
                return 1;
            }
            catch (RecordChangedException)
            {
                return 0;
            }
        }));
        int recorded = await MovementCount(cylinder.Id) - before;
        Check(outcomes.Sum() >= 1 && recorded == outcomes.Sum(), $"Her başarılı kayıt tam bir hareket bırakmalı (başarılı {outcomes.Sum()}, hareket {recorded}).");
    }

    private static async Task StaleVehicleUpdateIsRefused()
    {
        VehicleService vehicles = new();
        string plate = "34 ES " + Random.Shared.Next(1000, 9999);
        Vehicle vehicle = await vehicles.CreateAsync(new VehicleInput(plate, "Test", null, null, 10, null, true));
        uint version = (await vehicles.ListAsync(includeInactive: true)).First(v => v.Id == vehicle.Id).Version;
        await vehicles.UpdateAsync(vehicle.Id, new VehicleInput(plate, "Birinci kullanıcı", null, null, 10, null, true), version);
        bool refused = false;
        try
        {
            await vehicles.UpdateAsync(vehicle.Id, new VehicleInput(plate, "İkinci kullanıcı", null, null, 10, null, true), version);
        }
        catch (RecordChangedException)
        {
            refused = true;
        }

        Check(refused, "Eski sürümle yapılan araç güncellemesi reddedilmeli.");
        Check((await vehicles.ListAsync(includeInactive: true)).First(v => v.Id == vehicle.Id).Name == "Birinci kullanıcı", "İlk kaydedilen değişiklik sessizce ezilmemeli.");
    }

    private static async Task<int> MovementCount(int cylinderId, string? noteStart = null)
    {
        await using TupTakipDbContext db = new();
        IQueryable<CylinderMovement> query = db.CylinderMovements.Where(m => m.CylinderId == cylinderId);
        if (noteStart is not null)
        {
            query = query.Where(m => m.Description != null && m.Description.StartsWith(noteStart));
        }

        return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(query);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
