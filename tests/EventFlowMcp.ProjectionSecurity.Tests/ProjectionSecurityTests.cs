using EventFlowMcp.NServiceBus;
using EventFlowMcp.PostgresProjection;
using NServiceBus;
using Xunit;

namespace EventFlowMcp.ProjectionSecurity.Tests;

public sealed class ProjectionSecurityTests
{
    [Fact]
    public void Header_correlation_resolver_uses_only_the_standard_correlation_header()
    {
        var resolver = new HeaderCorrelationResolver();
        var result = resolver.Resolve(
            new object(),
            new Dictionary<string, string>
            {
                [Headers.CorrelationId] = "order-10042",
                ["CustomerEmail"] = "sensitive@example.test"
            });

        var correlation = Assert.Single(result);
        Assert.Equal("NServiceBus.CorrelationId", correlation.Key);
        Assert.Equal("order-10042", correlation.Value);
    }

    [Fact]
    public void Shared_projection_sanitizer_redacts_secrets_and_limits_value_size()
    {
        var value = new string('x', 5_000);
        var sanitized = ProjectionDataSanitizer.Sanitize(new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer should-not-be-persisted",
            ["X-Api-Key"] = "should-not-be-persisted",
            ["UsefulHeader"] = value
        });

        Assert.Equal("[redacted]", sanitized["Authorization"]);
        Assert.Equal("[redacted]", sanitized["X-Api-Key"]);
        Assert.Equal(4_097, sanitized["UsefulHeader"].Length);
    }
}
