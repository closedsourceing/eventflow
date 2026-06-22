# EventFlow MCP

EventFlow MCP is a read-only MCP server for developers debugging and designing NServiceBus systems with AI. It turns operational message and saga data into tools that Codex, Claude Code, and GitHub Copilot can use safely.

The intended developer question is not merely “what failed?” but “why is order `10042` stuck, which saga owns it, and what is the safe next step?”

## What it provides

- Failed-message search and evidence-led explanation
- End-to-end command, event, and timeout tracing by message ID, conversation ID, or business correlation value
- Saga instance discovery and chronological saga timelines
- Endpoint health overview
- Handler idempotency and saga-design review
- Architecture-knowledge search through a pluggable RAG provider
- ADR drafting

All operational tools are read-only. Message bodies and saga state are omitted by default and require an explicit tool argument to include them. Sensitive header and state keys are redacted.

## Architecture

```text
MCP client (Codex / Claude / Copilot)
                 |
            EventFlow MCP
                 |
  INServiceBusOperationsReader + IRagRetriever
                 |
  local fixture | ServiceControl adapter | queue/projection adapter
```

`INServiceBusOperationsReader` is the stable EventFlow contract for message traces, failed messages, endpoint health, and sagas. This keeps MCP tools independent of any one data store or ServiceControl release.

ServiceControl supplies the required operational data when endpoints have auditing, heartbeats/monitoring, and `NServiceBus.SagaAudit` enabled. EventFlow includes a read-only HTTP adapter for the ServiceControl routes used by ServicePulse—failed messages, endpoints, conversations, and saga history. That HTTP API is intended for ServicePulse and may change, so pin, test, and upgrade this adapter against the exact ServiceControl version your team runs. ServiceControl remains the external source of truth; EventFlow never writes to it.

The built-in `InMemory` provider is a safe, realistic fixture for local development and demonstrations.

## Project structure

```text
src/
  EventFlowMcp.Abstractions/  # Operations and RAG contracts
  EventFlowMcp.Core/          # MCP tools and in-memory local fixture
  EventFlowMcp.Rag.Http/      # Generic HTTP RAG adapter
  EventFlowMcp.Tool/          # Local stdio MCP server / NuGet .NET tool
  EventFlowMcp.Server/        # Shared HTTP MCP server
deploy/
  helm/                       # Team Kubernetes deployment chart
  kubernetes/                 # Plain Kubernetes example
samples/                      # MCP client configuration examples
```

## Run locally with stdio

Requirements: .NET 8 SDK and an MCP-capable client.

```bash
dotnet run --project src/EventFlowMcp.Tool/EventFlowMcp.Tool.csproj -- \
  Operations:Provider=InMemory \
  Rag:Provider=InMemory
```

Use `samples/codex-config.toml` or `samples/vscode-mcp.json` as the client configuration starting point.

Useful prompts with the local fixture:

```text
Trace the business process for order-10042.
```

```text
Find active saga instances for order-10042 and show the saga timeline.
```

```text
Explain failed-001. Is it safe to retry?
```

```text
Review this saga for correlation and duplicate-delivery risks: <paste code>
```

## Run a shared server locally or for a team with Docker

The shared server exposes MCP at `http://localhost:3001/mcp` and health at `http://localhost:3001/healthz`.

```bash
export EVENTFLOW_MCP_API_KEY='use-a-long-random-value'
docker compose up --build -d
```

Clients may authenticate with either of these headers:

```text
Authorization: Bearer <EVENTFLOW_MCP_API_KEY>
```

```text
X-MCP-API-Key: <EVENTFLOW_MCP_API_KEY>
```

For a manually built image:

```bash
docker build -t eventflow-mcp:0.2.0 .
docker run --rm -p 3001:8080 \
  -e Mcp__ApiKey='use-a-long-random-value' \
  -e Operations__Provider=InMemory \
  eventflow-mcp:0.2.0
```

The image runs as a non-root user. The compose deployment is read-only and supplies a temporary `/tmp` filesystem.

To connect the container to a local ServiceControl instance, use Docker Desktop’s host alias (or the reachable ServiceControl DNS name in a shared network):

```bash
export EVENTFLOW_OPERATIONS_PROVIDER=ServiceControl
export EVENTFLOW_SERVICECONTROL_API_BASE_URL='http://host.docker.internal:33333/api/'
docker compose up --build -d
```

## Kubernetes with Helm

For a shared deployment, use a cluster secret instead of placing the API key in Helm values or shell history:

```bash
kubectl create namespace eventflow
kubectl -n eventflow create secret generic eventflow-mcp-auth \
  --from-literal=api-key='use-a-long-random-value'

helm upgrade --install eventflow ./deploy/helm/eventflow-mcp \
  --namespace eventflow \
  --values deploy/helm/eventflow-mcp/values.team.example.yaml
```

