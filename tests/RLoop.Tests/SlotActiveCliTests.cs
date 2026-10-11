using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class SlotActiveCliTests
{
    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    public async Task SetWritesActiveAndKeepsOutputShape(string active)
    {
        var client = new SlotActiveTestClient();
        client.Slots["S1"] = new("S1", "Original", "Root", null, null, null, true, true, "", false, [], []);
        var (exit, report) = await Run(client, "slot", "set", "S1", "--active", active, "--json");
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(bool.Parse(active), Assert.Single(client.Updates).IsActive);
        var expected = JsonNode.Parse("""{"id":"S1","updated":true}""");
        var actual = JsonNode.Parse(report.RootElement.GetProperty("data").GetRawText());
        Assert.True(JsonNode.DeepEquals(expected, actual));
        report.Dispose();

        var inspected = await Run(client, "inspect", "S1", "--json");
        Assert.Equal(ExitCodes.Success, inspected.Exit);
        Assert.Equal(bool.Parse(active), inspected.Report.RootElement.GetProperty("data").GetProperty("isActive").GetBoolean());
        inspected.Report.Dispose();
    }

    [Fact]
    public async Task SetCombinesNameAndActiveAndEmptySetMentionsActive()
    {
        var client = new SlotActiveTestClient();
        client.Slots["S1"] = new("S1", "Original", "Root", null, null, null, true, true, "", false, [], []);
        var changed = await Run(client, "slot", "set", "S1", "--name", "Renamed", "--active", "false", "--json");
        Assert.Equal(ExitCodes.Success, changed.Exit);
        Assert.Equal("Renamed", Assert.Single(client.Updates).Name);
        Assert.False(client.Updates[0].IsActive);
        changed.Report.Dispose();

        var omittedClient = new SlotActiveTestClient();
        omittedClient.Slots["S1"] = new("S1", "Original", "Root", null, null, null, true, true, "", false, [], []);
        var omitted = await Run(omittedClient, "slot", "set", "S1", "--name", "Named", "--json");
        Assert.Equal(ExitCodes.Success, omitted.Exit);
        Assert.Null(Assert.Single(omittedClient.Updates).IsActive);
        omitted.Report.Dispose();

        var emptyClient = new SlotActiveTestClient();
        emptyClient.Slots["S1"] = client.Slots["S1"];
        var empty = await Run(emptyClient, "slot", "set", "S1", "--json");
        Assert.Equal(ExitCodes.InvalidArguments, empty.Exit);
        Assert.Equal("UPDATE_EMPTY", empty.Report.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("--active", empty.Report.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Empty(emptyClient.Updates);
        empty.Report.Dispose();
    }

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    public async Task CreateSupportsActiveAndOmissionPreservesNullRequest(string active)
    {
        var client = new SlotActiveTestClient();
        var (exit, report) = await Run(client, "slot", "create", "--name", "Created", "--active", active, "--json");
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(bool.Parse(active), Assert.Single(client.Creates).IsActive);
        Assert.Equal(new[] { "id", "parentId", "name", "generatedContentComponentId" },
            report.RootElement.GetProperty("data").EnumerateObject().Select(x => x.Name).ToArray());
        report.Dispose();

        var omitted = new SlotActiveTestClient();
        var omittedResult = await Run(omitted, "slot", "create", "--name", "Created", "--json");
        Assert.Equal(ExitCodes.Success, omittedResult.Exit);
        Assert.Null(Assert.Single(omitted.Creates).IsActive);
        omittedResult.Report.Dispose();
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData("TRUE")]
    [InlineData("False")]
    [InlineData(null)]
    public async Task InvalidActiveIsRejectedBeforeConnectionAndWrites(string? value)
    {
        var client = new SlotActiveTestClient();
        var args = new List<string> { "slot", "set", "S1" };
        if (value is null) args.Add("--active");
        else args.Add("--active=" + value);
        var (exit, report, connects) = await RunWithConnect(client, args.ToArray());
        Assert.Equal(ExitCodes.InvalidArguments, exit);
        Assert.Equal("INVALID_OPTION", report.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, connects);
        Assert.Empty(client.Updates);
        report.Dispose();
    }

    [Fact]
    public async Task MissingActiveBeforeAnotherOptionIsRejectedBeforeConnection()
    {
        var client = new SlotActiveTestClient();
        var (exit, report, connects) = await RunWithConnect(client, ["slot", "create", "--name", "Created", "--active", "--json"]);
        Assert.Equal(ExitCodes.InvalidArguments, exit);
        Assert.Equal("INVALID_OPTION", report.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, connects);
        Assert.Empty(client.Creates);
        report.Dispose();
    }

    private static async Task<(int Exit, JsonDocument Report)> Run(SlotActiveTestClient client, params string[] args)
    {
        var (exit, report, _) = await RunWithConnect(client, args);
        return (exit, report);
    }

    private static async Task<(int Exit, JsonDocument Report, int Connects)> RunWithConnect(SlotActiveTestClient client, string[] args)
    {
        var path = Path.Combine(Path.GetTempPath(), "slot-active-cli-" + Guid.NewGuid().ToString("N") + ".json");
        var connects = 0;
        try
        {
            var exit = await Program.RunAsync([.. args, "--url", client.Url, "--report", path], _ =>
            {
                connects++;
                return Task.FromResult<IResoniteClient>(client);
            });
            return (exit, JsonDocument.Parse(await File.ReadAllTextAsync(path)), connects);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
