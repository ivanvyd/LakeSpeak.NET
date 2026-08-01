using LakeSpeak.Application;
using LakeSpeak.Configuration;
using LakeSpeak.Genie;
using NSubstitute;

namespace LakeSpeak.Application.Tests;

/// <summary>
/// Resolution order decides which Genie Agent a question reaches. Getting it wrong answers
/// against the wrong data and looks exactly like success, so every branch is pinned here — this
/// project previously had no tests at all.
/// </summary>
public class AgentResolverTests
{
    private static IGenieClient ClientReturning(params GenieAgent[] agents)
    {
        var client = Substitute.For<IGenieClient>();
        client.ListAllAgentsAsync(Arg.Any<CancellationToken>()).Returns(AsAsync(agents));
        return client;
    }

    // A local helper rather than a System.Linq.Async dependency: the test project needs exactly
    // this one conversion, and a package reference for it would outlive the need.
    private static async IAsyncEnumerable<GenieAgent> AsAsync(GenieAgent[] agents)
    {
        foreach (var agent in agents)
        {
            yield return agent;
        }

        await Task.CompletedTask;
    }

    private static LakeSpeakConfig ConfigWith(params (string Alias, string Id)[] aliases)
    {
        var config = new LakeSpeakConfig();
        foreach (var (alias, id) in aliases)
        {
            config.Agents[alias] = new AgentAlias { Id = id };
        }

        return config;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_exact_id_resolves()
    {
        // Arrange
        var client = ClientReturning(new GenieAgent("01f-sales", "Sales"));
        var resolver = new AgentResolver(client, ConfigWith());

        // Act
        var resolution = await resolver.ResolveAsync("01f-sales", Ct);

        // Assert
        resolution.Agent!.AgentId.ShouldBe("01f-sales");
        resolution.IsAmbiguous.ShouldBeFalse();
    }

    [Fact]
    public async Task A_configured_alias_short_circuits_before_any_network_call()
    {
        // Arrange — an alias is an explicit instruction, so it is not second-guessed against the
        // listing. That also keeps the common path off the network.
        var client = ClientReturning();
        var resolver = new AgentResolver(client, ConfigWith(("finance", "01f-finance")));

        // Act
        var resolution = await resolver.ResolveAsync("finance", Ct);

        // Assert
        resolution.Agent!.AgentId.ShouldBe("01f-finance");
        client.DidNotReceive().ListAllAgentsAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("finance")]
    [InlineData("Finance")]
    [InlineData("FINANCE")]
    public async Task An_alias_matches_regardless_of_case(string typed)
    {
        // Arrange
        var resolver = new AgentResolver(ClientReturning(), ConfigWith(("Finance", "01f-finance")));

        // Act
        var resolution = await resolver.ResolveAsync(typed, Ct);

        // Assert
        resolution.Agent!.AgentId.ShouldBe("01f-finance");
    }

    [Fact]
    public async Task An_exact_title_resolves_when_it_is_unique()
    {
        // Arrange
        var client = ClientReturning(
            new GenieAgent("01f-a", "Sales Intelligence"),
            new GenieAgent("01f-b", "Platform Operations"));
        var resolver = new AgentResolver(client, ConfigWith());

        // Act
        var resolution = await resolver.ResolveAsync("Platform Operations", Ct);

        // Assert
        resolution.Agent!.AgentId.ShouldBe("01f-b");
    }

    [Fact]
    public async Task A_title_differing_only_by_case_still_resolves()
    {
        // Arrange
        var client = ClientReturning(new GenieAgent("01f-a", "Sales Intelligence"));
        var resolver = new AgentResolver(client, ConfigWith());

        // Act
        var resolution = await resolver.ResolveAsync("sales intelligence", Ct);

        // Assert
        resolution.Agent!.AgentId.ShouldBe("01f-a");
    }

    [Fact]
    public async Task Two_agents_sharing_a_title_are_ambiguous_rather_than_guessed()
    {
        // Arrange — picking the first of two Agents called Finance would silently answer against
        // the wrong data. This is the single most important case in the file.
        var client = ClientReturning(
            new GenieAgent("01f-a", "Finance"),
            new GenieAgent("01f-b", "Finance"));
        var resolver = new AgentResolver(client, ConfigWith());

        // Act
        var resolution = await resolver.ResolveAsync("Finance", Ct);

        // Assert
        resolution.Agent.ShouldBeNull();
        resolution.IsAmbiguous.ShouldBeTrue();
        resolution.Candidates.Count.ShouldBe(2);
    }

    [Fact]
    public async Task An_exact_title_wins_over_a_case_insensitive_near_match()
    {
        // Arrange — one exact match and one that differs by case. The exact one must win rather
        // than the pair being treated as ambiguous.
        var client = ClientReturning(
            new GenieAgent("01f-exact", "Finance"),
            new GenieAgent("01f-loose", "FINANCE"));
        var resolver = new AgentResolver(client, ConfigWith());

        // Act
        var resolution = await resolver.ResolveAsync("Finance", Ct);

        // Assert
        resolution.Agent!.AgentId.ShouldBe("01f-exact");
    }

    [Fact]
    public async Task An_unknown_name_is_not_found_rather_than_ambiguous()
    {
        // Arrange
        var client = ClientReturning(new GenieAgent("01f-a", "Sales"));
        var resolver = new AgentResolver(client, ConfigWith());

        // Act
        var resolution = await resolver.ResolveAsync("Marketing", Ct);

        // Assert — the two states drive different messages, so they must not collapse.
        resolution.NotFound.ShouldBeTrue();
        resolution.IsAmbiguous.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_name_is_not_found(string name)
    {
        // Arrange
        var resolver = new AgentResolver(ClientReturning(new GenieAgent("01f-a", "Sales")), ConfigWith());

        // Act
        var resolution = await resolver.ResolveAsync(name, Ct);

        // Assert
        resolution.NotFound.ShouldBeTrue();
    }
}
