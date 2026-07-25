using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using RentalCommand.Data.Leasing;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:btree_gist", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AtomicAuditLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommandType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CommandIdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MutationOrdinal = table.Column<long>(type: "bigint", nullable: false),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: true),
                    ActorLabel = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    EntityType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    EntityId = table.Column<int>(type: "integer", nullable: false),
                    Operation = table.Column<int>(type: "integer", nullable: false),
                    OldValues = table.Column<string>(type: "jsonb", nullable: true),
                    NewValues = table.Column<string>(type: "jsonb", nullable: true),
                    ChangeReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AtomicAuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AtomicCommandReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommandType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ResultContract = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AtomicCommandReceipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CapabilityDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    AuthorizationTargetKind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CapabilityDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EngineWorkerHeartbeats",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkerName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    LastHeartbeatUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LastErrorUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ProcessedCount = table.Column<long>(type: "bigint", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    CycleCount = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EngineWorkerHeartbeats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Portfolios",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ManagementCompanyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false, defaultValue: "USD"),
                    Settings = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    PublicApplicationToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsSandbox = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    SandboxSeededAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Portfolios", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProviderInboxEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: true),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ProviderEventId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EventType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    ProviderObjectId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    EventKind = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClaimOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaimExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProcessedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeadLetteredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureKind = table.Column<int>(type: "integer", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderInboxEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoleProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    DefaultExperience = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    DefaultScopeKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SimulationClocks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SimAnchorUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RealAnchorUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtRealUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SimulationClocks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SimWorkerCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestedSimUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ClaimOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaimExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResultJson = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    CreatedRealUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedRealUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SimWorkerCommands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemNoticeTemplateVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    SystemKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Classification = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    JurisdictionCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Provenance = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemNoticeTemplateVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LoginContextSelectionChallenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoginContextSelectionChallenges", x => x.Id);
                    table.CheckConstraint("CK_LoginContextSelectionChallenges_ConsumedFacts", "\"ConsumedAtUtc\" IS NULL OR \"ConsumedAtUtc\" >= \"CreatedAtUtc\"");
                    table.CheckConstraint("CK_LoginContextSelectionChallenges_Expiry", "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
                    table.ForeignKey(
                        name: "FK_LoginContextSelectionChallenges_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccountingConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ExternalAccountId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CompanyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AccessTokenCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    RefreshTokenCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    TokenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PullEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    PushEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastPulledAtJson = table.Column<string>(type: "jsonb", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConnectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DisconnectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextPullAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PullClaimOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PullClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    PullClaimExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PullAttemptCount = table.Column<int>(type: "integer", nullable: false),
                    PullLastAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TokenRotationState = table.Column<int>(type: "integer", nullable: false),
                    TokenGeneration = table.Column<long>(type: "bigint", nullable: false),
                    TokenRotationClaimOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TokenRotationClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    TokenRotationClaimExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TokenRotationAttemptCount = table.Column<int>(type: "integer", nullable: false),
                    TokenRotationLastAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountingConnections_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutomationSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    EnableRentCharges = table.Column<bool>(type: "boolean", nullable: false),
                    RentChargeLeadDays = table.Column<int>(type: "integer", nullable: false, defaultValue: 5),
                    EnableLateFees = table.Column<bool>(type: "boolean", nullable: false),
                    LateFeeGraceDays = table.Column<int>(type: "integer", nullable: false, defaultValue: 5),
                    EnableLeaseExpiryReminders = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    LeaseExpiryReminderDays = table.Column<int>(type: "integer", nullable: false, defaultValue: 60),
                    EnableRecurringMaintenance = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    EnableMorningBriefing = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    MorningBriefingSendHourLocal = table.Column<int>(type: "integer", nullable: false, defaultValue: 8),
                    MorningBriefingIncludeEmpty = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationSettings_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BankConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    InstitutionName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AccountName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AccountMask = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    AccountType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    AccountSubtype = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ExternalItemIdCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ExternalAccountIdCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ExternalItemIdHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ExternalAccountIdHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ExternalAccessTokenCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    SyncCursorCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankConnections_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Token = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Platform = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceTokens_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InspectionTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    InspectionType = table.Column<int>(type: "integer", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InspectionTemplates_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MessagingProviderSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    SmsProvider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    SmsCredentialACipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    SmsCredentialBCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    SmsCredentialCCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    SmsFromNumberCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessagingProviderSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessagingProviderSettings_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: true),
                    Type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ActionUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RelatedEntityType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    RelatedEntityId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.UniqueConstraint("AK_Notifications_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.ForeignKey(
                        name: "FK_Notifications_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OAuthStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    StateToken = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RedirectUri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    CodeVerifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OAuthStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OAuthStates_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: true),
                    MessageType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClaimOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaimExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcceptedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeliveredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeadLetteredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Provider = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ProviderMessageId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    FailureKind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    LastError = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutboxMessages_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OwnerEntities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    OwnerEntityType = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TaxId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AddressLine1 = table.Column<string>(type: "text", nullable: true),
                    AddressLine2 = table.Column<string>(type: "text", nullable: true),
                    City = table.Column<string>(type: "text", nullable: true),
                    State = table.Column<string>(type: "text", nullable: true),
                    PostalCode = table.Column<string>(type: "text", nullable: true),
                    Address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerEntities", x => x.Id);
                    table.UniqueConstraint("AK_OwnerEntities_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.ForeignKey(
                        name: "FK_OwnerEntities_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Owners",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    MailingAddress = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Owners", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Owners_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QueuedJobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    JobType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueuedJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QueuedJobs_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RenderedNotices",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    NoticeDraftId = table.Column<int>(type: "integer", nullable: false),
                    WorkspaceNoticeTemplateVersionId = table.Column<int>(type: "integer", nullable: false),
                    LeaseManagementId = table.Column<int>(type: "integer", nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    ContentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TemplateProvenance = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    JurisdictionCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    RenderedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovedByUserId = table.Column<int>(type: "integer", nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RenderedNotices", x => x.Id);
                    table.UniqueConstraint("AK_RenderedNotices_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.ForeignKey(
                        name: "FK_RenderedNotices_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ScanBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TargetEntityType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    FileCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScanBatches_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StoredFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    FilePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    EntityId = table.Column<long>(type: "bigint", nullable: true),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredFiles", x => x.Id);
                    table.UniqueConstraint("AK_StoredFiles_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.ForeignKey(
                        name: "FK_StoredFiles_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    FirstName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    EmergencyContact = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DateOfBirth = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                    table.UniqueConstraint("AK_Tenants_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.ForeignKey(
                        name: "FK_Tenants_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserAlertPreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    EnableInApp = table.Column<bool>(type: "boolean", nullable: false),
                    EnableMobilePush = table.Column<bool>(type: "boolean", nullable: false),
                    EnableEmail = table.Column<bool>(type: "boolean", nullable: false),
                    EnableSms = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAlertPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserAlertPreferences_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserAlertPreferences_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Vendors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ServiceType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    NormalizedPhone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Website = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TaxId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    AddressLine1 = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    City = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    State = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    PostalCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Is1099Eligible = table.Column<bool>(type: "boolean", nullable: false),
                    W9OnFile = table.Column<bool>(type: "boolean", nullable: false),
                    Preferred = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AverageRating = table.Column<decimal>(type: "numeric(3,2)", precision: 3, scale: 2, nullable: true),
                    RatingCount = table.Column<int>(type: "integer", nullable: false),
                    JobsCompleted = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vendors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Vendors_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceAccessContexts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    AccessRevision = table.Column<long>(type: "bigint", nullable: false),
                    LastAuthorizedExperience = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SuspendedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceAccessContexts", x => x.Id);
                    table.UniqueConstraint("AK_WorkspaceAccessContexts_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.UniqueConstraint("AK_WorkspaceAccessContexts_Id_UserId", x => new { x.Id, x.UserId });
                    table.UniqueConstraint("AK_WorkspaceAccessContexts_Id_UserId_PortfolioId", x => new { x.Id, x.UserId, x.PortfolioId });
                    table.CheckConstraint("CK_WorkspaceAccessContexts_AccessRevision_Positive", "\"AccessRevision\" > 0");
                    table.CheckConstraint("CK_WorkspaceAccessContexts_StatusFacts", "(\"Status\" = 'Active' AND \"SuspendedAtUtc\" IS NULL AND \"RevokedAtUtc\" IS NULL) OR (\"Status\" = 'Suspended' AND \"SuspendedAtUtc\" IS NOT NULL AND \"RevokedAtUtc\" IS NULL) OR (\"Status\" = 'Revoked' AND \"RevokedAtUtc\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_WorkspaceAccessContexts_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkspaceAccessContexts_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RoleProfileCapabilities",
                columns: table => new
                {
                    RoleProfileId = table.Column<int>(type: "integer", nullable: false),
                    CapabilityDefinitionId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleProfileCapabilities", x => new { x.RoleProfileId, x.CapabilityDefinitionId });
                    table.ForeignKey(
                        name: "FK_RoleProfileCapabilities_CapabilityDefinitions_CapabilityDef~",
                        column: x => x.CapabilityDefinitionId,
                        principalTable: "CapabilityDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RoleProfileCapabilities_RoleProfiles_RoleProfileId",
                        column: x => x.RoleProfileId,
                        principalTable: "RoleProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceNoticeTemplateVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    SystemKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    BasedOnSystemTemplateVersionId = table.Column<int>(type: "integer", nullable: false),
                    IsCustomized = table.Column<bool>(type: "boolean", nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    JurisdictionCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    JurisdictionReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    JurisdictionReviewedByUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceNoticeTemplateVersions", x => x.Id);
                    table.UniqueConstraint("AK_WorkspaceNoticeTemplateVersions_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.ForeignKey(
                        name: "FK_WorkspaceNoticeTemplateVersions_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkspaceNoticeTemplateVersions_SystemNoticeTemplateVersion~",
                        column: x => x.BasedOnSystemTemplateVersionId,
                        principalTable: "SystemNoticeTemplateVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AccountingEntityMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    AccountingConnectionId = table.Column<int>(type: "integer", nullable: false),
                    LocalEntityType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    LocalEntityId = table.Column<int>(type: "integer", nullable: true),
                    LocalEnumValue = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ExternalType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExternalDisplayName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmedByUserId = table.Column<int>(type: "integer", nullable: true),
                    Confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingEntityMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountingEntityMappings_AccountingConnections_AccountingCo~",
                        column: x => x.AccountingConnectionId,
                        principalTable: "AccountingConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccountingEntityMappings_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccountingSyncMaps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    AccountingConnectionId = table.Column<int>(type: "integer", nullable: false),
                    Direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ExternalType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LocalEntityType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    LocalEntityId = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingSyncMaps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountingSyncMaps_AccountingConnections_AccountingConnecti~",
                        column: x => x.AccountingConnectionId,
                        principalTable: "AccountingConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccountingSyncMaps_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlaidTokenExchangeAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    ClientOperationId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PublicTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InstitutionName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AccountName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AccountMask = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    AccountType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    AccountSubtype = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ExternalAccountIdCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ExternalAccountIdHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PreparedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RemoteAdmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RemoteReceiptRecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProviderRequestIdentity = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ExternalItemIdCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ExternalItemIdHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ExternalAccessTokenCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    BankConnectionId = table.Column<int>(type: "integer", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaidTokenExchangeAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaidTokenExchangeAttempts_BankConnections_BankConnectionId",
                        column: x => x.BankConnectionId,
                        principalTable: "BankConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PlaidTokenExchangeAttempts_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InspectionTemplateItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TemplateId = table.Column<int>(type: "integer", nullable: false),
                    Area = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Label = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionTemplateItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InspectionTemplateItems_InspectionTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "InspectionTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationReadStates",
                columns: table => new
                {
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    NotificationId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationReadStates", x => new { x.PortfolioId, x.NotificationId, x.UserId });
                    table.ForeignKey(
                        name: "FK_NotificationReadStates_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NotificationReadStates_Notifications_NotificationId_Portfol~",
                        columns: x => new { x.NotificationId, x.PortfolioId },
                        principalTable: "Notifications",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NotificationReadStates_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Properties",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    OwnerId = table.Column<int>(type: "integer", nullable: true),
                    OwnerEntityId = table.Column<int>(type: "integer", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PropertyType = table.Column<int>(type: "integer", nullable: false),
                    RentalStructure = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AddressLine1 = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    AddressLine2 = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    City = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PostalCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    YearBuilt = table.Column<int>(type: "integer", nullable: true),
                    ManagementFeePercent = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    PurchasePrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    LandValue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    InServiceDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ManualAnnualDepreciation = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    AccumulatedDepreciation = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Properties", x => x.Id);
                    table.UniqueConstraint("AK_Properties_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.ForeignKey(
                        name: "FK_Properties_OwnerEntities_OwnerEntityId",
                        column: x => x.OwnerEntityId,
                        principalTable: "OwnerEntities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Properties_Owners_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Owners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Properties_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LegalDocumentArtifacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    StoredFileId = table.Column<int>(type: "integer", nullable: false),
                    ArtifactKind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ByteLength = table.Column<long>(type: "bigint", nullable: false),
                    ContentSha256 = table.Column<string>(type: "char(64)", nullable: false),
                    LegalIssuanceFingerprint = table.Column<string>(type: "char(64)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegalDocumentArtifacts", x => x.Id);
                    table.UniqueConstraint("AK_LegalDocumentArtifacts_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.UniqueConstraint("AK_LegalDocumentArtifacts_Id_StoredFileId_PortfolioId", x => new { x.Id, x.StoredFileId, x.PortfolioId });
                    table.CheckConstraint("CK_LegalDocumentArtifact_ByteLength", "\"ByteLength\" > 0");
                    table.CheckConstraint("CK_LegalDocumentArtifact_ContentSha256", "\"ContentSha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_LegalDocumentArtifact_ContentType", "\"ContentType\" IN ('application/pdf', 'image/jpeg', 'image/png', 'image/webp', 'image/heic', 'image/gif')");
                    table.CheckConstraint("CK_LegalDocumentArtifact_IssuanceBinding", "(\"ArtifactKind\" IN ('IssuedAgreement', 'IssuedAddendum') AND \"LegalIssuanceFingerprint\" ~ '^[0-9a-f]{64}$') OR (\"ArtifactKind\" NOT IN ('IssuedAgreement', 'IssuedAddendum') AND \"LegalIssuanceFingerprint\" IS NULL)");
                    table.CheckConstraint("CK_LegalDocumentArtifact_Kind", "\"ArtifactKind\" IN ('IssuedAgreement', 'ExecutedAgreement', 'IssuedAddendum', 'ExecutedAddendum', 'CompletionCertificate')");
                    table.ForeignKey(
                        name: "FK_LegalDocumentArtifacts_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegalDocumentArtifacts_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegalDocumentArtifacts_StoredFiles_StoredFileId_PortfolioId",
                        columns: x => new { x.StoredFileId, x.PortfolioId },
                        principalTable: "StoredFiles",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PendingFileUploads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    ActorScopeId = table.Column<int>(type: "integer", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    OperationKeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    StoredFileId = table.Column<int>(type: "integer", nullable: true),
                    CleanupClaimOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CleanupClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    CleanupClaimExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingFileUploads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingFileUploads_StoredFiles_StoredFileId",
                        column: x => x.StoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AuthSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    ActiveAccessContextId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthSessions", x => x.Id);
                    table.CheckConstraint("CK_AuthSessions_ExpiresAfterCreation", "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
                    table.CheckConstraint("CK_AuthSessions_RevokedAt_Status", "\"Status\" <> 'Revoked' OR \"RevokedAtUtc\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_AuthSessions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AuthSessions_WorkspaceAccessContexts_ActiveAccessContextId_~",
                        columns: x => new { x.ActiveAccessContextId, x.UserId },
                        principalTable: "WorkspaceAccessContexts",
                        principalColumns: new[] { "Id", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OwnerUserAccesses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    AccessContextId = table.Column<int>(type: "integer", nullable: false),
                    ApplicationUserId = table.Column<int>(type: "integer", nullable: false),
                    OwnerEntityId = table.Column<int>(type: "integer", nullable: false),
                    EffectiveFromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EffectiveToUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GrantedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GrantedByUserId = table.Column<int>(type: "integer", nullable: false),
                    RevokedByUserId = table.Column<int>(type: "integer", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerUserAccesses", x => x.Id);
                    table.CheckConstraint("CK_OwnerUserAccess_EffectivePeriod", "\"EffectiveToUtc\" IS NULL OR \"EffectiveToUtc\" > \"EffectiveFromUtc\"");
                    table.CheckConstraint("CK_OwnerUserAccess_RevocationAfterGrant", "\"RevokedAtUtc\" IS NULL OR \"RevokedAtUtc\" >= \"GrantedAtUtc\"");
                    table.CheckConstraint("CK_OwnerUserAccess_RevocationPair", "(\"RevokedAtUtc\" IS NULL) = (\"RevokedByUserId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_OwnerUserAccesses_AspNetUsers_ApplicationUserId",
                        column: x => x.ApplicationUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerUserAccesses_AspNetUsers_GrantedByUserId",
                        column: x => x.GrantedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerUserAccesses_AspNetUsers_RevokedByUserId",
                        column: x => x.RevokedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerUserAccesses_OwnerEntities_OwnerEntityId_PortfolioId",
                        columns: x => new { x.OwnerEntityId, x.PortfolioId },
                        principalTable: "OwnerEntities",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerUserAccesses_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerUserAccesses_WorkspaceAccessContexts_AccessContextId_A~",
                        columns: x => new { x.AccessContextId, x.ApplicationUserId, x.PortfolioId },
                        principalTable: "WorkspaceAccessContexts",
                        principalColumns: new[] { "Id", "UserId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceMemberships",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccessContextId = table.Column<int>(type: "integer", nullable: false),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    DefaultExperience = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    EffectiveFromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EffectiveToUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SuspendedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceMemberships", x => x.Id);
                    table.UniqueConstraint("AK_WorkspaceMemberships_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.CheckConstraint("CK_WorkspaceMemberships_EffectivePeriod", "\"EffectiveToUtc\" IS NULL OR \"EffectiveToUtc\" > \"EffectiveFromUtc\"");
                    table.CheckConstraint("CK_WorkspaceMemberships_StatusFacts", "(\"Status\" = 'Active' AND \"SuspendedAtUtc\" IS NULL AND \"RevokedAtUtc\" IS NULL) OR (\"Status\" = 'Suspended' AND \"SuspendedAtUtc\" IS NOT NULL AND \"RevokedAtUtc\" IS NULL) OR (\"Status\" = 'Revoked' AND \"RevokedAtUtc\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_WorkspaceMemberships_WorkspaceAccessContexts_AccessContextI~",
                        columns: x => new { x.AccessContextId, x.PortfolioId },
                        principalTable: "WorkspaceAccessContexts",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TenantNoticePolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    AutomationKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Classification = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LeadDays = table.Column<int>(type: "integer", nullable: false),
                    SendHourLocal = table.Column<int>(type: "integer", nullable: false),
                    SendTenantPortal = table.Column<bool>(type: "boolean", nullable: false),
                    SendMobilePush = table.Column<bool>(type: "boolean", nullable: false),
                    SendEmail = table.Column<bool>(type: "boolean", nullable: false),
                    SendSms = table.Column<bool>(type: "boolean", nullable: false),
                    IncludePrimaryTenant = table.Column<bool>(type: "boolean", nullable: false),
                    IncludeCoTenant = table.Column<bool>(type: "boolean", nullable: false),
                    IncludeEligibleGuarantor = table.Column<bool>(type: "boolean", nullable: false),
                    IncludeOccupant = table.Column<bool>(type: "boolean", nullable: false),
                    FailureBehavior = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    WorkspaceNoticeTemplateVersionId = table.Column<int>(type: "integer", nullable: false),
                    ReviewedJurisdictionCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    JurisdictionReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    JurisdictionReviewedByUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantNoticePolicies", x => x.Id);
                    table.UniqueConstraint("AK_TenantNoticePolicies_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.ForeignKey(
                        name: "FK_TenantNoticePolicies_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TenantNoticePolicies_WorkspaceNoticeTemplateVersions_Worksp~",
                        columns: x => new { x.WorkspaceNoticeTemplateVersionId, x.PortfolioId },
                        principalTable: "WorkspaceNoticeTemplateVersions",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AccountingMappingPromotionJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    AccountingConnectionId = table.Column<int>(type: "integer", nullable: false),
                    AccountingEntityMappingId = table.Column<int>(type: "integer", nullable: false),
                    MappingRevision = table.Column<long>(type: "bigint", nullable: false),
                    PromotedCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingMappingPromotionJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountingMappingPromotionJobs_AccountingConnections_Accoun~",
                        column: x => x.AccountingConnectionId,
                        principalTable: "AccountingConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccountingMappingPromotionJobs_AccountingEntityMappings_Acc~",
                        column: x => x.AccountingEntityMappingId,
                        principalTable: "AccountingEntityMappings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccountingMappingPromotionJobs_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DocumentTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    RenderMode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    OriginalStoredFileId = table.Column<int>(type: "integer", nullable: true),
                    DraftHtml = table.Column<string>(type: "text", nullable: true),
                    CompiledStoredFileId = table.Column<int>(type: "integer", nullable: true),
                    DefaultForPortfolio = table.Column<bool>(type: "boolean", nullable: false),
                    IsSandboxSeeded = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ArchivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentTemplates", x => x.Id);
                    table.UniqueConstraint("AK_DocumentTemplates_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.CheckConstraint("CK_DocumentTemplate_Version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_DocumentTemplates_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DocumentTemplates_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DocumentTemplates_StoredFiles_CompiledStoredFileId",
                        column: x => x.CompiledStoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DocumentTemplates_StoredFiles_OriginalStoredFileId",
                        column: x => x.OriginalStoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Loans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: false),
                    Lender = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OriginalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CurrentBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AnnualInterestRatePct = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: false),
                    TermMonths = table.Column<int>(type: "integer", nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DayOfMonthDue = table.Column<int>(type: "integer", nullable: false),
                    MonthlyPrincipalInterest = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    MonthlyEscrow = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EscrowCoversTaxes = table.Column<bool>(type: "boolean", nullable: false),
                    EscrowCoversInsurance = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WorkerClaimOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WorkerClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkerClaimExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WorkerClaimAttemptCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Loans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Loans_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Loans_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OwnerDistributions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    OwnerEntityId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: true),
                    Date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Method = table.Column<int>(type: "integer", nullable: false),
                    Memo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerDistributions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OwnerDistributions_OwnerEntities_OwnerEntityId",
                        column: x => x.OwnerEntityId,
                        principalTable: "OwnerEntities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerDistributions_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OwnerDistributions_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PropertyDispositions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: false),
                    ClosedOnDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SalePrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SellingCosts = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    BuyerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Memo = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyDispositions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropertyDispositions_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PropertyDispositions_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TeamRoutingRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    Topic = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: true),
                    UseWorkspaceAdministratorFallback = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamRoutingRules", x => x.Id);
                    table.UniqueConstraint("AK_TeamRoutingRules_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.CheckConstraint("CK_TeamRoutingRules_WorkspaceOnlyTopics", "\"PropertyId\" IS NULL OR \"Topic\" NOT IN ('AccountAndSecurity', 'MorningBriefing')");
                    table.ForeignKey(
                        name: "FK_TeamRoutingRules_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TeamRoutingRules_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Units",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: false),
                    UnitNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FloorPlan = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Bedrooms = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: false),
                    Bathrooms = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: false),
                    SquareFeet = table.Column<int>(type: "integer", nullable: true),
                    MarketRent = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Units", x => x.Id);
                    table.UniqueConstraint("AK_Units_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.UniqueConstraint("AK_Units_Id_PropertyId_PortfolioId", x => new { x.Id, x.PropertyId, x.PortfolioId });
                    table.ForeignKey(
                        name: "FK_Units_Properties_PropertyId_PortfolioId",
                        columns: x => new { x.PropertyId, x.PortfolioId },
                        principalTable: "Properties",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AuthSessionRefreshTokenFamilies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AbsoluteExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReuseDetectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthSessionRefreshTokenFamilies", x => x.Id);
                    table.CheckConstraint("CK_AuthSessionRefreshTokenFamilies_Expiry", "\"AbsoluteExpiresAtUtc\" > \"CreatedAtUtc\"");
                    table.CheckConstraint("CK_AuthSessionRefreshTokenFamilies_ReuseRevokes", "\"ReuseDetectedAtUtc\" IS NULL OR (\"RevokedAtUtc\" IS NOT NULL AND \"ReuseDetectedAtUtc\" >= \"CreatedAtUtc\")");
                    table.CheckConstraint("CK_AuthSessionRefreshTokenFamilies_RevocationFacts", "(\"RevokedAtUtc\" IS NULL AND \"RevocationReason\" IS NULL) OR (\"RevokedAtUtc\" IS NOT NULL AND \"RevocationReason\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_AuthSessionRefreshTokenFamilies_AuthSessions_AuthSessionId",
                        column: x => x.AuthSessionId,
                        principalTable: "AuthSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MembershipRoleAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkspaceMembershipId = table.Column<int>(type: "integer", nullable: false),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    RoleProfileId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ScopeKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EffectiveFromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EffectiveToUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SuspendedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MembershipRoleAssignments", x => x.Id);
                    table.UniqueConstraint("AK_MembershipRoleAssignments_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.UniqueConstraint("AK_MembershipRoleAssignments_Id_WorkspaceMembershipId_Portfoli~", x => new { x.Id, x.WorkspaceMembershipId, x.PortfolioId });
                    table.CheckConstraint("CK_MembershipRoleAssignments_EffectivePeriod", "\"EffectiveToUtc\" IS NULL OR \"EffectiveToUtc\" > \"EffectiveFromUtc\"");
                    table.CheckConstraint("CK_MembershipRoleAssignments_ScopeKind", "\"ScopeKind\" IN ('AllProperties', 'SelectedProperties', 'AssignedWorkOrders')");
                    table.CheckConstraint("CK_MembershipRoleAssignments_StatusFacts", "(\"Status\" = 'Active' AND \"SuspendedAtUtc\" IS NULL AND \"RevokedAtUtc\" IS NULL) OR (\"Status\" = 'Suspended' AND \"SuspendedAtUtc\" IS NOT NULL AND \"RevokedAtUtc\" IS NULL) OR (\"Status\" = 'Revoked' AND \"RevokedAtUtc\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_MembershipRoleAssignments_RoleProfiles_RoleProfileId",
                        column: x => x.RoleProfileId,
                        principalTable: "RoleProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MembershipRoleAssignments_WorkspaceMemberships_WorkspaceMem~",
                        columns: x => new { x.WorkspaceMembershipId, x.PortfolioId },
                        principalTable: "WorkspaceMemberships",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceInvitations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    WorkspaceMembershipId = table.Column<int>(type: "integer", nullable: false),
                    InvitedUserId = table.Column<int>(type: "integer", nullable: false),
                    InvitedByUserId = table.Column<int>(type: "integer", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AcceptedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceInvitations", x => x.Id);
                    table.CheckConstraint("CK_WorkspaceInvitations_ExpiryAfterCreate", "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
                    table.CheckConstraint("CK_WorkspaceInvitations_TerminalState", "NOT (\"AcceptedAtUtc\" IS NOT NULL AND \"RevokedAtUtc\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_WorkspaceInvitations_AspNetUsers_InvitedByUserId",
                        column: x => x.InvitedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkspaceInvitations_AspNetUsers_InvitedUserId",
                        column: x => x.InvitedUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkspaceInvitations_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkspaceInvitations_WorkspaceMemberships_WorkspaceMembersh~",
                        columns: x => new { x.WorkspaceMembershipId, x.PortfolioId },
                        principalTable: "WorkspaceMemberships",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TenantNoticeWorkItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    TenantNoticePolicyId = table.Column<int>(type: "integer", nullable: false),
                    LeaseManagementId = table.Column<int>(type: "integer", nullable: false),
                    RecipientLeaseManagementPartyId = table.Column<int>(type: "integer", nullable: false),
                    TenantLedgerEntryId = table.Column<long>(type: "bigint", nullable: true),
                    DueAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BusinessKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    ClaimOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaimExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantNoticeWorkItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantNoticeWorkItems_TenantNoticePolicies_TenantNoticePoli~",
                        columns: x => new { x.TenantNoticePolicyId, x.PortfolioId },
                        principalTable: "TenantNoticePolicies",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DocumentTemplateFields",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    DocumentTemplateId = table.Column<int>(type: "integer", nullable: false),
                    FieldKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SignerRole = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PageNumber = table.Column<int>(type: "integer", nullable: false),
                    XPct = table.Column<double>(type: "double precision", nullable: false),
                    YPct = table.Column<double>(type: "double precision", nullable: false),
                    WidthPct = table.Column<double>(type: "double precision", nullable: false),
                    HeightPct = table.Column<double>(type: "double precision", nullable: false),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    Locked = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    DefaultText = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentTemplateFields", x => x.Id);
                    table.CheckConstraint("CK_DocumentTemplateField_HeightPct", "\"HeightPct\" > 0 AND \"HeightPct\" <= 1");
                    table.CheckConstraint("CK_DocumentTemplateField_Page", "\"PageNumber\" >= 1");
                    table.CheckConstraint("CK_DocumentTemplateField_WidthPct", "\"WidthPct\" > 0 AND \"WidthPct\" <= 1");
                    table.CheckConstraint("CK_DocumentTemplateField_XExtent", "\"XPct\" + \"WidthPct\" <= 1");
                    table.CheckConstraint("CK_DocumentTemplateField_XPct", "\"XPct\" >= 0 AND \"XPct\" <= 1");
                    table.CheckConstraint("CK_DocumentTemplateField_YExtent", "\"YPct\" + \"HeightPct\" <= 1");
                    table.CheckConstraint("CK_DocumentTemplateField_YPct", "\"YPct\" >= 0 AND \"YPct\" <= 1");
                    table.ForeignKey(
                        name: "FK_DocumentTemplateFields_DocumentTemplates_Scope",
                        columns: x => new { x.DocumentTemplateId, x.PortfolioId },
                        principalTable: "DocumentTemplates",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LegalDocumentSourceVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    SourceKind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    BusinessKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    DocumentTemplateId = table.Column<int>(type: "integer", nullable: true),
                    DocumentTemplateVersion = table.Column<int>(type: "integer", nullable: true),
                    RendererKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RendererVersion = table.Column<int>(type: "integer", nullable: true),
                    SnapshotPayload = table.Column<string>(type: "jsonb", nullable: false),
                    SourceStoredFileId = table.Column<int>(type: "integer", nullable: true),
                    SourceLegalDocumentArtifactId = table.Column<int>(type: "integer", nullable: true),
                    SourceContentSha256 = table.Column<string>(type: "char(64)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegalDocumentSourceVersions", x => x.Id);
                    table.UniqueConstraint("AK_LegalDocumentSourceVersions_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.CheckConstraint("CK_LegalDocumentSourceVersion_Kind", "\"SourceKind\" IN ('AuthoredTemplateSnapshot', 'BuiltInRenderer', 'ImportedExternalDocument')");
                    table.CheckConstraint("CK_LegalDocumentSourceVersion_Snapshot", "jsonb_typeof(\"SnapshotPayload\") = 'object'");
                    table.CheckConstraint("CK_LegalDocumentSourceVersion_SourceShape", "(\"SourceKind\" = 'AuthoredTemplateSnapshot' AND \"DocumentTemplateId\" IS NOT NULL AND \"DocumentTemplateVersion\" >= 1 AND \"RendererKey\" IS NOT NULL AND \"RendererVersion\" >= 1 AND \"SourceStoredFileId\" IS NULL AND \"SourceLegalDocumentArtifactId\" IS NULL AND \"SourceContentSha256\" IS NULL) OR (\"SourceKind\" = 'BuiltInRenderer' AND \"DocumentTemplateId\" IS NULL AND \"DocumentTemplateVersion\" IS NULL AND \"RendererKey\" IS NOT NULL AND \"RendererVersion\" >= 1 AND \"SourceStoredFileId\" IS NULL AND \"SourceLegalDocumentArtifactId\" IS NULL AND \"SourceContentSha256\" IS NULL) OR (\"SourceKind\" = 'ImportedExternalDocument' AND \"DocumentTemplateId\" IS NULL AND \"DocumentTemplateVersion\" IS NULL AND \"RendererKey\" IS NULL AND \"RendererVersion\" IS NULL AND \"SourceStoredFileId\" IS NOT NULL AND \"SourceLegalDocumentArtifactId\" IS NOT NULL AND \"SourceContentSha256\" ~ '^[0-9a-f]{64}$')");
                    table.ForeignKey(
                        name: "FK_LegalDocumentSourceVersions_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegalDocumentSourceVersions_DocumentTemplates_DocumentTempl~",
                        columns: x => new { x.DocumentTemplateId, x.PortfolioId },
                        principalTable: "DocumentTemplates",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegalDocumentSourceVersions_LegalDocumentArtifacts_SourceLe~",
                        columns: x => new { x.SourceLegalDocumentArtifactId, x.SourceStoredFileId, x.PortfolioId },
                        principalTable: "LegalDocumentArtifacts",
                        principalColumns: new[] { "Id", "StoredFileId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegalDocumentSourceVersions_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegalDocumentSourceVersions_StoredFiles_SourceStoredFileId_~",
                        columns: x => new { x.SourceStoredFileId, x.PortfolioId },
                        principalTable: "StoredFiles",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LoanPayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    LoanId = table.Column<int>(type: "integer", nullable: false),
                    PeriodKey = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    DueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PaidDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    InterestAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PrincipalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EscrowAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    BalanceAfter = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PaymentDoesNotCoverInterest = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoanPayments_Loans_LoanId",
                        column: x => x.LoanId,
                        principalTable: "Loans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LoanPayments_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TeamRoutingRuleRecipients",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TeamRoutingRuleId = table.Column<int>(type: "integer", nullable: false),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamRoutingRuleRecipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeamRoutingRuleRecipients_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TeamRoutingRuleRecipients_TeamRoutingRules_TeamRoutingRuleI~",
                        columns: x => new { x.TeamRoutingRuleId, x.PortfolioId },
                        principalTable: "TeamRoutingRules",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LeaseManagements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: false),
                    UnitId = table.Column<int>(type: "integer", nullable: false),
                    TransferredFromLeaseManagementId = table.Column<int>(type: "integer", nullable: true),
                    TransferPublicId = table.Column<Guid>(type: "uuid", nullable: true),
                    TransferredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TransferReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RelationshipNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PlannedPossessionAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PossessionGivenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PossessionAgreementExceptionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PossessionAgreementExceptionAuthorizedByUserId = table.Column<int>(type: "integer", nullable: true),
                    NoticeGivenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PlannedMoveOutAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PossessionReturnedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AccountClosedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CanceledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancellationReasonCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    CancellationNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    EndingDisposition = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, defaultValue: "Undecided"),
                    EndingDispositionDecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndingDispositionDecidedByUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    RowVersion = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaseManagements", x => x.Id);
                    table.UniqueConstraint("AK_LeaseManagements_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.UniqueConstraint("AK_LeaseManagements_Id_UnitId_PortfolioId", x => new { x.Id, x.UnitId, x.PortfolioId });
                    table.UniqueConstraint("AK_LeaseManagements_Id_UnitId_PropertyId_PortfolioId", x => new { x.Id, x.UnitId, x.PropertyId, x.PortfolioId });
                    table.CheckConstraint("CK_LeaseManagement_AccountCloseAfterTerminalFact", "\"AccountClosedAtUtc\" IS NULL OR (\"PossessionReturnedAtUtc\" IS NOT NULL AND \"AccountClosedAtUtc\" >= \"PossessionReturnedAtUtc\") OR (\"CanceledAtUtc\" IS NOT NULL AND \"AccountClosedAtUtc\" >= \"CanceledAtUtc\")");
                    table.CheckConstraint("CK_LeaseManagement_AccountCloseRequiresTerminalFact", "\"AccountClosedAtUtc\" IS NULL OR \"PossessionReturnedAtUtc\" IS NOT NULL OR \"CanceledAtUtc\" IS NOT NULL");
                    table.CheckConstraint("CK_LeaseManagement_CancellationPair", "(\"CanceledAtUtc\" IS NULL) = (\"CancellationReasonCode\" IS NULL)");
                    table.CheckConstraint("CK_LeaseManagement_EndingDisposition", "(\"EndingDisposition\" = 'Undecided' AND \"EndingDispositionDecidedAtUtc\" IS NULL AND \"EndingDispositionDecidedByUserId\" IS NULL) OR (\"EndingDisposition\" IN ('OfferRenewal', 'OfferMonthToMonth', 'NonRenewalMoveOut') AND \"EndingDispositionDecidedAtUtc\" IS NOT NULL AND \"EndingDispositionDecidedByUserId\" IS NOT NULL)");
                    table.CheckConstraint("CK_LeaseManagement_PossessionExceptionPair", "((\"PossessionAgreementExceptionReason\" IS NULL) = (\"PossessionAgreementExceptionAuthorizedByUserId\" IS NULL)) AND (\"PossessionAgreementExceptionReason\" IS NULL OR \"PossessionGivenAtUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_LeaseManagement_PossessionNotCanceled", "\"PossessionGivenAtUtc\" IS NULL OR \"CanceledAtUtc\" IS NULL");
                    table.CheckConstraint("CK_LeaseManagement_ReturnAfterPossession", "\"PossessionReturnedAtUtc\" IS NULL OR \"PossessionReturnedAtUtc\" >= \"PossessionGivenAtUtc\"");
                    table.CheckConstraint("CK_LeaseManagement_ReturnRequiresPossession", "\"PossessionReturnedAtUtc\" IS NULL OR \"PossessionGivenAtUtc\" IS NOT NULL");
                    table.CheckConstraint("CK_LeaseManagement_TransferProvenance", "(\"TransferredFromLeaseManagementId\" IS NULL AND \"TransferPublicId\" IS NULL AND \"TransferredAtUtc\" IS NULL AND \"TransferReason\" IS NULL) OR (\"TransferredFromLeaseManagementId\" IS NOT NULL AND \"TransferPublicId\" IS NOT NULL AND \"TransferredAtUtc\" IS NOT NULL AND \"TransferReason\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_LeaseManagements_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseManagements_AspNetUsers_EndingDispositionDecidedByUser~",
                        column: x => x.EndingDispositionDecidedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseManagements_AspNetUsers_PossessionAgreementExceptionAu~",
                        column: x => x.PossessionAgreementExceptionAuthorizedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseManagements_LeaseManagements_TransferredFromLeaseManag~",
                        columns: x => new { x.TransferredFromLeaseManagementId, x.PortfolioId },
                        principalTable: "LeaseManagements",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseManagements_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseManagements_Properties_PropertyId_PortfolioId",
                        columns: x => new { x.PropertyId, x.PortfolioId },
                        principalTable: "Properties",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseManagements_Units_UnitId_PropertyId_PortfolioId",
                        columns: x => new { x.UnitId, x.PropertyId, x.PortfolioId },
                        principalTable: "Units",
                        principalColumns: new[] { "Id", "PropertyId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecurringExpenses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: true),
                    UnitId = table.Column<int>(type: "integer", nullable: true),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Frequency = table.Column<int>(type: "integer", nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextRunDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WorkerClaimOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WorkerClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkerClaimExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WorkerClaimAttemptCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringExpenses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecurringExpenses_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecurringExpenses_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RecurringExpenses_Units_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "RecurringMaintenanceTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: false),
                    UnitId = table.Column<int>(type: "integer", nullable: true),
                    VendorId = table.Column<int>(type: "integer", nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Category = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    RecurrenceInterval = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    NextDueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ScheduledTime = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    EstimatedCost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    LastGeneratedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WorkerClaimOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WorkerClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkerClaimExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WorkerClaimAttemptCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringMaintenanceTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecurringMaintenanceTasks_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecurringMaintenanceTasks_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecurringMaintenanceTasks_Units_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RecurringMaintenanceTasks_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "RentalListings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: false),
                    UnitId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ContentVersion = table.Column<int>(type: "integer", nullable: false),
                    Headline = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Rent = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SecurityDeposit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Bedrooms = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    Bathrooms = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    SquareFeet = table.Column<int>(type: "integer", nullable: true),
                    AvailableOn = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LeaseTerms = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PetPolicy = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Utilities = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Parking = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Amenities = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RentalListings", x => x.Id);
                    table.UniqueConstraint("AK_RentalListings_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.ForeignKey(
                        name: "FK_RentalListings_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RentalListings_Units_UnitId_PropertyId_PortfolioId",
                        columns: x => new { x.UnitId, x.PropertyId, x.PortfolioId },
                        principalTable: "Units",
                        principalColumns: new[] { "Id", "PropertyId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AuthSessionRefreshCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RefreshTokenFamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConsumedByOperationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReplacedByCredentialId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReuseDetectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthSessionRefreshCredentials", x => x.Id);
                    table.UniqueConstraint("AK_AuthSessionRefreshCredentials_Id_RefreshTokenFamilyId", x => new { x.Id, x.RefreshTokenFamilyId });
                    table.CheckConstraint("CK_AuthSessionRefreshCredentials_ConsumedFacts", "(\"ConsumedAtUtc\" IS NULL AND \"ConsumedByOperationId\" IS NULL) OR (\"ConsumedAtUtc\" IS NOT NULL AND \"ConsumedByOperationId\" IS NOT NULL AND \"ConsumedAtUtc\" >= \"IssuedAtUtc\")");
                    table.CheckConstraint("CK_AuthSessionRefreshCredentials_Expiry", "\"ExpiresAtUtc\" > \"IssuedAtUtc\"");
                    table.CheckConstraint("CK_AuthSessionRefreshCredentials_ReplacementRequiresConsumption", "\"ReplacedByCredentialId\" IS NULL OR (\"ConsumedAtUtc\" IS NOT NULL AND \"ReplacedByCredentialId\" <> \"Id\")");
                    table.CheckConstraint("CK_AuthSessionRefreshCredentials_ReuseFacts", "\"ReuseDetectedAtUtc\" IS NULL OR (\"ConsumedAtUtc\" IS NOT NULL AND \"RevokedAtUtc\" IS NOT NULL AND \"ReuseDetectedAtUtc\" >= \"ConsumedAtUtc\")");
                    table.CheckConstraint("CK_AuthSessionRefreshCredentials_RevocationFacts", "(\"RevokedAtUtc\" IS NULL AND \"RevocationReason\" IS NULL) OR (\"RevokedAtUtc\" IS NOT NULL AND \"RevocationReason\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_AuthSessionRefreshCredentials_AuthSessionRefreshCredentials~",
                        columns: x => new { x.ReplacedByCredentialId, x.RefreshTokenFamilyId },
                        principalTable: "AuthSessionRefreshCredentials",
                        principalColumns: new[] { "Id", "RefreshTokenFamilyId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AuthSessionRefreshCredentials_AuthSessionRefreshTokenFamili~",
                        column: x => x.RefreshTokenFamilyId,
                        principalTable: "AuthSessionRefreshTokenFamilies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MembershipRoleAssignmentProperties",
                columns: table => new
                {
                    MembershipRoleAssignmentId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: false),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MembershipRoleAssignmentProperties", x => new { x.MembershipRoleAssignmentId, x.PropertyId });
                    table.ForeignKey(
                        name: "FK_MembershipRoleAssignmentProperties_MembershipRoleAssignment~",
                        columns: x => new { x.MembershipRoleAssignmentId, x.PortfolioId },
                        principalTable: "MembershipRoleAssignments",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MembershipRoleAssignmentProperties_Properties_PropertyId_Po~",
                        columns: x => new { x.PropertyId, x.PortfolioId },
                        principalTable: "Properties",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LeaseAgreements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    LeaseManagementId = table.Column<int>(type: "integer", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    AgreementNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChangeType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CorrectionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TransferredFromAgreementId = table.Column<int>(type: "integer", nullable: true),
                    ReplacesAgreementId = table.Column<int>(type: "integer", nullable: true),
                    RenewsAgreementId = table.Column<int>(type: "integer", nullable: true),
                    ReissuesAgreementId = table.Column<int>(type: "integer", nullable: true),
                    ReissueReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TermType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TermStartOn = table.Column<DateOnly>(type: "date", nullable: false),
                    TermEndOn = table.Column<DateOnly>(type: "date", nullable: true),
                    GoverningFromOn = table.Column<DateOnly>(type: "date", nullable: false),
                    SupersededEffectiveOn = table.Column<DateOnly>(type: "date", nullable: true),
                    SupersededByAgreementId = table.Column<int>(type: "integer", nullable: true),
                    SupersessionRecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BaseRentAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RentDueDay = table.Column<short>(type: "smallint", nullable: false),
                    SecurityDepositObligation = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    LateFeeAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    GracePeriodDays = table.Column<short>(type: "smallint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    TermsSchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    TermsPayload = table.Column<string>(type: "jsonb", nullable: false),
                    DocumentSourceVersionId = table.Column<int>(type: "integer", nullable: false),
                    IssuedArtifactId = table.Column<int>(type: "integer", nullable: true),
                    IssuedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExecutedArtifactId = table.Column<int>(type: "integer", nullable: true),
                    FullyExecutedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VoidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VoidReasonCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    VoidNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DraftCanceledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DraftCancellationReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DraftCanceledByUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    DraftRevision = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaseAgreements", x => x.Id);
                    table.UniqueConstraint("AK_LeaseAgreements_Id_LeaseManagementId_PortfolioId", x => new { x.Id, x.LeaseManagementId, x.PortfolioId });
                    table.UniqueConstraint("AK_LeaseAgreements_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.CheckConstraint("CK_LeaseAgreement_ChangeType", "\"ChangeType\" IN ('Initial', 'Transfer', 'Correction', 'Renewal', 'MonthToMonth', 'Restatement')");
                    table.CheckConstraint("CK_LeaseAgreement_CorrectionReason", "(\"ChangeType\" = 'Correction' AND \"CorrectionReason\" IS NOT NULL AND length(btrim(\"CorrectionReason\")) > 0) OR (\"ChangeType\" <> 'Correction' AND \"CorrectionReason\" IS NULL)");
                    table.CheckConstraint("CK_LeaseAgreement_Currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("CK_LeaseAgreement_DraftCancellation", "(\"DraftCanceledAtUtc\" IS NULL AND \"DraftCancellationReason\" IS NULL AND \"DraftCanceledByUserId\" IS NULL) OR (\"DraftCanceledAtUtc\" IS NOT NULL AND \"DraftCancellationReason\" IS NOT NULL AND \"DraftCanceledByUserId\" IS NOT NULL)");
                    table.CheckConstraint("CK_LeaseAgreement_DraftRevision", "\"DraftRevision\" >= 1");
                    table.CheckConstraint("CK_LeaseAgreement_Execution", "((\"FullyExecutedAtUtc\" IS NULL) = (\"ExecutedArtifactId\" IS NULL)) AND (\"FullyExecutedAtUtc\" IS NULL OR \"IssuedAtUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_LeaseAgreement_GoverningDate", "\"GoverningFromOn\" >= \"TermStartOn\" AND (\"TermEndOn\" IS NULL OR \"GoverningFromOn\" <= \"TermEndOn\")");
                    table.CheckConstraint("CK_LeaseAgreement_Issuance", "(\"IssuedAtUtc\" IS NULL) = (\"IssuedArtifactId\" IS NULL)");
                    table.CheckConstraint("CK_LeaseAgreement_Lineage", "(\"ChangeType\" = 'Initial' AND ((\"VersionNumber\" = 1 AND \"ReissuesAgreementId\" IS NULL) OR (\"VersionNumber\" > 1 AND \"ReissuesAgreementId\" IS NOT NULL)) AND \"ReplacesAgreementId\" IS NULL AND \"RenewsAgreementId\" IS NULL AND \"TransferredFromAgreementId\" IS NULL) OR (\"ChangeType\" = 'Transfer' AND ((\"VersionNumber\" = 1 AND \"ReissuesAgreementId\" IS NULL) OR (\"VersionNumber\" > 1 AND \"ReissuesAgreementId\" IS NOT NULL)) AND \"TransferredFromAgreementId\" IS NOT NULL AND \"ReplacesAgreementId\" IS NULL AND \"RenewsAgreementId\" IS NULL) OR (\"ChangeType\" IN ('Correction', 'Restatement') AND \"ReplacesAgreementId\" IS NOT NULL AND \"RenewsAgreementId\" IS NULL AND \"TransferredFromAgreementId\" IS NULL) OR (\"ChangeType\" IN ('Renewal', 'MonthToMonth') AND \"RenewsAgreementId\" IS NOT NULL AND \"ReplacesAgreementId\" IS NULL AND \"TransferredFromAgreementId\" IS NULL)");
                    table.CheckConstraint("CK_LeaseAgreement_Money", "\"BaseRentAmount\" >= 0 AND \"SecurityDepositObligation\" >= 0 AND \"LateFeeAmount\" >= 0");
                    table.CheckConstraint("CK_LeaseAgreement_Reissue", "(\"ReissuesAgreementId\" IS NULL AND \"ReissueReason\" IS NULL) OR (\"ReissuesAgreementId\" IS NOT NULL AND \"ReissuesAgreementId\" <> \"Id\" AND \"ReissueReason\" IS NOT NULL AND length(btrim(\"ReissueReason\")) > 0)");
                    table.CheckConstraint("CK_LeaseAgreement_RentPolicy", "\"RentDueDay\" BETWEEN 1 AND 31 AND \"GracePeriodDays\" BETWEEN 0 AND 31");
                    table.CheckConstraint("CK_LeaseAgreement_SchemaVersions", "\"TermsSchemaVersion\" >= 1");
                    table.CheckConstraint("CK_LeaseAgreement_Supersession", "(\"SupersededEffectiveOn\" IS NULL AND \"SupersededByAgreementId\" IS NULL AND \"SupersessionRecordedAtUtc\" IS NULL) OR (\"SupersededEffectiveOn\" IS NOT NULL AND \"SupersededByAgreementId\" IS NOT NULL AND \"SupersessionRecordedAtUtc\" IS NOT NULL AND \"SupersededEffectiveOn\" > \"GoverningFromOn\")");
                    table.CheckConstraint("CK_LeaseAgreement_Term", "(\"TermType\" = 'FixedTerm' AND \"TermEndOn\" IS NOT NULL AND \"TermEndOn\" >= \"TermStartOn\") OR (\"TermType\" = 'MonthToMonth' AND \"TermEndOn\" IS NULL)");
                    table.CheckConstraint("CK_LeaseAgreement_TerminalFacts", "NOT (\"VoidedAtUtc\" IS NOT NULL AND \"DraftCanceledAtUtc\" IS NOT NULL) AND (\"DraftCanceledAtUtc\" IS NULL OR \"IssuedAtUtc\" IS NULL)");
                    table.CheckConstraint("CK_LeaseAgreement_Version", "\"VersionNumber\" >= 1");
                    table.CheckConstraint("CK_LeaseAgreement_Void", "((\"VoidedAtUtc\" IS NULL) = (\"VoidReasonCode\" IS NULL)) AND (\"VoidedAtUtc\" IS NULL OR \"IssuedAtUtc\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_LeaseAgreements_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAgreements_AspNetUsers_DraftCanceledByUserId",
                        column: x => x.DraftCanceledByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAgreements_LeaseAgreements_ReissuesAgreementId_LeaseMa~",
                        columns: x => new { x.ReissuesAgreementId, x.LeaseManagementId, x.PortfolioId },
                        principalTable: "LeaseAgreements",
                        principalColumns: new[] { "Id", "LeaseManagementId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAgreements_LeaseAgreements_RenewsAgreementId_LeaseMana~",
                        columns: x => new { x.RenewsAgreementId, x.LeaseManagementId, x.PortfolioId },
                        principalTable: "LeaseAgreements",
                        principalColumns: new[] { "Id", "LeaseManagementId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAgreements_LeaseAgreements_ReplacesAgreementId_LeaseMa~",
                        columns: x => new { x.ReplacesAgreementId, x.LeaseManagementId, x.PortfolioId },
                        principalTable: "LeaseAgreements",
                        principalColumns: new[] { "Id", "LeaseManagementId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAgreements_LeaseAgreements_SupersededByAgreementId_Lea~",
                        columns: x => new { x.SupersededByAgreementId, x.LeaseManagementId, x.PortfolioId },
                        principalTable: "LeaseAgreements",
                        principalColumns: new[] { "Id", "LeaseManagementId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAgreements_LeaseAgreements_TransferredFromAgreementId_~",
                        columns: x => new { x.TransferredFromAgreementId, x.PortfolioId },
                        principalTable: "LeaseAgreements",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAgreements_LeaseManagements_LeaseManagementId_Portfoli~",
                        columns: x => new { x.LeaseManagementId, x.PortfolioId },
                        principalTable: "LeaseManagements",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAgreements_LegalDocumentArtifacts_ExecutedArtifactId_P~",
                        columns: x => new { x.ExecutedArtifactId, x.PortfolioId },
                        principalTable: "LegalDocumentArtifacts",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAgreements_LegalDocumentArtifacts_IssuedArtifactId_Por~",
                        columns: x => new { x.IssuedArtifactId, x.PortfolioId },
                        principalTable: "LegalDocumentArtifacts",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAgreements_LegalDocumentSourceVersions_DocumentSourceV~",
                        columns: x => new { x.DocumentSourceVersionId, x.PortfolioId },
                        principalTable: "LegalDocumentSourceVersions",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAgreements_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LeaseManagementParties",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    LeaseManagementId = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveThrough = table.Column<DateOnly>(type: "date", nullable: true),
                    GuarantorLegalNoticeEligible = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ChangeReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaseManagementParties", x => x.Id);
                    table.UniqueConstraint("AK_LeaseManagementParties_Id_LeaseManagementId_PortfolioId", x => new { x.Id, x.LeaseManagementId, x.PortfolioId });
                    table.UniqueConstraint("AK_LeaseManagementParties_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.CheckConstraint("CK_LeaseManagementParty_EffectiveDates", "\"EffectiveThrough\" IS NULL OR \"EffectiveThrough\" >= \"EffectiveFrom\"");
                    table.CheckConstraint("CK_LeaseManagementParty_GuarantorNoticeEligibility", "\"Role\" = 'Guarantor' OR \"GuarantorLegalNoticeEligible\" = false");
                    table.CheckConstraint("CK_LeaseManagementParty_Role", "\"Role\" IN ('PrimaryTenant', 'CoTenant', 'Guarantor', 'Occupant')");
                    table.ForeignKey(
                        name: "FK_LeaseManagementParties_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseManagementParties_LeaseManagements_LeaseManagementId_P~",
                        columns: x => new { x.LeaseManagementId, x.PortfolioId },
                        principalTable: "LeaseManagements",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseManagementParties_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseManagementParties_Tenants_TenantId_PortfolioId",
                        columns: x => new { x.TenantId, x.PortfolioId },
                        principalTable: "Tenants",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RentalApplications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: true),
                    UnitId = table.Column<int>(type: "integer", nullable: true),
                    FirstName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    DateOfBirth = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CurrentAddressLine1 = table.Column<string>(type: "text", nullable: true),
                    CurrentAddressLine2 = table.Column<string>(type: "text", nullable: true),
                    CurrentCity = table.Column<string>(type: "text", nullable: true),
                    CurrentState = table.Column<string>(type: "text", nullable: true),
                    CurrentPostalCode = table.Column<string>(type: "text", nullable: true),
                    CurrentAddress = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Employer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MonthlyIncome = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    DesiredMoveInDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IdExtractedFields = table.Column<string>(type: "jsonb", nullable: true),
                    ConsentGiven = table.Column<bool>(type: "boolean", nullable: false),
                    ConsentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConsentIpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DecisionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedTenantId = table.Column<int>(type: "integer", nullable: true),
                    PreparedLeaseManagementId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RentalApplications", x => x.Id);
                    table.UniqueConstraint("AK_RentalApplications_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.ForeignKey(
                        name: "FK_RentalApplications_LeaseManagements_PreparedLeaseManagement~",
                        columns: x => new { x.PreparedLeaseManagementId, x.PortfolioId },
                        principalTable: "LeaseManagements",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RentalApplications_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RentalApplications_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RentalApplications_Tenants_ApprovedTenantId",
                        column: x => x.ApprovedTenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RentalApplications_Units_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TenantAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    LeaseManagementId = table.Column<int>(type: "integer", nullable: false),
                    AccountNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    OpenedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    ClosedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CloseReasonCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    CloseNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantAccounts", x => x.Id);
                    table.UniqueConstraint("AK_TenantAccounts_Id_LeaseManagementId_PortfolioId", x => new { x.Id, x.LeaseManagementId, x.PortfolioId });
                    table.UniqueConstraint("AK_TenantAccounts_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.CheckConstraint("CK_TenantAccount_Close", "(\"ClosedAtUtc\" IS NULL AND \"CloseReasonCode\" IS NULL AND \"CloseNote\" IS NULL) OR (\"ClosedAtUtc\" IS NOT NULL AND \"CloseReasonCode\" IS NOT NULL)");
                    table.CheckConstraint("CK_TenantAccount_CloseAfterOpen", "\"ClosedAtUtc\" IS NULL OR \"ClosedAtUtc\" >= \"OpenedAtUtc\"");
                    table.CheckConstraint("CK_TenantAccount_Currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                    table.ForeignKey(
                        name: "FK_TenantAccounts_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantAccounts_LeaseManagements_LeaseManagementId_Portfolio~",
                        columns: x => new { x.LeaseManagementId, x.PortfolioId },
                        principalTable: "LeaseManagements",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantAccounts_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UnitOperationalPeriods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: false),
                    UnitId = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SourceLeaseManagementId = table.Column<int>(type: "integer", nullable: true),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnitOperationalPeriods", x => x.Id);
                    table.CheckConstraint("CK_UnitOperationalPeriod_EndAfterStart", "\"EndedAtUtc\" IS NULL OR \"EndedAtUtc\" > \"StartedAtUtc\"");
                    table.CheckConstraint("CK_UnitOperationalPeriod_TurnoverSource", "\"Type\" <> 'Turnover' OR \"SourceLeaseManagementId\" IS NOT NULL");
                    table.CheckConstraint("CK_UnitOperationalPeriod_Type", "\"Type\" IN ('Turnover', 'OutOfService', 'ManagementHold')");
                    table.ForeignKey(
                        name: "FK_UnitOperationalPeriods_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UnitOperationalPeriods_LeaseManagements_SourceLeaseManageme~",
                        columns: x => new { x.SourceLeaseManagementId, x.UnitId, x.PortfolioId },
                        principalTable: "LeaseManagements",
                        principalColumns: new[] { "Id", "UnitId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UnitOperationalPeriods_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UnitOperationalPeriods_Properties_PropertyId_PortfolioId",
                        columns: x => new { x.PropertyId, x.PortfolioId },
                        principalTable: "Properties",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UnitOperationalPeriods_Units_UnitId_PropertyId_PortfolioId",
                        columns: x => new { x.UnitId, x.PropertyId, x.PortfolioId },
                        principalTable: "Units",
                        principalColumns: new[] { "Id", "PropertyId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: false),
                    UnitId = table.Column<int>(type: "integer", nullable: true),
                    TenantId = table.Column<int>(type: "integer", nullable: true),
                    LeaseManagementId = table.Column<int>(type: "integer", nullable: true),
                    VendorId = table.Column<int>(type: "integer", nullable: true),
                    RecurringMaintenanceTaskId = table.Column<int>(type: "integer", nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Category = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ScheduledFor = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ScheduledWindowEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EstimatedCost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    ActualCost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExtractedData = table.Column<string>(type: "jsonb", nullable: true),
                    TechnicianAccessInstructions = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrders", x => x.Id);
                    table.UniqueConstraint("AK_WorkOrders_Id_PropertyId_PortfolioId", x => new { x.Id, x.PropertyId, x.PortfolioId });
                    table.ForeignKey(
                        name: "FK_WorkOrders_LeaseManagements_LeaseManagementId",
                        column: x => x.LeaseManagementId,
                        principalTable: "LeaseManagements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WorkOrders_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkOrders_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkOrders_RecurringMaintenanceTasks_RecurringMaintenanceTa~",
                        column: x => x.RecurringMaintenanceTaskId,
                        principalTable: "RecurringMaintenanceTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WorkOrders_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WorkOrders_Units_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WorkOrders_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ListingPhotos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    RentalListingId = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Category = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Caption = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    StoredFileId = table.Column<int>(type: "integer", nullable: true),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: true),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListingPhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ListingPhotos_RentalListings_RentalListingId_PortfolioId",
                        columns: x => new { x.RentalListingId, x.PortfolioId },
                        principalTable: "RentalListings",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ListingPhotos_StoredFiles_StoredFileId_PortfolioId",
                        columns: x => new { x.StoredFileId, x.PortfolioId },
                        principalTable: "StoredFiles",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ListingPublications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    RentalListingId = table.Column<int>(type: "integer", nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Mode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PublishedContentVersion = table.Column<int>(type: "integer", nullable: true),
                    ExternalListingId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ListingUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ApplicationUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ManagementUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LastConfirmedExternalStatus = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    LastConfirmedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CopyConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TermsConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PhotosConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    ProviderWorkspaceOpened = table.Column<bool>(type: "boolean", nullable: false),
                    LastDeliveryKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LastDeliveryStatus = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    LastDeliveryError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LastDeliveryAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListingPublications", x => x.Id);
                    table.UniqueConstraint("AK_ListingPublications_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.ForeignKey(
                        name: "FK_ListingPublications_RentalListings_RentalListingId_Portfoli~",
                        columns: x => new { x.RentalListingId, x.PortfolioId },
                        principalTable: "RentalListings",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EvictionCases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    LeaseManagementId = table.Column<int>(type: "integer", nullable: false),
                    LeaseAgreementId = table.Column<int>(type: "integer", nullable: true),
                    PropertyId = table.Column<int>(type: "integer", nullable: false),
                    UnitId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    FiledOnDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HearingDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedOnDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CourtName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CaseNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Resolution = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvictionCases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvictionCases_LeaseAgreements_LeaseAgreementId",
                        column: x => x.LeaseAgreementId,
                        principalTable: "LeaseAgreements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvictionCases_LeaseManagements_LeaseManagementId",
                        column: x => x.LeaseManagementId,
                        principalTable: "LeaseManagements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvictionCases_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EvictionCases_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvictionCases_Units_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Inspections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: false),
                    UnitId = table.Column<int>(type: "integer", nullable: true),
                    LeaseManagementId = table.Column<int>(type: "integer", nullable: true),
                    LeaseAgreementId = table.Column<int>(type: "integer", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ScheduledFor = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    TemplateId = table.Column<int>(type: "integer", nullable: true),
                    ReportStoredFileId = table.Column<int>(type: "integer", nullable: true),
                    Inspector = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Inspections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Inspections_LeaseAgreements_LeaseAgreementId",
                        column: x => x.LeaseAgreementId,
                        principalTable: "LeaseAgreements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Inspections_LeaseManagements_LeaseManagementId",
                        column: x => x.LeaseManagementId,
                        principalTable: "LeaseManagements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Inspections_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Inspections_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Inspections_Units_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "LeaseAddenda",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    SeriesPublicId = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    LeaseManagementId = table.Column<int>(type: "integer", nullable: false),
                    BaseAgreementId = table.Column<int>(type: "integer", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    AddendumNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ReplacesAddendumId = table.Column<int>(type: "integer", nullable: true),
                    EffectiveFromOn = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveThroughOn = table.Column<DateOnly>(type: "date", nullable: true),
                    SupersededEffectiveOn = table.Column<DateOnly>(type: "date", nullable: true),
                    SupersededByAddendumId = table.Column<int>(type: "integer", nullable: true),
                    SupersessionRecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TermsSchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    TermsPayload = table.Column<string>(type: "jsonb", nullable: false),
                    DocumentSourceVersionId = table.Column<int>(type: "integer", nullable: false),
                    IssuedArtifactId = table.Column<int>(type: "integer", nullable: true),
                    IssuedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExecutedArtifactId = table.Column<int>(type: "integer", nullable: true),
                    FullyExecutedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VoidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VoidReasonCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    VoidNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DraftCanceledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DraftCancellationReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    DraftRevision = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaseAddenda", x => x.Id);
                    table.UniqueConstraint("AK_LeaseAddenda_Id_LeaseManagementId_PortfolioId", x => new { x.Id, x.LeaseManagementId, x.PortfolioId });
                    table.UniqueConstraint("AK_LeaseAddenda_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.UniqueConstraint("AK_LeaseAddenda_Id_SeriesPublicId_LeaseManagementId_PortfolioId", x => new { x.Id, x.SeriesPublicId, x.LeaseManagementId, x.PortfolioId });
                    table.CheckConstraint("CK_LeaseAddendum_DraftCancellation", "(\"DraftCanceledAtUtc\" IS NULL) = (\"DraftCancellationReason\" IS NULL)");
                    table.CheckConstraint("CK_LeaseAddendum_DraftRevision", "\"DraftRevision\" >= 1");
                    table.CheckConstraint("CK_LeaseAddendum_EffectiveDates", "\"EffectiveThroughOn\" IS NULL OR \"EffectiveThroughOn\" >= \"EffectiveFromOn\"");
                    table.CheckConstraint("CK_LeaseAddendum_Execution", "((\"FullyExecutedAtUtc\" IS NULL) = (\"ExecutedArtifactId\" IS NULL)) AND (\"FullyExecutedAtUtc\" IS NULL OR \"IssuedAtUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_LeaseAddendum_Issuance", "(\"IssuedAtUtc\" IS NULL) = (\"IssuedArtifactId\" IS NULL)");
                    table.CheckConstraint("CK_LeaseAddendum_Lineage", "(\"VersionNumber\" = 1 AND \"ReplacesAddendumId\" IS NULL) OR (\"VersionNumber\" > 1 AND \"ReplacesAddendumId\" IS NOT NULL)");
                    table.CheckConstraint("CK_LeaseAddendum_Purpose", "\"Purpose\" IN ('Financial', 'Pet', 'Occupancy', 'Rules', 'Other')");
                    table.CheckConstraint("CK_LeaseAddendum_SchemaVersions", "\"TermsSchemaVersion\" >= 1");
                    table.CheckConstraint("CK_LeaseAddendum_Supersession", "(\"SupersededEffectiveOn\" IS NULL AND \"SupersededByAddendumId\" IS NULL AND \"SupersessionRecordedAtUtc\" IS NULL) OR (\"SupersededEffectiveOn\" IS NOT NULL AND \"SupersessionRecordedAtUtc\" IS NOT NULL AND \"SupersededEffectiveOn\" > \"EffectiveFromOn\")");
                    table.CheckConstraint("CK_LeaseAddendum_TerminalFacts", "NOT (\"VoidedAtUtc\" IS NOT NULL AND \"DraftCanceledAtUtc\" IS NOT NULL) AND (\"DraftCanceledAtUtc\" IS NULL OR \"IssuedAtUtc\" IS NULL)");
                    table.CheckConstraint("CK_LeaseAddendum_Version", "\"VersionNumber\" >= 1");
                    table.CheckConstraint("CK_LeaseAddendum_Void", "((\"VoidedAtUtc\" IS NULL) = (\"VoidReasonCode\" IS NULL)) AND (\"VoidedAtUtc\" IS NULL OR \"IssuedAtUtc\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_LeaseAddenda_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAddenda_LeaseAddenda_ReplacesAddendumId_SeriesPublicId~",
                        columns: x => new { x.ReplacesAddendumId, x.SeriesPublicId, x.LeaseManagementId, x.PortfolioId },
                        principalTable: "LeaseAddenda",
                        principalColumns: new[] { "Id", "SeriesPublicId", "LeaseManagementId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAddenda_LeaseAddenda_SupersededByAddendumId_SeriesPubl~",
                        columns: x => new { x.SupersededByAddendumId, x.SeriesPublicId, x.LeaseManagementId, x.PortfolioId },
                        principalTable: "LeaseAddenda",
                        principalColumns: new[] { "Id", "SeriesPublicId", "LeaseManagementId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAddenda_LeaseAgreements_BaseAgreementId_LeaseManagemen~",
                        columns: x => new { x.BaseAgreementId, x.LeaseManagementId, x.PortfolioId },
                        principalTable: "LeaseAgreements",
                        principalColumns: new[] { "Id", "LeaseManagementId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAddenda_LeaseManagements_LeaseManagementId_PortfolioId",
                        columns: x => new { x.LeaseManagementId, x.PortfolioId },
                        principalTable: "LeaseManagements",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAddenda_LegalDocumentArtifacts_ExecutedArtifactId_Port~",
                        columns: x => new { x.ExecutedArtifactId, x.PortfolioId },
                        principalTable: "LegalDocumentArtifacts",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAddenda_LegalDocumentArtifacts_IssuedArtifactId_Portfo~",
                        columns: x => new { x.IssuedArtifactId, x.PortfolioId },
                        principalTable: "LegalDocumentArtifacts",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAddenda_LegalDocumentSourceVersions_DocumentSourceVers~",
                        columns: x => new { x.DocumentSourceVersionId, x.PortfolioId },
                        principalTable: "LegalDocumentSourceVersions",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAddenda_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LeaseAgreementSigners",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    LeaseAgreementId = table.Column<int>(type: "integer", nullable: false),
                    LeaseManagementPartyId = table.Column<int>(type: "integer", nullable: true),
                    TenantId = table.Column<int>(type: "integer", nullable: true),
                    SignerRole = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    NameSnapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EmailSnapshot = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    SigningOrder = table.Column<short>(type: "smallint", nullable: false),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaseAgreementSigners", x => x.Id);
                    table.UniqueConstraint("AK_LeaseAgreementSigners_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.CheckConstraint("CK_LeaseAgreementSigner_Order", "\"SigningOrder\" > 0");
                    table.CheckConstraint("CK_LeaseAgreementSigner_Role", "\"SignerRole\" IN ('PrimaryTenant', 'CoTenant', 'Guarantor', 'Manager', 'Owner', 'Other')");
                    table.ForeignKey(
                        name: "FK_LeaseAgreementSigners_LeaseAgreements_LeaseAgreementId_Port~",
                        columns: x => new { x.LeaseAgreementId, x.PortfolioId },
                        principalTable: "LeaseAgreements",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAgreementSigners_LeaseManagementParties_LeaseManagemen~",
                        columns: x => new { x.LeaseManagementPartyId, x.PortfolioId },
                        principalTable: "LeaseManagementParties",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAgreementSigners_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAgreementSigners_Tenants_TenantId_PortfolioId",
                        columns: x => new { x.TenantId, x.PortfolioId },
                        principalTable: "Tenants",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantUserAccesses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    AccessContextId = table.Column<int>(type: "integer", nullable: false),
                    ApplicationUserId = table.Column<int>(type: "integer", nullable: false),
                    LeaseManagementPartyId = table.Column<int>(type: "integer", nullable: false),
                    GrantedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GrantedByUserId = table.Column<int>(type: "integer", nullable: false),
                    RevokedByUserId = table.Column<int>(type: "integer", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantUserAccesses", x => x.Id);
                    table.CheckConstraint("CK_TenantUserAccess_RevocationAfterGrant", "\"RevokedAtUtc\" IS NULL OR \"RevokedAtUtc\" >= \"GrantedAtUtc\"");
                    table.CheckConstraint("CK_TenantUserAccess_RevocationPair", "(\"RevokedAtUtc\" IS NULL) = (\"RevokedByUserId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_TenantUserAccesses_AspNetUsers_ApplicationUserId",
                        column: x => x.ApplicationUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantUserAccesses_AspNetUsers_GrantedByUserId",
                        column: x => x.GrantedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantUserAccesses_AspNetUsers_RevokedByUserId",
                        column: x => x.RevokedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantUserAccesses_LeaseManagementParties_LeaseManagementPa~",
                        columns: x => new { x.LeaseManagementPartyId, x.PortfolioId },
                        principalTable: "LeaseManagementParties",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantUserAccesses_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantUserAccesses_WorkspaceAccessContexts_AccessContextId_~",
                        columns: x => new { x.AccessContextId, x.ApplicationUserId, x.PortfolioId },
                        principalTable: "WorkspaceAccessContexts",
                        principalColumns: new[] { "Id", "UserId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AdverseActionNotices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    ApplicationId = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreditReportingAgency = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    GeneratedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StoredFileId = table.Column<int>(type: "integer", nullable: true),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdverseActionNotices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdverseActionNotices_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AdverseActionNotices_RentalApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "RentalApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AdverseActionNotices_StoredFiles_StoredFileId",
                        column: x => x.StoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ApplicantScreenings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    ApplicationId = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ProviderDisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ProviderReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ProviderHostedUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    OperationKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ConsentConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    ConsentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    InvitedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApplicantSubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastStatusAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Decision = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    DecisionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DecisionRecordedByUserId = table.Column<int>(type: "integer", nullable: true),
                    DecisionRecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConsumerReportUsedForDecision = table.Column<bool>(type: "boolean", nullable: false),
                    CreditReportingAgencyName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreditReportingAgencyAddress = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreditReportingAgencyPhone = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicantScreenings", x => x.Id);
                    table.UniqueConstraint("AK_ApplicantScreenings_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.ForeignKey(
                        name: "FK_ApplicantScreenings_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApplicantScreenings_AspNetUsers_DecisionRecordedByUserId",
                        column: x => x.DecisionRecordedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApplicantScreenings_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ApplicantScreenings_RentalApplications_ApplicationId_Portfo~",
                        columns: x => new { x.ApplicationId, x.PortfolioId },
                        principalTable: "RentalApplications",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationFinancialAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    RentalApplicationId = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    OpenedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationFinancialAccounts", x => x.Id);
                    table.UniqueConstraint("AK_ApplicationFinancialAccounts_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.CheckConstraint("CK_ApplicationFinancialAccount_Currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                    table.ForeignKey(
                        name: "FK_ApplicationFinancialAccounts_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApplicationFinancialAccounts_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApplicationFinancialAccounts_RentalApplications_RentalAppli~",
                        columns: x => new { x.RentalApplicationId, x.PortfolioId },
                        principalTable: "RentalApplications",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Appointments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: true),
                    UnitId = table.Column<int>(type: "integer", nullable: true),
                    LeaseManagementId = table.Column<int>(type: "integer", nullable: true),
                    RentalApplicationId = table.Column<int>(type: "integer", nullable: true),
                    TenantId = table.Column<int>(type: "integer", nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProspectName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ProspectEmail = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ScheduledStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ScheduledEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AssignedTo = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Appointments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Appointments_LeaseManagements_LeaseManagementId",
                        column: x => x.LeaseManagementId,
                        principalTable: "LeaseManagements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Appointments_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Appointments_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Appointments_RentalApplications_RentalApplicationId",
                        column: x => x.RentalApplicationId,
                        principalTable: "RentalApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Appointments_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Appointments_Units_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SecurityDepositAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    TenantAccountId = table.Column<int>(type: "integer", nullable: false),
                    OriginatingAgreementId = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityDepositAccounts", x => x.Id);
                    table.UniqueConstraint("AK_SecurityDepositAccounts_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.UniqueConstraint("AK_SecurityDepositAccounts_Id_TenantAccountId_PortfolioId", x => new { x.Id, x.TenantAccountId, x.PortfolioId });
                    table.CheckConstraint("CK_SecurityDepositAccount_Currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                    table.ForeignKey(
                        name: "FK_SecurityDepositAccounts_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SecurityDepositAccounts_LeaseAgreements_OriginatingAgreemen~",
                        columns: x => new { x.OriginatingAgreementId, x.PortfolioId },
                        principalTable: "LeaseAgreements",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SecurityDepositAccounts_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SecurityDepositAccounts_TenantAccounts_TenantAccountId_Port~",
                        columns: x => new { x.TenantAccountId, x.PortfolioId },
                        principalTable: "TenantAccounts",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantAccountConditionPeriods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    TenantAccountId = table.Column<int>(type: "integer", nullable: false),
                    Condition = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    EndedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantAccountConditionPeriods", x => x.Id);
                    table.CheckConstraint("CK_TenantAccountConditionPeriod_Condition", "\"Condition\" IN ('PaymentPlan', 'Collections')");
                    table.CheckConstraint("CK_TenantAccountConditionPeriod_EndAfterStart", "\"EndedAtUtc\" IS NULL OR \"EndedAtUtc\" > \"StartedAtUtc\"");
                    table.ForeignKey(
                        name: "FK_TenantAccountConditionPeriods_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantAccountConditionPeriods_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantAccountConditionPeriods_TenantAccounts_TenantAccountI~",
                        columns: x => new { x.TenantAccountId, x.PortfolioId },
                        principalTable: "TenantAccounts",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantAutopayEnrollments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    TenantAccountId = table.Column<int>(type: "integer", nullable: false),
                    AuthorizingPartyId = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ProviderCustomerId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProviderPaymentMethodId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AuthorizationArtifactId = table.Column<int>(type: "integer", nullable: true),
                    EnrolledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    CanceledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantAutopayEnrollments", x => x.Id);
                    table.CheckConstraint("CK_TenantAutopayEnrollment_CancelAfterEnroll", "\"CanceledAtUtc\" IS NULL OR \"CanceledAtUtc\" >= \"EnrolledAtUtc\"");
                    table.CheckConstraint("CK_TenantAutopayEnrollment_Cancellation", "(\"CanceledAtUtc\" IS NULL AND \"CancelReason\" IS NULL) OR (\"CanceledAtUtc\" IS NOT NULL AND \"CancelReason\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_TenantAutopayEnrollments_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantAutopayEnrollments_LeaseManagementParties_Authorizing~",
                        columns: x => new { x.AuthorizingPartyId, x.PortfolioId },
                        principalTable: "LeaseManagementParties",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantAutopayEnrollments_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantAutopayEnrollments_StoredFiles_AuthorizationArtifactI~",
                        columns: x => new { x.AuthorizationArtifactId, x.PortfolioId },
                        principalTable: "StoredFiles",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantAutopayEnrollments_TenantAccounts_TenantAccountId_Por~",
                        columns: x => new { x.TenantAccountId, x.PortfolioId },
                        principalTable: "TenantAccounts",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantPaymentAttempts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    TenantAccountId = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ProviderObjectId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RefundsPaymentAttemptId = table.Column<long>(type: "bigint", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AttemptType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    PaymentMethodSummary = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PayerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CheckNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    BankName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    PreparedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SettledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    ClaimOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaimExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantPaymentAttempts", x => x.Id);
                    table.UniqueConstraint("AK_TenantPaymentAttempts_Id_TenantAccountId_PortfolioId", x => new { x.Id, x.TenantAccountId, x.PortfolioId });
                    table.CheckConstraint("CK_TenantPaymentAttempt_Amount", "(\"AttemptType\" = 'Verification' AND \"Amount\" = 0) OR (\"AttemptType\" IN ('Charge','Refund') AND \"Amount\" > 0)");
                    table.CheckConstraint("CK_TenantPaymentAttempt_AttemptCount", "\"AttemptCount\" >= 0");
                    table.CheckConstraint("CK_TenantPaymentAttempt_Claim", "(\"ClaimOwner\" IS NULL AND \"ClaimToken\" IS NULL AND \"ClaimExpiresAtUtc\" IS NULL) OR (\"ClaimOwner\" IS NOT NULL AND \"ClaimToken\" IS NOT NULL AND \"ClaimExpiresAtUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_TenantPaymentAttempt_Currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("CK_TenantPaymentAttempt_RefundPayout", "\"AttemptType\" <> 'Refund' OR (\"Provider\" = 'manual' AND NULLIF(btrim(\"ProviderObjectId\"), '') IS NOT NULL AND NULLIF(btrim(\"PaymentMethodSummary\"), '') IS NOT NULL)");
                    table.CheckConstraint("CK_TenantPaymentAttempt_RefundProvenance", "(\"AttemptType\" = 'Refund') = (\"RefundsPaymentAttemptId\" IS NOT NULL)");
                    table.CheckConstraint("CK_TenantPaymentAttempt_RefundTerminal", "\"AttemptType\" <> 'Refund' OR \"State\" = 'Succeeded'");
                    table.CheckConstraint("CK_TenantPaymentAttempt_Settlement", "(\"State\" = 'Succeeded' AND \"SettledAtUtc\" IS NOT NULL AND \"SubmittedAtUtc\" IS NOT NULL AND \"SettledAtUtc\" >= \"SubmittedAtUtc\") OR (\"State\" <> 'Succeeded' AND \"SettledAtUtc\" IS NULL)");
                    table.CheckConstraint("CK_TenantPaymentAttempt_State", "\"State\" IN ('Prepared', 'Submitted', 'Succeeded', 'Failed', 'Canceled', 'Unknown')");
                    table.CheckConstraint("CK_TenantPaymentAttempt_Submission", "\"SubmittedAtUtc\" IS NULL OR \"SubmittedAtUtc\" >= \"PreparedAtUtc\"");
                    table.CheckConstraint("CK_TenantPaymentAttempt_Type", "\"AttemptType\" IN ('Charge', 'Refund', 'Verification')");
                    table.ForeignKey(
                        name: "FK_TenantPaymentAttempts_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantPaymentAttempts_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantPaymentAttempts_TenantAccounts_TenantAccountId_Portfo~",
                        columns: x => new { x.TenantAccountId, x.PortfolioId },
                        principalTable: "TenantAccounts",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantPaymentAttempts_TenantPaymentAttempts_RefundsPaymentA~",
                        columns: x => new { x.RefundsPaymentAttemptId, x.TenantAccountId, x.PortfolioId },
                        principalTable: "TenantPaymentAttempts",
                        principalColumns: new[] { "Id", "TenantAccountId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: true),
                    WorkOrderId = table.Column<int>(type: "integer", nullable: true),
                    StartedByLandlord = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastMessageAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastMessagePreview = table.Column<string>(type: "character varying(280)", maxLength: 280, nullable: true),
                    LandlordUnreadCount = table.Column<int>(type: "integer", nullable: false),
                    TenantUnreadCount = table.Column<int>(type: "integer", nullable: false),
                    TechnicianUnreadCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Conversations_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Conversations_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Conversations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Conversations_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VendorDispatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    WorkOrderId = table.Column<int>(type: "integer", nullable: false),
                    VendorId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DispatchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RespondedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Message = table.Column<string>(type: "character varying(1600)", maxLength: 1600, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorDispatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VendorDispatches_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VendorDispatches_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VendorDispatches_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VendorRatings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    VendorId = table.Column<int>(type: "integer", nullable: false),
                    WorkOrderId = table.Column<int>(type: "integer", nullable: true),
                    Stars = table.Column<int>(type: "integer", nullable: false),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorRatings", x => x.Id);
                    table.CheckConstraint("CK_VendorRating_Stars", "\"Stars\" >= 1 AND \"Stars\" <= 5");
                    table.ForeignKey(
                        name: "FK_VendorRatings_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VendorRatings_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VendorRatings_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrderResponsibilities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: false),
                    WorkOrderId = table.Column<int>(type: "integer", nullable: false),
                    WorkspaceMembershipId = table.Column<int>(type: "integer", nullable: false),
                    MembershipRoleAssignmentId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    EffectiveFromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EffectiveToUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AssignedByUserId = table.Column<int>(type: "integer", nullable: false),
                    AssignedByAccessContextId = table.Column<int>(type: "integer", nullable: false),
                    AssignedReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    AssignedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedByUserId = table.Column<int>(type: "integer", nullable: true),
                    EndedByAccessContextId = table.Column<int>(type: "integer", nullable: true),
                    EndedReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    EndedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrderResponsibilities", x => x.Id);
                    table.CheckConstraint("CK_WorkOrderResponsibilities_AssignedFacts", "\"AssignedAtUtc\" = \"EffectiveFromUtc\" AND length(btrim(\"AssignedReason\")) > 0");
                    table.CheckConstraint("CK_WorkOrderResponsibilities_EffectivePeriod", "\"EffectiveToUtc\" IS NULL OR \"EffectiveToUtc\" > \"EffectiveFromUtc\"");
                    table.CheckConstraint("CK_WorkOrderResponsibilities_EndFacts", "(\"EffectiveToUtc\" IS NULL AND \"EndedAtUtc\" IS NULL AND \"EndedByUserId\" IS NULL AND \"EndedByAccessContextId\" IS NULL AND \"EndedReason\" IS NULL) OR (\"EffectiveToUtc\" IS NOT NULL AND \"EndedAtUtc\" = \"EffectiveToUtc\" AND \"EndedByUserId\" IS NOT NULL AND \"EndedByAccessContextId\" IS NOT NULL AND \"EndedReason\" IS NOT NULL AND length(btrim(\"EndedReason\")) > 0)");
                    table.CheckConstraint("CK_WorkOrderResponsibilities_Kind", "\"Kind\" IN ('Primary', 'Supporting')");
                    table.ForeignKey(
                        name: "FK_WorkOrderResponsibilities_AspNetUsers_AssignedByUserId",
                        column: x => x.AssignedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrderResponsibilities_AspNetUsers_EndedByUserId",
                        column: x => x.EndedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrderResponsibilities_MembershipRoleAssignments_Members~",
                        columns: x => new { x.MembershipRoleAssignmentId, x.WorkspaceMembershipId, x.PortfolioId },
                        principalTable: "MembershipRoleAssignments",
                        principalColumns: new[] { "Id", "WorkspaceMembershipId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrderResponsibilities_WorkOrders_WorkOrderId_PropertyId~",
                        columns: x => new { x.WorkOrderId, x.PropertyId, x.PortfolioId },
                        principalTable: "WorkOrders",
                        principalColumns: new[] { "Id", "PropertyId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrderResponsibilities_WorkspaceAccessContexts_AssignedB~",
                        columns: x => new { x.AssignedByAccessContextId, x.AssignedByUserId, x.PortfolioId },
                        principalTable: "WorkspaceAccessContexts",
                        principalColumns: new[] { "Id", "UserId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrderResponsibilities_WorkspaceAccessContexts_EndedByAc~",
                        columns: x => new { x.EndedByAccessContextId, x.EndedByUserId, x.PortfolioId },
                        principalTable: "WorkspaceAccessContexts",
                        principalColumns: new[] { "Id", "UserId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrderResponsibilities_WorkspaceMemberships_WorkspaceMem~",
                        columns: x => new { x.WorkspaceMembershipId, x.PortfolioId },
                        principalTable: "WorkspaceMemberships",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrderStatusEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    WorkOrderId = table.Column<int>(type: "integer", nullable: false),
                    FromStatus = table.Column<int>(type: "integer", nullable: true),
                    ToStatus = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ChangedByUserId = table.Column<int>(type: "integer", nullable: true),
                    ChangedByLabel = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrderStatusEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkOrderStatusEvents_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExternalListingSignals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    ListingPublicationId = table.Column<int>(type: "integer", nullable: false),
                    ProviderMessageKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SignalType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SuggestedExternalListingId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SuggestedListingUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SuggestedExternalStatus = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Disposition = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConfirmedByUserId = table.Column<int>(type: "integer", nullable: true),
                    ConfirmedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalListingSignals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalListingSignals_ListingPublications_ListingPublicati~",
                        columns: x => new { x.ListingPublicationId, x.PortfolioId },
                        principalTable: "ListingPublications",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EvictionCaseEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    EvictionCaseId = table.Column<int>(type: "integer", nullable: false),
                    EventType = table.Column<int>(type: "integer", nullable: false),
                    EventDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvictionCaseEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvictionCaseEvents_EvictionCases_EvictionCaseId",
                        column: x => x.EvictionCaseId,
                        principalTable: "EvictionCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EvictionCaseEvents_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EvictionCaseRespondents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    EvictionCaseId = table.Column<int>(type: "integer", nullable: false),
                    LeaseManagementPartyId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvictionCaseRespondents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvictionCaseRespondents_EvictionCases_EvictionCaseId",
                        column: x => x.EvictionCaseId,
                        principalTable: "EvictionCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EvictionCaseRespondents_LeaseManagementParties_LeaseManagem~",
                        column: x => x.LeaseManagementPartyId,
                        principalTable: "LeaseManagementParties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvictionCaseRespondents_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InspectionItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    InspectionId = table.Column<int>(type: "integer", nullable: false),
                    Area = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Label = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Result = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    PhotoStoredFileId = table.Column<int>(type: "integer", nullable: true),
                    SpawnedWorkOrderId = table.Column<int>(type: "integer", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InspectionItems_Inspections_InspectionId",
                        column: x => x.InspectionId,
                        principalTable: "Inspections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InspectionItems_StoredFiles_PhotoStoredFileId",
                        column: x => x.PhotoStoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_InspectionItems_WorkOrders_SpawnedWorkOrderId",
                        column: x => x.SpawnedWorkOrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "LeaseAddendumFinancialEffects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    LeaseAddendumId = table.Column<int>(type: "integer", nullable: false),
                    EffectType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ChargeCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EffectiveFromOn = table.Column<DateOnly>(type: "date", nullable: true),
                    EffectiveThroughOn = table.Column<DateOnly>(type: "date", nullable: true),
                    DueOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaseAddendumFinancialEffects", x => x.Id);
                    table.UniqueConstraint("AK_LeaseAddendumFinancialEffects_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.CheckConstraint("CK_LeaseAddendumFinancialEffect_Currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("CK_LeaseAddendumFinancialEffect_EffectiveDates", "\"EffectiveThroughOn\" IS NULL OR \"EffectiveThroughOn\" >= \"EffectiveFromOn\"");
                    table.CheckConstraint("CK_LeaseAddendumFinancialEffect_Shape", "(\"EffectType\" = 'RecurringRentDelta' AND \"Amount\" <> 0 AND \"EffectiveFromOn\" IS NOT NULL AND \"DueOn\" IS NULL) OR (\"EffectType\" = 'OneTimeCharge' AND \"Amount\" > 0 AND \"DueOn\" IS NOT NULL AND \"EffectiveFromOn\" IS NULL AND \"EffectiveThroughOn\" IS NULL) OR (\"EffectType\" = 'DepositObligationDelta' AND \"Amount\" <> 0 AND \"EffectiveFromOn\" IS NOT NULL AND \"DueOn\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_LeaseAddendumFinancialEffects_LeaseAddenda_LeaseAddendumId_~",
                        columns: x => new { x.LeaseAddendumId, x.PortfolioId },
                        principalTable: "LeaseAddenda",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAddendumFinancialEffects_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LeaseAddendumSigners",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    LeaseAddendumId = table.Column<int>(type: "integer", nullable: false),
                    LeaseManagementPartyId = table.Column<int>(type: "integer", nullable: true),
                    TenantId = table.Column<int>(type: "integer", nullable: true),
                    SignerRole = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    NameSnapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EmailSnapshot = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    SigningOrder = table.Column<short>(type: "smallint", nullable: false),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaseAddendumSigners", x => x.Id);
                    table.UniqueConstraint("AK_LeaseAddendumSigners_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.CheckConstraint("CK_LeaseAddendumSigner_Order", "\"SigningOrder\" > 0");
                    table.CheckConstraint("CK_LeaseAddendumSigner_Role", "\"SignerRole\" IN ('PrimaryTenant', 'CoTenant', 'Guarantor', 'Manager', 'Owner', 'Other')");
                    table.ForeignKey(
                        name: "FK_LeaseAddendumSigners_LeaseAddenda_LeaseAddendumId_Portfolio~",
                        columns: x => new { x.LeaseAddendumId, x.PortfolioId },
                        principalTable: "LeaseAddenda",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAddendumSigners_LeaseManagementParties_LeaseManagement~",
                        columns: x => new { x.LeaseManagementPartyId, x.PortfolioId },
                        principalTable: "LeaseManagementParties",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAddendumSigners_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseAddendumSigners_Tenants_TenantId_PortfolioId",
                        columns: x => new { x.TenantId, x.PortfolioId },
                        principalTable: "Tenants",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LeaseRenewalAddendumDecisions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    LeaseManagementId = table.Column<int>(type: "integer", nullable: false),
                    RenewalAgreementId = table.Column<int>(type: "integer", nullable: false),
                    SourceAddendumSeriesPublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    Decision = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ReplacementAddendumId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaseRenewalAddendumDecisions", x => x.Id);
                    table.UniqueConstraint("AK_LeaseRenewalAddendumDecisions_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.CheckConstraint("CK_LeaseRenewalAddendumDecision_Replacement", "(\"Decision\" = 'ReissueAsAddendum' AND \"ReplacementAddendumId\" IS NOT NULL) OR (\"Decision\" IN ('End', 'IncorporateIntoBase') AND \"ReplacementAddendumId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_LeaseRenewalAddendumDecisions_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseRenewalAddendumDecisions_LeaseAddenda_ReplacementAdden~",
                        columns: x => new { x.ReplacementAddendumId, x.LeaseManagementId, x.PortfolioId },
                        principalTable: "LeaseAddenda",
                        principalColumns: new[] { "Id", "LeaseManagementId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseRenewalAddendumDecisions_LeaseAgreements_RenewalAgreem~",
                        columns: x => new { x.RenewalAgreementId, x.LeaseManagementId, x.PortfolioId },
                        principalTable: "LeaseAgreements",
                        principalColumns: new[] { "Id", "LeaseManagementId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseRenewalAddendumDecisions_LeaseManagements_LeaseManagem~",
                        columns: x => new { x.LeaseManagementId, x.PortfolioId },
                        principalTable: "LeaseManagements",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseRenewalAddendumDecisions_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SignatureRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    LeaseAgreementId = table.Column<int>(type: "integer", nullable: true),
                    LeaseAddendumId = table.Column<int>(type: "integer", nullable: true),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ProviderEnvelopeId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    IssuedArtifactId = table.Column<int>(type: "integer", nullable: false),
                    ExecutedArtifactId = table.Column<int>(type: "integer", nullable: true),
                    PreparedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProviderAcceptedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeclinedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VoidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ExecutionClaimOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ExecutionClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    ExecutionClaimExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExecutionAttemptCount = table.Column<int>(type: "integer", nullable: false),
                    ExecutionLastAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignatureRequests", x => x.Id);
                    table.UniqueConstraint("AK_SignatureRequests_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.CheckConstraint("CK_SignatureRequest_Claim", "(\"ExecutionClaimOwner\" IS NULL AND \"ExecutionClaimToken\" IS NULL AND \"ExecutionClaimExpiresAtUtc\" IS NULL) OR (\"ExecutionClaimOwner\" IS NOT NULL AND \"ExecutionClaimToken\" IS NOT NULL AND \"ExecutionClaimExpiresAtUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_SignatureRequest_Execution", "(\"ExecutedArtifactId\" IS NULL AND \"CompletedAtUtc\" IS NULL) OR (\"ExecutedArtifactId\" IS NOT NULL AND \"CompletedAtUtc\" IS NOT NULL AND \"Status\" = 'Completed')");
                    table.CheckConstraint("CK_SignatureRequest_Parent", "(\"LeaseAgreementId\" IS NULL) <> (\"LeaseAddendumId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_SignatureRequests_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SignatureRequests_LeaseAddenda_LeaseAddendumId_PortfolioId",
                        columns: x => new { x.LeaseAddendumId, x.PortfolioId },
                        principalTable: "LeaseAddenda",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SignatureRequests_LeaseAgreements_LeaseAgreementId_Portfoli~",
                        columns: x => new { x.LeaseAgreementId, x.PortfolioId },
                        principalTable: "LeaseAgreements",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SignatureRequests_LegalDocumentArtifacts_ExecutedArtifactId~",
                        columns: x => new { x.ExecutedArtifactId, x.PortfolioId },
                        principalTable: "LegalDocumentArtifacts",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SignatureRequests_LegalDocumentArtifacts_IssuedArtifactId_P~",
                        columns: x => new { x.IssuedArtifactId, x.PortfolioId },
                        principalTable: "LegalDocumentArtifacts",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SignatureRequests_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApplicantScreeningMilestones",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    ApplicantScreeningId = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    DeliveryId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EventType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicantScreeningMilestones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApplicantScreeningMilestones_ApplicantScreenings_ApplicantS~",
                        columns: x => new { x.ApplicantScreeningId, x.PortfolioId },
                        principalTable: "ApplicantScreenings",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationFinancialEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    ApplicationFinancialAccountId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: true),
                    UnitId = table.Column<int>(type: "integer", nullable: true),
                    EntryType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    EffectiveOn = table.Column<DateOnly>(type: "date", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Method = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ProviderReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Source = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SourceReference = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RelatedEntryId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationFinancialEntries", x => x.Id);
                    table.UniqueConstraint("AK_ApplicationFinancialEntries_Id_ApplicationFinancialAccountI~", x => new { x.Id, x.ApplicationFinancialAccountId, x.PortfolioId });
                    table.CheckConstraint("CK_ApplicationFinancialEntry_Amount", "\"Amount\" > 0");
                    table.CheckConstraint("CK_ApplicationFinancialEntry_Currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("CK_ApplicationFinancialEntry_TypeDirection", "(\"EntryType\" = 'FeeCollection' AND \"Direction\" = 'Increase' AND \"RelatedEntryId\" IS NULL) OR (\"EntryType\" = 'Refund' AND \"Direction\" = 'Decrease' AND \"RelatedEntryId\" IS NOT NULL) OR (\"EntryType\" = 'Adjustment')");
                    table.ForeignKey(
                        name: "FK_ApplicationFinancialEntries_ApplicationFinancialAccounts_Ap~",
                        columns: x => new { x.ApplicationFinancialAccountId, x.PortfolioId },
                        principalTable: "ApplicationFinancialAccounts",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApplicationFinancialEntries_ApplicationFinancialEntries_Rel~",
                        columns: x => new { x.RelatedEntryId, x.ApplicationFinancialAccountId, x.PortfolioId },
                        principalTable: "ApplicationFinancialEntries",
                        principalColumns: new[] { "Id", "ApplicationFinancialAccountId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApplicationFinancialEntries_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApplicationFinancialEntries_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApplicationFinancialEntries_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApplicationFinancialEntries_Units_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    TenantAccountId = table.Column<int>(type: "integer", nullable: false),
                    EntryType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Direction = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    EffectiveOn = table.Column<DateOnly>(type: "date", nullable: false),
                    DueOn = table.Column<DateOnly>(type: "date", nullable: true),
                    PostedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    BusinessKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TransferPublicId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseAgreementId = table.Column<int>(type: "integer", nullable: true),
                    LeaseAddendumId = table.Column<int>(type: "integer", nullable: true),
                    ReversesEntryId = table.Column<long>(type: "bigint", nullable: true),
                    ProviderPaymentAttemptId = table.Column<long>(type: "bigint", nullable: true),
                    SourceStoredFileId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantLedgerEntries", x => x.Id);
                    table.UniqueConstraint("AK_TenantLedgerEntries_Id_PortfolioId", x => new { x.Id, x.PortfolioId });
                    table.UniqueConstraint("AK_TenantLedgerEntries_Id_TenantAccountId_PortfolioId", x => new { x.Id, x.TenantAccountId, x.PortfolioId });
                    table.CheckConstraint("CK_TenantLedgerEntry_Amount", "\"Amount\" > 0");
                    table.CheckConstraint("CK_TenantLedgerEntry_Currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("CK_TenantLedgerEntry_Direction", "\"Direction\" IN ('Debit','Credit') AND ((\"EntryType\" = 'OpeningBalance') OR (\"EntryType\" IN ('RentCharge','AddendumCharge','LateFeeCharge','DepositCharge','ManualCharge') AND \"Direction\" = 'Debit') OR (\"EntryType\" IN ('PaymentReceipt','Credit') AND \"Direction\" = 'Credit') OR (\"EntryType\" = 'Refund' AND \"Direction\" = 'Debit') OR (\"EntryType\" IN ('Adjustment','TransferIn','TransferOut','Reversal') AND \"Direction\" IN ('Debit','Credit')))");
                    table.CheckConstraint("CK_TenantLedgerEntry_DueDate", "(\"EntryType\" IN ('RentCharge','AddendumCharge','LateFeeCharge','DepositCharge','ManualCharge') AND \"DueOn\" IS NOT NULL) OR (\"EntryType\" IN ('PaymentReceipt','Credit','Refund','Reversal') AND \"DueOn\" IS NULL) OR (\"EntryType\" IN ('OpeningBalance','Adjustment','TransferIn','TransferOut'))");
                    table.CheckConstraint("CK_TenantLedgerEntry_Provenance", "(\"EntryType\" <> 'RentCharge' OR \"LeaseAgreementId\" IS NOT NULL) AND (\"EntryType\" <> 'AddendumCharge' OR \"LeaseAddendumId\" IS NOT NULL)");
                    table.CheckConstraint("CK_TenantLedgerEntry_ReversalReference", "(\"EntryType\" = 'Reversal') = (\"ReversesEntryId\" IS NOT NULL)");
                    table.CheckConstraint("CK_TenantLedgerEntry_TransferProvenance", "(\"EntryType\" IN ('TransferIn','TransferOut')) = (\"TransferPublicId\" IS NOT NULL)");
                    table.CheckConstraint("CK_TenantLedgerEntry_Type", "\"EntryType\" IN ('OpeningBalance', 'RentCharge', 'AddendumCharge', 'LateFeeCharge', 'DepositCharge', 'ManualCharge', 'PaymentReceipt', 'Credit', 'Adjustment', 'Refund', 'TransferIn', 'TransferOut', 'Reversal')");
                    table.ForeignKey(
                        name: "FK_TenantLedgerEntries_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantLedgerEntries_LeaseAddenda_LeaseAddendumId_PortfolioId",
                        columns: x => new { x.LeaseAddendumId, x.PortfolioId },
                        principalTable: "LeaseAddenda",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantLedgerEntries_LeaseAgreements_LeaseAgreementId_Portfo~",
                        columns: x => new { x.LeaseAgreementId, x.PortfolioId },
                        principalTable: "LeaseAgreements",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantLedgerEntries_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantLedgerEntries_StoredFiles_SourceStoredFileId_Portfoli~",
                        columns: x => new { x.SourceStoredFileId, x.PortfolioId },
                        principalTable: "StoredFiles",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantLedgerEntries_TenantAccounts_TenantAccountId_Portfoli~",
                        columns: x => new { x.TenantAccountId, x.PortfolioId },
                        principalTable: "TenantAccounts",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantLedgerEntries_TenantLedgerEntries_ReversesEntryId_Ten~",
                        columns: x => new { x.ReversesEntryId, x.TenantAccountId, x.PortfolioId },
                        principalTable: "TenantLedgerEntries",
                        principalColumns: new[] { "Id", "TenantAccountId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantLedgerEntries_TenantPaymentAttempts_ProviderPaymentAt~",
                        columns: x => new { x.ProviderPaymentAttemptId, x.TenantAccountId, x.PortfolioId },
                        principalTable: "TenantPaymentAttempts",
                        principalColumns: new[] { "Id", "TenantAccountId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConversationMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConversationId = table.Column<int>(type: "integer", nullable: false),
                    SenderRole = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Body = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Channels = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationMessages_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TechnicianWorkEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    WorkOrderId = table.Column<int>(type: "integer", nullable: false),
                    WorkOrderResponsibilityId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceMembershipId = table.Column<int>(type: "integer", nullable: false),
                    MembershipRoleAssignmentId = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: true),
                    Unit = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    StoredFileId = table.Column<int>(type: "integer", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TechnicianWorkEntries", x => x.Id);
                    table.CheckConstraint("CK_TechnicianWorkEntries_KindFacts", "(\"Kind\" = 'Note' AND \"Note\" IS NOT NULL AND \"Quantity\" IS NULL AND \"StoredFileId\" IS NULL) OR (\"Kind\" IN ('Time', 'Material') AND \"Quantity\" > 0 AND \"Unit\" IS NOT NULL AND \"StoredFileId\" IS NULL) OR (\"Kind\" = 'Photo' AND \"StoredFileId\" IS NOT NULL AND \"Quantity\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_TechnicianWorkEntries_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TechnicianWorkEntries_MembershipRoleAssignments_MembershipR~",
                        columns: x => new { x.MembershipRoleAssignmentId, x.WorkspaceMembershipId, x.PortfolioId },
                        principalTable: "MembershipRoleAssignments",
                        principalColumns: new[] { "Id", "WorkspaceMembershipId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TechnicianWorkEntries_StoredFiles_StoredFileId",
                        column: x => x.StoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TechnicianWorkEntries_WorkOrderResponsibilities_WorkOrderRe~",
                        column: x => x.WorkOrderResponsibilityId,
                        principalTable: "WorkOrderResponsibilities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TechnicianWorkEntries_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TechnicianWorkEntries_WorkspaceMemberships_WorkspaceMembers~",
                        columns: x => new { x.WorkspaceMembershipId, x.PortfolioId },
                        principalTable: "WorkspaceMemberships",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SignatureSigners",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    SignatureRequestId = table.Column<int>(type: "integer", nullable: false),
                    AgreementSignerId = table.Column<int>(type: "integer", nullable: true),
                    AddendumSignerId = table.Column<int>(type: "integer", nullable: true),
                    NameSnapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EmailSnapshot = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    SigningOrder = table.Column<short>(type: "smallint", nullable: false),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    TokenHash = table.Column<string>(type: "char(64)", nullable: false),
                    TokenExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ConsentGivenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ViewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SignedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeclinedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SignatureType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    TypedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DrawnSignatureStoredFileId = table.Column<int>(type: "integer", nullable: true),
                    IpAddress = table.Column<IPAddress>(type: "inet", nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignatureSigners", x => x.Id);
                    table.UniqueConstraint("AK_SignatureSigners_Id_SignatureRequestId_PortfolioId", x => new { x.Id, x.SignatureRequestId, x.PortfolioId });
                    table.ForeignKey(
                        name: "FK_SignatureSigners_LeaseAddendumSigners_AddendumSignerId_Port~",
                        columns: x => new { x.AddendumSignerId, x.PortfolioId },
                        principalTable: "LeaseAddendumSigners",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SignatureSigners_LeaseAgreementSigners_AgreementSignerId_Po~",
                        columns: x => new { x.AgreementSignerId, x.PortfolioId },
                        principalTable: "LeaseAgreementSigners",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SignatureSigners_SignatureRequests_SignatureRequestId_Portf~",
                        columns: x => new { x.SignatureRequestId, x.PortfolioId },
                        principalTable: "SignatureRequests",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SignatureSigners_StoredFiles_DrawnSignatureStoredFileId_Por~",
                        columns: x => new { x.DrawnSignatureStoredFileId, x.PortfolioId },
                        principalTable: "StoredFiles",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NoticeDrafts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    LeaseManagementId = table.Column<int>(type: "integer", nullable: false),
                    TenantAccountId = table.Column<int>(type: "integer", nullable: false),
                    RecipientLeaseManagementPartyId = table.Column<int>(type: "integer", nullable: false),
                    LeaseAgreementId = table.Column<int>(type: "integer", nullable: true),
                    LeaseAddendumId = table.Column<int>(type: "integer", nullable: true),
                    TenantLedgerEntryId = table.Column<long>(type: "bigint", nullable: true),
                    PropertyId = table.Column<int>(type: "integer", nullable: true),
                    NoticeType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    GenerationPrompt = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    TriggerDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConversationId = table.Column<int>(type: "integer", nullable: true),
                    TenantNoticePolicyId = table.Column<int>(type: "integer", nullable: true),
                    WorkspaceNoticeTemplateVersionId = table.Column<int>(type: "integer", nullable: true),
                    RenderedNoticeId = table.Column<long>(type: "bigint", nullable: true),
                    ApprovedChannels = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DismissedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoticeDrafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NoticeDrafts_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_NoticeDrafts_LeaseAddenda_LeaseAddendumId_LeaseManagementId~",
                        columns: x => new { x.LeaseAddendumId, x.LeaseManagementId, x.PortfolioId },
                        principalTable: "LeaseAddenda",
                        principalColumns: new[] { "Id", "LeaseManagementId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NoticeDrafts_LeaseAgreements_LeaseAgreementId_LeaseManageme~",
                        columns: x => new { x.LeaseAgreementId, x.LeaseManagementId, x.PortfolioId },
                        principalTable: "LeaseAgreements",
                        principalColumns: new[] { "Id", "LeaseManagementId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NoticeDrafts_LeaseManagementParties_RecipientLeaseManagemen~",
                        columns: x => new { x.RecipientLeaseManagementPartyId, x.LeaseManagementId, x.PortfolioId },
                        principalTable: "LeaseManagementParties",
                        principalColumns: new[] { "Id", "LeaseManagementId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NoticeDrafts_LeaseManagements_LeaseManagementId_PortfolioId",
                        columns: x => new { x.LeaseManagementId, x.PortfolioId },
                        principalTable: "LeaseManagements",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NoticeDrafts_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NoticeDrafts_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_NoticeDrafts_TenantAccounts_TenantAccountId_LeaseManagement~",
                        columns: x => new { x.TenantAccountId, x.LeaseManagementId, x.PortfolioId },
                        principalTable: "TenantAccounts",
                        principalColumns: new[] { "Id", "LeaseManagementId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NoticeDrafts_TenantLedgerEntries_TenantLedgerEntryId_Tenant~",
                        columns: x => new { x.TenantLedgerEntryId, x.TenantAccountId, x.PortfolioId },
                        principalTable: "TenantLedgerEntries",
                        principalColumns: new[] { "Id", "TenantAccountId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ScanDrafts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    BatchId = table.Column<int>(type: "integer", nullable: true),
                    FilePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    SourceStoredFileId = table.Column<int>(type: "integer", nullable: true),
                    SourceContentSha256 = table.Column<string>(type: "char(64)", nullable: true),
                    SourceLabel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CaptureExperience = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    CaptureAccessContextId = table.Column<int>(type: "integer", nullable: true),
                    CaptureAccessRevision = table.Column<long>(type: "bigint", nullable: true),
                    CapturePropertyId = table.Column<int>(type: "integer", nullable: true),
                    CaptureUnitId = table.Column<int>(type: "integer", nullable: true),
                    CaptureLeaseManagementId = table.Column<int>(type: "integer", nullable: true),
                    CaptureLeaseAgreementId = table.Column<int>(type: "integer", nullable: true),
                    CaptureTenantAccountId = table.Column<int>(type: "integer", nullable: true),
                    CaptureTenantLedgerEntryId = table.Column<long>(type: "bigint", nullable: true),
                    CaptureWorkOrderId = table.Column<int>(type: "integer", nullable: true),
                    CaptureApplicationId = table.Column<int>(type: "integer", nullable: true),
                    CaptureRentalListingId = table.Column<int>(type: "integer", nullable: true),
                    ThumbnailPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    TargetEntityType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ProcessingClaimOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ProcessingClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    ProcessingClaimExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProcessingAttemptCount = table.Column<int>(type: "integer", nullable: false),
                    ProcessingLastAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExtractedFields = table.Column<string>(type: "jsonb", nullable: true),
                    ModelId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    TokensUsed = table.Column<int>(type: "integer", nullable: true),
                    CostUsd = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmedEntityId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanDrafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScanDrafts_LeaseAgreements_CaptureLeaseAgreementId",
                        column: x => x.CaptureLeaseAgreementId,
                        principalTable: "LeaseAgreements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScanDrafts_LeaseManagements_CaptureLeaseManagementId",
                        column: x => x.CaptureLeaseManagementId,
                        principalTable: "LeaseManagements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScanDrafts_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScanDrafts_Properties_CapturePropertyId",
                        column: x => x.CapturePropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScanDrafts_RentalApplications_CaptureApplicationId",
                        column: x => x.CaptureApplicationId,
                        principalTable: "RentalApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScanDrafts_RentalListings_CaptureRentalListingId",
                        column: x => x.CaptureRentalListingId,
                        principalTable: "RentalListings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScanDrafts_ScanBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "ScanBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ScanDrafts_StoredFiles_SourceStoredFileId",
                        column: x => x.SourceStoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScanDrafts_TenantAccounts_CaptureTenantAccountId",
                        column: x => x.CaptureTenantAccountId,
                        principalTable: "TenantAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScanDrafts_TenantLedgerEntries_CaptureTenantLedgerEntryId",
                        column: x => x.CaptureTenantLedgerEntryId,
                        principalTable: "TenantLedgerEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScanDrafts_Units_CaptureUnitId",
                        column: x => x.CaptureUnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScanDrafts_WorkOrders_CaptureWorkOrderId",
                        column: x => x.CaptureWorkOrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SecurityDepositEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    SecurityDepositAccountId = table.Column<int>(type: "integer", nullable: false),
                    EntryType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Direction = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    EffectiveOn = table.Column<DateOnly>(type: "date", nullable: false),
                    PostedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    BusinessKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TransferPublicId = table.Column<Guid>(type: "uuid", nullable: true),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    LeaseAgreementId = table.Column<int>(type: "integer", nullable: true),
                    LeaseAddendumId = table.Column<int>(type: "integer", nullable: true),
                    ReversesEntryId = table.Column<long>(type: "bigint", nullable: true),
                    TenantLedgerEntryId = table.Column<long>(type: "bigint", nullable: true),
                    SourceStoredFileId = table.Column<int>(type: "integer", nullable: true),
                    PayoutExternalReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityDepositEntries", x => x.Id);
                    table.UniqueConstraint("AK_SecurityDepositEntries_Id_SecurityDepositAccountId_Portfoli~", x => new { x.Id, x.SecurityDepositAccountId, x.PortfolioId });
                    table.CheckConstraint("CK_SecurityDepositEntry_Amount", "\"Amount\" > 0");
                    table.CheckConstraint("CK_SecurityDepositEntry_Currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("CK_SecurityDepositEntry_Direction", "(\"EntryType\" IN ('Receipt','TransferIn') AND \"Direction\" = 'Increase') OR (\"EntryType\" IN ('Deduction','Refund','TransferOut') AND \"Direction\" = 'Decrease') OR (\"EntryType\" IN ('Adjustment','Reversal') AND \"Direction\" IN ('Increase','Decrease'))");
                    table.CheckConstraint("CK_SecurityDepositEntry_PayoutProvenance", "\"PayoutExternalReference\" IS NULL OR \"EntryType\" = 'Refund'");
                    table.CheckConstraint("CK_SecurityDepositEntry_ReversalReference", "(\"EntryType\" = 'Reversal') = (\"ReversesEntryId\" IS NOT NULL)");
                    table.CheckConstraint("CK_SecurityDepositEntry_TransferProvenance", "(\"EntryType\" IN ('TransferIn','TransferOut')) = (\"TransferPublicId\" IS NOT NULL)");
                    table.CheckConstraint("CK_SecurityDepositEntry_Type", "\"EntryType\" IN ('Receipt', 'Deduction', 'Refund', 'TransferIn', 'TransferOut', 'Adjustment', 'Reversal')");
                    table.ForeignKey(
                        name: "FK_SecurityDepositEntries_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SecurityDepositEntries_LeaseAddenda_LeaseAddendumId_Portfol~",
                        columns: x => new { x.LeaseAddendumId, x.PortfolioId },
                        principalTable: "LeaseAddenda",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SecurityDepositEntries_LeaseAgreements_LeaseAgreementId_Por~",
                        columns: x => new { x.LeaseAgreementId, x.PortfolioId },
                        principalTable: "LeaseAgreements",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SecurityDepositEntries_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SecurityDepositEntries_SecurityDepositAccounts_SecurityDepo~",
                        columns: x => new { x.SecurityDepositAccountId, x.PortfolioId },
                        principalTable: "SecurityDepositAccounts",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SecurityDepositEntries_SecurityDepositEntries_ReversesEntry~",
                        columns: x => new { x.ReversesEntryId, x.SecurityDepositAccountId, x.PortfolioId },
                        principalTable: "SecurityDepositEntries",
                        principalColumns: new[] { "Id", "SecurityDepositAccountId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SecurityDepositEntries_StoredFiles_SourceStoredFileId_Portf~",
                        columns: x => new { x.SourceStoredFileId, x.PortfolioId },
                        principalTable: "StoredFiles",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SecurityDepositEntries_TenantLedgerEntries_TenantLedgerEntr~",
                        columns: x => new { x.TenantLedgerEntryId, x.PortfolioId },
                        principalTable: "TenantLedgerEntries",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantLedgerAllocations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    TenantAccountId = table.Column<int>(type: "integer", nullable: false),
                    DebitEntryId = table.Column<long>(type: "bigint", nullable: false),
                    CreditEntryId = table.Column<long>(type: "bigint", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ReversesAllocationId = table.Column<long>(type: "bigint", nullable: true),
                    AllocatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    BusinessKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantLedgerAllocations", x => x.Id);
                    table.UniqueConstraint("AK_TenantLedgerAllocations_Id_TenantAccountId_PortfolioId", x => new { x.Id, x.TenantAccountId, x.PortfolioId });
                    table.CheckConstraint("CK_TenantLedgerAllocation_Amount", "(\"ReversesAllocationId\" IS NULL AND \"Amount\" > 0) OR (\"ReversesAllocationId\" IS NOT NULL AND \"Amount\" < 0)");
                    table.CheckConstraint("CK_TenantLedgerAllocation_DistinctEntries", "\"DebitEntryId\" <> \"CreditEntryId\"");
                    table.ForeignKey(
                        name: "FK_TenantLedgerAllocations_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantLedgerAllocations_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantLedgerAllocations_TenantAccounts_TenantAccountId_Port~",
                        columns: x => new { x.TenantAccountId, x.PortfolioId },
                        principalTable: "TenantAccounts",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantLedgerAllocations_TenantLedgerAllocations_ReversesAll~",
                        columns: x => new { x.ReversesAllocationId, x.TenantAccountId, x.PortfolioId },
                        principalTable: "TenantLedgerAllocations",
                        principalColumns: new[] { "Id", "TenantAccountId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantLedgerAllocations_TenantLedgerEntries_CreditEntryId_T~",
                        columns: x => new { x.CreditEntryId, x.TenantAccountId, x.PortfolioId },
                        principalTable: "TenantLedgerEntries",
                        principalColumns: new[] { "Id", "TenantAccountId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantLedgerAllocations_TenantLedgerEntries_DebitEntryId_Te~",
                        columns: x => new { x.DebitEntryId, x.TenantAccountId, x.PortfolioId },
                        principalTable: "TenantLedgerEntries",
                        principalColumns: new[] { "Id", "TenantAccountId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NoticeDeliveryEvidence",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    RenderedNoticeId = table.Column<long>(type: "bigint", nullable: false),
                    RecipientLeaseManagementPartyId = table.Column<int>(type: "integer", nullable: false),
                    RecipientRole = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Channel = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Destination = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    OutboxMessageId = table.Column<long>(type: "bigint", nullable: false),
                    ConversationMessageId = table.Column<int>(type: "integer", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoticeDeliveryEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NoticeDeliveryEvidence_ConversationMessages_ConversationMes~",
                        column: x => x.ConversationMessageId,
                        principalTable: "ConversationMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NoticeDeliveryEvidence_LeaseManagementParties_RecipientLeas~",
                        columns: x => new { x.RecipientLeaseManagementPartyId, x.PortfolioId },
                        principalTable: "LeaseManagementParties",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NoticeDeliveryEvidence_OutboxMessages_OutboxMessageId",
                        column: x => x.OutboxMessageId,
                        principalTable: "OutboxMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NoticeDeliveryEvidence_RenderedNotices_RenderedNoticeId_Por~",
                        columns: x => new { x.RenderedNoticeId, x.PortfolioId },
                        principalTable: "RenderedNotices",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SignatureAuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    SignatureRequestId = table.Column<int>(type: "integer", nullable: false),
                    SignatureSignerId = table.Column<int>(type: "integer", nullable: true),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IpAddress = table.Column<IPAddress>(type: "inet", nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignatureAuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SignatureAuditEvents_SignatureRequests_SignatureRequestId_P~",
                        columns: x => new { x.SignatureRequestId, x.PortfolioId },
                        principalTable: "SignatureRequests",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SignatureAuditEvents_SignatureSigners_SignatureSignerId_Sig~",
                        columns: x => new { x.SignatureSignerId, x.SignatureRequestId, x.PortfolioId },
                        principalTable: "SignatureSigners",
                        principalColumns: new[] { "Id", "SignatureRequestId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BankTransactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    BankConnectionId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: true),
                    ProviderTransactionId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PostedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AuthorizedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    MerchantName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IsoCurrencyCode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Category = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MatchedTenantAccountId = table.Column<int>(type: "integer", nullable: true),
                    MatchedTenantLedgerEntryId = table.Column<long>(type: "bigint", nullable: true),
                    MatchedExpenseId = table.Column<int>(type: "integer", nullable: true),
                    MatchStatus = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    MatchConfidence = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RawData = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankTransactions_BankConnections_BankConnectionId",
                        column: x => x.BankConnectionId,
                        principalTable: "BankConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BankTransactions_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BankTransactions_Properties_PropertyId_PortfolioId",
                        columns: x => new { x.PropertyId, x.PortfolioId },
                        principalTable: "Properties",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankTransactions_TenantAccounts_MatchedTenantAccountId_Port~",
                        columns: x => new { x.MatchedTenantAccountId, x.PortfolioId },
                        principalTable: "TenantAccounts",
                        principalColumns: new[] { "Id", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankTransactions_TenantLedgerEntries_MatchedTenantLedgerEnt~",
                        columns: x => new { x.MatchedTenantLedgerEntryId, x.MatchedTenantAccountId, x.PortfolioId },
                        principalTable: "TenantLedgerEntries",
                        principalColumns: new[] { "Id", "TenantAccountId", "PortfolioId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CapitalAssets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: false),
                    UnitId = table.Column<int>(type: "integer", nullable: true),
                    SourceExpenseId = table.Column<int>(type: "integer", nullable: true),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CostBasis = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    InServiceDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Method = table.Column<int>(type: "integer", nullable: false),
                    RecoveryYears = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    Convention = table.Column<int>(type: "integer", nullable: false),
                    AccumulatedDepreciation = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DisposedOnDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CapitalAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CapitalAssets_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CapitalAssets_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CapitalAssets_Units_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Expenses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: true),
                    UnitId = table.Column<int>(type: "integer", nullable: true),
                    VendorId = table.Column<int>(type: "integer", nullable: true),
                    WorkOrderId = table.Column<int>(type: "integer", nullable: true),
                    CapitalizedAssetId = table.Column<int>(type: "integer", nullable: true),
                    RecurringExpenseId = table.Column<int>(type: "integer", nullable: true),
                    RecurringExpenseOccurrenceDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IncurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BillableToOwner = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Subtotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    ReceiptData = table.Column<string>(type: "jsonb", nullable: true),
                    PaymentMethod = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CardLast4 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DocumentKind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Expenses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Expenses_CapitalAssets_CapitalizedAssetId",
                        column: x => x.CapitalizedAssetId,
                        principalTable: "CapitalAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Expenses_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Expenses_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Expenses_RecurringExpenses_RecurringExpenseId",
                        column: x => x.RecurringExpenseId,
                        principalTable: "RecurringExpenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Expenses_Units_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Expenses_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Expenses_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ExpenseLineItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExpenseId = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    LineNumber = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseLineItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExpenseLineItems_Expenses_ExpenseId",
                        column: x => x.ExpenseId,
                        principalTable: "Expenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "CapabilityDefinitions",
                columns: new[] { "Id", "AuthorizationTargetKind", "Description", "Key" },
                values: new object[,]
                {
                    { 1, "Property", "Read in-scope management rental records.", "rentals.read" },
                    { 2, "Property", "Manage in-scope rental operations.", "rentals.manage" },
                    { 3, "Property", "Read in-scope work operations.", "work.read" },
                    { 4, "Property", "Manage in-scope work operations.", "work.manage" },
                    { 5, "Property", "Read in-scope operational reports.", "reports.read" },
                    { 6, "Property", "Read in-scope balances.", "money.balances.read" },
                    { 7, "Property", "Manage in-scope charges.", "money.charges.manage" },
                    { 8, "Property", "Manage in-scope payments.", "money.payments.manage" },
                    { 9, "Property", "Manage in-scope expenses.", "money.expenses.manage" },
                    { 10, "Property", "Manage in-scope deposits.", "money.deposits.manage" },
                    { 11, "Property", "Read in-scope owner reporting.", "money.owner-reports.read" },
                    { 12, "Property", "Perform non-destructive operational reconciliation in scope.", "money.reconciliation.operate" },
                    { 13, "Property", "Assign work or leasing responsibility to an existing in-scope member.", "responsibility.assign-existing-member" },
                    { 14, "Property", "Manage in-scope listings.", "leasing.listings.manage" },
                    { 15, "Property", "Manage in-scope applications.", "leasing.applications.manage" },
                    { 16, "Property", "Manage in-scope showings.", "leasing.showings.manage" },
                    { 17, "Property", "Prepare in-scope agreements without financial administration authority.", "leasing.agreements.prepare" },
                    { 18, "Property", "Manage in-scope leasing onboarding.", "leasing.onboarding.manage" },
                    { 19, "Property", "Read terms required for leasing work.", "leasing.terms.read" },
                    { 20, "Property", "Read deposit facts required for leasing work.", "leasing.deposits.read" },
                    { 21, "WorkOrder", "Read only work orders assigned to the member.", "maintenance.assigned-work.read" },
                    { 22, "WorkOrder", "Update only work orders assigned to the member.", "maintenance.assigned-work.update" },
                    { 23, "WorkOrder", "Use conversations attached to assigned work.", "maintenance.assigned-work.converse" },
                    { 24, "WorkOrder", "Record time and materials on assigned work.", "maintenance.assigned-work.time-materials.manage" },
                    { 25, "Workspace", "Read Team configuration.", "team.read" },
                    { 26, "Workspace", "Invite members and manage Team roles and scope.", "team.manage" },
                    { 27, "Workspace", "Manage workspace security settings.", "security.manage" },
                    { 28, "Workspace", "Manage subscription and billing settings.", "billing.manage" },
                    { 29, "Workspace", "Connect and administer external integrations and credentials.", "integrations.manage" },
                    { 30, "Workspace", "Export workspace data.", "data.export" },
                    { 31, "Workspace", "Connect, remove, or administer bank connections.", "bank-connections.manage" },
                    { 32, "Workspace", "Manage payout destinations and payouts.", "payouts.manage" },
                    { 33, "Workspace", "Initiate transfers and owner distributions.", "money.disbursements.manage" },
                    { 34, "Workspace", "Perform destructive reconciliation actions.", "money.reconciliation.destructive" },
                    { 35, "Workspace", "Perform destructive workspace or account actions.", "account.destructive-actions" },
                    { 36, "Property", "Collect in-scope application fees without refund authority.", "leasing.application-fees.collect" },
                    { 37, "Workspace", "Manage Team routing, tenant notice policy, delivery configuration, and notice templates.", "notifications.manage" },
                    { 38, "Property", "Manage tenant-notice drafts within assigned property scope.", "notifications.tenant-notices.manage" }
                });

            migrationBuilder.InsertData(
                table: "RoleProfiles",
                columns: new[] { "Id", "DefaultExperience", "DefaultScopeKind", "Description", "DisplayName", "Key" },
                values: new object[,]
                {
                    { 1, "Management", "AllProperties", "Full workspace authority, including Team, security, billing, integrations, banking, payouts, exports, and destructive actions.", "Workspace Administrator", "workspace-administrator" },
                    { 2, "Management", "SelectedProperties", "Daily rental, work, and operational money authority within independently assigned property scope.", "Property Manager", "property-manager" },
                    { 3, "Leasing", "SelectedProperties", "Listings, applications, showings, agreement preparation, and onboarding within assigned property scope.", "Leasing Agent", "leasing-agent" },
                    { 4, "Maintenance", "AssignedWorkOrders", "Assigned-work-only maintenance access with no general property or Unit browsing.", "Maintenance Technician", "maintenance-technician" }
                });

            migrationBuilder.InsertData(
                table: "SystemNoticeTemplateVersions",
                columns: new[] { "Id", "Body", "Classification", "JurisdictionCode", "Provenance", "PublishedAtUtc", "Subject", "SystemKey", "Version" },
                values: new object[,]
                {
                    { 1, "Hello {{tenant_name}}, this is a reminder that {{rent_amount}} is due on {{rent_due_date}} for {{property_address}}.", "Courtesy", null, "Rental Command supplied default v1", new DateTime(2026, 7, 13, 0, 0, 0, 0, DateTimeKind.Utc), "Upcoming rent reminder", "rent-reminder", 1 },
                    { 2, "Hello {{tenant_name}}, we would like to offer a renewal for {{property_address}} beginning {{renewal_start_date}}. Please review the attached terms.", "Operational", null, "Rental Command supplied default v1", new DateTime(2026, 7, 13, 0, 0, 0, 0, DateTimeKind.Utc), "Lease renewal offer", "lease-renewal-offer", 1 },
                    { 3, "Hello {{tenant_name}}, your current agreement ends {{lease_end_date}}. We are offering a month-to-month arrangement beginning the following day.", "Operational", null, "Rental Command supplied default v1", new DateTime(2026, 7, 13, 0, 0, 0, 0, DateTimeKind.Utc), "Month-to-month offer", "month-to-month-offer", 1 },
                    { 4, "Hello {{tenant_name}}, this notice concerns the agreement for {{property_address}}, which ends {{lease_end_date}}. Review the attached notice and contact management with questions.", "Legal", null, "Rental Command supplied default v1", new DateTime(2026, 7, 13, 0, 0, 0, 0, DateTimeKind.Utc), "Lease expiration and non-renewal notice", "lease-non-renewal", 1 },
                    { 5, "Hello {{tenant_name}}, our records show {{overdue_amount}} remains due for {{property_address}} as of {{today}}. This notice includes any applicable late fee described in your agreement.", "Legal", null, "Rental Command supplied default v1", new DateTime(2026, 7, 13, 0, 0, 0, 0, DateTimeKind.Utc), "Past-due rent notice", "late-rent-late-fee", 1 }
                });

            migrationBuilder.InsertData(
                table: "RoleProfileCapabilities",
                columns: new[] { "CapabilityDefinitionId", "RoleProfileId" },
                values: new object[,]
                {
                    { 1, 1 },
                    { 2, 1 },
                    { 3, 1 },
                    { 4, 1 },
                    { 5, 1 },
                    { 6, 1 },
                    { 7, 1 },
                    { 8, 1 },
                    { 9, 1 },
                    { 10, 1 },
                    { 11, 1 },
                    { 12, 1 },
                    { 13, 1 },
                    { 14, 1 },
                    { 15, 1 },
                    { 16, 1 },
                    { 17, 1 },
                    { 18, 1 },
                    { 19, 1 },
                    { 20, 1 },
                    { 21, 1 },
                    { 22, 1 },
                    { 23, 1 },
                    { 24, 1 },
                    { 25, 1 },
                    { 26, 1 },
                    { 27, 1 },
                    { 28, 1 },
                    { 29, 1 },
                    { 30, 1 },
                    { 31, 1 },
                    { 32, 1 },
                    { 33, 1 },
                    { 34, 1 },
                    { 35, 1 },
                    { 36, 1 },
                    { 37, 1 },
                    { 38, 1 },
                    { 1, 2 },
                    { 2, 2 },
                    { 3, 2 },
                    { 4, 2 },
                    { 5, 2 },
                    { 6, 2 },
                    { 7, 2 },
                    { 8, 2 },
                    { 9, 2 },
                    { 10, 2 },
                    { 11, 2 },
                    { 12, 2 },
                    { 13, 2 },
                    { 38, 2 },
                    { 1, 3 },
                    { 14, 3 },
                    { 15, 3 },
                    { 16, 3 },
                    { 17, 3 },
                    { 18, 3 },
                    { 19, 3 },
                    { 20, 3 },
                    { 36, 3 },
                    { 38, 3 },
                    { 21, 4 },
                    { 22, 4 },
                    { 23, 4 },
                    { 24, 4 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingConnections_ExpiredPullClaim",
                table: "AccountingConnections",
                columns: new[] { "PullClaimExpiresAtUtc", "Id" },
                filter: "\"PullClaimToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingConnections_ExpiredTokenRotationClaim",
                table: "AccountingConnections",
                columns: new[] { "TokenRotationClaimExpiresAtUtc", "Id" },
                filter: "\"TokenRotationClaimToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingConnections_PortfolioId",
                table: "AccountingConnections",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingConnections_PortfolioId_Provider",
                table: "AccountingConnections",
                columns: new[] { "PortfolioId", "Provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountingConnections_PullEligibility",
                table: "AccountingConnections",
                columns: new[] { "Status", "PullEnabled", "NextPullAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingConnections_Status_TokenExpiresAt",
                table: "AccountingConnections",
                columns: new[] { "Status", "TokenExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingEntityMappings_AccountingConnectionId",
                table: "AccountingEntityMappings",
                column: "AccountingConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingEntityMappings_PortfolioId",
                table: "AccountingEntityMappings",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingEntityMappings_PortfolioId_AccountingConnectionId~",
                table: "AccountingEntityMappings",
                columns: new[] { "PortfolioId", "AccountingConnectionId", "ExternalType", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountingMappingPromotionJobs_AccountingConnectionId",
                table: "AccountingMappingPromotionJobs",
                column: "AccountingConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingMappingPromotionJobs_AccountingEntityMappingId_Ma~",
                table: "AccountingMappingPromotionJobs",
                columns: new[] { "AccountingEntityMappingId", "MappingRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountingMappingPromotionJobs_PortfolioId_CompletedAtUtc_Id",
                table: "AccountingMappingPromotionJobs",
                columns: new[] { "PortfolioId", "CompletedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSyncMaps_AccountingConnectionId",
                table: "AccountingSyncMaps",
                column: "AccountingConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSyncMaps_ParkedPromotion",
                table: "AccountingSyncMaps",
                columns: new[] { "PortfolioId", "AccountingConnectionId", "ExternalType", "Id" },
                filter: "\"LocalEntityId\" IS NULL AND \"Direction\" = 'Import' AND \"Status\" IN ('NeedsReview', 'Unmatched')");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSyncMaps_PortfolioId",
                table: "AccountingSyncMaps",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSyncMaps_PortfolioId_AccountingConnectionId_Direc~",
                table: "AccountingSyncMaps",
                columns: new[] { "PortfolioId", "AccountingConnectionId", "Direction", "ExternalType", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdverseActionNotices_ApplicationId",
                table: "AdverseActionNotices",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_AdverseActionNotices_PortfolioId",
                table: "AdverseActionNotices",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_AdverseActionNotices_StoredFileId",
                table: "AdverseActionNotices",
                column: "StoredFileId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicantScreeningMilestones_ApplicantScreeningId_Portfolio~",
                table: "ApplicantScreeningMilestones",
                columns: new[] { "ApplicantScreeningId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicantScreeningMilestones_PortfolioId_ApplicantScreening~",
                table: "ApplicantScreeningMilestones",
                columns: new[] { "PortfolioId", "ApplicantScreeningId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicantScreeningMilestones_Source_DeliveryId",
                table: "ApplicantScreeningMilestones",
                columns: new[] { "Source", "DeliveryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicantScreenings_ApplicationId_PortfolioId",
                table: "ApplicantScreenings",
                columns: new[] { "ApplicationId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicantScreenings_CreatedByUserId",
                table: "ApplicantScreenings",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicantScreenings_DecisionRecordedByUserId",
                table: "ApplicantScreenings",
                column: "DecisionRecordedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicantScreenings_PortfolioId_ApplicationId_LastStatusAtU~",
                table: "ApplicantScreenings",
                columns: new[] { "PortfolioId", "ApplicationId", "LastStatusAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicantScreenings_PortfolioId_ApplicationId_OperationKey",
                table: "ApplicantScreenings",
                columns: new[] { "PortfolioId", "ApplicationId", "OperationKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicantScreenings_ProviderKey_ProviderReference",
                table: "ApplicantScreenings",
                columns: new[] { "ProviderKey", "ProviderReference" },
                unique: true,
                filter: "\"ProviderKey\" IS NOT NULL AND \"ProviderReference\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationFinancialAccounts_CreatedByUserId",
                table: "ApplicationFinancialAccounts",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationFinancialAccounts_PortfolioId",
                table: "ApplicationFinancialAccounts",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationFinancialAccounts_PublicId",
                table: "ApplicationFinancialAccounts",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationFinancialAccounts_RentalApplicationId",
                table: "ApplicationFinancialAccounts",
                column: "RentalApplicationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationFinancialAccounts_RentalApplicationId_PortfolioId",
                table: "ApplicationFinancialAccounts",
                columns: new[] { "RentalApplicationId", "PortfolioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationFinancialEntries_ApplicationFinancialAccountId_E~",
                table: "ApplicationFinancialEntries",
                columns: new[] { "ApplicationFinancialAccountId", "EffectiveOn", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationFinancialEntries_ApplicationFinancialAccountId_P~",
                table: "ApplicationFinancialEntries",
                columns: new[] { "ApplicationFinancialAccountId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationFinancialEntries_CreatedByUserId",
                table: "ApplicationFinancialEntries",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationFinancialEntries_PortfolioId_IdempotencyKey",
                table: "ApplicationFinancialEntries",
                columns: new[] { "PortfolioId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationFinancialEntries_PortfolioId_PropertyId_Effectiv~",
                table: "ApplicationFinancialEntries",
                columns: new[] { "PortfolioId", "PropertyId", "EffectiveOn", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationFinancialEntries_PortfolioId_Provider_ProviderRe~",
                table: "ApplicationFinancialEntries",
                columns: new[] { "PortfolioId", "Provider", "ProviderReference" },
                unique: true,
                filter: "\"Provider\" IS NOT NULL AND \"ProviderReference\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationFinancialEntries_PropertyId",
                table: "ApplicationFinancialEntries",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationFinancialEntries_PublicId",
                table: "ApplicationFinancialEntries",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationFinancialEntries_RelatedEntryId_ApplicationFinan~",
                table: "ApplicationFinancialEntries",
                columns: new[] { "RelatedEntryId", "ApplicationFinancialAccountId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationFinancialEntries_UnitId",
                table: "ApplicationFinancialEntries",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_LeaseManagementId",
                table: "Appointments",
                column: "LeaseManagementId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_PortfolioId",
                table: "Appointments",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_PropertyId",
                table: "Appointments",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_RentalApplicationId",
                table: "Appointments",
                column: "RentalApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_ScheduledStart",
                table: "Appointments",
                column: "ScheduledStart");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_Status",
                table: "Appointments",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_TenantId",
                table: "Appointments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_UnitId",
                table: "Appointments",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AtomicAuditLogs_CommandType_CommandIdempotencyKey_MutationO~",
                table: "AtomicAuditLogs",
                columns: new[] { "CommandType", "CommandIdempotencyKey", "MutationOrdinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AtomicAuditLogs_EntityType_EntityId",
                table: "AtomicAuditLogs",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AtomicAuditLogs_PortfolioId_Timestamp",
                table: "AtomicAuditLogs",
                columns: new[] { "PortfolioId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AtomicCommandReceipts_CommandType_IdempotencyKey",
                table: "AtomicCommandReceipts",
                columns: new[] { "CommandType", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessionRefreshCredentials_RefreshTokenFamilyId_ExpiresA~",
                table: "AuthSessionRefreshCredentials",
                columns: new[] { "RefreshTokenFamilyId", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessionRefreshCredentials_ReplacedByCredentialId",
                table: "AuthSessionRefreshCredentials",
                column: "ReplacedByCredentialId",
                unique: true,
                filter: "\"ReplacedByCredentialId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessionRefreshCredentials_ReplacedByCredentialId_Refres~",
                table: "AuthSessionRefreshCredentials",
                columns: new[] { "ReplacedByCredentialId", "RefreshTokenFamilyId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessionRefreshCredentials_TokenHash",
                table: "AuthSessionRefreshCredentials",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessionRefreshTokenFamilies_AuthSessionId_AbsoluteExpir~",
                table: "AuthSessionRefreshTokenFamilies",
                columns: new[] { "AuthSessionId", "AbsoluteExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessionRefreshTokenFamilies_ReuseDetectedAtUtc",
                table: "AuthSessionRefreshTokenFamilies",
                column: "ReuseDetectedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessions_ActiveAccessContextId_Status",
                table: "AuthSessions",
                columns: new[] { "ActiveAccessContextId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessions_ActiveAccessContextId_UserId",
                table: "AuthSessions",
                columns: new[] { "ActiveAccessContextId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessions_UserId_Status_ExpiresAtUtc",
                table: "AuthSessions",
                columns: new[] { "UserId", "Status", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationSettings_PortfolioId",
                table: "AutomationSettings",
                column: "PortfolioId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankConnections_PortfolioId",
                table: "BankConnections",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_BankConnections_PortfolioId_Provider_ExternalItemIdHash_Ext~",
                table: "BankConnections",
                columns: new[] { "PortfolioId", "Provider", "ExternalItemIdHash", "ExternalAccountIdHash" });

            migrationBuilder.CreateIndex(
                name: "IX_BankConnections_Status",
                table: "BankConnections",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_BankConnectionId",
                table: "BankTransactions",
                column: "BankConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_BankConnectionId_ProviderTransactionId",
                table: "BankTransactions",
                columns: new[] { "BankConnectionId", "ProviderTransactionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_MatchedExpenseId",
                table: "BankTransactions",
                column: "MatchedExpenseId");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_MatchedTenantAccountId_PortfolioId",
                table: "BankTransactions",
                columns: new[] { "MatchedTenantAccountId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_MatchedTenantLedgerEntryId_MatchedTenantAc~",
                table: "BankTransactions",
                columns: new[] { "MatchedTenantLedgerEntryId", "MatchedTenantAccountId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_MatchStatus",
                table: "BankTransactions",
                column: "MatchStatus");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_PortfolioId",
                table: "BankTransactions",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_PortfolioId_MatchedTenantAccountId_Matched~",
                table: "BankTransactions",
                columns: new[] { "PortfolioId", "MatchedTenantAccountId", "MatchedTenantLedgerEntryId" },
                filter: "\"MatchedTenantLedgerEntryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_PortfolioId_PropertyId_MatchStatus_PostedAt",
                table: "BankTransactions",
                columns: new[] { "PortfolioId", "PropertyId", "MatchStatus", "PostedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_PostedAt",
                table: "BankTransactions",
                column: "PostedAt");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_PropertyId_PortfolioId",
                table: "BankTransactions",
                columns: new[] { "PropertyId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_CapabilityDefinitions_Key",
                table: "CapabilityDefinitions",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CapitalAssets_Portfolio_Property_InServiceDate",
                table: "CapitalAssets",
                columns: new[] { "PortfolioId", "PropertyId", "InServiceDate" });

            migrationBuilder.CreateIndex(
                name: "IX_CapitalAssets_PortfolioId",
                table: "CapitalAssets",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_CapitalAssets_PropertyId",
                table: "CapitalAssets",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_CapitalAssets_SourceExpenseId",
                table: "CapitalAssets",
                column: "SourceExpenseId");

            migrationBuilder.CreateIndex(
                name: "IX_CapitalAssets_UnitId",
                table: "CapitalAssets",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessages_ConversationId",
                table: "ConversationMessages",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_LastMessageAt",
                table: "Conversations",
                column: "LastMessageAt");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_PortfolioId_TenantId",
                table: "Conversations",
                columns: new[] { "PortfolioId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_PortfolioId_WorkOrderId",
                table: "Conversations",
                columns: new[] { "PortfolioId", "WorkOrderId" },
                unique: true,
                filter: "\"WorkOrderId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_PropertyId",
                table: "Conversations",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_TenantId",
                table: "Conversations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_WorkOrderId",
                table: "Conversations",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTokens_PortfolioId_UserId",
                table: "DeviceTokens",
                columns: new[] { "PortfolioId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTokens_Token",
                table: "DeviceTokens",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTemplateFields_DocumentTemplateId_PortfolioId",
                table: "DocumentTemplateFields",
                columns: new[] { "DocumentTemplateId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTemplateFields_PortfolioId_DocumentTemplateId_Field~",
                table: "DocumentTemplateFields",
                columns: new[] { "PortfolioId", "DocumentTemplateId", "FieldKey" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTemplates_CompiledStoredFileId",
                table: "DocumentTemplates",
                column: "CompiledStoredFileId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTemplates_OriginalStoredFileId",
                table: "DocumentTemplates",
                column: "OriginalStoredFileId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTemplates_PortfolioId",
                table: "DocumentTemplates",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTemplates_PortfolioId_Kind_DefaultForPortfolio",
                table: "DocumentTemplates",
                columns: new[] { "PortfolioId", "Kind", "DefaultForPortfolio" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTemplates_PortfolioId_Kind_Status",
                table: "DocumentTemplates",
                columns: new[] { "PortfolioId", "Kind", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTemplates_PropertyId",
                table: "DocumentTemplates",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_EngineWorkerHeartbeats_WorkerName",
                table: "EngineWorkerHeartbeats",
                column: "WorkerName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvictionCaseEvents_EvictionCaseId_EventDate",
                table: "EvictionCaseEvents",
                columns: new[] { "EvictionCaseId", "EventDate" });

            migrationBuilder.CreateIndex(
                name: "IX_EvictionCaseEvents_PortfolioId",
                table: "EvictionCaseEvents",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_EvictionCaseRespondents_EvictionCaseId_LeaseManagementParty~",
                table: "EvictionCaseRespondents",
                columns: new[] { "EvictionCaseId", "LeaseManagementPartyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvictionCaseRespondents_LeaseManagementPartyId",
                table: "EvictionCaseRespondents",
                column: "LeaseManagementPartyId");

            migrationBuilder.CreateIndex(
                name: "IX_EvictionCaseRespondents_PortfolioId_LeaseManagementPartyId",
                table: "EvictionCaseRespondents",
                columns: new[] { "PortfolioId", "LeaseManagementPartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_EvictionCases_LeaseAgreementId",
                table: "EvictionCases",
                column: "LeaseAgreementId");

            migrationBuilder.CreateIndex(
                name: "IX_EvictionCases_LeaseManagementId",
                table: "EvictionCases",
                column: "LeaseManagementId");

            migrationBuilder.CreateIndex(
                name: "IX_EvictionCases_Portfolio_LeaseManagement_Status",
                table: "EvictionCases",
                columns: new[] { "PortfolioId", "LeaseManagementId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_EvictionCases_Portfolio_Property_Status",
                table: "EvictionCases",
                columns: new[] { "PortfolioId", "PropertyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_EvictionCases_PortfolioId",
                table: "EvictionCases",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_EvictionCases_PropertyId",
                table: "EvictionCases",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_EvictionCases_UnitId",
                table: "EvictionCases",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseLineItems_ExpenseId",
                table: "ExpenseLineItems",
                column: "ExpenseId");

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_CapitalizedAssetId",
                table: "Expenses",
                column: "CapitalizedAssetId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_Portfolio_IncurredAt",
                table: "Expenses",
                columns: new[] { "PortfolioId", "IncurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_Portfolio_PaidAt",
                table: "Expenses",
                columns: new[] { "PortfolioId", "PaidAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_Portfolio_Property_Category_IncurredAt",
                table: "Expenses",
                columns: new[] { "PortfolioId", "PropertyId", "Category", "IncurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_PortfolioId",
                table: "Expenses",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_PropertyId",
                table: "Expenses",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_Status",
                table: "Expenses",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_UnitId",
                table: "Expenses",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_VendorId",
                table: "Expenses",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_WorkOrderId",
                table: "Expenses",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "UX_Expenses_RecurringExpense_Occurrence",
                table: "Expenses",
                columns: new[] { "RecurringExpenseId", "RecurringExpenseOccurrenceDate" },
                unique: true,
                filter: "\"RecurringExpenseId\" IS NOT NULL AND \"RecurringExpenseOccurrenceDate\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalListingSignals_ListingPublicationId_Disposition_Rec~",
                table: "ExternalListingSignals",
                columns: new[] { "ListingPublicationId", "Disposition", "ReceivedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalListingSignals_ListingPublicationId_PortfolioId",
                table: "ExternalListingSignals",
                columns: new[] { "ListingPublicationId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalListingSignals_PortfolioId_ProviderMessageKey",
                table: "ExternalListingSignals",
                columns: new[] { "PortfolioId", "ProviderMessageKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InspectionItems_InspectionId",
                table: "InspectionItems",
                column: "InspectionId");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionItems_PhotoStoredFileId",
                table: "InspectionItems",
                column: "PhotoStoredFileId");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionItems_PortfolioId",
                table: "InspectionItems",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionItems_SpawnedWorkOrderId",
                table: "InspectionItems",
                column: "SpawnedWorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Inspections_LeaseAgreementId",
                table: "Inspections",
                column: "LeaseAgreementId");

            migrationBuilder.CreateIndex(
                name: "IX_Inspections_LeaseManagementId",
                table: "Inspections",
                column: "LeaseManagementId");

            migrationBuilder.CreateIndex(
                name: "IX_Inspections_PortfolioId",
                table: "Inspections",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_Inspections_PropertyId",
                table: "Inspections",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_Inspections_ScheduledFor",
                table: "Inspections",
                column: "ScheduledFor");

            migrationBuilder.CreateIndex(
                name: "IX_Inspections_Status",
                table: "Inspections",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Inspections_UnitId",
                table: "Inspections",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionTemplateItems_TemplateId",
                table: "InspectionTemplateItems",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionTemplates_PortfolioId",
                table: "InspectionTemplates",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAddenda_BaseAgreementId_LeaseManagementId_PortfolioId",
                table: "LeaseAddenda",
                columns: new[] { "BaseAgreementId", "LeaseManagementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAddenda_CreatedByUserId",
                table: "LeaseAddenda",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAddenda_DocumentSourceVersionId_PortfolioId",
                table: "LeaseAddenda",
                columns: new[] { "DocumentSourceVersionId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAddenda_ExecutedArtifactId_PortfolioId",
                table: "LeaseAddenda",
                columns: new[] { "ExecutedArtifactId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAddenda_IssuedArtifactId_PortfolioId",
                table: "LeaseAddenda",
                columns: new[] { "IssuedArtifactId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAddenda_LeaseManagementId_PortfolioId",
                table: "LeaseAddenda",
                columns: new[] { "LeaseManagementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAddenda_LeaseManagementId_SeriesPublicId_VersionNumber",
                table: "LeaseAddenda",
                columns: new[] { "LeaseManagementId", "SeriesPublicId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAddenda_PortfolioId_AddendumNumber_VersionNumber",
                table: "LeaseAddenda",
                columns: new[] { "PortfolioId", "AddendumNumber", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAddenda_PortfolioId_BaseAgreementId_SeriesPublicId_Ver~",
                table: "LeaseAddenda",
                columns: new[] { "PortfolioId", "BaseAgreementId", "SeriesPublicId", "VersionNumber" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAddenda_PortfolioId_LeaseManagementId_EffectiveFromOn_~",
                table: "LeaseAddenda",
                columns: new[] { "PortfolioId", "LeaseManagementId", "EffectiveFromOn", "EffectiveThroughOn" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAddenda_PublicId",
                table: "LeaseAddenda",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAddenda_ReplacesAddendumId_SeriesPublicId_LeaseManagem~",
                table: "LeaseAddenda",
                columns: new[] { "ReplacesAddendumId", "SeriesPublicId", "LeaseManagementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAddenda_SupersededByAddendumId_SeriesPublicId_LeaseMan~",
                table: "LeaseAddenda",
                columns: new[] { "SupersededByAddendumId", "SeriesPublicId", "LeaseManagementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAddendumFinancialEffects_LeaseAddendumId_PortfolioId",
                table: "LeaseAddendumFinancialEffects",
                columns: new[] { "LeaseAddendumId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAddendumFinancialEffects_PortfolioId_DueOn_Id",
                table: "LeaseAddendumFinancialEffects",
                columns: new[] { "PortfolioId", "DueOn", "Id" },
                filter: "\"EffectType\" = 'OneTimeCharge'");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAddendumFinancialEffects_PortfolioId_LeaseAddendumId_E~",
                table: "LeaseAddendumFinancialEffects",
                columns: new[] { "PortfolioId", "LeaseAddendumId", "EffectType", "EffectiveFromOn", "EffectiveThroughOn" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAddendumSigners_LeaseAddendumId_PortfolioId",
                table: "LeaseAddendumSigners",
                columns: new[] { "LeaseAddendumId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAddendumSigners_LeaseAddendumId_SigningOrder",
                table: "LeaseAddendumSigners",
                columns: new[] { "LeaseAddendumId", "SigningOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAddendumSigners_LeaseManagementPartyId_PortfolioId",
                table: "LeaseAddendumSigners",
                columns: new[] { "LeaseManagementPartyId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAddendumSigners_PortfolioId",
                table: "LeaseAddendumSigners",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAddendumSigners_TenantId_PortfolioId",
                table: "LeaseAddendumSigners",
                columns: new[] { "TenantId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAgreements_CreatedByUserId",
                table: "LeaseAgreements",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAgreements_DocumentSourceVersionId_PortfolioId",
                table: "LeaseAgreements",
                columns: new[] { "DocumentSourceVersionId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAgreements_DraftCanceledByUserId",
                table: "LeaseAgreements",
                column: "DraftCanceledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAgreements_ExecutedArtifactId_PortfolioId",
                table: "LeaseAgreements",
                columns: new[] { "ExecutedArtifactId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAgreements_IssuedArtifactId_PortfolioId",
                table: "LeaseAgreements",
                columns: new[] { "IssuedArtifactId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAgreements_LeaseManagementId_PortfolioId",
                table: "LeaseAgreements",
                columns: new[] { "LeaseManagementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAgreements_LeaseManagementId_VersionNumber",
                table: "LeaseAgreements",
                columns: new[] { "LeaseManagementId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAgreements_PortfolioId_AgreementNumber",
                table: "LeaseAgreements",
                columns: new[] { "PortfolioId", "AgreementNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAgreements_PortfolioId_GoverningFromOn_TermEndOn_Id",
                table: "LeaseAgreements",
                columns: new[] { "PortfolioId", "GoverningFromOn", "TermEndOn", "Id" },
                filter: "\"FullyExecutedAtUtc\" IS NOT NULL AND \"VoidedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAgreements_PortfolioId_IssuedAtUtc_FullyExecutedAtUtc_~",
                table: "LeaseAgreements",
                columns: new[] { "PortfolioId", "IssuedAtUtc", "FullyExecutedAtUtc", "Id" },
                filter: "\"VoidedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAgreements_PortfolioId_LeaseManagementId_VersionNumber",
                table: "LeaseAgreements",
                columns: new[] { "PortfolioId", "LeaseManagementId", "VersionNumber" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAgreements_PortfolioId_TermEndOn_Id",
                table: "LeaseAgreements",
                columns: new[] { "PortfolioId", "TermEndOn", "Id" },
                filter: "\"FullyExecutedAtUtc\" IS NOT NULL AND \"VoidedAtUtc\" IS NULL AND \"TermEndOn\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAgreements_PublicId",
                table: "LeaseAgreements",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAgreements_ReissuesAgreementId_LeaseManagementId_Portf~",
                table: "LeaseAgreements",
                columns: new[] { "ReissuesAgreementId", "LeaseManagementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAgreements_RenewsAgreementId_LeaseManagementId_Portfol~",
                table: "LeaseAgreements",
                columns: new[] { "RenewsAgreementId", "LeaseManagementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAgreements_ReplacesAgreementId_LeaseManagementId_Portf~",
                table: "LeaseAgreements",
                columns: new[] { "ReplacesAgreementId", "LeaseManagementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAgreements_SupersededByAgreementId_LeaseManagementId_P~",
                table: "LeaseAgreements",
                columns: new[] { "SupersededByAgreementId", "LeaseManagementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAgreements_TransferredFromAgreementId_PortfolioId",
                table: "LeaseAgreements",
                columns: new[] { "TransferredFromAgreementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAgreementSigners_LeaseAgreementId_PortfolioId",
                table: "LeaseAgreementSigners",
                columns: new[] { "LeaseAgreementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAgreementSigners_LeaseAgreementId_SigningOrder",
                table: "LeaseAgreementSigners",
                columns: new[] { "LeaseAgreementId", "SigningOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAgreementSigners_LeaseManagementPartyId_PortfolioId",
                table: "LeaseAgreementSigners",
                columns: new[] { "LeaseManagementPartyId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAgreementSigners_PortfolioId",
                table: "LeaseAgreementSigners",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseAgreementSigners_TenantId_PortfolioId",
                table: "LeaseAgreementSigners",
                columns: new[] { "TenantId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseManagementParties_CreatedByUserId",
                table: "LeaseManagementParties",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseManagementParties_LeaseManagementId_PortfolioId",
                table: "LeaseManagementParties",
                columns: new[] { "LeaseManagementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseManagementParties_PortfolioId_LeaseManagementId_Effect~",
                table: "LeaseManagementParties",
                columns: new[] { "PortfolioId", "LeaseManagementId", "EffectiveFrom", "EffectiveThrough", "Role" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseManagementParties_PortfolioId_TenantId_EffectiveFrom_Id",
                table: "LeaseManagementParties",
                columns: new[] { "PortfolioId", "TenantId", "EffectiveFrom", "Id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseManagementParties_TenantId_PortfolioId",
                table: "LeaseManagementParties",
                columns: new[] { "TenantId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseManagements_CreatedByUserId",
                table: "LeaseManagements",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseManagements_EndingDispositionDecidedByUserId",
                table: "LeaseManagements",
                column: "EndingDispositionDecidedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseManagements_PortfolioId_PlannedPossessionAtUtc_Id",
                table: "LeaseManagements",
                columns: new[] { "PortfolioId", "PlannedPossessionAtUtc", "Id" },
                filter: "\"PossessionGivenAtUtc\" IS NULL AND \"CanceledAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseManagements_PortfolioId_PossessionReturnedAtUtc_Accoun~",
                table: "LeaseManagements",
                columns: new[] { "PortfolioId", "PossessionReturnedAtUtc", "AccountClosedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseManagements_PortfolioId_RelationshipNumber",
                table: "LeaseManagements",
                columns: new[] { "PortfolioId", "RelationshipNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeaseManagements_PortfolioId_UnitId_CreatedAtUtc_Id",
                table: "LeaseManagements",
                columns: new[] { "PortfolioId", "UnitId", "CreatedAtUtc", "Id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseManagements_PossessionAgreementExceptionAuthorizedByUs~",
                table: "LeaseManagements",
                column: "PossessionAgreementExceptionAuthorizedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseManagements_PropertyId_PortfolioId",
                table: "LeaseManagements",
                columns: new[] { "PropertyId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseManagements_PublicId",
                table: "LeaseManagements",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeaseManagements_TransferPublicId",
                table: "LeaseManagements",
                column: "TransferPublicId",
                unique: true,
                filter: "\"TransferPublicId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseManagements_TransferredFromLeaseManagementId",
                table: "LeaseManagements",
                column: "TransferredFromLeaseManagementId",
                unique: true,
                filter: "\"TransferredFromLeaseManagementId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseManagements_TransferredFromLeaseManagementId_Portfolio~",
                table: "LeaseManagements",
                columns: new[] { "TransferredFromLeaseManagementId", "PortfolioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeaseManagements_UnitId_PropertyId_PortfolioId",
                table: "LeaseManagements",
                columns: new[] { "UnitId", "PropertyId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseRenewalAddendumDecisions_CreatedByUserId",
                table: "LeaseRenewalAddendumDecisions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseRenewalAddendumDecisions_LeaseManagementId_PortfolioId",
                table: "LeaseRenewalAddendumDecisions",
                columns: new[] { "LeaseManagementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseRenewalAddendumDecisions_PortfolioId_LeaseManagementId~",
                table: "LeaseRenewalAddendumDecisions",
                columns: new[] { "PortfolioId", "LeaseManagementId", "RenewalAgreementId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseRenewalAddendumDecisions_RenewalAgreementId_LeaseManag~",
                table: "LeaseRenewalAddendumDecisions",
                columns: new[] { "RenewalAgreementId", "LeaseManagementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseRenewalAddendumDecisions_RenewalAgreementId_SourceAdde~",
                table: "LeaseRenewalAddendumDecisions",
                columns: new[] { "RenewalAgreementId", "SourceAddendumSeriesPublicId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeaseRenewalAddendumDecisions_ReplacementAddendumId_LeaseMa~",
                table: "LeaseRenewalAddendumDecisions",
                columns: new[] { "ReplacementAddendumId", "LeaseManagementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LegalDocumentArtifacts_CreatedByUserId",
                table: "LegalDocumentArtifacts",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_LegalDocumentArtifacts_PortfolioId_ContentSha256",
                table: "LegalDocumentArtifacts",
                columns: new[] { "PortfolioId", "ContentSha256" });

            migrationBuilder.CreateIndex(
                name: "IX_LegalDocumentArtifacts_PortfolioId_StorageKey",
                table: "LegalDocumentArtifacts",
                columns: new[] { "PortfolioId", "StorageKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegalDocumentArtifacts_PortfolioId_StoredFileId",
                table: "LegalDocumentArtifacts",
                columns: new[] { "PortfolioId", "StoredFileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegalDocumentArtifacts_PublicId",
                table: "LegalDocumentArtifacts",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegalDocumentArtifacts_StoredFileId_PortfolioId",
                table: "LegalDocumentArtifacts",
                columns: new[] { "StoredFileId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LegalDocumentSourceVersions_CreatedByUserId",
                table: "LegalDocumentSourceVersions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_LegalDocumentSourceVersions_DocumentTemplateId_DocumentTemp~",
                table: "LegalDocumentSourceVersions",
                columns: new[] { "DocumentTemplateId", "DocumentTemplateVersion", "PortfolioId" },
                unique: true,
                filter: "\"SourceKind\" = 'AuthoredTemplateSnapshot'");

            migrationBuilder.CreateIndex(
                name: "IX_LegalDocumentSourceVersions_DocumentTemplateId_PortfolioId",
                table: "LegalDocumentSourceVersions",
                columns: new[] { "DocumentTemplateId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LegalDocumentSourceVersions_PortfolioId_BusinessKey",
                table: "LegalDocumentSourceVersions",
                columns: new[] { "PortfolioId", "BusinessKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegalDocumentSourceVersions_PortfolioId_RendererKey_Rendere~",
                table: "LegalDocumentSourceVersions",
                columns: new[] { "PortfolioId", "RendererKey", "RendererVersion" },
                unique: true,
                filter: "\"SourceKind\" = 'BuiltInRenderer'");

            migrationBuilder.CreateIndex(
                name: "IX_LegalDocumentSourceVersions_PublicId",
                table: "LegalDocumentSourceVersions",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegalDocumentSourceVersions_SourceLegalDocumentArtifactId_P~",
                table: "LegalDocumentSourceVersions",
                columns: new[] { "SourceLegalDocumentArtifactId", "PortfolioId" },
                unique: true,
                filter: "\"SourceKind\" = 'ImportedExternalDocument'");

            migrationBuilder.CreateIndex(
                name: "IX_LegalDocumentSourceVersions_SourceLegalDocumentArtifactId_S~",
                table: "LegalDocumentSourceVersions",
                columns: new[] { "SourceLegalDocumentArtifactId", "SourceStoredFileId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_LegalDocumentSourceVersions_SourceStoredFileId_PortfolioId",
                table: "LegalDocumentSourceVersions",
                columns: new[] { "SourceStoredFileId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_ListingPhotos_RentalListingId_PortfolioId",
                table: "ListingPhotos",
                columns: new[] { "RentalListingId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_ListingPhotos_RentalListingId_Position",
                table: "ListingPhotos",
                columns: new[] { "RentalListingId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ListingPhotos_StoredFileId_PortfolioId",
                table: "ListingPhotos",
                columns: new[] { "StoredFileId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_ListingPublications_RentalListingId_PortfolioId",
                table: "ListingPublications",
                columns: new[] { "RentalListingId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_ListingPublications_RentalListingId_ProviderKey_Mode",
                table: "ListingPublications",
                columns: new[] { "RentalListingId", "ProviderKey", "Mode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoanPayments_LoanId",
                table: "LoanPayments",
                column: "LoanId");

            migrationBuilder.CreateIndex(
                name: "IX_LoanPayments_LoanId_PeriodKey",
                table: "LoanPayments",
                columns: new[] { "LoanId", "PeriodKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoanPayments_PortfolioId",
                table: "LoanPayments",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_Loans_DebtServiceClaim",
                table: "Loans",
                columns: new[] { "Status", "WorkerClaimExpiresAtUtc", "StartDate", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Loans_PortfolioId",
                table: "Loans",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_Loans_PropertyId",
                table: "Loans",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_Loans_Status",
                table: "Loans",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_LoginContextSelectionChallenges_TokenHash",
                table: "LoginContextSelectionChallenges",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoginContextSelectionChallenges_UserId_ExpiresAtUtc",
                table: "LoginContextSelectionChallenges",
                columns: new[] { "UserId", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MembershipRoleAssignmentProperties_MembershipRoleAssignment~",
                table: "MembershipRoleAssignmentProperties",
                columns: new[] { "MembershipRoleAssignmentId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_MembershipRoleAssignmentProperties_PortfolioId_PropertyId",
                table: "MembershipRoleAssignmentProperties",
                columns: new[] { "PortfolioId", "PropertyId" });

            migrationBuilder.CreateIndex(
                name: "IX_MembershipRoleAssignmentProperties_PropertyId_PortfolioId",
                table: "MembershipRoleAssignmentProperties",
                columns: new[] { "PropertyId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_MembershipRoleAssignments_PortfolioId_Status_EffectiveFromU~",
                table: "MembershipRoleAssignments",
                columns: new[] { "PortfolioId", "Status", "EffectiveFromUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MembershipRoleAssignments_RoleProfileId_Status",
                table: "MembershipRoleAssignments",
                columns: new[] { "RoleProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MembershipRoleAssignments_WorkspaceMembershipId_PortfolioId",
                table: "MembershipRoleAssignments",
                columns: new[] { "WorkspaceMembershipId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_MembershipRoleAssignments_WorkspaceMembershipId_Status_Effe~",
                table: "MembershipRoleAssignments",
                columns: new[] { "WorkspaceMembershipId", "Status", "EffectiveFromUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MessagingProviderSettings_PortfolioId",
                table: "MessagingProviderSettings",
                column: "PortfolioId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDeliveryEvidence_ConversationMessageId",
                table: "NoticeDeliveryEvidence",
                column: "ConversationMessageId",
                unique: true,
                filter: "\"ConversationMessageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDeliveryEvidence_OutboxMessageId",
                table: "NoticeDeliveryEvidence",
                column: "OutboxMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDeliveryEvidence_RecipientLeaseManagementPartyId_Port~",
                table: "NoticeDeliveryEvidence",
                columns: new[] { "RecipientLeaseManagementPartyId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDeliveryEvidence_RenderedNoticeId_PortfolioId",
                table: "NoticeDeliveryEvidence",
                columns: new[] { "RenderedNoticeId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDeliveryEvidence_RenderedNoticeId_RecipientLeaseManag~",
                table: "NoticeDeliveryEvidence",
                columns: new[] { "RenderedNoticeId", "RecipientLeaseManagementPartyId", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDrafts_ConversationId",
                table: "NoticeDrafts",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDrafts_LeaseAddendumId",
                table: "NoticeDrafts",
                column: "LeaseAddendumId");

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDrafts_LeaseAddendumId_LeaseManagementId_PortfolioId",
                table: "NoticeDrafts",
                columns: new[] { "LeaseAddendumId", "LeaseManagementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDrafts_LeaseAgreementId",
                table: "NoticeDrafts",
                column: "LeaseAgreementId");

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDrafts_LeaseAgreementId_LeaseManagementId_PortfolioId",
                table: "NoticeDrafts",
                columns: new[] { "LeaseAgreementId", "LeaseManagementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDrafts_LeaseManagementId",
                table: "NoticeDrafts",
                column: "LeaseManagementId");

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDrafts_LeaseManagementId_PortfolioId",
                table: "NoticeDrafts",
                columns: new[] { "LeaseManagementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDrafts_PortfolioId",
                table: "NoticeDrafts",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDrafts_PropertyId",
                table: "NoticeDrafts",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDrafts_RecipientLeaseManagementPartyId",
                table: "NoticeDrafts",
                column: "RecipientLeaseManagementPartyId");

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDrafts_RecipientLeaseManagementPartyId_LeaseManagemen~",
                table: "NoticeDrafts",
                columns: new[] { "RecipientLeaseManagementPartyId", "LeaseManagementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDrafts_RenderedNoticeId",
                table: "NoticeDrafts",
                column: "RenderedNoticeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDrafts_Status",
                table: "NoticeDrafts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDrafts_TenantAccountId",
                table: "NoticeDrafts",
                column: "TenantAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDrafts_TenantAccountId_LeaseManagementId_PortfolioId",
                table: "NoticeDrafts",
                columns: new[] { "TenantAccountId", "LeaseManagementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDrafts_TenantLedgerEntryId",
                table: "NoticeDrafts",
                column: "TenantLedgerEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDrafts_TenantLedgerEntryId_TenantAccountId_PortfolioId",
                table: "NoticeDrafts",
                columns: new[] { "TenantLedgerEntryId", "TenantAccountId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDrafts_TenantNoticePolicyId",
                table: "NoticeDrafts",
                column: "TenantNoticePolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_NoticeDrafts_WorkspaceNoticeTemplateVersionId",
                table: "NoticeDrafts",
                column: "WorkspaceNoticeTemplateVersionId");

            migrationBuilder.CreateIndex(
                name: "UX_NoticeDrafts_OpenAgreementNotice",
                table: "NoticeDrafts",
                columns: new[] { "PortfolioId", "LeaseManagementId", "LeaseAgreementId", "RecipientLeaseManagementPartyId", "NoticeType" },
                unique: true,
                filter: "\"TenantLedgerEntryId\" IS NULL AND \"LeaseAgreementId\" IS NOT NULL AND \"Status\" IN ('Draft','Approved')");

            migrationBuilder.CreateIndex(
                name: "UX_NoticeDrafts_OpenLedgerNotice",
                table: "NoticeDrafts",
                columns: new[] { "PortfolioId", "TenantLedgerEntryId", "RecipientLeaseManagementPartyId", "NoticeType" },
                unique: true,
                filter: "\"TenantLedgerEntryId\" IS NOT NULL AND \"Status\" IN ('Draft','Approved')");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationReadStates_NotificationId_PortfolioId",
                table: "NotificationReadStates",
                columns: new[] { "NotificationId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationReadStates_PortfolioId_UserId_NotificationId",
                table: "NotificationReadStates",
                columns: new[] { "PortfolioId", "UserId", "NotificationId" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationReadStates_UserId",
                table: "NotificationReadStates",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_CreatedAt",
                table: "Notifications",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_PortfolioId",
                table: "Notifications",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId",
                table: "Notifications",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_OAuthStates_ExpiresAt",
                table: "OAuthStates",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_OAuthStates_PortfolioId",
                table: "OAuthStates",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_OAuthStates_StateToken",
                table: "OAuthStates",
                column: "StateToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ExpiredClaim",
                table: "OutboxMessages",
                column: "ClaimExpiresAtUtc",
                filter: "\"ClaimToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_IdempotencyKey",
                table: "OutboxMessages",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_PortfolioId",
                table: "OutboxMessages",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ProviderReceipt",
                table: "OutboxMessages",
                columns: new[] { "Provider", "ProviderMessageId" },
                filter: "\"ProviderMessageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Ready",
                table: "OutboxMessages",
                columns: new[] { "NextAttemptAtUtc", "CreatedAtUtc", "Id" },
                filter: "\"AcceptedAtUtc\" IS NULL AND \"DeadLetteredAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerDistributions_OwnerEntityId",
                table: "OwnerDistributions",
                column: "OwnerEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerDistributions_PortfolioId_OwnerEntityId_Date",
                table: "OwnerDistributions",
                columns: new[] { "PortfolioId", "OwnerEntityId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerDistributions_PropertyId",
                table: "OwnerDistributions",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerEntities_PortfolioId",
                table: "OwnerEntities",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerEntities_PortfolioId_IsPrimary",
                table: "OwnerEntities",
                columns: new[] { "PortfolioId", "IsPrimary" });

            migrationBuilder.CreateIndex(
                name: "IX_Owners_PortfolioId",
                table: "Owners",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerUserAccesses_AccessContextId_ApplicationUserId_Portfol~",
                table: "OwnerUserAccesses",
                columns: new[] { "AccessContextId", "ApplicationUserId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerUserAccesses_AccessContextId_OwnerEntityId",
                table: "OwnerUserAccesses",
                columns: new[] { "AccessContextId", "OwnerEntityId" },
                unique: true,
                filter: "\"RevokedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerUserAccesses_AccessContextId_PortfolioId_EffectiveFrom~",
                table: "OwnerUserAccesses",
                columns: new[] { "AccessContextId", "PortfolioId", "EffectiveFromUtc", "EffectiveToUtc", "RevokedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerUserAccesses_ApplicationUserId",
                table: "OwnerUserAccesses",
                column: "ApplicationUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerUserAccesses_GrantedByUserId",
                table: "OwnerUserAccesses",
                column: "GrantedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerUserAccesses_OwnerEntityId_PortfolioId",
                table: "OwnerUserAccesses",
                columns: new[] { "OwnerEntityId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerUserAccesses_PortfolioId",
                table: "OwnerUserAccesses",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerUserAccesses_PublicId",
                table: "OwnerUserAccesses",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OwnerUserAccesses_RevokedByUserId",
                table: "OwnerUserAccesses",
                column: "RevokedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingFileUploads_PortfolioId_ActorScopeId_Purpose_Operati~",
                table: "PendingFileUploads",
                columns: new[] { "PortfolioId", "ActorScopeId", "Purpose", "OperationKeyHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingFileUploads_State_CreatedAtUtc_CleanupClaimExpiresAt~",
                table: "PendingFileUploads",
                columns: new[] { "State", "CreatedAtUtc", "CleanupClaimExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingFileUploads_StoredFileId",
                table: "PendingFileUploads",
                column: "StoredFileId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaidTokenExchangeAttempts_BankConnectionId",
                table: "PlaidTokenExchangeAttempts",
                column: "BankConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaidTokenExchangeAttempts_PortfolioId_ClientOperationId",
                table: "PlaidTokenExchangeAttempts",
                columns: new[] { "PortfolioId", "ClientOperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlaidTokenExchangeAttempts_PortfolioId_Status_PreparedAtUtc",
                table: "PlaidTokenExchangeAttempts",
                columns: new[] { "PortfolioId", "Status", "PreparedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Portfolios_PublicApplicationToken",
                table: "Portfolios",
                column: "PublicApplicationToken",
                unique: true,
                filter: "\"PublicApplicationToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Portfolios_Status",
                table: "Portfolios",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Properties_OwnerEntityId",
                table: "Properties",
                column: "OwnerEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_Properties_OwnerId",
                table: "Properties",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Properties_PortfolioId",
                table: "Properties",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_Properties_Status",
                table: "Properties",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyDispositions_ClosedOnDate",
                table: "PropertyDispositions",
                column: "ClosedOnDate");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyDispositions_Portfolio_Property_Active",
                table: "PropertyDispositions",
                columns: new[] { "PortfolioId", "PropertyId" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyDispositions_PortfolioId",
                table: "PropertyDispositions",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyDispositions_PropertyId",
                table: "PropertyDispositions",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderInboxEvents_ClaimExpiresAtUtc_Id",
                table: "ProviderInboxEvents",
                columns: new[] { "ClaimExpiresAtUtc", "Id" },
                filter: "\"ProcessedAtUtc\" IS NULL AND \"DeadLetteredAtUtc\" IS NULL AND \"ClaimToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderInboxEvents_NextAttemptAtUtc_ReceivedAtUtc_Id",
                table: "ProviderInboxEvents",
                columns: new[] { "NextAttemptAtUtc", "ReceivedAtUtc", "Id" },
                filter: "\"ProcessedAtUtc\" IS NULL AND \"DeadLetteredAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderInboxEvents_Provider_ProviderEventId",
                table: "ProviderInboxEvents",
                columns: new[] { "Provider", "ProviderEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProviderInboxEvents_Provider_ProviderObjectId",
                table: "ProviderInboxEvents",
                columns: new[] { "Provider", "ProviderObjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_QueuedJobs_PortfolioId",
                table: "QueuedJobs",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_QueuedJobs_Status",
                table: "QueuedJobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringExpenses_GenerationClaim",
                table: "RecurringExpenses",
                columns: new[] { "Active", "NextRunDate", "WorkerClaimExpiresAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringExpenses_PortfolioId",
                table: "RecurringExpenses",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringExpenses_PropertyId",
                table: "RecurringExpenses",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringExpenses_UnitId",
                table: "RecurringExpenses",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringMaintenanceTasks_Active_NextDueDate",
                table: "RecurringMaintenanceTasks",
                columns: new[] { "IsActive", "NextDueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringMaintenanceTasks_GenerationClaim",
                table: "RecurringMaintenanceTasks",
                columns: new[] { "IsActive", "NextDueDate", "WorkerClaimExpiresAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringMaintenanceTasks_PortfolioId_IsActive_NextDueDate",
                table: "RecurringMaintenanceTasks",
                columns: new[] { "PortfolioId", "IsActive", "NextDueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringMaintenanceTasks_PropertyId",
                table: "RecurringMaintenanceTasks",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringMaintenanceTasks_UnitId",
                table: "RecurringMaintenanceTasks",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringMaintenanceTasks_VendorId",
                table: "RecurringMaintenanceTasks",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_RenderedNotices_PortfolioId_NoticeDraftId",
                table: "RenderedNotices",
                columns: new[] { "PortfolioId", "NoticeDraftId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RentalApplications_ApprovedTenantId",
                table: "RentalApplications",
                column: "ApprovedTenantId");

            migrationBuilder.CreateIndex(
                name: "IX_RentalApplications_PortfolioId",
                table: "RentalApplications",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_RentalApplications_PreparedLeaseManagementId_PortfolioId",
                table: "RentalApplications",
                columns: new[] { "PreparedLeaseManagementId", "PortfolioId" },
                unique: true,
                filter: "\"PreparedLeaseManagementId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RentalApplications_PropertyId",
                table: "RentalApplications",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_RentalApplications_Status",
                table: "RentalApplications",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_RentalApplications_SubmittedAtUtc",
                table: "RentalApplications",
                column: "SubmittedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RentalApplications_UnitId",
                table: "RentalApplications",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_RentalListings_PortfolioId",
                table: "RentalListings",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_RentalListings_PortfolioId_UnitId",
                table: "RentalListings",
                columns: new[] { "PortfolioId", "UnitId" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RentalListings_PropertyId",
                table: "RentalListings",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_RentalListings_UnitId",
                table: "RentalListings",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_RentalListings_UnitId_PropertyId_PortfolioId",
                table: "RentalListings",
                columns: new[] { "UnitId", "PropertyId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_RoleProfileCapabilities_CapabilityDefinitionId",
                table: "RoleProfileCapabilities",
                column: "CapabilityDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleProfiles_Key",
                table: "RoleProfiles",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScanBatches_PortfolioId",
                table: "ScanBatches",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanBatches_Status",
                table: "ScanBatches",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ScanDrafts_BatchId",
                table: "ScanDrafts",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanDrafts_CaptureApplicationId",
                table: "ScanDrafts",
                column: "CaptureApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanDrafts_CaptureLeaseAgreementId",
                table: "ScanDrafts",
                column: "CaptureLeaseAgreementId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanDrafts_CaptureLeaseManagementId",
                table: "ScanDrafts",
                column: "CaptureLeaseManagementId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanDrafts_CapturePropertyId",
                table: "ScanDrafts",
                column: "CapturePropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanDrafts_CaptureRentalListingId",
                table: "ScanDrafts",
                column: "CaptureRentalListingId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanDrafts_CaptureTenantAccountId",
                table: "ScanDrafts",
                column: "CaptureTenantAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanDrafts_CaptureTenantLedgerEntryId",
                table: "ScanDrafts",
                column: "CaptureTenantLedgerEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanDrafts_CaptureUnitId",
                table: "ScanDrafts",
                column: "CaptureUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanDrafts_CaptureWorkOrderId",
                table: "ScanDrafts",
                column: "CaptureWorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanDrafts_PortfolioId",
                table: "ScanDrafts",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanDrafts_PortfolioId_SourceContentSha256",
                table: "ScanDrafts",
                columns: new[] { "PortfolioId", "SourceContentSha256" });

            migrationBuilder.CreateIndex(
                name: "IX_ScanDrafts_SourceStoredFileId",
                table: "ScanDrafts",
                column: "SourceStoredFileId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanDrafts_Status_ProcessingClaimExpiresAtUtc_CreatedAt_Id",
                table: "ScanDrafts",
                columns: new[] { "Status", "ProcessingClaimExpiresAtUtc", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityDepositAccounts_CreatedByUserId",
                table: "SecurityDepositAccounts",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityDepositAccounts_OriginatingAgreementId_PortfolioId",
                table: "SecurityDepositAccounts",
                columns: new[] { "OriginatingAgreementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityDepositAccounts_PortfolioId",
                table: "SecurityDepositAccounts",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityDepositAccounts_TenantAccountId",
                table: "SecurityDepositAccounts",
                column: "TenantAccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityDepositAccounts_TenantAccountId_PortfolioId",
                table: "SecurityDepositAccounts",
                columns: new[] { "TenantAccountId", "PortfolioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityDepositEntries_CreatedByUserId",
                table: "SecurityDepositEntries",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityDepositEntries_LeaseAddendumId_PortfolioId",
                table: "SecurityDepositEntries",
                columns: new[] { "LeaseAddendumId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityDepositEntries_LeaseAgreementId_PortfolioId",
                table: "SecurityDepositEntries",
                columns: new[] { "LeaseAgreementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityDepositEntries_PortfolioId_LeaseAddendumId_Effectiv~",
                table: "SecurityDepositEntries",
                columns: new[] { "PortfolioId", "LeaseAddendumId", "EffectiveOn" },
                filter: "\"LeaseAddendumId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityDepositEntries_PortfolioId_LeaseAgreementId_Effecti~",
                table: "SecurityDepositEntries",
                columns: new[] { "PortfolioId", "LeaseAgreementId", "EffectiveOn" },
                filter: "\"LeaseAgreementId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityDepositEntries_PortfolioId_SecurityDepositAccountId~",
                table: "SecurityDepositEntries",
                columns: new[] { "PortfolioId", "SecurityDepositAccountId", "EffectiveOn", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityDepositEntries_PortfolioId_TransferPublicId_EntryTy~",
                table: "SecurityDepositEntries",
                columns: new[] { "PortfolioId", "TransferPublicId", "EntryType" },
                unique: true,
                filter: "\"TransferPublicId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityDepositEntries_PublicId",
                table: "SecurityDepositEntries",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityDepositEntries_ReversesEntryId",
                table: "SecurityDepositEntries",
                column: "ReversesEntryId",
                unique: true,
                filter: "\"ReversesEntryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityDepositEntries_ReversesEntryId_SecurityDepositAccou~",
                table: "SecurityDepositEntries",
                columns: new[] { "ReversesEntryId", "SecurityDepositAccountId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityDepositEntries_SecurityDepositAccountId_BusinessKey",
                table: "SecurityDepositEntries",
                columns: new[] { "SecurityDepositAccountId", "BusinessKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityDepositEntries_SecurityDepositAccountId_PortfolioId",
                table: "SecurityDepositEntries",
                columns: new[] { "SecurityDepositAccountId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityDepositEntries_SourceStoredFileId_PortfolioId",
                table: "SecurityDepositEntries",
                columns: new[] { "SourceStoredFileId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityDepositEntries_TenantLedgerEntryId_PortfolioId",
                table: "SecurityDepositEntries",
                columns: new[] { "TenantLedgerEntryId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_SignatureAuditEvents_PortfolioId_SignatureRequestId_Occurre~",
                table: "SignatureAuditEvents",
                columns: new[] { "PortfolioId", "SignatureRequestId", "OccurredAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SignatureAuditEvents_SignatureRequestId_PortfolioId",
                table: "SignatureAuditEvents",
                columns: new[] { "SignatureRequestId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_SignatureAuditEvents_SignatureSignerId_SignatureRequestId_P~",
                table: "SignatureAuditEvents",
                columns: new[] { "SignatureSignerId", "SignatureRequestId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequests_CreatedByUserId",
                table: "SignatureRequests",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequests_ExecutedArtifactId_PortfolioId",
                table: "SignatureRequests",
                columns: new[] { "ExecutedArtifactId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequests_ExecutionClaimExpiresAtUtc_Id",
                table: "SignatureRequests",
                columns: new[] { "ExecutionClaimExpiresAtUtc", "Id" },
                filter: "\"ExecutionClaimToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequests_IssuedArtifactId_PortfolioId",
                table: "SignatureRequests",
                columns: new[] { "IssuedArtifactId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequests_LeaseAddendumId",
                table: "SignatureRequests",
                column: "LeaseAddendumId",
                unique: true,
                filter: "\"LeaseAddendumId\" IS NOT NULL AND \"Status\" IN ('Prepared','Dispatching','AwaitingSignatures','Viewed','PartiallySigned','ExecutionPending')");

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequests_LeaseAddendumId_PortfolioId",
                table: "SignatureRequests",
                columns: new[] { "LeaseAddendumId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequests_LeaseAgreementId",
                table: "SignatureRequests",
                column: "LeaseAgreementId",
                unique: true,
                filter: "\"LeaseAgreementId\" IS NOT NULL AND \"Status\" IN ('Prepared','Dispatching','AwaitingSignatures','Viewed','PartiallySigned','ExecutionPending')");

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequests_LeaseAgreementId_PortfolioId",
                table: "SignatureRequests",
                columns: new[] { "LeaseAgreementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequests_PortfolioId",
                table: "SignatureRequests",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequests_Provider_IdempotencyKey",
                table: "SignatureRequests",
                columns: new[] { "Provider", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequests_Provider_ProviderEnvelopeId",
                table: "SignatureRequests",
                columns: new[] { "Provider", "ProviderEnvelopeId" },
                unique: true,
                filter: "\"ProviderEnvelopeId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequests_PublicId",
                table: "SignatureRequests",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequests_Status_NextAttemptAtUtc_PreparedAtUtc_Id",
                table: "SignatureRequests",
                columns: new[] { "Status", "NextAttemptAtUtc", "PreparedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SignatureSigners_AddendumSignerId_PortfolioId",
                table: "SignatureSigners",
                columns: new[] { "AddendumSignerId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_SignatureSigners_AgreementSignerId_PortfolioId",
                table: "SignatureSigners",
                columns: new[] { "AgreementSignerId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_SignatureSigners_DrawnSignatureStoredFileId_PortfolioId",
                table: "SignatureSigners",
                columns: new[] { "DrawnSignatureStoredFileId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_SignatureSigners_SignatureRequestId_PortfolioId",
                table: "SignatureSigners",
                columns: new[] { "SignatureRequestId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_SignatureSigners_SignatureRequestId_SigningOrder",
                table: "SignatureSigners",
                columns: new[] { "SignatureRequestId", "SigningOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SignatureSigners_TokenHash",
                table: "SignatureSigners",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SimWorkerCommands_Status_ClaimExpiresAtUtc_CreatedRealUtc_Id",
                table: "SimWorkerCommands",
                columns: new[] { "Status", "ClaimExpiresAtUtc", "CreatedRealUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_EntityType_EntityId",
                table: "StoredFiles",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_FilePath",
                table: "StoredFiles",
                column: "FilePath");

            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_PortfolioId",
                table: "StoredFiles",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_SystemNoticeTemplateVersions_SystemKey_Version",
                table: "SystemNoticeTemplateVersions",
                columns: new[] { "SystemKey", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeamRoutingRuleRecipients_TeamRoutingRuleId_PortfolioId",
                table: "TeamRoutingRuleRecipients",
                columns: new[] { "TeamRoutingRuleId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_TeamRoutingRuleRecipients_TeamRoutingRuleId_UserId",
                table: "TeamRoutingRuleRecipients",
                columns: new[] { "TeamRoutingRuleId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeamRoutingRuleRecipients_UserId",
                table: "TeamRoutingRuleRecipients",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TeamRoutingRules_PortfolioId_Topic_PropertyId",
                table: "TeamRoutingRules",
                columns: new[] { "PortfolioId", "Topic", "PropertyId" },
                unique: true,
                filter: "\"PropertyId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TeamRoutingRules_PortfolioId_Topic_Workspace",
                table: "TeamRoutingRules",
                columns: new[] { "PortfolioId", "Topic" },
                unique: true,
                filter: "\"PropertyId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TeamRoutingRules_PropertyId",
                table: "TeamRoutingRules",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_TechnicianWorkEntries_CreatedByUserId",
                table: "TechnicianWorkEntries",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TechnicianWorkEntries_MembershipRoleAssignmentId_WorkspaceM~",
                table: "TechnicianWorkEntries",
                columns: new[] { "MembershipRoleAssignmentId", "WorkspaceMembershipId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_TechnicianWorkEntries_PortfolioId_WorkOrderId_CreatedAtUtc_~",
                table: "TechnicianWorkEntries",
                columns: new[] { "PortfolioId", "WorkOrderId", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TechnicianWorkEntries_PortfolioId_WorkspaceMembershipId_Cre~",
                table: "TechnicianWorkEntries",
                columns: new[] { "PortfolioId", "WorkspaceMembershipId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TechnicianWorkEntries_StoredFileId",
                table: "TechnicianWorkEntries",
                column: "StoredFileId");

            migrationBuilder.CreateIndex(
                name: "IX_TechnicianWorkEntries_WorkOrderId",
                table: "TechnicianWorkEntries",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_TechnicianWorkEntries_WorkOrderResponsibilityId",
                table: "TechnicianWorkEntries",
                column: "WorkOrderResponsibilityId");

            migrationBuilder.CreateIndex(
                name: "IX_TechnicianWorkEntries_WorkspaceMembershipId_PortfolioId",
                table: "TechnicianWorkEntries",
                columns: new[] { "WorkspaceMembershipId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantAccountConditionPeriods_CreatedByUserId",
                table: "TenantAccountConditionPeriods",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantAccountConditionPeriods_PortfolioId",
                table: "TenantAccountConditionPeriods",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantAccountConditionPeriods_TenantAccountId_Condition",
                table: "TenantAccountConditionPeriods",
                columns: new[] { "TenantAccountId", "Condition" },
                unique: true,
                filter: "\"EndedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TenantAccountConditionPeriods_TenantAccountId_PortfolioId",
                table: "TenantAccountConditionPeriods",
                columns: new[] { "TenantAccountId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantAccounts_CreatedByUserId",
                table: "TenantAccounts",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantAccounts_LeaseManagementId",
                table: "TenantAccounts",
                column: "LeaseManagementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantAccounts_LeaseManagementId_PortfolioId",
                table: "TenantAccounts",
                columns: new[] { "LeaseManagementId", "PortfolioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantAccounts_PortfolioId_AccountNumber",
                table: "TenantAccounts",
                columns: new[] { "PortfolioId", "AccountNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantAccounts_PublicId",
                table: "TenantAccounts",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantAutopayEnrollments_AuthorizationArtifactId_PortfolioId",
                table: "TenantAutopayEnrollments",
                columns: new[] { "AuthorizationArtifactId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantAutopayEnrollments_AuthorizingPartyId_PortfolioId",
                table: "TenantAutopayEnrollments",
                columns: new[] { "AuthorizingPartyId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantAutopayEnrollments_CreatedByUserId",
                table: "TenantAutopayEnrollments",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantAutopayEnrollments_PortfolioId",
                table: "TenantAutopayEnrollments",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantAutopayEnrollments_TenantAccountId",
                table: "TenantAutopayEnrollments",
                column: "TenantAccountId",
                unique: true,
                filter: "\"CanceledAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TenantAutopayEnrollments_TenantAccountId_PortfolioId",
                table: "TenantAutopayEnrollments",
                columns: new[] { "TenantAccountId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerAllocations_CreatedByUserId",
                table: "TenantLedgerAllocations",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerAllocations_CreditEntryId_TenantAccountId_Portf~",
                table: "TenantLedgerAllocations",
                columns: new[] { "CreditEntryId", "TenantAccountId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerAllocations_DebitEntryId_TenantAccountId_Portfo~",
                table: "TenantLedgerAllocations",
                columns: new[] { "DebitEntryId", "TenantAccountId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerAllocations_PortfolioId",
                table: "TenantLedgerAllocations",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerAllocations_ReversesAllocationId",
                table: "TenantLedgerAllocations",
                column: "ReversesAllocationId",
                unique: true,
                filter: "\"ReversesAllocationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerAllocations_ReversesAllocationId_TenantAccountI~",
                table: "TenantLedgerAllocations",
                columns: new[] { "ReversesAllocationId", "TenantAccountId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerAllocations_TenantAccountId_BusinessKey",
                table: "TenantLedgerAllocations",
                columns: new[] { "TenantAccountId", "BusinessKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerAllocations_TenantAccountId_DebitEntryId_Id",
                table: "TenantLedgerAllocations",
                columns: new[] { "TenantAccountId", "DebitEntryId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerAllocations_TenantAccountId_PortfolioId",
                table: "TenantLedgerAllocations",
                columns: new[] { "TenantAccountId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerEntries_CreatedByUserId",
                table: "TenantLedgerEntries",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerEntries_LeaseAddendumId_PortfolioId",
                table: "TenantLedgerEntries",
                columns: new[] { "LeaseAddendumId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerEntries_LeaseAgreementId_PortfolioId",
                table: "TenantLedgerEntries",
                columns: new[] { "LeaseAgreementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerEntries_PortfolioId_EntryType_EffectiveOn_Id",
                table: "TenantLedgerEntries",
                columns: new[] { "PortfolioId", "EntryType", "EffectiveOn", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerEntries_PortfolioId_LeaseAddendumId_EffectiveOn",
                table: "TenantLedgerEntries",
                columns: new[] { "PortfolioId", "LeaseAddendumId", "EffectiveOn" },
                filter: "\"LeaseAddendumId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerEntries_PortfolioId_LeaseAgreementId_EffectiveOn",
                table: "TenantLedgerEntries",
                columns: new[] { "PortfolioId", "LeaseAgreementId", "EffectiveOn" },
                filter: "\"LeaseAgreementId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerEntries_PortfolioId_TenantAccountId_DueOn_Id",
                table: "TenantLedgerEntries",
                columns: new[] { "PortfolioId", "TenantAccountId", "DueOn", "Id" },
                filter: "\"Direction\" = 'Debit'");

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerEntries_PortfolioId_TenantAccountId_EffectiveOn~",
                table: "TenantLedgerEntries",
                columns: new[] { "PortfolioId", "TenantAccountId", "EffectiveOn", "Id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerEntries_PortfolioId_TransferPublicId_EntryType",
                table: "TenantLedgerEntries",
                columns: new[] { "PortfolioId", "TransferPublicId", "EntryType" },
                unique: true,
                filter: "\"TransferPublicId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerEntries_ProviderPaymentAttemptId",
                table: "TenantLedgerEntries",
                column: "ProviderPaymentAttemptId",
                unique: true,
                filter: "\"ProviderPaymentAttemptId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerEntries_ProviderPaymentAttemptId_TenantAccountI~",
                table: "TenantLedgerEntries",
                columns: new[] { "ProviderPaymentAttemptId", "TenantAccountId", "PortfolioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerEntries_PublicId",
                table: "TenantLedgerEntries",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerEntries_ReversesEntryId",
                table: "TenantLedgerEntries",
                column: "ReversesEntryId",
                unique: true,
                filter: "\"ReversesEntryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerEntries_ReversesEntryId_TenantAccountId_Portfol~",
                table: "TenantLedgerEntries",
                columns: new[] { "ReversesEntryId", "TenantAccountId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerEntries_SourceStoredFileId_PortfolioId",
                table: "TenantLedgerEntries",
                columns: new[] { "SourceStoredFileId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerEntries_TenantAccountId_BusinessKey",
                table: "TenantLedgerEntries",
                columns: new[] { "TenantAccountId", "BusinessKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerEntries_TenantAccountId_PortfolioId",
                table: "TenantLedgerEntries",
                columns: new[] { "TenantAccountId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantNoticePolicies_PortfolioId_AutomationKey",
                table: "TenantNoticePolicies",
                columns: new[] { "PortfolioId", "AutomationKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantNoticePolicies_WorkspaceNoticeTemplateVersionId_Portf~",
                table: "TenantNoticePolicies",
                columns: new[] { "WorkspaceNoticeTemplateVersionId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantNoticeWorkItems_BusinessKey",
                table: "TenantNoticeWorkItems",
                column: "BusinessKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantNoticeWorkItems_PortfolioId_TenantLedgerEntryId",
                table: "TenantNoticeWorkItems",
                columns: new[] { "PortfolioId", "TenantLedgerEntryId" },
                filter: "\"TenantLedgerEntryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TenantNoticeWorkItems_RecipientLeaseManagementPartyId_Lease~",
                table: "TenantNoticeWorkItems",
                columns: new[] { "RecipientLeaseManagementPartyId", "LeaseManagementId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantNoticeWorkItems_Status_DueAtUtc_ClaimExpiresAtUtc_Id",
                table: "TenantNoticeWorkItems",
                columns: new[] { "Status", "DueAtUtc", "ClaimExpiresAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantNoticeWorkItems_TenantNoticePolicyId_PortfolioId",
                table: "TenantNoticeWorkItems",
                columns: new[] { "TenantNoticePolicyId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantPaymentAttempts_CreatedByUserId",
                table: "TenantPaymentAttempts",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantPaymentAttempts_PortfolioId_State_NextAttemptAtUtc_Cl~",
                table: "TenantPaymentAttempts",
                columns: new[] { "PortfolioId", "State", "NextAttemptAtUtc", "ClaimExpiresAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantPaymentAttempts_Provider_IdempotencyKey",
                table: "TenantPaymentAttempts",
                columns: new[] { "Provider", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantPaymentAttempts_Provider_ProviderObjectId",
                table: "TenantPaymentAttempts",
                columns: new[] { "Provider", "ProviderObjectId" },
                unique: true,
                filter: "\"ProviderObjectId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TenantPaymentAttempts_PublicId",
                table: "TenantPaymentAttempts",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantPaymentAttempts_RefundsPaymentAttemptId",
                table: "TenantPaymentAttempts",
                column: "RefundsPaymentAttemptId",
                unique: true,
                filter: "\"RefundsPaymentAttemptId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TenantPaymentAttempts_RefundsPaymentAttemptId_TenantAccount~",
                table: "TenantPaymentAttempts",
                columns: new[] { "RefundsPaymentAttemptId", "TenantAccountId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantPaymentAttempts_TenantAccountId_PortfolioId",
                table: "TenantPaymentAttempts",
                columns: new[] { "TenantAccountId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_PortfolioId",
                table: "Tenants",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantUserAccesses_AccessContextId_ApplicationUserId_Portfo~",
                table: "TenantUserAccesses",
                columns: new[] { "AccessContextId", "ApplicationUserId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantUserAccesses_ApplicationUserId_LeaseManagementPartyId",
                table: "TenantUserAccesses",
                columns: new[] { "ApplicationUserId", "LeaseManagementPartyId" },
                unique: true,
                filter: "\"RevokedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TenantUserAccesses_GrantedByUserId",
                table: "TenantUserAccesses",
                column: "GrantedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantUserAccesses_LeaseManagementPartyId_PortfolioId",
                table: "TenantUserAccesses",
                columns: new[] { "LeaseManagementPartyId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantUserAccesses_PortfolioId_AccessContextId_RevokedAtUtc~",
                table: "TenantUserAccesses",
                columns: new[] { "PortfolioId", "AccessContextId", "RevokedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantUserAccesses_PublicId",
                table: "TenantUserAccesses",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantUserAccesses_RevokedByUserId",
                table: "TenantUserAccesses",
                column: "RevokedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UnitOperationalPeriods_CreatedByUserId",
                table: "UnitOperationalPeriods",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UnitOperationalPeriods_PortfolioId_UnitId_StartedAtUtc_Id",
                table: "UnitOperationalPeriods",
                columns: new[] { "PortfolioId", "UnitId", "StartedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_UnitOperationalPeriods_PropertyId_PortfolioId",
                table: "UnitOperationalPeriods",
                columns: new[] { "PropertyId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_UnitOperationalPeriods_SourceLeaseManagementId_UnitId_Portf~",
                table: "UnitOperationalPeriods",
                columns: new[] { "SourceLeaseManagementId", "UnitId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_UnitOperationalPeriods_UnitId_PropertyId_PortfolioId",
                table: "UnitOperationalPeriods",
                columns: new[] { "UnitId", "PropertyId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_UnitOperationalPeriods_UnitId_Type",
                table: "UnitOperationalPeriods",
                columns: new[] { "UnitId", "Type" },
                unique: true,
                filter: "\"EndedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Units_PortfolioId",
                table: "Units",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_Units_PropertyId_PortfolioId",
                table: "Units",
                columns: new[] { "PropertyId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserAlertPreferences_PortfolioId_UserId",
                table: "UserAlertPreferences",
                columns: new[] { "PortfolioId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserAlertPreferences_UserId",
                table: "UserAlertPreferences",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorDispatches_PortfolioId",
                table: "VendorDispatches",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorDispatches_Status",
                table: "VendorDispatches",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_VendorDispatches_VendorId",
                table: "VendorDispatches",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorDispatches_VendorId_Status_DispatchedAtUtc",
                table: "VendorDispatches",
                columns: new[] { "VendorId", "Status", "DispatchedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_VendorDispatches_WorkOrderId",
                table: "VendorDispatches",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorRatings_PortfolioId",
                table: "VendorRatings",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorRatings_VendorId",
                table: "VendorRatings",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorRatings_WorkOrderId",
                table: "VendorRatings",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Vendors_NormalizedPhone",
                table: "Vendors",
                column: "NormalizedPhone");

            migrationBuilder.CreateIndex(
                name: "IX_Vendors_PortfolioId",
                table: "Vendors",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderResponsibilities_AssignedByAccessContextId_Assigne~",
                table: "WorkOrderResponsibilities",
                columns: new[] { "AssignedByAccessContextId", "AssignedByUserId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderResponsibilities_AssignedByUserId",
                table: "WorkOrderResponsibilities",
                column: "AssignedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderResponsibilities_EndedByAccessContextId_EndedByUse~",
                table: "WorkOrderResponsibilities",
                columns: new[] { "EndedByAccessContextId", "EndedByUserId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderResponsibilities_EndedByUserId",
                table: "WorkOrderResponsibilities",
                column: "EndedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderResponsibilities_MembershipRoleAssignmentId_Worksp~",
                table: "WorkOrderResponsibilities",
                columns: new[] { "MembershipRoleAssignmentId", "WorkspaceMembershipId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderResponsibilities_PortfolioId_WorkspaceMembershipId~",
                table: "WorkOrderResponsibilities",
                columns: new[] { "PortfolioId", "WorkspaceMembershipId", "EffectiveFromUtc", "EffectiveToUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderResponsibilities_WorkOrderId_PropertyId_PortfolioId",
                table: "WorkOrderResponsibilities",
                columns: new[] { "WorkOrderId", "PropertyId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderResponsibilities_WorkspaceMembershipId_PortfolioId",
                table: "WorkOrderResponsibilities",
                columns: new[] { "WorkspaceMembershipId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "UX_WorkOrderResponsibilities_CurrentMember",
                table: "WorkOrderResponsibilities",
                columns: new[] { "PortfolioId", "WorkOrderId", "WorkspaceMembershipId" },
                unique: true,
                filter: "\"EffectiveToUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_WorkOrderResponsibilities_CurrentPrimary",
                table: "WorkOrderResponsibilities",
                columns: new[] { "PortfolioId", "WorkOrderId", "Kind" },
                unique: true,
                filter: "\"EffectiveToUtc\" IS NULL AND \"Kind\" = 'Primary'");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_LeaseManagementId",
                table: "WorkOrders",
                column: "LeaseManagementId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_PortfolioId",
                table: "WorkOrders",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_Priority",
                table: "WorkOrders",
                column: "Priority");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_PropertyId",
                table: "WorkOrders",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_RecurringMaintenanceTaskId",
                table: "WorkOrders",
                column: "RecurringMaintenanceTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_Status",
                table: "WorkOrders",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_TenantId",
                table: "WorkOrders",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_UnitId",
                table: "WorkOrders",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_VendorId",
                table: "WorkOrders",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderStatusEvents_PortfolioId",
                table: "WorkOrderStatusEvents",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderStatusEvents_WorkOrderId",
                table: "WorkOrderStatusEvents",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceAccessContexts_PortfolioId_Status",
                table: "WorkspaceAccessContexts",
                columns: new[] { "PortfolioId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceAccessContexts_UserId_PortfolioId",
                table: "WorkspaceAccessContexts",
                columns: new[] { "UserId", "PortfolioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceInvitations_InvitedByUserId",
                table: "WorkspaceInvitations",
                column: "InvitedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceInvitations_InvitedUserId",
                table: "WorkspaceInvitations",
                column: "InvitedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceInvitations_PortfolioId",
                table: "WorkspaceInvitations",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceInvitations_TokenHash",
                table: "WorkspaceInvitations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceInvitations_WorkspaceMembershipId_AcceptedAtUtc_Re~",
                table: "WorkspaceInvitations",
                columns: new[] { "WorkspaceMembershipId", "AcceptedAtUtc", "RevokedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceInvitations_WorkspaceMembershipId_PortfolioId",
                table: "WorkspaceInvitations",
                columns: new[] { "WorkspaceMembershipId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceMemberships_AccessContextId",
                table: "WorkspaceMemberships",
                column: "AccessContextId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceMemberships_AccessContextId_PortfolioId",
                table: "WorkspaceMemberships",
                columns: new[] { "AccessContextId", "PortfolioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceMemberships_PortfolioId_Status_EffectiveFromUtc",
                table: "WorkspaceMemberships",
                columns: new[] { "PortfolioId", "Status", "EffectiveFromUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceNoticeTemplateVersions_BasedOnSystemTemplateVersio~",
                table: "WorkspaceNoticeTemplateVersions",
                column: "BasedOnSystemTemplateVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceNoticeTemplateVersions_PortfolioId_SystemKey_Versi~",
                table: "WorkspaceNoticeTemplateVersions",
                columns: new[] { "PortfolioId", "SystemKey", "Version" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_BankTransactions_Expenses_MatchedExpenseId",
                table: "BankTransactions",
                column: "MatchedExpenseId",
                principalTable: "Expenses",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_CapitalAssets_Expenses_SourceExpenseId",
                table: "CapitalAssets",
                column: "SourceExpenseId",
                principalTable: "Expenses",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_TenantNoticeWorkItems_LeaseManagementParties_RecipientLease~",
                table: "TenantNoticeWorkItems",
                columns: new[] { "RecipientLeaseManagementPartyId", "LeaseManagementId", "PortfolioId" },
                principalTable: "LeaseManagementParties",
                principalColumns: new[] { "Id", "LeaseManagementId", "PortfolioId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "IX_RentalApplications_PortfolioId_Email_Open_CI"
                ON "RentalApplications" ("PortfolioId", lower(trim("Email")))
                WHERE "DeletedAt" IS NULL
                  AND "Email" IS NOT NULL
                  AND "Status" IN ('Submitted', 'UnderReview', 'Approved');
                """);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "IX_Units_PropertyId_UnitNumber_CI"
                ON "Units" ("PropertyId", lower(trim("UnitNumber")))
                WHERE "DeletedAt" IS NULL;
                """);

            // The effective clock is global and has one fixed row. Keep the seed idempotent so the
            // same clean baseline can be exercised repeatedly against disposable databases.
            migrationBuilder.Sql(
                "INSERT INTO \"SimulationClocks\" " +
                "(\"Id\", \"Mode\", \"SimAnchorUtc\", \"RealAnchorUtc\", \"TimeZoneId\", \"UpdatedAtRealUtc\") " +
                "VALUES (1, 'Real', TIMESTAMPTZ '2000-01-01 00:00:00+00', TIMESTAMPTZ '2000-01-01 00:00:00+00', " +
                "NULL, TIMESTAMPTZ '2000-01-01 00:00:00+00') " +
                "ON CONFLICT (\"Id\") DO NOTHING;");

            foreach (var statement in LeaseLegalSchemaSql.CreateStatements)
                migrationBuilder.Sql(statement);

            foreach (var statement in TenantAccountPostgreSqlContract.CreateStatements)
                migrationBuilder.Sql(statement);

            foreach (var statement in FoundationBaselinePostgreSql.CreateStatements)
                migrationBuilder.Sql(statement);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var statement in FoundationBaselinePostgreSql.DropStatements)
                migrationBuilder.Sql(statement);

            foreach (var statement in TenantAccountPostgreSqlContract.DropStatements)
                migrationBuilder.Sql(statement);

            foreach (var statement in LeaseLegalSchemaSql.DropStatements)
                migrationBuilder.Sql(statement);

            migrationBuilder.DropForeignKey(
                name: "FK_CapitalAssets_Portfolios_PortfolioId",
                table: "CapitalAssets");

            migrationBuilder.DropForeignKey(
                name: "FK_Expenses_Portfolios_PortfolioId",
                table: "Expenses");

            migrationBuilder.DropForeignKey(
                name: "FK_LeaseManagements_Portfolios_PortfolioId",
                table: "LeaseManagements");

            migrationBuilder.DropForeignKey(
                name: "FK_OwnerEntities_Portfolios_PortfolioId",
                table: "OwnerEntities");

            migrationBuilder.DropForeignKey(
                name: "FK_Owners_Portfolios_PortfolioId",
                table: "Owners");

            migrationBuilder.DropForeignKey(
                name: "FK_Properties_Portfolios_PortfolioId",
                table: "Properties");

            migrationBuilder.DropForeignKey(
                name: "FK_RecurringExpenses_Portfolios_PortfolioId",
                table: "RecurringExpenses");

            migrationBuilder.DropForeignKey(
                name: "FK_RecurringMaintenanceTasks_Portfolios_PortfolioId",
                table: "RecurringMaintenanceTasks");

            migrationBuilder.DropForeignKey(
                name: "FK_Tenants_Portfolios_PortfolioId",
                table: "Tenants");

            migrationBuilder.DropForeignKey(
                name: "FK_Vendors_Portfolios_PortfolioId",
                table: "Vendors");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkOrders_Portfolios_PortfolioId",
                table: "WorkOrders");

            migrationBuilder.DropForeignKey(
                name: "FK_LeaseManagements_AspNetUsers_CreatedByUserId",
                table: "LeaseManagements");

            migrationBuilder.DropForeignKey(
                name: "FK_LeaseManagements_AspNetUsers_EndingDispositionDecidedByUser~",
                table: "LeaseManagements");

            migrationBuilder.DropForeignKey(
                name: "FK_LeaseManagements_AspNetUsers_PossessionAgreementExceptionAu~",
                table: "LeaseManagements");

            migrationBuilder.DropForeignKey(
                name: "FK_CapitalAssets_Properties_PropertyId",
                table: "CapitalAssets");

            migrationBuilder.DropForeignKey(
                name: "FK_Expenses_Properties_PropertyId",
                table: "Expenses");

            migrationBuilder.DropForeignKey(
                name: "FK_LeaseManagements_Properties_PropertyId_PortfolioId",
                table: "LeaseManagements");

            migrationBuilder.DropForeignKey(
                name: "FK_RecurringExpenses_Properties_PropertyId",
                table: "RecurringExpenses");

            migrationBuilder.DropForeignKey(
                name: "FK_RecurringMaintenanceTasks_Properties_PropertyId",
                table: "RecurringMaintenanceTasks");

            migrationBuilder.DropForeignKey(
                name: "FK_Units_Properties_PropertyId_PortfolioId",
                table: "Units");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkOrders_Properties_PropertyId",
                table: "WorkOrders");

            migrationBuilder.DropForeignKey(
                name: "FK_CapitalAssets_Units_UnitId",
                table: "CapitalAssets");

            migrationBuilder.DropForeignKey(
                name: "FK_Expenses_Units_UnitId",
                table: "Expenses");

            migrationBuilder.DropForeignKey(
                name: "FK_LeaseManagements_Units_UnitId_PropertyId_PortfolioId",
                table: "LeaseManagements");

            migrationBuilder.DropForeignKey(
                name: "FK_RecurringExpenses_Units_UnitId",
                table: "RecurringExpenses");

            migrationBuilder.DropForeignKey(
                name: "FK_RecurringMaintenanceTasks_Units_UnitId",
                table: "RecurringMaintenanceTasks");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkOrders_Units_UnitId",
                table: "WorkOrders");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkOrders_LeaseManagements_LeaseManagementId",
                table: "WorkOrders");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkOrders_Tenants_TenantId",
                table: "WorkOrders");

            migrationBuilder.DropForeignKey(
                name: "FK_CapitalAssets_Expenses_SourceExpenseId",
                table: "CapitalAssets");

            migrationBuilder.DropTable(
                name: "AccountingMappingPromotionJobs");

            migrationBuilder.DropTable(
                name: "AccountingSyncMaps");

            migrationBuilder.DropTable(
                name: "AdverseActionNotices");

            migrationBuilder.DropTable(
                name: "ApplicantScreeningMilestones");

            migrationBuilder.DropTable(
                name: "ApplicationFinancialEntries");

            migrationBuilder.DropTable(
                name: "Appointments");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "AtomicAuditLogs");

            migrationBuilder.DropTable(
                name: "AtomicCommandReceipts");

            migrationBuilder.DropTable(
                name: "AuthSessionRefreshCredentials");

            migrationBuilder.DropTable(
                name: "AutomationSettings");

            migrationBuilder.DropTable(
                name: "BankTransactions");

            migrationBuilder.DropTable(
                name: "DeviceTokens");

            migrationBuilder.DropTable(
                name: "DocumentTemplateFields");

            migrationBuilder.DropTable(
                name: "EngineWorkerHeartbeats");

            migrationBuilder.DropTable(
                name: "EvictionCaseEvents");

            migrationBuilder.DropTable(
                name: "EvictionCaseRespondents");

            migrationBuilder.DropTable(
                name: "ExpenseLineItems");

            migrationBuilder.DropTable(
                name: "ExternalListingSignals");

            migrationBuilder.DropTable(
                name: "InspectionItems");

            migrationBuilder.DropTable(
                name: "InspectionTemplateItems");

            migrationBuilder.DropTable(
                name: "LeaseAddendumFinancialEffects");

            migrationBuilder.DropTable(
                name: "LeaseRenewalAddendumDecisions");

            migrationBuilder.DropTable(
                name: "ListingPhotos");

            migrationBuilder.DropTable(
                name: "LoanPayments");

            migrationBuilder.DropTable(
                name: "LoginContextSelectionChallenges");

            migrationBuilder.DropTable(
                name: "MembershipRoleAssignmentProperties");

            migrationBuilder.DropTable(
                name: "MessagingProviderSettings");

            migrationBuilder.DropTable(
                name: "NoticeDeliveryEvidence");

            migrationBuilder.DropTable(
                name: "NoticeDrafts");

            migrationBuilder.DropTable(
                name: "NotificationReadStates");

            migrationBuilder.DropTable(
                name: "OAuthStates");

            migrationBuilder.DropTable(
                name: "OwnerDistributions");

            migrationBuilder.DropTable(
                name: "OwnerUserAccesses");

            migrationBuilder.DropTable(
                name: "PendingFileUploads");

            migrationBuilder.DropTable(
                name: "PlaidTokenExchangeAttempts");

            migrationBuilder.DropTable(
                name: "PropertyDispositions");

            migrationBuilder.DropTable(
                name: "ProviderInboxEvents");

            migrationBuilder.DropTable(
                name: "QueuedJobs");

            migrationBuilder.DropTable(
                name: "RoleProfileCapabilities");

            migrationBuilder.DropTable(
                name: "ScanDrafts");

            migrationBuilder.DropTable(
                name: "SecurityDepositEntries");

            migrationBuilder.DropTable(
                name: "SignatureAuditEvents");

            migrationBuilder.DropTable(
                name: "SimulationClocks");

            migrationBuilder.DropTable(
                name: "SimWorkerCommands");

            migrationBuilder.DropTable(
                name: "TeamRoutingRuleRecipients");

            migrationBuilder.DropTable(
                name: "TechnicianWorkEntries");

            migrationBuilder.DropTable(
                name: "TenantAccountConditionPeriods");

            migrationBuilder.DropTable(
                name: "TenantAutopayEnrollments");

            migrationBuilder.DropTable(
                name: "TenantLedgerAllocations");

            migrationBuilder.DropTable(
                name: "TenantNoticeWorkItems");

            migrationBuilder.DropTable(
                name: "TenantUserAccesses");

            migrationBuilder.DropTable(
                name: "UnitOperationalPeriods");

            migrationBuilder.DropTable(
                name: "UserAlertPreferences");

            migrationBuilder.DropTable(
                name: "VendorDispatches");

            migrationBuilder.DropTable(
                name: "VendorRatings");

            migrationBuilder.DropTable(
                name: "WorkOrderStatusEvents");

            migrationBuilder.DropTable(
                name: "WorkspaceInvitations");

            migrationBuilder.DropTable(
                name: "AccountingEntityMappings");

            migrationBuilder.DropTable(
                name: "ApplicantScreenings");

            migrationBuilder.DropTable(
                name: "ApplicationFinancialAccounts");

            migrationBuilder.DropTable(
                name: "AuthSessionRefreshTokenFamilies");

            migrationBuilder.DropTable(
                name: "EvictionCases");

            migrationBuilder.DropTable(
                name: "ListingPublications");

            migrationBuilder.DropTable(
                name: "Inspections");

            migrationBuilder.DropTable(
                name: "InspectionTemplates");

            migrationBuilder.DropTable(
                name: "Loans");

            migrationBuilder.DropTable(
                name: "ConversationMessages");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "RenderedNotices");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "BankConnections");

            migrationBuilder.DropTable(
                name: "CapabilityDefinitions");

            migrationBuilder.DropTable(
                name: "ScanBatches");

            migrationBuilder.DropTable(
                name: "SecurityDepositAccounts");

            migrationBuilder.DropTable(
                name: "SignatureSigners");

            migrationBuilder.DropTable(
                name: "TeamRoutingRules");

            migrationBuilder.DropTable(
                name: "WorkOrderResponsibilities");

            migrationBuilder.DropTable(
                name: "TenantLedgerEntries");

            migrationBuilder.DropTable(
                name: "TenantNoticePolicies");

            migrationBuilder.DropTable(
                name: "AccountingConnections");

            migrationBuilder.DropTable(
                name: "RentalApplications");

            migrationBuilder.DropTable(
                name: "AuthSessions");

            migrationBuilder.DropTable(
                name: "RentalListings");

            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropTable(
                name: "LeaseAddendumSigners");

            migrationBuilder.DropTable(
                name: "LeaseAgreementSigners");

            migrationBuilder.DropTable(
                name: "SignatureRequests");

            migrationBuilder.DropTable(
                name: "MembershipRoleAssignments");

            migrationBuilder.DropTable(
                name: "TenantPaymentAttempts");

            migrationBuilder.DropTable(
                name: "WorkspaceNoticeTemplateVersions");

            migrationBuilder.DropTable(
                name: "LeaseManagementParties");

            migrationBuilder.DropTable(
                name: "LeaseAddenda");

            migrationBuilder.DropTable(
                name: "RoleProfiles");

            migrationBuilder.DropTable(
                name: "WorkspaceMemberships");

            migrationBuilder.DropTable(
                name: "TenantAccounts");

            migrationBuilder.DropTable(
                name: "SystemNoticeTemplateVersions");

            migrationBuilder.DropTable(
                name: "LeaseAgreements");

            migrationBuilder.DropTable(
                name: "WorkspaceAccessContexts");

            migrationBuilder.DropTable(
                name: "LegalDocumentSourceVersions");

            migrationBuilder.DropTable(
                name: "DocumentTemplates");

            migrationBuilder.DropTable(
                name: "LegalDocumentArtifacts");

            migrationBuilder.DropTable(
                name: "StoredFiles");

            migrationBuilder.DropTable(
                name: "Portfolios");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "Properties");

            migrationBuilder.DropTable(
                name: "OwnerEntities");

            migrationBuilder.DropTable(
                name: "Owners");

            migrationBuilder.DropTable(
                name: "Units");

            migrationBuilder.DropTable(
                name: "LeaseManagements");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropTable(
                name: "Expenses");

            migrationBuilder.DropTable(
                name: "CapitalAssets");

            migrationBuilder.DropTable(
                name: "RecurringExpenses");

            migrationBuilder.DropTable(
                name: "WorkOrders");

            migrationBuilder.DropTable(
                name: "RecurringMaintenanceTasks");

            migrationBuilder.DropTable(
                name: "Vendors");
        }
    }
}
