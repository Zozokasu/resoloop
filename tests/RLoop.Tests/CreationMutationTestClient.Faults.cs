namespace RLoop.Tests;

internal sealed partial class CreationMutationTestClient
{
    internal void RejectNextResponse() => RejectNextMutationResponse = true;

    internal void ChangeGenerationBeforeWriteGuard(int guardNumber, string generation)
    {
        BeforeGuardCallNumber = guardNumber;
        BeforeGuardAction = () => ConnectionGeneration = generation;
    }

    internal void MakeObservationPartialFor(string exactSlotId) => IncompleteSlotId = exactSlotId;
}
