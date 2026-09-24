(() => {
  "use strict";
  const panels = [...document.querySelectorAll("main > .panel")];
  const tabs = [...document.querySelectorAll(".tab[data-panel]")];
  const untilFound = "onbeforematch" in document.body;
  let current = null;

  function panelFor(id) {
    if (!id) return panels[0];
    const el = document.getElementById(id);
    if (!el) return panels[0];
    return el.classList.contains("panel") ? el : el.closest(".panel") || panels[0];
  }

  function show(panel, target, focus) {
    if (panel !== current) {
      panels.forEach(p => {
        const on = p === panel;
        if (on) p.removeAttribute("hidden");
        else p.setAttribute("hidden", untilFound ? "until-found" : "");
      });
      tabs.forEach(t => {
        const on = t.dataset.panel === panel.id;
        if (on) t.setAttribute("aria-current", "page"); else t.removeAttribute("aria-current");
      });
      document.title = panel.id === "start" ? "SSPI PS4 guide" : `${panel.dataset.title} · SSPI PS4 guide`;
      current = panel;
      const active = tabs.find(t => t.dataset.panel === panel.id);
      if (active && active.scrollIntoView) active.scrollIntoView({ block: "nearest", inline: "nearest" });
    }
    if (target && target !== panel) {
      target.scrollIntoView({ block: "start" });
    } else {
      window.scrollTo(0, 0);
    }
    if (focus) {
      const heading = (target && target !== panel ? target : panel).querySelector("h1, h2, h3");
      if (heading) {
        if (!heading.hasAttribute("tabindex")) heading.setAttribute("tabindex", "-1");
        heading.focus({ preventScroll: true });
      }
    }
  }

  function route(focus) {
    const id = decodeURIComponent(location.hash.slice(1));
    const target = id ? document.getElementById(id) : null;
    show(panelFor(id), target, focus);
  }

  window.addEventListener("hashchange", () => route(true));
  // Plain previous / next buttons at the end of each section.
  panels.forEach((p, i) => {
    const nav = document.createElement("nav");
    nav.className = "pager";
    nav.setAttribute("aria-label", "More sections");
    const link = (target, cls, label) => {
      const a = document.createElement("a");
      a.href = "#" + target.id;
      a.className = cls;
      a.innerHTML = `<small>${label}</small><span></span>`;
      a.querySelector("span").textContent = target.dataset.title;
      return a;
    };
    if (i > 0) nav.appendChild(link(panels[i - 1], "prev", "Previous"));
    if (i < panels.length - 1) nav.appendChild(link(panels[i + 1], "next", "Next"));
    p.appendChild(nav);
  });
  // Find in page can reveal text inside a hidden section; switch to that section.
  panels.forEach(p => p.addEventListener("beforematch", () => {
    history.replaceState(null, "", "#" + p.id);
    show(p, null, false);
  }));
  route(false);

  // ---- Walkthrough chapters ----
  const video = document.getElementById("walkthrough-video");
  const list = document.getElementById("chapters");
  const buttons = list ? [...list.querySelectorAll("button[data-t]")] : [];
  function seek(t) {
    if (!video) return;
    const go = () => { video.currentTime = t; video.play().catch(() => {}); };
    if (video.readyState >= 1) go(); else video.addEventListener("loadedmetadata", go, { once: true });
    if (video.preload === "none" || video.readyState === 0) video.load();
  }
  buttons.forEach(b => b.addEventListener("click", () => seek(Number(b.dataset.t))));
  if (video && buttons.length) {
    video.addEventListener("timeupdate", () => {
      let active = buttons[0];
      buttons.forEach(b => { if (video.currentTime + 0.25 >= Number(b.dataset.t)) active = b; });
      buttons.forEach(b => b.setAttribute("aria-current", b === active ? "true" : "false"));
    });
  }
  document.querySelectorAll(".chapter-link[data-chapter]").forEach(link => {
    const b = buttons.find(x => x.dataset.index === link.dataset.chapter);
    if (!b) { link.hidden = true; return; }
    link.addEventListener("click", () => {
      history.pushState(null, "", "#walkthrough");
      show(panelFor("walkthrough"), document.getElementById("walkthrough"), false);
      seek(Number(b.dataset.t));
    });
  });

  // ---- Screenshot lightbox ----
  const box = document.getElementById("lightbox");
  const boxImg = document.getElementById("lightbox-img");
  const boxCap = document.getElementById("lightbox-caption");
  // Every guide image can be enlarged, including setting rows and phone screens.
  document.querySelectorAll("main img").forEach(img => {
    if (img.closest("button.zoom, .hero")) return;
    const b = document.createElement("button");
    b.type = "button";
    b.className = "zoom";
    img.parentNode.insertBefore(b, img);
    b.appendChild(img);
  });
  document.querySelectorAll("button.zoom").forEach(z => {
    const img = z.querySelector("img");
    if (!img) return;
    z.setAttribute("aria-label", "Enlarge: " + img.alt);
    z.addEventListener("click", () => {
      if (!box || !box.showModal) { window.open(img.currentSrc || img.src, "_blank", "noopener"); return; }
      boxImg.src = img.currentSrc || img.src;
      boxImg.alt = img.alt;
      boxCap.textContent = img.alt;
      box.showModal();
    });
  });
  if (box) {
    document.getElementById("lightbox-close").addEventListener("click", () => box.close());
    box.addEventListener("click", e => { if (e.target === box || e.target === boxImg) box.close(); });
  }
})();
