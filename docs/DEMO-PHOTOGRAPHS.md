# Studio demo photograph catalogue

Aegis.Studio demonstration data uses real photographs. It does not generate synthetic photographs.

The demo loader downloads a curated set of photographs from Wikimedia Commons and stores them through Common.Storage. The original Commons file URL is retained in storage metadata (DemoSourceUrl) so provenance remains available after seeding.

Only photographs verified as CC0 or public domain are included in the curated catalogue.

| Demo use | Wikimedia Commons file | Rights used by demo |
| --- | --- | --- |
| Graduation / school | Graduating pupils.jpg | CC0 |
| Graduation / school | Graduating Students (GN11751).jpg | CC0 |
| Graduation / school | Graduation pictures.jpg | CC0 |
| Graduation / school | Ceremony graduation.jpg | CC0 |
| Corporate / ID | Headshot.Corporate.2023.jpg | CC0 |
| Corporate / ID | Woman (headshot).jpg | Public domain release |
| Family / baby | Parents and their baby.jpg | CC0 |
| Newborn | New born baby with father.jpg | CC0 |
| Sports / groups | Team picture time.jpg | CC0 |
| Product / commercial | Product photo.jpg | CC0 |
| Wedding | People at Wedding ceremony.jpg | CC0 |
| Wedding | A group picture together with the groom at Wedding ceremony.jpg | CC0 |
| Family portrait | Family portrait.jpg | U.S. federal government public domain |

The existing Carter Wedding catalogue also uses Wikimedia Commons photographs and records the Commons source URL in Common.Storage metadata.

## Seed behavior

The full demo adds 27 interconnected jobs and over 1,000 media records so Media, Subjects, production queues, dashboards and counts behave like a working studio. The loader downloads a limited curated set of real source photographs and caches the downloaded source bytes. Each asset version receives its own Common.Storage object and path; JPEG derivatives are constrained to 1200 pixels to keep the demo manageable.

This is intentional demo-data behavior. The records are distinct Studio assets, while the source photographs are a reusable licensed demonstration corpus.

Do not replace these photographs with AI-generated, procedurally generated or placeholder imagery.

