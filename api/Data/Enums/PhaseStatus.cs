using System.Text.Json.Serialization;

namespace Lifecycle.Data.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PhaseStatus
{
    NotStarted = 0,
    Planning = 1,
    InProgress = 2,
    Review = 3,
    Blocked = 4,
    Completed = 5,
    Cancelled = 6
}
