using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Policies;

public static class ResidentialUnitPolicy
{
    public const string RequiredDetailsMessage =
        "Bedrooms and bathrooms are required for residential dwellings.";

    public static bool IsInvalidForCreate(
        PropertyType propertyType,
        decimal? bedrooms,
        decimal? bathrooms) =>
        propertyType.RequiresResidentialUnitDetails()
        && (!bedrooms.HasValue || !bathrooms.HasValue);

    public static bool IsInvalidForUpdate(
        PropertyType propertyType,
        bool bedroomsSpecified,
        decimal? bedrooms,
        bool bathroomsSpecified,
        decimal? bathrooms) =>
        propertyType.RequiresResidentialUnitDetails()
        && ((bedroomsSpecified && !bedrooms.HasValue)
            || (bathroomsSpecified && !bathrooms.HasValue));
}
