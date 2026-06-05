# Mobile Docs Section Implementation Plan

> **For agentic workers:** This is a content-authoring task (markdown knowledge-base
> articles), not a code feature. There is no TDD loop; the "tests" are the existing
> KnowledgeBase loader contract plus `svelte-check` / `pnpm build` / `dotnet build`.
> Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a dedicated **Mobile** docs category teaching a non-technical landlord how
to run the business from their phone — capture by photo, voice (coming soon), running the
business day-to-day, and install/setup.

**Architecture:** Reuse the existing docs system exactly. Docs are markdown files in
`RentalCommand.Api/KnowledgeBase/*.md` with YAML-ish frontmatter (`title`, `category`,
`slug`, `order`, `summary`, `keywords`). `KnowledgeBaseService` loads, orders, and serves
them via the anonymous `GET /api/v1/docs` + `GET /api/v1/docs/{slug}` endpoints, which the
public SvelteKit `/docs` site renders. We add four new `.md` files under the new
`category: Mobile` and register that category in the service's `CategoryOrder` array so the
section appears in the right place in the sidebar. No backend logic, no migration, no web
component changes — the nav is data-driven off the index.

**Tech Stack:** .NET 10 (`KnowledgeBaseService`), markdown frontmatter, SvelteKit `/docs`
renderer (`marked`).

---

## Background / what already exists (read first)

- Loader + ordering: `RentalCommand.Api/Services/Domain/KnowledgeBaseService.cs`
  - `CategoryOrder` (lines ~28-39) is the hardcoded category sort order. Categories not
    listed sort *after* listed ones, alphabetically. **Mobile must be added here** or it
    lands at the bottom.
  - Frontmatter keys parsed: `title`, `category`, `slug`, `order`, `summary`, `keywords`.
    `order` defaults to 1000; within a category, articles sort by `order` then title.
- Content folder: `RentalCommand.Api/KnowledgeBase/*.md` (shipped via csproj
  `Content Include="KnowledgeBase\**\*.md"`). New `.md` files there are picked up
  automatically — no registry edit needed besides `CategoryOrder`.
- Existing scan/voice articles to NOT duplicate or contradict:
  - `scanning-documents.md` (category "Scan & Intake") — the desktop/web scan flow.
  - `voice-notes.md` (category "Scan & Intake") — **web mic → transcript → draft** (LIVE).
    This is distinct from the *future App-Actions voice commands* ("Hey, log an expense").
    The Mobile/voice article must frame **voice commands** as coming soon and not claim
    they exist; it may point at the live voice-notes feature as what works today.
- Renderer: `web/src/routes/(public)/docs/[slug]/+page.server.ts` runs `marked` on the
  body. Standard markdown images `![alt](/marketing/mobile/foo.png)` render as `<img>`.
  Image convention (from the welcome page): real device captures live in
  `web/static/marketing/mobile/` and are referenced as `/marketing/mobile/...`. We do NOT
  have real phone screenshots, so we reference those paths and leave
  `TODO(real-screenshots)` HTML comments where they go — no fabricated images.
- Sidebar nav is fully data-driven from the index (`+layout.svelte` / `[slug]/+page.svelte`
  iterate `data.index.categories`). Adding articles in a new category is all that's needed
  for them to appear; no Svelte edits required.

## File Structure

- Create: `RentalCommand.Api/KnowledgeBase/mobile-overview.md` (category Mobile, order 1)
- Create: `RentalCommand.Api/KnowledgeBase/mobile-capture-by-photo.md` (order 2)
- Create: `RentalCommand.Api/KnowledgeBase/mobile-running-the-business.md` (order 3)
- Create: `RentalCommand.Api/KnowledgeBase/mobile-voice-commands.md` (order 4)
- Create: `RentalCommand.Api/KnowledgeBase/mobile-install-and-setup.md` (order 5)
- Modify: `RentalCommand.Api/Services/Domain/KnowledgeBaseService.cs` — add `"Mobile"` to
  `CategoryOrder` (placed after "Operations", before "AI Assistant" — phone usage is an
  operational/how-to topic, sits well after the core domain categories).

