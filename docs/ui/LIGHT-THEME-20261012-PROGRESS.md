# Light theme reference palette — October 12, 2026

The supplied reference is a color reference only. Light now uses white card bodies, a pale blue-gray background, navy text, accessible blue actions, pastel title bars and matching card borders. Navigation/schedule are blue, Actions lavender, Workspaces/tasks mint, Needs Attention red/pink and Messages pink. Media headings, grid controls, search focus states, dialogs, reply/notes and selected views use the same palette.

Changes are confined to css/themes/light.css and its cache version in index.html. Existing Light card rounding is retained. No layout, sizing, spacing, JavaScript, upload or data behavior changed. Standard and Dark rules are untouched. The navy masthead, orange separator and brown footer remain the established shell.

Validation: all four theme stylesheet ownership checks pass. CSS parses with css-tree; new rules are scoped to office-light and introduce no geometry properties. Text contrast was checked numerically; body/heading text, secondary text and action blue on white/pale-blue surfaces exceed 4.5:1. Broader CSS ownership checks have two existing failures in unchanged studio-legacy.css (duplicate rail heading font-size) and jobs-page.css (row hover ownership).

Browser visual/computed-style verification remains pending. This environment has no Chromium binary, and the browser download returned an invalid archive. No live deployment or full UI run occurred. Review Today, Jobs, Workspaces, Front Desk, Media, Operations, Pipeline, Messages and dialogs after local deployment; confirm header controls retain white backgrounds while focused and all three themes retain their layout.
