# Studio demo photographs

Demo generation uses only the local Kratos demo photo library at `/var/lib/aegis-studio/demo-photos` (or the path configured by `AEGIS_STUDIO_DEMO_PHOTOS`). It makes no online image requests.

The six required photo categories (people, wedding, graduation, family, sports, and product) are bundled with Studio from the approved Bryan's Photo Studios presentation library. The seed selects from the matching category and reuses those source photos as needed to populate the larger demo galleries. Each demo asset remains a separate Studio media record and storage object.

After deploying Studio to Kratos, run the published installer as root:

    sudo bash /path/to/published/app/Demo/Prepare-Kratos-Demo-Photo-Library.sh

It copies the bundled photos into their explicit category folders and assigns ownership to the Studio systemd service account. It never downloads images or guesses categories from filenames. Demo generation validates every local image and confirms each required category has at least one usable image before clearing or rebuilding demo records.

The supplied photographs are approved for Studio demo-seed use. Do not republish them outside that demo context without the rights holder's permission.
