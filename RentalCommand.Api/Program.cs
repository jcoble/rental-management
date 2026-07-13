using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Extensions;
using RentalCommand.Api.Hubs;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Api.Services.Payments;
using RentalCommand.Api.Services.Voice;
using RentalCommand.Api.Simulation;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Atomic;

// QuestPDF Community license (free for small businesses / OSS) — required before any PDF is generated.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

var dataProtection = builder.Services.AddDataProtection().SetApplicationName("RentalCommand");
var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    Directory.CreateDirectory(dataProtectionKeysPath);
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}

// Raise Kestrel's hard body-size limit (default 30 MB) so large scan uploads
// (up to 50 MB per UploadSettings) are not silently rejected with 413 before
// the controller runs. 64 MB gives headroom over the 50 MB app-level gate for
// multipart framing overhead. The UploadSettings.MaxFileSizeBytes check in
// FileUploadValidator remains the real policy gate.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 64_000_000);

// Raise the ASP.NET multipart body limit to match.
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 64_000_000);

// --- Configuration binding ---
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection(JwtSettings.SectionName));
builder.Services.Configure<AtomicAuthSessionCredentialOptions>(
    builder.Configuration.GetSection(AtomicAuthSessionCredentialOptions.SectionName));
builder.Services.Configure<SeedSettings>(builder.Configuration.GetSection(SeedSettings.SectionName));
builder.Services.Configure<RentalCommand.Core.Configuration.PlatformAdminOptions>(
    builder.Configuration.GetSection(RentalCommand.Core.Configuration.PlatformAdminOptions.SectionName));
builder.Services.Configure<RentalCommand.Core.Configuration.AssistantConfig>(
    builder.Configuration.GetSection(RentalCommand.Core.Configuration.AssistantConfig.SectionName));
builder.Services.Configure<RentalCommand.Core.Configuration.UploadSettings>(
    builder.Configuration.GetSection(RentalCommand.Core.Configuration.UploadSettings.SectionName));
builder.Services.Configure<RentalCommand.Core.Configuration.GoogleAuthOptions>(
    builder.Configuration.GetSection(RentalCommand.Core.Configuration.GoogleAuthOptions.SectionName));
builder.Services.Configure<RentalCommand.Core.Configuration.StripeConfig>(
    builder.Configuration.GetSection(RentalCommand.Core.Configuration.StripeConfig.SectionName));
builder.Services.Configure<RentalCommand.Core.Configuration.ScreeningConfig>(
    builder.Configuration.GetSection(RentalCommand.Core.Configuration.ScreeningConfig.SectionName));
builder.Services.Configure<RentalCommand.Core.Configuration.QuickBooksOptions>(
    builder.Configuration.GetSection(RentalCommand.Core.Configuration.QuickBooksOptions.SectionName));
var llmProvider = builder.Configuration.GetValue<string>("Assistant:Provider") ?? "openai";
builder.Services.AddSingleton<RentalCommand.Api.Scanning.IImageTextExtractor,
    RentalCommand.Api.Scanning.TesseractImageTextExtractor>();