Five short, focused articles instead of one giant page: matches the existing one-topic-per-
file pattern and keeps each page skimmable for a non-technical reader.

---

### Task 1: Add the Mobile category to the ordering

**Files:**
- Modify: `RentalCommand.Api/Services/Domain/KnowledgeBaseService.cs` (the `CategoryOrder` array)

- [ ] **Step 1: Insert "Mobile" into `CategoryOrder`**

Place it after `"Operations"` and before `"AI Assistant"`:

```csharp
        "Scan & Intake",
        "Operations",
        "Mobile",
        "AI Assistant",
```

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build RentalCommand.Api/RentalCommand.Api.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add RentalCommand.Api/Services/Domain/KnowledgeBaseService.cs
git commit -m "docs(kb): register Mobile category in docs ordering"
```

---

### Task 2: Mobile overview article (section landing page)

**Files:**
- Create: `RentalCommand.Api/KnowledgeBase/mobile-overview.md`

- [ ] **Step 1: Write the article**

Frontmatter + body. `order: 1` so it's the first Mobile item. Plain language, task-
oriented, links to the other Mobile articles by slug. Sets expectations: what works on the
phone today, what's coming.

```markdown
---
title: Rental Command on Your Phone
category: Mobile
slug: mobile-overview
order: 1
summary: Run the business from your phone — what works today, and where to start.
keywords: mobile, phone, app, android, ios, on the go, overview, getting started on phone
---

Rental Command is built to be run from your phone. Most days you won't sit at a computer —
you'll be standing in a unit, picking up a check, or talking to a tenant. This section
explains how to do the everyday jobs from your phone.

## What you can do from your phone today

- **Snap a photo of a document** — a receipt, a rent check, a bill, or a lease — and let
  the app fill in the record for you. See **Capture by photo**.
- **Log expenses, record payments, open work orders, and check your dashboard** — all the
  daily jobs. See **Running the business from your phone**.
- **Record a quick voice note** for a maintenance issue and turn it into a draft. See
  **Voice notes** (under Scan & Intake).

## Coming soon

- **Voice commands** — saying "log a $40 plumbing expense for 12 Oak Street" instead of
  tapping through screens. This isn't built yet; see **Voice commands** for what's planned.

## Where to start

If you're new, do one job end to end on your phone: photograph a receipt, confirm the
draft, and watch it become an expense. Once you've seen that loop, everything else is the
same idea. Start with **Capture by photo**.
```

- [ ] **Step 2: Commit**

```bash
git add RentalCommand.Api/KnowledgeBase/mobile-overview.md
git commit -m "docs(kb): add Mobile overview article"
```

---

### Task 3: Capture by photo article (the live flagship flow)

**Files:**
- Create: `RentalCommand.Api/KnowledgeBase/mobile-capture-by-photo.md`

- [ ] **Step 1: Write the article**

This is the LIVE scan → draft → confirm flow, told from the phone. Cover: what to
photograph, how scan→draft→confirm works, confidence chips, fixing a field before
confirming. Reference (don't fabricate) screenshots under `/marketing/mobile/`, with a
`TODO(real-screenshots)` note. Do NOT over-claim — only describe the flow that exists.

```markdown
---
title: Capture by Photo
category: Mobile
slug: mobile-capture-by-photo
order: 2
summary: Photograph a receipt, check, bill, or lease and let the app do the typing.
keywords: photo, camera, capture, scan, receipt, check, bill, lease, draft, confirm, confidence, mobile
---

The whole idea of Rental Command is **the computer does the typing for you**. Instead of
keying in a receipt, you take a picture of it and confirm what the app read. This is the
single most useful thing you can do from your phone.

## What to photograph

Anything you'd otherwise type up:

- **Receipts and bills** → become an **expense**.
- **Rent checks and payment slips** → become a **payment**.
- **Invoices from a vendor** → become an **expense** (and can attach to a property).
- **Leases** → become a **lease** record.

