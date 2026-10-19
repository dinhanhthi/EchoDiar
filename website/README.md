# EchoDiar website

Static site (HTML + CSS + a little vanilla JS, no build step) for https://ed.dinhanhthi.com.

## Preview

```sh
python3 -m http.server -d website 8080
```

Open http://localhost:8080.

## Structure

- `index.html`: landing page. `docs.html`: documentation. `privacy.html`: privacy statement.
- `tokens.css`: design tokens (OKLCH colors, Geist fonts, spacing). `styles.css`: layout and components.
- `site.js`: theme toggle, docs scroll-spy, header version, screenshot lightbox.
- The app version in each page header (`.version-badge`) is read at runtime from the same-origin `appcast.xml` (highest build number) by `site.js`; the static `v0.1.0` in each page is only a fallback, so no hand edit is needed on release.
- `logo.png`, `poster.jpg`: images. `CNAME`: custom domain. `.nojekyll`: disables Jekyll. `llms.txt`: summary for LLM crawlers.

## Deploy

`.github/workflows/pages.yml` uploads this folder to GitHub Pages on every push to `main` that touches `website/`. It also publishes `appcast.xml` from the repo root, which Sparkle auto-update needs: do not remove it.

One-time setup:

1. Settings > Pages > Source = "GitHub Actions".
2. DNS: add a `CNAME` record `ed` pointing to `dinhanhthi.github.io`.
3. Settings > Pages: set the custom domain to `ed.dinhanhthi.com` and enable "Enforce HTTPS".

