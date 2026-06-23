/**
 * Shared registry of onboarding-wizard steps. This is the single source of truth that both the
 * wizard (`/onboarding`) and the Settings page consult, so the two stay in lock-step:
 *
 *  - the wizard renders one panel per `WizardStepKey` (split into 1–2 fields with plain-English
 *    help via {@link WizardStep});
 *  - Settings sections render a "Walk me through this" link that deep-links into the matching step
 *    (`/onboarding?step=<key>&from=settings`);
 *  - finishing a step deep-links back to wherever the user came from (the `from` query param).
 *
 * Keep the `key` values stable — they are used in URLs and `localStorage` progress keys.
 *
 * NOTE on docs links: `docsSlug` points at `/docs/<slug>` (the existing API-backed docs surface).
 * Each slug MUST resolve to a real, published Knowledge Base article (under
 * `RentalCommand.Api/KnowledgeBase/*.md`) so the "Read the step-by-step guide" link never dead-ends
 * on the docs error page (A12). When authoring a more specific article for a step, point its
 * `docsSlug` at the new file's slug; until then, reuse the closest existing article below.
 */
export type WizardStepKey =
	| 'portfolio'
	| 'owner'
	| 'property'
	| 'tenants'
	| 'lease'
	| 'notifications'
	| 'texting';

export interface WizardStepMeta {
	key: WizardStepKey;
	/** Short tab/label text. */
	label: string;
	/** Plain-English title shown at the top of the step panel. */
	title: string;
	/** Lucide icon name imported by the wizard (kept as a string so this module stays icon-free). */
	icon:
		| 'Building'
		| 'UserCircle2'
		| 'Home'
		| 'Users'
		| 'FileText'
		| 'Bell'
		| 'MessageSquare';
	/** WHAT this is + WHY we need it, for a non-technical landlord. */
	explanation: string;
	/** "Where do I find this?" hint — where the value lives on paper / in a provider account. */
	whereToFind: string;
	/** Docs article slug → `/docs/<slug>`. Must be a real, published Knowledge Base article (A12). */
	docsSlug: string;
	/**
	 * Whether this step is part of the *core* first-run flow (portfolio → lease). The two
	 * provider-setup steps (notifications, texting) are optional add-ons reached from Settings
	 * or the optional tail of the wizard, and are clearly marked skippable.
	 */
	core: boolean;
	/** `data-testid` anchor of the matching Settings section, for the wizard→settings deep-link back. */
	settingsAnchor?: string;
}

export const WIZARD_STEPS: WizardStepMeta[] = [
	{
		key: 'portfolio',
		label: 'Portfolio',
		title: 'Name your rental business',
		icon: 'Building',
		explanation:
			'Your portfolio is the name for your whole rental business — it shows at the top of every report and reminder. You can rename it anytime in Settings.',
		whereToFind:
			"There's nothing to look up. Use whatever you call your rentals — your own name, a family name, or your LLC. Example: “Smith Family Rentals”.",
		docsSlug: 'settings-and-notifications',
		core: true,
		settingsAnchor: 'settings-portfolio-basics',
	},
	{
		key: 'owner',
		label: 'Owner',
		title: 'Who owns the properties?',
		icon: 'UserCircle2',
		explanation:
			"The owner is the person or company that legally holds the property. We use it on owner reports and tax forms (like 1099s) later. If you own them yourself, that's just you as a person.",
		whereToFind:
			'The owner name and tax ID are on your property deed, your LLC paperwork, or a past tax return (Schedule E / 1099). The tax ID is an SSN for a person or an EIN for an LLC/trust.',
		docsSlug: 'getting-started',
		core: true,
	},
	{
		key: 'property',
		label: 'Property',
		title: 'Add a property',
		icon: 'Home',
		explanation:
			'A property is a single building or address. Inside it are the rentable units: a house is one unit, a duplex is two, an apartment building has as many units as it has apartments.',
		whereToFind:
			'The address is on your deed, tax bill, or insurance policy. Bedrooms, bathrooms, and the rent you ask for come from your listing or current lease.',
		docsSlug: 'properties-and-units',
		core: true,
	},
	{
		key: 'tenants',
		label: 'Tenants',
		title: 'Add your tenants',
		icon: 'Users',
		explanation:
			"Tenants are the people who rent from you. Adding their email or phone lets the app send rent reminders and let them into the tenant portal — both are optional, so add what you have.",
		whereToFind:
			"Names are on the signed lease. Email and phone are on the rental application or wherever you usually text them. It's fine to leave contact info blank for now.",
		docsSlug: 'tenants-and-applications',
		core: true,
	},
	{
		key: 'lease',
		label: 'Lease',
		title: 'Create the first lease',
		icon: 'FileText',
		explanation:
			'A lease ties a tenant to a unit and sets the rent, the dates, and the deposit. This is what drives rent charges, late fees, and renewal reminders.',
		whereToFind:
			'Everything here is on the signed lease agreement: the monthly rent, start and end dates, the security deposit, and the day rent is due. Have the lease handy — or snap a photo and let the computer read it.',
		docsSlug: 'leases',
		core: true,
	},
	{
		key: 'notifications',
		label: 'Email alerts',
		title: 'Where should alerts go?',
		icon: 'Bell',
		explanation:
			"This is the email address where the app sends you rent reminders, late-fee notices, and your daily briefing. Leave it blank to use your login email — most landlords do.",
		whereToFind:
			'Just your own email inbox — the one you check most. This is for alerts to YOU, not to your tenants.',
		docsSlug: 'settings-and-notifications',
		core: false,
		settingsAnchor: 'settings-notification-email',
	},
	{
		key: 'texting',
		label: 'Text messages',
		title: 'Turn on text messages (optional)',
		icon: 'MessageSquare',
		explanation:
			"Texting your tenants needs a SignalWire account — a service that sends the SMS for you. This step is completely optional; everything works on email and in-app alerts without it.",
		whereToFind:
			"Sign in at signalwire.com. The Project ID and Space URL are on your SignalWire dashboard; create an API Token under “API → Credentials”; the From Number is a phone number you buy in “Phone Numbers”. Copy each value over exactly.",
		docsSlug: 'settings-and-notifications',
		core: false,
		settingsAnchor: 'settings-notification-delivery',
	},
];

