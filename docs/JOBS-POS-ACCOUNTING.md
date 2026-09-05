# Aegis Studio - Jobs, POS and Accounting

## Core model

A Workspace represents a team or business area such as Weddings, Graduations, ID Photos or Production. A Job is the unit of customer work inside a Workspace.

A Job does not require an email. Staff can create a Job from a walk-in, telephone call, booking, internal request or other intake. Email is one channel that can be linked to an existing Job or used as the seed for a new Job.

A Job can be assigned to a Customer. Communications, calls, notes, tasks, bookings, media activity, payments, refunds, production and delivery events should appear on the Job timeline.

## Communications

Inbound email routing should first resolve the Workspace from the receiving address, then attempt to resolve the Customer and Job. Strong identifiers such as a job/booking reference take precedence over email-address matching. Ambiguous messages remain in the Workspace inbox for staff assignment rather than silently attaching to the wrong Job.

The same model is intended for future PBX integration. Caller ID can identify candidate Customers and active Jobs. A call event, notes and (where permitted) recording metadata can then be attached to the Job.

## Front desk and POS

Studio is intended to replace the operational CounterPoint workflow at Bryan's. POS is a capability within Studio rather than a separate silo.

The front desk can search for a Customer or Job, create a Job without an email, add products/services, take deposits and final payments, issue refunds and print receipts. Payments are linked to both the Sale and Job.

Supported tender concepts include cash, card, bank transfer, cheque, online payment and other/manual methods. A sale can accept multiple payments so split tender is possible.

Cash-register support includes register sessions, opening float, cashier identity, cash counting and close. Hardware integration (cash drawer and receipt printer) is intentionally kept behind infrastructure adapters.

## Accounting

Peachtree remains the accounting system of record. Studio remains the operational system of record for Customers, Jobs, Sales, Payments and production workflow.

Studio creates controlled accounting batches from closed business activity. Each batch has a unique Studio batch number, source references and posting state so the same batch cannot be treated as a new batch accidentally.

The accounting boundary is `IAccountingGateway`. A Peachtree/Sage adapter can use the best interface supported by Bryan's installed Peachtree version. If direct SDK/API integration is unavailable, the adapter can generate a validated import file without changing the Studio domain.

Account mappings are configuration, not hard-coded rules. Examples include cash, card clearing, bank, sales revenue, customer deposits, tax, refunds and accounts receivable.

Operational meaning and accounting treatment remain separate. For example, Studio records that a customer paid a deposit against a Job; accounting configuration determines whether that amount is posted to a customer-deposit liability, accounts receivable or another account.

## Target chain

Customer -> Workspace -> Job -> Booking/Order -> Deposit -> Production -> Final Payment -> Register Close -> Accounting Batch -> Peachtree -> Reconciliation

## Next implementation slices

1. Persist Jobs and JobActivity in `StudioDbContext` and add repositories/services.
2. Add explicit Job links to conversations and bookings, with safe inbound-email matching.
3. Persist Sales, SaleLines, Payments and RegisterSessions.
4. Add front-desk Job/POS screens and receipt output.
5. Add register close and reconciliation reports.
6. Add accounting mapping management and batch generation.
7. Implement the Peachtree adapter after confirming the exact Peachtree version and supported import/API mechanism.
8. Add unit/integration tests for job creation, message assignment, split tender, refunds, register close, balanced accounting batches and duplicate-post protection.
9. Add a future PBX adapter contract for incoming-call screen pop and call timeline events.
