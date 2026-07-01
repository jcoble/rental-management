# Rental Command — Google Play Internal Testing Release Checklist

A precise, ordered checklist to publish the **Rental Command** Android app to the
Google Play **Internal testing** track so your dad (and other named testers) can
install it from the Play Store.

- **App id (package name):** `com.rentalcommand.rental_command`
- **Signed bundle to upload:** `mobile/build/app/outputs/bundle/prodRelease/app-prod-release.aab`
- **Version of this build:** `1.0.0 (versionCode 1)`
- **Backend the app talks to:** production — `https://rc.coblesolutions.com`

> ⚠️ **Verify current requirements at execution time.** Google changes the Play
> Console flow and policy wording regularly. Treat this checklist as the intended
> path; if a screen looks different, follow Google's on-screen prompts and the
> linked help pages. Last written 2026-06-18.

---

## 0. Before you start — what you need on hand

- A Google account to own the developer account (use a long-lived personal/business
  Google account you control, **not** a throwaway).
- A credit/debit card for the **one-time $25** developer registration fee.
- A government photo ID for identity verification (Google now verifies all new
  developer accounts).
- The signed prod-flavor `app-prod-release.aab` file (already built — see path above).
- The **privacy policy hosted at a public URL** (see step 5b). The Markdown +
  HTML source live in `Docs/legal/privacy-policy.md` / `Docs/legal/privacy-policy.html`.
  You must put the HTML somewhere publicly reachable first.
- A test login for the app (an email + password that works against
  `rc.coblesolutions.com`) in case Google's reviewer asks for app access.

---

## 1. Create a Google Play developer account ($25, one-time)

1. Go to <https://play.google.com/console/signup>.
2. Sign in with the Google account that will OWN the app.
3. Choose account type **Personal** (an individual account — simplest for one person).
   - If you'd rather it be under a business name, choose **Organization**, but that
     requires a D-U-N-S number and takes longer. **Personal is recommended here.**
4. Enter your legal name and contact details exactly as on your ID.
5. Pay the **$25 USD** one-time registration fee.
6. Complete **identity verification** — Google will ask you to upload a photo of a
   government ID and possibly an address document.
   - ⏳ **This can take ~1–2 days (sometimes longer) to be approved.** You cannot
     publish until the account is verified, so do this step first and wait.
7. Accept the Developer Distribution Agreement.

---

## 2. Create the app in Play Console

1. In Play Console, click **Create app**.
2. Fill in:
   - **App name:** `Rental Command`
   - **Default language:** English (United States) — `en-US`
   - **App or game:** **App**
   - **Free or paid:** **Free**
3. Check the **Declarations** boxes (Developer Program Policies + US export laws).
4. Click **Create app**.

---

## 3. Upload the signed app bundle to the Internal testing track

1. In the left nav: **Test and release → Testing → Internal testing**.
2. Click **Create new release**.
3. **App signing:** when prompted, **let Google Play manage your app signing key**
   (Play App Signing — this is the default and recommended). You are uploading with
   your **upload key**; Google re-signs with the app signing key it holds. This is
   exactly what the build is set up for.
   - The `.aab` is signed with the **upload key** `CN=Rental Command, O=Cole Solutions`
     (keystore at `mobile/android/upload-keystore.jks`). Keep that keystore + its
     password safe — you need the same upload key for every future update.
4. Build the prod-flavor app bundle if needed:
   `cd mobile && flutter build appbundle --release --flavor prod --dart-define=FLAVOR=prod`.
5. **Upload** `mobile/build/app/outputs/bundle/prodRelease/app-prod-release.aab`.
6. **Release name:** Play prefills `1 (1.0.0)` — leave it or set `1.0.0-internal`.
7. **Release notes:** add a short note, e.g. `Initial internal test build.`
   (put it inside the `<en-US>` language tag if asked).
8. Click **Next**, review any warnings, then **Save** (don't roll out yet — finish
   the App content forms in step 5 first, or Play will block the rollout).

---

## 4. Set up the internal testers list (Dad + you)

1. Still under **Internal testing**, open the **Testers** tab.
2. Under **Create email list**, make a list (e.g. `Family testers`) and add tester
   Google account emails — **your dad's Gmail address** and your own.
   - Testers MUST use the Google account tied to the device they install on.
3. Save the list and make sure its checkbox is ticked for this track.
4. Copy the **"Copy link"** opt-in URL (the join link) under **How testers join your
   test**. Send that link to your dad.
5. On the test device, the tester opens the link, taps **Accept invite / Become a
   tester**, then taps the link to **Download it on Google Play** — the app installs
   from the Play Store like any other app.
   - It can take a few minutes to several hours after rollout for the listing to
     appear for testers. If "item not found," wait and retry.

