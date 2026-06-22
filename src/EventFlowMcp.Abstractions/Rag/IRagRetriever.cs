namespace EventFlowMcp.Abstractions.Rag;

public interface IRagRetriever
{
    Task<IReadOnlyList<RagResult>> SearchAsync(
        RagSearchRequest request,
        CancellationToken cancellationToken = default);
}
