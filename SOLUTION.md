# Implementation Decisions

- The task calls the endpoints other than admin-only analytics "the other five"; this is a wording error. There are eight endpoints total, so the seven non-analytics endpoints are available to any authenticated user.
- Idempotency records are stored on the enrollment row in Postgres. The response and business mutation commit in one transaction, so retries remain stable across API instances and process restarts. Keys are hashed and scoped by user and operation. Reusing a key with a different body returns `409` with reason `idempotency-key-reused`.
- Enrollment creation, seat changes, cancellation, waitlist joins, and waitlist departures use transaction-scoped PostgreSQL advisory locks. Class locks serialize capacity changes; pass locks serialize visit deductions and refunds. Cancellation locks the class before the affected passes, and pass locks are acquired in ascending user ID order.
- Waitlist join, leave, and booking cancellation acquire the same class lock inside their database transaction. Concurrent operations therefore run in one order: if leave commits first, the row is no longer eligible for promotion; if cancellation commits first and promotes the row, a later leave sees an active enrollment and returns `404`. The cancellation/refund/promotion changes commit together.
- Waitlist promotion is strict FIFO, ordered by creation time and enrollment ID. If the first waiting request needs more seats than are currently free, later requests wait behind it. A member whose pass is expired or has too few visits is skipped for this promotion attempt; the entry remains waiting and can be reconsidered when a later cancellation frees seats.
- Promotion changes the waitlist enrollment to active and charges its pass in the same transaction that handles cancellation. A club-cancelled class is never promoted; existing waiting rows remain waiting and are not charged.
- The first cancellation response's refunded visit count is stored with the enrollment so repeated cancellation requests return the same result without refunding twice.
- ID values must be positive: nonnumeric, zero, or negative IDs return `400`; positive IDs absent from the database return `404`. This applies to enrollment/waitlist path IDs, class IDs in request bodies, and room ID filters.
- An explicitly empty `continuationToken` is treated like an omitted token and starts from the first schedule page. Whitespace-only, malformed, or negative-offset tokens return `400`.
- Enrollment-card class and room metadata is cached in Redis for ten minutes; the mutable enrollment and pass data is queried on every request. Analytics summaries are cached by date range and optional room for 30 seconds to reduce repeated aggregation while bounding staleness.
- Redis cache failures are fail-open: reads become cache misses and writes/removals are logged and ignored. Rate limiting is fail-closed: if Redis cannot atomically count a request, the API returns `429`. The Redis multiplexer is configured to let the host start while reconnecting.

## Remaining Scope

- The 60-per-minute fixed-window rate limiter uses atomic Redis increments and is shared across API instances. Boundary-burst semantics and database index/load analysis remain tracked in `TODO.md`.
- This project does not add background jobs, class-management endpoints, or automatic refunds when the club cancels a class; those are outside the requested API contract.
