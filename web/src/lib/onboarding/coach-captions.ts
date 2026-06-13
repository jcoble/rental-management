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
};

/** Resolve the callout caption for a coach key. */
export function coachCaption(key: string): string {
	const task = GETTING_STARTED_TASKS.find((t) => t.coach === key);
	if (task) return task.eli5;
	return EXTRA_CAPTIONS[key] ?? "Here's the spot — go ahead.";
}
