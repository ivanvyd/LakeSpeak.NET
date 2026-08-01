using LakeSpeak.Configuration;

namespace LakeSpeak.QuestionPacks.Tests;

/// <summary>
/// Alias lookup must be case-insensitive after a YAML round trip.
/// </summary>
/// <remarks>
/// The dictionary is declared with <c>StringComparer.OrdinalIgnoreCase</c>, but YamlDotNet builds
/// its own dictionary and assigns it over the field initializer, silently discarding the
/// comparer. The declaration therefore proves nothing on its own — only a round trip does.
/// </remarks>
public sealed class ConfigAliasTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lakespeak-{Guid.NewGuid():N}.yaml");

    private LakeSpeakConfig Load(string yaml)
    {
        File.WriteAllText(_path, yaml);
        return LakeSpeakConfig.Load(_path);
    }

    [Theory]
    [InlineData("finance")]
    [InlineData("Finance")]
    [InlineData("FINANCE")]
    [InlineData("FiNaNcE")]
    public void An_alias_resolves_regardless_of_case(string typed)
    {
        // Arrange
        var config = Load(
            """
            version: 1
            agents:
              Finance:
                id: 01f-finance
            """);

        // Act
        var found = config.Agents.TryGetValue(typed, out var alias);

        // Assert
        found.ShouldBeTrue();
        alias!.Id.ShouldBe("01f-finance");
    }

    [Fact]
    public void The_comparer_survives_deserialisation()
    {
        // Arrange — falling through to title matching on a case mismatch is not a harmless extra
        // round trip: it can select a different Agent whose title happens to match, which is the
        // answer-against-the-wrong-data failure the resolver exists to prevent.
        var config = Load(
            """
            version: 1
            agents:
              Sales:
                id: 01f-sales
            """);

        // Act
        var comparer = config.Agents.Comparer;

        // Assert
        comparer.ShouldBe(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_config_with_no_agents_section_still_has_a_case_insensitive_dictionary()
    {
        // Arrange
        var config = Load("version: 1");

        // Act
        var comparer = config.Agents.Comparer;

        // Assert
        comparer.ShouldBe(StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