if (string.Equals(llmProvider, "anthropic", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<RentalCommand.Core.Interfaces.ILlmProvider, RentalCommand.Api.Scanning.AnthropicLlmProvider>(c =>
    {
        c.BaseAddress = new Uri("https://api.anthropic.com/");
        c.Timeout = TimeSpan.FromSeconds(90);
    });
}
else if (string.Equals(llmProvider, "gemini", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<RentalCommand.Core.Interfaces.ILlmProvider, RentalCommand.Api.Scanning.GeminiLlmProvider>(c =>
    {
        c.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
        c.Timeout = TimeSpan.FromSeconds(90);
    });
}
else if (string.Equals(llmProvider, "claude-cli", StringComparison.OrdinalIgnoreCase))
{
    // DEV-ONLY: shell out to the locally-installed Claude Code CLI (`claude -p`) so extraction can
    // use the developer's own Claude subscription at no per-token cost. No HttpClient — it invokes
    // the binary. Unsuitable for production; see ClaudeCliLlmProvider.
    builder.Services.AddSingleton<RentalCommand.Core.Interfaces.ILlmProvider, RentalCommand.Api.Scanning.ClaudeCliLlmProvider>();
}
else // default: openai
{
    builder.Services.AddHttpClient<RentalCommand.Core.Interfaces.ILlmProvider, RentalCommand.Api.Scanning.OpenAiLlmProvider>(c =>
    {
        c.BaseAddress = new Uri("https://api.openai.com/");
        c.Timeout = TimeSpan.FromSeconds(90);
    });
}
builder.Services.AddScoped<RentalCommand.Core.Interfaces.IFileStorage, RentalCommand.Api.Scanning.DiskFileStorage>();
builder.Services.AddHttpClient<IAudioTranscriptionService, OpenAiAudioTranscriptionService>(c =>
{
    c.BaseAddress = new Uri("https://api.openai.com/");
    c.Timeout = TimeSpan.FromSeconds(90);
});

// Google Places (New) address autocomplete. Key from the "GooglePlaces" config section
// (user-secrets in dev, container env in prod). Disabled gracefully when no key is set.
builder.Services.Configure<RentalCommand.Core.Configuration.GooglePlacesConfig>(
    builder.Configuration.GetSection(RentalCommand.Core.Configuration.GooglePlacesConfig.SectionName));
builder.Services.AddHttpClient<RentalCommand.Api.Services.Places.GooglePlacesService>(c =>
{
    c.BaseAddress = new Uri("https://places.googleapis.com/");
    c.Timeout = TimeSpan.FromSeconds(15);
});

var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>() ?? new JwtSettings();

// Fail fast on an unconfigured signing key. HS256 needs >= 256 bits (32 chars), and the
// committed placeholders must never be used outside local dev — otherwise anyone could forge tokens.
// Placeholder values stay usable in Development so local dev needs no secret.
JwtSecretGuard.Validate(jwtSettings.SecretKey, builder.Environment.IsDevelopment());

// --- Database ---
// The scoped AuditSaveChangesInterceptor is resolved from the same scope as the DbContext (the
// (sp, options) overload), so it can read the per-request ICurrentActor / IAuditScope.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Missing connection string 'DefaultConnection'.");
var migratorConnectionString = builder.Configuration.GetConnectionString("MigratorConnection");
var migrateOnly = args.Any(argument =>
    string.Equals(argument, "--migrate-only", StringComparison.OrdinalIgnoreCase));
string? engineConnectionStringForMigration = null;

if (migrateOnly)
{
    migratorConnectionString ??= throw new InvalidOperationException(
        "The one-shot migration process requires ConnectionStrings:MigratorConnection.");
    engineConnectionStringForMigration = builder.Configuration.GetConnectionString("EngineConnection")
        ?? throw new InvalidOperationException(
            "The one-shot migration process requires ConnectionStrings:EngineConnection.");
}
else if (!string.IsNullOrWhiteSpace(migratorConnectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings:MigratorConnection must not be available to the long-running API process.");
}

if (!migrateOnly)
{
    RentalCommand.Data.Security.RuntimeDatabaseRoleProvisioner.ValidateRuntimeConnectionString(
        connectionString,
        RentalCommand.Data.Security.DatabaseRuntimeIdentity.ApiRole,
        allowDevelopmentDefault: builder.Environment.IsDevelopment());
}

builder.Services.AddHttpContextAccessor();

// Converted commands opt into the atomic executor. Both interceptors are attached to the shared
// context during this unmerged rewrite: the legacy audit interceptor stands down only while an
// admitted atomic attempt is active, and the atomic interceptors are inert for unconverted paths.
builder.Services.AddAtomicPersistenceKernel(allowUnconvertedWrites: true);
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Api.Services.Domain.AtomicMoneyMutationCommand,
    RentalCommand.Api.Services.Domain.AtomicMoneyMutationResult,
    RentalCommand.Api.Services.Domain.AtomicMoneyMutationHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Leasing.CreatePropertyDispositionCommand,
    RentalCommand.Core.Leasing.CreatePropertyDispositionResult,
    RentalCommand.Data.Leasing.CreatePropertyDispositionHandler>();
builder.Services.AddAtomicCommandHandler<
    ChangeWorkspaceAssignmentScopeCommand,
    WorkspaceAccessMutationResult,
    ChangeWorkspaceAssignmentScopeHandler>();
builder.Services.AddAtomicCommandHandler<
    ChangeWorkspaceAssignmentEndCommand,
    WorkspaceAccessMutationResult,
    ChangeWorkspaceAssignmentEndHandler>();
builder.Services.AddAtomicCommandHandler<
    CreateWorkspaceMembershipCommand,
    CreateWorkspaceMembershipResult,
    CreateWorkspaceMembershipHandler>();
builder.Services.AddAtomicCommandHandler<
    AddWorkspaceRoleAssignmentCommand,
    WorkspaceTeamMutationResult,
    AddWorkspaceRoleAssignmentHandler>();
builder.Services.AddAtomicCommandHandler<
    EndWorkspaceRoleAssignmentCommand,
    WorkspaceTeamMutationResult,
    EndWorkspaceRoleAssignmentHandler>();
builder.Services.AddAtomicCommandHandler<
    ReplaceWorkspaceAssignmentPropertyScopeCommand,
    WorkspaceTeamMutationResult,
    ReplaceWorkspaceAssignmentPropertyScopeHandler>();
builder.Services.AddAtomicCommandHandler<
    ChangeWorkspaceMembershipStatusCommand,
    WorkspaceTeamMutationResult,
    ChangeWorkspaceMembershipStatusHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Esign.IssueLeaseAgreementCommand,
    RentalCommand.Core.Esign.IssueLeaseAgreementResult,
    RentalCommand.Data.Esign.IssueLeaseAgreementHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Esign.IssueLeaseAddendumCommand,
    RentalCommand.Core.Esign.IssueLeaseAddendumResult,
    RentalCommand.Data.Esign.IssueLeaseAddendumHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Esign.RecordNativeEsignViewCommand,
    RentalCommand.Core.Esign.RecordNativeEsignViewResult,
    RentalCommand.Data.Esign.RecordNativeEsignViewHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Esign.RecordNativeSignatureCommand,
    RentalCommand.Core.Esign.NativeSignerActionResult,
    RentalCommand.Data.Esign.RecordNativeSignatureHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Esign.RecordNativeDeclineCommand,
    RentalCommand.Core.Esign.NativeSignerActionResult,
    RentalCommand.Data.Esign.RecordNativeDeclineHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Esign.FinalizeNativeEsignRequestCommand,
    RentalCommand.Core.Esign.FinalizeNativeEsignRequestResult,
    RentalCommand.Data.Esign.FinalizeNativeEsignRequestHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Operations.DispatchWorkOrderToVendorCommand,
    RentalCommand.Core.Operations.DispatchWorkOrderToVendorResult,
    RentalCommand.Data.Operations.DispatchWorkOrderToVendorHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Operations.CompleteVendorDispatchFromInboundCommand,
    RentalCommand.Core.Operations.CompleteVendorDispatchFromInboundResult,
    RentalCommand.Data.Operations.CompleteVendorDispatchFromInboundHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Vendors.RequestVendorW9Command,
    RentalCommand.Core.Vendors.RequestVendorW9Result,
    RentalCommand.Data.Vendors.RequestVendorW9Handler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Screening.CreateAdverseActionNoticeCommand,
    RentalCommand.Core.Screening.CreateAdverseActionNoticeResult,
    RentalCommand.Data.Screening.CreateAdverseActionNoticeHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Documents.CreateStoredDocumentCommand,
    RentalCommand.Core.Documents.CreateStoredDocumentResult,
    RentalCommand.Data.Documents.CreateStoredDocumentHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Documents.DeleteStoredDocumentCommand,
    RentalCommand.Core.Documents.DeleteStoredDocumentResult,
    RentalCommand.Data.Documents.DeleteStoredDocumentHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Scanning.FinalizeScanUploadCommand,
    RentalCommand.Core.Scanning.FinalizeScanUploadResult,
    RentalCommand.Data.Scanning.FinalizeScanUploadHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Conversations.SendConversationMessageCommand,
    RentalCommand.Core.Conversations.SendConversationMessageResult,
    RentalCommand.Data.Conversations.SendConversationMessageHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Auth.IssueSessionRefreshCredentialCommand,
    RentalCommand.Core.Auth.SessionRefreshMutationResult,
    RentalCommand.Data.Auth.IssueSessionRefreshCredentialHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Auth.RotateSessionRefreshCredentialCommand,
    RentalCommand.Core.Auth.SessionRefreshMutationResult,
    RentalCommand.Data.Auth.RotateSessionRefreshCredentialHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Auth.IssueLoginContextSelectionChallengeCommand,
    RentalCommand.Core.Auth.LoginContextSelectionChallengeResult,
    RentalCommand.Data.Auth.IssueLoginContextSelectionChallengeHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Auth.StartAuthSessionCommand,
    RentalCommand.Core.Auth.StartAuthSessionResult,
    RentalCommand.Data.Auth.StartAuthSessionHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Auth.SwitchAuthSessionContextCommand,
    RentalCommand.Core.Auth.SwitchAuthSessionContextResult,
    RentalCommand.Data.Auth.SwitchAuthSessionContextHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Auth.RevokeAuthSessionCommand,
    RentalCommand.Core.Auth.RevokeAuthSessionResult,
    RentalCommand.Data.Auth.RevokeAuthSessionHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Payments.PrepareProviderPaymentCreateCommand,
    RentalCommand.Core.Payments.PrepareProviderPaymentCreateResult,
    RentalCommand.Data.Payments.PrepareProviderPaymentCreateHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Payments.FinalizeProviderPaymentCreateCommand,
    RentalCommand.Core.Payments.FinalizeProviderPaymentCreateResult,
    RentalCommand.Data.Payments.FinalizeProviderPaymentCreateHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Payments.FailProviderPaymentCreateCommand,
    RentalCommand.Core.Payments.FailProviderPaymentCreateResult,
    RentalCommand.Data.Payments.FailProviderPaymentCreateHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Payments.PrepareProviderAutopaySetupCommand,
    RentalCommand.Core.Payments.PrepareProviderAutopaySetupResult,
    RentalCommand.Data.Payments.PrepareProviderAutopaySetupHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Payments.RecordTenantReceiptCommand,
    RentalCommand.Core.Payments.RecordTenantReceiptResult,
    RentalCommand.Data.Payments.RecordTenantReceiptHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Payments.PostTenantChargeCommand,
    RentalCommand.Core.Payments.TenantChargeMutationResult,
    RentalCommand.Data.Payments.PostTenantChargeHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Payments.ReverseTenantChargeCommand,
    RentalCommand.Core.Payments.TenantChargeMutationResult,
    RentalCommand.Data.Payments.ReverseTenantChargeHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Payments.PostTenantCreditCommand,
    RentalCommand.Core.Payments.TenantLedgerMutationResult,
    RentalCommand.Data.Payments.PostTenantCreditHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Payments.PostTenantAdjustmentCommand,
    RentalCommand.Core.Payments.TenantLedgerMutationResult,
    RentalCommand.Data.Payments.PostTenantAdjustmentHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Payments.ReverseTenantLedgerEntryCommand,
    RentalCommand.Core.Payments.TenantLedgerMutationResult,
    RentalCommand.Data.Payments.ReverseTenantLedgerEntryHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Payments.RefundTenantPaymentCommand,
    RentalCommand.Core.Payments.TenantPaymentRefundResult,
    RentalCommand.Data.Payments.RefundTenantPaymentHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Payments.FundSecurityDepositCommand,
    RentalCommand.Core.Payments.SecurityDepositMutationResult,
    RentalCommand.Data.Payments.FundSecurityDepositHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Payments.DeductSecurityDepositCommand,
    RentalCommand.Core.Payments.SecurityDepositMutationResult,
    RentalCommand.Data.Payments.DeductSecurityDepositHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Payments.RefundSecurityDepositCommand,
    RentalCommand.Core.Payments.SecurityDepositMutationResult,
    RentalCommand.Data.Payments.RefundSecurityDepositHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Payments.ReverseSecurityDepositEntryCommand,
    RentalCommand.Core.Payments.SecurityDepositMutationResult,
    RentalCommand.Data.Payments.ReverseSecurityDepositEntryHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Payments.RecordVerifiedProviderPaymentEventCommand,
    RentalCommand.Core.Payments.RecordVerifiedProviderPaymentEventResult,
    RentalCommand.Data.Payments.RecordVerifiedProviderPaymentEventHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Accounting.ConfirmAccountingMappingCommand,
    RentalCommand.Core.Accounting.ConfirmAccountingMappingResult,
    RentalCommand.Data.Accounting.ConfirmAccountingMappingHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Accounting.ContinueAccountingMappingPromotionCommand,
    RentalCommand.Core.Accounting.ContinueAccountingMappingPromotionResult,
    RentalCommand.Data.Accounting.ContinueAccountingMappingPromotionHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Accounting.ApplyAccountingPullResultCommand,
    RentalCommand.Core.Accounting.ApplyAccountingPullResult,
    RentalCommand.Data.Accounting.ApplyAccountingPullResultHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Banking.PreparePlaidTokenExchangeCommand,
    RentalCommand.Core.Banking.PreparePlaidTokenExchangeResult,
    RentalCommand.Data.Banking.PreparePlaidTokenExchangeHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Banking.AdmitPlaidTokenExchangeCommand,
    RentalCommand.Core.Banking.AdmitPlaidTokenExchangeResult,
    RentalCommand.Data.Banking.AdmitPlaidTokenExchangeHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Banking.RecordPlaidTokenExchangeReceiptCommand,
    RentalCommand.Core.Banking.RecordPlaidTokenExchangeReceiptResult,
    RentalCommand.Data.Banking.RecordPlaidTokenExchangeReceiptHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Banking.ApplyPlaidConnectionCommand,
    RentalCommand.Core.Banking.ApplyPlaidConnectionResult,
    RentalCommand.Data.Banking.ApplyPlaidConnectionHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Banking.ApplyPlaidSyncCommand,
    RentalCommand.Core.Banking.ApplyPlaidSyncResult,
    RentalCommand.Data.Banking.ApplyPlaidSyncHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Banking.ImportBankTransactionsCommand,
    RentalCommand.Core.Banking.ImportBankTransactionsResult,
    RentalCommand.Data.Banking.ImportBankTransactionsHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Banking.ReconcileBankTransactionCommand,
    RentalCommand.Core.Banking.ReconcileBankTransactionResult,
    RentalCommand.Data.Banking.ReconcileBankTransactionHandler>();
// The live scan-confirm endpoint admits only the five completed non-lease targets through this
// persistence-only writer. Lease confirmation returns 503 until its aggregate writer is complete.
builder.Services.AddScoped<RentalCommand.Data.Scanning.ProductionScanConfirmationTargetWriter>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Scanning.ConfirmScanDraftCommand,
    RentalCommand.Core.Scanning.ConfirmScanDraftResult,
    RentalCommand.Data.Scanning.ConfirmScanDraftHandler<
        RentalCommand.Data.Scanning.ProductionScanConfirmationTargetWriter>>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Leasing.PrepareMoveInCommand,
    RentalCommand.Core.Leasing.PrepareMoveInResult,
    RentalCommand.Data.Leasing.PrepareMoveInHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Leasing.AddEffectivePartyCommand,
    RentalCommand.Core.Leasing.LeasePartyMutationResult,
    RentalCommand.Data.Leasing.AddEffectivePartyHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Leasing.EndEffectivePartyCommand,
    RentalCommand.Core.Leasing.LeasePartyMutationResult,
    RentalCommand.Data.Leasing.EndEffectivePartyHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Leasing.ChangeEffectivePartyRoleCommand,
    RentalCommand.Core.Leasing.LeasePartyMutationResult,
    RentalCommand.Data.Leasing.ChangeEffectivePartyRoleHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Leasing.GrantTenantUserAccessCommand,
    RentalCommand.Core.Leasing.LeasePartyMutationResult,
    RentalCommand.Data.Leasing.GrantTenantUserAccessHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Leasing.RevokeTenantUserAccessCommand,
    RentalCommand.Core.Leasing.LeasePartyMutationResult,
    RentalCommand.Data.Leasing.RevokeTenantUserAccessHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Authorization.GrantOwnerUserAccessCommand,
    RentalCommand.Core.Authorization.OwnerRelationshipAccessMutationResult,
    RentalCommand.Data.Authorization.GrantOwnerUserAccessHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Authorization.RevokeOwnerUserAccessCommand,
    RentalCommand.Core.Authorization.OwnerRelationshipAccessMutationResult,
    RentalCommand.Data.Authorization.RevokeOwnerUserAccessHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Leasing.GivePossessionCommand,
    RentalCommand.Core.Leasing.GivePossessionResult,
    RentalCommand.Data.Leasing.GivePossessionHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Leasing.ReturnPossessionCommand,
    RentalCommand.Core.Leasing.ReturnPossessionResult,
    RentalCommand.Data.Leasing.ReturnPossessionHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Leasing.CancelPlannedRelationshipCommand,
    RentalCommand.Core.Leasing.CancelPlannedRelationshipResult,
    RentalCommand.Data.Leasing.CancelPlannedRelationshipHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Leasing.TransferLeaseManagementCommand,
    RentalCommand.Core.Leasing.TransferLeaseManagementResult,
    RentalCommand.Data.Leasing.TransferLeaseManagementHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Leasing.EditLeaseAgreementDraftCommand,
    RentalCommand.Core.Leasing.LeaseAgreementDraftMutationResult,
    RentalCommand.Data.Leasing.EditLeaseAgreementDraftHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Leasing.CreateLeaseAgreementSuccessorDraftCommand,
    RentalCommand.Core.Leasing.LeaseAgreementDraftMutationResult,
    RentalCommand.Data.Leasing.CreateLeaseAgreementSuccessorDraftHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Leasing.CreateLeaseAddendumDraftCommand,
    RentalCommand.Core.Leasing.LeaseAddendumDraftMutationResult,
    RentalCommand.Data.Leasing.CreateLeaseAddendumDraftHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Leasing.EditLeaseAddendumDraftCommand,
    RentalCommand.Core.Leasing.LeaseAddendumDraftMutationResult,
    RentalCommand.Data.Leasing.EditLeaseAddendumDraftHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Leasing.CorrectLeaseAddendumDraftCommand,
    RentalCommand.Core.Leasing.LeaseAddendumDraftMutationResult,
    RentalCommand.Data.Leasing.CorrectLeaseAddendumDraftHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Leasing.VoidLeaseAgreementCommand,
    RentalCommand.Core.Leasing.VoidLegalArtifactResult,
    RentalCommand.Data.Leasing.VoidLeaseAgreementHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Leasing.VoidLeaseAddendumCommand,
    RentalCommand.Core.Leasing.VoidLegalArtifactResult,
    RentalCommand.Data.Leasing.VoidLeaseAddendumHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Leasing.CloseTenantAccountCommand,
    RentalCommand.Core.Leasing.CloseTenantAccountResult,
    RentalCommand.Data.Leasing.CloseTenantAccountHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Leasing.CompleteTurnoverCommand,
    RentalCommand.Core.Leasing.CompleteTurnoverResult,
    RentalCommand.Data.Leasing.CompleteTurnoverHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Applications.RecordApplicationFeeCommand,
    RentalCommand.Core.Applications.ApplicationFinanceMutationResult,
    RentalCommand.Data.Applications.RecordApplicationFeeHandler>();
builder.Services.AddAtomicCommandHandler<
    RentalCommand.Core.Applications.RefundApplicationFeeCommand,
    RentalCommand.Core.Applications.ApplicationFinanceMutationResult,
    RentalCommand.Data.Applications.RefundApplicationFeeHandler>();

// Row-Level Security backstop: the interceptor supplies only compact canonical session coordinates.
// PostgreSQL revalidates them against live session/access rows before admitting portfolio scope.
builder.Services.AddSingleton<RentalCommand.Api.Data.RlsConnectionInterceptor>();
builder.Services.AddDbContext<RentalCommandDbContext>((sp, options) =>
{
    options.UseNpgsql(migrateOnly ? migratorConnectionString! : connectionString)
        .UseAtomicPersistenceKernel(sp)
        .AddInterceptors(sp.GetRequiredService<RentalCommand.Data.Auditing.AuditSaveChangesInterceptor>());
    if (!migrateOnly)
    {
        options.AddInterceptors(sp.GetRequiredService<RentalCommand.Api.Data.RlsConnectionInterceptor>());
    }
});

// --- ASP.NET Identity (int keys) ---
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredLength = 8;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.User.RequireUniqueEmail = true;
    })
    .AddSignInManager()
    .AddEntityFrameworkStores<RentalCommandDbContext>()
    .AddDefaultTokenProviders();

// --- Authentication: canonical JWT bearer ---
// Called after AddIdentity so the JWT bearer scheme (not Identity's cookie) is the default.
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SecretKey)),
            ValidateIssuer = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtSettings.Audience,
            ValidateLifetime = true,
            // A small skew tolerance (NOT the 5-min default, NOT zero): zero meant a token expiring
            // even a second early by the server clock was a hard 401 — turning any client/server clock
            // drift, or a request landing right on the 15-min boundary (e.g. a slow scan upload), into
            // an intermittent "valid token rejected" logout. 2 min absorbs real-world drift safely.
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        options.Events = new JwtBearerEvents
        {
            // Allow SignalR hubs (added later) to pass the token via query string.
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/api/v1/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = context =>
            {
                if (context.Exception is SecurityTokenExpiredException)
                {
                    context.Response.Headers.Append("X-Token-Expired", "true");
                }
                return Task.CompletedTask;
            }
        };
    });

