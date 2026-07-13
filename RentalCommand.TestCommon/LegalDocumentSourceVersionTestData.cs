using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.TestCommon;

/// <summary>
/// Creates the minimum immutable built-in-renderer provenance required by canonical legal-document
/// test graphs. Production code must create source versions through the atomic persistence layer.
/// </summary>
public static class LegalDocumentSourceVersionTestData
{
    public static LegalDocumentSourceVersion BuiltIn(
        int portfolioId,
        int createdByUserId,
        DateTime? createdAtUtc = null,
        string? rendererKey = null)
    {
        var now = createdAtUtc ?? DateTime.UtcNow;
        var resolvedRendererKey = rendererKey ?? $"test-fixture-{Guid.NewGuid():N}";
        return new LegalDocumentSourceVersion
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            SourceKind = LegalDocumentSourceKind.BuiltInRenderer,
            BusinessKey = $"test:{resolvedRendererKey}",
            RendererKey = resolvedRendererKey,
            RendererVersion = 1,
            SnapshotPayload = "{}",
            CreatedAtUtc = now,
            CreatedByUserId = createdByUserId,
        };
    }
}
