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

// Screenshot lightbox: click the poster or a thumbnail to open a full-screen carousel. Left/Right
// buttons or arrow keys navigate (looping); click anywhere or press Esc to close.
(() => {
  const triggers = [...document.querySelectorAll("[data-zoom]")];
  const slides = triggers
    .map((trigger) => trigger.querySelector("img"))
    .filter(Boolean)
    .map((img) => ({ src: img.src, alt: img.alt }));
  if (!slides.length || typeof HTMLDialogElement === "undefined") return;

  const dialog = document.createElement("dialog");
  dialog.className = "lightbox";
  dialog.setAttribute("aria-label", "Screenshot viewer");
  dialog.innerHTML = `
    <img alt="" />
    <button class="lightbox-arrow lightbox-arrow--prev" type="button" aria-label="Previous screenshot">
      <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m15 6-6 6 6 6" /></svg>
    </button>
    <button class="lightbox-arrow lightbox-arrow--next" type="button" aria-label="Next screenshot">
      <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m9 6 6 6-6 6" /></svg>
    </button>
    <span class="lightbox-counter" aria-live="polite"></span>`;
  if (slides.length < 2) {
    dialog.querySelectorAll(".lightbox-arrow, .lightbox-counter").forEach((el) => el.remove());
  }
  document.body.append(dialog);

  const image = dialog.querySelector("img");
  const counter = dialog.querySelector(".lightbox-counter");
  const reduceMotion = matchMedia("(prefers-reduced-motion: reduce)");
  let index = 0;
  let preloaded = false;

  const show = (next, dir = 0) => {
    index = ((next % slides.length) + slides.length) % slides.length;
    image.src = slides[index].src;
    image.alt = slides[index].alt;
    if (counter) counter.textContent = `${index + 1} / ${slides.length}`;
    if (dir && !reduceMotion.matches) {
      // Read the motion tokens when the slide runs; the fallbacks keep animate() from
      // throwing on `duration: NaN` if the stylesheet is missing
      const tokens = getComputedStyle(document.documentElement);
      image.animate(
        [
          { opacity: 0, transform: `translateX(${dir * 1.75}rem)` },
          { opacity: 1, transform: "translateX(0)" },
        ],
        {
          duration: parseFloat(tokens.getPropertyValue("--dur-short")) || 220,
          easing: tokens.getPropertyValue("--ease-out").trim() || "ease-out",
        }
      );
    }
  };

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

  triggers.forEach((trigger, i) =>
    trigger.addEventListener("click", () => {
      show(i);
      if (!preloaded) {
        preloaded = true;
        slides.forEach((slide) => (new Image().src = slide.src));
      }
      dialog.showModal();
    })
  );

  for (const [selector, step] of [[".lightbox-arrow--prev", -1], [".lightbox-arrow--next", 1]]) {
    dialog.querySelector(selector)?.addEventListener("click", (event) => {
      event.stopPropagation();
      show(index + step, step);
    });
  }

  document.addEventListener("keydown", (event) => {
    if (!dialog.open || event.metaKey || event.ctrlKey || event.altKey) return;
    if (event.key === "ArrowRight") {
      event.preventDefault();
      show(index + 1, 1);
    } else if (event.key === "ArrowLeft") {
      event.preventDefault();
      show(index - 1, -1);
    }
  });

  dialog.addEventListener("click", close);
  dialog.addEventListener("cancel", (event) => {
    event.preventDefault();
    close();
  });
})();

// Header: a hairline appears once the page scrolls under it.
(() => {
  const header = document.querySelector(".site-header");
  if (!header) return;
  const update = () => header.classList.toggle("is-scrolled", scrollY > 4);
  addEventListener("scroll", update, { passive: true });
  update();
})();

// Waveforms: fill each [data-wave] with bars. A fixed pseudo-random sequence keeps the shape
// the same on every load; each bar gets its own speed and range so the motion never lines up.
(() => {
  let seed = 7;
  const random = () => ((seed = (seed * 16807) % 2147483647) - 1) / 2147483646;
  for (const wave of document.querySelectorAll("[data-wave]")) {
    const count = Number(wave.dataset.wave) || 64;
    const fragment = document.createDocumentFragment();
    for (let i = 0; i < count; i++) {
      const bar = document.createElement("i");
      const envelope = 0.45 + 0.55 * Math.sin((i / count) * Math.PI);
      const hi = (0.35 + random() * 0.65) * envelope;
      bar.style.setProperty("--hi", hi.toFixed(2));
      bar.style.setProperty("--lo", (0.08 + random() * 0.12).toFixed(2));
      bar.style.setProperty("--still", (hi * 0.7).toFixed(2));
      bar.style.setProperty("--rest", (0.08 + random() * 0.08).toFixed(2));
      bar.style.setProperty("--d", `${Math.round(380 + random() * 520)}ms`);
      bar.style.setProperty("--delay", `${-Math.round(random() * 900)}ms`);
      fragment.append(bar);
    }
    wave.append(fragment);
  }
})();

