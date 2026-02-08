using System.Text.Json.Serialization;

namespace Lifecycle.Data.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestLevel
{
    Smoke = 1,
    Comprehensive = 2,
    FullE2E = 3
}
