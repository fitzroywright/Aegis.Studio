# Free demo photo source

The Studio demo seed uses the 100 synthetic people in the [OpenPeople project](https://github.com/Prompt-Haus/OpenPeople). Its image assets are licensed CC BY 4.0. The seed downloads and validates the portraits before it writes demo records, then records the source and license in media metadata.

No paid image generator, API account, key, or credits are needed. Generation does not run during app startup; the existing public images are downloaded only when demo data is seeded.

The seed keeps a local cache so it can reuse the portraits. By default, the cache is created beneath the operating system's temporary directory as `aegis-studio-demo-ai-photos/people`. This is writable by the Studio process and may be cleared by the operating system.

To keep the cache in another location, set `AEGIS_STUDIO_DEMO_AI_PHOTOS` to a directory that the Studio service account can create and write to, then restart the service. Do not point it at a protected parent such as `/var/lib/aegis-studio` unless that directory has already been created and granted to the service account.

Wedding, graduation, family, newborn and sports demo images continue to use their contextual Wikimedia sources. These are also downloaded by the demo seed and stored through the configured media storage provider.