Hold the phone steady, get the whole document in frame, and make sure the amounts and
dates are readable. A clear, flat, well-lit photo reads far better than a crumpled one.

<!-- TODO(real-screenshots): add a captioned phone screenshot of the camera/upload step
     at /marketing/mobile/capture-camera.png once real device captures are available. -->

## How it works: photo → draft → confirm

1. Open **Scan a Document** and choose what you're scanning (Receipt / Bill, Rent Check,
   Maintenance Request, or Lease).
2. Take the photo (or pick one from your camera roll).
3. The app uploads it and reads it. You'll see **"Reading your document…"** for a few
   seconds.
4. It opens a **draft** with the fields it found filled in.
5. Check the fields, fix anything that's wrong, and tap **Confirm**.

Nothing is saved as a real record until you confirm. A draft is a safe halfway point — you
always get the final say.

<!-- TODO(real-screenshots): add a phone screenshot of a draft with extracted fields at
     /marketing/mobile/capture-draft.png. -->

## Reading the confidence chips

Next to the fields it filled in, the app shows a small **confidence** indicator — how sure
it is about what it read. A high-confidence field is very likely correct; a lower one is
worth a second look. Use the chips as a guide for where to focus: skim the confident
fields, double-check the unsure ones.

## Fixing a field before you confirm

If something is wrong — the amount is off, the date is misread, or it picked the wrong
property — just tap the field and edit it, the same as filling in any form. Then tap
**Confirm**. The corrected values are what get saved.

For receipts you can also mark it **already paid** or an **unpaid bill**, and attach it to
a property. For a rent check, pick **which lease** the payment is for. The draft tells you
what it still needs from you.

## If the photo can't be read

If a photo is too blurry or dark to read, you'll get a clear message rather than a wrong
guess. Retake the photo in better light, or type the record in by hand. You can also reject
a scan you don't want — it won't create anything.

## See also

- **Voice notes** — speak a maintenance issue instead of photographing it.
- **Running the business from your phone** — the everyday jobs, step by step.
```

- [ ] **Step 2: Commit**

```bash
git add RentalCommand.Api/KnowledgeBase/mobile-capture-by-photo.md
git commit -m "docs(kb): add Capture by Photo mobile article"
```

---

### Task 4: Running the business from your phone (day-in-the-life)

**Files:**
- Create: `RentalCommand.Api/KnowledgeBase/mobile-running-the-business.md`

- [ ] **Step 1: Write the article**

Task-oriented "To do X, …" steps for the four everyday jobs: log an expense, record a
payment, open a work order, check the dashboard. Keep it mobile-first and honest — these
all exist in the app today.

```markdown
---
title: Running the Business From Your Phone
category: Mobile
slug: mobile-running-the-business
order: 3
summary: The everyday jobs — log an expense, record a payment, open a work order, check the dashboard.
keywords: mobile, phone, expense, payment, work order, dashboard, daily, on the go, day in the life
---

Here are the jobs you'll do most often, each as a short set of steps you can follow on your
phone. If you have the source document in hand (a receipt, a check), remember you can
usually **photograph it** instead — see **Capture by photo**.

## To log an expense

1. Open the menu and tap **Expenses** (or photograph the receipt from **Scan a Document**).
2. Tap **Add expense**.
3. Enter the **amount**, pick a **category**, and choose the **property** it's for.
4. Mark whether it's **already paid** or an **unpaid bill**, and set the date.
5. Tap **Save**.

<!-- TODO(real-screenshots): add a phone screenshot of the Add Expense form at
     /marketing/mobile/log-expense.png. -->

## To record a rent payment

1. Open **Payments**, or open the **lease** the payment is for.
2. Tap **Add payment** (or **Record payment** on the lease).
3. Enter the **amount** and **date received**, and the **method** (cash, check, transfer).
4. Tap **Save**. The lease balance updates automatically.

If you're holding a rent check, photographing it from **Scan a Document** is faster — the
app reads the amount and you just pick the lease.

## To open a work order

