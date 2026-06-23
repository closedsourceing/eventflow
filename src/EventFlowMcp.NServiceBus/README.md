# EventFlow MCP NServiceBus integration

`EventFlowProjectionBehavior` observes NServiceBus handler execution and writes selected operational metadata through `IOperationsProjectionWriter`.

Register it in an endpoint pipeline with a stable, bounded-context-specific `IMessageCorrelationResolver`. Return only business identifiers such as an `OrderId`; do not return message payload data, customer information, or credentials. The behavior never writes message bodies and keeps only selected NServiceBus headers.

Use `EventFlowMcp.PostgresProjection` for a durable shared writer, or `EventFlowMcp.LocalProjection` for a local-only demo.
