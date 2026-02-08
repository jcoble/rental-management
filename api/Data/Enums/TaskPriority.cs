using System.Text.Json.Serialization;

namespace Lifecycle.Data.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskPriority
{
    P1 = 1,
    P2 = 2,
    P3 = 3,
    P4 = 4
}
