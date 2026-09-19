namespace RLoop.IntegrationTests;

// The public screenshot exporter uses one shared directory and an exclusive lock.
[CollectionDefinition("Screenshot exports", DisableParallelization = true)]
public sealed class ScreenshotCollection;
