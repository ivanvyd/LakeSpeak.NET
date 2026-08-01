using LakeSpeak.Configuration;
using LakeSpeak.Genie;

namespace LakeSpeak.Application;

public sealed record AgentResolution(GenieAgent? Agent, IReadOnlyList<GenieAgent> Candidates)
{
    public bool IsAmbiguous => Agent is null && Candidates.Count > 1;

    public bool NotFound => Agent is null && Candidates.Count == 0;
}

/// <summary>
/// Turns whatever the user typed into a single Agent.
/// </summary>
/// <remarks>
/// Resolution order is exact id, configured alias, exact title, then case-insensitive title.
/// Ambiguity is returned rather than guessed: picking the first of two Agents called "Finance"
/// would silently answer a question against the wrong data, which is worse than an error.
/// </remarks>
public sealed class AgentResolver(IGenieClient client, LakeSpeakConfig config)
{
    public async Task<AgentResolution> ResolveAsync(string nameOrId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nameOrId))
        {
            return new AgentResolution(null, []);
        }

        if (config.Agents.TryGetValue(nameOrId, out var alias) && !string.IsNullOrEmpty(alias.Id))
        {
            // A configured alias is an explicit instruction and is not second-guessed against
            // the listing, which also avoids a network call on the common path.
            return new AgentResolution(new GenieAgent(alias.Id, nameOrId), []);
        }

        // An id can be fetched directly. Without this, resolving one means paging the whole
        // listing until it turns up — four round trips in a workspace with 350 Agents, for the
        // form scripts and Question Packs are told to prefer. Gated on the id shape so that a
        // title like "sales" does not spend a guaranteed 404 before the listing it actually needs.
        if (LooksLikeAgentId(nameOrId))
        {
            try
            {
                return new AgentResolution(
                    await client.GetAgentAsync(nameOrId, cancellationToken).ConfigureAwait(false), []);
            }
            catch (GenieException ex) when (ex.Kind is GenieFailureKind.AgentNotFound)
            {
                // An id-shaped string that is not an Agent is still a legal title. Fall through.
            }
        }

        var agents = new List<GenieAgent>();
        await foreach (var agent in client.ListAllAgentsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(agent.AgentId, nameOrId, StringComparison.Ordinal))
            {
                return new AgentResolution(agent, []);
            }

            agents.Add(agent);
        }

        var exact = agents.Where(a => string.Equals(a.Title, nameOrId, StringComparison.Ordinal)).ToList();
        if (exact.Count == 1)
        {
            return new AgentResolution(exact[0], []);
        }

        if (exact.Count > 1)
        {
            return new AgentResolution(null, exact);
        }

        var loose = agents
            .Where(a => string.Equals(a.Title, nameOrId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return loose.Count == 1
            ? new AgentResolution(loose[0], [])
            : new AgentResolution(null, loose);
    }

    /// <summary>
    /// Whether a string has the shape of a Genie Agent id — 32 lowercase hex characters, as every
    /// id Databricks has returned takes that form. Deliberately a shape test and not a validity
    /// test: a false positive costs one 404 and falls through, a false negative costs nothing.
    /// </summary>
    private static bool LooksLikeAgentId(string value) =>
        value.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
