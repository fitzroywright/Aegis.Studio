# Job photograph delivery

From Job Media select 1–10 approved photographs and choose Send to Client.
Email uses the linked CRM client, existing verified sender and unified messaging outbox.
Full and compressed JPEG attachments require Finance release; watermarked proofs use the existing pixel-embedded proof renderer. Total email attachment limit is 2.5 MB. Compression uses a maximum 2048 pixel edge and JPEG quality 82.
No delivery action changes Job status.

Gallery email and WhatsApp share a generic verified-client sign-in page at PublicSite:WebBaseUrl. They require the selected set to match an existing published, approved gallery; they do not publish new media. WhatsApp is manual and recorded as prepared, not sent.

Individual download links are disabled by default. Configure protected deployment settings:
- PhotoDelivery__LinksEnabled=true
- PhotoDelivery__DownloadOrigin=https://<externally reachable Studio HTTPS origin>/
- PhotoDelivery__LinkSigningKey=<random secret of at least 32 bytes, stored outside source control>
- PhotoDelivery__LinkExpiryDays=7 (bounded to 1–30)

Only approved JPEGs up to 100 MB each are supported by individual links. Links grant access to the exact selected versions, are bearer credentials, and should not be forwarded. The client page sends the token in a POST body, rather than a request URL. Do not log request bodies or outbound message bodies containing these credentials. Restrict ingress to the necessary download routes; do not expose internal Studio solely for this feature without a reviewed reverse proxy policy. The public site remains a separate deployment; individual links use the explicitly configured Studio download origin, not the AWS public gallery service.

Job Media > Download links lists expiry, revocation and download request counts and permits revocation. Counts indicate authorized file responses, not confirmed receipt. Access rechecks current client association, approval and financial release. A previously downloaded copy cannot be recalled. Key rotation prevents regeneration of old links; revoke active links if a key is compromised. Existing token hashes remain valid until expiry or revocation.

The implementation was committed without running a build or automated tests at the user's request. Before enabling the opt-in download feature in production, validate anonymous ingress, client isolation, link expiry/revocation, finance checks, memory/rate limits, and a controlled email delivery. No production configuration or deployment was changed.
