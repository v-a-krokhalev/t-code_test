# TODO

- [ ] Move the JWT signing key and production connection strings to a secret provider; rotate the checked-in sample signing key before any non-local deployment.
- [ ] Move database migrations and seeding out of every API replica's startup, or coordinate them with a deployment job/distributed lock. Make seeding safe under concurrent starts.
- [ ] Prevent test cleanup from flushing a shared Redis database. Require a dedicated test database/instance or clean only test-prefixed keys.
- [ ] Decide whether the fixed-window limiter's boundary burst is acceptable. At a minute boundary, one user can make up to 60 requests just before and 60 just after the boundary. If the requirement means any rolling 60-second interval, replace it with a Redis sliding-window algorithm and add boundary tests.
- [ ] Load-test schedule, analytics, enrollment creation, and waitlist promotion against representative production-sized data. Capture PostgreSQL `EXPLAIN (ANALYZE, BUFFERS)` plans before adding indexes; evaluate composite indexes for class/status, user/class/status, and FIFO queue ordering based on observed plans.
- [ ] Add readiness checks for PostgreSQL and Redis so orchestration can distinguish a running process from an instance ready to serve requests.
