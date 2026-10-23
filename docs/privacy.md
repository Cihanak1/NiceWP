# NiceWP Privacy Policy & Data Handling

> **Summary**: NiceWP is designed with a local-first, privacy-respecting architecture. There is no user tracking, no telemetry, no cloud accounts, and no background phone-home analytics.

---

## 1. Local Processing & Telemetry

- **Zero NiceWP Telemetry**: NiceWP does not collect, record, or transmit analytics, diagnostic telemetry, hardware fingerprints, or user behavior to any NiceWP server or third-party analytics service.
- **Local Settings & Library**: All configuration settings (`settings.json`), library entries (`library.json`), and analysis caches (`wallpaper-analysis.json`) are stored strictly on your local disk in `%LocalAppData%\NiceWP\`.

---

## 2. Personalization Engine Privacy

- **100% Local Intelligence**: Personalization signals (including "More Like This", "Less Like This", "Never Show Again", favorites scoring, and manual apply patterns) are stored exclusively in `%LocalAppData%\NiceWP\personalization.json`.
- **Zero Cloud AI / Modeling**: All weighting, preference calculations, and decay algorithms run locally and deterministically on your CPU. No neural network inference or cloud profiling takes place.
- **Resettable**: The entire personalization profile can be wiped back to defaults at any time via **Settings > Personalization > Reset Personalization**, with zero residual cloud traces.

---

## 3. Discover & Online Library Network Usage

When you use the **Discover** tab to search and download wallpapers:
1. **Search Queries**: When you submit a search query, HTTPS requests are sent directly from your computer to the official APIs of the selected providers:
   - **Openverse API**: `https://api.openverse.org/v1/`
   - **Wikimedia Commons API**: `https://commons.wikimedia.org/w/api.php`
   - **NASA Image and Video Library API**: `https://images-api.nasa.gov/`
2. **Provider Visibility**: As with any standard web request, the provider's server sees your search query, user-agent string, and public IP address. Their respective privacy policies govern their server logs.
3. **Provider Toggles**: In **Settings > Online Library Providers**, you can independently enable or disable any provider. If a provider is toggled off, no requests are ever made to that service.
4. **Media Downloads**: When you download a wallpaper, the file is streamed directly from the hosting content delivery network (CDN) to your local disk under `%LocalAppData%\NiceWP\media\`. Provenance metadata (license, creator attribution, source URL) is preserved locally.

---

## 4. Network Security & SSRF Protection

NiceWP's online downloader implements strict local defense-in-depth:
- Only secure `https://` URLs are permitted.
- Loopback addresses (`127.0.0.1`, `localhost`), link-local IPs, private intranet ranges, and cloud metadata endpoints are strictly blocked to prevent Server-Side Request Forgery (SSRF).
- Maximum download sizes and image magic byte validation are enforced before saving to disk.