// Platform super-admin allowlist (F6 / TSK-212): operator endpoints resolve the canonical JWT
// subject to the user's current database email, then evaluate the configured list. The gate fails
// closed when the list is empty or the subject no longer resolves.
var platformAdminAllowlist = RentalCommand.Api.Auth.PlatformAdminPolicy.BuildAllowlist(
    builder.Configuration
        .GetSection(RentalCommand.Core.Configuration.PlatformAdminOptions.SectionName)
        .Get<RentalCommand.Core.Configuration.PlatformAdminOptions>());

builder.Services.AddAuthorization(options =>
{
    RentalCommand.Api.Auth.PlatformAdminPolicy.Register(options, platformAdminAllowlist);
    options.AddPolicy(
        RentalCommand.Api.Auth.CanonicalManagementPolicy.Name,
        policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new RentalCommand.Api.Auth.CanonicalManagementRequirement()));
});
builder.Services.AddSingleton<IAuthorizationPolicyProvider, CapabilityAuthorizationPolicyProvider>();
builder.Services.AddScoped<IAuthorizationHandler, CapabilityAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, CanonicalManagementAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
builder.Services.AddScoped<RentalCommand.Core.Authorization.IActiveAccessContextResolver,
    ActiveAccessContextResolver>();
builder.Services.AddScoped<RentalCommand.Core.Authorization.IWorkspaceAuthorizationEvaluator,
    WorkspaceAuthorizationEvaluator>();