// Illustrations pause their loops while off screen or while the tab is hidden.
const watchVisibility = (element, onChange) => {
  let inView = true;
  const apply = () => {
    const visible = inView && !document.hidden;
    element.classList.toggle("is-paused", !visible);
    if (visible) element.classList.add("is-visible");
    onChange?.(visible);
  };
  if ("IntersectionObserver" in window) {
    new IntersectionObserver(
      ([entry]) => {
        inView = entry.isIntersecting;
        apply();
      },
      { threshold: 0.2 }
    ).observe(element);
  }
  document.addEventListener("visibilitychange", apply);
  apply();
};

document.querySelectorAll(".vignette, .catchup__art").forEach((element) => watchVisibility(element));

// Hero stage: the call plays out line by line. A new line shows grey (the draft the live
// model refreshes) and turns solid when its chunk is final; then a question is asked and the
// answer's timestamp lights up the line it cites. Reduced motion shows the finished window.
(() => {
  const stage = document.querySelector("[data-stage]");
  if (!stage || matchMedia("(prefers-reduced-motion: reduce)").matches) return;

  const lines = [...stage.querySelectorAll(".tx__line")];
  const question = stage.querySelector(".ask__q");
  const answer = stage.querySelector(".ask__a");
  const cite = stage.querySelector(".ask .cite");
  const me = stage.querySelector('[data-channel="me"]');
  const others = stage.querySelector('[data-channel="others"]');
  const clock = stage.querySelector("[data-rec-time]");
  if (!lines.length || !question || !answer || !me || !others) return;

  let visible = true;
  let waiting = [];
  watchVisibility(stage, (now) => {
    visible = now;
    if (now) {
      waiting.forEach((resume) => resume());
      waiting = [];
    }
  });
  const whenVisible = () => (visible ? Promise.resolve() : new Promise((resume) => waiting.push(resume)));
  const wait = async (ms) => {
    await new Promise((done) => setTimeout(done, ms));
    await whenVisible();
  };

  // Recording clock: jumps to each line's time, then ticks.
  let seconds = 17 * 60 + 30;
  const showClock = () => {
    if (clock) clock.textContent = `${String(Math.floor(seconds / 60)).padStart(2, "0")}:${String(seconds % 60).padStart(2, "0")}`;
  };
  setInterval(() => {
    if (!visible || stage.classList.contains("is-stopped")) return;
    seconds += 1;
    showClock();
  }, 1000);

  const speak = (speaker) => {
    me.classList.toggle("is-speaking", speaker === "me");
    others.classList.toggle("is-speaking", speaker === "lan" || speaker === "minh");
    others.classList.toggle("is-minh", speaker === "minh");
  };

  const reset = () => {
    for (const line of lines) line.classList.remove("is-in", "is-final", "is-cited");
    question.classList.remove("is-in");
    answer.classList.remove("is-in");
    cite?.classList.remove("is-active");
    stage.classList.remove("is-stopped");
  };

  stage.classList.add("is-live");
  reset();

  (async () => {
    for (;;) {
      await wait(700);
      for (const line of lines) {
        const [m, s] = (line.dataset.at || "0:0").split(":").map(Number);
        seconds = m * 60 + s;
        showClock();
        speak(line.dataset.speaker);
        line.classList.add("is-in");
        await wait(1500);
        line.classList.add("is-final");
        await wait(450);
      }
      // Stop, then ask: local AI runs once the recording has stopped.
      speak(null);
      stage.classList.add("is-stopped");
      await wait(700);
      question.classList.add("is-in");
      await wait(1000);
      answer.classList.add("is-in");
      await wait(900);
      cite?.classList.add("is-active");
      stage.querySelector("[data-cited]")?.classList.add("is-cited");
      await wait(5000);
      reset();
      await wait(500);
    }
  })();
})();
