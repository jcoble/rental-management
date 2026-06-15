/**
 * The "Getting started" checklist — the hand-holding to-do list a brand-new (non-technical)
 * landlord works through to go from an empty account to up-and-running. It powers two surfaces:
 *
 *  - the persistent progress card on the dashboard (shows until everything is done), and
 *  - the full checklist on `/get-started`.
 *
 * Each task carries:
 *  - plain-English copy (`label` + a one-line `eli5`) aimed at someone who has never used software
 *    like this — no jargon;
 *  - a **deep-link target** (`route` + optional `coach` key + optional `hash`) that drops the user on
 *    the exact page and spotlights the exact control to use (see {@link taskHref} + the coach store);
 *  - a **completion predicate** computed purely from data the app already fetches for other reasons
 *    (the list endpoints), so a task auto-checks the moment the underlying record exists — there are
 *    NO per-task / per-row API calls. {@link GettingStartedSignals} is the small bag of those facts.
 *
 * Where a task maps to an onboarding-wizard step, its explanation copy is pulled from the shared wizard
 * registry (`wizard-steps.ts`, treated read-only here) via {@link wizardExplanation} so the checklist and
 * the wizard speak with one voice. Two tasks have NO matching wizard step — `unit` (units are a sub-step
 * of the wizard's Property panel) and `automations` (configured in Settings, not the wizard) — so they
 * carry their own copy here. The `fallback` passed to {@link wizardExplanation} is what shows if a step
 * key is ever renamed, so the row is never blank.
 *
 * Keep `key` values stable: they are persisted in `localStorage` (manual "mark done"/"skip" overrides)
 * and used in deep-link URLs.
 */
import {
	Building,
	UserCircle2,
	Home,
	DoorOpen,
	Users,
	FileText,
	Bell,
	MessageSquare,
	SlidersHorizontal,
} from '@lucide/svelte';
import type { Component } from 'svelte';
import { wizardStep } from './wizard-steps';

export type GettingStartedTaskKey =
	| 'portfolio'
	| 'owner'
	| 'property'
	| 'unit'
	| 'tenant'
	| 'lease'
	| 'notifications'
	| 'texting'
	| 'automations';

/**
 * The minimal set of facts the checklist needs to auto-check tasks. Every field is derived from a
 * list/settings query the surfaces already run — the checklist adds no new endpoints. Counts are used
 * (rather than booleans) so the surfaces can also show "3 properties" style context if they want.
 */
export interface GettingStartedSignals {
	/** The portfolio has been given a real name (not the default placeholder). */
	portfolioNamed: boolean;
	ownerCount: number;
	propertyCount: number;
	/** Total units across all properties — `sum(property.unitCount)`, no per-property call. */
	unitCount: number;
	tenantCount: number;
	leaseCount: number;
	/** A dedicated alert email is set (separate from the login email). */
	hasNotificationEmail: boolean;
	/** SignalWire / SMS provider credentials are configured. */
	hasTexting: boolean;
	/**
	 * The user deliberately switched on an automation that defaults OFF (rent charges or late fees).
	 * Lease-expiry reminders are intentionally excluded — they default ON, so counting them would
	 * auto-check this task for a brand-new account that never touched Settings.
	 */
	hasAutomations: boolean;
}

export interface GettingStartedTask {
	key: GettingStartedTaskKey;
	/** Short imperative label, e.g. "Add your first property". */
	label: string;
	/** One sentence, plain-English: what this is + why it matters. Explain-Like-I'm-5. */
	eli5: string;
	/** Lucide icon component for the row. */
	icon: Component;
	/** Route to land on when the user taps the task. */
	route: string;
	/**
	 * `data-coach` anchor to spotlight once the route loads (passed as `?coach=<key>`). Omit for tasks
	 * whose target is a whole settings section reached by `hash` instead (the settings page is owned by
	 * a sibling lane and is not field-coached here).
	 */
	coach?: string;
	/** `#fragment` to scroll to (used for the Settings sections). */
	hash?: string;
	/**
	 * Core tasks are the must-do spine (portfolio → lease) and gate "you're all set". The rest are
	 * recommended add-ons that make the system send reminders for you, shown but not required.
	 */
	core: boolean;
	/** Whether reaching this task's target needs a Live account (the in-app create flows do). */
	requiresLive?: boolean;
	/** Computes whether the underlying data already exists → task auto-checks. */
	isComplete: (s: GettingStartedSignals) => boolean;
}