builder.Services.AddScoped<RentalCommand.Core.Authorization.IEffectiveAccessContextSelectionQuery,
    EffectiveAccessContextSelectionQuery>();
builder.Services.AddScoped<RentalCommand.Core.Authorization.IAccessEnvelopeQuery,
    AccessEnvelopeQuery>();
builder.Services.AddScoped<RentalCommand.Core.Authorization.IMembershipAssignmentScopeValidator,
    MembershipAssignmentScopeValidator>();
builder.Services.AddScoped<WorkspaceAccessRevisionGuard>();

// --- Auth services ---
builder.Services.AddHttpClient("GoogleAuth");
builder.Services.AddScoped<ICanonicalAccessTokenService, CanonicalAccessTokenService>();
builder.Services.AddSingleton(serviceProvider =>
    new RentalCommand.Core.Auth.RefreshCredentialTokenFactory(
        serviceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<AtomicAuthSessionCredentialOptions>>()
            .Value
            .SigningKey));
builder.Services.AddScoped<IAtomicAuthSessionCredentialService, AtomicAuthSessionCredentialService>();
builder.Services.AddScoped<IAuthEmailSender, OutboxAuthEmailSender>();
builder.Services.AddScoped<ICanonicalAccountBootstrapService, CanonicalAccountBootstrapService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IGoogleAuthService, GoogleAuthService>();
// On-demand tenant portal provisioning for the staff "grant portal access" endpoint. Fresh seed
// workspaces contain no tenants, and startup never scans existing tenants or creates accounts in a loop.
builder.Services.AddScoped<ITenantPortalProvisioningService, TenantPortalProvisioningService>();
builder.Services.AddScoped<IdentitySeeder>();
builder.Services.AddScoped<DemoDataSeeder>();

