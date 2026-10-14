#!/usr/bin/env python3
"""Create a resumable, category-organized AI demo photo library for Bryan's Studio.

Requires Python 3.10+ and OPENAI_API_KEY. Uses the OpenAI Images API.
No external Python packages. Images are generated once, not during demo resets.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import random
import time
import urllib.error
import urllib.request

COUNTS = {"wedding": 40, "graduation": 30, "corporate": 30, "family": 25,
          "newborn": 20, "sports": 15, "product": 15, "id": 15, "portrait": 10}
SCENES = {
 "wedding": ["bride and groom at an outdoor Caribbean ceremony", "couple exchanging vows", "wedding party laughing", "first dance at reception", "bride getting ready", "groom with groomsmen", "family group portrait at wedding", "wedding guests celebrating"],
 "graduation": ["university graduate in cap and gown", "school graduation group portrait", "graduate with proud parents", "graduation ceremony on stage", "graduate outdoors with diploma", "two graduates celebrating"],
 "corporate": ["executive headshot in modern studio", "business leader in office", "professional team portrait", "entrepreneur portrait with natural window light", "corporate profile photograph"],
 "family": ["family of four outdoors", "multigenerational family studio portrait", "parents and children laughing", "siblings portrait", "family at a tropical park"],
 "newborn": ["sleeping newborn wrapped safely in soft blanket", "newborn with parent holding baby safely", "newborn close-up portrait", "parents with their newborn in natural window light"],
 "sports": ["youth football player on field", "track athlete at stadium", "basketball team portrait", "athlete holding medal", "football team group portrait"],
 "product": ["premium skincare bottle on studio backdrop", "handmade jewellery commercial product photo", "artisan coffee package studio photo", "leather handbag advertising photo", "fashion accessory on clean background"],
 "id": ["front-facing adult ID portrait against neutral light background", "passport-style portrait of adult with neutral expression", "formal identity portrait of adult"],
 "portrait": ["natural light editorial portrait", "senior portrait outdoors", "creative studio portrait", "casual lifestyle portrait", "formal studio portrait"]
}
DETAILS = ["warm morning light", "soft overcast daylight", "balanced studio softbox lighting",
           "golden hour", "subtle rim lighting", "neutral professional lighting"]
COMPOSITIONS = ["medium portrait framing", "environmental wide composition",
                "three-quarter portrait", "close-up with natural depth of field",
                "candid documentary composition"]
DIVERSITY = ["Jamaican and Caribbean people", "Black Caribbean people",
             "ethnically diverse people", "people of different adult ages"]
def prompt_for(category, index):
    rng = random.Random(f"bryans-studio:{category}:{index}:v1")
    scene = SCENES[category][index % len(SCENES[category])]
    detail = DETAILS[rng.randrange(len(DETAILS))]
    composition = COMPOSITIONS[rng.randrange(len(COMPOSITIONS))]
    people = DIVERSITY[rng.randrange(len(DIVERSITY))]
    return (f"Create ONE distinct, photorealistic professional photograph for a fictional photography studio demo. "
            f"Subject: {scene}. {people}. {detail}; {composition}. "
            f"Authentic human anatomy, natural skin texture, realistic camera optics, polished professional photography. "
            f"Different fictional subjects, outfits, backgrounds and poses from other photographs. "
            f"No collage, no grid, no montage, no borders, no watermarks, no logos, no text. "
            f"For ID photographs, centered face, plain background and even light. "
            f"All people are fictional; no resemblance to real public figures. "
            f"Composition variation ID {index + 1}.")
def generate(key, model, prompt, quality, size, attempts):
    payload = json.dumps({"model": model, "prompt": prompt, "size": size,
                          "quality": quality, "n": 1}).encode()
    for attempt in range(attempts):
        req = urllib.request.Request("https://api.openai.com/v1/images/generations",
            data=payload, headers={"Authorization": f"Bearer {key}",
                                   "Content-Type": "application/json"}, method="POST")
        try:
            with urllib.request.urlopen(req, timeout=180) as resp:
                result = json.load(resp)
            item = result["data"][0]
            if "b64_json" in item:
                return base64.b64decode(item["b64_json"])
            if "url" in item:
                with urllib.request.urlopen(item["url"], timeout=120) as response:
                    return response.read()
            raise RuntimeError("API response contains no image bytes")
        except urllib.error.HTTPError as exc:
            message = exc.read(2048).decode(errors="replace")
            if exc.code not in (429, 500, 502, 503, 504) or attempt == attempts - 1:
                raise RuntimeError(f"Image API HTTP {exc.code}: {message}") from exc
            retry = exc.headers.get("Retry-After")
            try: delay = min(120, max(1, float(retry))) if retry else min(120, 2 ** (attempt + 2))
            except ValueError: delay = min(120, 2 ** (attempt + 2))
            print(f"  HTTP {exc.code}; retrying in {delay:.0f}s", flush=True)
            time.sleep(delay)
        except (TimeoutError, urllib.error.URLError):
            if attempt == attempts - 1: raise
            time.sleep(min(120, 2 ** (attempt + 2)))
    raise RuntimeError("Exhausted retries")
def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--output", type=Path, default=Path("demo-ai-photos"))
    p.add_argument("--model", default="gpt-image-1.5")
    p.add_argument("--quality", default="medium", choices=("low", "medium", "high"))
    p.add_argument("--size", default="1024x1024")
    p.add_argument("--category", choices=tuple(COUNTS))
    p.add_argument("--limit", type=int, help="Maximum NEW images this run (for small pilot batches)")
    p.add_argument("--delay", type=float, default=2.0, help="Seconds between requests")
    p.add_argument("--attempts", type=int, default=5)
    p.add_argument("--dry-run", action="store_true")
    args = p.parse_args()
    if args.limit is not None and args.limit < 1: p.error("--limit must be positive")
    key = os.getenv("OPENAI_API_KEY")
    if not args.dry_run and not key: p.error("Set OPENAI_API_KEY in your environment")
    completed = 0
    for category, count in COUNTS.items():
        if args.category and category != args.category: continue
        directory = args.output / category
        for i in range(count):
            name = f"{category}-{i+1:03d}"
            image = directory / f"{name}.png"
            metadata = directory / f"{name}.json"
            if image.exists() and metadata.exists():
                try:
                    info = json.loads(metadata.read_text(encoding="utf-8"))
                    if hashlib.sha256(image.read_bytes()).hexdigest() == info.get("sha256"):
                        continue
                except (OSError, ValueError): pass
            if args.limit is not None and completed >= args.limit:
                print(f"Batch limit reached: {completed} new images"); return
            prompt = prompt_for(category, i)
            if args.dry_run:
                print(f"[DRY RUN] {image}: {prompt}"); completed += 1; continue
            directory.mkdir(parents=True, exist_ok=True)
            print(f"Generating {category} {i+1}/{count}: {image}", flush=True)
            data = generate(key, args.model, prompt, args.quality, args.size, args.attempts)
            digest = hashlib.sha256(data).hexdigest()
            temp = image.with_suffix(".png.part")
            temp.write_bytes(data)
            temp.replace(image)
            info = {"category": category, "index": i+1, "source": "OpenAI Images API",
                    "ai_generated": True, "model": args.model, "quality": args.quality,
                    "size": args.size, "prompt": prompt, "sha256": digest,
                    "filename": image.name}
            metadata.write_text(json.dumps(info, indent=2) + "\n", encoding="utf-8")
            completed += 1
            time.sleep(max(0, args.delay))
    print(f"Finished. New images generated: {completed}. Total target: {sum(COUNTS.values())}.")
if __name__ == "__main__":
    main()
