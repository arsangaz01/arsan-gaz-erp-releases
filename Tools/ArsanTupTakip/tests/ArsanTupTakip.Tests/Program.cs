using ArsanTupTakip.Tests;

// The data folder is read once when AppPaths is first touched, so it must point at a scratch folder before any test runs.
string dataDirectory = Path.Combine(Path.GetTempPath(), "arsantuptakip-tests-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("TUPTAKIP_DATA_DIR", dataDirectory);
try
{
    CylinderTrackingTests.Run();
    CylinderTripTrackingTests.Run();
    ReorderPredictionTests.Run();
    CustomerStockTests.Run();
    VehicleTrackingTests.Run();
    StockCountTests.Run();
    LabelCodesTests.Run();
    ApiLogicTests.Run();
    PermissionTests.Run();
    ConcurrencyTests.Run();
    Console.WriteLine("Tüm ArsanTüpTakip testleri geçti.");
}
finally
{
    try
    {
        Directory.Delete(dataDirectory, recursive: true);
    }
    catch (IOException)
    {
    }
}
