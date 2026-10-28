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
- `site.js`: theme toggle, docs scroll-spy, header version, Windows download links, screenshot lightbox (the poster and the thumbnails open one carousel).
- The feed gates only the Windows download links (the `[data-windows]` elements of `index.html` and `docs.html`, which ship hidden). `site.js` fetches the same-origin `windows/EchoDiar.appinstaller` (committed once a release's Windows files are attached, `docs/RELEASING.md`), takes the release tag from its `MainBundle` `Uri`, points every `[data-windows-asset="<name>"]` link at `https://github.com/dinhanhthi/EchoDiar/releases/download/<tag>/<name>`, then shows the blocks. Never `releases/latest` for Windows: during a release it points at a release whose Windows files are not attached yet. Each link's static `href` is the Releases page (no-JS fallback); any failure keeps the blocks hidden, so local previews never show them. The explanatory sections (`docs.html#install-windows`, `#windows-warnings`) are always visible.
- The app version in each page header (`.version-badge`) is read at runtime from the same-origin `appcast.xml` (highest build number) by `site.js`; the static `v0.1.0` in each page is only a fallback, so no hand edit is needed on release.
- `logo.png`, `poster.jpg`, `screenshots/`: images. The screenshots are made from local, untracked sources in `docs/showcase/source/screenshots/` (demo library, clean captures, `label-shots.py` for the ribbons; its README says how). `CNAME`: custom domain. `.nojekyll`: disables Jekyll. `llms.txt`: summary for LLM crawlers.

## Deploy

`.github/workflows/pages.yml` uploads this folder to GitHub Pages on every push to `main` that touches `website/`. It also publishes `appcast.xml` from the repo root, which Sparkle auto-update needs: do not remove it. A push of `windows/EchoDiar.appinstaller` (the Windows App Installer feed) deploys it to `/windows/EchoDiar.appinstaller` the same way.

One-time setup:

1. Settings > Pages > Source = "GitHub Actions".
2. DNS: add a `CNAME` record `ed` pointing to `dinhanhthi.github.io`.
3. Settings > Pages: set the custom domain to `ed.dinhanhthi.com` and enable "Enforce HTTPS".

