# Studio demo photo library on Kratos

Studio reads demo photographs from the persistent folder /var/lib/aegis-studio/demo-photos. Set AEGIS_STUDIO_DEMO_PHOTOS on the Studio service to use another path.

## Photo sources

The supplied Bryan's Photo Studios presentation photos are installed from the separate Bryan's Studio Demo Photo Library ZIP. The seed reads only these local files, reuses them as needed to populate galleries, and never downloads photos from online sources.

## Install on Kratos

After deploying Studio, copy the photo-library ZIP from your Mac to Kratos, for example:

    scp ~/Downloads/Bryans-Studio-Demo-Photo-Library.zip <your-kratos-account>@kratos:/tmp/

Then run this as root on Kratos:

    sudo bash /path/to/published/app/Demo/Prepare-Kratos-Demo-Photo-Library.sh /tmp/Bryans-Studio-Demo-Photo-Library.zip

The setup script creates the persistent photo root, assigns ownership to the Studio systemd service account, and installs the supplied photos into category folders. It does not download images. The seed validates the local photo library before clearing or recreating demo records, then stores selected media through the configured Common.Storage provider.

If the service uses a different unit name, set AEGIS_STUDIO_SYSTEMD_UNIT for the setup command. If it uses a different photo root, set AEGIS_STUDIO_DEMO_PHOTOS both for the setup command and in the Studio service environment.