/** Pull the shared wizard explanation for a step, falling back to a local default. */
function wizardExplanation(key: Parameters<typeof wizardStep>[0], fallback: string): string {
	return wizardStep(key)?.explanation ?? fallback;
}

export const GETTING_STARTED_TASKS: GettingStartedTask[] = [
	{
		key: 'portfolio',
		label: 'Name your rental business',
		eli5: wizardExplanation(
			'portfolio',
			'Give your whole operation a name — it shows on every report and reminder. Just use what you call your rentals.'
		),
		icon: Building,
		route: '/settings',
		// Settings is tabbed: the hash must be a TAB KEY so the right tab opens (settings/+page.svelte's
		// $effect honors tab-key hashes), and the coach key spotlights the section once that tab renders.
		hash: 'portfolio',
		coach: 'settings-portfolio',
		core: true,
		isComplete: (s) => s.portfolioNamed,
	},
	{
		// Auto-completes: onboarding creates a primary "self-owner" from the landlord's own account, so
		// this is checked off without a manual "add an owner" step. The /owners deep-link is for ADDING
		// more owners (co-owners, an LLC) once the first one already exists.
		key: 'owner',
		label: 'Confirm who owns the properties',
		eli5: wizardExplanation(
			'owner',
			'The owner is the person or company that legally holds the property — used later on owner reports and tax forms. We start this off as you; add co-owners or an LLC anytime.'
		),
		icon: UserCircle2,
		route: '/owners',
		coach: 'add-owner',
		core: true,
		requiresLive: true,
		isComplete: (s) => s.ownerCount > 0,
	},
	{
		key: 'property',
		label: 'Add your first property',
		eli5: wizardExplanation(
			'property',
			'A property is one building or address. Add it once, then put the rentable units inside it.'
		),
		icon: Home,
		route: '/properties',
		coach: 'add-property',
		core: true,
		requiresLive: true,
		isComplete: (s) => s.propertyCount > 0,
	},
	{
		// No matching wizard step: units are a sub-step of the wizard's Property panel, so this carries
		// its own copy rather than pulling from the registry.
		key: 'unit',
		label: 'Add a unit to that property',
		eli5: 'A unit is a single rentable space. A house is one unit; a duplex is two. Open a property to add its units.',
		icon: DoorOpen,
		route: '/properties',
		coach: 'open-property-for-units',
		core: true,
		requiresLive: true,
		isComplete: (s) => s.unitCount > 0,
	},
	{
		key: 'tenant',
		label: 'Add your tenants',
		eli5: wizardExplanation(
			'tenants',
			'Tenants are the people who rent from you. Adding their email or phone lets the app send them reminders.'
		),
		icon: Users,
		route: '/tenants',
		coach: 'add-tenant',
		core: true,
		requiresLive: true,
		isComplete: (s) => s.tenantCount > 0,
	},
	{
		key: 'lease',
		label: 'Create the first lease',
		eli5: wizardExplanation(
			'lease',
			'A lease ties a tenant to a unit and sets the rent, dates, and deposit. This is what drives rent charges and reminders.'
		),
		icon: FileText,
		route: '/leases',
		coach: 'add-lease',
		core: true,
		requiresLive: true,
		isComplete: (s) => s.leaseCount > 0,
	},
	{
		key: 'notifications',
		label: 'Set where alerts go',
		eli5: wizardExplanation(
			'notifications',
			'Pick the email where rent reminders and your daily briefing land. Leave it blank to use your login email.'
		),
		icon: Bell,
		route: '/settings',
		hash: 'notifications',
		coach: 'settings-notifications',
		core: false,
		isComplete: (s) => s.hasNotificationEmail,
	},
	{
		// No matching wizard step: automations are configured in Settings, not the wizard, so this
		// carries its own copy rather than pulling from the registry.
		key: 'automations',
		label: 'Turn on automatic reminders',
		eli5: 'Let the app charge rent, add late fees, and warn about expiring leases on its own — so you do not have to remember.',
		icon: SlidersHorizontal,
		route: '/settings',
		hash: 'automations',
		coach: 'settings-automations',
		core: false,
		isComplete: (s) => s.hasAutomations,
	},
	{
		key: 'texting',
		label: 'Connect texting (optional)',
		eli5: wizardExplanation(
			'texting',
			'Texting tenants needs a SignalWire account. Totally optional — everything works on email and in-app alerts without it.'
		),
		icon: MessageSquare,
		route: '/settings',
		// SMS provider config lives in the Messaging tab (NOT Automations) — point there and spotlight it.
		hash: 'messaging',
		coach: 'settings-messaging',
		core: false,
		isComplete: (s) => s.hasTexting,
	},
];

