# Bryan's unified AWS web/API portal — implementation and local acceptance

Branch: feature/unified-web-portal-auth-media-workday (draft PR #266). Base: merged main after PR #265.

## Architecture and compatibility
- One existing Node application, one existing intake/WorkDay SQLite database, and private S3 object storage for new gallery photographs when configured.
- Both web.bryanstudiosltd.com and api.bryanstudiosltd.com proxy to the same Node listener. Hostnames affect the root page, not API authorization.
- API hostname / returns a minimal JSON service status; existing /api/v1/* endpoints remain on both hosts.
- PUBLIC_ALLOWED_HOSTS must contain both hostnames in production. The reverse proxy MUST pass the original validated Host. Do not trust arbitrary X-Forwarded-Host.
- Existing Studio GalleryPushWorker stages its approved immutable versions using existing metadata + PUT media endpoints, and staff still approve publication. Client requests remain contact-based with optional Booking Reference; no client is linked by matching email alone.
- Existing WorkDay provisioning, account sessions, API operations and Studio's outbound exchange stay authoritative. The new web panel uses these existing contracts.
- Existing gallery bytes in SQLite remain readable. New media can be put into private S3. Client browser delivery is ONLY watermarked preview; final originals are NEVER available to ordinary client sessions.
- No ecommerce or payments added.

## Local Mac acceptance, before merge

Run on Mac with Node >= 24 (server uses node:sqlite), at repository root:

    git fetch origin
    git switch feature/unified-web-portal-auth-media-workday
    cd Aegis.Studio.PublicSite
    npm install --save-exact sharp@0.34.1 @aws-sdk/client-s3@3.1156.0
    node --check server.mjs
    node --check client-auth.mjs
    node --check gallery-watermark.mjs
    node --check gallery-object-storage.mjs
    node --check intake-api.mjs
    node --check intake-store.mjs
    node --check public/app.js
    node --check public/workday-web.js
    npm test

Commit BOTH package.json and the regenerated package-lock.json to this feature branch after local installation. Do not run production npm ci before package-lock.json has been updated. This draft deliberately does not claim the dependency lockfile has been generated remotely.

Run .NET local build/test:

    cd ..
    dotnet build Aegis.Studio.Web/Aegis.Studio.Web.csproj --no-restore --disable-build-servers -m:1 -p:BuildInParallel=false
    dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj --no-restore --disable-build-servers -m:1 -p:BuildInParallel=false

Run PostgreSQL integration tests against a disposable database using STUDIO_TEST_POSTGRES before merging. No production DB use.

## Production configuration

Set NODE_ENV=production, HTTPS reverse proxy and configure:
- PUBLIC_ALLOWED_HOSTS=web.bryanstudiosltd.com,api.bryanstudiosltd.com
- PUBLIC_WEB_HOST=web.bryanstudiosltd.com
- PUBLIC_API_HOST=api.bryanstudiosltd.com
- PUBLIC_WEB_ORIGIN=https://web.bryanstudiosltd.com
- Existing PUBLIC_API_KEYS_JSON / provisioning / WorkDay secrets kept intact
- FACEBOOK_APP_ID, FACEBOOK_APP_SECRET
- MICROSOFT_CLIENT_ID, MICROSOFT_CLIENT_SECRET
- MICROSOFT_TENANT_ID=consumers for personal Hotmail/Outlook accounts; a specific tenant UUID may be used for organizational-only sign-in, not arbitrary tenants.
- YAHOO_CLIENT_ID, YAHOO_CLIENT_SECRET
- GALLERY_STORAGE_MODE=s3, GALLERY_S3_BUCKET, AWS_REGION, optional GALLERY_S3_PREFIX
- PUBLIC_WATERMARK_PROCESSING_ENABLED=true
- GALLERY_WATERMARK_TEXT, GALLERY_WATERMARK_OPACITY, GALLERY_PREVIEW_MAX_WIDTH, GALLERY_WATERMARK_VERSION

Credentials go into AWS secret/environment facilities, never commit them. Use an IAM instance/task role with narrowly scoped GetObject/PutObject access to the private gallery prefix; block public access on the bucket.

### Provider application callbacks

- Facebook: https://web.bryanstudiosltd.com/auth/facebook/callback
- Microsoft: https://web.bryanstudiosltd.com/auth/microsoft/callback
- Yahoo: https://web.bryanstudiosltd.com/auth/yahoo/callback

Microsoft consumer authority is used for Hotmail, Outlook.com and Live.com accounts. Yahoo uses its current OIDC discovery document with Authorization Code + PKCE. Provider buttons are visible only when credentials are configured. No mock identities are enabled.

**Security review required before production:** run actual test accounts through all provider flows; check issuer, nonce, state cookie, session expiration, login/logout, proof redemption, cross-account isolation, invalid token rejection, callback errors and reverse-proxy origin behavior. Client linking uses Studio-issued one-time proofs, not email matching.

### Gallery access

- Studio publishes metadata and media via existing /api/v1/galleries routes.
- When watermark processing is on, media PUT generates a JPEG derivative before confirming upload and publication requires all derivatives.
- Authenticated client gallery reads under /api/galleries/* require a verified provider subject linked to the exact Studio Client ID. Download route for originals returns 403.
- Integration-only original: GET /api/v1/galleries/{publicationId}/media/{versionId}/original using a dedicated gallery:original:read bearer credential.
- Do not give gallery:original:read to the external website's browser or public JavaScript. Giving any external server originals means that server can distribute them, so provision explicitly.
- Use private no-store responses; never expose S3 URLs in browser responses.
- Existing pre-watermark published galleries need derivative backfill before client preview works. Use scoped POST /api/v1/galleries/{publicationId}/previews/backfill?limit=2 (gallery:publish) repeatedly until remaining=0, against a test gallery first. Do NOT expose old SQLite originals as fallback previews. If watermark configuration changes, bump GALLERY_WATERMARK_VERSION and call the same backfill endpoint with refresh=true and the returned nextCursor as after=... until nextCursor is null; it replaces derivatives but never originals.

### WorkDay upload acceptance

- Existing /api/v1/auth/login, /workday/bootstrap and assignment upload endpoints are reused.
- The web WorkDay panel lists only the authenticated account's published Today/Upcoming assignments.
- AWS checks assignment membership and JPEG/PNG/WebP file signatures and bounds before enqueuing; Studio independently checks AddAttachments and assignment ownership when consuming the queue.
- Upload success means queued, **not yet stored/approved in Studio**. Studio acknowledges later through existing exchange.
- Upload originals remain unwatermarked and never auto-publish.
- Existing exchange stores uploads in a durable SQLite queue including base64; this is a temporary scalability limit, not a final high-throughput streaming upload pipeline. Plan an object-staged upload/ack protocol before large batch production use.

## Outstanding before release
- Install new npm dependencies locally and commit synchronized package-lock.
- Validate real provider setup and account linking with test identities; revoke/relink cases.
- Add the real Bryan's logo/brand assets after comparing the approved public site. Current shell reuses existing reference styling with Bryan's text.
- Implement gallery derivative backfill for existing published media and verify operational S3 permissions.
- Complete WorkDay upload memory/queue scalability improvements, assignment permission test matrix and Studio receipt confirmation.
- Benchmark simultaneous client viewing and upload traffic.
- Exercise failure/retry, withdrawal and rollback with real AWS test deployment.
- Update deployment automation to configure DNS/TLS/proxy before enabling production clients.

## Rollback
Keep the previous released image and SQLite database backup. Do not drop any legacy media tables. Because private S3 objects are not shared with Studio, rollback to the old application preserves Studio's source of truth; any new S3-only publications may require a replay/republication after rollback. Revoke client sign-in provider credentials and deny public access if security checks fail. Never enable direct public S3 reads as a rollback workaround.
