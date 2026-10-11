namespace RentalCommand.Core.Inspections;

public sealed record AtomicInspectionItemOrderResult(
    bool InspectionExists,
    int InspectionStatus,
    bool IsValid,
    bool HasChanges);
