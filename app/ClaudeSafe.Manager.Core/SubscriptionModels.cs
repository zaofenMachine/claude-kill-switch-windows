using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ClaudeSafe.Manager;

public sealed class SubscriptionSnapshot
{
    public string ActiveId { get; init; } = "";
    public List<SubscriptionSummary> Items { get; init; } = [];
    public bool Busy { get; init; }
    public string Message { get; init; } = "";
}

// The UI never receives a subscription token, its path/query, or cached credentials.
public sealed class SubscriptionSummary
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public bool Active { get; init; }
    public string MaskedUrl { get; init; } = "";
    public int NodeCount { get; init; }
    public DateTimeOffset? LastUpdatedUtc { get; init; }
    public string LastError { get; init; } = "";
}

internal sealed class SubscriptionCatalogState
{
    [JsonRequired] public int Version { get; set; } = 1;
    [JsonRequired] public string ActiveId { get; set; } = "";
    [JsonRequired] public List<StoredSubscription> Items { get; set; } = [];
}

internal sealed class StoredSubscription
{
    [JsonRequired] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public string Kind { get; set; } = "";
    [JsonRequired] public string Url { get; set; } = "";
    // A new revision makes concurrent results for an old URL or cache inapplicable.
    [JsonRequired] public string Revision { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset? LastUpdatedUtc { get; set; }
    public string LastErrorCode { get; set; } = "";
    [JsonRequired] public List<JsonObject> Nodes { get; set; } = [];
}
