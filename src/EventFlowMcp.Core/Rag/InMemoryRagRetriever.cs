using EventFlowMcp.Abstractions.Rag;

namespace EventFlowMcp.Core.Rag;

public sealed class InMemoryRagRetriever : IRagRetriever
{
    private static readonly IReadOnlyList<RagResult> Documents =
    [
        new RagResult(
            Id: "adr-001",
            Source: "ADR",
            Title: "Outbox pattern for reliable integration events",
            Text: "When a service updates its database and publishes an integration event, use the Outbox pattern. Persist outgoing messages in the same transaction as the business state change, then dispatch them asynchronously. Consumers must be idempotent because duplicate delivery is possible.",
            Score: 1,
            Metadata: new Dictionary<string, string> { ["tags"] = "outbox,event-driven,idempotency" }),

        new RagResult(
            Id: "adr-002",
            Source: "Architecture Guide",
            Title: "Saga design rules",
            Text: "Use sagas or process managers for long-running business processes that span multiple bounded contexts. Saga state should be minimal, correlated by business identifiers, and designed for retries. Avoid putting business authority in the saga when a domain aggregate should own the decision.",
            Score: 1,
            Metadata: new Dictionary<string, string> { ["tags"] = "saga,ddd,nservicebus" }),

        new RagResult(
            Id: "adr-003",
            Source: "Operations Playbook",
            Title: "Retry and dead-letter handling",
            Text: "Transient failures should be retried with backoff. Poison messages should move to an error queue after the configured retry policy. Before retrying from ServiceControl, verify that the underlying dependency has recovered and that handlers are idempotent.",
            Score: 1,
            Metadata: new Dictionary<string, string> { ["tags"] = "retry,dead-letter,servicecontrol" })
    ];

    public Task<IReadOnlyList<RagResult>> SearchAsync(
        RagSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var terms = Tokenize(request.Query).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (terms.Count == 0)
            return Task.FromResult<IReadOnlyList<RagResult>>([]);

        var results = Documents
            .Select(doc => doc with { Score = Score(doc, terms) })
            .Where(doc => doc.Score >= (request.MinimumScore ?? 0.05))
            .OrderByDescending(doc => doc.Score)
            .Take(Math.Max(1, request.MaxResults))
            .ToList();

        return Task.FromResult<IReadOnlyList<RagResult>>(results);
    }

    private static double Score(RagResult doc, HashSet<string> queryTerms)
    {
        var metadataValues = doc.Metadata is null ? string.Empty : string.Join(' ', doc.Metadata.Values);
        var textTerms = Tokenize($"{doc.Title} {doc.Text} {metadataValues}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (textTerms.Count == 0)
            return 0;

        var overlap = queryTerms.Count(textTerms.Contains);
        return (double)overlap / queryTerms.Count;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        return text
            .Split([' ', '.', ',', ';', ':', '-', '_', '/', '\\', '(', ')', '[', ']', '{', '}', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 2)
            .Select(x => x.ToLowerInvariant());
    }
}
