/**
 * Plain-English captions for each `data-coach` anchor, shown in the spotlight callout. Keyed by the
 * coach key carried in `?coach=<key>` deep-links.
 *
 * Most keys map 1:1 to a getting-started task and reuse that task's label/eli5; a couple of anchors
 * (e.g. the property→units two-hop) get a bespoke "do this next" caption here. Anything not listed
 * falls back to a generic prompt, so a new `data-coach` attribute never breaks — it just gets a plain
 * highlight.
 */
import { GETTING_STARTED_TASKS } from './getting-started-tasks';

const EXTRA_CAPTIONS: Record<string, string> = {
	// The "add a unit" task lands on /properties first; the user opens a property, THEN adds units on
	// the detail page. Two anchors cover the two hops.
	'open-property-for-units':
		'Open a property (click its row), then use “Add Unit” on its page to add a rentable unit.',
	'add-unit': 'Click here to add a unit — a single rentable space inside this property.',

	// Guided Setup (TSK-602): one "Show me on the form" anchor per wizard step, spotlighting that
	// step's primary field. On-demand only — the user has to tap "Show me", it never auto-fires.
	'onboarding-portfolio': 'Type your rental business name here — your own name, a family name, or your LLC.',
	'onboarding-owner': 'Type the legal owner’s name here — yourself, your LLC, or your trust.',
	'onboarding-import': 'Drop a photo or PDF of a signed lease here — we’ll read it and build the rest.',
	'onboarding-property': 'Type the property name here, then its address below.',
	'onboarding-tenants': 'Type your tenant’s name here. Use “Add another tenant” for each additional one.',
	'onboarding-lease': 'Start by picking which tenant this lease is for.',
	'onboarding-notifications': 'Type the email where YOU want alerts sent — or leave it blank to use your login email.',
	'onboarding-texting': 'Paste your SignalWire Project ID here to turn on text messages.',
};

/** Resolve the callout caption for a coach key. */
export function coachCaption(key: string): string {
	const task = GETTING_STARTED_TASKS.find((t) => t.coach === key);
	if (task) return task.eli5;
	return EXTRA_CAPTIONS[key] ?? "Here's the spot — go ahead.";
}
