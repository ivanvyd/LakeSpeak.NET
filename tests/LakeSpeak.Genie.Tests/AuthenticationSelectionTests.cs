using LakeSpeak.Genie.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace LakeSpeak.Genie.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AuthenticationEnvironmentGroup
{
    public const string Name = "Authentication environment";
}

[Collection(AuthenticationEnvironmentGroup.Name)]
public sealed class AuthenticationSelectionTests : IDisposable
{
    private static readonly Uri Workspace = new("https://example.azuredatabricks.net");

    private static readonly string[] VariableNames =
    [
        EnvironmentTokenProvider.TokenVariable,
        M2mTokenProvider.ClientIdVariable,
        M2mTokenProvider.ClientSecretVariable,
    ];

    private readonly Dictionary<string, string?> _originalValues = VariableNames
        .ToDictionary(name => name, Environment.GetEnvironmentVariable);

    public AuthenticationSelectionTests()
    {
        foreach (var name in VariableNames)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void A_PAT_takes_precedence_over_an_incomplete_M2M_pair()
    {
        // Arrange
        Environment.SetEnvironmentVariable(EnvironmentTokenProvider.TokenVariable, "pat");
        Environment.SetEnvironmentVariable(M2mTokenProvider.ClientIdVariable, "client-id");

        // Act
        var providerType = ResolveProviderType();

        // Assert
        providerType.ShouldBe(typeof(EnvironmentTokenProvider));
    }

    [Fact]
    public void A_PAT_takes_precedence_over_a_complete_M2M_pair()
    {
        // Arrange
        Environment.SetEnvironmentVariable(EnvironmentTokenProvider.TokenVariable, "pat");
        Environment.SetEnvironmentVariable(M2mTokenProvider.ClientIdVariable, "client-id");
        Environment.SetEnvironmentVariable(M2mTokenProvider.ClientSecretVariable, "client-secret");

        // Act
        var providerType = ResolveProviderType();

        // Assert
        providerType.ShouldBe(typeof(EnvironmentTokenProvider));
    }

    [Theory]
    [InlineData(M2mTokenProvider.ClientIdVariable)]
    [InlineData(M2mTokenProvider.ClientSecretVariable)]
    public void An_incomplete_M2M_pair_fails_before_the_CLI_fallback(string configuredVariable)
    {
        // Arrange
        Environment.SetEnvironmentVariable(configuredVariable, "configured");

        // Act
        var exception = Should.Throw<InvalidOperationException>(() => ResolveProviderType());

        // Assert
        exception.Message.ShouldContain(M2mTokenProvider.ClientIdVariable);
        exception.Message.ShouldContain(M2mTokenProvider.ClientSecretVariable);
        exception.Message.ShouldContain("must be set together");
    }

    [Fact]
    public void A_complete_M2M_pair_selects_the_native_provider()
    {
        // Arrange
        Environment.SetEnvironmentVariable(M2mTokenProvider.ClientIdVariable, "client-id");
        Environment.SetEnvironmentVariable(M2mTokenProvider.ClientSecretVariable, "client-secret");

        // Act
        var providerType = ResolveProviderType();

        // Assert
        providerType.ShouldBe(typeof(M2mTokenProvider));
    }

    [Fact]
    public void No_environment_credentials_selects_the_CLI_broker()
    {
        // Arrange - the constructor cleared all credential variables.

        // Act
        var providerType = ResolveProviderType();

        // Assert
        providerType.ShouldBe(typeof(DatabricksCliTokenProvider));
    }

    [Fact]
    public void A_registered_provider_overrides_environment_selection()
    {
        // Arrange - a partial pair would fail if AddLakeSpeak replaced the existing provider.
        Environment.SetEnvironmentVariable(M2mTokenProvider.ClientIdVariable, "client-id");

        // Act
        var providerType = ResolveProviderType(services =>
            services.AddGenieTokenProvider(_ => ValueTask.FromResult("caller-token")));

        // Assert
        providerType.ShouldBe(typeof(DelegateTokenProvider));
    }

    public void Dispose()
    {
        foreach (var (name, value) in _originalValues)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static Type ResolveProviderType(Action<IServiceCollection>? registerFirst = null)
    {
        var services = new ServiceCollection();
        registerFirst?.Invoke(services);
        services.AddLakeSpeak(options => options.Host = Workspace);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IGenieTokenProvider>().GetType();
    }
}
