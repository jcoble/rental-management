using System.Text.Json.Serialization;

namespace Lifecycle.Data.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestAutonomyLevel
{
    Manual = 0,
    SemiAuto = 1,
    AutoCreate = 2,
    FullAuto = 3
}
