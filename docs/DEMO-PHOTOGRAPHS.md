# Studio demo photographs

Demo generation uses only the local Kratos demo photo library at `/var/lib/aegis-studio/demo-photos` (or the path configured by `AEGIS_STUDIO_DEMO_PHOTOS`). It makes no online image requests.

The supplied Bryan's Photo Studios presentation photographs are copied from the demo-library ZIP into category folders: people, wedding, graduation, family, sports, and product. The seed selects from the matching folder and reuses those source photos as needed to populate the larger demo galleries. Each demo asset remains a separate Studio media record and storage object.

Before deleting or recreating demo records, Studio validates the image files and confirms each required category has at least one usable local image. If a category is missing or empty, generation stops with an actionable error and leaves the existing demo data intact.

The nine supplied photographs are approved for Studio demo-seed use. Do not republish them outside that demo context without the rights holder's permission.
