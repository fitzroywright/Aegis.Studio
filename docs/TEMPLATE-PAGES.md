# Template and automation pages

The Admin menu opens three independent documents:

| Page | URL | Page controller |
| --- | --- | --- |
| Job Templates | `/admin/job-templates` | `wwwroot/job-templates.js` |
| Work Order Templates | `/admin/work-order-templates` | `wwwroot/work-order-templates.js` |
| Workflow Automation | `/admin/workflow-automation` | `wwwroot/workflow-automation.js` |

These menu entries are ordinary links, supporting full document navigation, new tabs,
bookmarks and refresh. Each URL uses Studio's existing authenticated document fallback
and shared shell. Page content is rendered by its own controller. No HTML shell is
copied, and no template-kind switch or combined guidance panel remains.

Job Templates retains the graph editor's draft, validation, publication and version APIs.
Work Order Templates owns its list and ordered-step editor. Workflow Automation retains
its existing trigger/action editor and authorization audit. Each page requests only its
own definitions. Administrator access is checked before fetching business definitions.
There are no data migrations or changes to existing jobs or saved templates.

Old `#studio=templates&kind=Job`, `WorkOrder` and `Automation` bookmarks, plus the old
`workflows-admin` destination, normalize to the matching page URL. Browser history stores
each distinct page destination. Session Plan Templates remains its existing separate page.
