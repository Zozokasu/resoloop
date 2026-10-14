namespace RLoop.IntegrationTests;

public sealed class LiveIntegrationGateTests
{
    [Fact]
    public void SkipReasonIsNullOnlyForOne()
    {
        Assert.Null(LiveIntegrationGate.SkipReason("1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    public void SkipReasonNamesTheVariableForOtherValues(string? value)
    {
        Assert.Equal("RESOLOOP_RUN_INTEGRATION is not 1", LiveIntegrationGate.SkipReason(value));
    }

    [Fact]
    public void ClosedGateReasonWinsOverUnmetCondition()
    {
        Assert.Equal("RESOLOOP_RUN_INTEGRATION is not 1", LiveIntegrationGate.SkipReason("0", "X is not 1", false));
    }

    [Fact]
    public void UnmetConditionIsReasonWhenGateIsOpen()
    {
        Assert.Equal("X is not 1", LiveIntegrationGate.SkipReason("1", "X is not 1", false));
    }

    [Fact]
    public void MetConditionRunsWhenGateIsOpen()
    {
        Assert.Null(LiveIntegrationGate.SkipReason("1", "X is not 1", true));
    }
}
