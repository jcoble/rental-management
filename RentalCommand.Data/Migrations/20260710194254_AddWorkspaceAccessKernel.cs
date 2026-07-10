using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceAccessKernel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Properties_Id_PortfolioId",
                table: "Properties",
                columns: new[] { "Id", "PortfolioId" });

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
                name: "AuthSessionRefreshCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RefreshTokenFamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReplacedByCredentialId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReuseDetectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthSessionRefreshCredentials", x => x.Id);
                    table.UniqueConstraint("AK_AuthSessionRefreshCredentials_Id_RefreshTokenFamilyId", x => new { x.Id, x.RefreshTokenFamilyId });
                    table.CheckConstraint("CK_AuthSessionRefreshCredentials_ConsumedFacts", "\"ConsumedAtUtc\" IS NULL OR \"ConsumedAtUtc\" >= \"IssuedAtUtc\"");
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
                    { 35, "Workspace", "Perform destructive workspace or account actions.", "account.destructive-actions" }
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
                    { 14, 3 },
                    { 15, 3 },
                    { 16, 3 },
                    { 17, 3 },
                    { 18, 3 },
                    { 19, 3 },
                    { 20, 3 },
                    { 21, 4 },
                    { 22, 4 },
                    { 23, 4 },
                    { 24, 4 }
                });

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
                name: "IX_CapabilityDefinitions_Key",
                table: "CapabilityDefinitions",
                column: "Key",
                unique: true);

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
                name: "IX_RoleProfileCapabilities_CapabilityDefinitionId",
                table: "RoleProfileCapabilities",
                column: "CapabilityDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleProfiles_Key",
                table: "RoleProfiles",
                column: "Key",
                unique: true);

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthSessionRefreshCredentials");

            migrationBuilder.DropTable(
                name: "MembershipRoleAssignmentProperties");

            migrationBuilder.DropTable(
                name: "RoleProfileCapabilities");

            migrationBuilder.DropTable(
                name: "AuthSessionRefreshTokenFamilies");

            migrationBuilder.DropTable(
                name: "MembershipRoleAssignments");

            migrationBuilder.DropTable(
                name: "CapabilityDefinitions");

            migrationBuilder.DropTable(
                name: "AuthSessions");

            migrationBuilder.DropTable(
                name: "RoleProfiles");

            migrationBuilder.DropTable(
                name: "WorkspaceMemberships");

            migrationBuilder.DropTable(
                name: "WorkspaceAccessContexts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Properties_Id_PortfolioId",
                table: "Properties");
        }
    }
}
