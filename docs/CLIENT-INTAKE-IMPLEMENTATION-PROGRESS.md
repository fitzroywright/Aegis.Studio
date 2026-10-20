# Client intake implementation checkpoint

Base: 64f1e2fc6794ffe69f1aa8d7bd4da4c2ca562282.
Feature branch: feature/client-intake-outbound-completion.

## Written so far
- Durable public-site SQLite intake, annual W reference allocation, submission replay protection, change feed and exact-revision acknowledgments.
- Studio outbound poller, original references, inquiry/booking import, roster staging and attachment status.
- Shared roster editor and Front Desk/Job Details entry points; CSV/XLSX preview and mapping.
- Subject messages and pickup preferences using the existing outbox and reminder scheduler.
- Provider acceptance persisted before Sent Items verification; verification status kept separate.
- Background public gallery export and local public-site storage.

## Still in progress
Source review and fixes; production identity integration compatibility; API contract and developer examples; regression tests; complete validation/deployment instructions. Check actual branch code rather than treating this list as acceptance evidence.

## Execution boundary
No restore, build, tests, deployment or live send has run here. All restore/build/test execution is reserved for the user's MacBook. Live mail requires a designated test recipient and runtime evidence. No merge or deployment has been performed.

