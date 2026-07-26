using Microsoft.EntityFrameworkCore;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

// Compatibility contexts retained for the remaining provider-neutral SQLite fixtures. The
// PostgreSQL-dependent accounting, owner-statement, reports, and Schedule E suites do not use them.
internal sealed class AccountingServiceTestDbContext(
    DbContextOptions<RentalCommandDbContext> options)
    : SqliteCompatibleRentalCommandDbContext(options);

internal sealed class ReportsServiceTestDbContext(
    DbContextOptions<RentalCommandDbContext> options)
    : SqliteCompatibleRentalCommandDbContext(options);
