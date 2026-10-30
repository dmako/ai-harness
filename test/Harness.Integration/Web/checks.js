// The layout and accessibility checks harness-uicheck runs in each page
// (TASK-0057). Returns [{rule, el, text, detail}].
(() => {
  const out = [];
  const seen = new Set();
  const vw = document.documentElement.clientWidth;
  const add = (rule, el, detail) => {
    const k = rule + "|" + path(el) + "|" + detail;
    if (seen.has(k)) return;
    seen.add(k);
    out.push({ rule, el: path(el), text: label(el), detail });
  };
  function path(el) {
    const parts = [];
    for (let e = el; e && e.nodeType === 1 && e !== document.body && parts.length < 4; e = e.parentElement) {
      let s = e.tagName.toLowerCase();
      if (e.id) s += "#" + e.id;
      const cls = typeof e.className === "string" ? e.className.trim().split(/\s+/).filter(Boolean).slice(0, 2) : [];
      if (cls.length) s += "." + cls.join(".");
      parts.unshift(s);
    }
    return parts.join(" > ");
  }
  function label(el) {
    const t = (el.innerText || el.textContent || el.value || el.getAttribute("aria-label") || "").replace(/\s+/g, " ").trim();
    return t.length > 70 ? t.slice(0, 67) + "…" : t;
  }
  const shown = (el) => {
    const r = el.getBoundingClientRect();
    if (r.width === 0 || r.height === 0) return false;
    // content-visibility (a closed fold's content keeps its layout), visibility and opacity
    return el.checkVisibility({ contentVisibilityAuto: true, visibilityProperty: true, opacityProperty: true });
  };
  // the part of a rect its clipping ancestors show (a clamped line, a
  // scrolled box), null when none of it shows
  const visiblePart = (el, r) => {
    let left = r.left, top = r.top, right = r.right, bottom = r.bottom;
    for (let e = el; e && e !== document.body && e !== document.documentElement; e = e.parentElement) {
      const cs = getComputedStyle(e);
      if (cs.overflowX === "visible" && cs.overflowY === "visible") continue;
      const c = e.getBoundingClientRect();
      left = Math.max(left, c.left); top = Math.max(top, c.top);
      right = Math.min(right, c.right); bottom = Math.min(bottom, c.bottom);
      if (right <= left || bottom <= top) return null;
    }
    return { left, top, right, bottom, width: right - left, height: bottom - top };
  };
  // elements holding text of their own (a text node child)
  const texts = [];
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  for (let n = walker.nextNode(); n; n = walker.nextNode()) {
    if (!n.textContent.trim()) continue;
    const el = n.parentElement;
    if (!el || el.closest("script, style, noscript, [aria-hidden=true]") || !shown(el)) continue;
    const range = document.createRange();
    range.selectNodeContents(n);
    const rects = [...range.getClientRects()].map((r) => visiblePart(el, r)).filter((r) => r && r.width > 0.5 && r.height > 0.5);
    if (rects.length) texts.push({ el, node: n, rects });
  }

  // 1. horizontal overflow: the page scrolls sideways
  if (document.documentElement.scrollWidth > vw + 1) {
    const scrolls = (el) => {
      for (let e = el.parentElement; e; e = e.parentElement) {
        const ox = getComputedStyle(e).overflowX;
        if (ox === "auto" || ox === "scroll" || ox === "hidden" || ox === "clip") return e !== document.documentElement && e !== document.body;
      }
      return false;
    };
    let culprits = 0;
    for (const el of document.body.querySelectorAll("*")) {
      const r = el.getBoundingClientRect();
      const past = (e) => e.getBoundingClientRect().right > vw + 1;
      // the innermost element past the edge: none of its children is
      // a child inside a scroll box does not count: the box is then the culprit
      if (r.right > vw + 1 && r.width > 0 && !scrolls(el) && ![...el.children].some((c) => past(c) && !scrolls(c))) {
        add("overflow", el, `ends at ${Math.round(r.right)} px, the page is ${vw} px wide`);
        if (++culprits >= 5) break;
      }
    }
    if (!culprits) {
      // nothing past the edge on its own: name what reaches furthest
      // the outermost element past the edge whose parent is not
      let far = null, farR = 0;
      for (const el of document.body.querySelectorAll("*")) {
        const r = el.getBoundingClientRect();
        if (r.right > vw + 1 && r.width > 0 && el.parentElement && el.parentElement.getBoundingClientRect().right <= vw + 1) { far = el; farR = r.right; break; }
      }
      // or a box whose content (text included) spills past the window
      // though the box fits: the deepest such
      for (const el of document.body.querySelectorAll("*")) {
        const cs = getComputedStyle(el);
        const r = el.getBoundingClientRect();
        if (cs.overflowX === "visible" && r.width > 0 && r.left + el.scrollWidth > vw + 1 && r.right <= vw + 1 && !scrolls(el)) { far = el; farR = r.left + el.scrollWidth; }
      }
      add("overflow", far || document.body, `the page is ${document.documentElement.scrollWidth} px wide in a ${vw} px window${far ? `; the furthest element ends at ${Math.round(farR)} px` : ""}`);
    }
  }

  // 2. text overlapping other text (SVG chart labels included)
  const boxes = texts.map((t) => ({ el: t.el, rects: t.rects }));
  for (const el of document.querySelectorAll("svg text")) {
    if (shown(el)) boxes.push({ el, rects: [el.getBoundingClientRect()] });
  }
  const related = (a, b) => a === b || a.contains(b) || b.contains(a);
  const fixedish = (el) => {
    for (let e = el; e; e = e.parentElement) {
      const p = getComputedStyle(e).position;
      if (p === "fixed" || p === "sticky") return true;
    }
    return false;
  };
  // pairs are compared within 64 px bands of the page, not all with all
  const bands = new Map();
  boxes.forEach((b, i) => {
    b.fixed = fixedish(b.el);
    const ks = new Set();
    for (const r of b.rects) {
      for (let k = Math.floor((r.top + scrollY) / 64); k <= Math.floor((r.bottom + scrollY) / 64); k++) ks.add(k);
    }
    for (const k of ks) {
      if (!bands.has(k)) bands.set(k, []);
      bands.get(k).push(i);
    }
  });
  const compared = new Set();
  for (const idx of bands.values()) {
    for (let x = 0; x < idx.length; x++) {
      for (let y = x + 1; y < idx.length; y++) {
        const pk = idx[x] * 1e6 + idx[y];
        if (compared.has(pk)) continue;
        compared.add(pk);
        const a = boxes[idx[x]], b = boxes[idx[y]];
        if (related(a.el, b.el) || a.fixed !== b.fixed) continue;
        for (const ra of a.rects) {
          for (const rb of b.rects) {
            const w = Math.min(ra.right, rb.right) - Math.max(ra.left, rb.left);
            const h = Math.min(ra.bottom, rb.bottom) - Math.max(ra.top, rb.top);
            if (w > 2 && h > 2) {
              add("overlap", a.el, `overlaps "${label(b.el)}" (${path(b.el)}) by ${Math.round(w)}×${Math.round(h)} px`);
            }
          }
        }
      }
    }
  }

  // 3. clipped by its container: content cut off by overflow hidden/clip
  for (const el of document.body.querySelectorAll("*")) {
    const cs = getComputedStyle(el);
    const hid = (v) => v === "hidden" || v === "clip";
    if (!(hid(cs.overflowX) || hid(cs.overflowY)) || !shown(el) || !el.textContent.trim()) continue;
    if (cs.textOverflow === "ellipsis" || cs.webkitLineClamp !== "none" && cs.webkitLineClamp) continue; // cut on purpose, shown as cut
    if (el.closest("[role=img], svg") || el.clientWidth <= 1 && el.clientHeight <= 1) continue; // a chart; screen-reader text
    const overX = hid(cs.overflowX) && el.scrollWidth > el.clientWidth + 2;
    const overY = hid(cs.overflowY) && el.scrollHeight > el.clientHeight + 2;
    if (overX || overY) add("clipped", el, `${overX ? `${el.scrollWidth - el.clientWidth} px wider` : ""}${overX && overY ? ", " : ""}${overY ? `${el.scrollHeight - el.clientHeight} px taller` : ""} than it shows`);
  }

  // 4. a squeezed column: several words, about one per line, in a narrow box
  for (const t of texts) {
    const words = t.node.textContent.trim().split(/\s+/).length;
    const width = Math.max(...t.rects.map((r) => r.width));
    if (words >= 4 && t.rects.length >= Math.min(words, 4) && t.rects.length >= words * 0.6 && width < 90) {
      add("squeezed", t.el, `${words} words over ${t.rects.length} lines, ${Math.round(width)} px wide`);
    }
  }

  // 5. controls smaller than 24 px (WCAG 2.2 target size, minimum)
  for (const el of document.querySelectorAll("button, select, input:not([type=hidden]):not([type=checkbox]):not([type=radio]), textarea, summary, [role=tab], a.btn, a.tile, label > input")) {
    if (!shown(el) || el.closest("label") && (el.type === "checkbox" || el.type === "radio")) continue;
    const r = el.getBoundingClientRect();
    if (r.width < 24 || r.height < 24) {
      // a summary or control inline in a line of text is exempt
      if (el.tagName === "SUMMARY" && r.height >= 18 && r.width >= 48) continue;
      add("small-target", el, `${Math.round(r.width)}×${Math.round(r.height)} px`);
    }
  }

  // 6. text contrast below WCAG AA (4.5:1; 3:1 for large text)
  const parse = (c) => {
    let m = /^rgba?\(([^)]+)\)$/.exec(c);
    if (m) {
      const p = m[1].split(/[\s,\/]+/).filter(Boolean).map(Number);
      return [p[0], p[1], p[2], p.length > 3 ? p[3] : 1];
    }
    m = /^color\(srgb ([^)]+)\)$/.exec(c);
    if (m) {
      const p = m[1].split(/[\s\/]+/).filter(Boolean).map(Number);
      return [p[0] * 255, p[1] * 255, p[2] * 255, p.length > 3 ? p[3] : 1];
    }
    return null;
  };
  const over = (top, under) => [0, 1, 2].map((i) => top[i] * top[3] + under[i] * (1 - top[3])).concat(1);
  const background = (el) => {
    const layers = [];
    for (let e = el; e; e = e.parentElement) {
      const cs = getComputedStyle(e);
      if (cs.backgroundImage && cs.backgroundImage !== "none") return null; // a picture or gradient: not judged
      const c = parse(cs.backgroundColor);
      if (c && c[3] > 0) {
        layers.push(c);
        if (c[3] >= 1) break;
      }
    }
    let bg = [255, 255, 255, 1];
    for (let i = layers.length - 1; i >= 0; i--) bg = over(layers[i], bg);
    return bg;
  };
  const lum = (c) => {
    const ch = c.slice(0, 3).map((v) => {
      v /= 255;
      return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
    });
    return 0.2126 * ch[0] + 0.7152 * ch[1] + 0.0722 * ch[2];
  };
  const judged = new Set();
  for (const t of texts) {
    const el = t.el;
    if (judged.has(el) || el.closest("svg, [disabled], .inactive, del")) continue;
    judged.add(el);
    const cs = getComputedStyle(el);
    const fg0 = parse(cs.color);
    const bg = background(el);
    if (!fg0 || !bg) continue;
    const fg = over(fg0, bg);
    const l1 = lum(fg), l2 = lum(bg);
    const ratio = (Math.max(l1, l2) + 0.05) / (Math.min(l1, l2) + 0.05);
    const size = parseFloat(cs.fontSize), bold = Number(cs.fontWeight) >= 700;
    const need = size >= 24 || size >= 18.66 && bold ? 3 : 4.5;
    if (ratio < need) add("contrast", el, `${ratio.toFixed(2)}:1, needs ${need}:1 (${cs.color} on rgb(${bg.slice(0, 3).map(Math.round).join(", ")}))`);
  }

  // 7. a section that failed to load
  for (const el of document.querySelectorAll(".secerr")) {
    if (shown(el)) add("section-error", el, "a section shows its error");
  }
  return out;
})()
