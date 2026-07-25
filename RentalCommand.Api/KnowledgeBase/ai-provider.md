---
title: AI Provider
category: Settings
slug: ai-provider
order: 2
summary: Configure the workspace AI provider used by Scan / Add.
keywords: AI provider, BYOK, bring your own key, OpenAI, Anthropic, API key, credential, Scan Add, scan, integrations, settings, rotate, test
---

Rental Command uses the workspace AI provider for **Scan / Add** document extraction. A workspace administrator configures it from **Settings → Setup & import → Manage AI provider** at `/settings/integrations/ai`.

## Supported providers

The provider can be **OpenAI** or **Anthropic**. Choose the provider, enter the model ID, and paste the API key for that provider.

The default model IDs shown by the settings page are:

- OpenAI: `gpt-4o`
- Anthropic: `claude-3-5-sonnet-latest`

## Test before saving

Use **Test credential** before saving. Rental Command verifies the provider, model, and key together. The save action stays unavailable until the exact provider, model, and key combination has passed the test.

After a successful test:

- **Activate provider** saves the first workspace credential.
- **Rotate credential** replaces an existing credential with a newly tested provider/model/key combination.
- **Remove provider** deletes the configured workspace AI credential.

## Key handling

The API key field is password-style input. After save, Rental Command does not show the key again.

Saved keys are encrypted at rest and are write-only from the settings screen. To change a key, paste a replacement key, test it, then rotate the credential.

## What happens if no provider is configured

If the workspace has no AI provider configured, **Scan / Add** stops with a clear missing-provider message. It does not fall back to a shared Rental Command key.

## Who can manage it

The Settings entry is shown only to users with workspace integration-management access, and every AI-provider status, test, activate, rotate, and remove operation requires that access. Opening the route directly does not grant access to those operations. Record access, property scope, and role capabilities still apply to the documents and drafts created through Scan / Add.
