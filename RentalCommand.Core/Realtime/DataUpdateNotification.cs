using System.Text.Json;
using System.Text.Json.Serialization;

namespace RentalCommand.Core.Realtime;

/// <summary>
/// Cross-process realtime data-change notification carried over the PostgreSQL
/// <c>LISTEN/NOTIFY</c> channel <see cref="ChannelName"/>.
/// <para>
/// SignalR is in-memory per process and RentalCommand runs no backplane, so an entity change
/// written by the out-of-process Engine cannot reach the hub (which lives in the API process).
/// The Engine publishes one of these on each entity create/update/delete; an API-hosted listener
/// (<c>EntityChangeListener</c>) receives it and re-broadcasts on the SignalR hub so connected
/// browsers refresh. This mirrors EdiPlatform's API-hosted broadcaster, minus RabbitMQ.
/// </para>
/// <para>
/// Kept compact (short wire keys, null <see cref="Data"/> omitted) to stay under PostgreSQL's
/// ~8 KB NOTIFY payload limit. <see cref="SerializerOptions"/> is the single source of truth for
/// the wire format shared by the publisher and the listener so the two never drift.
/// </para>
/// </summary>
public sealed class DataUpdateNotification
{
    /// <summary>
    /// The NOTIFY/LISTEN channel name. A bare, lower-case identifier (no quoting needed) that is
    /// distinct enough not to collide with any other channel on the shared database.
    /// </summary>
    public const string ChannelName = "rc_entity_change";

    /// <summary>Op code for a create/update — re-broadcast as SignalR <c>EntityUpdated</c>.</summary>
    public const string OpUpdate = "u";

    /// <summary>Op code for a delete — re-broadcast as SignalR <c>EntityDeleted</c>.</summary>
    public const string OpDelete = "d";

    /// <summary><see cref="OpUpdate"/> or <see cref="OpDelete"/>.</summary>
    [JsonPropertyName("op")]
    public string Op { get; set; } = OpUpdate;

    /// <summary>
    /// Portfolio the change belongs to. The API listener uses it only as a database/RLS boundary;
    /// SignalR recipients are resolved from current session, capability, and resource scope.
    /// </summary>
    [JsonPropertyName("pf")]
    public int PortfolioId { get; set; }

    /// <summary>Entity type name (e.g. "Payment", "WorkOrder", "Notification", "ScanDraft").</summary>
    [JsonPropertyName("t")]
    public string EntityType { get; set; } = null!;

    /// <summary>Entity id.</summary>
    [JsonPropertyName("id")]
    public int EntityId { get; set; }

    /// <summary>
    /// Optional internal publisher hint (updates only; always <c>null</c> for deletes). The API
    /// broadcaster never forwards this object over SignalR; clients receive a minimal invalidation
    /// and refetch through their authorized REST query. It may be dropped when the PostgreSQL NOTIFY
    /// size limit would otherwise be exceeded.
    /// </summary>
    [JsonPropertyName("d")]
    public object? Data { get; set; }

    /// <summary>
    /// Shared wire format for both the Engine publisher and the API listener. Enums render as their
    /// string names (matching the REST + SignalR conventions app-wide) and null <see cref="Data"/> is
    /// omitted to conserve the NOTIFY byte budget.
    /// </summary>
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
