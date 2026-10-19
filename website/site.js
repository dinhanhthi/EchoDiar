// Theme toggle. The saved theme is applied early by a tiny inline script in <head>.
(() => {
  const root = document.documentElement;
  const button = document.getElementById("theme-toggle");
  if (!button) return;

  button.addEventListener("click", () => {
    const systemDark = matchMedia("(prefers-color-scheme: dark)").matches;
    const current = root.dataset.theme || (systemDark ? "dark" : "light");
    const next = current === "dark" ? "light" : "dark";
    root.dataset.theme = next;
    try {
      localStorage.setItem("echodiar.theme", next);
    } catch {
      // Storage blocked (private mode): the choice lasts for this visit only.
    }
  });
})();

// Docs scroll-spy: the active section is the last one whose top has passed near the viewport top.
(() => {
  const links = [...document.querySelectorAll(".toc a[href^='#']")];
  if (!links.length) return;
  const entries = links
    .map((link) => ({ link, section: document.getElementById(link.hash.slice(1)) }))
    .filter((entry) => entry.section);

  const update = () => {
    const atBottom = innerHeight + scrollY >= document.documentElement.scrollHeight - 2;
    let current = entries[0];
    for (const entry of entries) {
      if ((atBottom && scrollY > 0) || entry.section.getBoundingClientRect().top <= 120) current = entry;
    }
    for (const { link } of entries) {
      if (link === current.link) link.setAttribute("aria-current", "location");
      else link.removeAttribute("aria-current");
    }
  };

  addEventListener("scroll", update, { passive: true });
  addEventListener("resize", update);
  update();
})();

// Header version. The badge's data-source says where it comes from:
//   appcast: newest build in the same-origin appcast.xml (Sparkle apps)
//   github:  latest GitHub release of data-repo
//   static:  keep the text written in the HTML
// Any failure keeps the static fallback text.
(async () => {
  const badge = document.getElementById("version-badge");
  if (!badge) return;
  const source = badge.dataset.source;
  const repo = badge.dataset.repo || "";
  if (!/^[\w.-]+\/[\w.-]+$/.test(repo)) return;
  let version = null;
  try {
    if (source === "appcast") {
      const response = await fetch("appcast.xml");
      if (!response.ok) return;
      const xml = new DOMParser().parseFromString(await response.text(), "application/xml");
      const ns = "http://www.andymatuschak.org/xml-namespaces/sparkle";
      let best = null;
      for (const item of xml.getElementsByTagName("item")) {
        const build = Number(item.getElementsByTagNameNS(ns, "version")[0]?.textContent);
        const short = item.getElementsByTagNameNS(ns, "shortVersionString")[0]?.textContent?.trim();
        if (short && /^\d+(\.\d+)*$/.test(short) && Number.isFinite(build) && (!best || build > best.build)) best = { build, short };
      }
      version = best?.short ?? null;
    } else if (source === "github") {
      const response = await fetch(`https://api.github.com/repos/${repo}/releases/latest`, {
        headers: { Accept: "application/vnd.github+json" },
      });
      if (!response.ok) return;
      version = /^v?(\d+(?:\.\d+)*)$/.exec((await response.json()).tag_name ?? "")?.[1] ?? null;
    }
  } catch {
    // Offline, rate limited or no appcast (local preview): keep the static version.
  }
  if (!version) return;
  badge.textContent = `v${version}`;
  badge.href = `https://github.com/${repo}/releases/tag/v${version}`;
})();

// Screenshot lightbox: click the poster to view it full screen; click anywhere or press Esc to close.
(() => {
  const trigger = document.querySelector("[data-zoom]");
  const source = trigger?.querySelector("img");
  if (!trigger || !source || typeof HTMLDialogElement === "undefined") return;
  const dialog = document.createElement("dialog");
  dialog.className = "lightbox";
  dialog.setAttribute("aria-label", "Screenshot");
  const image = document.createElement("img");
  image.src = source.currentSrc || source.src;
  image.alt = source.alt;
  dialog.append(image);
  document.body.append(dialog);
  const reduceMotion = matchMedia("(prefers-reduced-motion: reduce)");
  // Play the closing animation, then close; fall back to an immediate close if it never ends.
  const close = () => {
    if (!dialog.open || dialog.hasAttribute("data-closing")) return;
    if (reduceMotion.matches) return dialog.close();
    dialog.setAttribute("data-closing", "");
    const finish = () => {
      clearTimeout(timer);
      image.removeEventListener("animationend", finish);
      dialog.removeAttribute("data-closing");
      if (dialog.open) dialog.close();
    };
    const timer = setTimeout(finish, 400);
    image.addEventListener("animationend", finish);
  };
  trigger.addEventListener("click", () => dialog.showModal());
  dialog.addEventListener("click", close);
  dialog.addEventListener("cancel", (event) => {
    event.preventDefault();
    close();
  });
})();
