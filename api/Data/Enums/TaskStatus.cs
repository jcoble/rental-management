using System.Text.Json.Serialization;

namespace Lifecycle.Data.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskStatus
{
    Backlog = 0,
    Todo = 1,
    InProgress = 2,
    Review = 3,
    Blocked = 4,
    Done = 5,
    Cancelled = 6
}
