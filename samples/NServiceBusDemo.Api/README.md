# NServiceBus demo API

This is a self-contained demo API for EventFlow. It hosts a local NServiceBus endpoint using Learning Transport and keeps one hardcoded order: `order-10042`.

Run it from the repository root:

```bash
$HOME/.dotnet/dotnet run --project samples/NServiceBusDemo.Api/NServiceBusDemo.Api.csproj
```

Then submit and approve the demo order:

```bash
curl -X POST http://localhost:5000/orders/order-10042/submit
curl -X POST http://localhost:5000/orders/order-10042/approve
curl http://localhost:5000/orders/order-10042
```

The local NServiceBus messages demonstrate command handlers and a saga. A demo-only pipeline behavior records selected operational metadata—message IDs, business correlation, handler timing, saga transitions, and failures—to `.eventflow/nservicebus-demo.operations.json`. It deliberately does **not** persist message bodies or arbitrary headers.

Start EventFlow's local stdio MCP server against that projection in a second terminal:

```bash
$HOME/.dotnet/dotnet run --project src/EventFlowMcp.Tool/EventFlowMcp.Tool.csproj -- \
  Operations:Provider=LocalProjection \
  Operations:LocalProjection:Path="$PWD/.eventflow/nservicebus-demo.operations.json" \
  Rag:Provider=InMemory
```

Then ask an MCP client to trace `order-10042` or find its sagas. The resulting data came from the local NServiceBus pipeline, not a simulated fixture. For a bounded end-to-end smoke test that automatically stops both processes, run:

```bash
bash samples/NServiceBusDemo.Api/run-local-demo.sh
```

To use a local PostgreSQL projection instead, set `EventFlow__Projection__Provider=Postgres` and provide `EventFlow__Projection__Postgres__ConnectionString` to the API. Point the EventFlow MCP server at the same database with `Operations:Provider=PostgresProjection` and a separate SELECT-only connection string. The writer creates the `eventflow` schema and retains operational metadata only; do not use a database credential that can read application business tables.
