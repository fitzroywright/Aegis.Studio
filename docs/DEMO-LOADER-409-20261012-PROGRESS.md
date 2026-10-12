# Demo loader HTTP 409 diagnostic fix

Reported: Demo generation failed: Demo loader returned HTTP 409.
The server already returns the rejection reason in JSON, but demo-loader.js discarded it.
The loader now displays the server error, message or Problem Details detail for start and
status failures. Non-JSON/empty responses retain the HTTP fallback. Rejected starts do not
poll or trigger a second operation.

The exact deployed rejection remains unknown until its response is read. Start can reject
reset when a load is active, when Storage:FactoryResetExclusiveRoot is not true, when the
storage provider lacks enumeration/lifecycle purge, or on a database/startup error.
No reset gate was removed and no external reset was performed.

Validation: all eight demo-loader UI tests passed, including configuration and active-run
409 reasons, status errors, the HTTP fallback, and existing successful reset/poll behavior.
