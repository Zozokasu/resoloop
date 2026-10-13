using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationStampTests
{
    private const string Nonce = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Generation = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Handwritten = "{\"version\":1,\"creationId\":\"room\",\"ownerNonce\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"generationId\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}";

    [Fact]
    public void SerializesInCanonicalHandwrittenOrderAndParsesExactly()
    {
        var stamp = new CreationStamp(1, "room", Nonce, Generation);

        Assert.Equal(Handwritten, stamp.Serialize());
        Assert.Equal(stamp, CreationStamp.Parse(Handwritten));
        Assert.True(CreationStamp.TryParse(Handwritten, out var parsed));
        Assert.Equal(stamp, parsed);
    }

    [Fact]
    public void GeneratesIndependent256BitLowercaseHexValues()
    {
        var nonce = CreationStamp.CreateOwnerNonce();
        var stamp = CreationStamp.Create("room", nonce);
        Assert.Equal(64, nonce.Length);
        Assert.All(nonce, IsLowerHex);
        Assert.Equal(64, stamp.GenerationId.Length);
        Assert.All(stamp.GenerationId, IsLowerHex);
        Assert.NotEqual(nonce, stamp.GenerationId);
        Assert.NotEqual(CreationStamp.Create("room", nonce).GenerationId, stamp.GenerationId);
        Assert.Equal(1, stamp.Version);
        Assert.Equal("room", stamp.CreationId);
    }

    [Fact]
    public void AppliesCreationIdAndNonceRulesWithoutNormalization()
    {
        var exact = Handwritten.Replace("\"room\"", "\"Room\"", StringComparison.Ordinal);
        Assert.Equal("Room", CreationStamp.Parse(exact).CreationId);
        Assert.False(CreationStamp.TryParse(Handwritten.Replace("\"room\"", "\"r\\u0000oom\"", StringComparison.Ordinal), out _));
        Assert.False(CreationStamp.TryParse(Handwritten.Replace("\"room\"", "\"" + new string('x', 129) + "\"", StringComparison.Ordinal), out _));
        Assert.Throws<RLoopException>(() => CreationStamp.Create("room", new string('a', 63)));
    }

    [Theory]
    [InlineData("{\"version\":1,\"creationId\":\"room\",\"ownerNonce\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"generationId\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"extra\":0}")]
    [InlineData("{\"version\":1,\"version\":1,\"creationId\":\"room\",\"ownerNonce\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"generationId\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}")]
    [InlineData("{\"version\":2,\"creationId\":\"room\",\"ownerNonce\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"generationId\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}")]
    [InlineData("{\"version\":1,\"creationId\":\"room\",\"ownerNonce\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"generationId\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}")]
    [InlineData("{\"version\":1,\"creationId\":\"\",\"ownerNonce\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"generationId\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}")]
    public void RejectsInvalidStampShapeAndValues(string json)
    {
        Assert.False(CreationStamp.TryParse(json, out _));
        var error = Assert.Throws<RLoopException>(() => CreationStamp.Parse(json));
        Assert.Equal("CREATION_OWNER_UNPROVEN", error.Code);
        Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
    }

    private static void IsLowerHex(char value) => Assert.True(value is >= '0' and <= '9' or >= 'a' and <= 'f');
}
