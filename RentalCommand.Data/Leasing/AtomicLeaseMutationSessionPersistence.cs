using RentalCommand.Core.Atomic;
using RentalCommand.Data.Atomic;

namespace RentalCommand.Data.Leasing;

public static partial class AtomicLeaseMutationPersistence
{
    private static AtomicAuditScope RequireAuditScope(
        RentalCommandDbContext db,
        IAtomicCommandContext context)
    {
        if (context is not AtomicCommandContext owner || !owner.Owns(db) || !context.IsActive)
        {
            throw new AtomicArchitectureException(
                "Atomic helper requires the exact scoped DbContext and active command context.");
        }

        return owner.AuditScope;
    }
}
