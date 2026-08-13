namespace RentalCommand.Core.Policies;

public static class PropertyUnitPolicy
{
    public static bool DoesNotBelong(int? selectedPropertyId, int? unitPropertyId) =>
        selectedPropertyId is > 0
        && unitPropertyId != null
        && selectedPropertyId != unitPropertyId;
}