/** Steps shown in the core first-run flow (portfolio → lease). */
export const CORE_WIZARD_STEPS = WIZARD_STEPS.filter((s) => s.core);

export interface OnboardingSetupShortcut {
	key: 'scan-new-rental' | 'spreadsheet-import';
	label: string;
	description: string;
	href: string;
	primary: boolean;
}

export const ONBOARDING_SETUP_SHORTCUTS: OnboardingSetupShortcut[] = [
	{
		key: 'scan-new-rental',
		label: 'Scan a lease',
		description: 'Start from a signed lease photo or PDF and confirm the property, unit, tenant, and lease it finds.',
		href: '/scan/new-rental',
		primary: true,
	},
	{
		key: 'spreadsheet-import',
		label: 'Import from a spreadsheet',
		description: 'Bring in existing properties, units, or tenants from a CSV file.',
		href: '/import',
		primary: false,
	},
];

/**
 * Settings hub sections — the single source of plain-English copy for the tabbed Settings page
 * (`/settings`). Settings and the onboarding wizard speak with ONE voice: where a section has a
 * matching wizard step the copy is reused verbatim from {@link WIZARD_STEPS} via `wizardStep`, and
 * sections beyond the first-run flow (automations, team, owners, import, activity history) declare
 * their own ELI5 copy here.
 *
 * Each section answers, for a non-technical landlord:
 *   - `intro`     — WHAT this tab is, in one or two plain sentences.
 *   - `why`       — WHY it matters / what it does for them.
 *   - `whereToFind` — (optional) where to get any value the tab asks for (mirrors the wizard hint).
 *   - `docsSlug`  — `/docs/<slug>` "Learn more" placeholder (graceful if the article isn't written).
 *   - `walkThrough` — (optional) wizard step key, so the tab can show a "Walk me through this" chip.
 *   - `linkTo` / `linkLabel` — (optional) for sections that live on their own page (team, owners,
 *     import, activity history): the tab explains them and sends the landlord to the real page
 *     rather than duplicating that page's form (detail-page-not-modal house rule).
 */
export type SettingsSectionKey =
	| 'portfolio'
	| 'notifications'
	| 'automations'
	| 'messaging'
	| 'team'
	| 'owners'
	| 'setup'
	| 'activity';

export interface SettingsSectionMeta {
	key: SettingsSectionKey;
	/** Short tab label. */
	label: string;
	/** Lucide icon name (kept as a string so this module stays icon-free). */
	icon:
		| 'Building'
		| 'Bell'
		| 'Wand2'
		| 'MessageSquare'
		| 'Shield'
		| 'BadgeDollarSign'
		| 'Upload'
		| 'History';
	/** Plain-English heading shown at the top of the tab. */
	title: string;
	/** One or two plain sentences: WHAT this tab is. */
	intro: string;
	/** WHY it matters / what it does for the landlord. */
	why: string;
	/** Optional "where do I find this?" hint, for tabs that ask for outside values. */
	whereToFind?: string;
	/** `/docs/<slug>` placeholder for "Learn more". */
	docsSlug: string;
	/** Wizard step key, when this section maps to a guided step (shows a "Walk me through this" chip). */
	walkThrough?: WizardStepKey;
	/** For sections that live on their own page: where the full page lives. */
	linkTo?: string;
	/** Button label for `linkTo`. */
	linkLabel?: string;
}

