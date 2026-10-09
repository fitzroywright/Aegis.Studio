# Load or upgrade the Studio demo

Sign in as a Studio Administrator. Open **Admin**, then choose **Load Demo Data** and confirm. The same button upgrades databases that already contain the original demo; it does not require a reset.

The expansion adds to the six original DEMO jobs:

- 24 synthetic PNG landscape illustrations, in Demo selects and Demo proofs collections.
- 30 stored image versions: each job's first illustration has a second proof with provenance.
- 12 appointments over the next two weeks, linked to three demo studio rooms.
- 24 scheduled tasks assigned to the administrator loading the expansion.
- 16 fictional subjects, showing both single-person cards and batch rosters.
- Three workspace announcements, task audit entries and six internal conversation notes.

Open a DEMO workspace and a job. **Gallery** shows the images and version history; **Subjects** shows people; **Tasks / Calendar** shows the same tasks as a list or monthly calendar; **Activity** shows the task audit. Appointments appear under **Pipeline → Open scheduling**. Today shows the new tasks assigned to the loader.

Images are generated locally and stored in the configured VersionStorePath, or the normal VersionStore folder. No network image service is required. These are explicitly synthetic illustrations, not client photographs. No galleries are published and no outbound notifications are sent.

The loader is administrator-only. A transaction and advisory lock serialize seed requests. A separate demo-data-v2 marker allows existing demo databases to upgrade once; repeat clicks do not duplicate records or reset edited data. The original demo includes fictional deposits; the expansion adds no payments. Real workspaces are not modified.
