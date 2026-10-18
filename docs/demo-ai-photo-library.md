# Bryan's Studio — AI Demo Photograph Library

This is **development/demo-only**. No generation is performed during app startup or demo regeneration. The generation tool is a separate, opt-in script that makes paid OpenAI Images API calls. It never reads or writes production jobs or production media.

## 200-image target

| Folder | Images |
|---|---:|
| wedding | 40 |
| graduation | 30 |
| corporate | 30 |
| family | 25 |
| newborn | 20 |
| sports | 15 |
| product | 15 |
| id | 15 |
| portrait | 10 |
| **Total** | **200** |

Each image is a separate AI generation request, not a tile extracted from a collage. Prompts vary by subject, lighting, composition, and fictional demographics. Realistic photographs are intended; results should still be visually reviewed before demonstration.

## Generate on Windows

Requirements: Python 3.10+, an OpenAI API account with Images API access, a funded API project, and an API key. API usage is billed separately from ChatGPT. Never commit the key or generated images to Git.

```powershell
cd D:\Projects\Aegis.Studio
git pull origin main
$env:OPENAI_API_KEY = Read-Host "OpenAI API key"
python .\scripts\generate-demo-ai-photos.py --output D:\StudioDemoPhotos --dry-run --limit 3
python .\scripts\generate-demo-ai-photos.py --output D:\StudioDemoPhotos --limit 5
```

Inspect those five results first. Then resume to reach 200:

```powershell
python .\scripts\generate-demo-ai-photos.py --output D:\StudioDemoPhotos
```

The script skips completed files when their SHA-256 hash matches the adjacent JSON metadata. Interrupted runs can be resumed with the **same command**. A failed request exits with an error; restart to resume. Avoid multiple concurrent generators writing the same directory.

Options:
- `--category wedding` generates only that category.
- `--limit 10` caps the number of **new** images per invocation.
- `--quality low|medium|high` controls cost and image quality (default medium).
- `--delay 2` sets spacing between API requests.
- `--model gpt-image-1.5` selects the image model (default).
- `--dry-run` previews prompts without spending credits.

Pricing, quotas, supported models, and rate limits can change; check the [OpenAI API pricing](https://openai.com/api/pricing/) and [Images API documentation](https://platform.openai.com/docs/guides/image-generation) before running all 200. Budget for 200 individual requests; the tool does not estimate or cap dollar spending.

Each image has a matching JSON sidecar with prompt, model, category, generated-image flag, SHA-256, and source. The application reads the image files and marks their source as AI-generated; the sidecars remain for provenance and auditing.

## Install on Kratos

Keep the library **outside** the Studio demo object-storage root and outside resettable database records. This directory persists when demo data is cleared:

`/var/lib/aegis-studio/demo-ai-photos`

From Windows PowerShell, after generation:

```powershell
# Ensure destination exists and is writable by your SSH account.
ssh kratos "sudo mkdir -p /var/lib/aegis-studio/demo-ai-photos && sudo chown -R \$(id -un):\$(id -gn) /var/lib/aegis-studio/demo-ai-photos"
scp -r D:\StudioDemoPhotos\* kratos:/var/lib/aegis-studio/demo-ai-photos/
ssh kratos "sudo find /var/lib/aegis-studio/demo-ai-photos -type d -exec chmod 755 {} \; && sudo find /var/lib/aegis-studio/demo-ai-photos -type f -exec chmod 644 {} \;"
```

If the `kratos` SSH alias is unavailable, substitute the working host name and SSH identity used by your deployment script. Confirm the service account can read the directory. If your deployment uses a different persistent location, set `AEGIS_STUDIO_DEMO_AI_PHOTOS` in the `aegis-studio` systemd environment and restart the service.

Check installed image counts:

```bash
for d in wedding graduation corporate family newborn sports product id portrait; do
  printf '%-12s ' "$d"
  find "/var/lib/aegis-studio/demo-ai-photos/$d" -maxdepth 1 -type f \( -iname '*.png' -o -iname '*.jpg' -o -iname '*.jpeg' -o -iname '*.webp' \) | wc -l
done
```

Then deploy the latest Studio code and run **Reset and Regenerate** in the test environment. The application selects matching AI images from the library, rotating files by media index. If the library is incomplete, legacy source preparation may still use Wikimedia Commons; install at least one valid image in every supported category before expecting a fully offline reset.

## Operational boundaries

- AI images represent fictional people and should be identified as such in demo materials.
- Do not put real client photographs or sensitive personal data in prompts.
- Generation is not part of startup, build, tests, deployment, or demo reset.
- The library contains up to 200 unique source images, but a job with 40–50 media records can reuse sources if its category has fewer unique images. This is not 40–50 distinct images per job.
- The existing code selects AI photos when available. Do not interpret a successful demo reset as proof that all generated images were visually reviewed or that each asset is unique.
