using LakeSpeak.Cli.Commands;
using LakeSpeak.Configuration;
using LakeSpeak.Genie;
using LakeSpeak.QuestionPacks;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LakeSpeak.Cli.Tests;

public sealed class PackPolicyCommandTests
{
    [Theory]
    [InlineData(false, false, ExitCode.GenieFailure)]
    [InlineData(true, false, ExitCode.PartialPackFailure)]
    [InlineData(false, true, ExitCode.GenieFailure)]
    [InlineData(true, true, ExitCode.PartialPackFailure)]
    public async Task Policy_failures_preserve_stop_or_publish_partial_report_semantics(
        bool continueOnFailure, bool truncated, int expectedExit)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lakespeak-pack-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "report.md");
        await File.WriteAllTextAsync(target, "existing report", TestContext.Current.CancellationToken);
        var client = Client(truncated);

        try
        {
            var exit = await RunAsync(
                Pack(directory, continueOnFailure), target, client, TestContext.Current.CancellationToken);

            exit.ShouldBe(expectedExit);
            var report = await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken);
            if (continueOnFailure)
            {
                report.ShouldContain("1 of 2 questions failed");
                report.ShouldContain("behavior.requireQueryResult requires");
                report.ShouldContain("## second");
                report.ShouldContain("Check the generated SQL");
                await client.Received(1).AskAsync(
                    "agent-1", "second", Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>());
            }
            else
            {
                report.ShouldBe("existing report");
                await client.DidNotReceive().AskAsync(
                    "agent-1", "second", Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>());
            }

            Directory.EnumerateFiles(directory).ShouldBe([target]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cancellation_does_not_publish_a_policy_failure_report()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var directory = Path.Combine(Path.GetTempPath(), $"lakespeak-pack-policy-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "report.md");
        await File.WriteAllTextAsync(target, "existing report", TestContext.Current.CancellationToken);
        var client = Client(truncated: false);
        client.AskAsync("agent-1", "first", Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Response(null);
            });

        try
        {
            var exit = await RunAsync(Pack(directory, continueOnFailure: true), target, client, cancellation.Token);

            exit.ShouldBe(ExitCode.Timeout);
            (await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken)).ShouldBe("existing report");
            Directory.EnumerateFiles(directory).ShouldBe([target]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static IGenieClient Client(bool truncated)
    {
        var client = Substitute.For<IGenieClient>();
        client.AskAsync("agent-1", "first", Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response(truncated ? new GenieQueryResult([], [], true, 1) : null));
        client.AskAsync("agent-1", "second", Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response(new GenieQueryResult([new GenieColumn("count", "INT")], [], false, 0)));
        return client;
    }

    private static GenieResponse Response(GenieQueryResult? result) => new(
        "agent-1", "conversation", "message", GenieMessageState.Completed,
        "Which quarter?", null, result, [], new GenieResponseMetadata(TimeSpan.Zero, 1));

    private static QuestionPack Pack(string directory, bool continueOnFailure) => new(
        "policy", null, "sales", null,
        [new PackQuestion("first", null, "first", null), new PackQuestion("second", null, "second", null)],
        new PackOutput("markdown", null),
        new PackBehavior(continueOnFailure, false, false, false, TimeSpan.FromMinutes(1))
        {
            RequireQueryResult = true,
        })
    {
        BaseDirectory = directory,
    };

    private static Task<int> RunAsync(
        QuestionPack pack, string target, IGenieClient client, CancellationToken cancellationToken)
    {
        var parsed = Program.CreateRootCommand().Parse(["pack", "run", "unused.yaml", "--output", target, "--force"]);
        var config = new LakeSpeakConfig
        {
            Agents = new Dictionary<string, AgentAlias>(StringComparer.OrdinalIgnoreCase)
            {
                ["sales"] = new() { Id = "agent-1" },
            },
        };
        return CliHost.RunAsync(
            parsed,
            (host, ct) => PackCommand.RunAsync(host, parsed, pack, ct),
            cancellationToken,
            CliProfileRequest.ForNewCommand(pack.Agent, pack.Profile),
            (parseResult, request) => CliHost.Create(
                parseResult, request!, config, recent: null,
                (_, _, _) => new Uri("https://offline.example/"),
                services => services.AddSingleton(client)));
    }
}
