using System.Text.Json.Serialization;

namespace Lifecycle.Data.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestType
{
    Unit = 0,
    Integration = 1,
    UI = 2,
    Manual = 3
}
