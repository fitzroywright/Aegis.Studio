# Registration truth audit

Scope: `common-registration-migration`.

- Aegis.Studio registers only with Aegis.Configuration.
- Studio does not register with Aegis.Diagnostics.
- Runtime discovery reports requirement/configured-state evidence only; configuration and secret values are not published.
- Registration success is accepted only after Common.Registration reports an actual successful Configuration HTTP response.
- Missing credential, missing or malformed contract, HTTP rejection, timeout, network failure, and unexpected exceptions remain explicit failures.
- Configuration unavailability is tolerated without fabricating a registered state; the publisher retries until an actual registration succeeds.
- Common.Registration applies the metadata-only policy again at the transport boundary as defense in depth.
- No Operations, Configuration, or Diagnostics product code is changed by this migration.

This audit is committed on the migration branch to force CI against the current Common.Registration staging branch before this migration is considered proven.
