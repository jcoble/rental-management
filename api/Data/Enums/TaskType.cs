using System.Text.Json.Serialization;

namespace Lifecycle.Data.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskType
{
    Feature = 0,
    Bug = 1,
    Refactor = 2,
    Docs = 3,
    Test = 4,
    Infra = 5,
    Research = 6
}
