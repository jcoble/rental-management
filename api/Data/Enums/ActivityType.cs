using System.Text.Json.Serialization;

namespace Lifecycle.Data.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActivityType
{
    TaskCreated = 0,
    TaskUpdated = 1,
    TaskMoved = 2,
    TaskDeleted = 3,
    PhaseCreated = 4,
    PhaseUpdated = 5,
    PhaseCompleted = 6,
    MilestoneCreated = 7,
    MilestoneCompleted = 8,
    TestCreated = 9,
    TestRun = 10,
    AttachmentAdded = 11,
    CommentAdded = 12,
    LabelCreated = 13,
    ProjectCreated = 14,
    BulkTasksCreated = 15,
    PhaseBreakdown = 16
}
