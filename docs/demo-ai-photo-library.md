# Studio demo photo library on Kratos

Studio reads demo photographs only from the persistent folder `/var/lib/aegis-studio/demo-photos`. Set `AEGIS_STUDIO_DEMO_PHOTOS` on the Studio service to use another path.

The setup script installs the approved Bryan's Studio Demo Photo Library ZIP into six exact categories. It does not download images or infer categories from filenames. Demo generation validates the local library before clearing or rebuilding records, then uses and reuses those local photos to populate each gallery. It does not contact Wikimedia Commons, OpenPeople, or another online image source.

## Install on Kratos

From the Mac used by `Build-Deploy-Studio-Kratos.ps1`, copy the ZIP to Kratos using the deployment script's default host and account:

    scp -o User=fwright@fftpj.local ~/Downloads/Bryans-Studio-Demo-Photo-Library.zip kratos:/tmp/

Then run the installer at the Studio deployment path defined by `Deploy-Kratos-Server.sh`:

    ssh -l fwright@fftpj.local kratos 'sudo bash /opt/aegis/studio/Demo/Prepare-Kratos-Demo-Photo-Library.sh /tmp/Bryans-Studio-Demo-Photo-Library.zip'

The setup script creates the persistent photo root, assigns ownership to the Studio systemd service account, and installs the supplied photos into exact category folders. It verifies all six required categories and never downloads photos.

If the service uses a different unit name, set `AEGIS_STUDIO_SYSTEMD_UNIT` for the setup command. If it uses a different photo root, set `AEGIS_STUDIO_DEMO_PHOTOS` for both the setup command and the Studio service environment.