/** The must-do spine (portfolio → lease). */
export const CORE_GETTING_STARTED_TASKS = GETTING_STARTED_TASKS.filter((t) => t.core);

/** Look up a task by key. */
export function gettingStartedTask(key: GettingStartedTaskKey): GettingStartedTask | undefined {
	return GETTING_STARTED_TASKS.find((t) => t.key === key);
}

/**
 * Build the deep-link href for a task: `route` plus a `?coach=<key>` query (so the destination knows
 * which element to spotlight) and/or a `#hash` (so the browser scrolls the right section into view).
 */
export function taskHref(task: Pick<GettingStartedTask, 'route' | 'coach' | 'hash'>): string {
	const query = task.coach ? `?coach=${encodeURIComponent(task.coach)}` : '';
	const hash = task.hash ? `#${task.hash}` : '';
	return `${task.route}${query}${hash}`;
}

export type GettingStartedStatus = 'done' | 'todo';

export interface GettingStartedProgress {
	/** Auto-completed (data exists) OR manually marked done/skipped by the user. */
	doneCount: number;
	totalCount: number;
	coreDoneCount: number;
	coreTotalCount: number;
	/** Every core task is satisfied → the spine is complete. */
	allCoreDone: boolean;
	/** Every task (core + optional) is satisfied → hide the card entirely. */
	allDone: boolean;
}

/**
 * A task counts as done when its data exists OR the user explicitly marked it done/skipped (the
 * `manualDone` set, persisted per-portfolio). Auto-completion always wins — once the record exists the
 * box is checked regardless of any manual override.
 */
export function isTaskDone(
	task: GettingStartedTask,
	signals: GettingStartedSignals,
	manualDone: ReadonlySet<string>
): boolean {
	return task.isComplete(signals) || manualDone.has(task.key);
}

export function computeProgress(
	signals: GettingStartedSignals,
	manualDone: ReadonlySet<string>
): GettingStartedProgress {
	const done = (t: GettingStartedTask) => isTaskDone(t, signals, manualDone);
	const core = CORE_GETTING_STARTED_TASKS;
	const coreDone = core.filter(done).length;
	const allDone = GETTING_STARTED_TASKS.filter(done).length;
	return {
		doneCount: allDone,
		totalCount: GETTING_STARTED_TASKS.length,
		coreDoneCount: coreDone,
		coreTotalCount: core.length,
		allCoreDone: coreDone === core.length,
		allDone: allDone === GETTING_STARTED_TASKS.length,
	};
}
