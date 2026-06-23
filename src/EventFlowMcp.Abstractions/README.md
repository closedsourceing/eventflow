# EventFlow MCP abstractions

Stable contracts for EventFlow operational readers, projection writers, message traces, failures, endpoint health, sagas, and RAG retrieval.

Use these contracts to keep NServiceBus telemetry storage separate from MCP-facing tools. Implementations should remain read-only on the MCP side and retain only the minimal metadata authorized for developer diagnostics.
