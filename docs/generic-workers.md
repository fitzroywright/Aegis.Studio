# Generic workers

Studio uses a role-aware worker model. A worker may be a photographer, editor, retoucher, mechanic, technician, driver, reviewer, or another configured type.

A job may have multiple worker assignments, with at most one assignment marked primary. The primary worker is accountable for the job; supporting assignments carry their own job role, responsibility, schedule, status, and completion history.

Existing photographer records remain supported as the PHOTOGRAPHER job role during migration. Scheduling, permissions, notifications, and roster matching should use the assignment job role rather than hard-coded photographer labels.

The persistence migration is in WorkerSchema.sql. It seeds the existing photography roles and copies current photographer profiles into the generic worker directory. New job roles can be added without changing the job model.
