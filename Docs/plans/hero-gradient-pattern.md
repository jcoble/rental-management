# Hero gradient card: soften the blend + reuse as a color-keyed pattern

**Task:** Notion TSK-35. The user likes the green gradient on the Monthly Rent hero of the
lease detail page. Two asks: (1) make the fade **more seamless** (it reads a touch abrupt),
(2) **reuse it** as a pattern with **context-keyed colors** across detail pages.

## Current state

`web/src/routes/(protected)/leases/[id]/+page.svelte`:

```svelte
<Card.Root class="overflow-hidden" data-testid="lease-hero">
  <div class="bg-gradient-to-br {heroAccent} to-transparent">
    <Card.Content ...>...</Card.Content>
  </div>
</Card.Root>
```

`heroAccent` is status-keyed: `from-success/10` (Active), `from-destructive/10`
(Expired/Terminated), `from-warning/10` (NoticeGiven), else `from-primary/10`.

Why it bands: a two-stop `from-{color}/10 to-transparent` diagonal goes from a tinted corner
to *fully transparent* (the page background, which differs from the card surface). The midpoint
is a single linear ramp, so the eye catches the transition edge — and fading to `transparent`
(not the card token) lets the page bg bleed through near the far corner.

## The softened recipe

Fade into the **card token**, not full transparent, and add a **mid-stop** so the ramp is a
gentle three-stop wash instead of one hard diagonal:

- Start a touch stronger: `from-{color}/15` (was `/10`).
- Add `via-{color}/5` mid-stop to ease the falloff.
- End at `to-transparent` (the card surface already shows through the tint; the wrapper sits
  *inside* `Card.Root`, so "transparent" reveals the card bg, not the page bg — keeps it
  seamless and theme-correct in both light/dark).
- Keep `bg-gradient-to-br` direction; lengthening via a 3-stop ramp already softens the band.

Net: `bg-gradient-to-br from-{color}/15 via-{color}/5 to-transparent`. Calm, no hard edge.

## Shared, color-keyed wrapper: `HeroCard`

New `web/src/lib/components/shared/HeroCard.svelte` — a thin wrapper around `Card.Root` +
the softened gradient, keyed by a small **tone** vocabulary that maps onto existing semantic
tokens (so it works in both themes):

| tone        | token        | use                                       |
|-------------|--------------|-------------------------------------------|
| `success`   | `--success`  | active lease, paid payment, completed WO  |
| `warning`   | `--warning`  | notice given, overdue/partial payment     |
| `destructive` | `--destructive` | terminated/expired, failed pay, emergency WO |
| `primary`   | `--primary`  | default / neutral-positive                |
| `muted`     | muted fg     | inert / cancelled                         |

Tailwind can't interpolate `from-{tone}/15` from a runtime var with full JIT safety, so the
component uses a **static lookup** (`Record<Tone, string>` of complete class strings) — every
class string is literal, so JIT sees them. Props:

- `tone: Tone` (default `primary`)
- `class`, `contentClass`, `testid`
- `children` snippet (hero body — pages keep their own layout/markup inside)

Markup:

```svelte
<Card.Root class={cn('overflow-hidden', className)} data-testid={testid}>
  <div class={gradientByTone[tone]}>
    <Card.Content class={cn('...', contentClass)}>{@render children()}</Card.Content>
  </div>
</Card.Root>
```

Expose a tiny `heroToneForStatus(status)` helper too? No — keep tone selection at the call
site (each page already derives status/severity). The wrapper owns *only* the recipe + token
mapping. That's the reuse unit.

## Adoption (lease + 3 others)

1. **Lease** (existing) — refactor to `HeroCard tone={...}` from status. Same look, softened.
2. **Payment** (`accounting/payments/[id]`) — currently no hero. Add a Monthly-Rent-style hero
   showing **Amount** + type/status/lease, tone green when Paid, warning when Late/Partial,
   destructive when Failed, else primary.
3. **Work order** (`maintenance/work-orders/[id]`) — add a hero showing **title** + property +
   status, tone keyed by **priority/severity**: Emergency=destructive, High=warning, else
   primary.
4. **Property** (`properties/[id]`) — add a hero showing **name** + occupancy
   (`occupiedUnits / unitCount`), tone primary (neutral), success when fully occupied.

`data-testid` on each hero (`*-hero`) and the headline figure where sensible.

## Out of scope

No backend, no migration, no new tokens, no theme changes. Pure web/ presentational refactor.

## Verify

- `cd web && npx svelte-check --threshold error && pnpm build`
- `dotnet build` sanity (no .NET change, just confirm green).
- Visually confirm lease hero softened + at least one adopting page.
