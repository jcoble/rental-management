# Step 5 isolated deposits request-path capture

- Capture time UTC: `2026-07-25T14:32:40.9798290+00:00`
- Authenticated action: `GET /api/v1/tenant-accounts/deposits/page?skip=0&take=20`
- Result: `200`
- Request/action duration: `178.930 ms`
- 401/token-refresh replay: `none observed`; no replay duration is combined with the authenticated 200 request.
- PostgreSQL statements: `2`, executed sequentially as DB-side count then deterministic DB-side page.

| Sequence | Shape | Started UTC | Completed UTC | Duration ms |
|---:|---|---|---|---:|
| 1 | count | 2026-07-25T14:32:41.0583580+00:00 | 2026-07-25T14:32:41.1077200+00:00 | 49.359 |
| 2 | page | 2026-07-25T14:32:41.1355520+00:00 | 2026-07-25T14:32:41.1576790+00:00 | 22.126 |

This isolated PostgreSQL capture supplies correlated SQL timing only. The deployed
API/APK identity and ten UI-driven emulator samples are intentionally pending the
contract's separate real-emulator verifier.