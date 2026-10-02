namespace RLoop.IntegrationTests;

// Live tests that write to the same world share one per-URL session lock and must not run concurrently.
// A non-parallel collection also never runs alongside "Screenshot exports".
[CollectionDefinition("Live world writes", DisableParallelization = true)]
public sealed class LiveWorldWritesCollection;