Set your real container repository and tag in the team values file. The chart creates a `ClusterIP` Service by default; enable and configure `ingress` only behind TLS and your organization’s network/access controls.

The chart includes non-root execution, a read-only container filesystem, API-key secret support, resource requests/limits, health probes, optional ingress, and optional HPA. It deliberately refuses to install if neither `mcp.apiKey` nor `mcp.existingSecret` is set.

Validate a release before applying it:

```bash
helm lint deploy/helm/eventflow-mcp --set mcp.apiKey=development-only-key
helm template eventflow deploy/helm/eventflow-mcp \
  --set image.repository=ghcr.io/acme/eventflow-mcp \
  --set mcp.apiKey=development-only-key > /tmp/eventflow-mcp.yaml
kubectl apply --dry-run=client -f /tmp/eventflow-mcp.yaml
```

## GitHub Actions delivery

The `CI` workflow builds the solution, packages the local MCP tool, validates the Helm chart, and builds the Docker image on every pull request and push to `main`.

The `Release` workflow runs only when a semantic version tag is pushed, for example:

```bash
git tag v0.2.0
git push origin v0.2.0
```

Configure these repository **secrets** before pushing a release tag:

| Secret | Purpose |
|---|---|
| `DOCKERHUB_USERNAME` | Docker Hub account that owns `eventflow-mcp` |
| `DOCKERHUB_TOKEN` | Docker Hub access token with push access |
| `NUGET_API_KEY` | API key for publishing `EventFlowMcp.Tool` to NuGet.org |
| `KUBE_CONFIG_DATA` | Base64-encoded kubeconfig; only required for cluster deployment |

The release publishes `<DockerHub username>/eventflow-mcp:vX.Y.Z`, updates the `latest` tag, and pushes the NuGet tool package. Kubernetes deployment is deliberately disabled unless this repository variable is set to `true`:

```text
KUBERNETES_DEPLOY_ENABLED=true
```

When enabling it, also define these repository **variables**:

| Variable | Example |
|---|---|
| `KUBERNETES_NAMESPACE` | `eventflow` |
| `KUBERNETES_RELEASE_NAME` | `eventflow-mcp` |
| `EVENTFLOW_MCP_SECRET_NAME` | `eventflow-mcp-auth` |
| `SERVICECONTROL_API_BASE_URL` | `http://servicecontrol.platform.svc.cluster.local:33333/api/` |

`EVENTFLOW_MCP_SECRET_NAME` must already exist in the target namespace and contain an `api-key` value. Give the kubeconfig only the permissions needed for the EventFlow namespace.

## Plugging in real NServiceBus data

The production integration belongs behind this interface:

```csharp
public interface INServiceBusOperationsReader
{
    Task<MessageTrace?> GetMessageTraceAsync(string identifier, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SagaInstance>> SearchSagasAsync(SagaSearchRequest request, CancellationToken cancellationToken = default);
    // failed-message and endpoint-health reads
}
```

The included ServiceControl HTTP reader is enabled with:

```bash
dotnet run --project src/EventFlowMcp.Server/EventFlowMcp.Server.csproj -- \
  Operations:Provider=ServiceControl \
  Operations:ServiceControl:ApiBaseUrl=http://localhost:33333/api/ \
  Mcp:ApiKey=use-a-long-random-value
```

It reads `errors`, `endpoints`, `conversations/{id}`, and `sagas/{id}` only. ServiceControl has no global saga-search API, so EventFlow resolves sagas from a traced conversation and requires a correlation value for saga discovery. Recommended alternatives when you need more control or long retention are:

1. A consumer of ServiceControl’s forwarded audit/error log queues that writes a minimal EventFlow read model.
2. An adapter over an existing internal observability projection.
3. A specialized `INServiceBusOperationsReader` for persistence-specific, live saga data where that access is justified.

Do not access ServiceControl’s embedded RavenDB directly. Keep EventFlow read-only until it has authorization, environment scoping, audit logging, PII controls, and human approval for any mutation.

## RAG

`Rag:Provider=InMemory` enables the local sample knowledge. To use an internal RAG endpoint, set `Rag:Provider=Http` and configure `Rag:Http`:

```json
{
  "Rag": {
    "Provider": "Http",
    "Http": {
      "Endpoint": "https://rag.company.example",
      "SearchPath": "/rag/search",
      "ApiKey": "secret",
      "ApiKeyHeaderName": "X-RAG-API-Key"
    }
  }
}
```

The request is a `RagSearchRequest`; the response is a JSON array of `RagResult` records.

## Next meaningful implementation step

Add contract tests against the exact ServiceControl deployment and authentication model used by your team. That turns the included adapter into a dependable evidence-backed assistant for a live NServiceBus environment without changing the MCP tools clients already rely on.