1. Open **Work Orders** and tap **New work order** (or photograph a maintenance request,
   or record a **voice note** describing the problem).
2. Choose the **property** (and unit if it applies).
3. Describe the problem and set a **priority**.
4. Tap **Save**. You can assign a vendor and track it to done from the same screen.

<!-- TODO(real-screenshots): add a phone screenshot of the New Work Order form at
     /marketing/mobile/work-order.png. -->

## To check the dashboard

Tap **Dashboard** (your home screen). At a glance you'll see your money snapshot,
occupancy, overdue rent, open work orders, today's briefing, and recent activity. It's the
fastest way to see "what needs me today" while you're out and about.

<!-- TODO(real-screenshots): add a phone screenshot of the mobile dashboard at
     /marketing/mobile/dashboard.png. -->

## See also

- **Capture by photo** — let a photo do the data entry.
- **Install and setup** — getting the app on your phone.
```

- [ ] **Step 2: Commit**

```bash
git add RentalCommand.Api/KnowledgeBase/mobile-running-the-business.md
git commit -m "docs(kb): add Running the Business From Your Phone article"
```

---

### Task 5: Voice commands (coming soon)

**Files:**
- Create: `RentalCommand.Api/KnowledgeBase/mobile-voice-commands.md`

- [ ] **Step 1: Write the article**

Honest "coming soon" framing. Point at the LIVE voice-notes feature for what works today;
do NOT document commands that don't exist. Keep it light.

```markdown
---
title: Voice Commands
category: Mobile
slug: mobile-voice-commands
order: 4
summary: Hands-free voice commands are planned. Here's what's coming — and what you can do with your voice today.
keywords: voice, voice commands, speak, hands free, dictate, coming soon, mobile, microphone
---

> **Coming soon.** Spoken voice *commands* — telling the app to do a task out loud — aren't
> built yet. This page explains what's planned so you know what to expect, and points you to
> the voice feature that **does** work today.

## What you can do with your voice today

You can already record a **voice note** and have the app turn it into a draft — perfect for
capturing a maintenance issue while you're standing in front of it. For example, say *"The
furnace at 12 Oak Street is making a loud banging noise"* and the app transcribes it and
creates a work-order draft for you to confirm. See **Voice notes** (under Scan & Intake)
for how to use it.

## What's planned

The goal is to let you *run a task* by speaking, instead of tapping through screens — for
example asking the app to log an expense or record a payment out loud, then confirming the
draft it builds. As with everything else in Rental Command, a spoken command would create a
**draft** you confirm — never a silent change.

We'll document the exact phrases here once the feature ships. Until then, use **voice
notes** for hands-free maintenance capture, and **photo capture** for receipts, checks,
and bills.

## See also

- **Voice notes** — the live "speak it, confirm a draft" feature.
- **Capture by photo** — let a photo do the typing.
```

- [ ] **Step 2: Commit**

```bash
git add RentalCommand.Api/KnowledgeBase/mobile-voice-commands.md
git commit -m "docs(kb): add Voice Commands (coming soon) mobile article"
```

---

### Task 6: Install and setup (Android first)

**Files:**
- Create: `RentalCommand.Api/KnowledgeBase/mobile-install-and-setup.md`

- [ ] **Step 1: Write the article**

Android-first. Camera/mic permissions. Be honest about offline/iOS — keep it general, do
not over-promise. Today the product is used in a mobile browser; a native app is on the
roadmap. State only what's true.

```markdown
---
title: Install and Setup
category: Mobile
slug: mobile-install-and-setup
order: 5
summary: Get Rental Command on your phone, allow camera and microphone, and know what works offline.
keywords: install, setup, android, ios, iphone, permissions, camera, microphone, offline, add to home screen, mobile
---

You can use Rental Command on your phone right now in your phone's web browser — there's
nothing to download to get started. A dedicated app is on the roadmap; this page covers how
to set things up today and what's coming.

## Get it on your phone (Android first)

