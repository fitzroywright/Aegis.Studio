# Studio Photographer

Studio Photographer is the native Android/iOS field companion for Aegis Studio. Its purpose is operational scheduling and booking management, not photo editing or gallery administration.

## First slice

- Sign in against the existing Studio authentication endpoint.
- Resolve the signed-in account to its Studio photographer profile.
- Show only schedule entries assigned to that photographer's schedule resource.
- Show booking/job time, location, status and notes.
- Let the photographer record availability/unavailability from the phone.
- Keep the API under `/api/mobile/photographer` so the native client remains a thin client over Studio rather than becoming a second system of record.

## Next slices

1. Booking/job detail optimized for field use, including client contact and job-order information.
2. Accept/confirm assignment and controlled field status changes (travel, arrived, started, completed).
3. Calendar view, conflict warnings and richer availability/leave entry.
4. Push notifications for assignments, cancellations and schedule changes.
5. Offline cache/outbox for schedule and status changes when connectivity is poor.
6. Calendar/map integration and quick contact actions.

The web application remains the system of record. Mobile functionality should reuse Studio authorization, scheduling, notifications, configuration and diagnostics rather than duplicating those concerns.
