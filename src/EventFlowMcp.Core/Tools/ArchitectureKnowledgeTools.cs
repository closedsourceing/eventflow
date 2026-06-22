using System.ComponentModel;
using EventFlowMcp.Abstractions.Rag;
using ModelContextProtocol.Server;

namespace EventFlowMcp.Core.Tools;

[McpServerToolType]
public sealed class ArchitectureKnowledgeTools
{
    [McpServerTool]
    [Description("Searches architecture knowledge through the configured RAG retriever. The retriever is vendor-neutral and can be backed by any vector DB or internal RAG service.")]
    public static async Task<string> SearchArchitectureKnowledge(
        IRagRetriever retriever,
        [Description("Question or search query.")] string query,
        [Description("Maximum number of chunks to return.")] int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        var results = await retriever.SearchAsync(
            new RagSearchRequest(query, Math.Clamp(maxResults, 1, 20)),
            cancellationToken);

        if (results.Count == 0)
        {
            return "No architecture knowledge was found. Configure Rag:Provider=InMemory for the demo, Rag:Provider=Http for an external RAG service, or plug in your own IRagRetriever implementation.";
        }

        return string.Join("\n\n---\n\n", results.Select(FormatResult));
    }

    [McpServerTool]
    [Description("Builds a RAG context block that an AI assistant can use to answer architecture questions from company knowledge.")]
    public static async Task<string> BuildArchitectureRagContext(
        IRagRetriever retriever,
        [Description("Question the developer wants answered.")] string question,
        CancellationToken cancellationToken = default)
    {
        var results = await retriever.SearchAsync(new RagSearchRequest(question, MaxResults: 5), cancellationToken);

        if (results.Count == 0)
            return "No relevant context found. Answer should state that the knowledge base has no matching architecture context.";

        return $"""
        Use this retrieved architecture context to answer the developer's question.

        Rules:
        - Prefer answers grounded in the retrieved context.
        - Mention the source/title when useful.
        - If context is insufficient, say what is missing.
        - Do not invent production facts.

        Developer question:
        {question}

        Retrieved context:
        {string.Join("\n\n---\n\n", results.Select(FormatResult))}
        """;
    }

    private static string FormatResult(RagResult r)
    {
        var metadata = r.Metadata is null || r.Metadata.Count == 0
            ? "n/a"
            : string.Join(", ", r.Metadata.Select(x => $"{x.Key}={x.Value}"));

        return $"""
        Id: {r.Id}
        Source: {r.Source}
        Title: {r.Title}
        Score: {r.Score?.ToString("0.000") ?? "n/a"}
        Metadata: {metadata}

        {r.Text}
        """;
    }
}
