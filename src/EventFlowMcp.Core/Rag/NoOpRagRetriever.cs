using EventFlowMcp.Abstractions.Rag;

namespace EventFlowMcp.Core.Rag;

public sealed class NoOpRagRetriever : IRagRetriever
{
    public Task<IReadOnlyList<RagResult>> SearchAsync(
        RagSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<RagResult>>([]);
    }
}
