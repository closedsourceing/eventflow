# EventFlow MCP PostgreSQL projection

This package provides a durable `INServiceBusOperationsReader` and `IOperationsProjectionWriter` backed by PostgreSQL.

Use separate identities:

- A writer identity may initialize the narrow `eventflow` schema, record metadata, and apply retention.
- The EventFlow MCP server receives a SELECT-only identity.

The projection does not persist message bodies. It redacts sensitive header/state keys before writing and limits retained value length. Do not grant this identity access to application business tables.
