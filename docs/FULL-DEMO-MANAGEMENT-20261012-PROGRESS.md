# Full demo regeneration and consolidated Studio Management

Requested October 12, 2026. Base main a922ef4b64a4f22849e6f680c5ee2c6e82561bc7.
Working branch feature/full-demo-studio-management-20261012.

Implemented: asynchronous full-generator reset and generation, retained run status through reset,
cleared graph seed markers, scoped reset to the configured database schema, transaction-local
existing demo seed mode for historical/closed scenarios, and consolidated Studio Management
catalog/template/admin controls. Avatar menu now has Preferences, Studio Management and Sign Out.
The separate Administration entry and redundant Profile entry are removed. Existing administrator
checks and reset/storage ownership checks remain in effect.

Validation so far: build clean; seven focused loader/management/menu UI tests passed;
full UI suite 116 passed and 58 existing failures. Reset persistence check passed.
Full media/template dataset rebuild check is being executed against embedded PostgreSQL.
It uses a genuine repository photo fixture in the source cache, not network downloads.
The asynchronous coordinator test is marked Database=Native because its progress callbacks
use separate concurrent database connections and cannot run on a single-session multiplexer.
No external reset or deployment performed. Update this checkpoint after final verification.