1. Open your phone's web browser (Chrome on Android) and go to your Rental Command address.
2. Sign in with your email and password.
3. **Add it to your home screen** so it opens like an app: in the browser menu, choose
   **Add to Home screen**. You'll get an icon you can tap straight to the app.

iPhone works in Safari the same way (**Share → Add to Home Screen**). The experience is
tuned for Android first; iPhone support is being refined.

<!-- TODO(real-screenshots): add an "Add to Home screen" walkthrough screenshot at
     /marketing/mobile/add-to-home-screen.png once real captures are available. -->

## Allow camera and microphone

The photo and voice features need permission to use your phone's hardware:

- **Camera** — needed to photograph receipts, checks, bills, and leases. The first time you
  take a photo, your browser asks to use the camera. Tap **Allow**.
- **Microphone** — needed to record a voice note. The first time you record, your browser
  asks to use the microphone. Tap **Allow**.

If you tapped "block" by mistake, you can re-enable these in your browser's site settings
for the Rental Command address.

## Working offline

Rental Command is an online app — it needs an internet connection to save records, read
your documents, and show live numbers. If you lose signal mid-task, finish the step once
you're back online. We don't recommend relying on it with no connection.

## Trouble signing in?

If a page won't load or you get signed out, close the tab and reopen the app from your home
screen icon, then sign in again. If it keeps happening, check your internet connection
first.

## See also

- **Capture by photo** — the first thing to try once you're set up.
- **Running the business from your phone** — the everyday jobs.
```

- [ ] **Step 2: Commit**

```bash
git add RentalCommand.Api/KnowledgeBase/mobile-install-and-setup.md
git commit -m "docs(kb): add Install and Setup mobile article"
```

---

### Task 7: Verify end-to-end

**Files:** none (verification only)

- [ ] **Step 1: .NET build (KB content ships via csproj content include)**

Run: `dotnet build RentalCommand.Api/RentalCommand.Api.csproj`
Expected: Build succeeded.

- [ ] **Step 2: KB loader tests still pass (sanity — content-agnostic, but confirm)**

Run: `dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter KnowledgeBase`
Expected: Passed.

- [ ] **Step 3: Web type check**

Run: `cd web && npx svelte-check --threshold error`
Expected: 0 errors (no web source changed; this is a guard).

- [ ] **Step 4: Web build**

Run: `cd web && pnpm build`
Expected: build completes.

- [ ] **Step 5: (Optional, if dev env up) eyeball the section**

Start the API + web, open `/docs`, confirm a **Mobile** category appears with the five
articles, the sidebar groups them under Mobile, and each page renders (TODO comments are
HTML comments and won't show).

---

## Self-Review

**Spec coverage:**
- Capture by photo (scan→draft→confirm, confidence chips, fix a field) → Task 3. ✓
- Voice commands (coming soon, light, don't document non-existent commands) → Task 5. ✓
- Running the business from a phone (log expense, record payment, open work order, check
  dashboard, task-oriented) → Task 4. ✓
- Install / setup (Android first, camera/mic permissions, offline, honest) → Task 6. ✓
- Add to existing docs system, same pattern, register in nav/category → Tasks 1-6 (new
  category in `CategoryOrder`, frontmatter matching existing files, data-driven nav). ✓
- Screenshot placeholders referencing `/marketing/mobile/...` with `TODO(real-screenshots)`,
  no fabricated images → Tasks 3, 4, 6. ✓
- No backend change beyond the category list, no migration → confirmed. ✓
- Short spec at `Docs/plans/mobile-docs-section.md` (writing-plans) → this file. ✓

**Placeholder scan:** Article bodies are complete; the only "TODO" tokens are the
intentional `TODO(real-screenshots)` HTML comments the spec asks for. ✓

**Consistency:** Slugs (`mobile-overview`, `mobile-capture-by-photo`,
`mobile-running-the-business`, `mobile-voice-commands`, `mobile-install-and-setup`),
category string `"Mobile"`, and orders 1-5 are consistent across all tasks and match the
`CategoryOrder` insert. Cross-links reference real existing slugs/titles (Voice notes,
Scan & Intake). ✓
