# Studio demo photo library on Kratos

Studio reads demo photographs only from the persistent folder `/var/lib/aegis-studio/demo-photos`. Set `AEGIS_STUDIO_DEMO_PHOTOS` on the Studio service to use another path.

The six required categories are bundled with the Studio application from the approved Bryan's Studio Demo Photo Library. Demo generation validates the local library before clearing or rebuilding records, then uses and reuses these local photos to populate each gallery. It does not contact Wikimedia Commons, OpenPeople, or another online image source.

## Install on Kratos

After deploying Studio, run this as root on Kratos:

    sudo bash /path/to/published/app/Demo/Prepare-Kratos-Demo-Photo-Library.sh

The setup script creates the persistent photo root, assigns ownership to the Studio systemd service account, and copies images into their explicit category folders. It takes no ZIP argument and does not download photos.

If the service uses a different unit name, set `AEGIS_STUDIO_SYSTEMD_UNIT` for the setup command. If it uses a different photo root, set `AEGIS_STUDIO_DEMO_PHOTOS` for both the setup command and the Studio service environment.