export const SETTINGS_SECTIONS: SettingsSectionMeta[] = [
	{
		key: 'portfolio',
		label: 'Portfolio',
		icon: 'Building',
		title: 'Your rental business',
		intro:
			'The name, time zone, and a few everyday defaults for your whole rental business. The name shows at the top of every report and reminder.',
		why: 'Getting the time zone right keeps rent due-dates and reminders on YOUR clock. The collection day sets when each month’s rent is considered due.',
		whereToFind:
			'Nothing to look up — use whatever you call your rentals (your name, a family name, or your LLC). Pick the time zone where the properties are.',
		docsSlug: 'settings-and-notifications',
		walkThrough: 'portfolio',
	},
	{
		key: 'notifications',
		label: 'Notifications',
		icon: 'Bell',
		title: 'How you hear from the app',
		intro:
			'Choose where alerts go and how you want to hear about each kind of update — in the app, by email, or by text.',
		why: 'This is how the app keeps you in the loop on rent, late fees, renewals, and your daily summary, without you having to go looking.',
		whereToFind:
			'Just your own email inbox — the one you check most. These alerts go to YOU, not to your tenants.',
		docsSlug: 'settings-and-notifications',
		walkThrough: 'notifications',
	},
	{
		key: 'automations',
		label: 'Automations',
		icon: 'Wand2',
		title: 'Let the app do the routine work',
		intro:
			'Turn on the chores you’d rather not do by hand: posting rent charges, adding late fees, and queuing renewal reminders.',
		why: 'With these on, the app quietly stays ahead of the calendar so nothing slips — you just review and approve. Leave them off to do each step yourself.',
		docsSlug: 'settings-and-notifications',
	},
	{
		key: 'messaging',
		label: 'Messaging',
		icon: 'MessageSquare',
		title: 'Talking to your tenants',
		intro:
			'Set how new messages reach tenants by default (email and text), and connect a text-messaging account if you want to send SMS.',
		why: 'The tenant portal inbox always gets the message; these settings just pre-pick email and text so you don’t have to choose every time.',
		whereToFind:
			'Texting needs an account with an SMS provider (like SignalWire or Twilio). Your IDs and keys are on that provider’s dashboard — copy each value over exactly.',
		docsSlug: 'settings-and-notifications',
		walkThrough: 'texting',
	},
	{
		key: 'team',
		label: 'Team',
		icon: 'Shield',
		title: 'People who help you',
		intro:
			'Invite a co-owner, partner, or employee and decide what each person is allowed to see and do.',
		why: 'Everyone gets their own login, so you never share a password — and you stay in control of who can touch money, leases, and tenant info.',
		docsSlug: 'settings-and-notifications',
		linkTo: '/admin/users',
		linkLabel: 'Manage team',
	},
	{
		key: 'owners',
		label: 'Owners',
		icon: 'BadgeDollarSign',
		title: 'Who owns the properties',
		intro:
			'The people or companies that legally own your properties. If you own them yourself, that’s just you.',
		why: 'Owners are used on owner statements and year-end tax forms (like 1099s), and let you split a property between more than one owner.',
		whereToFind:
			'Owner names and tax IDs are on the deed, your LLC paperwork, or a past tax return (Schedule E / 1099).',
		docsSlug: 'getting-started',
		walkThrough: 'owner',
		linkTo: '/owners',
		linkLabel: 'Manage owners',
	},
	{
		key: 'setup',
		label: 'Setup & import',
		icon: 'Upload',
		title: 'Bring in what you already have',
		intro:
			'New here? Walk through the guided setup, or bring your existing properties, tenants, and leases in from a spreadsheet.',
		why: 'Importing saves you from retyping — the app reads your spreadsheet and creates drafts for you to confirm, so you’re up and running fast.',
		docsSlug: 'scanning-documents',
		linkTo: '/import',
		linkLabel: 'Import from a spreadsheet',
	},
	{
		key: 'activity',
		label: 'Activity history',
		icon: 'History',
		title: 'A record of what happened',
		intro:
			'A searchable log of every change in your account — who did what, and when.',
		why: 'If you ever wonder “wait, when did that change?”, this is where you look. It’s also your paper trail if a tenant or co-owner ever disputes something.',
		docsSlug: 'getting-started',
		linkTo: '/audit',
		linkLabel: 'Open activity history',
	},
];

/** Look up a settings section's metadata by key. */
export function settingsSection(key: SettingsSectionKey): SettingsSectionMeta | undefined {
	return SETTINGS_SECTIONS.find((s) => s.key === key);
}

/** Look up a step's metadata by key. */
export function wizardStep(key: WizardStepKey): WizardStepMeta | undefined {
	return WIZARD_STEPS.find((s) => s.key === key);
}

/** `/docs/<slug>` link for a step's "Learn more". */
export function wizardDocsHref(step: WizardStepMeta): string {
	return `/docs/${step.docsSlug}`;
}

/**
 * Deep-link into the wizard at a given step. `from` lets the wizard send the user back where they
 * came from when the step is done (e.g. `from=settings`).
 */
export function wizardStepHref(key: WizardStepKey, from?: string): string {
	const params = new URLSearchParams({ step: key });
	if (from) params.set('from', from);
	return `/onboarding?${params.toString()}`;
}
