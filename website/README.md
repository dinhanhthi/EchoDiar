# EchoDiar website

Static site (HTML + CSS + a little vanilla JS, no build step) for https://echodiar.coprimal.dev.

## Preview

```sh
python3 -m http.server -d website 8080
```

Open http://localhost:8080.

## Structure

- `index.html`: landing page (hero, how it works, screenshots, features, local-first, get started). `docs.html`: documentation. `privacy.html`: privacy statement. The three pages share one stylesheet and the same header and footer markup (only `aria-current` differs), so keep them in sync when you change one.
- `tokens.css`: design tokens (OKLCH colors from the logo's greens, light and dark, Instrument Sans and JetBrains Mono from Google Fonts, spacing). `styles.css`: layout and components.
- `site.js`: theme toggle, docs scroll-spy, header version, screenshot lightbox (the thumbnails open one carousel), the waveforms, and the hero animation (a call transcribed line by line, then a cited answer). Animations pause off screen; with reduced motion the illustrations show their final state. Without JS everything is visible.
- Keep the docs ids: the Windows app opens `docs.html#windows-warnings`, and the release scripts print `docs.html#install-windows`. If the fonts change, update the "This website" list in `privacy.html`.
- Windows links point at the Microsoft Store listing `https://apps.microsoft.com/detail/9N5X564S1F2D?mode=direct` (static, no feed lookup). `docs.html#windows-warnings` tells users of the older self-signed copy how to move to the Store; the app's Settings > About links that anchor, so keep the id.
- The app version in each page header (`.version-badge`) is read at runtime from the same-origin `appcast.xml` (highest build number) by `site.js`; the static `v0.1.0` in each page is only a fallback, so no hand edit is needed on release.
- `logo.png`, `poster.jpg` (social preview image), `screenshots/`: images. The screenshots are made from local, untracked sources in `docs/showcase/source/screenshots/` (demo library, clean captures, `label-shots.py` for the ribbons; its README says how). `CNAME`: custom domain. `.nojekyll`: disables Jekyll. `llms.txt`: summary for LLM crawlers.

## Deploy

`.github/workflows/pages.yml` uploads this folder to GitHub Pages on every push to `main` that touches `website/`. It also publishes `appcast.xml` from the repo root, which Sparkle auto-update needs: do not remove it. A push of `windows/EchoDiar.appinstaller` (the Windows App Installer feed) deploys it to `/windows/EchoDiar.appinstaller` the same way.

One-time setup:

1. Settings > Pages > Source = "GitHub Actions".
2. DNS: add a `CNAME` record `echodiar` in the `coprimal.dev` zone pointing to `coprimal.github.io`, DNS only (not proxied, or Pages cannot issue its certificate).
3. Settings > Pages: set the custom domain to `echodiar.coprimal.dev` and enable "Enforce HTTPS".

