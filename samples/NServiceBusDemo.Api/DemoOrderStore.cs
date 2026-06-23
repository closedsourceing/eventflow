using System.Collections.Concurrent;

namespace NServiceBusDemo.Api;

public sealed class DemoOrderStore
{
    public const string DemoOrderId = "order-10042";

    private readonly ConcurrentDictionary<string, DemoOrder> orders = new(StringComparer.OrdinalIgnoreCase)
    {
        [DemoOrderId] = new(DemoOrderId, "Ada Lovelace", 149.95m, "Draft", DateTimeOffset.UtcNow)
    };

    public IReadOnlyList<DemoOrder> GetAll()
        => orders.Values.OrderBy(order => order.Id, StringComparer.OrdinalIgnoreCase).ToList();

    public bool TryGet(string orderId, out DemoOrder order) => orders.TryGetValue(orderId, out order!);

    public bool TryMarkSubmissionRequested(string orderId, out DemoOrder order)
        => TryUpdate(orderId, "SubmissionRequested", out order);

    public void MarkSubmitted(string orderId) => TryUpdate(orderId, "Submitted", out _);

    public void MarkApproved(string orderId) => TryUpdate(orderId, "Approved", out _);

    private bool TryUpdate(string orderId, string status, out DemoOrder order)
    {
        while (orders.TryGetValue(orderId, out var current))
        {
            var updated = current with { Status = status, LastUpdatedAt = DateTimeOffset.UtcNow };
            if (orders.TryUpdate(orderId, updated, current))
            {
                order = updated;
                return true;
            }
        }

        order = default!;
        return false;
    }
}

public sealed record DemoOrder(
    string Id,
    string Customer,
    decimal Total,
    string Status,
    DateTimeOffset LastUpdatedAt);
