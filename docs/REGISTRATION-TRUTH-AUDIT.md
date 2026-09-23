# Registration truth audit

Scope: `common-registration-migration`.

- Aegis.Studio registers its durable identity with Aegis.Configuration and publishes registration lifecycle telemetry to Aegis.Operations.
- Studio does not register directly with Aegis.Diagnostics; remote DiagnosticLevel capability remains disabled until implemented.
- Runtime discovery reports requirement/configured-state evidence only; configuration and secret values are not published.
- Registration success is accepted only after the standalone Common.Registration library reports an actual successful Configuration HTTP response.
- Missing credential, missing or malformed contract, HTTP rejection, timeout, network failure, and unexpected exceptions remain explicit failures.
- Configuration unavailability is tolerated without fabricating a registered state; the publisher continues the lifecycle after success by periodically re-authenticating the durable identity and republishing the sanitized Configuration contract.
- Common.Registration recursively removes value-bearing fields and normalizes requirement-kind wire metadata at the transport boundary as defense in depth.
- Registration lifecycle events are authenticated to Aegis.Operations with the same durable registration credential, matching the Aegis.Hello reference pattern.
- Studio CI and `Aegis.Studio.slnx` consume `fitzroywright/Common.Registration` directly at `Common/Common.Registration`.
- No Operations, Configuration, or Diagnostics product code is changed by this migration.

Validation status: the permanent Common.Registration repository is green. Studio's migration job has still failed before workflow steps execute because no GitHub runner is assigned, so Studio compilation is not yet claimed.