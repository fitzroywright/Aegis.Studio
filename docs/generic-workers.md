# Generic workers

Studio uses a role-aware worker model. A worker may be a photographer, editor, retoucher, mechanic, technician, driver, reviewer, or another configured type.

A job may have multiple worker assignments, with at most one assignment marked primary. The primary worker is accountable for the job; supporting assignments carry their own worker type, responsibility, schedule, status, and completion history.

Existing photographer records remain supported as the PHOTOGRAPHER worker type during migration. Scheduling, permissions, notifications, and roster matching should use the assignment worker type rather than hard-coded photographer labels.

The persistence migration is in WorkerSchema.sql. It seeds the existing photography roles and copies current photographer profiles into the generic worker directory. New worker types can be added without changing the job model.
