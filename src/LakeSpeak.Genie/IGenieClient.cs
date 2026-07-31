namespace LakeSpeak.Genie;

/// <summary>
/// A client for the Databricks Genie Conversation API.
/// </summary>
/// <remarks>
/// The high-level entry points are <see cref="AskAsync"/> and <see cref="FollowUpAsync"/>: they
/// start or continue a conversation, poll to a terminal state, normalize the attachments, and
/// optionally fetch the query result — the sequence every caller would otherwise write. The
/// lower-level operations remain available for callers who need the individual steps.
/// </remarks>
public interface IGenieClient
{
    /// <summary>Lists Genie Agents one page at a time.</summary>
    Task<GenieAgentPage> ListAgentsAsync(
        string? pageToken = null,
        CancellationToken cancellationToken = default);

    /// <summary>Lists every Agent the caller can see, following pagination.</summary>
    IAsyncEnumerable<GenieAgent> ListAllAgentsAsync(CancellationToken cancellationToken = default);

    Task<GenieAgent> GetAgentAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>Starts a conversation and returns immediately, without waiting for an answer.</summary>
    Task<(GenieConversationRef Conversation, string MessageId)> StartConversationAsync(
        string agentId,
        string question,
        CancellationToken cancellationToken = default);

    /// <summary>Adds a message to an existing conversation and returns immediately.</summary>
    Task<string> SendMessageAsync(
        string agentId,
        string conversationId,
        string question,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches a message's current state without waiting.</summary>
    Task<GenieResponse> GetMessageAsync(
        string agentId,
        string conversationId,
        string messageId,
        CancellationToken cancellationToken = default);

    /// <summary>Polls until the message reaches a terminal state, the timeout elapses, or the token is cancelled.</summary>
    Task<GenieResponse> WaitForResponseAsync(
        string agentId,
        string conversationId,
        string messageId,
        GenieAskOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches the result of a query attachment.</summary>
    Task<GenieQueryResult?> GetQueryResultAsync(
        string agentId,
        string conversationId,
        string messageId,
        string attachmentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-runs an attachment's query after its cached result expired. Recovery for
    /// <see cref="GenieMessageState.QueryResultExpired"/>; does not re-ask the question.
    /// </summary>
    Task<GenieQueryResult?> ReExecuteQueryAsync(
        string agentId,
        string conversationId,
        string messageId,
        string attachmentId,
        CancellationToken cancellationToken = default);

    Task SendFeedbackAsync(
        string agentId,
        string conversationId,
        string messageId,
        GenieFeedbackRating rating,
        string? comment = null,
        CancellationToken cancellationToken = default);

    /// <summary>Asks a question in a new conversation and waits for the complete answer.</summary>
    Task<GenieResponse> AskAsync(
        string agentId,
        string question,
        GenieAskOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Asks a follow-up in an existing conversation and waits for the complete answer.</summary>
    Task<GenieResponse> FollowUpAsync(
        string agentId,
        string conversationId,
        string question,
        GenieAskOptions? options = null,
        CancellationToken cancellationToken = default);
}
