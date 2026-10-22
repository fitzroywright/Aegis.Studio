# Studio demo photo library on Kratos

Studio reads demo photographs from the persistent folder /var/lib/aegis-studio/demo-photos. Set AEGIS_STUDIO_DEMO_PHOTOS on the Studio service to use another path.

## Photo sources

The supplied Bryan's Photo Studios presentation photos are installed from the separate Bryan's Studio Demo Photo Library ZIP. The setup script places each image in a category folder on Kratos. The seed uses matching local photos first, then the reviewed free contextual catalogue for additional variation.

The setup script also downloads 100 realistic people portraits from the free OpenPeople set into people/. They are synthetic demo people under CC BY 4.0; Studio records their source and license in media metadata. Validated contextual catalogue images are cached persistently under source-cache/ for reuse on later seeds.

## Install on Kratos

After deploying Studio, copy the photo-library ZIP from your Mac to Kratos, for example:

    scp ~/Downloads/Bryans-Studio-Demo-Photo-Library.zip <your-kratos-account>@kratos:/tmp/

Then run this as root on Kratos:

    sudo bash /path/to/published/app/Demo/Prepare-Kratos-Demo-Photo-Library.sh /tmp/Bryans-Studio-Demo-Photo-Library.zip

The script creates the photo root with restricted permissions, assigns ownership to the Studio systemd service account, installs the supplied photos, and downloads up to 100 additional OpenPeople portraits. Rerunning it skips completed portrait downloads.

If the service uses a different unit name, set AEGIS_STUDIO_SYSTEMD_UNIT for the setup command. If the service uses a different photo root, set AEGIS_STUDIO_DEMO_PHOTOS both for the setup command and in the Studio service environment.

The seed validates local photographs and required remote catalogue inputs before clearing or recreating demo records. It then stores the selected media through the configured Common.Storage provider. No paid image service or API credentials are required.
