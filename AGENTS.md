# Repository working rules

## UI and CSS ownership

- Keep one canonical implementation for each page component, renderer, interaction, and stylesheet concern.
- Never add duplicate renderers, duplicate implementations, appended CSS override blocks, or workaround layers to make a change appear to work.
- Before changing a page, trace the current renderer and stylesheet owner. Edit that owner in place and remove obsolete competing code when consolidating behavior.
- Do not keep parallel fallback renderers for the same UI. Normalize the available data and pass it through one renderer.
- Avoid `!important` and repeated selector/property declarations. If a behavior needs different values at a breakpoint, keep the responsive rule in the component's owning CSS section.
- Before committing, inspect the full diff and search the affected page for duplicate render paths, duplicate selectors/properties, and stale rules. Run relevant tests when the environment supports them; state clearly when a check could not be run.

## Build and verification

- Run builds and tests locally on the user's Mac when requested; do not substitute cloud builds or claim unrun checks passed.
- Do not commit an unverified change as complete. Report the exact checks performed and any remaining verification needed.
