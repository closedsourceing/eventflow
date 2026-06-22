using System.ComponentModel;
using ModelContextProtocol.Server;

namespace EventFlowMcp.Core.Tools;

[McpServerToolType]
public sealed class DistributedSystemsAdvisorTools
{
    [McpServerTool]
    [Description("Reviews an event-driven design idea and returns practical distributed-system recommendations.")]
    public static string ReviewEventDrivenDesign(
        [Description("Short description of the business scenario or design.")] string scenario,
        [Description("Messaging technology, e.g. NServiceBus, RabbitMQ, Kafka, Azure Service Bus.")] string messaging = "NServiceBus/RabbitMQ",
        [Description("Whether high availability is required.")] bool highAvailabilityRequired = true)
    {
        var ha = highAvailabilityRequired
            ? "Design for redundant endpoints, durable queues, health checks, retry/backoff, dead-letter handling, monitored dependencies, and documented recovery procedures."
            : "Start with a simpler deployment, but keep handlers idempotent and avoid design choices that block later HA.";

        return $"""
        Event-driven design review

        Scenario:
        {scenario}

        Suggested approach:
        - Start with DDD boundaries: identify bounded contexts and ownership of business decisions.
        - Publish integration events only for facts that already happened.
        - Keep commands intentional and targeted; keep events as durable business facts.
        - Use {messaging} for asynchronous communication between contexts.
        - Use the Outbox pattern where database state changes and outgoing messages must be consistent.
        - Make consumers idempotent because duplicate delivery can happen.
        - Use sagas/process managers for long-running processes across bounded contexts.
        - For audit-heavy domains, consider event sourcing, but do not force it everywhere.
        - {ha}

        First architecture artifact to create:
        A one-page ADR describing the bounded contexts, message contracts, consistency model, retry strategy, and ownership of each event.
        """;
    }

    [McpServerTool]
    [Description("Creates a first ADR draft for a distributed-system architecture decision.")]
    public static string CreateAdrDraft(
        [Description("Title of the architecture decision.")] string title,
        [Description("Context/problem statement.")] string context,
        [Description("Chosen decision.")] string decision,
        [Description("Consequences, tradeoffs, or risks.")] string consequences = "")
    {
        return $"""
        # ADR: {title}

        ## Status
        Proposed

        ## Context
        {context}

        ## Decision
        {decision}

        ## Consequences
        {(string.IsNullOrWhiteSpace(consequences) ? "- To be completed.\n- Include operational impact, failure modes, ownership, and rollback strategy." : consequences)}

        ## Operational notes
        - Define monitoring and alerting.
        - Define retry and dead-letter behavior.
        - Confirm idempotency expectations.
        - Confirm whether data is safe to expose to AI-assisted tooling.
        """;
    }

    [McpServerTool]
    [Description("Reviews an NServiceBus message handler for idempotency, retries, side effects, and Outbox safety based on a pasted description or code snippet.")]
    public static string ReviewHandlerDesign(
        [Description("Handler description or code snippet.")] string handlerDescription)
    {
        return $"""
        Handler design review

        Handler/content reviewed:
        {handlerDescription}

        Check these points:
        - Does the handler use a stable business key, not only a generated technical id?
        - Can the same message be processed twice without creating duplicate side effects?
        - Are external API calls guarded by idempotency keys or compensating checks?
        - Are database writes protected by unique constraints or existing-state checks?
        - Is the Outbox enabled for outgoing messages?
        - Are retries safe if the dependency timed out after completing the operation?

        Recommended outcome:
        Treat the handler as non-idempotent until duplicate processing has been proven safe with tests or database constraints.
        """;
    }

    [McpServerTool]
    [Description("Reviews an NServiceBus saga design for correlation, ordering, timeout, completion, and duplicate-delivery risks based on a pasted description or code snippet.")]
    public static string ReviewSagaDesign(
        [Description("Saga description or code snippet, including messages, correlation mapping, and completion behavior if available.")] string sagaDescription)
    {
        return $"""
        Saga design review

        Saga/content reviewed:
        {sagaDescription}

        Check these points:
        - Is the saga correlated by a stable business identifier, rather than the internal NServiceBus saga Id?
        - Can every externally-originated message that may arrive first create the saga instance, rather than being silently discarded?
        - Is the correlation value unique and protected by the selected saga persistence?
        - Does every state transition tolerate duplicate delivery and retry after a partial external side effect?
        - Are timeouts explicit, idempotent, and cancelled or ignored safely after completion?
        - Does the completion path avoid losing outgoing messages when persistence cannot atomically store saga state and dispatch messages?
        - Is business authority kept in the appropriate aggregate/service instead of concentrating unrelated decisions in the saga?
        - Is there an observable terminal state, plus a recovery path for stuck or orphaned instances?

        Recommended tests:
        1. Each permitted starter arrives first.
        2. The same input is delivered twice.
        3. Two correlated inputs arrive concurrently or out of order.
        4. A timeout arrives after the saga completes.
        5. A dependency succeeds but acknowledgement fails, causing a retry.
        """;
    }
}
