using RLoop.Core;
using RLoop.ResoniteLink;

try
{
    if (args.Length == 3 && args[0] == "export")
        CatalogMapper.Export(CatalogMapper.LoadSnapshot(args[1])).Save(args[2]);
    else if (args.Length == 3 && args[0] == "import")
    {
        var catalog = ApplyCatalog.Load(args[1]);
        if (catalog.UnavailableReason() is { } reason) throw new RLoopException("APPLY_CATALOG_UNAVAILABLE", reason, ExitCodes.ValidationFailed);
        catalog.Save(args[2]);
    }
    else if (args.Length == 7 && args[0] == "capture" && args[1] == "--live" && args[2] == "--url" && args[4] == "--types")
    {
        var names = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(args[5])) ?? [];
        // Capture owns the two-minute budget so it can save partial limit evidence.
        var snapshot = await CatalogCapture.ReadAsync(new Uri(args[3]), names, CancellationToken.None);
        CatalogMapper.SaveSnapshot(snapshot, args[6]);
    }
    else
    {
        Console.Error.WriteLine("Usage: export SNAPSHOT.json CATALOG.json | import CATALOG.json OUTPUT.json | capture --live --url ws://localhost:PORT --types FULL_NAMES.json SNAPSHOT.json");
        return ExitCodes.InvalidArguments;
    }
    return ExitCodes.Success;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return ExitCodes.ValidationFailed;
}
