using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data;

/// <summary>
/// Primary application + Identity database context. Uses an <c>int</c> Identity key
/// (<see cref="IdentityRole{Int32}"/>) so the auth user PK matches every domain entity.
/// </summary>
public class RentalCommandDbContext : IdentityDbContext<ApplicationUser, IdentityRole<int>, int>
{
    public RentalCommandDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }

    // Domain entities
    public DbSet<Portfolio> Portfolios => Set<Portfolio>();
    public DbSet<Owner> Owners => Set<Owner>();
    public DbSet<OwnerEntity> OwnerEntities => Set<OwnerEntity>();
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Lease> Leases => Set<Lease>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<ExpenseLineItem> ExpenseLineItems => Set<ExpenseLineItem>();
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<VendorDispatch> VendorDispatches => Set<VendorDispatch>();
    public DbSet<VendorRating> VendorRatings => Set<VendorRating>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<WorkOrderStatusEvent> WorkOrderStatusEvents => Set<WorkOrderStatusEvent>();
    public DbSet<RecurringMaintenanceTask> RecurringMaintenanceTasks => Set<RecurringMaintenanceTask>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<Inspection> Inspections => Set<Inspection>();
    public DbSet<InspectionItem> InspectionItems => Set<InspectionItem>();
    public DbSet<InspectionTemplate> InspectionTemplates => Set<InspectionTemplate>();
    public DbSet<InspectionTemplateItem> InspectionTemplateItems => Set<InspectionTemplateItem>();
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<PortalMessage> PortalMessages => Set<PortalMessage>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationSettings> NotificationSettings => Set<NotificationSettings>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<BankConnection> BankConnections => Set<BankConnection>();
    public DbSet<BankTransaction> BankTransactions => Set<BankTransaction>();
    public DbSet<NoticeDraft> NoticeDrafts => Set<NoticeDraft>();
    public DbSet<RentalApplication> RentalApplications => Set<RentalApplication>();
    public DbSet<ScreeningResult> ScreeningResults => Set<ScreeningResult>();
    public DbSet<AdverseActionNotice> AdverseActionNotices => Set<AdverseActionNotice>();

    // Native e-signature (envelope + per-signer tokens + append-only audit trail)
    public DbSet<SignatureRequest> SignatureRequests => Set<SignatureRequest>();
    public DbSet<SignatureSigner> SignatureSigners => Set<SignatureSigner>();
    public DbSet<SignatureAuditEvent> SignatureAuditEvents => Set<SignatureAuditEvent>();

    // Auth + audit + infrastructure entities (Task 3)
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<QueuedJob> QueuedJobs => Set<QueuedJob>();
    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();
    public DbSet<ScanDraft> ScanDrafts => Set<ScanDraft>();
    public DbSet<ScanBatch> ScanBatches => Set<ScanBatch>();
    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();
    public DbSet<SecurityDepositHolding> SecurityDepositHoldings => Set<SecurityDepositHolding>();
    public DbSet<OpeningBalance> OpeningBalances => Set<OpeningBalance>();

    // Stripe payment groundwork
    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();
    public DbSet<StripeWebhookEvent> StripeWebhookEvents => Set<StripeWebhookEvent>();
    public DbSet<AutopayEnrollment> AutopayEnrollments => Set<AutopayEnrollment>();

    // Engine resilience — worker heartbeats written by each background worker every poll cycle
    public DbSet<EngineWorkerHeartbeat> EngineWorkerHeartbeats => Set<EngineWorkerHeartbeat>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Configures the ASP.NET Identity schema (AspNetUsers/Roles/etc.) with int keys.
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(e => e.DisplayName).HasMaxLength(200);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.OwnerEntity)
                .WithMany()
                .HasForeignKey(e => e.OwnerEntityId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Token).IsRequired().HasMaxLength(256);
            entity.Property(e => e.TokenHash).IsRequired().HasMaxLength(128);
            entity.Property(e => e.IpAddress).HasMaxLength(64);
            entity.Property(e => e.UserAgent).HasMaxLength(512);
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.ExpiresAt);
            entity.HasOne(e => e.User)
                .WithMany(u => u.RefreshTokens)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ActorLabel).HasMaxLength(120);
            entity.Property(e => e.EntityType).IsRequired().HasMaxLength(120);
            entity.Property(e => e.ChangeReason).HasMaxLength(1000);
            entity.Property(e => e.IpAddress).HasMaxLength(64);
            // Append-only JSON payloads (Postgres jsonb).
            entity.Property(e => e.OldValues).HasColumnType("jsonb");
            entity.Property(e => e.NewValues).HasColumnType("jsonb");
            entity.Property(e => e.Operation).HasConversion<int>();
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => new { e.EntityType, e.EntityId });
            entity.HasIndex(e => e.Timestamp);
            // Composite index for the paged viewer query (scope by portfolio, newest-first).
            entity.HasIndex(e => new { e.PortfolioId, e.Timestamp });
            entity.HasOne(e => e.User)
                .WithMany(u => u.AuditLogs)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<OwnerEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.TaxId).HasMaxLength(64);
            entity.Property(e => e.Address).HasMaxLength(500);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.Email).HasMaxLength(200);
            entity.Property(e => e.OwnerEntityType).HasConversion<int>();
            entity.HasIndex(e => e.PortfolioId);
            // Find the auto-created self-owner quickly (and assert at most one per portfolio in code).
            entity.HasIndex(e => new { e.PortfolioId, e.IsPrimary });
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.OwnerEntities)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StoredFile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(260);
            entity.Property(e => e.FilePath).IsRequired().HasMaxLength(1024);
            entity.Property(e => e.ContentType).IsRequired().HasMaxLength(120);
            entity.Property(e => e.EntityType).HasMaxLength(120);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => new { e.EntityType, e.EntityId });
            entity.HasQueryFilter(e => e.DeletedAt == null);
        });

        modelBuilder.Entity<ScanDraft>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FilePath).IsRequired().HasMaxLength(1024);
            entity.Property(e => e.ThumbnailPath).HasMaxLength(1024);
            entity.Property(e => e.TargetEntityType).IsRequired().HasMaxLength(120);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ModelId).HasMaxLength(120);
            entity.Property(e => e.FailureReason).HasMaxLength(500);
            entity.Property(e => e.ReviewedBy).HasMaxLength(200);
            entity.Property(e => e.CostUsd).HasPrecision(18, 4);
            entity.Property(e => e.ExtractedFields).HasColumnType("jsonb");
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.BatchId);
            // The owning batch is optional (single-file scans carry null). SetNull rather than Cascade
            // so a draft (and the record it created) survives if a batch row is ever removed.
            entity.HasOne(e => e.Batch)
                .WithMany()
                .HasForeignKey(e => e.BatchId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ScanBatch>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.TargetEntityType).IsRequired().HasMaxLength(120);
            // Stored as the string enum name to match the app-wide string-enum convention.
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(40);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.Status);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeviceToken>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Token).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Platform).IsRequired().HasMaxLength(20);
            // Each physical device token must be unique across all rows.
            entity.HasIndex(e => e.Token).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.UserId });
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Notification>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Type).IsRequired().HasMaxLength(80);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Message).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.Severity).IsRequired().HasMaxLength(20);
            entity.Property(e => e.ActionUrl).HasMaxLength(500);
            entity.Property(e => e.RelatedEntityType).HasMaxLength(120);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.IsRead);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NotificationSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SignalWireProjectIdCipherText).HasMaxLength(4000);
            entity.Property(e => e.SignalWireTokenCipherText).HasMaxLength(4000);
            entity.Property(e => e.SignalWireSpaceUrlCipherText).HasMaxLength(4000);
            entity.Property(e => e.SignalWireFromNumberCipherText).HasMaxLength(4000);
            // Pluggable SMS provider (BYO): provider name + generic encrypted credential slots.
            entity.Property(e => e.SmsProvider).HasMaxLength(32);
            entity.Property(e => e.SmsCredentialACipherText).HasMaxLength(4000);
            entity.Property(e => e.SmsCredentialBCipherText).HasMaxLength(4000);
            entity.Property(e => e.SmsCredentialCCipherText).HasMaxLength(4000);
            entity.Property(e => e.SmsFromNumberCipherText).HasMaxLength(4000);
            entity.Property(e => e.DailyBriefingSmsRecipientsCipherText).HasMaxLength(4000);
            entity.Property(e => e.DailyBriefingEmailRecipientsCipherText).HasMaxLength(4000);
            entity.Property(e => e.EnableLeaseExpiryReminders).HasDefaultValue(true);
            entity.Property(e => e.RentChargeLeadDays).HasDefaultValue(5);
            entity.Property(e => e.LateFeeGraceDays).HasDefaultValue(5);
            entity.Property(e => e.LeaseExpiryReminderDays).HasDefaultValue(60);
            // Per-portfolio now (was a single global row). One settings row per portfolio.
            entity.HasIndex(e => e.PortfolioId).IsUnique();
        });

        modelBuilder.Entity<NotificationPreference>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Stored as the string enum name to match the app-wide string-enum convention and to keep
            // the unique key stable if the enum is ever reordered.
            entity.Property(e => e.NotificationType).HasConversion<string>().HasMaxLength(40);
            entity.HasIndex(e => new { e.PortfolioId, e.NotificationType }).IsUnique();
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BankConnection>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).IsRequired().HasMaxLength(40);
            entity.Property(e => e.InstitutionName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.AccountName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.AccountMask).HasMaxLength(20);
            entity.Property(e => e.AccountType).HasMaxLength(80);
            entity.Property(e => e.AccountSubtype).HasMaxLength(80);
            entity.Property(e => e.ExternalItemIdCipherText).HasMaxLength(4000);
            entity.Property(e => e.ExternalAccountIdCipherText).HasMaxLength(4000);
            entity.Property(e => e.ExternalAccessTokenCipherText).HasMaxLength(4000);
            entity.Property(e => e.SyncCursorCipherText).HasMaxLength(4000);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(40);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.Status);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BankTransaction>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ProviderTransactionId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(500);
            entity.Property(e => e.MerchantName).HasMaxLength(200);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.IsoCurrencyCode).IsRequired().HasMaxLength(8);
            entity.Property(e => e.Category).HasMaxLength(200);
            entity.Property(e => e.MatchStatus).IsRequired().HasMaxLength(40);
            entity.Property(e => e.MatchConfidence).HasPrecision(5, 2);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.Property(e => e.RawData).HasColumnType("jsonb");
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.BankConnectionId);
            entity.HasIndex(e => e.PostedAt);
            entity.HasIndex(e => e.MatchStatus);
            entity.HasIndex(e => new { e.BankConnectionId, e.ProviderTransactionId }).IsUnique();
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.BankConnection)
                .WithMany(c => c.Transactions)
                .HasForeignKey(e => e.BankConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.MatchedPayment)
                .WithMany()
                .HasForeignKey(e => e.MatchedPaymentId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.MatchedExpense)
                .WithMany()
                .HasForeignKey(e => e.MatchedExpenseId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<NoticeDraft>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.NoticeType).IsRequired().HasMaxLength(80);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(40);
            entity.Property(e => e.Subject).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Body).IsRequired().HasMaxLength(4000);
            entity.Property(e => e.Reason).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.GenerationPrompt).HasMaxLength(8000);
            entity.Property(e => e.ApprovedChannels).HasMaxLength(100);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.LeaseId);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => new { e.PortfolioId, e.LeaseId, e.NoticeType, e.Status });
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Lease)
                .WithMany()
                .HasForeignKey(e => e.LeaseId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany()
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Conversation)
                .WithMany()
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<RentalApplication>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FirstName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.LastName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Email).HasMaxLength(200);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.CurrentAddress).HasMaxLength(500);
            entity.Property(e => e.Employer).HasMaxLength(200);
            entity.Property(e => e.MonthlyIncome).HasPrecision(18, 2);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.Property(e => e.DecisionReason).HasMaxLength(1000);
            entity.Property(e => e.ConsentIpAddress).HasMaxLength(64);
            // Provenance of the photo-ID/pay-stub autofill, stored as Postgres jsonb.
            entity.Property(e => e.IdExtractedFields).HasColumnType("jsonb");
            // Stored as the string enum name to match the app-wide string-enum convention.
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(40);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.SubmittedAtUtc);
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.RentalApplications)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany()
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Unit)
                .WithMany()
                .HasForeignKey(e => e.UnitId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.ApprovedTenant)
                .WithMany()
                .HasForeignKey(e => e.ApprovedTenantId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ScreeningResult>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CreditScoreBand).HasMaxLength(40);
            entity.Property(e => e.ProviderReference).HasMaxLength(200);
            // Raw provider response, stored as Postgres jsonb.
            entity.Property(e => e.RawResultJson).HasColumnType("jsonb");
            // Stored as the string enum name to match the app-wide string-enum convention.
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.Recommendation).HasConversion<string>().HasMaxLength(40);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.ApplicationId);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Application)
                .WithMany()
                .HasForeignKey(e => e.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AdverseActionNotice>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Reason).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.CreditReportingAgency).IsRequired().HasMaxLength(500);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.ApplicationId);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Application)
                .WithMany()
                .HasForeignKey(e => e.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.StoredFile)
                .WithMany()
                .HasForeignKey(e => e.StoredFileId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<SignatureRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PublicId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.DocumentName).IsRequired().HasMaxLength(260);
            entity.Property(e => e.Subject).HasMaxLength(200);
            entity.Property(e => e.ContentSha256).HasMaxLength(64);
            // Stored as the string enum name to match the app-wide string-enum convention.
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(40);
            // The envelope is resolved by its opaque PublicId (webhook/status path) — unique + indexed.
            entity.HasIndex(e => e.PublicId).IsUnique();
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.LeaseId);
            entity.HasIndex(e => e.Status);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Lease)
                .WithMany()
                .HasForeignKey(e => e.LeaseId)
                .OnDelete(DeleteBehavior.Cascade);
            // The stored files outlive the request row; never cascade a file delete back into it.
            entity.HasOne(e => e.OriginalStoredFile)
                .WithMany()
                .HasForeignKey(e => e.OriginalStoredFileId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.SignedStoredFile)
                .WithMany()
                .HasForeignKey(e => e.SignedStoredFileId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<SignatureSigner>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Email).IsRequired().HasMaxLength(256);
            entity.Property(e => e.Token).IsRequired().HasMaxLength(64);
            entity.Property(e => e.TypedName).HasMaxLength(200);
            entity.Property(e => e.IpAddress).HasMaxLength(64);
            entity.Property(e => e.UserAgent).HasMaxLength(512);
            // Stored as string enum names to match the app-wide string-enum convention.
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.SignatureType).HasConversion<string>().HasMaxLength(40);
            // The public signing endpoints resolve a session by this token only — unique + indexed.
            entity.HasIndex(e => e.Token).IsUnique();
            entity.HasIndex(e => e.SignatureRequestId);
            entity.HasOne(e => e.SignatureRequest)
                .WithMany(r => r.Signers)
                .HasForeignKey(e => e.SignatureRequestId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SignatureAuditEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Type).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.IpAddress).HasMaxLength(64);
            entity.Property(e => e.UserAgent).HasMaxLength(512);
            entity.Property(e => e.Detail).HasMaxLength(2000);
            entity.HasIndex(e => e.SignatureRequestId);
            entity.HasOne(e => e.SignatureRequest)
                .WithMany(r => r.AuditEvents)
                .HasForeignKey(e => e.SignatureRequestId)
                .OnDelete(DeleteBehavior.Cascade);
            // The signer link is optional (request-level events carry null) and never cascades.
            entity.HasOne(e => e.Signer)
                .WithMany()
                .HasForeignKey(e => e.SignerId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.MessageType).IsRequired().HasMaxLength(120);
            entity.Property(e => e.Payload).HasColumnType("jsonb");
            entity.Property(e => e.Error).HasMaxLength(4000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.SentAt);
            // Drain hot path (OutboxDispatchWorker): WHERE SentAt IS NULL AND RetryCount < 5
            // ORDER BY CreatedAt. A partial index over the unsent rows, ordered by CreatedAt, serves
            // both the filter and the sort without scanning/sorting the (ever-growing) sent rows.
            // RetryCount is included so the retry-budget predicate is covered too.
            entity.HasIndex(e => new { e.CreatedAt, e.RetryCount })
                  .HasDatabaseName("IX_OutboxMessages_Unsent_CreatedAt")
                  .HasFilter("\"SentAt\" IS NULL");
            // PortfolioId is optional: system/auth emails are not scoped to any portfolio.
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QueuedJob>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.JobType).IsRequired().HasMaxLength(120);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Payload).HasColumnType("jsonb");
            entity.Property(e => e.Error).HasMaxLength(4000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.Status);
        });

        modelBuilder.Entity<Portfolio>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ManagementCompanyName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.TimeZone).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(8).HasDefaultValue("USD");
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.Settings).HasMaxLength(10000);
            entity.Property(e => e.PublicApplicationToken).HasMaxLength(64);
            entity.Property(e => e.Status).HasConversion<int>();
            // Account-wide sandbox/live flag. Defaults to false so every existing/seeded portfolio
            // (incl. the dev-admin portfolio 1) stays Live and is unaffected by the migration.
            entity.Property(e => e.IsSandbox).HasDefaultValue(false);
            entity.HasIndex(e => e.Status);
            // Resolve the public application link by token; unique + filtered so multiple null
            // tokens (portfolios not accepting applications) never collide.
            entity.HasIndex(e => e.PublicApplicationToken)
                  .IsUnique()
                  .HasFilter("\"PublicApplicationToken\" IS NOT NULL");
            entity.HasQueryFilter(e => e.DeletedAt == null);
        });

        modelBuilder.Entity<Owner>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Email).HasMaxLength(200);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.MailingAddress).HasMaxLength(500);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Owners)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Property>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.AddressLine1).IsRequired().HasMaxLength(250);
            entity.Property(e => e.AddressLine2).HasMaxLength(250);
            entity.Property(e => e.City).IsRequired().HasMaxLength(100);
            entity.Property(e => e.State).IsRequired().HasMaxLength(100);
            entity.Property(e => e.PostalCode).IsRequired().HasMaxLength(20);
            entity.Property(e => e.ManagementFeePercent).HasPrecision(18, 2);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.Property(e => e.PropertyType).HasConversion<int>();
            entity.Property(e => e.Status).HasConversion<int>();
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.OwnerId);
            entity.HasIndex(e => e.OwnerEntityId);
            entity.HasIndex(e => e.Status);
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Properties)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Owner)
                .WithMany(o => o.Properties)
                .HasForeignKey(e => e.OwnerId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.OwnerEntity)
                .WithMany()
                .HasForeignKey(e => e.OwnerEntityId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Unit>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UnitNumber).IsRequired().HasMaxLength(50);
            entity.Property(e => e.FloorPlan).HasMaxLength(100);
            entity.Property(e => e.Bedrooms).HasPrecision(4, 1);
            entity.Property(e => e.Bathrooms).HasPrecision(4, 1);
            entity.Property(e => e.MarketRent).HasPrecision(18, 2);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.Property(e => e.Status).HasConversion<int>();
            entity.HasIndex(e => e.PropertyId);
            entity.HasIndex(e => e.Status);
            // Unique unit number within a property.
            entity.HasIndex(e => new { e.PropertyId, e.UnitNumber }).IsUnique();
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasOne(e => e.Property)
                .WithMany(p => p.Units)
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FirstName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.LastName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Email).HasMaxLength(200);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.EmergencyContact).HasMaxLength(200);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Tenants)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Lease>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.LeaseNumber).IsRequired().HasMaxLength(100);
            entity.Property(e => e.MonthlyRent).HasPrecision(18, 2);
            entity.Property(e => e.SecurityDeposit).HasPrecision(18, 2);
            entity.Property(e => e.LateFeeAmount).HasPrecision(18, 2);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.Property(e => e.Status).HasConversion<int>();
            // E-sign workflow state. Status stored as int to match Lease.Status; the envelope id is
            // indexed because the anonymous webhook resolves the lease by it.
            entity.Property(e => e.EsignStatus).HasConversion<int>();
            entity.Property(e => e.EsignEnvelopeId).HasMaxLength(200);
            // Full scan-extraction superset for leases imported from a scanned PDF (Postgres jsonb).
            entity.Property(e => e.ExtractedData).HasColumnType("jsonb");
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.PropertyId);
            entity.HasIndex(e => e.UnitId);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.EsignEnvelopeId);
            entity.HasQueryFilter(e => e.DeletedAt == null);
            // StartDate must precede EndDate, and RentDueDay must be a valid day of month.
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Lease_StartBeforeEnd", "\"StartDate\" < \"EndDate\"");
                t.HasCheckConstraint("CK_Lease_RentDueDay", "\"RentDueDay\" >= 1 AND \"RentDueDay\" <= 31");
            });
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Leases)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany(p => p.Leases)
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Unit)
                .WithMany(u => u.Leases)
                .HasForeignKey(e => e.UnitId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Tenant)
                .WithMany(t => t.Leases)
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.Method).HasMaxLength(100);
            entity.Property(e => e.ExternalReference).HasMaxLength(200);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.Property(e => e.PaymentType).HasConversion<int>();
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(p => p.PeriodKey).HasMaxLength(7);
            // Promoted scan-check fields + full extraction superset (Postgres jsonb).
            entity.Property(e => e.PayerName).HasMaxLength(200);
            entity.Property(e => e.CheckNumber).HasMaxLength(100);
            entity.Property(e => e.BankName).HasMaxLength(200);
            entity.Property(e => e.ExtractedData).HasColumnType("jsonb");
            // Idempotency: at most one auto-generated payment per (lease, type, period). Manual payments
            // (PeriodKey == null) are excluded by the filter, so they never collide.
            entity.HasIndex(p => new { p.LeaseId, p.PaymentType, p.PeriodKey })
                  .IsUnique()
                  .HasFilter("\"PeriodKey\" IS NOT NULL");
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.LeaseId);
            entity.HasIndex(e => e.Status);
            // LateFee daily sweep (LateFeeService.AssessAsync) scans ALL portfolios:
            // WHERE PaymentType = Rent AND PeriodKey IS NOT NULL AND Status IN (Scheduled/Late/Partial)
            // AND DueDate < today. None of the single-column indexes above cover that, so it degraded
            // to a sequential scan of the unbounded Payments table. This composite, filtered to the
            // auto-generated rent rows (PeriodKey IS NOT NULL), covers the predicate.
            entity.HasIndex(e => new { e.PaymentType, e.Status, e.DueDate })
                  .HasDatabaseName("IX_Payments_LateFeeSweep")
                  .HasFilter("\"PeriodKey\" IS NOT NULL");
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Payments)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Lease)
                .WithMany(l => l.Payments)
                .HasForeignKey(e => e.LeaseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Expense>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Category).HasConversion<int>();
            entity.Property(e => e.Description).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.Subtotal).HasPrecision(18, 2);
            entity.Property(e => e.TaxAmount).HasPrecision(18, 2);
            entity.Property(e => e.ReceiptData).HasColumnType("jsonb");
            // Promoted scalar receipt fields (alongside the ReceiptData jsonb superset).
            entity.Property(e => e.PaymentMethod).HasMaxLength(100);
            entity.Property(e => e.CardLast4).HasMaxLength(20);
            entity.Property(e => e.DocumentKind).HasMaxLength(50);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.Property(e => e.Status).HasConversion<int>();
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.PropertyId);
            entity.HasIndex(e => e.VendorId);
            entity.HasIndex(e => e.WorkOrderId);
            entity.HasIndex(e => e.Status);
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Expenses)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany(p => p.Expenses)
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Vendor)
                .WithMany(v => v.Expenses)
                .HasForeignKey(e => e.VendorId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.WorkOrder)
                .WithMany(w => w.Expenses)
                .HasForeignKey(e => e.WorkOrderId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasMany(e => e.LineItems)
                .WithOne(li => li.Expense)
                .HasForeignKey(li => li.ExpenseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExpenseLineItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.Quantity).HasPrecision(18, 2);
            entity.Property(e => e.UnitPrice).HasPrecision(18, 2);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.HasIndex(e => e.ExpenseId);
        });

        modelBuilder.Entity<Vendor>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ServiceType).IsRequired().HasMaxLength(120);
            entity.Property(e => e.Email).HasMaxLength(200);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.TaxId).HasMaxLength(50);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.Property(e => e.AverageRating).HasPrecision(3, 2);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Vendors)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VendorDispatch>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.Message).HasMaxLength(1600);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.WorkOrderId);
            entity.HasIndex(e => e.VendorId);
            entity.HasIndex(e => e.Status);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.WorkOrder)
                .WithMany()
                .HasForeignKey(e => e.WorkOrderId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Vendor)
                .WithMany(v => v.Dispatches)
                .HasForeignKey(e => e.VendorId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VendorRating>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Comment).HasMaxLength(2000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.VendorId);
            entity.HasIndex(e => e.WorkOrderId);
            entity.ToTable(t => t.HasCheckConstraint("CK_VendorRating_Stars", "\"Stars\" >= 1 AND \"Stars\" <= 5"));
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Vendor)
                .WithMany(v => v.Ratings)
                .HasForeignKey(e => e.VendorId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.WorkOrder)
                .WithMany()
                .HasForeignKey(e => e.WorkOrderId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WorkOrder>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(4000);
            entity.Property(e => e.Category).HasMaxLength(120);
            entity.Property(e => e.EstimatedCost).HasPrecision(18, 2);
            entity.Property(e => e.ActualCost).HasPrecision(18, 2);
            entity.Property(e => e.CreatedBy).HasMaxLength(120);
            entity.Property(e => e.Priority).HasConversion<int>();
            entity.Property(e => e.Status).HasConversion<int>();
            // Full scan-extraction superset for work orders created from a scan draft (Postgres jsonb).
            entity.Property(e => e.ExtractedData).HasColumnType("jsonb");
            // ScheduledFor + ScheduledWindowEnd bracket the tenant-facing arrival window.
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.PropertyId);
            entity.HasIndex(e => e.UnitId);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.LeaseId);
            entity.HasIndex(e => e.VendorId);
            entity.HasIndex(e => e.Priority);
            entity.HasIndex(e => e.Status);
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.WorkOrders)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany(p => p.WorkOrders)
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Unit)
                .WithMany(u => u.WorkOrders)
                .HasForeignKey(e => e.UnitId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Tenant)
                .WithMany(t => t.WorkOrders)
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Lease)
                .WithMany(l => l.WorkOrders)
                .HasForeignKey(e => e.LeaseId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Vendor)
                .WithMany(v => v.WorkOrders)
                .HasForeignKey(e => e.VendorId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WorkOrderStatusEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FromStatus).HasConversion<int>();
            entity.Property(e => e.ToStatus).HasConversion<int>();
            entity.Property(e => e.Note).HasMaxLength(2000);
            entity.Property(e => e.ChangedByLabel).HasMaxLength(120);
            entity.HasIndex(e => e.WorkOrderId);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasOne(e => e.WorkOrder)
                .WithMany(w => w.StatusEvents)
                .HasForeignKey(e => e.WorkOrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RecurringMaintenanceTask>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(4000);
            entity.Property(e => e.Category).HasMaxLength(120);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            // Stored as the string enum name to match the app-wide string-enum convention.
            entity.Property(e => e.RecurrenceInterval).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.Priority).HasConversion<int>();
            // The worker's hot path: scan active, not-yet-deleted tasks that are due. The query filter
            // already excludes soft-deleted rows; this index serves the (active, due) scan per portfolio.
            entity.HasIndex(e => new { e.PortfolioId, e.IsActive, e.NextDueDate });
            entity.HasIndex(e => e.PropertyId);
            entity.HasIndex(e => e.UnitId);
            entity.HasIndex(e => e.VendorId);
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.RecurringMaintenanceTasks)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany()
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Unit)
                .WithMany()
                .HasForeignKey(e => e.UnitId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Vendor)
                .WithMany()
                .HasForeignKey(e => e.VendorId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Appointment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ProspectName).HasMaxLength(200);
            entity.Property(e => e.ProspectEmail).HasMaxLength(200);
            entity.Property(e => e.AssignedTo).HasMaxLength(120);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.Property(e => e.Type).HasConversion<int>();
            entity.Property(e => e.Status).HasConversion<int>();
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.PropertyId);
            entity.HasIndex(e => e.UnitId);
            entity.HasIndex(e => e.LeaseId);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ScheduledStart);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Appointments)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany(p => p.Appointments)
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Unit)
                .WithMany(u => u.Appointments)
                .HasForeignKey(e => e.UnitId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Lease)
                .WithMany(l => l.Appointments)
                .HasForeignKey(e => e.LeaseId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Tenant)
                .WithMany(t => t.Appointments)
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Inspection>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Outcome).HasMaxLength(500);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.Property(e => e.Inspector).HasMaxLength(200);
            entity.Property(e => e.Type).HasConversion<int>();
            entity.Property(e => e.Status).HasConversion<int>();
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.PropertyId);
            entity.HasIndex(e => e.UnitId);
            entity.HasIndex(e => e.LeaseId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ScheduledFor);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Inspections)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany(p => p.Inspections)
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Unit)
                .WithMany(u => u.Inspections)
                .HasForeignKey(e => e.UnitId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Lease)
                .WithMany(l => l.Inspections)
                .HasForeignKey(e => e.LeaseId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<InspectionItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Area).IsRequired().HasMaxLength(120);
            entity.Property(e => e.Label).IsRequired().HasMaxLength(300);
            entity.Property(e => e.Note).HasMaxLength(2000);
            // Stored as the string enum name to match the app-wide string-enum convention.
            entity.Property(e => e.Result).HasConversion<string>().HasMaxLength(40);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.InspectionId);
            entity.HasOne(e => e.Inspection)
                .WithMany(i => i.Items)
                .HasForeignKey(e => e.InspectionId)
                .OnDelete(DeleteBehavior.Cascade);
            // Photo + spawned work order are optional links; never cascade from those rows back here.
            entity.HasOne(e => e.PhotoStoredFile)
                .WithMany()
                .HasForeignKey(e => e.PhotoStoredFileId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.SpawnedWorkOrder)
                .WithMany()
                .HasForeignKey(e => e.SpawnedWorkOrderId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<InspectionTemplate>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.InspectionType).HasConversion<int>();
            entity.HasIndex(e => e.PortfolioId);
            // PortfolioId is optional: built-in templates (if ever seeded) carry null.
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<InspectionTemplateItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Area).IsRequired().HasMaxLength(120);
            entity.Property(e => e.Label).IsRequired().HasMaxLength(300);
            entity.HasIndex(e => e.TemplateId);
            entity.HasOne(e => e.Template)
                .WithMany(t => t.Items)
                .HasForeignKey(e => e.TemplateId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserAccount>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Email).IsRequired().HasMaxLength(200);
            entity.Property(e => e.DisplayName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.PasswordHash).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Role).HasConversion<int>();
            entity.HasIndex(e => new { e.PortfolioId, e.Email }).IsUnique();
            entity.HasIndex(e => e.Role);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.UserAccounts)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Owner)
                .WithMany(o => o.UserAccounts)
                .HasForeignKey(e => e.OwnerId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Tenant)
                .WithMany(t => t.UserAccounts)
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PortalMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Subject).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Body).IsRequired().HasMaxLength(5000);
            entity.Property(e => e.Reply).HasMaxLength(5000);
            entity.Property(e => e.Channels).HasMaxLength(100);
            entity.Property(e => e.Status).HasConversion<int>();
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.UserAccountId);
            entity.HasIndex(e => e.RecipientTenantId);
            entity.HasIndex(e => e.Status);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.PortalMessages)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            // UserAccountId is now optional (landlord-authored messages carry null + FromLandlord=true).
            // SetNull rather than Cascade so deleting a portal account does not delete the thread.
            entity.HasOne(e => e.UserAccount)
                .WithMany(u => u.Messages)
                .HasForeignKey(e => e.UserAccountId)
                .OnDelete(DeleteBehavior.SetNull);
            // Recipient tenant of a landlord message — optional, no cascade (matches other optional FKs).
            entity.HasOne(e => e.RecipientTenant)
                .WithMany()
                .HasForeignKey(e => e.RecipientTenantId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Property)
                .WithMany()
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Unit)
                .WithMany()
                .HasForeignKey(e => e.UnitId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Subject).IsRequired().HasMaxLength(200);
            entity.Property(e => e.LastMessagePreview).HasMaxLength(280);
            entity.HasIndex(e => new { e.PortfolioId, e.TenantId });
            entity.HasIndex(e => e.LastMessageAt);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            // Tenant is required (a conversation is always with one tenant); no cascade so deleting a
            // tenant does not silently destroy the thread history. Soft-delete is the norm anyway.
            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Property)
                .WithMany()
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ConversationMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Body).IsRequired().HasMaxLength(4000);
            entity.Property(e => e.Channels).HasMaxLength(100);
            // Stored as the string enum name to match the app-wide convention.
            entity.Property(e => e.SenderRole).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(e => e.ConversationId);
            // Cascade: deleting a conversation removes its messages.
            entity.HasOne(e => e.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SecurityDepositHolding>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50).HasConversion<string>();
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.ReturnedAmount).HasPrecision(18, 2);
            entity.Property(e => e.DeductionsJson).IsRequired().HasColumnType("jsonb");
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.LeaseId);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Lease)
                .WithMany()
                .HasForeignKey(e => e.LeaseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OpeningBalance>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.Note).HasMaxLength(2000);
            entity.HasIndex(e => e.PortfolioId);
            // One opening balance per lease (within a portfolio). The lease id alone is globally unique,
            // but keying on (portfolio, lease) keeps the guarantee scoped and matches the tenant model.
            entity.HasIndex(e => new { e.PortfolioId, e.LeaseId }).IsUnique();
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Lease)
                .WithMany()
                .HasForeignKey(e => e.LeaseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // --- Stripe payment groundwork ---

        modelBuilder.Entity<PaymentTransaction>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(10);
            entity.Property(e => e.Provider).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ProviderPaymentIntentId).HasMaxLength(200);
            entity.Property(e => e.FailureReason).HasMaxLength(1000);
            // Unique index on ProviderPaymentIntentId — filtered so nulls don't collide.
            entity.HasIndex(e => e.ProviderPaymentIntentId)
                  .IsUnique()
                  .HasFilter("\"ProviderPaymentIntentId\" IS NOT NULL");
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.PaymentId);
            entity.HasOne(e => e.Payment)
                .WithMany()
                .HasForeignKey(e => e.PaymentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StripeWebhookEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EventId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(200);
            entity.HasIndex(e => e.EventId).IsUnique();
        });

        modelBuilder.Entity<AutopayEnrollment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.StripeCustomerId).HasMaxLength(200);
            entity.Property(e => e.StripePaymentMethodId).HasMaxLength(200);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.TenantId);
            // At most one Active enrollment per lease. Filtered so cancelled (inactive) rows never
            // collide and a tenant can re-enroll after cancelling.
            entity.HasIndex(e => e.LeaseId)
                  .IsUnique()
                  .HasFilter("\"Active\" = true");
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Lease)
                .WithMany()
                .HasForeignKey(e => e.LeaseId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EngineWorkerHeartbeat>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.WorkerName).IsRequired().HasMaxLength(120);
            entity.Property(e => e.LastErrorMessage).HasMaxLength(2000);
            entity.Property(e => e.Metadata).HasColumnType("jsonb");
            entity.Property(e => e.Status).HasConversion<int>();
            entity.HasIndex(e => e.WorkerName).IsUnique();
        });

        // ----------------------------------------------------------------------------------------
        // Soft-delete consistency across required relationships (audit M-7, root cause of H-4).
        //
        // A soft-deletable principal (Portfolio/Lease/Expense/RentalApplication/WorkOrder, each with
        // a `DeletedAt == null` global filter) was the REQUIRED end of relationships whose dependent
        // rows had NO matching filter. EF Core flagged every one of these with warning EF10622
        // ("required entity is filtered out"). More importantly, the dependents stayed visible after
        // their principal was soft-deleted: a query of `_db.Payments` referencing only the scalar
        // `LeaseId` FK never triggered the Lease filter, so soft-deleted leases' payments kept
        // inflating the overdue/collected/past-due KPIs and produced ghost "who's behind" rows (H-4).
        //
        // Fix: give each such dependent a query filter that matches its principal's soft-delete state
        // by walking the required navigation. Because the relationship is required, EF emits an INNER
        // JOIN to the (already-filtered) principal set on every query of the dependent — so a row
        // whose principal is soft-deleted simply disappears everywhere, including the scalar-FK
        // aggregates. This is the single, declarative source of truth the audit asked for; no per-site
        // service change and no denormalized DeletedAt column on the leaf tables is required.
        //
        // Identity / global / infra tables (AspNet*, AuditLog, OutboxMessage, EngineWorkerHeartbeat,
        // StripeWebhookEvent) are intentionally NOT filtered here — they are not soft-deletable and
        // several legitimately outlive any single business row.
        // ----------------------------------------------------------------------------------------

        // Dependents of Lease (Lease has `DeletedAt == null`). Payment is the H-4 root.
        modelBuilder.Entity<Payment>().HasQueryFilter(e => e.Lease!.DeletedAt == null);
        modelBuilder.Entity<AutopayEnrollment>().HasQueryFilter(e => e.Lease!.DeletedAt == null);
        modelBuilder.Entity<NoticeDraft>().HasQueryFilter(e => e.Lease!.DeletedAt == null);
        modelBuilder.Entity<OpeningBalance>().HasQueryFilter(e => e.Lease!.DeletedAt == null);
        modelBuilder.Entity<SecurityDepositHolding>().HasQueryFilter(e => e.Lease!.DeletedAt == null);
        modelBuilder.Entity<SignatureRequest>().HasQueryFilter(e => e.Lease!.DeletedAt == null);

        // Dependents of Portfolio (Portfolio has `DeletedAt == null`).
        modelBuilder.Entity<Appointment>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<BankConnection>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<BankTransaction>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<Conversation>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<DeviceToken>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<Inspection>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<Notification>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<NotificationPreference>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<Owner>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<PortalMessage>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<QueuedJob>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<ScanBatch>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<ScanDraft>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<UserAccount>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<VendorDispatch>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<VendorRating>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);

        // Dependents of RentalApplication (RentalApplication has `DeletedAt == null`); nav is `Application`.
        modelBuilder.Entity<AdverseActionNotice>().HasQueryFilter(e => e.Application!.DeletedAt == null);
        modelBuilder.Entity<ScreeningResult>().HasQueryFilter(e => e.Application!.DeletedAt == null);

        // Dependent of Expense (Expense has `DeletedAt == null`).
        modelBuilder.Entity<ExpenseLineItem>().HasQueryFilter(e => e.Expense!.DeletedAt == null);

        // Dependent of WorkOrder (WorkOrder has `DeletedAt == null`).
        modelBuilder.Entity<WorkOrderStatusEvent>().HasQueryFilter(e => e.WorkOrder!.DeletedAt == null);

        // Transitive (grandchild) dependents: their direct parent is now itself filtered above, so
        // EF flags the same EF10622 one level down. Chain the filter through to the root soft-deletable
        // principal's `DeletedAt` so the whole sub-tree disappears when an ancestor is soft-deleted.
        modelBuilder.Entity<ConversationMessage>().HasQueryFilter(e => e.Conversation!.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<InspectionItem>().HasQueryFilter(e => e.Inspection!.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<PaymentTransaction>().HasQueryFilter(e => e.Payment!.Lease!.DeletedAt == null);
        modelBuilder.Entity<SignatureAuditEvent>().HasQueryFilter(e => e.SignatureRequest!.Lease!.DeletedAt == null);
        modelBuilder.Entity<SignatureSigner>().HasQueryFilter(e => e.SignatureRequest!.Lease!.DeletedAt == null);
    }
}
