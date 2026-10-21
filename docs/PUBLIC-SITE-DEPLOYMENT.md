# Aegis Studio Public Site deployment

The reference website now saves intake and serves gallery copies from its own persistent storage. It does not call internal Studio. Studio polls the website API and pushes approved galleries outbound.

Use [Client intake deployment and validation](CLIENT-INTAKE-HANDOFF.md) for the current MacBook build, configuration, migration, storage and Kratos procedure. Use [Public website V1 integration](public-site-v1/INTEGRATION.md) for the external developer contract and credentials. The older Studio Public V1 interface remains a compatibility interface and is not the polling contract.

Production client sign-in still requires its existing identity integration; mock Google/Facebook identities are for local acceptance only. Disable PUBLIC_DEMO_MODE on any public production deployment. The new intake and gallery workers remain disabled until explicitly configured and validated.
