using System.Text.Json.Serialization;

namespace Lifecycle.Data.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectStatus
{
    Active = 0,
    OnHold = 1,
    Completed = 2,
    Archived = 3
}
