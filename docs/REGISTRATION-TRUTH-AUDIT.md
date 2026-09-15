# Registration truth audit

Scope: `common-registration-migration`.

- Aegis.Studio registers only with Aegis.Configuration.
- Studio does not register with Aegis.Diagnostics.
- Runtime discovery reports requirement/configured-state evidence only; configuration and secret values are not published.
- Registration success is accepted only after the standalone Common.Registration library reports an actual successful Configuration HTTP response.
- Missing credential, missing or malformed contract, HTTP rejection, timeout, network failure, and unexpected exceptions remain explicit failures.
- Configuration unavailability is tolerated without fabricating a registered state; the publisher retries until an actual registration succeeds and then stops retrying.
- Common.Registration recursively removes value-bearing fields and normalizes requirement-kind wire metadata at the transport boundary as defense in depth.
- Studio CI and `Aegis.Studio.slnx` consume `fitzroywright/Common.Registration` directly at `Common/Common.Registration`.
- No Operations, Configuration, or Diagnostics product code is changed by this migration.

Validation status: the permanent Common.Registration repository is green. Studio's migration job has still failed before workflow steps execute because no GitHub runner is assigned, so Studio compilation is not yet claimed.