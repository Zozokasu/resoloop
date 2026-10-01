using RLoop.ContractGen;

if (args.Length != 1) throw new ArgumentException("Usage: dotnet run --project tools/RLoop.ContractGen -- tools/resoloop-jsx/src/generated");
Directory.CreateDirectory(args[0]);
foreach (var artifact in ContractGenerator.Generate())
    File.WriteAllText(Path.Combine(args[0], artifact.Key), artifact.Value, new System.Text.UTF8Encoding(false));
