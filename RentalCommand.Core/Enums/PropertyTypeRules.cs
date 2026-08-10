namespace RentalCommand.Core.Enums;

public static class PropertyTypeRules
{
    public static bool RequiresResidentialUnitDetails(this PropertyType propertyType) =>
        propertyType is PropertyType.SingleFamily
            or PropertyType.MultiFamily
            or PropertyType.Condo
            or PropertyType.Townhome;
}