> Internal testing supports up to 100 testers and does **not** require the
> 20-tester / 14-day closed-testing gate that production needs.

---

## 5. Complete the required "App content" forms

Left nav: **Policy and programs → App content** (some items also under
**Monetisation setup** / **Store presence**). Work through each card until it shows
a green check. Rollout to testers is blocked until the mandatory ones are done.

### 5a. App access
- If **all** functionality is behind a login (Rental Command is), choose
  **"All or some functionality is restricted"** and provide **demo login
  credentials** (a working email + password for `rc.coblesolutions.com`) plus any
  steps the reviewer needs. This lets Google's reviewer get in.

### 5b. Privacy policy
- Paste the **public URL** where you hosted the privacy policy.
- You must host `Docs/legal/privacy-policy.html` (or an equivalent page) at a real
  https URL first — e.g. `https://coblesolutions.com/rental-command/privacy` or a
  GitHub Pages URL. The URL must be publicly reachable (no login).

### 5c. Data safety
- Fill in the **Data safety** form. It must **match the privacy policy** in
  `Docs/legal/privacy-policy.md`. Declare that the app **collects and stores**:
  - **Personal info:** name, email address, phone number, physical address,
    date of birth, other info (emergency contact, tenant/applicant notes).
  - **Financial info:** payment/rent records, financial account info (bank
    connection via processor), other financial info. *(Card/bank numbers are
    handled by Stripe — declare "processed by a payment processor"; the app does
    not store raw card numbers.)*
  - **Photos / documents / audio:** uploaded documents & scans (IDs, pay stubs,
    inspection photos), voice memo audio.
  - **App activity & device IDs:** push notification token, in-app messages.
  - For each: collected = **Yes**; shared with third parties = limited to service
    providers (Stripe for payments, Firebase for push, screening provider) — declare
    those as **service providers**, not "sold."
  - **Data is encrypted in transit:** Yes.
  - **Users can request deletion:** Yes — point to the contact email / deletion
    process described in the privacy policy.
  - **Data is NOT sold.**
- ⚠️ Re-read each Data safety question against the actual privacy policy before
  submitting — Google enforces consistency.

### 5d. Content rating
- Open the **Content rating** questionnaire, enter a contact email, choose
  category **Utility / Productivity / Other** (not a game).
- Answer all questions honestly — Rental Command has **no** violence, sexual
  content, gambling, or user-generated public content. It will rate **Everyone**.

### 5e. Target audience and content
- **Target age:** adults (18+) — this is a landlord business tool, **not** directed
  at children.
- Confirm the app is **not** primarily child-directed (answer No to "appeals to
  children").

### 5f. Ads
- **Does your app contain ads?** → **No**.

### 5g. Government apps
- **Is this a government app?** → **No**.

### 5h. Financial features (if shown)
- If a **Financial features** declaration appears (because the app handles rent
  payments), declare that payments are processed by **Stripe** (a licensed payment
  processor) and that the app itself is not a lender/bank. Provide any requested
  documentation.

### 5i. Other cards that may appear
- **News app?** No. **COVID-19 app?** No. **Data deletion** — provide the in-app or
  contact-email path described in the privacy policy.

---

## 6. Roll out to internal testers

1. Go back to **Internal testing → (your saved release) → Edit/Review release**.
2. Resolve any remaining errors (Play lists them at the top — usually a missing
   App content card from step 5).
3. Click **Start rollout to Internal testing** and confirm.
4. Wait for processing, then share the opt-in link (step 4) with your dad.

---

## 7. After internal testing — going to public production (later)

When you're ready for anyone to install it from the Play Store:

- New personal developer accounts must run a **closed test with at least 20 testers
  for 14 continuous days** before they can apply for production access. Internal
  testing does **not** satisfy this — plan for the closed test ahead of your public
  launch.
- You'll then complete a **production release** on the **Production** track and apply
  for production access; Google reviews it.
- Verify the **current** closed-testing / production requirements in Play Console at
  that time — Google adjusts these thresholds.

---

## Quick reference

| Item | Value |
|------|-------|
| Package name | `com.rentalcommand.rental_command` |
| App bundle (AAB) | `mobile/build/app/outputs/bundle/prodRelease/app-prod-release.aab` |
| Upload keystore | `mobile/android/upload-keystore.jks` (gitignored — back it up off-repo) |
| Upload key alias | `upload` |
| Version | `1.0.0 (versionCode 1)` |
| Privacy policy source | `Docs/legal/privacy-policy.md` / `.html` (must be hosted at a public URL) |
| Backend | `https://rc.coblesolutions.com` (production) |
| Track | Internal testing (≤100 testers, no 14-day gate) |
