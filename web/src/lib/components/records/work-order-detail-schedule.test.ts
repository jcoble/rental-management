import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = readFileSync(new URL('./WorkOrderDetail.svelte', import.meta.url), 'utf8');

describe('work order detail schedule edit flow', () => {
	it('preserves scheduled start and service-window end as editable datetimes', () => {
		assert.match(source, /scheduledWindowEnd:\s*''/);
		assert.match(source, /scheduledFor:\s*utcIsoToLocalInput\(wo\.scheduledFor\)/);
		assert.match(source, /scheduledWindowEnd:\s*utcIsoToLocalInput\(wo\.scheduledWindowEnd\)/);
		assert.doesNotMatch(source, /scheduledFor:\s*wo\.scheduledFor\?\.slice\(0,\s*10\)/);
		assert.doesNotMatch(source, /formatDateOnly\(wo\.scheduledFor\)/);
	});

	it('renders schedule fields through datetime-local inputs in the supported detail edit form', () => {
		assert.match(
			source,
			/<InlineField label="Scheduled start" type="datetime-local"[^>]+testid="work-order-detail-scheduled"/
		);
		assert.match(
			source,
			/<InlineField label="Service window end" type="datetime-local"[^>]+testid="work-order-detail-scheduled-window-end"/
		);
	});

	it('sends changed schedule fields with the local offset so linked appointments keep the window', () => {
		assert.match(source, /import \{ formatDateOnly, localInputToOffsetIso \} from '\$lib\/utils\/date'/);
		assert.match(source, /for \(const field of \['scheduledFor', 'scheduledWindowEnd'\] as const\)/);
		assert.match(source, /data\[field\] = localInputToOffsetIso\(form\[field\]\)/);
		assert.match(source, /workOrderWindowErrors\(\)/);
		assert.match(source, /Window end must be after the start time\./);
	});
});