// --- CORS (restrict to the web app origin) ---
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "https://localhost:5667" };
builder.Services.AddCors(options =>
{
    options.AddPolicy("WebApp", policy =>
    {
        policy.WithOrigins(corsOrigins)
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
            .AllowAnyHeader()
            .WithExposedHeaders("X-Access-Envelope-Refresh")
            .AllowCredentials();
    });
});

// Serialize/accept enums as their string names (e.g. "InProgress", "Normal") rather than
// integers, matching what the SvelteKit client sends and renders. Without this the API
// binds enums as numbers and 400s on the client's string enum values.
// Dev-only simulation controllers ([SimulationOnly]) have all their routes stripped at startup when
// simulation is inactive (production / flag off), so they simply do not exist there.
var simulationEnabled = SimulationGate.IsEnabled(builder.Configuration, builder.Environment);
builder.Services.AddControllers(options =>
    {
        options.Conventions.Add(new SimulationOnlyConvention(simulationEnabled));
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddHealthChecks();

// Global exception handling: maps domain-rule violations to clean 400/409 ProblemDetails and prevents
// raw persistence faults (DbUpdate/Postgres constraint violations) from leaking SQL/type/stack/constraint
// names to the client (full detail is still logged server-side). See GlobalExceptionHandler.
builder.Services.AddExceptionHandler<RentalCommand.Api.GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// --- SignalR (realtime hubs) + DataUpdateService broadcaster ---
// Mirror the REST enum-as-string convention on realtime payloads too.
builder.Services.AddSignalR().AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddScoped<IDataUpdateService, DataUpdateService>();

// Realtime backplane bridge (TSK-624): LISTENs on the Postgres channel the Engine NOTIFYs, and
// re-broadcasts each cross-process entity change onto the SignalR hub via DataUpdateService. Without
// this, Engine-originated automation (rent charges, late fees, notices, scan completion, …) never
// pushes live — the hub is in-memory per-process and the Engine runs in a separate process.
builder.Services.AddHostedService<EntityChangeListener>();

// --- Domain feature services (per-entity scoped CRUD) ---
builder.Services.AddDomainServices();

// --- Outbox message publisher (API-side: enqueues rows; Engine dispatches them) ---
builder.Services.AddScoped<IMessagePublisher, RentalCommand.Data.Outbox.OutboxMessagePublisher>();

// --- Stripe payment services (gated — no-ops when Stripe keys are absent) ---
builder.Services.AddScoped<IStripePaymentService, StripePaymentService>();

// A concrete screening vendor is installed as a provider-neutral adapter. Until one is selected,
// integrated screening is honestly unavailable while external Zillow/other workflows remain usable.
builder.Services.AddScoped<RentalCommand.Core.Interfaces.IScreeningProvider,
    RentalCommand.Api.Services.Screening.DisabledScreeningProvider>();

// Admin Engine Health: reads the Engine's worker-heartbeat table + active LLM config to back
// the admin /admin/engine page (the Engine has no HTTP port, so the DB is the health contract).
builder.Services.AddScoped<RentalCommand.Api.Services.Admin.IAdminEngineStatusService,
    RentalCommand.Api.Services.Admin.AdminEngineStatusService>();

// --- Master simulation clock (TSK-615) ---
// Binds the ambient TimeProvider + IAppTimeZoneProvider. In production (or when Simulation:Enabled is
// false) this is TimeProvider.System — real clock, unchanged behavior. In non-prod with the flag on it
// binds the controllable SimulationTimeProvider; pinFrameworkAuthClock keeps cookie/security-stamp auth
// timing on the real clock even while domain time is simulated (S1).
builder.Services.AddSimulationClock(builder.Configuration, builder.Environment, pinFrameworkAuthClock: true);

var app = builder.Build();

if (migrateOnly)
{
    var migrationOptions = new DbContextOptionsBuilder<RentalCommandDbContext>()
        .UseNpgsql(migratorConnectionString!)
        .Options;
    await using (var migrationDb = new RentalCommandDbContext(migrationOptions))
    {
        await DatabaseMigrator.MigrateWithLockAsync(migrationDb);
    }

    await RentalCommand.Data.Security.RuntimeDatabaseRoleProvisioner.ProvisionAsync(
        migratorConnectionString!,
        connectionString,
        engineConnectionStringForMigration!,
        allowDevelopmentDefaults: builder.Environment.IsDevelopment());

    await using var seedScope = app.Services.CreateAsyncScope();
    await seedScope.ServiceProvider.GetRequiredService<IdentitySeeder>().SeedAsync();
    if (app.Configuration.GetValue<bool>("Seed:DemoData", false))
    {
        await seedScope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();
    }

    return;
}

// Behind Traefik (TLS terminator) the API receives plain HTTP on :8080, so honor X-Forwarded-Proto
// / X-Forwarded-For FIRST — otherwise Request.Scheme is "http", any absolute URL the API emits is
// http:// (browser mixed-content "Not Secure"), and the client IP is the proxy's.
//
// SECURITY (L-6): we do NOT trust forwarded headers from *any* caller — that lets a peer spoof
// X-Forwarded-For/Proto, poisoning the client IP used for refresh-token IP logging and the request
// scheme. The only ingress is Traefik on the internal Docker network, so we trust forwarded headers
// only from the proxy network(s). Configurable via ForwardedHeaders:KnownNetworks (a list of CIDRs);
// when unset we default to the RFC 1918 private ranges + loopback, which covers the Docker bridge the
// API actually sits on while still rejecting forwarded headers from any public source. The API is
// never published directly (compose keeps :8080 unpublished), so this is fail-safe if the topology
// changes.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    // Traefik adds one hop; allow a little headroom but don't trust an arbitrarily deep chain.
    ForwardLimit = 2
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();

var configuredKnownNetworks = app.Configuration
    .GetSection("ForwardedHeaders:KnownNetworks")
    .Get<string[]>();
var knownNetworkCidrs = configuredKnownNetworks is { Length: > 0 }
    ? configuredKnownNetworks
    : new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8", "::1/128" };

foreach (var cidr in knownNetworkCidrs)
{
    if (string.IsNullOrWhiteSpace(cidr)) continue;
    if (System.Net.IPNetwork.TryParse(cidr.Trim(), out var network))
    {
        forwardedHeadersOptions.KnownIPNetworks.Add(network);
    }
    else
    {
        app.Logger.LogWarning("Ignoring invalid ForwardedHeaders:KnownNetworks entry '{Cidr}'.", cidr);
    }
}
app.UseForwardedHeaders(forwardedHeadersOptions);

// Outermost exception wrapper: turns domain-rule violations into clean 400/409 ProblemDetails and keeps
// raw persistence faults from leaking internals (see GlobalExceptionHandler). Registered before the
// inline auth-context middleware below so anything that bubbles past it (DbUpdateException, domain
// validation, etc.) is mapped cleanly; MissingAuthContextException is still handled by that middleware
// and never reaches here.
app.UseExceptionHandler();

app.UseCors("WebApp");

// Map auth-context failures to a clean 401 instead of a 500. GetPortfolioId()/GetUserId() throw
// MissingAuthContextException when canonical access-context validation cannot establish the
// required workspace/user coordinates — without this the throw would surface as a 500.
// NOTE: catch the SPECIFIC type, NOT generic UnauthorizedAccessException — the BCL throws the latter
// for filesystem permission errors (e.g. an unwritable upload volume), and treating those as 401
// disguises infra failures as "session expired". Those now propagate to an honest 500.
// Registered high so it wraps controller execution.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (RentalCommand.Api.Controllers.MissingAuthContextException)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync("{\"error\":\"Unauthorized\"}");
        }
    }
});

app.UseAuthentication();
app.UseMiddleware<RentalCommand.Api.Auth.CanonicalAccessContextMiddleware>();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();

// --- SignalR hubs (auth required; websocket transports pass the JWT via ?access_token=) ---
app.MapHub<DataUpdateHub>("/api/v1/hubs/updates");

app.Run();

// Exposed for WebApplicationFactory<Program> in integration/Tier-2 tests.
public partial class Program;
