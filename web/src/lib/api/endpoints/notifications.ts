import { api } from "../client";
import { idempotentMutation } from "../idempotency";
import type {
  NotificationItem,
  NotificationListParams,
  UnreadCountResponse,
  MyAlertsResponse,
  UpdateMyAlertsRequest,
  MorningBriefingSettingsResponse,
  UpdateMorningBriefingSettingsRequest,
  TeamRoutingRuleResponse,
  TeamRoutingRuleRecipientResponse,
  TeamRoutingRecipientPreview,
  UpsertTeamRoutingRuleRequest,
  TenantNoticePolicyResponse,
  UpsertTenantNoticePolicyRequest,
  WorkspaceNoticeTemplateResponse,
  CreateWorkspaceNoticeTemplateVersionRequest,
  NoticeMergeFieldHelpResponse,
  TenantNoticeRecipientPreviewResponse,
  NoticeDeliveryStatusResponse,
} from "$lib/api/types/notification";

export const notifications = {
  list: (params: NotificationListParams = {}) => {
    const search = new URLSearchParams();
    if (params.unreadOnly) search.set("unreadOnly", "true");
    if (params.take) search.set("take", String(params.take));
    if (params.skip) search.set("skip", String(params.skip));
    const query = search.toString();
    return api.get<NotificationItem[]>(
      `/notifications${query ? `?${query}` : ""}`
    );
  },

  unreadCount: () =>
    api.get<UnreadCountResponse>("/notifications/unread-count"),
  markAsRead: (id: number) =>
    idempotentMutation(`notifications:read:${id}`, (operationKey) =>
      api.post<void>(
        `/notifications/${id}/read`,
        {},
        {
          headers: { "Idempotency-Key": operationKey },
        }
      )
    ),
  markAllAsRead: () =>
    idempotentMutation("notifications:read-all", (operationKey) =>
      api.post<void>(
        "/notifications/read-all",
        {},
        {
          headers: { "Idempotency-Key": operationKey },
        }
      )
    ),
  broadcast: (data: {
    title: string;
    message: string;
    severity?: string;
    actionUrl?: string | null;
  }) =>
    idempotentMutation(
      `notifications:broadcast:${JSON.stringify(data)}`,
      (operationKey) =>
        api.post<NotificationItem>("/notifications/broadcast", data, {
          headers: { "Idempotency-Key": operationKey },
        })
    ),
  myAlerts: {
    get: () => api.get<MyAlertsResponse>("/my-alerts"),
    update: (request: UpdateMyAlertsRequest) =>
      idempotentMutation(
        `my-alerts:${JSON.stringify(request)}`,
        (operationKey) =>
          api.put<MyAlertsResponse>("/my-alerts", request, {
            headers: { "Idempotency-Key": operationKey },
          })
      ),
  },
  morningBriefing: {
    get: () =>
      api.get<MorningBriefingSettingsResponse>(
        "/team-routing/morning-briefing"
      ),
    update: (request: UpdateMorningBriefingSettingsRequest) =>
      idempotentMutation(
        `team-routing:morning-briefing:${JSON.stringify(request)}`,
        (operationKey) =>
          api.put<MorningBriefingSettingsResponse>(
            "/team-routing/morning-briefing",
            request,
            { headers: { "Idempotency-Key": operationKey } }
          )
      ),
  },
  teamRouting: {
    list: () => api.get<TeamRoutingRuleResponse[]>("/team-routing"),
    replace: (request: UpsertTeamRoutingRuleRequest) =>
      idempotentMutation(
        `team-routing:${JSON.stringify(request)}`,
        (operationKey) =>
          api.put<TeamRoutingRuleResponse>("/team-routing", request, {
            headers: { "Idempotency-Key": operationKey },
          })
      ),
    recipients: (ruleId: number) =>
      api.get<TeamRoutingRuleRecipientResponse[]>(
        `/team-routing/${ruleId}/recipients`
      ),
    preview: (ruleId: number) =>
      api.get<TeamRoutingRecipientPreview[]>(`/team-routing/${ruleId}/preview`),
  },
  tenantNotices: {
    listPolicies: () =>
      api.get<TenantNoticePolicyResponse[]>("/tenant-notices"),
    updatePolicy: (
      automationKey: string,
      request: UpsertTenantNoticePolicyRequest
    ) =>
      idempotentMutation(
        `tenant-notices:policy:${automationKey}:${JSON.stringify(request)}`,
        (operationKey) =>
          api.put<TenantNoticePolicyResponse>(
            `/tenant-notices/${encodeURIComponent(automationKey)}`,
            request,
            { headers: { "Idempotency-Key": operationKey } }
          )
      ),
    listTemplates: () =>
      api.get<WorkspaceNoticeTemplateResponse[]>("/tenant-notices/templates"),
    listDeliveries: (take = 50) =>
      api.get<NoticeDeliveryStatusResponse[]>(
        `/tenant-notices/deliveries?take=${encodeURIComponent(String(take))}`
      ),
    previewRecipients: (automationKey: string, leaseManagementId: number) =>
      api.get<TenantNoticeRecipientPreviewResponse[]>(
        `/tenant-notices/${encodeURIComponent(automationKey)}/recipients?leaseManagementId=${encodeURIComponent(String(leaseManagementId))}`
      ),
    mergeFields: (systemKey: string) =>
      api.get<NoticeMergeFieldHelpResponse[]>(
        `/tenant-notices/templates/${encodeURIComponent(systemKey)}/merge-fields`
      ),
    createTemplateVersion: (
      systemKey: string,
      request: CreateWorkspaceNoticeTemplateVersionRequest
    ) =>
      idempotentMutation(
        `tenant-notices:template:${systemKey}:${JSON.stringify(request)}`,
        (operationKey) =>
          api.post<TenantNoticePolicyResponse>(
            `/tenant-notices/templates/${encodeURIComponent(
              systemKey
            )}/versions`,
            request,
            { headers: { "Idempotency-Key": operationKey } }
          )
      ),
    restoreDefault: (systemKey: string) =>
      idempotentMutation(
        `tenant-notices:template:${systemKey}:restore`,
        (operationKey) =>
          api.post<TenantNoticePolicyResponse>(
            `/tenant-notices/templates/${encodeURIComponent(
              systemKey
            )}/restore-default`,
            {},
            { headers: { "Idempotency-Key": operationKey } }
          )
      ),
  },
};
