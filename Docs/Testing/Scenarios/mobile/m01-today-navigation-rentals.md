# Mobile scenario M01 — Today screen, navigation, Rentals

## Purpose
The landlord opens the app on the phone, reads today's summary, and gets to any rental in two taps. Exercise the Today screen, the bottom tabs, the sample-data banner, and the Rentals tab through to a unit's detail.

## Read the implementation first
- mobile/lib/features/home/ (Today screen, "Today" list items, greeting, quick actions)
- mobile/lib/core/router/ (routes and tab shell)
- mobile/lib/features/properties/, mobile/lib/features/units/ (Rentals list, property/unit detail)
- The API the screens call: RentalCommand.Api/Controllers/PortfolioController.cs (`/dashboard`), PropertyController.cs, UnitController.cs

## Rough exploration areas
- Today: greeting/date, "Here's today's summary", the Today list (overdue rent, repairs, appointments); tap items — do they land on the right record? Back returns to Today?
- The "Example data — tap to start fresh" banner: what it does, whether it explains itself. Do NOT confirm a reset.
- Bottom tabs: Today, Rentals, Money, Work, Inbox — each loads, state survives tab switching, badge counts match content.
- Rentals: list, search, property → units → unit detail; occupancy/rent shown matches the web values (compare with the API or web for 2–3 units).
- Notifications bell and profile icon in the app bar.
- Pull-to-refresh, empty states, loading states, rotation not required.

## Edge cases worth trying
- Tap fast twice on a Today item (double navigation?), press Android back from deep screens all the way out, background the app and return (session still valid), airplane-mode-like failure is NOT required.

## What to verify visually
- Nothing clipped or overlapping at the phone's size; text readable; touch targets not tiny; plain landlord words (no jargon, no raw enum names, no "null"/"NaN"/"undefined").
