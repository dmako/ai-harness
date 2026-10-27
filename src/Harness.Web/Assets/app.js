"use strict";
// harness GUI: a view of /api with a few reviewed writes (knowledge
// corrections, workspace membership). Everything is built with DOM nodes
// and textContent, never HTML strings, so stored text cannot inject
// markup.

const MEMORY_TYPES = ["goal", "constraint", "decision", "convention", "warning", "fact", "open_question"];
const MEMORY_GROUPS = {
  goal: "Goals", constraint: "Constraints", decision: "Decisions", convention: "Conventions",
  warning: "Warnings", fact: "Facts", open_question: "Open questions",
};
const TASK_STATUSES = ["ready", "claimed", "in_progress", "review", "blocked", "backlog", "done", "deferred"];
// Status is shown as icon + label, never color alone.
const STATUS_ICON = { ready: "●", claimed: "◐", in_progress: "◐", review: "◆", blocked: "✕", backlog: "○", done: "✓", deferred: "◌" };

const store = {
  get(k, d) { try { return localStorage.getItem("harness." + k) ?? d; } catch { return d; } },
  set(k, v) { try { localStorage.setItem("harness." + k, v); } catch { /* private window */ } },
};

const state = {
  projects: [],
  days: store.get("days", "30"),
  knowledge: { types: new Set(["goal", "constraint", "decision", "convention", "warning"]), q: "", inactive: false, scope: "" },
  proposals: { status: "pending" },
  tasks: { statuses: new Set(["ready", "claimed", "in_progress", "review", "blocked"]), blueprint: "", open: new Set(), collapsed: new Set() },
  history: new Set(), // memories whose history panel is open
};

// --- tiny DOM helper ---
function h(tag, attrs, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v == null || v === false) continue;
    if (k === "class") el.className = v;
    else if (k.startsWith("on")) el.addEventListener(k.slice(2), v);
    else el.setAttribute(k, v === true ? "" : v);
  }
  for (const c of children.flat(Infinity)) {
    if (c == null || c === false) continue;
    el.append(c instanceof Node ? c : String(c));
  }
  return el;
}

// fill replaces an element's children like h() adds them: lists are
// flattened, null and false skipped, anything else shown as text.
function fill(el, ...kids) {
  el.replaceChildren(...kids.flat(Infinity).filter((c) => c != null && c !== false).map((c) => (c instanceof Node ? c : String(c))));
}

// --- formatting ---
const pct = (f) => `${Math.round((f || 0) * 100)}%`;
const shortCommit = (c) => (c ? c.slice(0, 12) : "");
const baseName = (p) => p.split(/[\\/]/).filter(Boolean).pop() || p;
function ago(iso) {
  if (!iso) return "";
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (s < 60) return "just now";
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 48 * 3600) return `${Math.floor(s / 3600)} h ago`;
  return `${Math.floor(s / 86400)} days ago`;
}
function ms(n) { return n >= 1000 ? `${(n / 1000).toFixed(1)} s` : `${n} ms`; }
// compact writes an axis count: 950, 1.2K, 12K
function compact(n) { return n < 1000 ? String(n) : `${(n / 1000).toFixed(n < 10000 ? 1 : 0).replace(/\.0$/, "")}K`; }
function bytes(n) { return n < 1024 ? `${n} B` : `${(n / 1024).toFixed(1)} K`; }

// A Go pseudo-version (v0.0.0-20260929194539-761645d87556+dirty) reads
// better as its commit and date.
function harnessVersion(v) {
  const m = /^v0\.0\.0-(\d{4})(\d{2})(\d{2})\d{6}-([0-9a-f]{12})(\+dirty)?$/.exec(v || "");
  if (!m) return [v || "", ""];
  return [m[4].slice(0, 7) + (m[5] ? " (dirty)" : ""), `built from ${m[1]}-${m[2]}-${m[3]}`];
}

// stateKind sorts a project's live state into ok, stale, warn or bad.
function stateKind(live) {
  if (live.state === "ok") return live.stale ? "stale" : "ok";
  return live.state === "missing" || live.state === "error" ? "bad" : "warn";
}
const STATE_GLYPH = { ok: "✓", stale: "↻", warn: "▲", bad: "✕" };
const STATE_WORD = { ok: "ok", stale: "stale index", warn: "needs a fix", bad: "unreadable" };
// stateGlyph is a compact state: a shape per state (never colour alone)
// with the full text as its label.
function stateGlyph(live) {
  const k = stateKind(live);
  const text = live.state === "ok" ? STATE_WORD[k] : live.state.replaceAll("_", " ");
  return h("span", { class: `glyph g-${k}`, title: text, "aria-label": text, role: "img" }, STATE_GLYPH[k]);
}

// healthBadge is a project's state in one word, linking to Health: what
// is wrong if anything, otherwise healthy.
function healthBadge(p) {
  const l = p.live;
  const href = projectHref(p.id, "health");
  if (l.state !== "ok") return h("a", { href, class: "badge-link" }, stateBadge(l));
  const plural = (n, w) => `${n} ${w}${n === 1 ? "" : "s"}`;
  const [cls, label] = l.checks_failed > 0 ? ["bad", `${plural(l.checks_failed, "check")} failing`] : l.stale ? ["warn", "index stale"]
    : l.checks_warned > 0 ? ["warn", plural(l.checks_warned, "warning")] : ["ok", "healthy"];
  return h("a", { href, class: "badge-link", title: "See Health" }, h("span", { class: `state ${cls}` }, label));
}

function stateBadge(live) {
  const cls = live.state === "ok" ? (live.stale ? "warn" : "ok") : live.state === "missing" || live.state === "error" ? "bad" : "warn";
  const label = live.state === "ok" ? (live.stale ? "stale index" : "ok") : live.state.replaceAll("_", " ");
  return h("span", { class: `state ${cls}` }, label);
}

function meter(hits, of, title) {
  const f = of > 0 ? hits / of : 0;
  const fill = h("div", { class: "fill" + (hits === 0 ? " zero" : "") });
  fill.style.setProperty("--w", `${(f * 100).toFixed(1)}%`);
  return h("div", { class: "meter", title: title || `${hits} of ${of}` },
    h("div", { class: "track", role: "img", "aria-label": `${pct(f)}, ${hits} of ${of}` }, fill),
    h("span", { class: "num" }, `${pct(f)} · ${hits}/${of}`));
}

function rateMeter(f) {
  const fill = h("div", { class: "fill" + (f === 0 ? " zero" : "") });
  fill.style.setProperty("--w", `${(f * 100).toFixed(1)}%`);
  return h("div", { class: "meter" },
    h("div", { class: "track", role: "img", "aria-label": pct(f) }, fill), h("span", { class: "num" }, pct(f)));
}

// rate flags: error rates and slow p95s are called out with icon + text
function rateCell(rate, warnAt, badAt) {
  const cls = rate >= badAt ? "flag bad" : rate >= warnAt ? "flag warn" : "";
  return h("td", { class: "n" }, cls ? h("span", { class: cls }, pct(rate)) : pct(rate));
}
function latencyCell(n) {
  const cls = n >= 10000 ? "flag bad" : n >= 3000 ? "flag warn" : "";
  return h("td", { class: "n" }, cls ? h("span", { class: cls }, ms(n)) : ms(n));
}

function statusPill(status) {
  return h("span", { class: `pill st-${status}` }, h("span", { class: "ico", "aria-hidden": "true" }, STATUS_ICON[status] || "•"), status.replace("_", " "));
}
const LEVEL_ICON = { ok: "✓", warn: "▲", fail: "✕", problem: "✕", info: "ⓘ" };
function levelPill(level) {
  return h("span", { class: `pill lv-${level}` }, h("span", { class: "ico", "aria-hidden": "true" }, LEVEL_ICON[level] || "•"), level);
}
// taskBar shows how a project's tasks divide over their states, one
// stacked bar; with legend, each part is named with its icon and count.
const TASK_PARTS = [["done", "done", ["done"]], ["flight", "in flight", ["claimed", "in_progress", "review"]], ["ready", "ready", ["ready"]],
  ["blocked", "blocked", ["blocked"]], ["backlog", "backlog", ["backlog"]], ["deferred", "deferred", ["deferred"]]];
function taskBar(all, legend) {
  const n = (sts) => all.filter((x) => sts.includes(x.status)).length;
  const parts = TASK_PARTS.map(([k, label, sts]) => [k, label, n(sts), STATUS_ICON[sts[sts.length - 1]]]).filter(([, , c]) => c > 0);
  const bar = h("div", { class: "tbar", role: "img", "aria-label": parts.map(([, l, c]) => `${c} ${l}`).join(", ") },
    parts.map(([k, l, c]) => { const x = h("span", { class: "tb-" + k, title: `${c} ${l}` }); x.style.flexGrow = c; return x; }));
  if (!legend) return bar;
  return h("div", { class: "tbarbox" }, bar, h("div", { class: "tlegend" },
    parts.map(([k, l, c, ico]) => h("span", { class: "tl-" + k }, h("span", { class: "ico", "aria-hidden": "true" }, ico), h("b", {}, c), " " + l))));
}

function rateBar(f) {
  const x = h("span", {});
  x.style.width = `${Math.round(f * 100)}%`;
  return h("div", { class: "tbar one", role: "img", "aria-label": pct(f) }, x);
}
function tile(label, value, note) {
  return h("div", { class: "tile" }, h("div", { class: "label" }, label), h("div", { class: "value" }, value), note && h("div", { class: "note" }, note));
}
function section(title, hint, ...content) {
  return h("section", { class: "section" }, h("h3", {}, title), hint && h("p", { class: "hint" }, hint), ...content);
}
function kv(rows) {
  return h("dl", { class: "kv" }, rows.filter(([, v]) => v != null && v !== false && v !== "" && !(Array.isArray(v) && !v.length))
    .map(([k, v]) => [h("dt", {}, k), h("dd", {}, v)]));
}

// --- data ---
async function api(path, headers) {
  const res = await fetch(path, { headers: { Accept: "application/json", ...(headers || {}) } });
  const body = await res.json().catch(() => ({ error: res.statusText }));
  if (!res.ok) throw new Error(body.error || res.statusText);
  return body;
}
// post performs a write: the X-Harness-UI header and the JSON body are what
// the server requires (the browser adds the same-origin Origin).
async function post(path, body) {
  const res = await fetch(path, {
    method: "POST",
    headers: { Accept: "application/json", "Content-Type": "application/json", "X-Harness-UI": "1" },
    body: JSON.stringify(body || {}),
  });
  const out = await res.json().catch(() => ({ error: res.statusText }));
  if (!res.ok) throw new Error(out.error || res.statusText);
  return out;
}
const projectAPI = (p, what) => `/api/projects/${encodeURIComponent(p.id)}/${what}`;
// workInbox counts what waits in Work › Inbox: proposed tasks, blueprints
// and architectural tasks or marks.
const workInbox = (l) => (l.pending_arch_proposals || 0) + (l.pending_task_proposals || 0) + (l.pending_blueprint_proposals || 0);

// chevron is the open/closed marker of anything that expands: drawn in
// CSS so it reads the same size in every font.
const chevron = (open) => h("span", { class: "chev" + (open ? " open" : ""), "aria-hidden": "true" });

// --- routing (TASK-0023) ---
//   #/                              Home: the machine
//   #/p/<project>/<view>[/<item>]   a project's view
//   #/w/<workspace>/<view>          a workspace's view
//   #/knowledge, #/machine          your knowledge, the machine
//   #/search?q=…                    search
// Links of the earlier layout (#/<project>/<tab>, #/workspaces/<name>)
// redirect to their new places.
const PROJECT_VIEWS = [["overview", "Overview"], ["architecture", "Architecture"], ["knowledge", "Knowledge"], ["work", "Work"], ["agents", "Agents"], ["health", "Health"]];
const WORKSPACE_VIEWS = [["overview", "Overview"], ["issues", "Issues"], ["knowledge", "Knowledge"], ["shared", "Shared code"], ["libraries", "Libraries"], ["architecture", "Architecture"], ["members", "Members"]];
const LEGACY_TABS = { findings: "overview", context: "agents/context", providers: "agents/tools", metrics: "agents/tools", knowledge: "knowledge", proposals: "knowledge/inbox", tasks: "work", health: "health" };
const enc = encodeURIComponent;
// Display names (chosen in the page or with 'harness projects rename' and
// 'harness workspace rename'); the short names stay what commands use.
const pname = (p) => p.title || p.name || baseName(p.root);
const wname = (w) => w.title || w.name;
const wsByName = (name) => (state.workspaces || []).find((w) => w.name === name);
const wtitle = (name) => { const w = wsByName(name); return w ? wname(w) : name; };
const defaultView = () => { const v = store.get("projectView", "overview"); return PROJECT_VIEWS.some(([x]) => x === v) ? v : "overview"; };
const projectHref = (id, view, item) => `#/p/${enc(id)}/${view || defaultView()}${item ? "/" + enc(item) : ""}`;
const workspaceHref = (name, view) => `#/w/${enc(name)}/${view || "overview"}`;

function route() {
  const raw = location.hash.replace(/^#\/?/, "");
  const [path, query] = raw.split("?");
  const seg = path.split("/").filter(Boolean).map((s) => { try { return decodeURIComponent(s); } catch { return s; } });
  const r = { page: "home", id: "", ws: "", view: "", item: "", q: new URLSearchParams(query || "").get("q") || "", seg };
  switch (seg[0]) {
    case undefined: return r;
    case "p": return { ...r, page: "project", id: seg[1] || "", view: PROJECT_VIEWS.some(([v]) => v === seg[2]) ? seg[2] : defaultView(), item: seg.slice(3).join("/") };
    case "w": return { ...r, page: "workspace", ws: seg[1] || "", view: WORKSPACE_VIEWS.some(([v]) => v === seg[2]) ? seg[2] : "overview", item: seg[3] || "" };
    case "knowledge": case "machine": case "search": return { ...r, page: seg[0] };
    case "docs": return { ...r, page: "docs", item: seg[1] || "guide" };
    default: return { ...r, page: "legacy" };
  }
}

// legacyHash maps a link of the earlier layout to its new place.
function legacyHash(seg) {
  if (seg[0] === "workspaces") return seg[1] ? workspaceHref(seg[1]) : "#/";
  const to = LEGACY_TABS[seg[1]] || "overview";
  const [view, sub] = to.split("/");
  const item = seg[2] ? seg.slice(2).join("/") : sub;
  return projectHref(seg[0], view, item);
}

// sub is a view's own sections (Knowledge: memories, inbox; Agents:
// context, tools, findings), linked like tabs.
function subnav(base, items, current) {
  return h("nav", { class: "subnav", "aria-label": "Sections" }, items.map(([k, label, count]) =>
    h("a", { href: `${base}/${k}`, "aria-current": k === current ? "page" : null }, label, count > 0 && h("span", { class: "badge" }, count))));
}

// --- frame: breadcrumb, sidebar, theme ---
function renderCrumbs() {
  const r = route();
  const crumbs = [["Home", "#/"]];
  if (r.page === "workspace") {
    crumbs.push([wtitle(r.ws), workspaceHref(r.ws)]);
    if (r.view !== "overview") crumbs.push([WORKSPACE_VIEWS.find(([v]) => v === r.view)[1], ""]);
  } else if (r.page === "project") {
    const p = state.projects.find((x) => x.id === r.id);
    const wss = (p && p.workspaces) || [];
    const ws = wss.includes(state.lastWs) ? state.lastWs : wss[0];
    if (ws) crumbs.push([wtitle(ws), workspaceHref(ws)]);
    crumbs.push([p ? pname(p) : r.id, projectHref(r.id, "overview")]);
    crumbs.push([PROJECT_VIEWS.find(([v]) => v === r.view)[1], ""]);
  } else if (r.page === "knowledge") crumbs.push(["Your knowledge", ""]);
  else if (r.page === "machine") crumbs.push(["Machine", ""]);
  else if (r.page === "search") crumbs.push(["Search", ""]);
  const el = document.getElementById("crumbs");
  fill(el, ...crumbs.flatMap(([label, href], i) => [
    i > 0 && h("span", { class: "sep", "aria-hidden": "true" }, "›"),
    href && i < crumbs.length - 1 ? h("a", { href }, label) : h("span", { "aria-current": "page" }, label)]).filter(Boolean));
}

const THEMES = ["system", "light", "dark"];
function applyTheme() {
  const t = store.get("theme", "system");
  if (t === "system") document.documentElement.removeAttribute("data-theme");
  else document.documentElement.setAttribute("data-theme", t);
  const b = document.getElementById("theme");
  if (b) {
    b.textContent = { system: "◐", light: "☀", dark: "☾" }[t];
    b.title = `Theme: ${t} (click to change)`;
    b.setAttribute("aria-label", `Theme: ${t}`);
  }
}

// waitingIn is how many things in a project wait for a person.
const waitingIn = (p) => (p.live.pending_proposals || 0) + (p.live.pending_arch_proposals || 0) + (p.live.checks_failed || 0) + (p.live.state !== "ok" ? 1 : 0);

function navProject(p, ws, r) {
  const n = waitingIn(p);
  return h("a", {
    class: "nrow", href: projectHref(p.id, r.page === "project" ? r.view : ""), "aria-current": r.page === "project" && r.id === p.id && (!ws || state.lastWs === ws || !(p.workspaces || []).includes(state.lastWs)) ? "page" : null,
    onclick: () => { state.lastWs = ws || ""; document.getElementById("projects").classList.remove("open"); },
  }, h("span", { class: "nname" }, pname(p)), stateGlyph(p.live),
  n > 0 && h("span", { class: "badge", title: `${n} thing(s) wait for you: proposals, failing checks or unreadable state` }, n));
}

// NAV_CAP is how many projects an open workspace group lists; the rest
// are one link away on the workspace page.
const NAV_CAP = 8;

function renderProjects() {
  const nav = document.getElementById("projects");
  const r = route();
  const close = () => nav.classList.remove("open");
  const wss = state.workspaces || [];
  const inWs = new Set(wss.flatMap((w) => w.members.map((m) => m.project_id)));
  const q = (state.navFilter || "").toLowerCase();
  const hit = (p) => !q || pname(p).toLowerCase().includes(q) || p.root.toLowerCase().includes(q);
  // A big workspace starts collapsed, unless the page is inside it.
  const open = (w, members) => {
    const v = store.get("ws-open." + w.name, "");
    if (v) return v === "1";
    return members.length <= NAV_CAP || (r.page === "workspace" && r.ws === w.name) || members.some((p) => p.id === r.id);
  };
  const filter = state.projects.length > 12 && h("input", {
    type: "search", class: "nfilter", placeholder: "Filter projects", "aria-label": "Filter projects", value: state.navFilter || "",
    oninput: (ev) => { state.navFilter = ev.target.value; renderProjects(); document.querySelector(".nfilter")?.focus(); },
  });
  const summary = (members) => {
    const count = {};
    for (const p of members) count[stateKind(p.live)] = (count[stateKind(p.live)] || 0) + 1;
    return ["bad", "warn", "stale", "ok"].filter((k) => count[k]).map((k) => h("span", { class: `glyph g-${k}`, title: `${count[k]} ${STATE_WORD[k]}` }, STATE_GLYPH[k], h("span", { class: "gcount" }, count[k])));
  };
  const outside = state.projects.filter((p) => !inWs.has(p.id) && hit(p));
  fill(nav, [
    h("a", { class: "ntop", href: "#/", "aria-current": r.page === "home" ? "page" : null, onclick: close }, "⌂ Home"),
    h("a", { class: "ntop", href: "#/search", "aria-current": r.page === "search" ? "page" : null, onclick: close }, "⌕ Search"),
    filter,
    h("h2", { class: "nhead" }, "Workspaces", h("span", { class: "count" }, wss.length)),
    wss.length ? wss.map((w) => {
      const all = w.members.map((m) => state.projects.find((p) => p.id === m.project_id)).filter(Boolean);
      const members = all.filter(hit);
      if (q && !members.length) return null;
      const isOpen = q ? true : open(w, all);
      const waiting = all.reduce((s, p) => s + waitingIn(p), 0);
      const toggle = h("button", {
        class: "ghost small twist", "aria-expanded": String(isOpen), "aria-label": `Show or hide the projects of ${wname(w)}`,
        onclick: () => { store.set("ws-open." + w.name, isOpen ? "0" : "1"); renderProjects(); },
      }, chevron(isOpen));
      // shown in a stable order (by name), so selecting a project or a
      // count changing never moves a row; past the cap, the ones waiting
      // and the current one are the ones kept
      const byName = (a, b) => pname(a).localeCompare(pname(b));
      const sorted = [...members].sort(byName);
      const keep = [...members].sort((a, b) => waitingIn(b) - waitingIn(a) || (b.id === r.id) - (a.id === r.id) || byName(a, b)).slice(0, NAV_CAP);
      if (r.page === "project" && !keep.some((p) => p.id === r.id) && members.some((p) => p.id === r.id)) keep.push(members.find((p) => p.id === r.id));
      const shown = q || members.length <= NAV_CAP ? sorted : sorted.filter((p) => keep.includes(p));
      return h("div", { class: "ngroup" },
        h("div", { class: "nws" }, toggle,
          h("a", { href: workspaceHref(w.name), "aria-current": r.page === "workspace" && r.ws === w.name ? "page" : null, onclick: close },
            h("span", { class: "nicon", "aria-hidden": "true" }, "▦"), h("span", { class: "nname" }, wname(w)), h("span", { class: "count" }, w.members.length)),
          waiting > 0 && h("span", { class: "badge", title: `${waiting} thing(s) wait for you in its projects` }, waiting)),
        !isOpen && h("div", { class: "nsum" }, summary(all)),
        isOpen && h("div", { class: "nmembers" }, shown.map((p) => navProject(p, w.name, r)),
          !q && sorted.length > shown.length && h("a", { class: "nmore", href: workspaceHref(w.name, "members"), onclick: close }, `All ${sorted.length} projects →`)));
    }) : h("p", { class: "nempty" }, h("a", { href: "#/", onclick: close }, "Create one on Home"), " to search repositories together."),
    (outside.length > 0 || !q) && h("h2", { class: "nhead" }, "Projects", h("span", { class: "count" }, outside.length)),
    outside.map((p) => navProject(p, "", r)),
    !state.projects.length && h("p", { class: "nempty" }, "No projects registered yet. 'harness init' in a repository registers it."),
    q && !outside.length && !wss.some((w) => w.members.some((m) => { const p = state.projects.find((x) => x.id === m.project_id); return p && hit(p); })) && h("p", { class: "nempty" }, "No project matches."),
    h("div", { class: "nfoot" },
      h("a", { class: "ntop", href: "#/knowledge", "aria-current": r.page === "knowledge" ? "page" : null, onclick: close }, "Your knowledge"),
      h("a", { class: "ntop", href: "#/machine", "aria-current": r.page === "machine" ? "page" : null, onclick: close }, "Machine"),
      h("a", { class: "ntop", href: "#/docs", "aria-current": r.page === "docs" ? "page" : null, onclick: close }, "Docs")),
  ]);
}

// --- main ---
// renderMain draws the current route. A live update ({ live: true }) draws
// it off-screen and swaps it in when complete, keeping the scroll position
// and the focused field, so the page changes in place; filters and open
// panels live in state, so they come back as they were.
let renderSeq = 0;
async function renderMain(opts) {
  const live = !!(opts && opts.live);
  const n = ++renderSeq;
  const main = document.getElementById("main");
  if (live && main.querySelector(".aform, .decide")) return;
  renderCrumbs();
  if (!live) return drawMain(main, false);
  const next = h("div", {});
  await drawMain(next, true);
  if (n !== renderSeq || main.querySelector(".aform, .decide")) return; // navigation or a review opened while loading
  const x = window.scrollX, y = window.scrollY;
  const focus = focusOf(main);
  fill(main, ...next.childNodes);
  window.scrollTo(x, y);
  restoreFocus(main, focus);
}

// focusOf remembers the field being typed in, to put the caret back.
function focusOf(main) {
  const el = document.activeElement;
  if (!el || !main.contains(el) || !/^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName)) return null;
  const label = el.getAttribute("aria-label");
  const key = label ? `[aria-label="${CSS.escape(label)}"]` : el.name ? `[name="${CSS.escape(el.name)}"]` : "";
  if (!key) return null;
  let sel = null;
  try { sel = [el.selectionStart, el.selectionEnd]; } catch { /* no selection on this input type */ }
  return { selector: el.tagName.toLowerCase() + key, sel };
}
function restoreFocus(main, f) {
  const el = f && main.querySelector(f.selector);
  if (!el) return;
  el.focus({ preventScroll: true });
  if (f.sel && f.sel[0] != null) { try { el.setSelectionRange(f.sel[0], f.sel[1]); } catch { /* not a text field */ } }
}

// --- sections (TASK-0047) ---
// A section owns a node and draws itself: a loading bar the first time
// (or at once, from what the page already has), then its content, or its
// error with Retry; the rest of the page never waits for it. It names what
// it shows: a project (or a workspace's members, or any project) and the
// kinds of data (tasks, knowledge, proposals, findings, activity, sync,
// summary; registry and workspaces for the machine's lists). A live change
// draws again only the sections showing what changed, in place: scroll,
// focus and open folds are kept, an open form keeps its section as it is,
// and an answer arriving after a newer one is dropped.
const sections = new Set();

function mount(o) {
  const el = h("div", { class: "msec" });
  const m = { el, o, seq: 0 };
  m.run = async (first, force) => {
    const review = el.querySelector(".aform, .decide");
    if (!first && !force && review) return; // a form is open: leave it be
    const n = ++m.seq;
    if (o.draw) return put(m, o.draw);
    if (first) fill(el, h("div", { class: "loadbar", role: "progressbar", "aria-label": "Loading" }));
    const busy = first ? 0 : setTimeout(() => el.classList.add("busy"), 150);
    try {
      const node = await o.load();
      const currentReview = el.querySelector(".aform, .decide");
      if (n === m.seq && (!currentReview || currentReview === review)) put(m, () => node);
    } catch (e) {
      if (n === m.seq) fill(el, h("div", { class: "secerr" }, h("p", { class: "error" }, e.message), h("button", { class: "ghost", onclick: () => m.run(false) }, "Retry")));
    } finally {
      clearTimeout(busy);
      el.classList.remove("busy");
    }
  };
  sections.add(m);
  m.run(true);
  return el;
}

// put draws a section's content, keeping the page's scroll, the field
// being typed in and the folds that were open.
function put(m, draw) {
  const el = m.el;
  const x = window.scrollX, y = window.scrollY;
  const focus = focusOf(el);
  const open = new Set([...el.querySelectorAll("details[open] > summary")].map((s) => s.textContent));
  fill(el, draw());
  for (const s of el.querySelectorAll("details > summary")) if (open.has(s.textContent)) s.parentElement.open = true;
  if (el.isConnected) window.scrollTo(x, y);
  restoreFocus(el, focus);
}

// wants: a change shows something a section draws.
function wants(m, c) {
  const ks = m.o.kinds || [];
  if (c === "all") return true;
  if (!ks.length) return false;
  if ((c.registry && ks.includes("registry")) || (c.workspaces && ks.includes("workspaces"))) return true;
  const kinds = c.kinds || {};
  const ids = m.o.project ? [m.o.project] : m.o.members ? m.o.members() : m.o.anyProject ? Object.keys(kinds) : [];
  return ids.some((id) => (kinds[id] || []).some((k) => k === "all" || ks.includes(k)));
}

// invalidate draws again the sections that show what changed ("all":
// every section on the page, open forms too: they belong to the write that
// just finished).
function invalidate(c) {
  for (const m of sections) {
    if (!m.el.isConnected) { sections.delete(m); continue; }
    if (wants(m, c)) m.run(false, c === "all");
  }
}

// afterWrite follows a write made on this page: the lists and the
// sections, not the page around them.
async function afterWrite() {
  live.last = Date.now();
  if (await loadProjects()) renderProjects();
  invalidate("all");
}

async function drawMain(main, live) {
  const r = route();
  // the page main shows, so moving within it can keep its header and tabs
  const key = r.page === "project" ? "p:" + r.id : r.page === "workspace" ? "w:" + r.ws : "";
  const same = !live && key && main.dataset.page === key && main.querySelector("[role=tabpanel]");
  if (!same) delete main.dataset.page;
  switch (r.page) {
    case "legacy": location.replace(legacyHash(r.seg)); return;
    case "home": return renderHome(main);
    case "workspace": return renderWorkspacePage(main, r);
    case "knowledge": return renderUserKnowledge(main);
    case "machine": {
      fill(main, mount({ anyProject: true, kinds: ["registry", "workspaces", "summary"], load: async () => { const box = h("div", {}); await renderMachine(box); return box; } }));
      return;
    }
    case "docs": return renderDocs(main, r);
    case "search": return renderSearch(main, r);
  }
  const { id, view } = r;
  const cur = () => state.projects.find((x) => x.id === id);
  const p = cur();
  if (same && p) {
    for (const t of main.querySelectorAll(".tabs [role=tab]")) t.setAttribute("aria-selected", String(t.getAttribute("href") === projectHref(id, view)));
    fill(main.querySelector("[role=tabpanel]"), projectBody(cur, view, r.item));
    return;
  }
  if (!p) {
    fill(main, h("p", { class: "error" }, `Project ${id} is not registered on this machine.`), h("p", {}, h("a", { href: "#/" }, "Home")));
    return;
  }
  const head = mount({ project: id, kinds: ["summary", "proposals", "sync", "findings"], draw: () => projectHead(cur() || p) });
  fill(main, head, h("div", { role: "tabpanel" }, projectBody(cur, view, r.item)));
  main.dataset.page = "p:" + id;
}

// projectHead is a project page's header and tabs, from the project list.
function projectHead(p) {
  const id = p.id, view = route().view;
  const l = p.live;
  const renameBox = h("div", {});
  // The header: name and health; where it lives and what it belongs to;
  // one line of what changes. The harness version shows only when agents
  // run another build than this page.
  const [ver] = harnessVersion(p.harness_version);
  const [cur] = harnessVersion(state.build && state.build.version);
  const facts = [
    stackBadge(p.id),
    (l.branch || l.commit) && h("span", { title: l.commit || "" }, h("span", { class: "mi", "aria-hidden": "true" }, "⎇"), h("b", {}, l.branch || "detached HEAD"), l.commit && h("span", { class: "mono muted" }, " " + shortCommit(l.commit).slice(0, 7))),
    l.synced_at && h("span", {}, h("span", { class: "mi", "aria-hidden": "true" }, "↻"), "synced ", ago(l.synced_at)),
    p.last_mcp && h("span", {}, h("span", { class: "mi", "aria-hidden": "true" }, "◷"), "agents ", ago(p.last_mcp), (p.clients || []).length > 0 && h("span", { class: "muted" }, ` · ${p.clients.join(", ")}`)),
    ver && cur && ver !== cur && h("span", { class: "warnish", title: `This page runs harness ${cur}; the project's agents last used ${ver}. Restart the agents' MCP servers to use this build.` }, "▲ agents run another harness build"),
  ].filter(Boolean);
  const head = h("div", { class: "phead" },
    h("div", { class: "title" }, h("h2", {}, pname(p)), healthBadge(p), h("span", { class: "spacer" }),
      h("button", { class: "action", title: "Change the display name or the short name", onclick: () => fill(renameBox, renameForm({
        what: "project", display: p.title || "", short: p.name || "", folder: baseName(p.root), box: renameBox,
        send: (body) => post(projectAPI(p, "rename"), body),
      })) }, "✎ Rename")),
    h("div", { class: "where" },
      h("span", { class: "mono" }, p.root),
      p.name && p.name !== baseName(p.root) && h("span", { class: "muted" }, `short name ${p.name}`),
      (p.workspaces || []).length > 0 && h("span", { class: "inws" }, "in ",
        p.workspaces.map((w) => h("a", { class: "wschip", href: workspaceHref(w), title: "Open this workspace", onclick: () => { state.lastWs = w; } }, wtitle(w))))),
    renameBox,
    facts.length > 0 && h("p", { class: "metaline" }, facts),
    l.detail && h("p", { class: "detail" }, l.detail),
    l.stale && h("p", { class: "detail" }, l.stale));
  const badges = { knowledge: l.pending_proposals, work: workInbox(l), health: l.checks_failed };
  const tabs = h("div", { class: "tabs", role: "tablist", "aria-label": "Views" },
    PROJECT_VIEWS.map(([v, label]) => h("a", { href: projectHref(id, v), role: "tab", "aria-selected": String(v === view) },
      label, badges[v] > 0 && h("span", { class: v === "health" ? "badge bad" : "badge" }, badges[v]))));
  return [head, tabs];
}

// VIEW_KINDS: the kinds of data each project view shows (Health: none, it
// runs doctor's checks when opened or refreshed).
const VIEW_KINDS = {
  overview: ["summary", "tasks", "knowledge", "proposals", "findings", "sync"], architecture: ["findings", "sync"],
  "work/inbox": ["proposals"], work: ["tasks", "proposals"], health: [], "knowledge/inbox": ["proposals"], knowledge: ["knowledge"],
  "agents/tools": ["activity", "sync"], "agents/findings": ["findings", "activity"], agents: ["activity"],
};

// projectBody is a project's view as a section.
function projectBody(cur, view, item) {
  const p = cur();
  const l = p.live;
  // Health and Architecture read no project state (Architecture reads the
  // repository's files), so they work while the state cannot be read
  if (l.state !== "ok" && view !== "health" && !(view === "architecture" && l.state !== "missing")) {
    return h("div", {}, h("p", { class: "empty" }, "This project's state cannot be read until the problem above is fixed. "),
      h("a", { href: projectHref(p.id, "health") }, "See Health"));
  }
  const kinds = VIEW_KINDS[`${view}/${item}`] || VIEW_KINDS[view] || [];
  return mount({ project: p.id, kinds, load: () => projectView(cur() || p, view, item, {}) });
}

// projectView draws one of a project's views; Knowledge and Agents have
// sections of their own.
async function projectView(p, view, item, opts) {
  const base = `#/p/${enc(p.id)}/${view}`;
  switch (view) {
    case "overview": return viewOverview(p);
    case "architecture": return viewArchitecture(p);
    case "work": {
      // one switch: Blueprints, Tasks, Inbox; a task's id opens Tasks
      // blueprints were called milestones (TASK-0047): old links still open them
      const named = item === "milestones" ? "blueprints" : item;
      const sec = ["blueprints", "tasks", "inbox"].includes(named) ? named : named ? "tasks" : state.tasks.view || "blueprints";
      if (sec !== "inbox") state.tasks.view = sec;
      const nav = subnav(base, [["blueprints", "Blueprints"], ["tasks", "Tasks"], ["inbox", "Inbox", workInbox(p.live)]], sec);
      return h("div", {}, nav, sec === "inbox" ? await viewArchProposals(p) : await viewTasks(p, opts, sec));
    }
    case "health": return viewHealth(p);
    case "knowledge": {
      const inbox = item === "inbox";
      const nav = subnav(base, [["memories", "Memories"], ["inbox", "Inbox", p.live.pending_proposals]], inbox ? "inbox" : "memories");
      return h("div", {}, nav, inbox ? await viewProposals(p) : await viewKnowledge(p));
    }
    case "agents": {
      const sec = ["context", "tools", "findings"].includes(item) ? item : "context";
      const nav = subnav(base, [["context", "Context quality"], ["tools", "Tools & providers"], ["findings", "Findings"]], sec);
      const content = await { context: viewContext, tools: viewProviders, findings: viewFindings }[sec](p);
      return h("div", {}, nav, content);
    }
  }
  return h("p", { class: "error" }, `No view ${view}.`);
}

// --- workspaces (TASK-0021) ---
// Every workspace with its members; create, add, remove and delete go
// through a review of what will change before anything is sent. The writes
// only edit the machine's workspace list, never a project's own state.
const WS_NOTE = "Only the machine's workspace list (workspaces.json) changes; no project's .harness state is touched.";

async function renderWorkspacePage(main, r) {
  const ws = () => (state.workspaces || []).find((x) => x.name === r.ws);
  const members = () => (ws() ? ws().members.map((m) => state.projects.find((p) => p.id === m.project_id)).filter(Boolean) : []);
  const memberIds = () => members().map((p) => p.id);
  const body = () => mount({ members: memberIds, kinds: WS_VIEW_KINDS[r.view] || [], load: () => workspaceView(ws(), members(), r.view, r.item) });
  if (ws() && main.dataset.page === "w:" + r.ws && main.querySelector("[role=tabpanel]")) {
    for (const t of main.querySelectorAll(".tabs [role=tab]")) t.setAttribute("aria-selected", String(t.getAttribute("href") === workspaceHref(r.ws, r.view)));
    fill(main.querySelector("[role=tabpanel]"), body());
    return;
  }
  if (!ws()) {
    fill(main, h("p", { class: "error" }, `There is no workspace ${r.ws} on this machine.`), h("p", {}, h("a", { href: "#/" }, "Home")));
    return;
  }
  state.lastWs = r.ws;
  const head = mount({ members: memberIds, kinds: ["workspaces", "registry", "summary", "proposals"], draw: () => workspaceHead(ws(), members(), r) });
  fill(main, head, h("div", { role: "tabpanel" }, body()));
  main.dataset.page = "w:" + r.ws;
}

// WS_VIEW_KINDS: the kinds of the members' data each workspace view shows.
const WS_VIEW_KINDS = {
  overview: ["summary", "findings", "sync", "workspaces", "registry"], issues: ["sync"], knowledge: ["workspaces"],
  shared: ["sync", "findings"], libraries: ["sync"], members: ["workspaces", "registry", "summary", "sync"], architecture: ["sync", "findings"],
};

// workspaceHead is a workspace page's header and tabs.
function workspaceHead(w, members, r) {
  if (!w) return h("p", { class: "error" }, `There is no workspace ${r.ws} on this machine.`);
  const waiting = members.reduce((s, p) => s + waitingIn(p), 0);
  const renameBox = h("div", {});
  const head = h("div", { class: "phead" },
    h("div", { class: "title" }, h("span", { class: "kind" }, "workspace"), h("h2", {}, wname(w)), h("span", { class: "spacer" }),
      h("button", { class: "action", title: "Change the display name or the short name", onclick: () => fill(renameBox, renameForm({
        what: "workspace", display: w.title || "", short: w.name, box: renameBox,
        send: async (body) => { const res = await post(`/api/workspaces/${enc(w.name)}/rename`, body); location.hash = workspaceHref(res.workspace, route().view); },
      })) }, "✎ Rename")),
    w.title && h("p", { class: "path mono" }, `short name ${w.name}: agents and commands use workspace:${w.name}`),
    renameBox,
    h("p", { class: "path" }, `${w.members.length} project(s), created ${ago(w.created)}`,
      waiting > 0 ? ` · ${waiting} thing(s) wait for you` : ""));
  const tabs = h("div", { class: "tabs", role: "tablist", "aria-label": "Views" },
    WORKSPACE_VIEWS.map(([v, label]) => h("a", { href: workspaceHref(w.name, v), role: "tab", "aria-selected": String(v === route().view) }, label)));
  return [head, tabs];
}

async function workspaceView(w, members, view, item) {
  switch (view) {
    case "overview": {
      const glance = h("div", {}, h("p", { class: "empty" }, "Reading the members…"));
      atAGlance(w).then((x) => fill(glance, x)).catch((e) => fill(glance, h("p", { class: "error" }, e.message)));
      const out = h("div", {},
        section("Projects", null, membersSummary(w, members)),
        section("At a glance", "What the workspace is made of and how its parts depend on each other; details on Architecture and Members.", glance));
      return out;
    }
    case "issues": return issuesView(w);
    case "knowledge": return levelKnowledge(`workspace:${w.name}`, "Knowledge for every project of this workspace: it applies to each of them unless a project states otherwise (then the project's own takes precedence).");
    case "shared": return sharedCodeView(w);
    case "libraries": return sharedCode(w);
    case "members": {
      const [stk] = await Promise.all([api(`/api/workspaces/${enc(w.name)}/stack`).catch(() => null)]);
      return h("div", {}, membersManage(w), versionsTable(w, stk));
    }
    case "architecture": {
      const lens = item === "build" ? "build" : "runtime";
      const nav = subnav(workspaceHref(w.name, "architecture"), [["runtime", "Runtime: calls"], ["build", "Build: packages"]], lens);
      if (lens === "runtime") return h("div", {}, nav, await systemView(w));
      const g = await api(`/api/workspaces/${enc(w.name)}/dependencies`);
      const axes = (g.nodes || []).length <= 12
        ? "Read it on two axes: down is depth (users above what they use, as in Runtime), right is how many members use it. A member three or more others use gets a bus: where their lines run together, they draw as one heavier line. A library on a bus stands at the right, level with the middle of its users, whose lines meet and run in together."
        : "Users above what they use, as in Runtime; select a member to follow its lines.";
      return h("div", {}, nav, section("Build dependencies", `Which members compile against another member's packages (NuGet, npm, Go modules), read from their manifests: what has to be upgraded and rebuilt when one changes. ${axes} Libraries (no host or app of their own) are dashed.`, dependencyMap(g, w)));
    }
    default:
      return h("p", { class: "empty" }, `No view ${view}.`);
  }
}

// findingsRollup: the open findings per member, then between them (the
// services' calls, the code repeated across them): a bar of problems,
// warnings and notes each, worst first, marks apart (HAR-0117).
const sev = (r) => (r.levels.problem || 0) * 10000 + (r.levels.warn || 0) * 100 + (r.levels.info || 0);
function findingsRollup(w, rows) {
  const href = (r) => r.kind === "member" ? projectHref(r.member_id, "architecture") : workspaceHref(w.name, r.kind === "shared" ? "shared" : "architecture");
  const total = (r) => (r.levels.problem || 0) + (r.levels.warn || 0) + (r.levels.info || 0);
  const most = Math.max(1, ...rows.map(total));
  const LV = [["problem", "problem", "problems"], ["warn", "warning", "warnings"], ["info", "note", "notes"]];
  const row = (r) => {
    const n = total(r);
    const bar = h("div", { class: "rbar", role: "img", "aria-label": LV.map(([k, one, many]) => r.levels[k] ? `${r.levels[k]} ${r.levels[k] === 1 ? one : many}` : "").filter(Boolean).join(", ") || "nothing open" },
      LV.map(([k]) => {
        if (!r.levels[k]) return null;
        const seg = h("span", { class: `rseg rs-${k}` });
        seg.style.setProperty("--w", `${(100 * r.levels[k]) / most}%`);
        return seg;
      }));
    return h("div", { class: "rrow" + (n ? "" : " clear") },
      h("a", { class: "rname", href: href(r) }, r.name),
      h("div", { class: "rmid" }, r.note ? h("span", { class: "muted small" }, r.note) : n ? bar : h("span", { class: "okay small" }, "✓ nothing open")),
      h("div", { class: "rnums" }, LV.map(([k, one, many]) => r.levels[k] ? h("span", { class: `rn rn-${k}` }, `${r.levels[k]} ${r.levels[k] === 1 ? one : many}`) : null),
        r.review > 0 && h("span", { class: "tag fmark review" }, `${r.review} back for review`),
        r.by_design > 0 && h("span", { class: "tag fmark by_design" }, `${r.by_design} by design`)),
      h("div", { class: "rwhen muted small" }, r.snapshot ? ago(r.snapshot.taken_at) : ""));
  };
  const members = rows.filter((r) => r.kind === "member").sort((a, b) => sev(b) - sev(a) || a.name.localeCompare(b.name));
  const between = rows.filter((r) => r.kind !== "member");
  return h("div", { class: "card pad rollup" },
    h("div", { class: "rlegend muted small" }, LV.map(([k, , many]) => h("span", {}, h("span", { class: `rdot rs-${k}` }), many))),
    members.map(row),
    between.length > 0 && h("div", { class: "rsep muted small" }, "Between the members"),
    between.map(row));
}

// --- workspace at a glance ---
// A few lines on what the workspace is made of and how its parts depend on
// each other, each linking to where the detail is; only what applies.
async function atAGlance(w) {
  const [sys, stk, dep, fnd, iss] = await Promise.all([
    api(`/api/workspaces/${enc(w.name)}/system`).catch(() => null),
    api(`/api/workspaces/${enc(w.name)}/stack`).catch(() => null),
    api(`/api/workspaces/${enc(w.name)}/dependencies`).catch(() => null),
    api(`/api/workspaces/${enc(w.name)}/findings`).catch(() => null),
    api(`/api/workspaces/${enc(w.name)}/issues`).catch(() => null)]);
  const name = (id) => { const p = state.projects.find((x) => x.id === id); return p ? pname(p) : id; };
  const lines = [];
  const line = (icon, text, href, more) => lines.push(h("li", {}, h("span", { class: "gicon", "aria-hidden": "true" }, icon), h("span", {}, text, more && h("span", { class: "muted" }, " " + more)),
    href && h("a", { class: "glink", href }, "details →")));
  const arch = workspaceHref(w.name, "architecture");
  // what is open, first: findings and design issues together (HAR-0117)
  if (fnd || iss) {
    const rows = (fnd && fnd.rows) || [], is = ((iss && iss.issues) || []).filter((x) => x.kind !== "local-copy" && x.kind !== "duplicate");
    const count = (lv) => rows.reduce((n, r) => n + (r.levels[lv] || 0), 0) + is.filter((x) => x.level === lv).length;
    const [p, wn, nt] = [count("problem"), count("warn"), count("info")];
    const worst = rows.filter((r) => r.kind === "member").sort((a, b) => sev(b) - sev(a))[0];
    const words = [p && `${p} problem${p === 1 ? "" : "s"}`, wn && `${wn} warning${wn === 1 ? "" : "s"}`, nt && `${nt} note${nt === 1 ? "" : "s"}`].filter(Boolean);
    if (words.length) line(p ? "✕" : wn ? "▲" : "○", `${words.join(", ")} open`, workspaceHref(w.name, "issues"),
      worst && sev(worst) > 0 ? `· most in ${worst.name}` : "");
    else line("✓", "Nothing open", null, "· no findings or design issues");
  }
  if (sys) {
    const services = sys.nodes.filter((n) => n.kind === "service" || n.kind === "frontend");
    const libs = sys.nodes.filter((n) => n.kind === "library");
    const outside = sys.nodes.filter((n) => n.kind === "external" || n.kind === "broker");
    const parts = [`${services.length} service${services.length === 1 ? "" : "s"}`, libs.length && `${libs.length} librar${libs.length === 1 ? "y" : "ies"}`, outside.length && `${outside.length} outside system${outside.length === 1 ? "" : "s"}`].filter(Boolean);
    const badges = {};
    for (const n of [...services, ...libs]) { const st = (state.stacks || {})[n.member_id]; if (st) (badges[st.badge] = badges[st.badge] || []).push(n.name); }
    const bs = Object.entries(badges);
    line("▦", `Made of ${parts.join(", ")}`, null, bs.length === 1 ? `· all ${bs[0][0]}` : bs.length ? `· ${bs.map(([b, ns]) => `${b} (${ns.length})`).join(", ")}` : "");
    // who everyone needs at run time
    const calls = sys.links.filter((l) => l.style === "grpc" || l.style === "http");
    for (const n of services) {
      const callers = new Set(calls.filter((l) => l.to === n.id && services.some((x) => x.id === l.from)).map((l) => l.from));
      if (services.length > 2 && callers.size === services.length - 1) line("⇣", `Every other service calls ${n.name}`, arch, "· it is the one they cannot run without");
    }
    const cycles = (sys.smells || []).filter((x) => x.kind === "sync-cycle");
    if (cycles.length) line("▲", cycles[0].title, arch);
    // databases
    const dbs = sys.nodes.filter((n) => n.kind === "database");
    if (dbs.length) {
      const tenant = dbs.filter((d) => d.detail === "per tenant").length;
      const shared = (sys.smells || []).filter((x) => x.kind === "shared-database");
      line("⛁", `${dbs.length} database${dbs.length === 1 ? "" : "s"} in the code${tenant ? `, ${tenant} of them per tenant` : ""}`, arch, shared.length ? `· ${shared.length} shared between services` : "· each owned by one service");
    }
  }
  // libraries used by everyone (build time)
  if (dep) {
    const users = {};
    for (const e of dep.edges || []) (users[e.to] = users[e.to] || new Set()).add(e.from);
    for (const n of (dep.nodes || []).filter((x) => x.library)) {
      const k = (users[n.project_id] || new Set()).size, of = (dep.nodes || []).length - 1;
      if (k) line("◇", `${name(n.project_id)} is used by ${k === of ? `all ${k}` : `${k} of ${of}`} other members`, workspaceHref(w.name, "architecture") + "/build", "· a library, used through its packages");
    }
  }
  // versions behind or declared stale
  if (stk) {
    const behind = new Map(), stale = [];
    for (const r of (stk.rows || []).filter((x) => x.kind === "core" && x.published)) {
      for (const [id, vs] of Object.entries(r.versions)) {
        if (vs.some((v) => cmpVer(v, r.published) < 0)) behind.set(id, (behind.get(id) || new Set()).add(name(r.owner)));
        if (vs.some((v) => cmpVer(v, r.published) > 0) && !stale.includes(r.name)) stale.push(r.name);
      }
    }
    if (behind.size) line("↺", `${behind.size} member${behind.size === 1 ? " uses" : "s use"} older versions of packages from ${[...new Set([...behind.values()].flatMap((x) => [...x]))].join(", ")}`, workspaceHref(w.name, "members"), "· than their owners declare now");
    if (stale.length) line("?", `${stale.length} package version declaration${stale.length === 1 ? " looks" : "s look"} stale`, workspaceHref(w.name, "members"), `· ${stale.slice(0, 2).join(", ")}${stale.length > 2 ? "…" : ""}`);
    const rts = (stk.rows || []).filter((x) => x.kind === "runtime" && x.differ);
    for (const r of rts) line("↺", `${r.name} versions differ`, workspaceHref(w.name, "members"), `· ${[...new Set(Object.values(r.versions).flat())].join(", ")}`);
  }
  return lines.length ? h("ul", { class: "card pad glance" }, lines) : h("p", { class: "empty" }, "Nothing read yet.");
}

// --- system architecture (TASK-0027) ---
// The workspace as a system: services, front-ends, outside systems,
// brokers and databases, and how they communicate, read from the members'
// code and configuration. Distinct from package dependencies (Overview).
const STYLE_LABEL = { grpc: "gRPC", http: "HTTP", messaging: "messaging", database: "database" };
const KIND_LABEL = { frontend: "Front-ends", service: "Services", external: "Outside systems", broker: "Brokers", database: "Databases" };
async function systemView(w) {
  const s = await api(`/api/workspaces/${enc(w.name)}/system`);
  const byId = Object.fromEntries(s.nodes.map((n) => [n.id, n]));
  const label = (id) => (byId[id] ? byId[id].name : id);
  const count = (k) => s.nodes.filter((n) => n.kind === k).length;
  const styles = ["grpc", "http", "messaging", "database"].filter((st) => s.links.some((l) => l.style === st));
  const head = h("div", { class: "card pad archsum" },
    h("div", {}, h("b", {}, `${count("service") + count("frontend")} services${count("frontend") ? ` (${count("frontend")} front-end${count("frontend") === 1 ? "" : "s"})` : ""}`),
      h("span", { class: "muted" }, ` · ${count("external")} outside system${count("external") === 1 ? "" : "s"} · ${count("database")} database${count("database") === 1 ? "" : "s"} in code${count("broker") ? ` · ${count("broker")} broker${count("broker") === 1 ? "" : "s"}` : ""} · ${s.contracts.length} gRPC contracts`)),
    h("div", { class: "muted small" }, "What the code and configuration declare, not a running system: gRPC contracts and who serves and uses them, the services each configuration names, databases from the code's contexts, messaging libraries. No addresses: they belong to an environment. Package dependencies are the Build lens."));
  const taskMembers = s.nodes.filter((n) => n.kind === "service" || n.kind === "frontend" || n.kind === "library").map((n) => ({ id: n.id, name: n.name }));
  // repeated code is Shared code's (HAR-0106)
  const sysOnly = (xs) => (xs || []).filter((x) => x.kind !== "shared-code");
  const smells = smellsSection(s.findings && s.findings.length ? sysOnly(s.findings) : sysOnly(s.smells), "Between the services, each with the files that show it. Chatty is a heuristic.",
    "✓ No system smells: no circle of synchronous calls, every contract has both sides, no database is shared.",
    { snapshot: s.snapshot, members: taskMembers, memberName: label, post: (action, body) => post(`/api/workspaces/${enc(w.name)}/findings/${action}`, body),
      snippet: wsSnippet(w) });

  // the map: callers above the services they call; each service holds its
  // own databases (stacked when there is one per tenant); outside systems
  // and brokers in their own row; members talking to nothing as a note
  const side = h("aside", { class: "depside archside" });
  const map = h("div", { class: "archmap sysmap" });
  const svg = sv("svg", { class: "archlines", "aria-hidden": "true" });
  const nodes = {};
  let sel = null;
  const calls = s.links.filter((l) => l.style !== "database");
  const dbsOf = (id) => s.links.filter((l) => l.from === id && l.style === "database").map((l) => byId[l.to]).filter(Boolean);
  const talking = new Set(calls.flatMap((l) => [l.from, l.to]));
  const members = s.nodes.filter((n) => n.kind === "service" || n.kind === "frontend");
  const silent = members.filter((n) => !talking.has(n.id) && !dbsOf(n.id).length);
  const shown = members.filter((n) => !silent.includes(n));
  const others = shown.length - 1;
  const card = (n) => {
    const out = calls.filter((l) => l.from === n.id).length;
    const callers = new Set(calls.filter((l) => l.to === n.id && byId[l.from] && (byId[l.from].kind === "service" || byId[l.from].kind === "frontend")).map((l) => l.from));
    const hub = others > 1 && callers.size === others;
    const dbs = dbsOf(n.id);
    const el = h("button", { class: `anode syscard sys-${n.kind}${hub ? " hub" : ""}`, title: n.name, onclick: () => select(sel === n.id ? null : n.id) },
      h("span", { class: "aname" }, n.name),
      h("span", { class: "ameta" }, h("span", {}, `calls ${out} · called by ${callers.size}`),
        n.member_id && stackBadge(n.member_id, "small side", true)),
      hub && h("span", { class: "hubtag" }, "every other service calls it"),
      dbs.length > 0 && h("span", { class: "dbhead" }, "Databases"),
      dbs.length > 0 && h("span", { class: "dbs" }, dbs.map((d) => h("span", { class: "dbchip" + (d.detail === "per tenant" ? " tenant" : ""), title: d.detail === "per tenant" ? `${d.name}: one database per tenant (one context, many databases)` : `${d.name}${d.detail ? ": " + d.detail : ""}` },
        h("span", { class: "dbicon", "aria-hidden": "true" }), d.name, d.detail === "per tenant" && h("span", { class: "dbnote" }, "per tenant")))));
    nodes[n.id] = el;
    return el;
  };
  const layersDesc = [...new Set(shown.map((n) => n.layer))].sort((a, b) => b - a);
  const outside = s.nodes.filter((n) => n.kind === "external" || n.kind === "broker");
  fill(map, svg,
    layersDesc.map((l, i) => h("div", { class: "arow" }, h("div", { class: "alabel" }, i === 0 && layersDesc.length > 1 ? "callers" : i === layersDesc.length - 1 && layersDesc.length > 1 ? "called" : ""),
      h("div", { class: "anodes" }, shown.filter((n) => n.layer === l).map(card)))),
    outside.length > 0 && h("div", { class: "arow outrow" }, h("div", { class: "alabel" }, "outside"), h("div", { class: "anodes" }, outside.map((n) => {
      const el = h("button", { class: `anode sys-${n.kind}`, title: n.detail ? `declared by ${n.detail}` : n.name, onclick: () => select(sel === n.id ? null : n.id) },
        h("span", { class: "aname" }, n.kind === "broker" ? "⇄ " + n.name : n.name), h("span", { class: "ameta" }, n.kind === "broker" ? "broker" : "outside the workspace"));
      nodes[n.id] = el;
      return el;
    }))));
  function draw() {
    const box = map.getBoundingClientRect();
    svg.setAttribute("width", box.width); svg.setAttribute("height", box.height);
    const lines = [];
    for (const l of calls) {
      const a = nodes[l.from], b = nodes[l.to];
      if (!a || !b) continue;
      const hot = sel && (l.from === sel || l.to === sel);
      if (sel && !hot) continue;
      const fa = a.getBoundingClientRect(), ta = b.getBoundingClientRect();
      const x1 = fa.left + fa.width / 2 - box.left, x2 = ta.left + ta.width / 2 - box.left;
      const down = ta.top > fa.bottom - 4, up = ta.bottom < fa.top + 4;
      const y1 = (down ? fa.bottom : up ? fa.top : fa.bottom) - box.top;
      const y2 = (down ? ta.top : up ? ta.bottom : ta.bottom) - box.top;
      const m = down || up ? (y1 + y2) / 2 : y1 + Math.min(40, 14 + Math.abs(x2 - x1) / 12);
      // a line skipping a row goes around the side, not behind the cards
      // between (the side away from the middle)
      let d = `M${x1},${y1} C${x1},${m} ${x2},${m} ${x2},${y2}`;
      if (down && y2 - y1 > 170) {
        const right = x2 >= x1;
        const cx = right ? Math.max(fa.right, ta.right) - box.left + 36 : Math.min(fa.left, ta.left) - box.left - 36;
        const ym = (y1 + y2) / 2;
        d = `M${x1},${y1} C${x1},${y1 + 40} ${cx},${y1 + 20} ${cx},${ym} C${cx},${y2 - 20} ${x2},${y2 - 40} ${x2},${y2}`;
      }
      const p = sv("path", { d, class: `sline st-${l.style}${hot ? " hot" : ""}`, "marker-end": `url(#sa-${l.style})` });
      p.append(sv("title", {}, `${label(l.from)} → ${label(l.to)} (${STYLE_LABEL[l.style]}): ${l.what.join(", ")}`));
      lines.push(p);
    }
    fill(svg, sv("defs", {}, ["grpc", "http", "messaging"].map((st) => sv("marker", { id: `sa-${st}`, viewBox: "0 0 8 8", refX: "7", refY: "4", markerWidth: "7", markerHeight: "7", orient: "auto" },
      sv("path", { d: "M0,0 L8,4 L0,8 z", class: `shead st-${st}` })))), lines);
  }
  function select(id) {
    sel = id;
    const rel = new Set(id ? calls.filter((l) => l.from === id || l.to === id).flatMap((l) => [l.from, l.to]) : []);
    for (const [k, el] of Object.entries(nodes)) {
      el.classList.toggle("sel", k === id);
      el.classList.toggle("dim", !!id && !rel.has(k));
    }
    draw();
    if (!id) return fill(side, h("p", { class: "hint" }, "Select a service to see whom it calls and who calls it, how (gRPC contracts, HTTP, messaging) and where in the code, with its databases."));
    const n = byId[id];
    const linkItem = (l, other) => h("li", {},
      h("a", { href: "#", onclick: (ev) => { ev.preventDefault(); select(other); } }, label(other)), " ", h("span", { class: `stag st-${l.style}` }, STYLE_LABEL[l.style]),
      (l.contracts || []).length > 0 && h("div", { class: "muted small" }, `${l.contracts.length} contract${l.contracts.length === 1 ? "" : "s"}: `, listCut(l.contracts.map((c) => c.replace(/^I/, "").replace(/GrpcService$/, "")), 4)),
      l.what.filter((x) => !(l.contracts || []).includes(x)).length > 0 && h("div", { class: "muted small" }, "declared by ", listCut(l.what.filter((x) => !(l.contracts || []).includes(x)), 3), l.basis && ` (matched by ${l.basis})`),
      h("details", { class: "cut" }, h("summary", { class: "muted small" }, "evidence"), h("ul", { class: "evlist" }, l.evidence.map((e) => evidenceItem(e, wsSnippet(w), 0)))));
    const out = calls.filter((l) => l.from === id), inn = calls.filter((l) => l.to === id), dbs = dbsOf(id);
    fill(side, h("h4", {}, n.name), h("p", { class: "muted small" }, n.kind === "external" ? `outside the workspace, declared by ${n.detail}` : n.kind),
      n.member_id && h("a", { href: projectHref(n.member_id, "architecture"), class: "small" }, "Its project architecture →"),
      out.length > 0 && [h("h5", {}, `Calls (${out.length})`), h("ul", {}, out.map((l) => linkItem(l, l.to)))],
      inn.length > 0 && [h("h5", {}, n.kind === "broker" ? `Used by (${inn.length})` : `Called by (${inn.length})`), h("ul", {}, inn.map((l) => linkItem(l, l.from)))],
      dbs.length > 0 && [h("h5", {}, `Databases (${dbs.length})`), h("ul", {}, dbs.map((d) => {
        const l = s.links.find((x) => x.from === id && x.to === d.id);
        return h("li", {}, d.name, h("span", { class: "muted small" }, d.detail === "per tenant" ? " · one per tenant" : d.detail ? " · " + d.detail : ""),
          l && h("div", { class: "muted small" }, listCut(l.what, 3)));
      }))]);
  }
  select(null);
  const legend = h("div", { class: "syslegend" }, styles.filter((st) => st !== "database").map((st) => h("span", {}, sv("svg", { width: "28", height: "10", "aria-hidden": "true" }, sv("line", { x1: "1", y1: "5", x2: "27", y2: "5", class: `sline hot st-${st}` })), STYLE_LABEL[st])),
    h("span", {}, h("span", { class: "dbchip mini" }, h("span", { class: "dbicon" })), "database"), h("span", {}, h("span", { class: "dbchip mini tenant" }, h("span", { class: "dbicon" })), "one database per tenant"));
  const layout = h("div", { class: "deplayout archlayout" }, h("div", { class: "depwrap archwrap" }, legend, map), side);
  requestAnimationFrame(draw);
  if (window.ResizeObserver) new ResizeObserver(() => draw()).observe(map);

  // contracts: declared where, served by whom, used by whom
  const ctable = (cs) => h("div", { class: "card" }, h("table", {},
    h("thead", {}, h("tr", {}, ["contract", "declared in", "served by", "used by"].map((c) => h("th", {}, c)))),
    h("tbody", {}, cs.map((c) => h("tr", {},
      h("td", { class: "mono", title: c.declared }, c.name),
      h("td", {}, label(c.owner)),
      h("td", {}, c.servers.length ? c.servers.map(label).join(", ") : h("span", { class: "flag warn" }, "none")),
      h("td", {}, c.clients.length ? c.clients.map(label).join(", ") : h("span", { class: "muted" }, "no member")))))));
  // contracts used between members first; the rest folded
  const used = s.contracts.filter((c) => c.clients.length || !c.servers.length), idle = s.contracts.filter((c) => !c.clients.length && c.servers.length);
  const contracts = s.contracts.length > 0 && section("gRPC contracts", "Each service contract: the member declaring it, the members serving it (implementing it) and using it.",
    used.length > 0 && ctable(used),
    idle.length > 0 && h("details", { class: "more card pad section" }, h("summary", {}, `${idle.length} served, used by no other member`), ctable(idle)));
  return h("div", {}, head, smells,
    section("Communication", "Who calls whom and how: callers above the services they call, lines pointing from the caller. Each service shows the databases its code owns.", layout,
      silent.length > 0 && h("p", { class: "muted small" }, `Services with no call, configured address or database found: ${silent.map((n) => n.name).join(", ")}.`)),
    contracts);
}

// --- dependency map (scales to dozens of members) ---
const SVGNS = "http://www.w3.org/2000/svg";
// sv builds an SVG element (h() builds HTML ones).
function sv(tag, attrs, ...children) {
  const el = document.createElementNS(SVGNS, tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v == null || v === false) continue;
    if (k.startsWith("on")) el.addEventListener(k.slice(2), v); else el.setAttribute(k, v);
  }
  for (const c of children.flat(Infinity)) if (c != null && c !== false) el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  return el;
}

// depModel prepares the graph for drawing: foundations (packages most
// members use) leave the drawing, cycles are found (strongly connected
// components), the rest is layered left to right (providers left) and
// ordered within each column to keep crossings low.
function depModel(g) {
  const nodes = g.nodes || [], edges = g.edges || [];
  const ids = nodes.map((n) => n.project_id);
  const usedBy = {}, uses = {};
  for (const id of ids) { usedBy[id] = []; uses[id] = []; }
  for (const e of edges) { uses[e.from].push(e); usedBy[e.to].push(e); }
  // Foundations: in a large workspace (over 12 members), packages used by
  // at least 60% of them are taken out of the map so it stays readable; a
  // small one draws everything.
  const foundation = new Set(ids.length <= 12 ? [] : ids.filter((id) => usedBy[id].length >= Math.max(3, Math.ceil(ids.length * 0.6))));
  const library = new Set(nodes.filter((n) => n.library).map((n) => n.project_id));
  const drawn = edges.filter((e) => !foundation.has(e.to));
  // Cycles: Tarjan's strongly connected components over every edge.
  let index = 0; const idx = {}, low = {}, on = new Set(), stack = [], comps = [];
  const out = (id) => uses[id].map((e) => e.to);
  function strong(v) {
    idx[v] = low[v] = index++; stack.push(v); on.add(v);
    for (const w of out(v)) {
      if (idx[w] === undefined) { strong(w); low[v] = Math.min(low[v], low[w]); } else if (on.has(w)) low[v] = Math.min(low[v], idx[w]);
    }
    if (low[v] === idx[v]) { const c = []; let w; do { w = stack.pop(); on.delete(w); c.push(w); } while (w !== v); comps.push(c); }
  }
  for (const id of ids) if (idx[id] === undefined) strong(id);
  const comp = {};
  comps.forEach((c, i) => c.forEach((id) => { comp[id] = i; }));
  const cycles = comps.filter((c) => c.length > 1);
  // Columns: a member sits one column right of the furthest member it uses
  // (cycles share a column; foundations are left out).
  const col = {};
  const rest = ids.filter((id) => !foundation.has(id));
  for (const id of rest) col[id] = 0;
  for (let round = 0; round < rest.length; round++) {
    let moved = false;
    for (const e of drawn) {
      if (comp[e.from] === comp[e.to] || foundation.has(e.from)) continue;
      if (col[e.from] < col[e.to] + 1) { col[e.from] = col[e.to] + 1; moved = true; }
    }
    if (!moved) break;
  }
  for (const c of cycles) { const m = Math.max(...c.filter((id) => col[id] !== undefined).map((id) => col[id])); c.forEach((id) => { if (col[id] !== undefined) col[id] = m; }); }
  const columns = [];
  for (const id of rest) (columns[col[id]] ||= []).push(id);
  // Order: alphabetical first, then sweeps by the mean row of neighbours.
  const name = (id) => (nodes.find((n) => n.project_id === id) || {}).project || id;
  columns.forEach((c) => c.sort((a, b) => name(a).localeCompare(name(b))));
  const row = {};
  const place = () => columns.forEach((c) => c.forEach((id, i) => { row[id] = i / Math.max(1, c.length - 1); }));
  place();
  for (let sweep = 0; sweep < 6; sweep++) {
    for (const c of columns) {
      const key = {};
      for (const id of c) {
        const nb = [...uses[id].map((e) => e.to), ...usedBy[id].map((e) => e.from)].filter((x) => row[x] !== undefined && x !== id);
        key[id] = nb.length ? nb.reduce((s, x) => s + row[x], 0) / nb.length : row[id];
      }
      c.sort((a, b) => key[a] - key[b]);
    }
    place();
  }
  // Members of one cycle sit next to each other.
  for (const c of columns) {
    const groups = [];
    for (const id of c) {
      const g = groups.find((x) => comp[x[0]] === comp[id] && cycles.some((cy) => cy.includes(id)));
      g ? g.push(id) : groups.push([id]);
    }
    c.splice(0, c.length, ...groups.flat());
  }
  place();
  return { nodes, edges, drawn, foundation, library, cycles, comp, columns, uses, usedBy, name };
}

// dependencyMap draws the model with focus (click a member), a filter and
// focus panel; foundations and cycles are called out above it.
function dependencyMap(g, w) {
  const m = depModel(g);
  if (!m.nodes.length) return h("p", { class: "empty" }, "No member could be read.");
  if (!m.edges.length) return h("p", { class: "empty" }, "No member references another member's packages.");
  const label = (id) => { const p = state.projects.find((x) => x.id === id); return p ? pname(p) : m.name(id); };
  const st = { focus: "", indirect: false, filter: "" };
  const box = h("div", {});
  const side = h("div", { class: "depside" });
  const filterIn = h("input", { type: "search", placeholder: "Filter projects", "aria-label": "Filter projects", oninput: (ev) => { st.filter = ev.target.value.trim().toLowerCase(); draw(); } });
  const indirect = h("label", { class: "check small" }, h("input", { type: "checkbox", onchange: (ev) => { st.indirect = ev.target.checked; draw(); } }), "include indirect");

  // reach: what a member uses / is used by, directly or transitively.
  function reach(id, dir) {
    const seen = new Set(), todo = [id];
    while (todo.length) {
      const x = todo.pop();
      for (const e of dir === "uses" ? m.uses[x] : m.usedBy[x]) {
        const y = dir === "uses" ? e.to : e.from;
        if (!seen.has(y) && y !== id) { seen.add(y); if (st.indirect) todo.push(y); }
      }
    }
    return seen;
  }
  const focus = (id) => { st.focus = st.focus === id ? "" : id; draw(); };

  function sideFor(id) {
    if (!id) return [h("p", { class: "hint" }, "Click a project to see what it uses and what uses it."),
      m.cycles.length > 0 && h("p", { class: "hint" }, "Projects with a dashed red border depend on each other in a cycle.")];
    const usesSet = reach(id, "uses"), byS = reach(id, "usedBy");
    const direct = (a, b) => m.edges.find((e) => e.from === a && e.to === b);
    const item = (x, a, b) => { const e = direct(a, b); return h("li", {}, h("a", { href: "#", onclick: (ev) => { ev.preventDefault(); focus(x); } }, label(x)),
      e ? [h("div", { class: "muted small pkgs" }, listCut(e.packages, 2)), via(e)] : h("span", { class: "muted small" }, " (indirect)")); };
    // the projects (manifests) of the using member that name the packages
    const via = (e) => {
      let ps = [...new Set((e.via || []).map((r) => r.path.split("/").pop().replace(/\.(csproj|json|mod)$/, "")))].sort();
      const parts = ps.map((x) => x.split("."));
      let k = 0; // drop the prefix the names share (Betrian.TransportPlanner.)
      while (ps.length > 1 && parts.every((q) => q.length > k + 1 && q[k] === parts[0][k])) k++;
      ps = parts.map((q) => q.slice(k).join("."));
      return ps.length > 0 && h("div", { class: "muted small via", title: (e.via || []).map((r) => `${r.path}:${r.line} ${r.matched}`).join("\n") }, "in ", listCut(ps, 3));
    };
    const cycle = m.cycles.find((c) => c.includes(id));
    const partners = cycle ? cycle.filter((x) => x !== id).map(label).sort() : [];
    return [h("div", { class: "dside-head" }, h("b", { class: "dname" }, label(id)),
        h("button", { class: "ghost xclose", title: "Stop highlighting it (Esc)", "aria-label": "Stop highlighting it", onclick: () => focus(id) }, "✕")),
      h("a", { href: projectHref(id), class: "small dopen" }, "Open project →"),
      cycle && h("p", { class: "dside-cycle" }, levelPill("warn"),
        ` In a cycle: ${label(id)} uses ${partners.join(", ")} and ${partners.length > 1 ? "they use" : "it uses"} ${label(id)}, so none of them can change or ship alone.`),
      h("p", { class: "dside-k" }, h("b", {}, `Uses ${usesSet.size}`), h("span", { class: "muted" }, ` · their packages it references${st.indirect ? ", directly or through others" : ""}`)),
      h("ul", { class: "deps uses" }, [...usesSet].sort((a, b) => label(a).localeCompare(label(b))).map((x) => item(x, id, x))),
      h("p", { class: "dside-k" }, h("b", {}, `Used by ${byS.size}`), h("span", { class: "muted" }, " · projects referencing its packages")),
      h("ul", { class: "deps usedby" }, [...byS].sort((a, b) => label(a).localeCompare(label(b))).map((x) => item(x, x, id)))];
  }

  function drawMap() {
    // Layers run left to right (providers left) or top to bottom (providers
    // on top), whichever is narrower: deep graphs read best top-down.
    const NW = 172, NH = 46, PY = 14;
    const layers = m.columns.length;
    // Two axes: down is depth (users above what they use), right is how
    // widely a member is used (its users, direct): foundations end up
    // bottom right, applications top left. A layer's members sharing a
    // column sit side by side in it.
    const tb = true;
    const GA = 16, GL = 58;
    const layerOf = {};
    m.columns.forEach((c, l) => c.forEach((id) => { layerOf[id] = l; }));
    const ids = m.columns.flat();
    // the two axes suit a small workspace; a large one (over 12 members)
    // keeps compact rows, as wide as its widest layer
    const twoAxis = ids.length <= 12;
    const fanIn = (id) => (twoAxis ? m.usedBy[id].length : 0);
    const buckets = [...new Set(ids.map(fanIn))].sort((a, b) => a - b);
    const colIx = (id) => buckets.indexOf(fanIn(id));
    const cell = {}; // "layer:col" -> members in it
    for (const id of ids) (cell[layerOf[id] + ":" + colIx(id)] = cell[layerOf[id] + ":" + colIx(id)] || []).push(id);
    const colW = buckets.map((_, c) => Math.max(1, ...Object.entries(cell).filter(([k]) => k.endsWith(":" + c)).map(([, v]) => v.length)) * (NW + GA));
    const colX = colW.map((_, c) => colW.slice(0, c).reduce((x, w) => x + w, 0));
    // lines that would pass behind a member directly below get a side lane
    const xOf = {};
    for (const [k, v] of Object.entries(cell)) v.forEach((id, i) => { xOf[id] = colX[+k.split(":")[1]] + i * (NW + GA); });
    if (!twoAxis) {
      const full = colW[0];
      for (const [k, v] of Object.entries(cell)) v.forEach((id, i) => { xOf[id] = (full - (v.length * (NW + GA))) / 2 + i * (NW + GA); });
    }
    const skips = !twoAxis ? [] : [...m.drawn].filter((e) => xOf[e.from] !== undefined && xOf[e.to] !== undefined && xOf[e.from] === xOf[e.to] && Math.abs(layerOf[e.from] - layerOf[e.to]) > 1)
      .sort((a, b) => Math.abs(layerOf[b.from] - layerOf[b.to]) - Math.abs(layerOf[a.from] - layerOf[a.to]));
    const lane = new Map(skips.map((e, i) => [e, { side: i % 2 ? -1 : 1, n: Math.floor(i / 2) }]));
    // other lines skipping a layer go as elbows through the free space above
    // and right of the diagonal: across from the user, then down into the
    // used member; lines into one member are spaced apart
    const elbow = new Map();
    const into = {};
    for (const e of m.drawn) {
      if (!twoAxis || lane.has(e) || xOf[e.from] === undefined || xOf[e.to] === undefined || Math.abs(layerOf[e.from] - layerOf[e.to]) < 2) continue;
      (into[e.to] = into[e.to] || []).push(e);
    }
    for (const es of Object.values(into)) es.sort((a, b) => xOf[a.from] - xOf[b.from]).forEach((e, k) => elbow.set(e, { k, n: es.length }));
    // a member three or more others use gets a bus: one trunk rising from it
    // that each user joins at its own height (their shared stretch draws as
    // one line, one arrow enters it)
    const busInto = new Set();
    if (twoAxis) {
      const count = {};
      for (const e of m.drawn) if (layerOf[e.from] > layerOf[e.to]) count[e.to] = (count[e.to] || 0) + 1;
      for (const [id, n] of Object.entries(count)) if (n >= 3) busInto.add(id);
    }
    const LANE = 16, sideRoom = skips.length ? 26 + Math.ceil(skips.length / 2) * LANE : 0;
    const PX = 14 + sideRoom;
    const pos = {};
    for (const id of ids) pos[id] = { x: PX + xOf[id], y: PY + (layers - 1 - layerOf[id]) * (NH + GL) };
    // a library on a bus is a rail: a column of its own on the right,
    // centred on its users, which tap it from the side (it is a foundation,
    // not a step in the depth)
    // Its users meet just right of the grid and run in together as a
    // cable: each line keeps its own track across the cable's width, the
    // top user's at the top, and the cable has one arrowhead.
    const RAIL = 84, CABLE = 5;
    const rail = new Map();
    const railIds = [...busInto].filter((id) => m.library.has(id));
    const gridRight = Math.max(PX, ...ids.filter((id) => !railIds.includes(id)).map((id) => pos[id].x + NW));
    railIds.forEach((id, i) => {
      const from = m.drawn.filter((e) => e.to === id && pos[e.from]).map((e) => e.from).sort((p, q) => pos[p].y - pos[q].y || xOf[p] - xOf[q]);
      const ys = from.map((u) => pos[u].y + NH / 2);
      const mid = (ys[0] + ys[ys.length - 1]) / 2;
      const x = gridRight + RAIL + i * (NW + RAIL);
      pos[id] = { x, y: mid - NH / 2 };
      const track = new Map(from.map((u, k) => [u, from.length > 1 ? (k / (from.length - 1) - 0.5) * (CABLE - 1.5) : 0]));
      rail.set(id, { jx: x - RAIL + 18, track, n: from.length });
    });
    const width = Math.max(...Object.values(pos).map((p) => p.x)) + NW + PX;
    const height = Math.max(...Object.values(pos).map((p) => p.y)) + NH + PY;
    const f = st.focus;
    const usesS = f ? reach(f, "uses") : new Set(), byS = f ? reach(f, "usedBy") : new Set();
    const match = (id) => !st.filter || label(id).toLowerCase().includes(st.filter);
    const lit = (id) => (!f || id === f || usesS.has(id) || byS.has(id)) && match(id);
    const edgeClass = (e) => {
      if (f) {
        if ((e.from === f || usesS.has(e.from)) && usesS.has(e.to) && (e.from === f || st.indirect)) return "on uses";
        if ((e.to === f || byS.has(e.to)) && byS.has(e.from) && (e.to === f || st.indirect)) return "on usedby";
        return "dim";
      }
      return st.filter && !(match(e.from) || match(e.to)) ? "dim" : "";
    };
    const path = (e) => {
      const a = pos[e.from], b = pos[e.to];
      const sameLayer = tb ? a.y === b.y : a.x === b.x;
      const l = lane.get(e), el = elbow.get(e);
      if (rail.has(e.to)) {
        // across to the junction, up or down to its own track, into the
        // cable
        const r = rail.get(e.to), bx = r.jx, ay = a.y + NH / 2, ly = b.y + NH / 2 + r.track.get(e.from), d = ly > ay ? 10 : -10;
        if (Math.abs(ly - ay) < 1) return `M ${a.x + NW} ${ay} L ${bx + 16} ${ly}`;
        return `M ${a.x + NW} ${ay} L ${bx - 10} ${ay} Q ${bx} ${ay} ${bx} ${ay + d} L ${bx} ${ly - d} Q ${bx} ${ly} ${bx + 10} ${ly} L ${bx + 16} ${ly}`;
      }
      if (busInto.has(e.to) && a.y < b.y) {
        const bx = b.x + NW * 0.72, y = a.y + NH / 2;
        if (bx > a.x + NW) return `M ${a.x + NW} ${y} L ${bx - 10} ${y} Q ${bx} ${y} ${bx} ${y + 10} L ${bx} ${b.y}`;
        if (bx < a.x) return `M ${a.x} ${y} L ${bx + 10} ${y} Q ${bx} ${y} ${bx} ${y + 10} L ${bx} ${b.y}`;
        return `M ${bx} ${a.y + NH} L ${bx} ${b.y}`;
      }
      if (el && a.y < b.y) {
        // the space right of the staircase belongs to the rails; elbows go
        // down from the user, then across into the used member's side
        const off = (el.k - (el.n - 1) / 2) * 10;
        const x = a.x + NW / 2 + off, y = b.y + NH / 2 + (el.k - (el.n - 1) / 2) * 5;
        if (x < b.x - 12) return `M ${x} ${a.y + NH} L ${x} ${y - 10} Q ${x} ${y} ${x + 10} ${y} L ${b.x} ${y}`;
        return `M ${x} ${a.y + NH} C ${x} ${a.y + NH + GL / 2}, ${b.x + NW / 2} ${b.y - GL / 2}, ${b.x + NW / 2} ${b.y}`;
      }
      if (tb && l) {
        // around the side: out of the user's side, down a lane, into the
        // used member's side
        const left = Math.min(...Object.values(pos).map((p) => p.x)), right = Math.max(...Object.values(pos).map((p) => p.x)) + NW;
        const x = l.side > 0 ? right + 22 + l.n * LANE : left - 22 - l.n * LANE;
        const ax = l.side > 0 ? a.x + NW : a.x, bx = l.side > 0 ? b.x + NW : b.x;
        const ay = a.y + NH / 2 + (l.n % 2 ? 6 : -6), by = b.y + NH / 2 + (l.n % 2 ? 6 : -6);
        const r = 10 * l.side, dir = by > ay ? 1 : -1;
        return `M ${ax} ${ay} L ${x - r} ${ay} Q ${x} ${ay} ${x} ${ay + dir * 10} L ${x} ${by - dir * 10} Q ${x} ${by} ${x - r} ${by} L ${bx} ${by}`;
      }
      if (tb) {
        if (sameLayer) { const y = a.y + NH; return `M ${a.x + NW / 2} ${y} C ${a.x + NW / 2} ${y + 34}, ${b.x + NW / 2} ${y + 34}, ${b.x + NW / 2} ${y}`; }
        const down = b.y > a.y;
        const x1 = a.x + NW / 2, y1 = down ? a.y + NH : a.y, x2 = b.x + NW / 2, y2 = down ? b.y : b.y + NH, d = down ? GL / 2 : -GL / 2;
        return `M ${x1} ${y1} C ${x1} ${y1 + d}, ${x2} ${y2 - d}, ${x2} ${y2}`;
      }
      if (sameLayer) { const x = a.x; return `M ${x} ${a.y + NH / 2} C ${x - 34} ${a.y + NH / 2}, ${x - 34} ${b.y + NH / 2}, ${x} ${b.y + NH / 2}`; }
      const x1 = a.x, y1 = a.y + NH / 2, x2 = b.x + NW, y2 = b.y + NH / 2;
      return `M ${x1} ${y1} C ${x1 - GL / 2} ${y1}, ${x2 + GL / 2} ${y2}, ${x2} ${y2}`;
    };
    const svg = sv("svg", { class: "depmap", viewBox: `0 0 ${width} ${height}`, role: "img", "aria-label": `Dependencies between the projects of ${wname(w)}` },
      sv("defs", {}, ["", "uses", "usedby"].map((k) => sv("marker", { id: `dep-arrow${k}`, viewBox: "0 0 10 10", refX: "9", refY: "5", markerWidth: "7", markerHeight: "7", orient: "auto-start-reverse" },
        sv("path", { d: "M 0 0 L 10 5 L 0 10 z", class: `dep-arrowhead ${k}` })))),
      // dimmed edges first, lit ones on top
      [...m.drawn].filter((e) => pos[e.from] && pos[e.to]).sort((x, y) => (edgeClass(x).startsWith("on") ? 1 : 0) - (edgeClass(y).startsWith("on") ? 1 : 0)).map((e) => {
        const cls = edgeClass(e);
        const kind = cls.includes("usedby") ? "usedby" : cls.includes("uses") ? "uses" : "";
        return sv("path", { d: path(e), class: `dep-edge2 ${cls}`, "marker-end": rail.has(e.to) ? null : `url(#dep-arrow${kind})` },
          sv("title", {}, `${label(e.from)} uses ${label(e.to)}: ${e.packages.join(", ")}`));
      }),
      // bus trunks over the edges, only where two or more lines run the
      // same way (from where the turns have joined to the arrowhead), lit
      // as the edges they carry
      [...busInto].filter((id) => pos[id]).map((id) => {
        const b = pos[id];
        const cs = m.drawn.filter((e) => e.to === id && pos[e.from]).map(edgeClass);
        const on = cs.find((c) => c.startsWith("on"));
        const cls = `dep-trunk ${on || (cs.every((c) => c === "dim") ? "dim" : "")}`;
        if (rail.has(id)) {
          // the cable (its width fixed, the tracks spread across it), then one arrowhead
          const { jx } = rail.get(id), ly = b.y + NH / 2, w = CABLE, hx = b.x - 9;
          return sv("g", { class: `dep-cable ${cls.replace("dep-trunk", "")}` },
            sv("path", { class: "dep-cable-body", d: `M ${jx + 14} ${ly} L ${hx} ${ly}`, "stroke-width": w }),
            sv("path", { class: "dep-cable-head", d: `M ${hx} ${ly - w / 2 - 4} L ${b.x} ${ly} L ${hx} ${ly + w / 2 + 4} Z` }));
        }
        const ys = m.drawn.filter((e) => e.to === id && pos[e.from] && pos[e.from].y < b.y).map((e) => pos[e.from].y + NH / 2).sort((p, q) => p - q);
        const bx = b.x + NW * 0.72;
        return ys.length >= 2 ? sv("path", { class: cls, d: `M ${bx} ${ys[1] + 10} L ${bx} ${b.y - 8}` }) : null;
      }),
      m.columns.flat().map((id) => {
        const p = pos[id];
        const inCycle = m.cycles.some((c) => c.includes(id));
        const cls = ["dep-node2", id === f && "focus", usesS.has(id) && "uses", byS.has(id) && "usedby", !lit(id) && "dim", inCycle && "cycle", m.library.has(id) && "lib"].filter(Boolean).join(" ");
        const n = label(id);
        const sub = (m.library.has(id) ? "library · " : "") + [m.uses[id].length && `uses ${m.uses[id].length}`, m.usedBy[id].length && `used by ${m.usedBy[id].length}`].filter(Boolean).join(" · ");
        return sv("g", { class: cls, tabindex: "0", role: "button", "data-id": id, "aria-label": `${n}: uses ${m.uses[id].length}, used by ${m.usedBy[id].length}`,
          onclick: () => focus(id), onkeydown: (ev) => { if (ev.key === "Enter" || ev.key === " ") { ev.preventDefault(); focus(id); } } },
        sv("title", {}, `${n}: uses ${m.uses[id].length}, used by ${m.usedBy[id].length}`),
        sv("rect", { x: p.x, y: p.y, width: NW, height: NH, rx: 8 }),
        sv("text", { x: p.x + 10, y: p.y + 19 }, n.length > 21 ? n.slice(0, 20) + "…" : n),
        sv("text", { x: p.x + 10, y: p.y + 36, class: "deg" }, sub || "uses none here"));
      }));
    svg.style.setProperty("--w", `${width}px`);
    const wrap = h("div", { class: "depwrap" }, svg);
    if (f) setTimeout(() => wrap.querySelector(`[data-id="${CSS.escape(f)}"]`)?.scrollIntoView({ block: "nearest", inline: "nearest" }), 0);
    return wrap;
  }

  function draw() {
    fill(box, drawMap());
    fill(side, sideFor(st.focus));
  }
  const found = [...m.foundation].sort((a, b) => m.usedBy[b].length - m.usedBy[a].length);
  const head = h("div", { class: "dephead" },
    h("div", { class: "deptools" }, filterIn, indirect),
    found.length > 0 && h("div", { class: "depinfo" }, h("span", { class: "dk" }, "Foundations"),
      h("div", { class: "dv" }, found.map((id) => h("a", { class: "fchip", href: "#", onclick: (ev) => { ev.preventDefault(); focus(id); } }, label(id), h("span", { class: "count" }, `used by ${m.usedBy[id].length}`))),
        h("span", { class: "hint" }, "used by most members, so their arrows are left out of the map"))),
    m.cycles.length > 0 && h("div", { class: "depinfo" }, h("span", { class: "dk" }, "Cycles"),
      h("div", { class: "dv" }, m.cycles.map((c) => h("a", { class: "cchip", href: "#", onclick: (ev) => { ev.preventDefault(); focus(c[0]); } }, "▲ ", c.map(label).sort().join(" ⇄ "))),
        h("span", { class: "hint" }, "projects that use each other; click to see how"))));
  draw();
  const card = h("div", { class: "card pad depcard", onkeydown: (ev) => { if (ev.key === "Escape" && st.focus) { st.focus = ""; draw(); } } }, head, h("div", { class: "deplayout" }, box, side));
  return card;
}

// --- issues: design smells between the members ---
// A list of one-line issues grouped by kind (the kind's explanation once
// in its header); the selected one's details on the right.
const ISSUE_KINDS = [
  ["cycle", "Cycles", "Projects that use each other's packages can't be changed or released on their own."],
  ["local-copy", "Local copies", "Projects keeping their own copy of something a project they already use provides: the copies drift."],
  ["duplicate", "Duplicates", "The same code in several projects, to be fixed and changed in each."],
  ["version-drift", "Version drift", "The same package at different versions in different projects."],
  ["hub", "Hubs", "Services many others depend on directly: their changes ripple."],
];
const ISSUE_TODO = {
  "cycle": "Find the dependency that points the wrong way and move what both need into a shared library, or turn the call into an event or an API call.",
  "local-copy": "Replace the copy with the provided type (or derive from it, if the project needs to adjust it), then delete the copy.",
  "duplicate": "If it is one concern, move it into a library the projects share; if the copies are meant to differ, give them distinct names.",
  "version-drift": "Align the versions (a central package version file helps); start with different major versions.",
  "hub": "Decide whether it is a shared library (then version and publish it as one) or a service (then talk to it through an API, not its packages).",
};
async function issuesView(w) {
  const [r, f] = await Promise.all([api(`/api/workspaces/${enc(w.name)}/issues`), api(`/api/workspaces/${enc(w.name)}/findings`).catch(() => null)]);
  const rollup = f && section("Findings", "Open architecture findings per member and between them, as each one's last sync recorded them; open a row for the detail and the evidence.",
    findingsRollup(w, f.rows || []));
  // copies are findings now (shared code, with homes and triage)
  const is = (r.issues || []).filter((x) => x.kind !== "local-copy" && x.kind !== "duplicate");
  if (!is.length) return h("div", {}, rollup, section("Design issues", null, h("p", { class: "okay" }, "✓ No design issues found between these projects.")));
  const key = "issues." + w.name;
  const hidden = new Set(store.get(key, "").split(",").filter(Boolean));
  let sel = is[0];
  const list = h("div", { class: "ilist", role: "listbox", "aria-label": "Design issues" });
  const detail = h("div", { class: "idetail" });
  const chips = h("div", { class: "dupfilter" });
  const n = (lv) => is.filter((x) => x.level === lv).length;
  function draw() {
    fill(chips, ISSUE_KINDS.filter(([k]) => is.some((x) => x.kind === k)).map(([k, label]) => h("button", {
      class: "chip", "aria-pressed": String(!hidden.has(k)),
      onclick: () => { hidden.has(k) ? hidden.delete(k) : hidden.add(k); store.set(key, [...hidden].join(",")); draw(); },
    }, label, h("span", { class: "count" }, is.filter((x) => x.kind === k).length))));
    const shown = is.filter((x) => !hidden.has(x.kind));
    if (!shown.includes(sel)) sel = shown[0];
    fill(list, ISSUE_KINDS.map(([k, label, why]) => {
      const xs = shown.filter((x) => x.kind === k);
      return xs.length > 0 && h("div", { class: "igroup" },
        h("div", { class: "ihead" }, h("b", {}, label), h("span", { class: "count" }, xs.length), h("div", { class: "hint" }, why)),
        xs.map((x) => h("button", {
          class: "irow" + (x === sel ? " on" : ""), role: "option", "aria-selected": String(x === sel),
          onclick: () => { sel = x; draw(); },
        }, h("span", { class: `glyph g-${x.level === "problem" ? "bad" : x.level === "warn" ? "warn" : "stale"}`, "aria-label": x.level }, x.level === "problem" ? "✕" : x.level === "warn" ? "▲" : "○"),
        h("span", { class: "isub" }, x.subject || x.title), h("span", { class: "isum" }, x.summary || ""))));
    }));
    fill(detail, sel ? issueDetail(sel, wsSnippet(w)) : h("p", { class: "empty" }, "Nothing selected."));
  }
  draw();
  return h("div", {}, rollup,
    section("Design issues", "Smells that only show across repositories, read from the members' manifests and code. Evidence, not a verdict.",
      h("div", { class: "tiles" },
        tile("Problems", n("problem"), "act on these first"), tile("Warnings", n("warn"), "worth fixing"), tile("Notes", n("info"), "worth a decision")),
      h("div", { class: "deptools" }, chips),
      h("div", { class: "ilayout" }, list, detail)));
}

function issueDetail(x, snip) {
  const proj = (name) => { const p = state.projects.find((q) => pname(q) === name || q.name === name || baseName(q.root) === name); return p ? h("a", { class: "pchip", href: projectHref(p.id) }, stateGlyph(p.live), pname(p)) : h("span", { class: "pchip" }, name); };
  return h("div", { class: "card pad" },
    h("div", { class: "fhead" }, levelPill(x.level), h("span", { class: "farea" }, (ISSUE_KINDS.find(([k]) => k === x.kind) || [, x.kind])[1])),
    h("h3", { class: "ititle" }, x.title),
    h("h4", {}, "Why it matters"), h("p", {}, x.why),
    ISSUE_TODO[x.kind] && [h("h4", {}, "What to do"), h("p", {}, ISSUE_TODO[x.kind])],
    (x.evidence || []).length > 0 && [h("h4", {}, "Where"), h("div", { class: "evid" }, x.evidence.map((e) => h("div", { class: "where2" },
      h("div", {}, h("b", {}, e.project), e.note && h("span", { class: "muted small" }, ` · ${e.note}`)),
      e.path && (snip ? h("ul", { class: "evlist" }, evidenceItem(`${e.project}: ${e.path}${e.line ? ":" + e.line : ""}`, snip, 0))
        : h("div", { class: "mono small muted path" }, `${e.path}${e.line ? ":" + e.line : ""}`)))))],
    (x.projects || []).length > 0 && [h("h4", {}, "Projects"), h("div", { class: "pchips" }, x.projects.map(proj))]);
}

// --- shared code: the same code in several members ---
const DUP_VERDICTS = [["identical", "Identical"], ["near copy", "Near copies"], ["similar", "Similar"], ["same name only", "Same name only"], ["extends", "Extends a shared one"]];
async function sharedCodeView(w) {
  const d = await api(`/api/workspaces/${enc(w.name)}/duplicates`);
  const groups = d.groups || [];
  // the copies as findings: a suggested home each, triaged like the others
  const members = (state.workspaces || []).find((x) => x.name === w.name)?.members || [];
  const memberName = (id) => { const m = members.find((x) => x.project_id === id); return m ? m.title || m.project : id; };
  const copies = smellsSection(d.findings || [], "Code repeated in several members (identical, near copies, similar), each with where it could live once: a library that already holds a copy, else one they all use, else a new shared library.",
    "✓ No code is copied between members (same names only, if anything: see below).",
    { snapshot: d.snapshot, members: members.map((m) => ({ id: m.project_id, name: m.title || m.project })), memberName,
      post: (action, body) => post(`/api/workspaces/${enc(w.name)}/findings/${action}`, body), snippet: wsSnippet(w) });
  const key = "dup." + w.name;
  let show = new Set((store.get(key, "identical,near copy,similar")).split(",").filter(Boolean));
  let q = "";
  const list = h("div", {});
  const n = (v) => groups.filter((g) => g.verdict === v).length;
  const chips = h("div", { class: "dupfilter" });
  function draw() {
    fill(chips, DUP_VERDICTS.map(([v, label]) => h("button", {
      class: `chip dv-${v.replace(/ /g, "-")}`, "aria-pressed": String(show.has(v)),
      onclick: () => { show.has(v) ? show.delete(v) : show.add(v); store.set(key, [...show].join(",")); draw(); },
    }, label, h("span", { class: "count" }, n(v)))));
    const shown = groups.filter((g) => show.has(g.verdict) && (!q || g.name.toLowerCase().includes(q) || g.copies.some((c) => c.project.toLowerCase().includes(q))));
    fill(list, shown.length ? h("div", { class: "card" }, h("table", { class: "dups" },
      h("thead", {}, h("tr", {}, h("th", {}, "code"), h("th", {}, "how alike"), h("th", { class: "n" }, "projects"), h("th", {}, "where"), h("th", {}, "could live in"))),
      h("tbody", {}, shown.slice(0, 150).map((g) => h("tr", {},
        h("td", {}, h("code", {}, g.name), " ", h("span", { class: "muted small" }, g.kind),
          (g.names || []).length > 0 && h("div", { class: "muted small" }, g.names.join(", "))),
        h("td", {}, h("span", { class: `dupv dv-${g.verdict.replace(/ /g, "-")}` }, g.verdict), g.verdict !== "identical" && h("span", { class: "muted small" }, ` ${Math.round(g.similarity * 100)}%`)),
        h("td", { class: "n" }, g.copies.length),
        h("td", { class: "wrap" }, whereList(g.copies, wsSnippet(w))),
        h("td", { class: "small wrap" }, g.home ? [h("b", {}, g.home.member || "a new shared library"), h("div", { class: "muted" }, g.home.reason),
          (g.home.knowledge || []).map((k) => h("div", { class: "muted kn" }, "↳ ", k))] : h("span", { class: "muted" }, "–")))))))
      : h("p", { class: "empty" }, groups.length ? "Nothing matches the filter." : "No code appears in more than one project."));
  }
  const filter = h("input", { type: "search", placeholder: "Filter by name or project", "aria-label": "Filter shared code", oninput: (ev) => { q = ev.target.value.trim().toLowerCase(); draw(); } });
  draw();
  return h("div", {}, copies,
    section("All code by name", "The same code in several projects, whether or not a library is involved: types declared in two or more projects, compared by their code, and files with the same content. " +
      "How alike: the share of code lines the copies have in common (comments, imports and namespaces ignored).",
    h("div", { class: "deptools" }, chips, filter),
    list,
    (d.notes || []).filter((x) => !x.startsWith("similarity compares")).map((x) => h("p", { class: "hint" }, x))));
}

// whereList shows copies one per line, the project first; more than
// three fold behind "+N more".
function whereList(copies, snip) {
  const row = (c) => h("div", { class: "where" }, h("b", {}, c.project), " ",
    snip ? h("ul", { class: "evlist inline" }, evidenceItem(`${c.project}: ${c.path}${c.line ? ":" + c.line : ""}`, snip, 0))
      : h("span", { class: "mono small muted" }, `${c.path}${c.line ? ":" + c.line : ""}`),
    c.extends && h("span", { class: "muted small" }, " · extends it"));
  if (copies.length <= 3) return copies.map(row);
  return [copies.slice(0, 3).map(row), h("details", { class: "cut" }, h("summary", {}, `+${copies.length - 3} more`), copies.slice(3).map(row))];
}

// --- libraries: who uses a member's packages ---
async function sharedCode(w) {
  const g = await api(`/api/workspaces/${enc(w.name)}/dependencies`);
  const usedBy = (id) => (g.edges || []).filter((e) => e.to === id);
  const libs = (g.nodes || []).filter((n) => n.packages.length && usedBy(n.project_id).length)
    .sort((a, b) => usedBy(b.project_id).length - usedBy(a.project_id).length || a.project.localeCompare(b.project));
  if (!libs.length) return h("p", { class: "empty" }, "No project here uses another project's packages.");
  const key = "lib." + w.name;
  let lib = store.get(key, "");
  if (!libs.some((n) => n.project === lib)) lib = libs[0].project;
  const total = (g.nodes || []).length;
  const label = (n) => { const p = state.projects.find((x) => x.id === n.project_id); return p ? pname(p) : n.project; };
  const pick = h("nav", { class: "liblist", "aria-label": "Libraries" });
  const detail = h("div", { class: "libdetail" });
  function drawPick() {
    fill(pick, h("div", { class: "k" }, "Libraries"), libs.map((n) => h("a", {
      href: "#", "aria-current": n.project === lib ? "page" : null,
      onclick: (ev) => { ev.preventDefault(); lib = n.project; store.set(key, lib); drawPick(); load(); },
    }, h("span", { class: "nname" }, label(n)), h("span", { class: "count" }, `used by ${usedBy(n.project_id).length}`))));
  }
  async function load() {
    fill(detail, h("p", { class: "empty" }, "Reading every member's files…"));
    const node = libs.find((n) => n.project === lib);
    try { fill(detail, libraryView(await api(`/api/workspaces/${enc(w.name)}/adoption?library=${enc(lib)}`), node, label(node), total, w)); }
    catch (e) { fill(detail, h("p", { class: "error" }, e.message)); }
  }
  drawPick();
  load();
  return h("div", {},
    h("p", { class: "hint" }, "Projects whose packages other projects use, and how much of each is used: read from the manifests and the code's imports."),
    h("div", { class: "libs" }, pick, detail));
}

// libraryView: which of a library's packages are used, and by whom.
function libraryView(r, node, name, total, w) {
  const consumers = r.consumers || [];
  const pkgUsers = {};
  for (const p of r.packages) pkgUsers[p] = new Set();
  for (const c of consumers) for (const ref of c.packages || []) (pkgUsers[ref.matched] ||= new Set()).add(c.project);
  const twins = {};
  for (const t of r.possible_local_implementations || []) (twins[t.project] ||= []).push(t.declared.name);
  const others = total - 1;
  const usedPkgs = r.packages.filter((p) => pkgUsers[p].size);
  const pkgRows = [...r.packages].sort((a, b) => pkgUsers[b].size - pkgUsers[a].size || a.localeCompare(b));
  const bar = (n, of) => { const f = h("div", { class: "fill" + (n ? "" : " zero") }); f.style.setProperty("--w", `${of ? (100 * n) / of : 0}%`); return h("div", { class: "meter" }, h("div", { class: "track" }, f), h("span", { class: "num" }, `${n} of ${of}`)); };
  const rows = [...consumers].sort((a, b) => (b.packages || []).length - (a.packages || []).length || b.import_files - a.import_files);
  return h("div", {},
    h("h3", { class: "libtitle" }, name),
    h("p", { class: "libsum" }, `Offers ${r.packages.length} package(s); ${consumers.length} of ${others} other project(s) use it` +
      (usedPkgs.length < r.packages.length ? `, ${r.packages.length - usedPkgs.length} package(s) nobody here uses.` : ".")),
    section("Its packages", "How many projects reference each package in their manifests.",
      h("div", { class: "card" }, h("table", { class: "libpk" },
        h("thead", {}, h("tr", {}, h("th", {}, "package"), h("th", {}, "used by"), h("th", {}, "projects"))),
        h("tbody", {}, pkgRows.map((p) => h("tr", {},
          h("td", { class: "mono small" }, p),
          h("td", {}, bar(pkgUsers[p].size, others)),
          h("td", { class: "wrap small" }, pkgUsers[p].size ? listCut([...pkgUsers[p]].sort(), 4) : h("span", { class: "muted" }, "nobody")))))))),
    section("Who uses it", "Per project: which of its packages it references and how many of its code files import it.",
      h("div", { class: "card" }, h("table", { class: "libuse" },
        h("thead", {}, h("tr", {}, h("th", {}, "project"), h("th", {}, "packages"), h("th", { class: "n" }, "files importing"), h("th", {}, "own copies of its types"))),
        h("tbody", {}, rows.map((c) => h("tr", {},
          h("td", {}, c.project),
          h("td", { class: "small wrap" }, h("b", {}, `${(c.packages || []).length ? new Set(c.packages.map((x) => x.matched)).size : 0} of ${r.packages.length}`), " ",
            h("span", { class: "muted" }, [...new Set((c.packages || []).map((x) => x.matched))].map((x) => x.replace(/^.*\./, "")).join(", "))),
          h("td", { class: "n" }, c.import_files),
          h("td", { class: "small" }, twins[c.project] ? h("a", { href: workspaceHref(w.name, "issues"), title: twins[c.project].join(", ") }, `▲ ${twins[c.project].length} — see Issues`) : h("span", { class: "muted" }, "none")))))))),
    (r.non_consumers || []).length > 0 && h("p", { class: "hint" }, `Not using it: ${r.non_consumers.join(", ")}.`));
}

// listCut shows the first n of a list and the rest behind "+N more".
function listCut(xs, n) {
  if (xs.length <= n) return xs.join(", ");
  return h("details", { class: "cut" }, h("summary", {}, `${xs.slice(0, n).join(", ")} +${xs.length - n} more`), xs.slice(n).join(", "));
}

// membersSummary is the workspace Overview's line about its projects:
// how they are and what waits; the table is in Members.
function membersSummary(w, members) {
  const count = {};
  for (const p of members) count[stateKind(p.live)] = (count[stateKind(p.live)] || 0) + 1;
  const kinds = ["bad", "warn", "stale", "ok"].filter((k) => count[k]);
  const strip = h("div", { class: "hstrip" }, kinds.map((k) => { const seg = h("span", { class: `seg-${k}` }); seg.style.setProperty("--w", `${(100 * count[k]) / members.length}%`); return seg; }));
  const waiting = members.filter((p) => waitingIn(p) > 0).sort((a, b) => waitingIn(b) - waitingIn(a));
  return h("div", { class: "card pad msum" }, strip,
    h("div", { class: "hcounts" }, kinds.map((k) => h("span", { class: `glyph g-${k}` }, STATE_GLYPH[k], h("span", { class: "gtext" }, ` ${count[k]} ${STATE_WORD[k]}`))),
      h("a", { href: workspaceHref(w.name, "members"), class: "small" }, `All ${members.length} projects →`)),
    waiting.length ? h("div", { class: "pchips" }, h("span", { class: "small muted" }, "Waiting for you:"),
      waiting.slice(0, 8).map((p) => h("a", { class: "pchip", href: projectHref(p.id) }, stateGlyph(p.live), pname(p), h("span", { class: "badge" }, waitingIn(p)))),
      waiting.length > 8 && h("a", { class: "pchip more", href: workspaceHref(w.name, "members") }, `+${waiting.length - 8} more`))
      : h("div", { class: "okay small" }, "✓ nothing waits for you in these projects"));
}

// --- home: the machine ---
async function renderHome(main) {
  const ps = state.projects;
  const wss = state.workspaces || [];
  const inWs = new Set(wss.flatMap((w) => w.members.map((m) => m.project_id)));
  const newBox = h("div", {});
  // the tiles and the workspaces' cards draw from the project list, again
  // when a project's summary changes
  const tiles = mount({ anyProject: true, kinds: ["summary", "proposals", "sync", "workspaces"], draw: () => {
    const ps = state.projects, wss = state.workspaces || [];
    const inWs = new Set(wss.flatMap((w) => w.members.map((m) => m.project_id)));
    const pending = ps.reduce((n, p) => n + (p.live.pending_proposals || 0), 0);
    const failing = ps.filter((p) => p.live.checks_failed > 0 || p.live.state !== "ok").length;
    return h("div", { class: "tiles section" },
      tile("Projects", ps.length, `${ps.filter((p) => p.live.state === "ok").length} readable`),
      tile("Workspaces", wss.length, `${inWs.size} project(s) in them`),
      tile("Need attention", failing, "failing doctor checks or unreadable state"),
      tile("Proposals to review", pending, pending ? "in each project's Knowledge › Inbox" : "nothing waiting"));
  } });
  const wsGrid = mount({ anyProject: true, kinds: ["summary", "proposals", "sync", "workspaces"],
    draw: () => h("div", { class: "dash" }, (state.workspaces || []).map(workspaceSummary)) });
  const grid = h("div", { class: "dash" });
  fill(main, 
    h("div", { class: "phead" }, h("div", { class: "title" }, h("h2", {}, "Home")),
      h("p", { class: "path" }, `Every repository on this machine that uses harness. Activity over the last ${state.days} days.`)),
    tiles,
    h("div", { class: "withside" },
      h("div", { class: "mainside" },
        h("section", { class: "section" },
          h("div", { class: "shead" },
            h("div", {}, h("h3", {}, "Workspaces"), h("p", { class: "hint" }, "Repositories searched and reasoned about together.")),
            h("button", { class: "primary", onclick: () => fill(newBox, createForm(newBox)) }, "New workspace")),
          newBox, wss.length ? wsGrid : h("p", { class: "empty" }, "No workspaces yet.")),
        (!wss.length || ps.some((p) => !inWs.has(p.id))) && section(wss.length ? "Projects outside workspaces" : "Projects", null, grid)),
      h("aside", { class: "sidecol" }, activitySection(""))));
  const outside = ps.filter((p) => !inWs.has(p.id));
  if (!outside.length) grid.append(h("p", { class: "empty" }, ps.length ? "Every project is in a workspace." : "No projects registered yet. 'harness init' in a repository registers it."));
  // each card draws from the project list and fills itself in
  for (const p of outside) {
    grid.append(mount({ project: p.id, kinds: ["summary", "tasks", "findings", "proposals", "sync"],
      draw: () => { const c = projectCard(state.projects.find((x) => x.id === p.id) || p); c.load(); return c.el; } }));
  }
}

// workspaceSummary is a workspace's card on Home: how its projects are
// (a strip and counts per state), what waits, and a few names.
function workspaceSummary(w) {
  const members = w.members.map((m) => state.projects.find((x) => x.id === m.project_id)).filter(Boolean);
  const waiting = members.filter((p) => waitingIn(p) > 0).sort((a, b) => waitingIn(b) - waitingIn(a));
  const count = {};
  for (const p of members) count[stateKind(p.live)] = (count[stateKind(p.live)] || 0) + 1;
  const kinds = ["bad", "warn", "stale", "ok"].filter((k) => count[k]);
  const strip = h("div", { class: "hstrip", role: "img", "aria-label": kinds.map((k) => `${count[k]} ${STATE_WORD[k]}`).join(", ") },
    kinds.map((k) => { const seg = h("span", { class: `seg-${k}` }); seg.style.setProperty("--w", `${(100 * count[k]) / members.length}%`); return seg; }));
  const go = (p, n) => h("a", { class: "pchip", href: projectHref(p.id), title: ((state.stacks || {})[p.id] || {}).badge || "", onclick: () => { state.lastWs = w.name; } }, stateGlyph(p.live), pname(p), (state.stacks || {})[p.id] && h("span", { class: "chipmark" }, langMark(state.stacks[p.id])), n > 0 && h("span", { class: "badge" }, n));
  const others = members.filter((p) => waitingIn(p) === 0).sort((a, b) => pname(a).localeCompare(pname(b)));
  const SHOW = 6;
  return h("article", { class: "pcard wscard" },
    h("a", { class: "ptitle", href: workspaceHref(w.name) }, h("span", {}, wname(w)), h("span", { class: "count" }, `${members.length} project(s)`)),
    strip,
    h("div", { class: "hcounts" }, kinds.map((k) => h("span", { class: `glyph g-${k}` }, STATE_GLYPH[k], h("span", { class: "gtext" }, ` ${count[k]} ${STATE_WORD[k]}`)))),
    waiting.length ? h("div", { class: "wswait" }, h("div", { class: "k" }, `${waiting.length} project(s) wait for you`),
      h("div", { class: "pchips" }, waiting.slice(0, 4).map((p) => go(p, waitingIn(p))),
        waiting.length > 4 && h("a", { class: "pchip more", href: workspaceHref(w.name, "members") }, `+${waiting.length - 4} more`)))
      : h("div", { class: "okay" }, "✓ nothing waiting"),
    h("div", { class: "pchips" }, others.slice(0, SHOW).map(go),
      others.length > SHOW && h("a", { class: "pchip more", href: workspaceHref(w.name) }, `+${others.length - SHOW} more`)));
}

// --- activity (7 days) ---
const ACTIVITY_ICON = { session: "◆", task: "✓", memory: "✎", sync: "↻" };
// activitySection loads what happened (across the machine, or in one
// project) and lists it newest first.
function activitySection(projectID) {
  const qs = new URLSearchParams({ days: "7", limit: projectID ? "15" : "30" });
  if (projectID) qs.set("project", projectID);
  const list = mount({ project: projectID || null, anyProject: !projectID, kinds: ["activity", "tasks", "knowledge", "sync"], load: async () => {
    const items = (await api(`/api/activity?${qs}`)).items || [];
    return items.length ? h("ol", { class: "activity" }, items.map((x) => activityRow(x, !projectID)))
      : h("p", { class: "empty" }, "Nothing happened in the last 7 days.");
  } });
  return section(projectID ? "Recent activity" : "Activity", "The last 7 days: agent sessions, task and knowledge changes, syncs.", list);
}

function activityRow(x, showProject) {
  const href = x.kind === "task" ? projectHref(x.project_id, "work", x.ref) : x.kind === "memory" ? projectHref(x.project_id, "knowledge")
    : x.kind === "sync" ? projectHref(x.project_id, "health") : projectHref(x.project_id, "agents", "tools");
  return h("li", { class: "act" + (x.level ? ` lv-${x.level}` : "") },
    h("span", { class: `act-ico k-${x.kind}`, "aria-hidden": "true" }, ACTIVITY_ICON[x.kind] || "•"),
    h("div", { class: "act-body" },
      h("div", {}, h("a", { href }, x.title), x.level && [" ", levelPill(x.level)]),
      x.detail && h("div", { class: "act-detail" }, x.detail),
      h("div", { class: "act-meta" }, h("span", { title: x.at }, ago(x.at)),
        showProject && [" · ", h("a", { href: projectHref(x.project_id) }, x.project)],
        x.actor && x.kind !== "session" && ` · ${x.actor}`)));
}

// --- project overview ---
async function viewOverview(p) {
  const l = p.live;
  const [ts, rep] = await Promise.allSettled([api(projectAPI(p, "tasks")), api(projectAPI(p, `report?days=${state.days}`))]);
  const all = ts.status === "fulfilled" ? ts.value.tasks || [] : [];
  const fs = rep.status === "fulfilled" ? rep.value.findings || [] : [];
  const st = rep.status === "fulfilled" ? rep.value.context || {} : {};
  const done = all.filter((x) => x.status === "done").length;
  const items = [];
  if (l.pending_proposals > 0) items.push(["warn", `${l.pending_proposals} proposal(s) wait for review`, projectHref(p.id, "knowledge", "inbox")]);
  if (l.pending_arch_proposals > 0) items.push(["warn", `${l.pending_arch_proposals} architectural proposal(s) wait for review`, projectHref(p.id, "work", "inbox")]);
  if (l.checks_failed > 0) items.push(["problem", `${l.checks_failed} doctor check(s) failing`, projectHref(p.id, "health")]);
  if (l.stale) items.push(["warn", l.stale, projectHref(p.id, "health")]);
  for (const x of all.filter((x) => x.status === "blocked")) items.push(["warn", `${x.id} is blocked${x.blocked_reason ? ": " + x.blocked_reason : ""}`, projectHref(p.id, "work", x.id)]);
  for (const f of fs.filter((f) => f.level === "problem" || f.level === "warn").slice(0, 5)) items.push([f.level, f.message, projectHref(p.id, "agents", "findings")]);
  const rank = { problem: 0, warn: 1, info: 2 };
  items.sort((a, b) => rank[a[0]] - rank[b[0]]);
  return h("div", {},
    h("div", { class: "tiles" },
      h("a", { class: "tile link", href: projectHref(p.id, "agents", "context") }, h("div", { class: "label" }, "Context hits"),
        h("div", { class: "value" }, st.changed ? pct(st.hit_rate) : "—"),
        st.changed ? [rateBar(st.hit_rate), h("div", { class: "note" }, `${st.hits} of ${st.changed} changed files were in an answer`)] : h("div", { class: "note" }, "no scored answers yet")),
      h("a", { class: "tile link", href: projectHref(p.id, "work") }, h("div", { class: "label" }, "Tasks"),
        h("div", { class: "value" }, all.length ? `${done} of ${all.length}` : "—"),
        all.length ? [taskBar(all, false), h("div", { class: "note" }, `${all.filter((x) => x.status === "ready").length} ready to take`)] : h("div", { class: "note" }, "no tasks")),
      h("a", { class: "tile link", href: projectHref(p.id, "knowledge", "inbox") }, h("div", { class: "label" }, "Proposals"),
        h("div", { class: "value" }, l.pending_proposals || 0), h("div", { class: "note" }, l.pending_proposals ? "waiting in the Inbox" : "nothing waiting"))),
    h("div", { class: "withside" },
      h("div", { class: "mainside" },
        section("Needs attention", "What waits for a person in this project, most urgent first.",
          items.length ? h("ul", { class: "attn" }, items.map(([lv, msg, href]) => h("li", {}, levelPill(lv), h("a", { href }, msg))))
            : h("p", { class: "okay" }, "✓ Nothing waits for you here.")),
        fs.length > 0 && h("p", { class: "section" }, h("a", { href: projectHref(p.id, "agents", "findings") }, `All ${fs.length} findings of harness report, with their actions →`))),
      h("aside", { class: "sidecol" }, activitySection(p.id))));
}

// --- machine ---
async function renderMachine(main) {
  const m = await api("/api/machine").catch((e) => ({ error: e.message }));
  const viewSel = h("select", { "aria-label": "Default project view", onchange: (ev) => { store.set("projectView", ev.target.value); } },
    PROJECT_VIEWS.map(([v, label]) => h("option", { value: v, selected: v === defaultView() }, label)));
  const themeSel = h("select", { "aria-label": "Theme", onchange: (ev) => { store.set("theme", ev.target.value); applyTheme(); } },
    THEMES.map((t) => h("option", { value: t, selected: t === store.get("theme", "system") }, t === "system" ? "follow the system" : t)));
  const b = m.build || {}, t = m.telemetry || {}, paths = m.paths || {};
  fill(main, 
    h("div", { class: "phead" }, h("div", { class: "title" }, h("h2", {}, "Machine")),
      h("p", { class: "path" }, "This harness installation, where its state lives, and this page's settings.")),
    m.error && h("p", { class: "error" }, m.error),
    section("Settings", "Kept in this browser.", h("div", { class: "card pad" }, kv([
      ["Open projects on", viewSel],
      ["Theme", themeSel],
    ]))),
    section("harness", null, h("div", { class: "card pad" }, kv([
      ["Version", b.version], ["Commit", b.commit && h("code", {}, b.commit.slice(0, 12))], ["Built", b.date], ["Platform", b.platform], ["Runtime", b.runtime],
    ]))),
    section("C# sessions (graphify-csharp)", "Warm sessions keep a C# solution compiled for callers, references and impact; loading a large one takes minutes. Sessions harness starts stay warm for the next agent session and stop after 2 hours without queries; ones started by hand are left alone.",
      graphifySessions(null)),
    section("State", "Machine-wide files; each project keeps its own state in its .harness folder.", h("div", { class: "card pad" }, kv([
      ["Global folder", paths.global && h("code", {}, paths.global)], ["Project registry", paths.registry && h("code", {}, paths.registry)],
      ["Workspaces", paths.workspaces && h("code", {}, paths.workspaces)], ["Workspace and user knowledge", paths.knowledge && h("code", {}, paths.knowledge)],
    ]))),
    section("Developer telemetry", "Opt-in aggregated reports to the harness developer ('harness telemetry'). Never code, paths or names.",
      h("div", { class: "card pad" }, kv([
        ["State", t.enabled ? h("span", { class: "okay" }, "✓ on") : "off"],
        ["Endpoint", t.endpoint && h("code", {}, t.endpoint)],
        ["Last sent", t.last_sent ? ago(t.last_sent) : t.enabled ? "not yet" : ""],
      ]))));
}

// --- docs (TASK-0044) ---
// The documentation the binary carries: the guide and the reference, one
// section at a time, the list beside it.
async function renderDocs(main, r) {
  const [list, sec] = await Promise.all([api("/api/docs"), api(`/api/docs/${enc(r.item)}`).catch((e) => ({ error: e.message }))]);
  const title = (d) => (list.docs.find((x) => x.Name === d) || {}).Title || d;
  const groups = [];
  for (const s of list.sections) {
    let g = groups.find((x) => x.doc === s.doc);
    if (!g) groups.push(g = { doc: s.doc, items: [] });
    g.items.push(s);
  }
  fill(main,
    h("div", { class: "phead" }, h("div", { class: "title" }, h("h2", {}, "Docs")),
      h("p", { class: "path" }, "The guide and the reference this harness carries; 'harness docs <section>' prints the same.")),
    h("div", { class: "docs" },
      h("nav", { class: "doclist", "aria-label": "Sections" }, groups.map((g) => h("div", {},
        h("h4", {}, title(g.doc)),
        h("ul", {}, g.items.map((s) => h("li", {}, h("a", { href: "#/docs/" + enc(s.slug), "aria-current": s.slug === sec.slug ? "page" : null }, s.title))))))),
      h("article", { class: "doctext card pad" }, sec.error ? h("p", { class: "error" }, sec.error) : markdown(sec.body || ""))));
}

// markdown renders the docs' Markdown as elements (never as HTML): headings,
// paragraphs, lists, tables, code blocks, inline code, bold and links.
function markdown(src) {
  const out = [];
  const lines = src.split("\n");
  const inline = (t) => {
    const parts = [];
    const re = /(`[^`]+`)|(\*\*[^*]+\*\*)|\[([^\]]+)\]\(([^)\s]+)\)/g;
    let last = 0, m;
    while ((m = re.exec(t))) {
      if (m.index > last) parts.push(t.slice(last, m.index));
      if (m[1]) parts.push(h("code", {}, m[1].slice(1, -1)));
      else if (m[2]) parts.push(h("b", {}, m[2].slice(2, -2)));
      else {
        const url = m[4];
        const href = url.startsWith("#") ? "#/docs/" + enc(url.slice(1)) : /^https?:\/\//.test(url) ? url : null;
        parts.push(href ? h("a", { href, rel: "noreferrer" }, m[3]) : m[3]);
      }
      last = re.lastIndex;
    }
    if (last < t.length) parts.push(t.slice(last));
    return parts;
  };
  for (let i = 0; i < lines.length;) {
    const l = lines[i];
    if (/^```/.test(l.trim())) {
      const code = [];
      for (i++; i < lines.length && !/^```/.test(lines[i].trim()); i++) code.push(lines[i]);
      i++;
      out.push(h("pre", {}, h("code", {}, code.join("\n"))));
    } else if (/^#{1,5} /.test(l)) {
      const n = l.match(/^#+/)[0].length;
      out.push(h("h" + Math.min(n + 1, 6), {}, inline(l.slice(n + 1))));
      i++;
    } else if (/^\s*([-*]|\d+\.) /.test(l)) {
      const ordered = /^\s*\d+\. /.test(l);
      const items = [];
      while (i < lines.length && (/^\s*([-*]|\d+\.) /.test(lines[i]) || (/^\s{2,}\S/.test(lines[i]) && items.length))) {
        if (/^\s*([-*]|\d+\.) /.test(lines[i])) items.push(lines[i].replace(/^\s*([-*]|\d+\.) /, ""));
        else items[items.length - 1] += " " + lines[i].trim();
        i++;
      }
      out.push(h(ordered ? "ol" : "ul", {}, items.map((x) => h("li", {}, inline(x)))));
    } else if (/^\|/.test(l)) {
      const rows = [];
      while (i < lines.length && /^\|/.test(lines[i])) rows.push(lines[i++]);
      const cells = (row) => row.replace(/^\||\|$/g, "").split("|").map((c) => c.trim());
      const body = rows.filter((x, k) => k !== 1 || !/^\|[\s:|-]+\|?$/.test(x));
      out.push(h("table", { class: "doctable" }, h("thead", {}, h("tr", {}, cells(body[0]).map((c) => h("th", {}, inline(c))))),
        h("tbody", {}, body.slice(1).map((row) => h("tr", {}, cells(row).map((c) => h("td", {}, inline(c))))))));
    } else if (l.trim() === "") {
      i++;
    } else {
      const para = [];
      while (i < lines.length && lines[i].trim() !== "" && !/^(#{1,5} |```|\||\s*([-*]|\d+\.) )/.test(lines[i])) para.push(lines[i++]);
      out.push(h("p", {}, inline(para.join(" "))));
    }
  }
  return out;
}

// --- your knowledge: the user level ---
async function renderUserKnowledge(main) {
  fill(main, 
    h("div", { class: "phead" }, h("div", { class: "title" }, h("h2", {}, "Your knowledge")),
      h("p", { class: "path" }, "Memories at user scope: they apply to every project of yours on this machine.")),
    await levelKnowledge("user", "Knowledge for every project of yours on this machine; a workspace's or a project's own statement takes precedence."));
}

// --- search ---
// Results come from /api/search: projects, workspaces, memories (every
// readable project and the wider levels), tasks and blueprints.
const SEARCH_KINDS = [["project", "Projects"], ["workspace", "Workspaces"], ["memory", "Memories"], ["task", "Tasks"], ["blueprint", "Blueprints"]];

// hitHref is where a search result leads.
function hitHref(x) {
  switch (x.kind) {
    case "project": return projectHref(x.project_id, "overview");
    case "workspace": return workspaceHref(x.workspace);
    case "task": return projectHref(x.project_id, "work", x.ref);
    case "blueprint": return projectHref(x.project_id, "work");
    case "memory":
      if (x.project_id) return projectHref(x.project_id, "knowledge");
      return x.workspace ? workspaceHref(x.workspace, "knowledge") : "#/knowledge";
  }
  return "#/";
}
// hitWhere names where a result lives.
function hitWhere(x) {
  if (x.kind === "project" || x.kind === "workspace") return x.detail || "";
  if (x.project) return x.project;
  return x.workspace ? "workspace · " + wtitle(x.workspace) : "you · all your projects";
}
function hitRow(x, onclick) {
  return h("a", { class: "hit", href: hitHref(x), onclick },
    x.kind === "memory" ? h("span", { class: `hkind ty-${x.detail}` }, h("span", { class: "dot", "aria-hidden": "true" }), x.detail)
      : x.kind === "task" ? h("span", { class: "hkind" }, x.detail) : null,
    h("span", { class: "htitle" }, x.title),
    h("span", { class: "hwhere" }, hitWhere(x)));
}

async function renderSearch(main, r) {
  const head = h("div", { class: "phead" }, h("div", { class: "title" }, h("h2", {}, r.q ? `Search: “${r.q}”` : "Search")),
    h("p", { class: "path" }, "Projects, workspaces, memories, tasks and blueprints across the machine. Code is searched by agents, with search_project."));
  if (r.q.length < 2) return fill(main, head, h("p", { class: "empty" }, "Type at least two characters in the search box above."));
  const res = await api(`/api/search?q=${enc(r.q)}`);
  const hits = res.hits || [];
  const groups = SEARCH_KINDS.map(([k, label]) => {
    const xs = hits.filter((x) => x.kind === k);
    return xs.length && h("section", { class: "section hits" }, h("h3", {}, label, " ", h("span", { class: "count" }, xs.length >= res.per_kind ? `${xs.length}+` : xs.length)),
      h("div", { class: "hitlist" }, xs.map((x) => hitRow(x))));
  }).filter(Boolean);
  fill(main, head, groups.length ? groups : h("p", { class: "empty" }, `Nothing matches “${r.q}”.`));
}

// quickSearch shows the top results under the search box while typing;
// Enter opens the full results.
function quickSearch(input) {
  const box = h("div", { class: "quick", role: "listbox", hidden: true });
  input.closest("form").append(box);
  let timer, seq = 0;
  const close = () => { box.hidden = true; };
  input.addEventListener("input", () => {
    clearTimeout(timer);
    const q = input.value.trim();
    if (q.length < 2) return close();
    timer = setTimeout(async () => {
      const n = ++seq;
      let res;
      try { res = await api(`/api/search?q=${enc(q)}`); } catch { return close(); }
      if (n !== seq) return;
      const hits = res.hits || [];
      const top = SEARCH_KINDS.flatMap(([k, label]) => {
        const xs = hits.filter((x) => x.kind === k).slice(0, 3);
        return xs.length ? [h("div", { class: "qlabel" }, label), ...xs.map((x) => hitRow(x, () => { input.value = ""; close(); }))] : [];
      });
      fill(box, top.length ? top : h("p", { class: "empty" }, "No matches."),
        h("a", { class: "qall", href: `#/search?q=${enc(q)}`, onclick: close }, `All results for “${q}” ↵`));
      box.hidden = false;
    }, 180);
  });
  input.addEventListener("keydown", (ev) => { if (ev.key === "Escape") { close(); input.blur(); } });
  document.addEventListener("click", (ev) => { if (!input.closest("form").contains(ev.target)) close(); });
  input.closest("form").addEventListener("submit", close);
}

// membersManage is a workspace's Members view: a table of its projects
// (paths relative to the folder they share), a filter, and changes that
// are reviewed first: remove the selected ones, add projects, delete.
function membersManage(w) {
  const box = h("div", {});
  const sel = new Set();
  let q = "";
  const roots = w.members.map((m) => m.root).filter(Boolean);
  // the folder every member lives under, shown once
  let common = roots.length > 1 ? roots[0].split("/").slice(0, -1).join("/") : "";
  for (const r of roots) while (common && !r.startsWith(common + "/")) common = common.split("/").slice(0, -1).join("/");
  const rel = (root) => (common && root.startsWith(common + "/") ? root.slice(common.length + 1) : root);
  const members = w.members.map((m) => ({ m, p: state.projects.find((x) => x.id === m.project_id) }));
  const others = state.projects.filter((p) => !w.members.some((m) => m.project_id === p.id));
  const done = () => renderMain();
  const tbody = h("tbody", {});
  const removeBtn = h("button", { class: "ghost", disabled: true }, "Remove selected");
  const all = h("input", { type: "checkbox", "aria-label": "Select all shown" });
  function shown() {
    return members.filter(({ m, p }) => !q || (p ? pname(p) : m.project).toLowerCase().includes(q) || (m.root || "").toLowerCase().includes(q))
      .sort((a, b) => (b.p ? waitingIn(b.p) : 0) - (a.p ? waitingIn(a.p) : 0) || (a.p ? pname(a.p) : a.m.project).localeCompare(b.p ? pname(b.p) : b.m.project));
  }
  function update() {
    removeBtn.disabled = !sel.size;
    removeBtn.textContent = sel.size ? `Remove selected (${sel.size})` : "Remove selected";
    const vis = shown();
    all.checked = vis.length > 0 && vis.every(({ m }) => sel.has(m.project_id));
  }
  function draw() {
    fill(tbody, shown().map(({ m, p }) => {
      const cb = h("input", { type: "checkbox", "aria-label": `Select ${p ? pname(p) : m.project}`, onchange: (ev) => { ev.target.checked ? sel.add(m.project_id) : sel.delete(m.project_id); update(); } });
      cb.checked = sel.has(m.project_id);
      return h("tr", {},
        h("td", { class: "cbx" }, cb),
        h("td", {}, p ? h("a", { href: projectHref(p.id), onclick: () => { state.lastWs = w.name; } }, pname(p)) : h("span", { class: "muted" }, m.project_id)),
        h("td", {}, p ? stateGlyph(p.live) : h("span", { class: "muted" }, "not registered")),
        h("td", {}, p && stackBadge(p.id, "small")),
        h("td", { class: "n" }, p && waitingIn(p) ? h("span", { class: "badge" }, waitingIn(p)) : h("span", { class: "muted" }, "—")),
        h("td", { class: "small" }, p && p.live.synced_at ? ago(p.live.synced_at) : h("span", { class: "muted" }, "never")),
        h("td", { class: "small" }, p && p.last_mcp ? ago(p.last_mcp) : h("span", { class: "muted" }, "—")),
        h("td", {}, p && p.live.context_answers_30d > 0 ? rateMeter(p.live.context_hit_rate_30d) : h("span", { class: "muted small" }, "—")),
        h("td", { class: "mono small muted wrap", title: m.root || "" }, m.root ? rel(m.root) : "no longer registered on this machine"));
    }));
    update();
  }
  all.addEventListener("change", () => { for (const { m } of shown()) all.checked ? sel.add(m.project_id) : sel.delete(m.project_id); draw(); });
  removeBtn.addEventListener("click", () => {
    const ids = [...sel];
    const name = (id) => { const x = members.find(({ m }) => m.project_id === id); return x && x.p ? pname(x.p) : id; };
    fill(box, reviewPanel({
      title: `Remove ${ids.length} project(s) from ${wname(w)}?`, lines: ids.map((id) => `− ${name(id)}`).concat([`Their own state and the other members stay as they are.`]),
      confirm: "Remove", danger: true, onBack: () => fill(box),
      onConfirm: async () => { for (const id of ids) await post(`/api/workspaces/${enc(w.name)}/leave`, { project_id: id }); await afterWrite(); },
    }));
  });
  const filter = h("input", { type: "search", placeholder: "Filter members", "aria-label": "Filter members", oninput: (ev) => { q = ev.target.value.trim().toLowerCase(); draw(); } });
  draw();
  return h("div", {},
    h("div", { class: "deptools" }, filter,
      removeBtn,
      others.length > 0 && h("button", { onclick: () => fill(box, pickProjects({
        title: `Add projects to ${wname(w)}`, projects: others, checked: new Set(), submit: "Review",
        onBack: () => fill(box),
        onPick: (ids) => fill(box, reviewPanel({
          title: `Add ${ids.length} project(s) to ${wname(w)}?`, lines: ids.map((id) => `+ ${projectLabel(id)}`), confirm: "Add",
          onBack: () => fill(box),
          onConfirm: async () => { await post(`/api/workspaces/${enc(w.name)}/join`, { project_ids: ids }); await afterWrite(); },
        })),
      })) }, "Add projects"),
      h("span", { class: "spacer" }),
      h("button", { class: "ghost danger-text", onclick: () => fill(box, reviewPanel({
        title: `Delete workspace ${wname(w)}?`, lines: [`The workspace and its membership list go; its ${w.members.length} project(s) keep all their state.`,
          "Knowledge recorded at workspace scope stays on disk but no longer applies to any project."],
        confirm: "Delete workspace", danger: true, onBack: () => fill(box),
        onConfirm: async () => { await post(`/api/workspaces/${enc(w.name)}/delete`, {}); location.hash = "#/"; await refresh(); },
      })) }, "Delete workspace")),
    box,
    common && h("p", { class: "hint" }, "Paths under ", h("code", {}, common)),
    h("div", { class: "card" }, h("table", { class: "members" },
      h("thead", {}, h("tr", {}, h("th", { class: "cbx" }, all), h("th", {}, "project"), h("th", {}, "state"), h("th", {}, "stack"), h("th", { class: "n" }, "waiting"), h("th", {}, "synced"), h("th", {}, "agent used"), h("th", {}, "context hits"), h("th", {}, "path"))),
      tbody)));
}

const projectLabel = (id) => { const p = state.projects.find((x) => x.id === id); return p ? `${pname(p)}  ${p.root}` : id; };

// createForm: a name, and optionally the registered projects under a
// folder, all shown checked for review before anything is created.
function createForm(box) {
  const nameIn = h("input", { type: "text", name: "ws-name", placeholder: "e.g. company", "aria-label": "Workspace name" });
  const titleIn = h("input", { type: "text", name: "ws-title", placeholder: "e.g. Company services (optional)", "aria-label": "Display name" });
  const folderIn = h("input", { type: "text", name: "ws-folder", placeholder: "/path/to/folder (optional)", "aria-label": "Folder" });
  const err = h("p", { class: "error", hidden: true });
  const fail = (m) => { err.textContent = m; err.hidden = false; };
  const next = async (ev) => {
    ev.preventDefault();
    const name = nameIn.value.trim();
    const title = titleIn.value.trim().replace(/\s+/g, " ");
    if (!/^[a-z0-9][a-z0-9._-]{0,63}$/.test(name)) return fail("Use lower-case letters, digits, '.', '_' or '-' (starting with a letter or digit).");
    let checked = new Set();
    const folder = folderIn.value.trim();
    if (folder) {
      try {
        const found = await api(`/api/workspaces/discover?folder=${encodeURIComponent(folder)}`);
        checked = new Set(found.projects.map((p) => p.project_id));
        if (!checked.size) return fail(`No registered projects under ${found.folder}.`);
      } catch (e) { return fail(e.message); }
    }
    fill(box, pickProjects({
      title: `Projects in ${name}` + (folder ? ` (those under ${folder} are checked)` : ""), projects: state.projects, checked,
      submit: "Review", allowNone: true, onBack: () => fill(box, createForm(box)),
      onPick: (ids) => fill(box, reviewPanel({
        title: `Create workspace ${title || name}?`, lines: [title && `shown as “${title}”, short name ${name}`, ...(ids.length ? ids.map((id) => `+ ${projectLabel(id)}`) : ["No projects yet; add them later."])].filter(Boolean),
        confirm: "Create", onBack: () => fill(box, createForm(box)),
        onConfirm: async () => { await post("/api/workspaces", { name, title, project_ids: ids }); location.hash = workspaceHref(name); refresh(); },
      })),
    }));
  };
  return h("form", { class: "aform", onsubmit: next },
    h("p", { class: "hint" }, "New workspace"),
    h("label", {}, h("span", {}, "Short name (what agents and commands use)"), nameIn),
    h("label", {}, h("span", {}, "Display name (any text, optional)"), titleIn),
    h("label", {}, h("span", {}, "Find registered projects under a folder"), folderIn),
    err,
    h("div", { class: "review" }, h("button", { class: "primary", type: "submit" }, "Next"),
      h("button", { type: "button", class: "ghost", onclick: () => fill(box) }, "Cancel")));
}

// renameForm changes a display name (any text) and a short name (what
// commands, scopes and workspace results use), reviewed before it is sent.
function renameForm({ what, display, short, folder, box, send }) {
  const dIn = h("input", { type: "text", name: "rn-display", "aria-label": "Display name", placeholder: what === "project" ? folder : short });
  dIn.value = display;
  const sIn = h("input", { type: "text", name: "rn-short", "aria-label": "Short name", placeholder: folder || "" });
  sIn.value = short;
  const err = h("p", { class: "error", hidden: true });
  const shortRule = what === "workspace" ? /^[a-z0-9][a-z0-9._-]{0,63}$/ : /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;
  const next = (ev) => {
    ev.preventDefault();
    const d = dIn.value.trim().replace(/\s+/g, " "), sh = sIn.value.trim();
    if (d.length > 80) { err.textContent = "Keep the display name under 80 characters."; err.hidden = false; return; }
    if ((what === "workspace" || sh) && !shortRule.test(sh)) {
      err.textContent = what === "workspace" ? "Short name: lower-case letters, digits, '.', '_' or '-'." : "Short name: letters, digits, '.', '_' or '-' (empty: the folder's name).";
      err.hidden = false; return;
    }
    const body = {}, lines = [];
    if (d !== display) { body.title = d; lines.push(d ? `display name: ${display || "(none)"} → ${d}` : `display name removed (${display})`); }
    if (sh !== short) {
      body.name = sh;
      lines.push(`short name: ${short || folder} → ${sh || folder}`);
      lines.push(what === "workspace" ? `agents and commands will use workspace:${sh}` : `workspace results will name it ${sh || folder}`);
    }
    if (!lines.length) { fill(box); return; }
    fill(box, reviewPanel({
      title: `Rename this ${what}?`, lines, confirm: "Rename", onBack: () => fill(box, renameForm({ what, display, short, folder, box, send })),
      note: what === "workspace" ? WS_NOTE : "Only the machine's project registry changes; the repository and its .harness state are untouched.",
      onConfirm: async () => { await send(body); await afterWrite(); },
    }));
  };
  return h("form", { class: "aform", onsubmit: next },
    h("p", { class: "hint" }, `Rename this ${what}`),
    h("label", {}, h("span", {}, "Display name (any text; empty shows the short name)"), dIn),
    h("label", {}, h("span", {}, what === "workspace" ? "Short name (what agents and commands use)" : "Short name (how workspace results name it; empty: the folder's name)"), sIn),
    err,
    h("div", { class: "review" }, h("button", { class: "primary", type: "submit" }, "Review"),
      h("button", { type: "button", class: "ghost", onclick: () => fill(box) }, "Cancel")));
}

// pickProjects is a checklist of registered projects.
function pickProjects({ title, projects, checked, submit, allowNone, onPick, onBack }) {
  const err = h("p", { class: "error", hidden: true });
  const boxes = projects.map((p) => {
    const cb = h("input", { type: "checkbox", value: p.id, "aria-label": pname(p) });
    cb.checked = checked.has(p.id);
    return h("label", { class: "check" }, cb, h("span", {}, pname(p)), h("span", { class: "mono muted small" }, p.root));
  });
  return h("form", {
    class: "aform",
    onsubmit: (ev) => {
      ev.preventDefault();
      const ids = boxes.map((l) => l.querySelector("input")).filter((c) => c.checked).map((c) => c.value);
      if (!ids.length && !allowNone) { err.textContent = "Choose at least one project."; err.hidden = false; return; }
      onPick(ids);
    },
  }, h("p", { class: "hint" }, title), boxes, err,
  h("div", { class: "review" }, h("button", { class: "primary", type: "submit" }, submit), h("button", { type: "button", class: "ghost", onclick: onBack }, "Back")));
}

// reviewPanel shows what a change will do before it is sent.
function reviewPanel({ title, lines, confirm, danger, onConfirm, onBack, note }) {
  const err = h("p", { class: "error", hidden: true });
  const ok = h("button", { class: danger ? "danger" : "primary", type: "submit" }, confirm);
  return h("form", {
    class: "aform",
    onsubmit: async (ev) => {
      ev.preventDefault();
      ok.disabled = true;
      try { await onConfirm(); } catch (e) { err.textContent = e.message; err.hidden = false; ok.disabled = false; }
    },
  }, h("p", { class: "hint" }, title), h("ul", { class: "changes mono" }, lines.map((l) => h("li", {}, l))),
  h("p", { class: "muted small" }, note || WS_NOTE), err,
  h("div", { class: "review" }, ok, h("button", { type: "button", class: "ghost", onclick: onBack }, "Back")));
}

function projectCard(p) {
  const l = p.live;
  const href = (tab) => projectHref(p.id, ...({ context: ["agents", "context"], findings: ["agents", "findings"], proposals: ["knowledge", "inbox"], tasks: ["work"], health: ["health"] }[tab] || [tab]));
  const tasksRow = h("div", { class: "row" }, h("span", { class: "k" }, "Tasks"), h("span", { class: "v muted" }, l.state === "ok" ? "…" : "—"));
  const findRow = h("div", { class: "row" }, h("span", { class: "k" }, "Findings"), h("span", { class: "v muted" }, l.state === "ok" ? "…" : "—"));
  const checks = l.checks_failed > 0 ? h("span", { class: "flag bad" }, `${l.checks_failed} failing`)
    : l.checks_warned > 0 ? h("span", { class: "flag warn" }, `${l.checks_warned} warning(s)`) : h("span", { class: "okay" }, "✓ all pass");
  const el = h("article", { class: "pcard" },
    h("a", { class: "ptitle", href: href("context") }, h("span", {}, pname(p)), stateBadge(l)),
    h("div", { class: "pmeta mono" }, l.commit ? `${l.branch || "detached HEAD"} · ${shortCommit(l.commit)}` : p.root),
    h("div", { class: "pmeta" }, [l.synced_at && `synced ${ago(l.synced_at)}`, p.last_mcp && `agent ${ago(p.last_mcp)}`].filter(Boolean).join(" · ")),
    l.detail && h("p", { class: "detail" }, l.detail),
    h("div", { class: "rows" },
      h("div", { class: "row" }, h("a", { class: "k", href: href("health") }, "Doctor"), h("span", { class: "v" }, checks)),
      h("div", { class: "row" }, h("a", { class: "k", href: href("context") }, "Context hits"),
        h("span", { class: "v" }, l.state === "ok" && l.context_answers_30d > 0 ? rateMeter(l.context_hit_rate_30d) : h("span", { class: "muted" }, "no scored answers (30 days)"))),
      h("div", { class: "row" }, h("a", { class: "k", href: href("proposals") }, "Proposals"),
        h("span", { class: "v" }, l.pending_proposals > 0 ? h("span", { class: "flag warn" }, `${l.pending_proposals} to review`) : h("span", { class: "muted" }, "none pending"))),
      findRow, tasksRow));
  async function load() {
    if (l.state !== "ok") return;
    const [ts, rep] = await Promise.allSettled([api(projectAPI(p, "tasks")), api(projectAPI(p, `report?days=${state.days}`))]);
    if (ts.status === "fulfilled") {
      const all = ts.value.tasks || [];
      const n = (s) => all.filter((x) => x.status === s).length;
      const done = n("done");
      fill(tasksRow, h("a", { class: "k", href: href("tasks") }, "Tasks"), h("span", { class: "v" },
        all.length ? [meter(done, all.length, `${done} of ${all.length} done`),
          h("span", { class: "counts" }, ["ready", "in_progress", "blocked"].filter((s) => n(s)).map((s) => [statusPill(s), " ", n(s), " "]))]
          : h("span", { class: "muted" }, "no tasks")));
    }
    if (rep.status === "fulfilled") {
      const fs = rep.value.findings || [];
      const n = (lv) => fs.filter((f) => f.level === lv).length;
      const worst = fs.filter((f) => f.level !== "info").slice(0, 2);
      fill(findRow, h("a", { class: "k", href: href("findings") }, "Findings"), h("span", { class: "v" },
        !fs.length ? h("span", { class: "okay" }, "✓ nothing to report")
          : [h("span", { class: "counts" }, ["problem", "warn", "info"].filter((lv) => n(lv)).map((lv) => [levelPill(lv), " ", n(lv), " "])),
            worst.length > 0 && h("span", { class: "issues" }, worst.map((f) => h("span", { class: "flag " + (f.level === "problem" ? "bad" : "warn") }, f.message)))]));
    }
  }
  return { el, load };
}

// --- health: harness status + doctor ---
async function viewHealth(p) {
  const wrap = h("div", {});
  const out = h("div", {});
  const probeBtn = h("button", { onclick: () => load(true) }, "Check providers");
  async function load(probe) {
    fill(out, h("p", { class: "empty" }, probe ? "Starting each provider to check it (can take a while)…" : "Loading…"));
    probeBtn.disabled = true;
    try {
      const r = await api(projectAPI(p, `health${probe ? "?probe=1" : ""}`), probe ? { "X-Harness-UI": "1" } : null);
      fill(out, healthReport(r));
    } catch (e) {
      fill(out, h("p", { class: "error" }, e.message));
    } finally {
      probeBtn.disabled = false;
    }
  }
  wrap.append(h("div", { class: "shead" },
    h("p", { class: "hint" }, "What 'harness doctor' and 'harness status' report. Provider checks start each provider, so they run only on request."), probeBtn), out);
  load(false);
  return wrap;
}

// --- stack badge (TASK-0033) ---
// What a project is built on in a few words, with a small mark for its
// language (drawn here, in the design system's colours) and an atom when
// it uses React: "C# · .NET 10", "TypeScript · React 18 · Node 22".
const LANG_MARK = { "C#": ["C#", "cs"], TypeScript: ["TS", "ts"], JavaScript: ["JS", "js"], Go: ["Go", "go"], Dart: ["Dt", "dart"] };
function langMark(st) {
  const [txt, cls] = LANG_MARK[st.language] || [st.language.slice(0, 2), "other"];
  const react = (st.frameworks || []).some((f) => f.name === "React" || f.name === "Next.js" || f.name === "React Native");
  return [h("span", { class: `lmark lm-${cls}`, "aria-hidden": "true" }, txt),
    react && sv("svg", { class: "atom", viewBox: "-12 -12 24 24", "aria-hidden": "true" },
      sv("circle", { r: "2.2" }), [0, 60, 120].map((r) => sv("ellipse", { rx: "10.5", ry: "4", transform: `rotate(${r})` })))];
}
// stackBadge: the mark says the language, the text the rest ("[C#] .NET
// 10", "[TS] React 18 · Node 22"); markOnly keeps just the mark, the rest
// on hover (where it would repeat on every card).
function stackBadge(id, cls, markOnly) {
  const st = (state.stacks || {})[id];
  if (!st) return null;
  const rest = st.badge.split(" · ").slice(1).join(" · ") || (st.language === "Go" ? st.badge.replace(/^Go /, "") : "");
  const title = [st.language, st.runtime && `${st.runtime.name} ${st.runtime.version}`, ...(st.frameworks || []).map((f) => `${f.name}${f.version ? " " + f.version : ""}`)].filter(Boolean).join(" · ");
  return h("span", { class: "stackbadge" + (cls ? " " + cls : ""), title, "aria-label": st.badge },
    langMark(st), !markOnly && rest && h("span", { class: "sbtext" }, rest.replace(/^Dart \d+$/, "")));
}
// versionsTable compares what the members are built on: runtimes, main
// frameworks, and the members' own packages against the version their
// owner declares.
function versionsTable(w, d) {
  if (!d || !(d.rows || []).length) return null;
  const ids = w.members.map((m) => m.project_id).filter((id) => d.stacks[id] || d.rows.some((r) => r.versions[id]));
  const name = (id) => { const p = state.projects.find((x) => x.id === id); return p ? pname(p) : id; };
  const KIND = { runtime: "Runtime", framework: "Frameworks", core: "The members' own packages" };
  // one owner's packages used at the same versions are one row
  const merged = [];
  for (const r of d.rows) {
    const key = r.kind === "core" ? r.owner + "\x00" + JSON.stringify(Object.entries(r.versions).sort()) + "\x00" + (r.published || "") : null;
    const prev = key && merged.find((x) => x.key === key);
    if (prev) { prev.names.push(r.name); continue; }
    merged.push({ ...r, key, names: [r.name] });
  }
  let last = "";
  const rows = [];
  for (const r of merged) {
    if (r.kind !== last) {
      last = r.kind;
      rows.push(h("tr", { class: "vgroup" }, h("td", { colspan: String(ids.length + 2) }, KIND[r.kind])));
    }
    const all = Object.values(r.versions).flat();
    const top = all.slice().sort(cmpVer).pop();
    rows.push(h("tr", { class: r.differ ? "differ" : "" },
      h("td", { class: r.kind === "core" ? "mono" : "", title: r.names.length > 1 ? r.names.join("\n") : "" }, r.names[0],
        r.names.length > 1 && h("span", { class: "muted small" }, ` and ${r.names.length - 1} more package${r.names.length === 2 ? "" : "s"}`),
        r.owner && h("div", { class: "muted small" }, `from ${name(r.owner)}`)),
      ids.map((id) => {
        const vs = r.versions[id];
        if (!vs) return h("td", { class: "muted" }, "·");
        const behind = vs.some((v) => cmpVer(v, r.published || top) < 0);
        return h("td", { class: "mono" + (behind ? " vdiff" : "") }, vs.join(", "), behind && h("span", { class: "sr" }, " (behind)"));
      }),
      h("td", { class: "mono" }, r.published || (r.kind === "core" ? h("span", { class: "muted" }, "—") : ""),
        r.published && all.some((v) => cmpVer(v, r.published) > 0) && h("span", { class: "flag info", title: "A member uses a newer version than the owner's files declare: the declaration may be stale (CI may set the version)." }, " stale?"))));
  }
  return section("Versions across the workspace", "What each member is built on, compared. Highlighted: a member on an older version than another, or than the one its owner declares now.",
    h("div", { class: "card versions" }, h("table", {},
      h("thead", {}, h("tr", {}, h("th", {}, ""), ids.map((id) => h("th", {}, name(id))), h("th", {}, "declared by its owner"))),
      h("tbody", {}, rows))));
}

// builtOnSection: a project's stack in full, and the versions of the
// workspace members' packages it uses against what their owners declare.
function builtOnSection(b) {
  if (!b || !b.stack) return null;
  const st = b.stack;
  const part = (x) => h("span", { class: "bpart", title: x.at ? `declared in ${x.at}` : "" }, h("b", {}, x.name), x.version && h("span", { class: "muted" }, " " + x.version));
  const uses = b.uses || [];
  return section("Built on", "Read from the project's files; versions as declared there (hover for where).",
    h("div", { class: "card pad builton" },
      h("div", { class: "bline" }, langMark(st), st.runtime && part(st.runtime), (st.frameworks || []).map(part)),
      uses.length > 0 && h("table", { class: "uses" },
        h("thead", {}, h("tr", {}, h("th", {}, "workspace package"), h("th", {}, "from"), h("th", {}, "uses"), h("th", {}, "declared there"))),
        h("tbody", {}, uses.map((u) => {
          const behind = u.declared && u.versions.some((v) => cmpVer(v, u.declared) < 0);
          const ahead = u.declared && u.versions.some((v) => cmpVer(v, u.declared) > 0);
          return h("tr", {}, h("td", { class: "mono" }, u.package), h("td", {}, h("a", { href: projectHref(u.member_id, "architecture") }, u.member)),
            h("td", { class: "mono" + (behind || ahead ? " vdiff" : "") }, u.versions.join(", ") || "—"),
            h("td", { class: "mono" }, u.declared || h("span", { class: "muted" }, "—"),
              behind && h("span", { class: "flag warn" }, " behind"), ahead && h("span", { class: "flag info" }, " used is newer: is this declaration stale?")));
        })))));
}

// cmpVer orders versions part by part ("1.4.0-alpha.29" < "1.4.0-alpha.42").
function cmpVer(a, b) {
  const pa = String(a).split(/[.\-+]/), pb = String(b).split(/[.\-+]/);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const x = pa[i] ?? "", y = pb[i] ?? "";
    const nx = /^\d+$/.test(x), ny = /^\d+$/.test(y);
    const c = nx && ny ? Number(x) - Number(y) : String(x).localeCompare(String(y));
    if (c) return c;
  }
  return 0;
}

// --- project architecture (TASK-0026) ---
const SMELL_KIND = { layering: "lower layers referencing higher ones", "deep-import": "reaching into a feature's internals", unused: "not used",
  duplicate: "functions written more than once", template: "functions following one template", "interface-candidate": "contracts written once per variant", "repeated-sequence": "statements written out in several functions", "shared-code": "code repeated across members",
  boundary: "dependencies your rules forbid", "rule-unclear": "rules not understood", "stale-knowledge": "knowledge naming code that is gone",
  "no-solution": "in no solution", "test-reference": "production code referencing tests", "missing-project": "references to what is not there", cycle: "circles",
  complexity: "functions with many paths through them" };
const SMELL_WHY = {
  layering: "A lower layer depends on a higher one, so the lower one cannot be used or changed without it. Roles are read from the names (a heuristic).",
  "deep-import": "Files inside a feature are imported instead of its entry file (index), so a change inside the feature breaks its importers. Export what is needed from the index.",
  unused: "Nothing here uses them.",
  duplicate: "Functions or methods with the same code, their names kept (the percentage is how alike): a change to one likely needs the others.",
  template: "Functions taking the same steps on other names (their own repository, entity or call): not copies. A long template could live once in a shared base or generic helper; a short one is a pattern.",
  "interface-candidate": "Operations written once per variant (DetectClaude, DetectCodex), often with code choosing among the variants by name: an interface with one implementation per variant would make the next variant one implementation. Read from names: a heuristic.",
  "repeated-sequence": "The same run of statements written out in several functions, even when each statement calls a shared function: a change to the steps is made in each. One call, however often repeated, is usage and not listed; error checks and cleanup are not counted.",
  "shared-code": "The same code lives in several members: a fix in one copy misses the others. Each says where it could live once.",
  boundary: "References that break the boundary rules recorded as knowledge (layers, forbid, only); each names its rule.",
  complexity: "Cyclomatic complexity counts the independent paths through a function (its branches, loops, cases, short-circuits and null checks): each path wants its own test, and the more there are, the harder a change is to get right. The level follows the thresholds in [architecture.complexity]; the highest come first. Exact .NET values replace harness's count once the Metrics worker has measured HEAD.",
};
// smellsSection lists smells, each one line opening to why and where;
// three or more of a kind are one line opening to the list. With t (the
// findings' history and triage, TASK-0030): each shows whether it is new
// or changed since the last change, its mark, and the author's actions
// (by design with a reason, make it a task, take the mark back); marked
// ones hide behind a toggle, and gone ones (made a task: resolved) fold
// at the end.
// --- evidence shows its code (TASK-0049) ---
// Each place of a finding opens to the code there, read when opened (the
// function it names, a repeated run marked inside it, or the lines around
// a line); copies, templates, repeated runs and shared code can show their
// places side by side, the lines that differ marked. snip(at, span) is the
// snippet URL of a piece of evidence (null: the page cannot read code).
const COMPARE_KINDS = new Set(["duplicate", "template", "repeated-sequence", "shared-code"]);

// projectSnippet and wsSnippet build the snippet URL of evidence.
const projectSnippet = (p) => (at, span) => projectAPI(p, `snippet?at=${enc(at)}&span=${span || 0}`);
const wsSnippet = (w) => (at, span) => `/api/workspaces/${enc(w.name)}/snippet?at=${enc(at)}&span=${span || 0}`;

// runSpan is how many lines a repeated run's evidence covers: its
// statements, listed after the first line of why.
function runSpan(x) {
  if (x.kind !== "repeated-sequence") return 0;
  return (x.why || "").split("\n").slice(1).filter((l) => l && !l.startsWith("They differ")).length;
}

function evidenceText(e) {
  return e.split(" → ").map((part, i) => h("code", { class: i ? "evto" : "" }, i ? "→ " + part : part));
}

// namesFile tells evidence naming a place in a file ("member: path:line
// (Name)") from a dependency, a memory or a folder (findings.ParseSite).
function namesFile(e) {
  if (e.includes(" → ") || e.startsWith("memory ")) return false;
  const m = /^(?:[^\s/:]+: )?([^\s:()]+?)(?::\d+)?(?: \([^()]+\))?(?:: .*)?$/.exec(e.trim());
  return !!m && /[./]/.test(m[1]);
}

// evidenceItem is one place, opening to its code when snip can read it.
function evidenceItem(e, snip, span) {
  if (!snip || !namesFile(e)) return h("li", {}, evidenceText(e));
  const d = h("details", { class: "evcode" }, h("summary", {}, evidenceText(e)));
  d.addEventListener("toggle", () => {
    if (!d.open || d.dataset.loaded) return;
    d.dataset.loaded = "1";
    d.append(mount({ load: async () => codeView((await api(snip(e, span))).snippet) }));
  });
  return h("li", {}, d);
}

// evidenceList is a finding's evidence, folded, with Compare for copies.
function evidenceList(x, snip) {
  const ev = x.evidence || [];
  if (!ev.length) return null;
  const span = runSpan(x);
  const panel = h("div", {});
  const compare = snip && ev.length > 1 && COMPARE_KINDS.has(x.kind) && h("button", { class: "ghost small", type: "button",
    onclick: () => fill(panel, mount({ load: () => compareView(ev.slice(0, 3), snip, span) })) }, `Compare ${Math.min(ev.length, 3)} places side by side`);
  return h("details", { class: "evfold" }, h("summary", {}, `Evidence (${ev.length})`), compare, panel,
    h("ul", { class: "evlist" }, ev.map((e) => evidenceItem(e, snip, span))));
}

// codeView shows a snippet with its line numbers: the lines the evidence
// is about marked, and (differs) the lines another place does not have.
function codeView(sn, differs) {
  if (sn.folder) {
    return h("div", { class: "code" }, h("div", { class: "codehead muted small" }, `${sn.member ? sn.member + ": " : ""}${sn.path}/ · folder`),
      h("div", { class: "codebody" }, sn.lines.length ? sn.lines.map((l) => h("div", { class: "cl" }, h("span", { class: "lc" }, l))) : h("div", { class: "cl muted" }, "(empty)")),
      sn.truncated && h("p", { class: "muted small" }, `Cut at ${sn.lines.length} entries.`));
  }
  const f = sn.focus || [0, 0];
  return h("div", { class: "code" },
    h("div", { class: "codehead muted small" }, `${sn.member ? sn.member + ": " : ""}${sn.path}:${sn.start}`, sn.function && h("span", {}, ` · ${sn.function}`)),
    h("div", { class: "codebody", role: "presentation" }, sn.lines.map((l, i) => {
      const n = sn.start + i;
      const cls = "cl" + (f[0] && n >= f[0] && n <= f[1] ? " focus" : "") + (differs && differs.has(i) ? " differs" : "");
      return h("div", { class: cls }, h("span", { class: "ln", "aria-hidden": "true" }, n), h("span", { class: "lc" }, l || " "));
    })),
    sn.truncated && h("p", { class: "muted small" }, `Cut at ${sn.lines.length} lines.`));
}

// aroundFocus is a snippet cut to its focus (a repeated run) and a few
// lines around it: the run is what is compared, not the functions.
function aroundFocus(sn) {
  const f = sn.focus;
  if (!f || !f[0]) return sn;
  const from = Math.max(f[0] - 2, sn.start), to = Math.min(f[1] + 2, sn.start + sn.lines.length - 1);
  return { ...sn, start: from, lines: sn.lines.slice(from - sn.start, to - sn.start + 1), truncated: false };
}

// compareView shows places side by side; a line that is not in the other
// places (spaces aside) is marked as differing.
async function compareView(at, snip, span) {
  const sns = (await Promise.all(at.map(async (e) => (await api(snip(e, span))).snippet))).map(aroundFocus);
  const norm = (l) => l.trim().replace(/\s+/g, " ");
  const sets = sns.map((sn) => new Set(sn.lines.map(norm)));
  return h("div", { class: "compare" }, sns.map((sn, k) => {
    const differs = new Set();
    sn.lines.forEach((l, i) => {
      const t = norm(l);
      if (t && sets.some((set, j) => j !== k && !set.has(t))) differs.add(i);
    });
    return codeView(sn, differs);
  }));
}

const MARK_LABEL = { by_design: "by design", review: "back for review", task: "task" };
const smellPrefs = { marked: false };
function smellsSection(list, intro, okay, t) {
  t = t || {};
  const active = list.filter((x) => x.status !== "gone");
  const gone = list.filter((x) => x.status === "gone");
  const quiet = (x) => x.mark && x.mark.state === "by_design";
  const marked = active.filter(quiet);
  if (!active.length && !gone.length) return h("p", { class: "okay section" }, okay);
  // evidence folded, each place opening to its code; a caller -> server
  // pair on two lines
  const evidence = (x) => evidenceList(x, t.snippet || null);
  // the evidence model (HAR-0102): a fact of the files, or a heuristic with
  // how sure it is; who found it is in the body
  const basis = (x) => x.basis && h("span", { class: `tag fbasis ${x.basis}`, title: x.basis === "heuristic"
    ? "A heuristic: read from names or thresholds, or judged without what lies outside; the percentage is how sure" : "A fact the files state" },
  x.basis === "heuristic" && x.confidence ? `heuristic · ${Math.round(x.confidence * 100)}%` : x.basis);
  // the recorded rule a boundary finding breaks (HAR-0104)
  const breaks = (x) => x.rule && h("p", { class: "small frule" }, h("b", {}, x.kind === "stale-knowledge" ? "Names: " : "Rule: "), h("code", {}, x.rule.line),
    h("span", { class: "muted" }, ` — ${x.rule.type || "memory"}${x.rule.scope ? ` at ${x.rule.scope}` : ""} (${x.rule.memory})`));
  const foundBy = (x) => x.source && x.source.provider && h("p", { class: "muted small fsource" }, "Found by ",
    x.source.provider === "harness" ? "harness, reading the files" : x.source.provider, x.source.tool ? [" (", h("code", {}, x.source.tool), ")"] : "");
  const chips = (x) => [
    basis(x),
    (x.status === "new" || x.status === "changed") && h("span", { class: `tag fstat ${x.status}`, title: `${x.status} since the last change of the findings` }, x.status),
    x.mark && h("span", { class: `tag fmark ${x.mark.state}` }, MARK_LABEL[x.mark.state], x.mark.task_id ? ` ${x.mark.task_id}` : ""),
  ];
  const where = (id) => (t.memberName ? t.memberName(id) : "this project");
  // the author's triage, in a panel under the finding
  const markForm = (x, panel) => {
    const reason = h("textarea", { rows: 2, required: true, placeholder: "Why it is intended" }, (x.mark && x.mark.reason) || "");
    return writeForm(panel, x.mark && x.mark.state === "review" ? "Still by design?" : "Mark by design",
      [reason, h("p", { class: "muted small" }, "It stays listed, marked, and hides unless marked ones are shown. The reason is recorded as a decision for agents working there; it comes back for review if it grows.")],
      "Mark by design", () => t.post("mark", { key: x.key, reason: reason.value }));
  };
  const taskForm = (x, panel) => {
    const pick = t.members && h("select", { "aria-label": "Member" }, t.members.map((m) => h("option", { value: m.id, selected: m.id === x.project ? "" : null }, m.name)));
    return writeForm(panel, "Make it a task?",
      [pick && h("label", {}, h("span", {}, "In the member"), pick),
        h("p", { class: "muted small" }, `An ARCH task${pick ? "" : " in this project"} with the finding's why, what could help and its evidence; the finding links to it and shows resolved once it is gone. It lives in harness, not in a backlog file.`)],
      "Make a task", () => t.post("task", { key: x.key, project: pick ? pick.value : "" }));
  };
  // the knowledge a finding is about (a broken rule, a stale decision):
  // the author may change or retire it instead of the code (HAR-0107)
  const level = (r) => r.scope && (r.scope.startsWith("workspace:") || r.scope === "user");
  const memURL = (r, what) => level(r) ? `/api/knowledge/memories/${enc(r.memory)}${what ? "/" + what : `?level=${enc(r.scope)}`}`
    : t.project && projectAPI(t.project, `memories/${enc(r.memory)}${what ? "/" + what : ""}`);
  const changeForm = (x, panel) => {
    const r = x.rule;
    const content = h("textarea", { rows: 3, "aria-label": "The corrected statement" });
    const reason = h("input", { type: "text", "aria-label": "Why", placeholder: "Why it changes (e.g. the code moved to …)" });
    api(memURL(r)).then((d) => { content.value = d.memory.content; }).catch(() => {});
    return writeForm(panel, `Change this ${r.type || "memory"}?`,
      [h("label", {}, h("span", {}, "Corrected statement"), content), h("label", {}, h("span", {}, "Why"), reason),
        h("p", { class: "muted small" }, "It supersedes the memory (kept in its history); agents get the corrected one from now on.")],
      "Change it", () => post(memURL(r, "supersede"), { ...(level(r) ? { level: r.scope } : {}), content: content.value, reason: reason.value }));
  };
  const retireForm = (x, panel) => {
    const r = x.rule;
    const reason = h("input", { type: "text", "aria-label": "Why", placeholder: "Why it no longer holds" });
    return writeForm(panel, `Retire this ${r.type || "memory"}?`, [h("label", {}, h("span", {}, "Why"), reason),
      h("p", { class: "muted small" }, "It stops applying now; nothing is deleted.")],
    "Retire it", () => post(memURL(r, "retire"), { ...(level(r) ? { level: r.scope } : {}), reason: reason.value }));
  };
  const triage = (x) => {
    if (!t.post || !x.key) return null;
    const panel = h("div", {});
    const m = x.mark;
    const btn = (label, form) => h("button", { class: "ghost small", type: "button", onclick: () => fill(panel, form(x, panel)) }, label);
    const knowledge = x.rule && x.rule.memory && (level(x.rule) || t.project);
    const unmark = (label) => h("button", { class: "ghost small", type: "button", onclick: async () => { await t.post("unmark", { key: x.key }); await afterWrite(); } }, label);
    return h("div", { class: "triage" },
      m && m.state === "review" && h("p", { class: "small freview" }, h("b", {}, "Back for review: "), `${m.review_reason}. It was marked by design: ${m.reason}`),
      m && m.state === "by_design" && h("p", { class: "small muted" }, h("b", {}, "By design: "), m.reason, m.proposed_by ? ` (proposed by ${m.proposed_by})` : ""),
      m && m.state === "task" && h("p", { class: "small muted" }, h("b", {}, `Task ${m.task_id}`), ` in ${where(m.task_project)}`, x.status === "gone" ? ": the finding is gone, resolved." : "."),
      x.status !== "gone" && h("div", { class: "tbtns" },
        (!m || m.state === "review") && btn(m ? "Still by design…" : "By design…", markForm),
        (!m || m.state !== "task") && btn(x.rule ? "Fix the code (task)…" : "Make a task…", taskForm),
        knowledge && btn(`Change the ${x.rule.type || "memory"}…`, changeForm),
        knowledge && x.kind === "stale-knowledge" && btn("Retire it…", retireForm),
        m && unmark(m.state === "task" ? "Unlink the task" : "Take the mark back")),
      panel);
  };
  const body = (x, sub) => h("div", { class: "sbody" },
    !sub && x.fix && h("p", { class: "sfix" }, h("b", {}, "Could help: "), x.fix),
    h("p", { class: "muted small swhy" }, x.why), breaks(x), evidence(x), foundBy(x), triage(x));
  const box = h("div", {});
  const draw = () => {
    const shown = active.filter((x) => smellPrefs.marked || !quiet(x));
    const snap = t.snapshot;
    fill(box,
      (t.post || marked.length > 0) && h("div", { class: "ftools small muted" },
        h("span", {}, snap ? `Since the last change (${ago(snap.taken_at)}): ${snap.added} new, ${snap.changed} changed, ${snap.gone} gone` : "Not synced yet: harness sync records them, so they show as new, changed or gone."),
        marked.length > 0 && h("label", { class: "fshow" }, h("input", { type: "checkbox", checked: smellPrefs.marked || null, onchange: (ev) => { smellPrefs.marked = ev.target.checked; draw(); } }),
          ` show ${marked.length} marked by design`)),
      shown.length ? h("ul", { class: "attn smells" }, smellGroups(shown).map(([kind, xs]) => {
        if (xs.length < 3) {
          return xs.map((x) => h("li", { class: "smell" + (quiet(x) ? " quiet" : "") }, h("details", { open: x.level === "problem" && !x.mark ? "" : null },
            h("summary", {}, levelPill(x.level), h("span", { class: "stitle" }, x.title), chips(x)), body(x))));
        }
        const fresh = xs.filter((x) => x.status === "new" || x.status === "changed").length;
        // one "could help" for the group when it is the same for all;
        // otherwise each says its own (a shared-code group's home)
        const sameFix = xs.every((x) => x.fix === xs[0].fix);
        const item = (x) => h("li", { class: quiet(x) ? "quiet" : "" }, h("details", {}, h("summary", {}, x.title, chips(x)), body(x, sameFix)));
        // the pill shows the group's worst level, whatever comes first (TASK-0039)
        const levels = SMELL_LEVELS.map(([lv]) => lv).filter((lv) => xs.some((x) => x.level === lv));
        return h("li", { class: "smell" }, h("details", {},
          h("summary", {}, levelPill(levels[0] || xs[0].level), h("span", { class: "stitle" }, h("b", {}, `${xs.length} × `), SMELL_KIND[kind] || kind),
            fresh > 0 && h("span", { class: "tag fstat new" }, `${fresh} new or changed`)),
          h("div", { class: "sbody" }, sameFix && xs[0].fix && h("p", { class: "sfix" }, h("b", {}, "Could help: "), xs[0].fix),
            h("p", { class: "muted small swhy" }, SMELL_WHY[kind] || xs[0].why),
            levels.length > 1 ? levelFilter(kind, xs, item) : smellList(xs, item))));
      })) : h("p", { class: "okay" }, marked.length ? `✓ Nothing open: ${marked.length} marked by design.` : okay),
      gone.length > 0 && h("details", { class: "evfold gonefold" }, h("summary", {}, `Gone (${gone.length})`),
        h("ul", { class: "evlist" }, gone.map((x) => h("li", {}, x.title,
          x.mark && x.mark.state === "task" ? h("span", { class: "tag fmark resolved" }, `resolved · ${x.mark.task_id}`) : null)))));
  };
  draw();
  return section("Smells", intro, box);
}

// levelFilter shows a group spanning levels (complexity: notes up to
// problems) with a chip per level and its count; a chip hides or shows its
// level, the choice kept per kind for this viewer. Only the list is drawn
// again, so the open group stays open (TASK-0039).
const SMELL_LEVELS = [["problem", "problem", "problems"], ["warn", "warning", "warnings"], ["info", "note", "notes"]];
function levelFilter(kind, xs, item) {
  const key = "smells.levels." + kind;
  const hidden = new Set(store.get(key, "").split(",").filter(Boolean));
  const chips = h("div", { class: "dupfilter lvfilter", role: "group", "aria-label": "Levels shown" });
  const list = h("div", {});
  function draw() {
    fill(chips, SMELL_LEVELS.filter(([lv]) => xs.some((x) => x.level === lv)).map(([lv, one, many]) => {
      const n = xs.filter((x) => x.level === lv).length;
      return h("button", {
        type: "button", class: "chip", "aria-pressed": String(!hidden.has(lv)), "data-level": lv,
        onclick: () => { hidden.has(lv) ? hidden.delete(lv) : hidden.add(lv); store.set(key, [...hidden].join(",")); draw(); },
      }, h("span", { class: `pill lv-${lv}` }, h("span", { class: "ico", "aria-hidden": "true" }, LEVEL_ICON[lv])), n === 1 ? one : many,
      h("span", { class: "count" }, n));
    }));
    const shown = xs.filter((x) => !hidden.has(x.level));
    fill(list, shown.length ? smellList(shown, item) : h("p", { class: "muted small" }, "Every level is hidden: choose one above."));
  }
  draw();
  return [chips, list];
}

// smellList lists a kind's findings in the server's order (most worth
// acting on first, TASK-0039): the first ones, the rest folded.
const SMELLS_SHOWN = 8;
function smellList(xs, item) {
  const rest = xs.slice(SMELLS_SHOWN);
  return h("ul", { class: "smellsub" }, xs.slice(0, SMELLS_SHOWN).map(item),
    rest.length > 0 && h("li", {}, h("details", { class: "evfold" }, h("summary", {}, `${rest.length} more, worth less`),
      h("ul", { class: "smellsub" }, rest.map(item)))));
}

// writeForm is a small triage form in panel: fields, a confirm button
// that runs send and refreshes, an error line, Cancel.
function writeForm(panel, title, fields, confirm, send) {
  const err = h("p", { class: "error", hidden: true });
  const ok = h("button", { class: "primary", type: "submit" }, confirm);
  return h("form", {
    class: "aform",
    onsubmit: async (ev) => {
      ev.preventDefault();
      ok.disabled = true;
      try { await send(); await afterWrite(); } catch (e) { err.textContent = e.message; err.hidden = false; ok.disabled = false; }
    },
  }, h("p", { class: "hint" }, title), fields, err,
  h("div", { class: "review" }, ok, h("button", { type: "button", class: "ghost", onclick: () => fill(panel) }, "Cancel")));
}

// smellGroups groups smells by kind, worst first, keeping their order.
function smellGroups(smells) {
  const groups = [];
  for (const x of smells) {
    const g = groups.find(([k]) => k === x.kind);
    if (g) g[1].push(x); else groups.push([x.kind, [x]]);
  }
  return groups;
}
// Read from the repository's files: solutions, projects in layers (what
// references what), packages, smells with evidence, and which workspace
// members use each package. The providers' raw overview stays below.
const ROLE_NOTE = { model: "models, contracts", data: "data access, state, API calls", logic: "business logic", api: "host, API", test: "tests",
  base: "shared basics: types, utils, theme", hooks: "React hooks", ui: "components", feature: "a feature", page: "pages, routing, app shell" };
async function viewArchitecture(p) {
  const [s, built] = await Promise.all([api(projectAPI(p, "structure")), api(projectAPI(p, "stack")).catch(() => null)]);
  const builtOn = builtOnSection(built);
  if (!s.known) {
    return h("div", {}, builtOn, h("p", { class: "empty section" }, "No .NET project (.csproj), go.mod, package.json or tsconfig found: this view reads .NET, Go and TypeScript/JavaScript repositories."), couplingSection(p));
  }
  const a = s.architecture;
  const noun = a.noun || "project", nouns = noun + "s";
  const ts = a.language === "typescript" || a.language === "go"; // references are imports
  const users = s.package_users || {};
  const all = a.projects;
  // names without the prefix every project shares (Betrian.TransportPlanner.)
  const parts = all.map((x) => x.name.split("."));
  let common = 0;
  while (parts.every((q) => q.length > common + 1 && q[common] === parts[0][common])) common++;
  const short = (x) => x.name.split(".").slice(common).join(".") || x.name;
  const byPath = Object.fromEntries(all.map((x) => [x.path, x]));
  const memberCount = (x) => new Set((users[x.package] || []).map((u) => u.member_id)).size;

  // summary and smells
  const sols = a.solutions.map((x) => x.path);
  const prod = all.filter((x) => x.kind !== "test");
  const layers = new Set(prod.map((x) => x.layer)).size;
  const head = h("div", { class: "card pad archsum" },
    h("div", {}, h("b", {}, `${prod.length} ${prod.length === 1 ? noun : nouns} in ${layers} layer${layers === 1 ? "" : "s"}`),
      all.length > prod.length && h("span", { class: "muted" }, ` · ${all.length - prod.length} test ${nouns}`)),
    a.language === "dotnet" ? h("div", { class: "muted small" }, sols.length ? `solution${sols.length === 1 ? "" : "s"}: ` : "no solution", sols.map((x, i) => [i ? ", " : "", h("code", {}, x)]),
      ` · graphify loads: ${s.input}`)
      : a.language === "go" ? h("div", { class: "muted small" }, "Go module: its packages; references are imports within the module (other modules are dependencies).")
      : h("div", { class: "muted small" }, noun === "folder"
        ? "TypeScript: the folders below the source root, each feature a unit of its own; references are imports (relative, tsconfig paths, Vite aliases)."
        : "TypeScript monorepo: the workspace packages; references are their dependencies on each other and imports of them."));
  const smells = smellsSection(s.findings || a.smells, `Inside this repository, each with the file that shows it. Roles come from the ${noun} names, so layering findings are a heuristic.`,
    `✓ No structural smells: no circles, no lower layer references a higher one, no production code depends on tests, every ${noun} is used.`,
    s.findings ? { snapshot: s.snapshot, project: p, post: (action, body) => post(projectAPI(p, `findings/${action}`), body), snippet: projectSnippet(p) }
      : { snippet: projectSnippet(p) });

  // the layer map: tests on top, then the layers from the highest down
  const side = h("aside", { class: "depside archside" });
  const map = h("div", { class: "archmap" });
  const svg = sv("svg", { class: "archlines", "aria-hidden": "true" });
  const nodes = {};
  let sel = null;
  const node = (x) => {
    const n = memberCount(x);
    const el = h("button", { class: `anode role-${x.role || "none"}`, title: x.name, onclick: () => select(sel === x.path ? null : x.path) },
      h("span", { class: "aname" }, short(x)),
      h("span", { class: "ameta" }, x.role ? h("span", { class: "arole" }, x.role) : x.kind === "entry" ? h("span", { class: "arole" }, "entry") : null,
        n > 0 && h("span", { class: "ausers", title: `${n} workspace member(s) use its package` }, `↗ ${n}`)));
    nodes[x.path] = el;
    return el;
  };
  const rows = [];
  const tests = all.filter((x) => x.kind === "test");
  if (tests.length) rows.push(["tests", tests]);
  // within a row, the less used first and the most used last, as on the
  // workspace's build map: lines into heavily used units come from one side
  const byUse = (x, y) => x.referenced_by.length - y.referenced_by.length || x.name.localeCompare(y.name);
  for (let l = a.layers - 1; l >= 0; l--) {
    const xs = all.filter((x) => x.kind !== "test" && x.layer === l).sort(byUse);
    if (xs.length) rows.push([`layer ${l}`, xs]);
  }
  fill(map, svg, rows.map(([label, xs]) => h("div", { class: "arow" }, h("div", { class: "alabel" }, label), h("div", { class: "anodes" }, xs.map(node)))));

  function draw() {
    const box = map.getBoundingClientRect();
    svg.setAttribute("width", box.width); svg.setAttribute("height", box.height);
    const lines = [];
    for (const x of all) {
      for (const r of x.references) {
        const from = nodes[x.path], to = nodes[r];
        if (!from || !to) continue;
        const hot = sel && (x.path === sel || r === sel);
        if (sel && !hot) continue;
        if (!sel && (x.kind === "test")) continue; // test edges only when selected
        const fa = from.getBoundingClientRect(), ta = to.getBoundingClientRect();
        const x1 = fa.left + fa.width / 2 - box.left, y1 = fa.bottom - box.top;
        const x2 = ta.left + ta.width / 2 - box.left, y2 = ta.top - box.top;
        const down = y2 > y1;
        const ya = down ? y1 : fa.top - box.top, yb = down ? y2 : ta.bottom - box.top;
        const m = (ya + yb) / 2;
        lines.push(sv("path", { d: `M${x1},${ya} C${x1},${m} ${x2},${m} ${x2},${yb}`,
          class: "aline" + (hot ? (x.path === sel ? " out" : " in") : "") }));
      }
    }
    fill(svg, sv("defs", {}, ["", " out", " in"].map((c) => sv("marker", { id: "ah" + c.trim(), viewBox: "0 0 8 8", refX: "7", refY: "4", markerWidth: "7", markerHeight: "7", orient: "auto" },
      sv("path", { d: "M0,0 L8,4 L0,8 z", class: "ahead" + c })))), lines.map((l) => { l.setAttribute("marker-end", `url(#ah${l.getAttribute("class").replace("aline", "").trim()})`); return l; }));
  }
  function select(path) {
    sel = path;
    const x = path && byPath[path];
    for (const [k, el] of Object.entries(nodes)) {
      el.classList.toggle("sel", k === path);
      el.classList.toggle("rel-out", !!x && x.references.includes(k));
      el.classList.toggle("rel-in", !!x && x.referenced_by.includes(k));
      el.classList.toggle("dim", !!x && k !== path && !x.references.includes(k) && !x.referenced_by.includes(k));
    }
    draw();
    if (!x) return fill(side, h("p", { class: "hint" }, `Select a ${noun} to see what it ${ts ? "imports" : "references"}, what ${ts ? "imports" : "references"} it, its packages and who uses it. Lines point from a ${noun} to the ${nouns} it ${ts ? "imports" : "references"}.`));
    const list = (paths) => paths.length ? h("ul", {}, paths.map((q) => h("li", {}, h("a", { href: "#", onclick: (ev) => { ev.preventDefault(); select(q); } }, byPath[q] ? short(byPath[q]) : q)))) : h("p", { class: "muted small" }, "none");
    const us = users[x.package] || [];
    const byMember = {};
    for (const u of us) (byMember[u.member] = byMember[u.member] || []).push(u.project.split("/").pop().replace(/\.csproj$/, ""));
    fill(side,
      h("h4", {}, x.name), h("p", { class: "muted small" }, h("code", {}, x.path)),
      h("p", { class: "small" }, [x.kind, x.role && x.role !== "test" && `${x.role} (${ROLE_NOTE[x.role]}, from its name)`, `layer ${x.layer}`].filter(Boolean).join(" · ")),
      x.package && h("p", { class: "small" }, "publishes ", h("code", {}, x.package)),
      h("h5", {}, ts ? "Imports" : "References"), list(x.references),
      h("h5", {}, ts ? "Imported by" : "Referenced by"), list(x.referenced_by),
      x.package && [h("h5", {}, "Used in the workspace"),
        us.length ? h("ul", {}, Object.entries(byMember).map(([m, ps]) => h("li", {}, h("b", {}, m), h("div", { class: "muted small" }, ps.join(", ")))))
          : h("p", { class: "muted small" }, s.workspaces ? "by no other member" : "not in a workspace")],
      h("h5", {}, `${a.language === "go" ? "Modules" : a.language === "typescript" ? "npm packages" : "Packages"} (${x.packages.length})`),
      x.packages.length ? h("ul", { class: "pkgs" }, x.packages.map((q) => h("li", {}, q.name, q.version && h("span", { class: "muted" }, " " + q.version)))) : h("p", { class: "muted small" }, "none"),
      a.language === "dotnet" && x.solutions.length === 0 && h("p", { class: "flag warn" }, "in no solution"));
  }
  select(null);
  const layout = h("div", { class: "deplayout archlayout" }, h("div", { class: "depwrap archwrap" }, map), side);
  requestAnimationFrame(draw);
  if (window.ResizeObserver) new ResizeObserver(() => draw()).observe(map);
  return h("div", {}, builtOn, head, smells, complexitySection(p),
    section("Layers", `${noun[0].toUpperCase() + nouns.slice(1)} by what they ${ts ? "import" : "reference"}: layer 0 ${ts ? "imports" : "references"} no other ${noun} here, each layer above ${ts ? "imports" : "references"} the ones below; ${nouns} in a circle share a layer. Tests sit on top.`, layout),
    couplingSection(p));
}

// filePath shows a path wrapping at its slashes: the folder dimmed, the
// file name bright.
function filePath(p) {
  const i = p.lastIndexOf("/");
  return h("code", { class: "fpath", title: p }, i >= 0 && h("span", { class: "muted" }, p.slice(0, i + 1).replace(/\//g, "/\u200b")), p.slice(i + 1));
}

// couplingSection (HAR-0105): what changes most and what changes together,
// from the Git history; loaded after the rest (partners start providers).
// With uncommitted changes, the usual partners they leave out come first.
function couplingSection(p) {
  const body = h("div", {}, h("p", { class: "empty" }, "Reading the history…"));
  const pct = (x) => `${Math.round(x * 100)}%`;
  const partner = (q) => h("span", { class: "tag cpart", title: `${q.path}: ${q.together} commits together, ${pct(q.share)} of its commits (${q.provider}, ${q.tool})` },
    h("span", { class: "cname" }, q.path.split("/").pop()), h("span", { class: "cpct" }, pct(q.share)));
  api(projectAPI(p, "coupling"), { "X-Harness-UI": "1" }).then((c) => {
    const miss = c.missing || [];
    fill(body,
      miss.length > 0 && h("div", { class: "card pad cmiss" },
        h("p", {}, h("b", {}, `Your uncommitted change leaves out ${miss.length} file(s) that usually change with it`), h("span", { class: "muted" }, " (a reminder, not a rule)")),
        miss.map((m) => h("p", { class: "cline" }, h("code", {}, m.partner.path), " usually changes with ", h("code", {}, m.changed),
          h("span", { class: "muted" }, ` (${m.partner.together} commits together, ${pct(m.partner.share)} of its commits)`)))),
      c.changed && !miss.length && h("p", { class: "okay" }, `✓ Your uncommitted change (${c.changed.length} file(s)) leaves out none of their usual partners.`),
      (c.hotspots || []).length ? h("table", { class: "hot" },
        h("thead", {}, h("tr", {}, h("th", {}, "file"), h("th", { class: "n" }, "commits"), h("th", {}, "changes together with"))),
        h("tbody", {}, c.hotspots.map((x) => h("tr", {}, h("td", { class: "cfile" }, filePath(x.path)), h("td", { class: "n" }, x.commits),
          h("td", {}, (x.partners || []).length ? h("div", { class: "cparts" }, x.partners.map(partner)) : h("span", { class: "muted" }, c.providers ? "nothing often" : "–"))))))
        : h("p", { class: "empty" }, "No code file changed more than once in the window."),
      !c.providers && h("p", { class: "muted small" }, "No provider offers change coupling (ripwire does), so partners are unknown; the counts are Git's."),
      (c.errors || []).length > 0 && h("p", { class: "muted small" }, c.errors.join("; ")));
  }).catch((e) => fill(body, h("p", { class: "error" }, e.message)));
  return section("Change coupling", "Code files changed most in the last 180 days (hotspots: harness's count from Git) and the files that change with them (co-change, from providers offering it; the percentage is the share of the file's commits). A partner that usually changes but was not touched may have been forgotten.", body);
}

// complexitySection shows how many functions have each cyclomatic
// complexity, per language, with the thresholds that make a note, a warning
// or a problem: the data to choose thresholds from. It loads on its own
// (counting reads every function), the rest of the page does not wait.
const LANGUAGE_NAME = { csharp: "C#", go: "Go", typescript: "TypeScript/JavaScript" };
function complexitySection(p) {
  const body = h("div", {}, h("div", { class: "loadbar", role: "progressbar", "aria-label": "Loading" }));
  api(projectAPI(p, "complexity")).then((c) => {
    const langs = c.languages || [];
    fill(body, langs.length ? langs.map((l) => complexityChart(l, langs.length > 1))
      : h("p", { class: "empty" }, "No functions in the languages [architecture.complexity] lists."));
  }).catch((e) => fill(body, h("p", { class: "error" }, e.message)));
  return section("Complexity distribution", "Functions by cyclomatic complexity, with the thresholds from [architecture.complexity] marked: where the tail starts shows where a threshold pays off. Lambdas and nested functions count toward the function around them.", body);
}

// complexityChart is one language's histogram: bins of five on a value
// axis, each bar shaded by the level its values start at, the thresholds
// as lines; counts label only the reported bars (the tail is the story),
// every bar has its tooltip, and the table holds every number.
const CX_LEVEL = { ok: "below the note threshold", info: "note", warn: "warning", problem: "problem" };
function complexityChart(l, titled) {
  const t = l.thresholds;
  const level = (v) => (v >= t.problem ? "problem" : v >= t.warn ? "warn" : v >= t.note ? "info" : "ok");
  const size = 5;
  const max = Math.max(...l.counts.map(([v]) => v), t.problem);
  const top = Math.min(Math.ceil(max / size) * size, 150); // longer tails fold into the last bin
  const bins = [];
  for (let lo = 1; lo <= top; lo += size) bins.push({ lo, hi: lo + size - 1, n: 0 });
  for (const [v, n] of l.counts) bins[Math.min(Math.floor((v - 1) / size), bins.length - 1)].n += n;
  const open = max > top;
  const count = (lv) => l.counts.filter(([v]) => level(v) === lv).reduce((s, [, n]) => s + n, 0);
  const range = (b, i) => (open && i === bins.length - 1 ? `${b.lo}+` : `${b.lo}–${b.hi}`);
  // geometry: one value axis, one count axis from zero
  // the top band (T) holds two rows of threshold labels above the counts on the tallest bars
  const W = 720, H = 204, L = 44, R = 12, T = 48, B = 26;
  const pw = W - L - R, ph = H - T - B, base = T + ph;
  const x = (v) => L + ((v - 1) / top) * pw;
  const peak = Math.max(1, ...bins.map((b) => b.n));
  const top4 = (x0, w, y0) => {
    const r = Math.min(4, w / 2, base - y0);
    return `M${x0},${base}V${y0 + r}Q${x0},${y0} ${x0 + r},${y0}H${x0 + w - r}Q${x0 + w},${y0} ${x0 + w},${y0 + r}V${base}Z`;
  };
  const bars = bins.map((b, i) => {
    if (!b.n) return null;
    const x0 = x(b.lo) + 1, w = Math.min(24, x(b.hi + 1) - x(b.lo) - 2);
    const y0 = base - Math.max(2, (b.n / peak) * ph);
    const lv = level(b.lo);
    return sv("g", { class: "cxbar" },
      sv("path", { class: `cx-${lv}`, d: top4(x0, w, y0) }),
      sv("rect", { class: "cxhit", x: x0 - 1, y: T, width: w + 2, height: ph }), // hover target taller than the bar
      sv("title", {}, `complexity ${range(b, i)}: ${b.n} function${b.n === 1 ? "" : "s"} (${CX_LEVEL[lv]})`),
      lv !== "ok" && sv("text", { class: "cxn", x: x0 + w / 2, y: y0 - 4, "text-anchor": "middle" }, b.n));
  });
  // thresholds: a line each, their labels on one baseline; a label goes to a
  // second row only when it would run into the one before it (11px text is
  // about 6.5px a character)
  const ends = [-Infinity, -Infinity];
  const marks = [["info", t.note], ["warn", t.warn], ["problem", t.problem]].filter(([, v]) => v <= top).map(([lv, v]) => {
    const xv = x(v), label = `${LEVEL_ICON[lv]} ${CX_LEVEL[lv]} ≥ ${v}`;
    const row = xv + 4 >= ends[0] + 6 ? 0 : 1;
    ends[row] = xv + 4 + label.length * 6.5;
    return sv("g", { class: `cxth cxth-${lv}` },
      sv("line", { x1: xv, x2: xv, y1: T - 18 - row * 12, y2: base }),
      sv("text", { x: xv + 4, y: T - 22 - row * 12 }, label));
  });
  const ticks = [1];
  for (let v = 11; v <= top; v += 10) ticks.push(v);
  const svg = sv("svg", { class: "cxchart", viewBox: `0 0 ${W} ${H}`, role: "img",
    "aria-label": `${l.members} functions by cyclomatic complexity: ${count("info")} notes, ${count("warn")} warnings, ${count("problem")} problems` },
  sv("line", { class: "cxgrid", x1: L, x2: W - R, y1: T, y2: T }),
  sv("line", { class: "cxaxis", x1: L, x2: W - R, y1: base, y2: base }),
  sv("text", { class: "cxtick", x: L - 6, y: T + 4, "text-anchor": "end" }, compact(peak)),
  sv("text", { class: "cxtick", x: L - 6, y: base, "text-anchor": "end" }, "0"),
  ticks.map((v) => sv("text", { class: "cxtick", x: x(v), y: base + 16, "text-anchor": "middle" }, v)),
  bars, marks);
  const reported = count("info") + count("warn") + count("problem");
  return h("div", { class: "cxlang" },
    titled && h("h4", {}, LANGUAGE_NAME[l.language] || l.language),
    h("p", { class: "small" }, `${l.members} function${l.members === 1 ? "" : "s"}; at these thresholds ${reported} reported: `,
      [["problem", "problem"], ["warn", "warning"], ["info", "note"]].map(([lv, word], i) =>
        [i ? ", " : "", h("span", { class: `pill lv-${lv}` }, h("span", { class: "ico", "aria-hidden": "true" }, LEVEL_ICON[lv])), `${count(lv)} ${word}${count(lv) === 1 ? "" : "s"}`]),
      l.measured > 0 && h("span", { class: "muted" }, ` · ${l.measured} measured by Microsoft.CodeAnalysis.Metrics`)),
    svg,
    h("details", { class: "cut" }, h("summary", { class: "muted small" }, "Table"),
      h("table", { class: "cxtable" }, h("thead", {}, h("tr", {}, h("th", {}, "complexity"), h("th", { class: "n" }, "functions"), h("th", {}, "level"))),
        h("tbody", {}, bins.map((b, i) => h("tr", {}, h("td", {}, range(b, i)), h("td", { class: "n" }, b.n), h("td", {}, CX_LEVEL[level(b.lo)])))))));
}

// indexedSection shows what a provider offering project.overview (as MCP
// project_overview) has indexed, on request: loading it starts providers.
function indexedSection(p, name) {
  const out = h("div", {});
  const btn = h("button", {
    onclick: async () => {
      btn.disabled = true;
      fill(out, h("p", { class: "empty" }, "Asking the provider (starts it; can take a while)…"));
      try {
        const a = await api(projectAPI(p, "architecture"), { "X-Harness-UI": "1" });
        const sum = (a.structure || {})[name];
        fill(out,
          a.stale && h("p", { class: "detail" }, a.stale),
          (a.withheld || []).includes(name) && h("p", { class: "detail" }, "Withheld until the next sync (index from another branch)."),
          (a.errors || []).filter((e) => e.includes(name)).map((e) => h("p", { class: "error" }, e)),
          sum ? [overviewBars(sum), h("details", { class: "cut" }, h("summary", { class: "muted small" }, "Raw"), tree(sum, 0))]
            : h("p", { class: "empty" }, "It returned no overview."));
      } catch (e) {
        fill(out, h("p", { class: "error" }, e.message));
      } finally {
        btn.disabled = false;
      }
    },
  }, "Load");
  return h("details", { class: "more indexed" }, h("summary", {}, "What it has indexed"),
    h("p", { class: "hint" }, "Counts of what the provider found in the code: a check that it indexed the project, not the architecture (see Architecture). Loading starts it."),
    h("div", { class: "filters" }, btn), out);
}

// overviewBars shows the lists in a provider summary that are counts by
// kind ([{type|label|name, count}]) as bars; the rest stays in the raw view.
function overviewBars(sum) {
  const lists = Object.entries(sum || {}).filter(([, v]) => Array.isArray(v) && v.length && v.every((x) => x && typeof x === "object" && typeof x.count === "number"));
  if (!lists.length) return null;
  const word = (k) => ({ edge_types: "Relations in the code", node_labels: "Things in the code", languages: "Languages" }[k] || k.replaceAll("_", " "));
  return h("div", { class: "obars" }, lists.map(([k, v]) => {
    const max = Math.max(...v.map((x) => x.count));
    return h("div", {}, h("h5", {}, word(k)), h("div", { class: "card bars" }, v.slice(0, 12).flatMap((x) => {
      const name = String(x.type || x.label || x.name || x.language || "?").toLowerCase().replaceAll("_", " ");
      const f = h("div", { class: "fill" }); f.style.setProperty("--w", `${(100 * x.count) / max}%`);
      return [h("span", { class: "k" }, name), h("div", { class: "meter" }, h("div", { class: "track" }, f), h("span", { class: "num" }, x.count.toLocaleString()))];
    })));
  }));
}

// tree renders a provider's summary: objects as key/value lists, lists cut
// to their first 20 items, depth-limited.
function tree(v, depth) {
  if (v == null) return h("span", { class: "muted" }, "—");
  if (typeof v !== "object") return h("span", {}, String(v));
  if (depth >= 3) return h("code", { class: "small" }, JSON.stringify(v).slice(0, 200));
  if (Array.isArray(v)) {
    if (!v.length) return h("span", { class: "muted" }, "none");
    return h("ul", { class: "tree" }, v.slice(0, 20).map((x) => h("li", {}, tree(x, depth + 1))),
      v.length > 20 && h("li", { class: "muted" }, `… ${v.length - 20} more`));
  }
  return h("dl", { class: "kv tree" }, Object.entries(v).map(([k, x]) => [h("dt", {}, k), h("dd", {}, tree(x, depth + 1))]));
}

// checkParts splits a passing check's detail into its key value, shown
// beside the name, and the rest (paths), shown below it.
function checkParts(c) {
  const d = c.detail || "";
  let m;
  switch (c.name) {
    case "binary": return (m = /^(\S+) \((.*)\)$/.exec(d)) ? [m[1] === "dev" ? "dev build" : m[1].replace(/^v0\.0\.0-\d{14}-([0-9a-f]{7})[0-9a-f]*/, "$1"), [m[2].replaceAll(", ", " · "), m[1] !== "dev" && m[1]]] : ["", [d]];
    case "git": return (m = /^(.*) — (.+) at ([0-9a-f]+)$/.exec(d)) ? [`${m[2]} @ ${m[3].slice(0, 7)}`, [m[1]]] : ["", [d]];
    case "project": return (m = /^(\S+), (\d+) worktree\(s\)$/.exec(d)) ? [`${m[2]} worktree${m[2] === "1" ? "" : "s"}`, [m[1]]] : ["", [d]];
    case "storage": return (m = /^(.*) is (writable.*)$/.exec(d)) ? [m[2], [m[1]]] : ["", [d]];
    case "database": return (m = /^(.*), schema (.+)$/.exec(d)) ? [`schema ${m[2]}`, [m[1]]] : ["", [d]];
    case "permissions": return (m = /is (.+)$/.exec(d)) ? [m[1], []] : ["", [d]];
    case "config": {
      const parts = d.split("; ");
      return [parts.map((x) => x.split(" ")[0]).join(" + "), parts];
    }
  }
  if (/scope: /.test(d)) {
    const parts = d.split("; ");
    return [parts.map((x) => x.split(" scope")[0]).join(" + ") + " scope", parts];
  }
  if ((m = /^configured: (.*?)(?: \((.+)\))?$/.exec(d))) return [m[2] ? `configured · ${m[2]}` : "configured", [m[1]]];
  return ["", [d]];
}

function healthReport(r) {
  const checks = r.checks || [];
  const n = (lv) => checks.filter((c) => c.level === lv).length;
  const st = r.status || {};
  const db = st.database || {};
  const sync = st.sync || {};
  // one summary line; the database and the index are named only when
  // something is wrong with them
  const issues = [
    n("fail") > 0 && `${n("fail")} failing check(s)`,
    n("warn") > 0 && `${n("warn")} warning(s)`,
    !db.exists ? "no database" : db.schema_version !== db.schema_latest && `schema ${db.schema_version} of ${db.schema_latest}`,
    db.error,
    sync.stale ? `index stale: ${sync.stale}` : !sync.synced_at && "never synced",
  ].filter(Boolean);
  const out = h("div", {},
    h("div", { class: "card pad hsum " + (r.healthy ? "ok" : "bad") },
      h("span", { class: "hico", "aria-hidden": "true" }, r.healthy ? "✓" : "✕"),
      h("div", {}, h("b", {}, r.healthy ? (issues.length ? "Healthy, with notes" : "Healthy") : "Needs a fix"),
        h("div", { class: "muted" }, [
          issues.length ? issues.join(" · ") : `${checks.length} checks pass`,
          !issues.length && db.schema_version && `schema ${db.schema_version}`,
          sync.synced_at && `synced ${ago(sync.synced_at)}`,
          r.probed ? "providers checked" : "providers not checked",
        ].filter(Boolean).join(" · ")))));
  // Checks by area; what is wrong first, with its fix, then the passing
  // ones as a compact grid (the detail on hover and a second line).
  const AREA = { binary: "Installation", config: "Installation", permissions: "Installation",
    git: "Project", project: "Project", storage: "Project", database: "Project", "claude-code": "Agents' clients", codex: "Agents' clients" };
  const CHECK_ICON = { ok: "✓", warn: "▲", fail: "✕" };
  const check = (c) => {
    const [value, lines] = c.level === "ok" ? checkParts(c) : ["", [c.detail]];
    return h("div", { class: `chk lv-${c.level}` },
      h("span", { class: "cico", "aria-hidden": "true" }, CHECK_ICON[c.level] || "•"),
      h("div", { class: "cbody" },
        h("div", { class: "chead" }, h("span", { class: "cname" }, c.name, c.level !== "ok" && h("span", { class: "sr" }, ` (${c.level})`)), value && h("span", { class: "cvalue" }, value)),
        lines.filter(Boolean).map((x) => h("div", { class: "cdetail" }, x)), c.fix && h("div", { class: "fix" }, h("b", {}, "Fix: "), c.fix)));
  };
  const bad = checks.filter((c) => c.level !== "ok");
  const stacks = [["Installation", "Agents' clients"], ["Project"], ["Code providers"]];
  out.append(section("Checks", "What 'harness doctor' verifies. Failures mean harness cannot work correctly here; warnings mean something is missing.",
    bad.length > 0 && h("div", { class: "chkgrid wide" }, bad.map(check)),
    h("div", { class: "chkareas" }, stacks.map((st) => h("div", { class: "chkstack" }, st.map((ar) => {
      const cs = checks.filter((c) => c.level === "ok" && (AREA[c.name] || "Code providers") === ar);
      return cs.length > 0 && h("div", { class: "chkarea" }, h("h4", {}, ar, h("span", { class: "count" }, `✓ ${cs.length} pass`)), h("div", { class: "chkgrid" }, cs.map(check)));
    }))))));
  const git = st.git;
  out.append(h("details", { class: "card pad section more" }, h("summary", {}, "Paths and identifiers"),
    h("div", { class: "cols" },
      kv([
        ["Project id", st.project && h("code", {}, st.project.id)],
        ["Copied from", st.project && st.project.copied_from],
        ["Root", h("code", {}, st.project_root)],
        ["State root", st.state_root !== st.project_root && h("code", {}, st.state_root)],
        ["Database", h("code", {}, db.path)],
        ["Config", [st.config && st.config.global && h("div", {}, "global ", h("code", {}, st.config.global)),
          st.config && st.config.project && h("div", {}, "project ", h("code", {}, st.config.project))]],
      ]),
      git ? kv([
        ["Branch", git.detached ? "detached HEAD" : git.branch],
        ["Commit", h("code", {}, shortCommit(git.commit))],
        ["Worktree", git.linked_worktree ? "linked" : "main"],
        ["Worktrees", (st.worktrees || []).map((w) => h("div", {}, h("code", {}, w.path), w.branch ? ` (${w.branch})` : ""))],
        ["Providers", (st.providers || []).join(", ") || "none enabled"],
      ]) : h("p", { class: "error" }, st.git_error || "not a Git repository"))));
  const runs = sync.runs || [];
  // Index freshness per provider, from the runs (newest first).
  const fresh = {};
  for (const x of runs) {
    for (const pr of x.providers || []) {
      const f = fresh[pr.name] || (fresh[pr.name] = { last: x, refreshed: null, failed: null });
      if (pr.status === "refreshed" && !f.refreshed) f.refreshed = { at: x.started_at, head: x.head_commit };
      if (pr.status === "failed" && !f.failed) f.failed = { at: x.started_at, detail: pr.detail };
    }
  }
  const names = Object.keys(fresh).sort();
  if (names.length) {
    out.append(section("Index freshness", `Per provider over the last ${runs.length} syncs: when it last refreshed its index and its last failure.`,
      h("div", { class: "card" }, h("table", {},
        h("thead", {}, h("tr", {}, ["provider", "last outcome", "last refreshed", "at commit", "last failure"].map((c) => h("th", {}, c)))),
        h("tbody", {}, names.map((n) => {
          const f = fresh[n];
          const last = (f.last.providers || []).find((pr) => pr.name === n);
          return h("tr", {},
            h("td", {}, n),
            h("td", {}, last.status === "failed" ? h("span", { class: "flag bad" }, "failed") : last.status.replace("_", " ")),
            h("td", {}, f.refreshed ? ago(f.refreshed.at) : h("span", { class: "muted" }, "not in these syncs")),
            h("td", { class: "mono" }, f.refreshed ? (f.refreshed.head || "").slice(0, 7) : ""),
            h("td", { class: "wrap" }, f.failed ? [ago(f.failed.at), f.failed.detail && h("div", { class: "evidence" }, f.failed.detail)] : h("span", { class: "muted" }, "none")));
        }))))));
  }
  if (runs.length) {
    const SYNC_ICON = { ok: "✓", failed: "✕" };
    out.append(section("Recent syncs", "What each sync did: the commits it moved across, the files, and what each provider did with its index.",
      h("ol", { class: "card synclist" }, runs.map((x) => h("li", { class: `sync st-${x.status}` },
        h("span", { class: "cico", "aria-hidden": "true" }, SYNC_ICON[x.status] || "▲"),
        h("div", { class: "sbody" },
          h("div", { class: "sline" },
            h("b", { title: x.started_at }, ago(x.started_at)), h("span", { class: "tag" }, x.mode), x.status !== "ok" && h("span", { class: "flag " + (x.status === "failed" ? "bad" : "warn") }, x.status),
            h("span", { class: "mono muted" }, x.base_commit ? `${x.base_commit.slice(0, 7)} → ${(x.head_commit || "").slice(0, 7)}` : (x.head_commit || "").slice(0, 7)),
            h("span", { class: "delta", title: "files added, changed, removed" }, h("span", { class: x.added ? "add" : "zero" }, `+${x.added}`), h("span", { class: x.changed ? "chg" : "zero" }, `~${x.changed}`), h("span", { class: x.removed ? "del" : "zero" }, `−${x.removed}`))),
          h("div", { class: "muted small" }, (x.providers || []).map((pr) => `${pr.name} ${pr.status.replace("_", " ")}`).join(" · "),
            (x.error || x.reason) && h("div", {}, x.error || x.reason))))))));
  }
  return out;
}

// --- knowledge ---
async function viewKnowledge(p) {
  const k = state.knowledge;
  const wrap = h("div", {});
  const list = h("div", {});
  const summary = h("div", { class: "hint" });
  const scopeSel = h("select", { "aria-label": "Scope", onchange: (ev) => { k.scope = ev.target.value; draw(); } });
  let memories = [];
  let seq = 0;
  async function load() {
    const n = ++seq;
    if (k.types.size === 0) {
      memories = [];
      return draw("Pick at least one type.");
    }
    const qs = new URLSearchParams({ type: [...k.types].join(","), limit: "500", levels: "1" });
    if (k.q) qs.set("q", k.q);
    if (k.inactive) qs.set("inactive", "1");
    try {
      const res = await api(projectAPI(p, `memories?${qs}`));
      if (n !== seq) return;
      memories = res.memories || [];
      draw();
    } catch (e) {
      fill(list, h("p", { class: "error" }, e.message));
    }
  }
  function draw(empty) {
    const scopes = [...new Set(memories.map((m) => m.scope))].sort((a, b) => (a === "project" ? -1 : b === "project" ? 1 : a.localeCompare(b)));
    if (k.scope && !scopes.includes(k.scope)) k.scope = "";
    fill(scopeSel, h("option", { value: "" }, `all scopes (${scopes.length})`),
      scopes.map((s) => h("option", { value: s, selected: s === k.scope }, s)));
    const shown = memories.filter((m) => !k.scope || m.scope === k.scope);
    summary.textContent = `${shown.length} memor${shown.length === 1 ? "y" : "ies"}${k.scope ? " in " + k.scope : ""}`;
    if (!shown.length) return fill(list, h("p", { class: "empty" }, empty || "No memories match."));
    fill(list, ...MEMORY_TYPES.filter((t) => shown.some((m) => m.type === t)).map((t) => {
      const group = shown.filter((m) => m.type === t);
      return h("section", { class: `mgroup ty-${t}` },
        h("h3", {}, h("span", { class: "dot", "aria-hidden": "true" }), MEMORY_GROUPS[t], h("span", { class: "count" }, group.length)),
        h("div", { class: "mems" }, group.map((m) => memoryCard(p, m))));
    }));
  }
  const chips = MEMORY_TYPES.map((t) => h("button", {
    class: `chip ty-${t}`, "aria-pressed": String(k.types.has(t)),
    onclick: (ev) => {
      k.types.has(t) ? k.types.delete(t) : k.types.add(t);
      ev.currentTarget.setAttribute("aria-pressed", String(k.types.has(t)));
      load();
    },
  }, h("span", { class: "dot", "aria-hidden": "true" }), MEMORY_GROUPS[t]));
  let timer;
  const search = h("input", {
    type: "search", placeholder: "Search memories", value: k.q, "aria-label": "Search memories",
    oninput: (ev) => { k.q = ev.target.value.trim(); clearTimeout(timer); timer = setTimeout(load, 250); },
  });
  const inactive = h("label", { class: "check" },
    h("input", { type: "checkbox", checked: k.inactive, onchange: (ev) => { k.inactive = ev.target.checked; load(); } }),
    "include superseded and expired");
  const applies = appliesPanel(p);
  wrap.append(applies, h("div", { class: "filters" }, chips), h("div", { class: "filters" }, search, scopeSel, inactive, summary), list);
  list.append(h("p", { class: "empty" }, "Loading…"));
  await Promise.all([load(), applies.ready]);
  return wrap;
}

// appliesPanel answers "what is an agent told about this area?": the
// goals, constraints and decisions that apply to a path, subsystem or
// branch on the project's current branch (as goals_current,
// constraints_current and decisions_for answer it).
function appliesPanel(p) {
  const out = h("div", { class: "applies-out" });
  const input = h("input", { type: "search", placeholder: "path:internal/gui, subsystem:sync, branch:m16 or a path", "aria-label": "Scope" });
  const it = route().item;
  input.value = (it && it !== "memories" && it !== "inbox" ? it : "") || state.knowledge.applies || ""; // #/p/<project>/knowledge/<scope> links here
  async function show() {
    let scope = input.value.trim();
    if (scope && !/^(path|subsystem|branch|project)(:|$)/.test(scope)) scope = "path:" + scope;
    state.knowledge.applies = input.value.trim();
    if (!scope) return fill(out);
    fill(out, h("p", { class: "empty" }, "Loading…"));
    try {
      const a = await api(projectAPI(p, `applies?scope=${encodeURIComponent(scope)}`));
      const group = (title, ms) => h("div", { class: "agroup" }, h("h4", {}, `${title} `, h("span", { class: "count" }, ms.length)),
        ms.length ? h("div", { class: "mems" }, ms.map((m) => memoryCard(p, m))) : h("p", { class: "empty" }, "none"));
      fill(out, 
        h("p", { class: "hint" }, `What an agent is told about ${a.scope}${a.branch ? " on branch " + a.branch : ""}:`),
        group("Goals", a.goals || []), group("Constraints", a.constraints || []), group("Decisions, conventions and warnings", a.decisions || []));
    } catch (e) {
      fill(out, h("p", { class: "error" }, e.message));
    }
  }
  const form = h("form", { class: "filters", onsubmit: (ev) => { ev.preventDefault(); show(); } },
    h("b", {}, "What applies here?"), input, h("button", { type: "submit" }, "Show"),
    h("button", { type: "button", class: "ghost", onclick: () => { input.value = ""; show(); } }, "Clear"));
  const panel = h("section", { class: "applies card pad" }, form, out);
  panel.ready = input.value ? show() : Promise.resolve();
  return panel;
}

// isLevel: a memory of a level wider than the project (HAR-0143).
const isLevel = (scope) => scope === "user" || (scope || "").startsWith("workspace:");
const levelWord = (scope) => scope === "user" ? "all your projects" : `workspace ${wtitle(scope.slice("workspace:".length))}`;

function memoryCard(p, m) {
  const inactive = !!m.superseded_by || (m.valid_until && new Date(m.valid_until) < new Date());
  const panel = h("div", { class: "mpanel" });
  const level = isLevel(m.scope) ? m.scope : "";
  // a level's memory is read and changed at its level
  const url = (what) => level ? `/api/knowledge/memories/${enc(m.id)}${what}${what ? "" : "?level=" + enc(level)}` : projectAPI(p, `memories/${encodeURIComponent(m.id)}${what}`);
  const send = (what, v) => post(url(what), level ? { ...v, level } : v);
  async function openHistory(force) {
    if (panel.dataset.open === "history" && !force) { delete panel.dataset.open; state.history.delete(m.id); return fill(panel); }
    panel.dataset.open = "history";
    state.history.add(m.id);
    fill(panel, h("p", { class: "empty" }, "Loading…"));
    try { fill(panel, historyView(await api(url("")))); } catch (e) { fill(panel, h("p", { class: "error" }, e.message)); }
  }
  const actions = h("div", { class: "review" },
    !inactive && h("button", {
      onclick: () => fill(panel, actionForm({
        title: "Correct this memory: the new statement replaces it (same type and scope); the old one stays in its history.",
        fields: [{ name: "content", label: "Corrected statement", multiline: true, value: m.content, required: true },
          { name: "reason", label: "Why", required: true }],
        submit: "Save correction",
        onSubmit: (v) => send("/supersede", v).then(afterWrite),
        onCancel: () => fill(panel),
      })),
    }, "Correct"),
    !inactive && h("button", {
      onclick: () => fill(panel, actionForm({
        title: "Retire this memory: it stops applying now. Nothing is deleted; it stays readable with its history.",
        fields: [{ name: "reason", label: "Why it no longer applies", required: true }],
        submit: "Retire", danger: true,
        onSubmit: (v) => send("/retire", v).then(afterWrite),
        onCancel: () => fill(panel),
      })),
    }, "Retire"),
    !inactive && !level && p && m.scope === "project" && h("button", { title: "Record it for a workspace or for all your projects", onclick: () => fill(panel, promoteForm(p, m, panel)) }, "Promote"),
    h("button", { class: "ghost", onclick: () => openHistory() }, "History"));
  if (state.history.has(m.id)) openHistory(true); // it was open before a redraw
  return h("article", { class: `mem ty-${m.type}` + (inactive ? " inactive" : "") },
    h("div", { class: "content" }, m.content),
    h("div", { class: "prov" },
      h("span", { class: "scope" + (level ? " lvl" : "") }, level ? (m.scope === "user" ? "you · all your projects" : `workspace · ${wtitle(m.scope.slice(10))}`) : m.scope),
      m.source_ref && h("span", {}, h("b", {}, "source "), m.source_ref),
      h("span", {}, h("b", {}, m.source_type === "user" ? "stated by user" : "recorded by agent"), m.recorded_by ? ` · ${m.recorded_by}` : ""),
      h("span", { title: m.created_at }, ago(m.created_at)),
      m.confidence != null && h("span", {}, `confidence ${m.confidence}`),
      m.valid_until && h("span", {}, `valid until ${m.valid_until.slice(0, 10)}`),
      h("code", { class: "id" }, m.id)),
    (m.overrides || []).length > 0 && h("div", { class: "overrides" }, "Takes precedence here over ", m.overrides.join(", ")),
    (m.overridden_in || []).length > 0 && h("div", { class: "overrides" }, "Overridden by their own memory in: ", m.overridden_in.join(", ")),
    m.superseded_by && h("div", { class: "superseded" }, "Superseded by ",
      h("a", { href: "#", onclick: (ev) => { ev.preventDefault(); openHistory(true); }, title: "Show the versions, newest included" }, m.superseded_by),
      m.supersede_reason ? ": " + m.supersede_reason : ""),
    actions, panel);
}

// promoteForm records a project memory for one of the project's
// workspaces or for all the user's projects, after showing where it
// already holds (a dry run).
function promoteForm(p, m, panel) {
  const targets = [...(p.workspaces || []).map((w) => [`workspace:${w}`, `Workspace ${wtitle(w)}`]), ["user", "All your projects"]];
  const sel = h("select", { "aria-label": "Promote to" }, targets.map(([v, l]) => h("option", { value: v }, l)));
  const err = h("p", { class: "error", hidden: true });
  return h("form", {
    class: "aform",
    onsubmit: async (ev) => {
      ev.preventDefault();
      try {
        const to = sel.value;
        const dry = await post(projectAPI(p, `memories/${enc(m.id)}/promote`), { to, dry_run: true });
        const holds = dry.holds_in || [];
        fill(panel, reviewPanel({
          title: `Promote to ${levelWord(to)}?`,
          lines: [m.content, holds.length ? `Already holds in: ${holds.map((x) => x.project).join(", ")} (their own copies keep precedence there until retired)` : "No other project records it yet.",
            `This project's copy is retired; the ${to === "user" ? "user" : "workspace"} one applies here from now on.`],
          confirm: "Promote", note: "Recorded as stated by you.",
          onBack: () => fill(panel, promoteForm(p, m, panel)),
          onConfirm: async () => {
            const res = await post(projectAPI(p, `memories/${enc(m.id)}/promote`), { to });
            if (res.status === "needs_decision") throw new Error(`That level already holds something similar: ${(res.similar_at_level || []).join("; ")}`);
            await afterWrite();
          },
        }));
      } catch (e) { err.textContent = e.message; err.hidden = false; }
    },
  }, h("p", { class: "hint" }, "Promote: record this for more than this project. Where should it apply?"), h("label", {}, h("span", {}, "Apply to"), sel), err,
  h("div", { class: "review" }, h("button", { class: "primary", type: "submit" }, "Review"), h("button", { type: "button", class: "ghost", onclick: () => fill(panel) }, "Cancel")));
}

// levelKnowledge is a level's memories (the user's, or a workspace's)
// with the projects that override each, and a form to add one.
async function levelKnowledge(level, intro) {
  const res = await api(`/api/knowledge?level=${enc(level)}`);
  const ms = res.memories || [];
  const box = h("div", {});
  const add = h("button", { class: "primary", onclick: () => fill(box, actionForm({
    title: `Add for ${levelWord(level)}: it applies to every one of them unless a project states otherwise.`,
    fields: [{ name: "type", label: "Type (decision, convention, constraint, warning, goal, fact)", value: "convention", required: true },
      { name: "content", label: "Statement", multiline: true, required: true }, { name: "source_ref", label: "Source (optional)" }],
    submit: "Add",
    onSubmit: async (v) => {
      const r = await post("/api/knowledge/memories", { ...v, level });
      if (r.status === "needs_decision") throw new Error(`Something similar is already recorded: ${(r.similar || []).map((x) => x.content).join("; ")}`);
      await afterWrite();
    },
    onCancel: () => fill(box),
  })) }, "Add");
  const groups = MEMORY_TYPES.filter((t) => ms.some((m) => m.type === t)).map((t) => {
    const g = ms.filter((m) => m.type === t);
    return h("section", { class: `mgroup ty-${t}` }, h("h3", {}, h("span", { class: "dot", "aria-hidden": "true" }), MEMORY_GROUPS[t], h("span", { class: "count" }, g.length)),
      h("div", { class: "mems" }, g.map((m) => memoryCard(null, m))));
  });
  return h("div", {}, h("p", { class: "hint" }, intro), h("div", { class: "review" }, add), box,
    groups.length ? groups : h("p", { class: "empty section" }, "Nothing recorded yet. Agents add here with memory_put scope " + (level === "user" ? "user" : level) + ", or promote a project's memory from its Knowledge view."));
}

function historyView(d) {
  return h("div", { class: "history" },
    (d.history || []).length > 1 && [h("h4", {}, "Versions (oldest first)"),
      h("ol", {}, d.history.map((v) => h("li", { class: v.id === d.memory.id ? "this" : "" }, v.content, " ", h("code", { class: "muted" }, v.id))))],
    h("h4", {}, "Audit trail"),
    h("ul", { class: "events" }, (d.events || []).map((e) => h("li", {},
      h("span", { class: "ev" }, e.event), " ", h("span", { class: "muted" }, `${e.actor} · ${ago(e.at)}`),
      e.related_id && [" → ", h("code", {}, e.related_id)], e.detail && h("div", { class: "evidence" }, e.detail)))));
}

// actionForm is an inline form for one write: fields, submit and cancel.
function actionForm({ title, fields, submit, danger, onSubmit, onCancel }) {
  const inputs = {};
  const err = h("p", { class: "error", hidden: true });
  const save = h("button", { class: danger ? "danger" : "primary", type: "submit" }, submit);
  const form = h("form", {
    class: "aform",
    onsubmit: async (ev) => {
      ev.preventDefault();
      const values = Object.fromEntries(Object.entries(inputs).map(([k, el]) => [k, el.value.trim()]));
      for (const f of fields) {
        if (f.required && !values[f.name]) { err.textContent = `${f.label} is required.`; err.hidden = false; return; }
      }
      save.disabled = true;
      try { await onSubmit(values); } catch (e) { err.textContent = e.message; err.hidden = false; save.disabled = false; }
    },
  },
    title && h("p", { class: "hint" }, title),
    fields.map((f) => {
      const el = f.multiline ? h("textarea", { rows: "3", name: f.name }) : h("input", { type: "text", name: f.name });
      el.value = f.value || "";
      inputs[f.name] = el;
      return h("label", {}, h("span", {}, f.label), el);
    }),
    err,
    h("div", { class: "review" }, save, h("button", { type: "button", class: "ghost", onclick: onCancel }, "Cancel")));
  setTimeout(() => form.querySelector("textarea, input")?.focus(), 0);
  return form;
}

async function viewContext(p) {
  const st = await api(`/api/projects/${encodeURIComponent(p.id)}/context?days=${state.days}`);
  const misses = st.changed - st.hits;
  const out = h("div", {},
    h("div", { class: "tiles" },
      tile("Hit rate", st.changed ? pct(st.hit_rate) : "—", `${st.hits} of ${st.changed} changed files were in the context`),
      tile("Scored answers", st.calls, `${st.open} waiting for a commit`),
      tile("Callee misses", misses > 0 ? pct(st.callee_misses / misses) : "—", `${st.callee_misses} of ${misses} misses (what code.callees, HAR-0145, would add)`)));
  if (!st.changed) {
    out.append(h("p", { class: "empty section" }, "No scored answers yet: they are scored when HEAD moves on (harness sync) or a task completes with a commit."));
    return out;
  }
  const langs = Object.entries(st.by_language || {}).sort((a, b) => b[1][1] - a[1][1]);
  out.append(section("By language", "Changed files that a get_context answer contained.",
    h("div", { class: "card bars" }, langs.flatMap(([lang, [hits, of]]) => [h("span", { class: "k" }, lang), meter(hits, of)]))));
  out.append(section("By provider, capability and language",
    "Of the changed files in the answers each one contributed to, how many it surfaced. Tier 0: hits in the top tier; median: median position of its hits.",
    h("div", { class: "card" }, h("table", {},
      h("thead", {}, h("tr", {}, h("th", {}, "provider"), h("th", {}, "capability"), h("th", {}, "language"), h("th", {}, "hits"), h("th", { class: "n" }, "tier 0"), h("th", { class: "n" }, "median"))),
      h("tbody", {}, (st.by_provider || []).map((r) => h("tr", {},
        h("td", {}, r.provider), h("td", {}, r.capability.replace(/^code\./, "")), h("td", {}, r.language),
        h("td", {}, meter(r.hits, r.changed)), h("td", { class: "n" }, r.hits_tier0), h("td", { class: "n" }, r.median_position))))))));
  if ((st.top_misses || []).length) {
    out.append(section("Changed but never in the context", "Candidates for better gathering or a language-specific provider.",
      h("div", { class: "card" }, h("table", {},
        h("thead", {}, h("tr", {}, h("th", { class: "n" }, "times"), h("th", {}, "language"), h("th", {}, "file"), h("th", {}, "called from"))),
        h("tbody", {}, st.top_misses.map((m) => h("tr", {},
          h("td", { class: "n" }, `${m.times}×`), h("td", {}, m.language), h("td", { class: "mono wrap" }, m.path), h("td", { class: "mono wrap" }, m.called_from || ""))))))));
  }
  return out;
}

async function viewFindings(p) {
  const r = await api(projectAPI(p, `report?days=${state.days}`));
  const st = r.context || {}, ar = st.after_read || {}, k = r.knowledge || {};
  const fs = r.findings || [];
  const n = (lv) => fs.filter((f) => f.level === lv).length;
  const out = h("div", {},
    h("div", { class: "tiles" },
      tile("Problems", n("problem"), "act on these first"),
      tile("Warnings", n("warn"), "worth a look"),
      tile("Context hits", st.changed ? pct(st.hit_rate) : "—", `${st.hits || 0} of ${st.changed || 0} changed files, ${st.calls || 0} answers`),
      tile("Read after context", ar.touched ? pct(ar.hit_rate) : "—", `${ar.hits || 0} of ${ar.touched || 0} files read or edited next`)),
    h("p", { class: "hint section" }, `What harness report says about the last ${r.days} days, most severe first. The same as 'harness report' in a terminal.`));
  out.append(fs.length ? h("div", { class: "findings" }, fs.map(findingCard)) : h("p", { class: "empty" }, "Nothing to report."));
  out.append(section("Numbers", null, h("div", { class: "card pad" }, kv([
    ["Callee misses", `${st.callee_misses || 0} of ${(st.changed || 0) - (st.hits || 0)} misses`],
    ["Hook events", `${(r.metrics && r.metrics.hooks && r.metrics.hooks.events) || 0}`],
    ["Knowledge", `${r.knowledge_reads} reads, ${r.knowledge_writes} writes by agents; ${k.current_memories} current memories`],
    ["Proposals", `${k.proposed} made, ${k.approved} approved, ${k.rejected} rejected, ${k.pending} pending`],
  ]))));
  return out;
}

function findingCard(f) {
  return h("article", { class: `finding lv-${f.level}` },
    h("div", { class: "fhead" }, levelPill(f.level), h("span", { class: "farea" }, f.area)),
    h("div", { class: "fmsg" }, f.message),
    f.trend ? h("div", { class: "fchart" }, dayChart(f.trend), h("span", { class: "muted small" }, `${f.trend.days[0].day.slice(5)} → ${f.trend.days[f.trend.days.length - 1].day.slice(5)}, red: days with the problem`))
      : f.history && h("pre", { class: "fhist" }, f.history),
    f.action && h("div", { class: "faction" }, "→ ", f.action));
}

// dayChart draws a capability's days: one bar per day scaled to the
// busiest day, red where the day had errors or slow calls.
function dayChart(trend) {
  // at most 60 bars: a long window is summed into runs of days
  const all = (trend && trend.days) || [];
  const per = Math.max(1, Math.ceil(all.length / 60));
  const days = [];
  for (let i = 0; i < all.length; i += per) {
    const run = all.slice(i, i + per);
    days.push(run.reduce((a, d) => ({ day: a.day, calls: a.calls + d.calls, errors: a.errors + d.errors, slow: a.slow + d.slow }),
      { day: per > 1 ? `${run[0].day} – ${run[run.length - 1].day}` : run[0].day, calls: 0, errors: 0, slow: 0 }));
  }
  const peak = Math.max(1, ...days.map((d) => d.calls));
  const chart = h("div", { class: "daychart", role: "img",
    "aria-label": `${days.reduce((s, d) => s + d.calls, 0)} calls over ${days.length} days` });
  for (const d of days) {
    const bar = h("span", {
      class: d.errors + d.slow > 0 ? "bad" : d.calls ? "" : "none",
      title: `${d.day}: ${d.calls} call(s)${d.errors ? `, ${d.errors} failed` : ""}${d.slow ? `, ${d.slow} slow` : ""}`,
    });
    bar.style.setProperty("--h", `${d.calls ? Math.max(8, Math.round((d.calls / peak) * 100)) : 4}%`);
    chart.append(bar);
  }
  return chart;
}

async function viewProviders(p) {
  const [v, m] = await Promise.all([api(projectAPI(p, `providers?days=${state.days}`)), api(projectAPI(p, `metrics?days=${state.days}`))]);
  const provs = v.providers || [];
  const used = provs.filter((x) => x.enabled && !x.inactive);
  const idle = provs.filter((x) => !x.enabled || x.inactive);
  const kind = (x) => (x.type === "harness" ? "provider protocol" : "MCP");
  const cap = (c) => c.replace(/^code\./, "");
  const out = h("div", {},
    h("p", { class: "hint" }, `Code providers harness routes agents' questions to, and how they answered over ${state.days} days (project languages: ${(v.languages || []).join(", ") || "none recognized"}). Whether they start: `,
      h("a", { href: projectHref(p.id, "health") }, "Health → Check providers"), "."));
  out.append(section("Used here", used.length ? null : "No provider serves this project's languages.", used.map((ps) => {
    const caps = ps.capabilities || [];
    const called = caps.filter((c) => c.metrics);
    const quiet = caps.filter((c) => !c.metrics);
    const calls = called.reduce((n, c) => n + c.metrics.calls, 0);
    const errs = called.reduce((n, c) => n + c.metrics.calls * c.metrics.error_rate, 0);
    const timeouts = called.some((c) => c.metrics.timeouts > 0);
    const cols = ["calls", "errors", timeouts && "timeouts", "empty", "p95", "context hits"].filter(Boolean);
    return h("article", { class: "provcard" },
      h("div", { class: "provhead" },
        h("h3", {}, ps.name), h("span", { class: "tag" }, kind(ps)), ps.pack && h("span", { class: "tag" }, `pack ${ps.pack}`),
        h("span", { class: "provsum" }, calls ? `${calls} call(s) · ${pct(calls ? errs / calls : 0)} failed` : `not called in ${state.days} days`)),
      h("div", { class: "pmeta mono muted" }, ps.command),
      (ps.only || []).length > 0 && h("div", { class: "pmeta" }, "Limited to ", ps.only.map(cap).join(", ")),
      caps.length === 0 ? h("p", { class: "empty" }, ps.type === "harness" ? "Declares its capabilities when started; none called yet." : "No capabilities.")
        : [called.length > 0 && h("div", { class: "card captable" }, h("table", {},
          h("thead", {}, h("tr", {}, h("th", {}, "capability"), cols.map((c) => h("th", { class: "n" }, c)), h("th", {}, "per day"), h("th", { class: "n" }, "since last problem"))),
          h("tbody", {}, called.map((c) => {
            const r = c.metrics, t = c.trend;
            const last = t && (t.last_error_at || t.last_slow_at)
              ? `${Math.min(t.last_error_at ? t.calls_after_error : Infinity, t.last_slow_at ? t.calls_after_slow : Infinity)} call(s)` : t ? "none" : "";
            return h("tr", {},
              h("td", {}, h("div", {}, cap(c.capability), !c.declared && h("span", { class: "muted small", title: "seen in calls; the provider declares its capabilities when started" }, " (seen)")),
                c.tool && h("div", { class: "mono muted small" }, c.tool)),
              h("td", { class: "n" }, r.calls), rateCell(r.error_rate, 0.1, 0.5), timeouts && h("td", { class: "n" }, r.timeouts),
              h("td", { class: "n" }, pct(r.empty_rate)), latencyCell(r.p95_ms), h("td", { class: "n" }, r.context_hit_share >= 0 ? pct(r.context_hit_share) : "—"),
              h("td", { class: "chartcell" }, t ? dayChart(t) : ""),
              h("td", { class: "n" }, last));
          })))),
          quiet.length > 0 && h("p", { class: "pmeta muted" }, `Not called in ${state.days} days: `, quiet.map((c) => cap(c.capability) + (c.tool ? ` (${c.tool})` : "")).join(", "))],
      caps.some((c) => c.capability === "project.overview") && indexedSection(p, ps.name),
      ps.name === "graphify-csharp" && h("div", { class: "pmeta" }, h("h4", {}, "Warm session"), graphifySessions(p.id)));
  })));
  if (idle.length) out.append(section("Not used here", "Configured on this machine, but disabled or serving other languages.",
    h("div", { class: "card idlelist" }, idle.map((ps) => h("div", { class: "idlerow" },
      h("span", { class: "iname" }, ps.name),
      h("span", { class: "tag" }, kind(ps)),
      h("span", { class: "ireason" }, !ps.enabled ? "disabled" : ps.inactive.replace(/^not used here: /, "")),
      (ps.languages || []).length > 0 && h("span", { class: "muted small" }, ps.languages.join(", ")))))));
  // which architecture capabilities have a provider (HAR-0108)
  const arch = v.architecture || [];
  if (arch.length) out.append(section("Architecture evidence", "Who serves each architecture capability; without a provider nothing fails, and the line says what is missing.",
    h("div", { class: "card" }, h("table", { class: "archcov" },
      h("thead", {}, h("tr", {}, h("th", {}, "capability"), h("th", {}, "served by"), h("th", {}, "without one"))),
      h("tbody", {}, arch.map((c) => h("tr", {},
        h("td", { class: "mono" }, c.capability.replace(/^arch\./, "")),
        h("td", {}, c.providers.length ? c.providers.join(", ") : h("span", { class: "muted" }, "none")),
        h("td", { class: "wrap small muted" }, c.without || "—"))))))));
  out.append(toolsSection(m, p));
  return out;
}

// graphifySessions lists graphify-csharp's warm sessions (TASK-0043), of
// one project or the whole machine, each with its state and start, restart
// or stop; a write like the others (X-Harness-UI, loopback only).
function graphifySessions(projectId) {
  const box = h("div", { class: "card gsessions" }, h("p", { class: "muted small" }, "Loading sessions…"));
  const load = async () => {
    const d = await api(`/api/graphify/sessions${projectId ? "?project=" + enc(projectId) : ""}`);
    const ss = d.sessions || [];
    const act = (action, body, label) => h("button", { class: "ghost small", type: "button", onclick: async (ev) => {
      ev.target.disabled = true;
      try { await post(`/api/graphify/sessions/${action}`, body); } catch (e) { fill(box, h("p", { class: "error" }, e.message)); return; }
      setTimeout(() => load().catch(() => {}), 1500);
    } }, label);
    fill(box,
      d.error && h("p", { class: "error" }, d.error),
      ss.length ? h("table", { class: "gstable" }, h("tbody", {}, ss.map((s) => h("tr", {},
        !projectId && h("td", {}, s.project_id ? h("a", { href: projectHref(s.project_id, "agents", "tools") }, s.project_name || s.project_id) : h("span", { class: "mono small" }, s.root)),
        h("td", { class: "small" }, s.summary),
        h("td", { class: "mono muted small", title: s.id }, s.id.slice(0, 8)),
        h("td", { class: "gsbtns" }, s.project_id && act("restart", { project: s.project_id }, "Restart"), act("stop", { session: s.id }, "Stop"))))))
        : h("p", { class: "muted small" }, projectId ? "No warm session for this project: the first callers, references or impact question starts one." : "No graphify sessions on this machine."),
      h("div", { class: "gsbtns" }, projectId && !ss.length && act("start", { project: projectId }, "Start a session"),
        h("button", { class: "ghost small", type: "button", onclick: () => load().catch((e) => fill(box, h("p", { class: "error" }, e.message))) }, "Refresh")));
  };
  load().catch((e) => fill(box, h("p", { class: "error" }, e.message)));
  return box;
}

function toolsSection(m, p) {
  const tools = m.tools || [];
  const out = h("div", {});
  out.append(section("MCP tools", "Calls agents made to harness, by tool. Error rates of 10% and over and p95 latencies of 3 s and over are marked.",
    tools.length === 0 ? h("p", { class: "empty" }, "No tool calls recorded in this window.") :
      h("div", { class: "card" }, h("table", {},
        h("thead", {}, h("tr", {}, ["tool", "kind"].map((t) => h("th", {}, t)), ["calls", "errors", "empty", "p50", "p95", "avg size"].map((t) => h("th", { class: "n" }, t)), h("th", {}, "clients"), h("th", {}, "last"))),
        h("tbody", {}, tools.map((r) => h("tr", {},
          h("td", { class: "mono" }, r.tool), h("td", {}, r.kind || ""), h("td", { class: "n" }, r.calls),
          rateCell(r.error_rate, 0.1, 0.5), h("td", { class: "n" }, pct(r.empty_rate)),
          h("td", { class: "n" }, ms(r.p50_ms)), latencyCell(r.p95_ms), h("td", { class: "n" }, bytes(r.avg_bytes)),
          h("td", {}, (r.clients || []).join(", ")), h("td", {}, ago(r.last_at)))))))));
  if (p) out.append(hooksSection(p));
  return out;
}

// hooksSection shows which hooks are installed, read from the files as
// 'harness hooks status' does, next to the events they recorded in a week
// (TASK-0044): installed but silent says the hook may not run.
function hooksSection(p) {
  const box = h("div", { class: "card pad" }, h("p", { class: "muted small" }, "Reading the hook files…"));
  const yes = (on, label) => h("span", { class: on ? "okay" : "muted" }, on ? `✓ ${label}` : `– ${label}`);
  api(projectAPI(p, "hooks")).then((d) => {
    const ev = d.events || {};
    const git = d.git || [];
    const gitOn = git.filter((g) => g.installed).length;
    fill(box,
      h("h4", {}, "Git sync hooks"),
      d.git_error ? h("p", { class: "error small" }, d.git_error)
        : h("p", { class: "small" }, gitOn === git.length && git.length ? yes(true, `installed (${git.map((g) => g.name).join(", ")})`)
          : gitOn ? [yes(true, `${gitOn} of ${git.length}`), " installed: ", git.filter((g) => !g.installed).map((g) => g.name).join(", "), " missing"]
            : yes(false, "not installed: harness hooks install"),
        d.git_dir && h("span", { class: "muted mono" }, "  " + d.git_dir)),
      h("h4", {}, "Client hooks"),
      h("table", { class: "gstable" }, h("tbody", {}, (d.clients || []).map((c) => h("tr", {},
        h("td", {}, c.client === "claude-code" ? "Claude Code" : "Codex", h("span", { class: "muted" }, ` · ${c.scope}`)),
        h("td", { class: "small" }, yes(!!c.command, "tool events"), c.client === "claude-code" && ["  ", yes(!!c.session, "session start")]),
        h("td", { class: "mono muted small" }, c.path),
        h("td", { class: "small" }, c.error ? h("span", { class: "error" }, c.error) : c.problem ? h("span", { class: "error" }, c.problem) : ""))))),
      h("p", { class: "small" }, ev.events ? `${ev.events} event(s) in 7 days over ${ev.sessions} session(s) from ${(ev.clients || []).join(", ")}, last ${ago(ev.last_at)}.`
        : "No events recorded in 7 days."),
      d.note && h("p", { class: "small freview" }, d.note));
  }).catch((e) => fill(box, h("p", { class: "error" }, e.message)));
  return section("Hooks", "Read from the hook files, as 'harness hooks status' does; the events are what they recorded.", box);
}

async function viewProposals(p) {
  const st = state.proposals;
  const wrap = h("div", {});
  const list = h("div", { class: "mems" });
  async function load() {
    fill(list, h("p", { class: "empty" }, "Loading…"));
    try {
      const res = await api(`/api/projects/${encodeURIComponent(p.id)}/proposals?status=${st.status}`);
      const ps = res.proposals || [];
      fill(list, ...(ps.length ? ps.map((x) => proposalCard(p, x)) : [h("p", { class: "empty" }, `No ${st.status === "all" ? "" : st.status + " "}proposals.`)]));
    } catch (e) {
      fill(list, h("p", { class: "error" }, e.message));
    }
  }
  const chips = ["pending", "approved", "rejected", "all"].map((s) => h("button", {
    class: "chip", "aria-pressed": String(st.status === s),
    onclick: () => {
      st.status = s;
      for (const c of chips) c.setAttribute("aria-pressed", String(c.dataset.status === s));
      load();
    },
    "data-status": s,
  }, s));
  wrap.append(h("p", { class: "hint" }, "Memories agents proposed but nobody confirmed yet. They are not active until approved; approving or rejecting here is recorded as you."),
    h("div", { class: "filters" }, chips), list);
  await load();
  return wrap;
}

function proposalCard(p, x) {
  const panel = h("div", { class: "mpanel" });
  const url = (what) => projectAPI(p, `proposals/${encodeURIComponent(x.id)}/${what}`);
  // approve, and when the proposal resembles current memories, ask which
  // to replace or whether to keep both (as harness proposals approve does)
  async function approve(body) {
    const res = await post(url("approve"), body);
    if (res.status !== "needs_decision") return afterWrite();
    fill(panel, h("div", { class: "decide" },
      h("p", {}, h("b", {}, "This resembles current memories. "), "Replace one of them with it, or keep both:"),
      h("ul", {}, (res.similar || []).map((sim) => h("li", { class: `ty-${sim.memory.type}` },
        h("span", { class: "num" }, `${Math.round(sim.similarity * 100)}%`),
        h("span", {}, h("span", { class: "dot", "aria-hidden": "true" }), sim.memory.content, " ", h("code", { class: "muted" }, sim.memory.id)),
        h("button", { onclick: () => approve({ ...body, supersedes: [sim.memory.id] }).catch(showErr) }, "Replace this one")))),
      h("div", { class: "review" },
        h("button", { class: "primary", onclick: () => approve({ ...body, keep_both: true }).catch(showErr) }, "Keep both"),
        h("button", { class: "ghost", onclick: () => fill(panel) }, "Cancel"))));
  }
  const showErr = (e) => fill(panel, h("p", { class: "error" }, e.message));
  return h("article", { class: `mem ty-${x.type}` + (x.status === "rejected" ? " inactive" : "") },
    h("div", { class: "mtype" }, h("span", { class: "dot", "aria-hidden": "true" }), MEMORY_GROUPS[x.type] ? MEMORY_GROUPS[x.type].replace(/s$/, "") : x.type,
      x.status !== "pending" && [" · ", statusWord(x)]),
    h("div", { class: "content" }, x.content),
    x.rationale && h("p", { class: "why" }, h("b", {}, "Why: "), x.rationale),
    h("div", { class: "prov" },
      h("span", { class: "scope" }, x.scope),
      x.source_ref && h("span", {}, h("b", {}, "source "), x.source_ref),
      h("span", {}, h("b", {}, "proposed by "), x.proposed_by || "unknown"),
      h("span", { title: x.created_at }, ago(x.created_at)),
      x.supersedes && h("span", {}, h("b", {}, "would supersede "), h("code", {}, x.supersedes)),
      x.memory_id && h("span", {}, h("b", {}, "memory "), h("code", {}, x.memory_id)),
      h("code", { class: "id" }, x.id)),
    (x.similar || []).length > 0 && h("div", { class: "similar" },
      h("div", { class: "label" }, "Resembles current memories (approving asks whether to replace one or keep both):"),
      h("ul", {}, x.similar.map((sim) => h("li", { class: `ty-${sim.memory.type}` }, h("span", { class: "num" }, `${Math.round(sim.similarity * 100)}%`),
        h("span", {}, h("span", { class: "dot", "aria-hidden": "true" }), sim.memory.content, " ", h("code", { class: "muted" }, sim.memory.id)))))),
    x.status === "pending" && h("div", { class: "review" },
      h("button", { class: "primary", onclick: () => approve({}).catch(showErr) }, "Approve"),
      h("button", {
        onclick: () => fill(panel, actionForm({
          title: "Edit the statement, then approve it.",
          fields: [{ name: "content", label: "Statement", multiline: true, value: x.content, required: true }],
          submit: "Approve edited",
          onSubmit: (v) => approve({ content: v.content }),
          onCancel: () => fill(panel),
        })),
      }, "Edit & approve"),
      h("button", {
        class: "ghost",
        onclick: () => fill(panel, actionForm({
          fields: [{ name: "note", label: "Note (optional)" }],
          submit: "Reject", danger: true,
          onSubmit: (v) => post(url("reject"), v).then(afterWrite),
          onCancel: () => fill(panel),
        })),
      }, "Reject")),
    panel);
}

function statusWord(x) {
  const ico = x.status === "approved" ? "✓" : "✕";
  return h("span", { class: `pstatus ps-${x.status}` }, `${ico} ${x.status}${x.decided_by ? " by " + x.decided_by : ""}${x.decision_note ? ": " + x.decision_note : ""}`);
}

// --- architectural proposals (TASK-0032) ---
// Agents propose a task (from a finding, or their own observation: a
// model's interpretation, labelled apart from harness's findings) or a
// by-design mark; nothing changes until the author approves (creating the
// task or the mark, restated first if wanted) or rejects.
async function viewArchProposals(p) {
  const st = state.archProposals || (state.archProposals = { status: "pending" });
  const list = h("div", { class: "mems" });
  async function load() {
    fill(list, h("p", { class: "empty" }, "Loading…"));
    try {
      const [tres, bres, res] = await Promise.all([api(projectAPI(p, `task-proposals?status=${st.status}`)),
        api(projectAPI(p, `blueprint-proposals?status=${st.status}`)), api(projectAPI(p, `arch-proposals?status=${st.status}`))]);
      const ts = tres.proposals || [], bs = bres.proposals || [], ps = res.proposals || [];
      fill(list, ...(ts.length || bs.length || ps.length ? [
        ts.length > 0 && h("h3", { class: "inboxhead" }, "Tasks"), ...ts.map((x) => taskProposalCard(p, x)),
        bs.length > 0 && h("h3", { class: "inboxhead" }, "Blueprints"), ...bs.map((x) => blueprintProposalCard(p, x)),
        ps.length > 0 && h("h3", { class: "inboxhead" }, "Architecture"), ...ps.map((x) => archProposalCard(p, x)),
      ] : [h("p", { class: "empty" }, `No ${st.status === "all" ? "" : st.status + " "}proposals.`)]));
    } catch (e) {
      fill(list, h("p", { class: "error" }, e.message));
    }
  }
  const chips = ["pending", "approved", "rejected", "all"].map((s) => h("button", {
    class: "chip", "aria-pressed": String(st.status === s), "data-status": s,
    onclick: () => {
      st.status = s;
      for (const c of chips) c.setAttribute("aria-pressed", String(c.dataset.status === s));
      load();
    },
  }, s));
  await load();
  return h("div", {},
    h("p", { class: "hint" }, "Tasks, blueprints, architectural tasks and by-design marks agents proposed. Nothing changes until you approve one (it creates the task, the blueprint or the mark, or restates the blueprint) or reject it. Agents are told not to create these themselves; that is steered, not enforced: one editing a backlog file still could."),
    h("div", { class: "filters" }, chips), list);
}

// --- tasks by hand and agents' task proposals (TASK-0046) ---
const TASK_TYPES = ["feature", "bug", "chore", "spike", "docs", "test", "process"];
const PRIORITIES = ["low", "medium", "high", "critical"];

// taskFields is the form for a task's definition (new, a proposal
// restated before it is approved, or an unclaimed task edited); value()
// reads it back. An edited task keeps its id.
function taskFields(p, init, blueprints, keepId) {
  const lines = (v) => v.split("\n").map((x) => x.trim()).filter(Boolean);
  const commas = (v) => v.split(",").map((x) => x.trim()).filter(Boolean);
  const id = h("input", { type: "text", value: init.id || "", placeholder: "next free id", "aria-label": "Id", disabled: !!keepId });
  if (!init.id) api(projectAPI(p, "tasks-next-id")).then((r) => { id.placeholder = r.id; }).catch(() => {});
  const title = h("input", { type: "text", value: init.title || "", required: true, "aria-label": "Title" });
  const goal = h("textarea", { rows: 2, "aria-label": "Goal" }, init.goal || "");
  const pick = (opts, cur, label) => h("select", { "aria-label": label }, opts.map(([v, t]) => h("option", { value: v, selected: v === cur }, t)));
  const type = pick(TASK_TYPES.map((t) => [t, t]), init.type || "feature", "Type");
  const prio = pick(PRIORITIES.map((t) => [t, t]), init.priority || "medium", "Priority");
  // A blueprint named but not (yet) known, as a proposal may, stays
  // chosen rather than falling back to none.
  const known = blueprints.map((m) => [m.id, m.title ? `${m.id} · ${m.title}` : m.id]);
  if (init.blueprint && !blueprints.some((m) => m.id === init.blueprint)) known.push([init.blueprint, `${init.blueprint} (new)`]);
  const ms = pick([["", "none"], ...known], init.blueprint || "", "Blueprint");
  const deps = h("input", { type: "text", value: (init.depends_on || []).join(", "), placeholder: "TASK-0041, HAR-0109", "aria-label": "Depends on" });
  const acc = h("textarea", { rows: 3, placeholder: "one criterion per line, each one checkable", "aria-label": "Acceptance criteria" }, (init.acceptance || []).join("\n"));
  const scope = h("input", { type: "text", value: (init.scope || []).join(", "), placeholder: "internal/tasks, docs", "aria-label": "Scope" });
  const field = (label, el) => h("label", {}, h("span", {}, label), el);
  return {
    nodes: [field("Title", title), field("Goal: the outcome and why", goal),
      h("div", { class: "frow" }, field("Type", type), field("Priority", prio), field("Blueprint", ms), field("Id", id)),
      field("Depends on", deps), field("Acceptance criteria", acc), field("Scope (paths it may touch)", scope)],
    value: () => ({ id: id.value.trim(), title: title.value, goal: goal.value, type: type.value, priority: prio.value, blueprint: ms.value,
      depends_on: commas(deps.value), acceptance: lines(acc.value), scope: commas(scope.value) }),
  };
}

function newTaskForm(p, panel, blueprints) {
  const f = taskFields(p, {}, blueprints);
  return writeForm(panel, "A new task: it starts in the backlog and is ready once its dependencies are done. 'harness tasks export' writes it into the backlog file.",
    [f.nodes], "Add the task", () => post(projectAPI(p, "tasks"), f.value()));
}

// editTaskForm restates a ready or backlog task with the same form; the
// definition is fixed once an agent claims it.
async function editTaskForm(p, panel, x) {
  const ms = await api(projectAPI(p, "blueprints")).then((r) => r.blueprints || []).catch(() => []);
  const init = { ...x, depends_on: (x.depends_on || []).map((d) => d.id), acceptance: (x.acceptance || []).map((c) => c.criterion) };
  const f = taskFields(p, init, ms, true);
  return writeForm(panel, `Edit ${x.id}? It can change until an agent claims it; its validation and notes stay as they are.`,
    [f.nodes], "Save the task", () => post(projectAPI(p, `tasks/${enc(x.id)}/edit`), f.value()));
}

function taskProposalCard(p, x) {
  const panel = h("div", { class: "mpanel" });
  const url = (what) => projectAPI(p, `task-proposals/${enc(x.id)}/${what}`);
  const d = x.task || {};
  const approveForm = async () => {
    const ms = await api(projectAPI(p, "blueprints")).then((r) => r.blueprints || []).catch(() => []);
    const f = taskFields(p, d, ms);
    return writeForm(panel, `Create this task? ${x.proposed_by} is recorded as the proposer and you as the one who made it.`,
      [f.nodes], "Create the task", () => post(url("approve"), { task: f.value() }));
  };
  const rejectForm = () => {
    const note = h("textarea", { rows: 2, placeholder: "Why not (optional)" });
    return writeForm(panel, "Reject it?", [note], "Reject", () => post(url("reject"), { note: note.value }));
  };
  const acc = d.acceptance || [];
  return h("article", { class: "mem aprop" + (x.status === "rejected" ? " inactive" : "") },
    h("div", { class: "mtype" }, "Task", h("span", { class: "tag" }, `${d.type || "feature"} · ${d.priority || "medium"}`),
      d.blueprint && h("span", { class: "tag" }, d.blueprint),
      x.status !== "pending" && h("span", { class: "muted" }, ` · ${x.status}${x.result ? ` → ${x.result}` : ""}`)),
    h("div", { class: "content" }, d.title),
    d.goal && h("p", { class: "why" }, h("b", {}, "Goal: "), d.goal),
    x.reason && h("p", { class: "why" }, h("b", {}, "Why proposed: "), x.reason),
    acc.length > 0 && h("details", { class: "evfold", open: true }, h("summary", {}, `Acceptance (${acc.length})`), h("ol", { class: "evlist" }, acc.map((a) => h("li", {}, a)))),
    h("div", { class: "prov" },
      (d.depends_on || []).length > 0 && h("span", {}, h("b", {}, "depends on "), d.depends_on.join(", ")),
      (d.scope || []).length > 0 && h("span", {}, h("b", {}, "scope "), d.scope.join(", ")),
      h("span", {}, h("b", {}, "proposed by "), x.proposed_by || "unknown"),
      h("span", { title: x.proposed_at }, ago(x.proposed_at)),
      x.decided_by && h("span", {}, h("b", {}, `${x.status} by `), x.decided_by),
      x.note && h("span", {}, h("b", {}, "note "), x.note),
      h("code", { class: "id" }, x.id)),
    x.status === "pending" && h("div", { class: "review" },
      h("button", { class: "primary", onclick: async () => {
        const opening = h("div", { class: "aform" }, h("p", { class: "hint" }, "Loading review…"));
        fill(panel, opening);
        try {
          const form = await approveForm();
          if (opening.parentNode === panel) fill(panel, form);
        } catch (e) { if (opening.parentNode === panel) fill(panel, h("p", { class: "error" }, e.message)); }
      } }, "Create the task…"),
      h("button", { class: "ghost", onclick: () => fill(panel, rejectForm()) }, "Reject…")),
    panel);
}

// blueprintProposalCard is an agent's new blueprint or change to one
// (TASK-0049): approved as proposed or restated first, or rejected.
function blueprintProposalCard(p, x) {
  const panel = h("div", { class: "mpanel" });
  const url = (what) => projectAPI(p, `blueprint-proposals/${enc(x.id)}/${what}`);
  const d = x.blueprint || {};
  const isNew = !x.changes;
  const approveForm = () => {
    const f = blueprintFields(p, d, isNew);
    return writeForm(panel, isNew ? `Create this blueprint? ${x.proposed_by} is recorded as the proposer.` : `Restate ${x.changes} like this? Its id and tasks stay.`,
      [f.nodes], isNew ? "Create the blueprint" : "Save the blueprint", () => post(url("approve"), { edit: f.value() }));
  };
  const rejectForm = () => {
    const note = h("textarea", { rows: 2, placeholder: "Why not (optional)" });
    return writeForm(panel, "Reject it?", [note], "Reject", () => post(url("reject"), { note: note.value }));
  };
  const exit = d.exit || [];
  return h("article", { class: "mem aprop" + (x.status === "rejected" ? " inactive" : ""), "data-proposal": x.id },
    h("div", { class: "mtype" }, isNew ? "New blueprint" : "Blueprint change", !isNew && h("span", { class: "tag" }, x.changes),
      x.status !== "pending" && h("span", { class: "muted" }, ` · ${x.status}${x.result ? ` → ${x.result}` : ""}`)),
    h("div", { class: "content" }, d.title),
    d.goal && h("p", { class: "why" }, h("b", {}, "Goal: "), d.goal),
    x.reason && h("p", { class: "why" }, h("b", {}, "Why proposed: "), x.reason),
    exit.length > 0 && h("details", { class: "evfold", open: true }, h("summary", {}, `Done when (${exit.length})`), h("ul", { class: "evlist" }, exit.map((e) => h("li", {}, e)))),
    d.notes && h("p", { class: "why" }, h("b", {}, "Notes: "), d.notes),
    h("div", { class: "prov" },
      h("span", {}, h("b", {}, "proposed by "), x.proposed_by || "unknown"),
      h("span", { title: x.proposed_at }, ago(x.proposed_at)),
      x.decided_by && h("span", {}, h("b", {}, `${x.status} by `), x.decided_by),
      x.note && h("span", {}, h("b", {}, "note "), x.note),
      h("code", { class: "id" }, x.id)),
    x.status === "pending" && h("div", { class: "review" },
      h("button", { class: "primary", onclick: () => fill(panel, approveForm()) }, isNew ? "Create the blueprint…" : "Apply the change…"),
      h("button", { class: "ghost", onclick: () => fill(panel, rejectForm()) }, "Reject…")),
    panel);
}

function archProposalCard(p, x) {
  const panel = h("div", { class: "mpanel" });
  const url = (what) => projectAPI(p, `arch-proposals/${enc(x.id)}/${what}`);
  const task = x.kind === "task", own = x.basis === "observation";
  const approveForm = () => {
    const title = task && h("input", { type: "text", value: x.title, "aria-label": "Title" });
    const body = h("textarea", { rows: 3, "aria-label": task ? "What should change and why" : "Why it is intended" }, x.body || "");
    return writeForm(panel, task ? "Create this task?" : "Mark the finding by design?",
      [title && h("label", {}, h("span", {}, "Title"), title), h("label", {}, h("span", {}, task ? "What should change and why" : "Why it is intended"), body),
        h("p", { class: "muted small" }, task ? `An ARCH task in this project${x.finding_key ? ", carrying the finding's evidence and linked to it" : ", marked as the agent's observation"}; ${x.proposed_by} is recorded as the proposer.`
          : "The finding is marked and hidden unless marked ones are shown; the reason becomes a decision for agents working there.")],
      task ? "Create the task" : "Mark by design", () => post(url("approve"), { title: title ? title.value : "", body: body.value }));
  };
  const rejectForm = () => {
    const note = h("textarea", { rows: 2, placeholder: "Why not (optional)" });
    return writeForm(panel, "Reject it?", [note], "Reject", () => post(url("reject"), { note: note.value }));
  };
  const ev = x.evidence || [];
  return h("article", { class: "mem aprop" + (x.status === "rejected" ? " inactive" : "") },
    h("div", { class: "mtype" }, task ? (own ? "Task from the agent's observation" : "Task from a finding") : "By design",
      own ? h("span", { class: "tag fown", title: "Not found by harness: the agent's reading of the code" }, "model interpretation")
        : h("span", { class: "tag" }, `harness finding · ${x.basis}`),
      x.status !== "pending" && h("span", { class: "muted" }, ` · ${x.status}${x.result && x.result !== "marked" ? ` → ${x.result}` : ""}`)),
    h("div", { class: "content" }, x.title),
    x.body && h("p", { class: "why" }, h("b", {}, task ? "Proposed: " : "Why intended: "), x.body),
    ev.length > 0 && h("details", { class: "evfold" }, h("summary", {}, `Evidence (${ev.length})`), h("ul", { class: "evlist" }, ev.map((e) => h("li", {}, h("code", {}, e))))),
    h("div", { class: "prov" },
      h("span", { class: "scope" }, x.scope),
      x.finding_key && h("span", {}, h("b", {}, "finding "), h("code", {}, x.finding_key)),
      h("span", {}, h("b", {}, "proposed by "), x.proposed_by || "unknown"),
      h("span", { title: x.proposed_at }, ago(x.proposed_at)),
      x.decided_by && h("span", {}, h("b", {}, `${x.status} by `), x.decided_by),
      x.note && h("span", {}, h("b", {}, "note "), x.note),
      h("code", { class: "id" }, x.id)),
    x.status === "pending" && h("div", { class: "review" },
      h("button", { class: "primary", onclick: () => fill(panel, approveForm()) }, task ? "Create the task…" : "Mark by design…"),
      h("button", { class: "ghost", onclick: () => fill(panel, rejectForm()) }, "Reject…")),
    panel);
}

// --- blueprints by hand (TASK-0048) ---
// blueprintFields is the form for a blueprint's description, new or
// edited; a new one starts with the next free id and a placeholder title.
function blueprintFields(p, init, isNew) {
  const lines = (v) => v.split("\n").map((x) => x.trim()).filter(Boolean);
  const id = isNew && h("input", { type: "text", value: "", required: true, "aria-label": "Id" });
  if (isNew) api(projectAPI(p, "blueprints-next-id")).then((r) => { if (!id.value) id.value = r.id; }).catch(() => {});
  const title = h("input", { type: "text", value: init.title || (isNew ? "New blueprint" : ""), required: true, "aria-label": "Name" });
  const goal = h("textarea", { rows: 2, "aria-label": "Goal" }, init.goal || "");
  const exit = h("textarea", { rows: 3, placeholder: "one criterion per line", "aria-label": "Done when" }, (init.exit || []).join("\n"));
  const notes = h("textarea", { rows: 2, "aria-label": "Notes" }, init.notes || "");
  const field = (label, el) => h("label", {}, h("span", {}, label), el);
  return {
    nodes: [isNew ? h("div", { class: "frow" }, field("Name", title), field("Id", id)) : field("Name", title),
      field("Goal: what it is for", goal), field("Done when", exit), field("Notes", notes)],
    value: () => ({ ...(isNew ? { id: id.value.trim() } : {}), title: title.value, goal: goal.value, exit: lines(exit.value), notes: notes.value }),
  };
}

function newBlueprintForm(p, panel) {
  const f = blueprintFields(p, {}, true);
  return writeForm(panel, "A new blueprint: group tasks under it by choosing it in their Blueprint field.",
    [f.nodes], "Add the blueprint", () => post(projectAPI(p, "blueprints"), f.value()));
}

function editBlueprintForm(p, panel, m) {
  const f = blueprintFields(p, m, false);
  return writeForm(panel, `Edit ${m.id}? Its id stays.`,
    [f.nodes], "Save the blueprint", () => post(projectAPI(p, `blueprints/${enc(m.id)}/edit`), f.value()));
}

// removeBlueprintForm asks what happens to the tasks of a blueprint that
// has some: moved to another blueprint (or none), or deleted with it.
function removeBlueprintForm(p, panel, m, others) {
  const name = "bprm-" + m.id;
  const move = h("input", { type: "radio", name, value: "move", checked: true, "aria-label": "Move the tasks" });
  const del = h("input", { type: "radio", name, value: "delete", "aria-label": "Delete the tasks" });
  const to = h("select", { "aria-label": "Move to", onchange: () => { move.checked = true; } },
    h("option", { value: "" }, "no blueprint"),
    others.map((o) => h("option", { value: o.id }, o.title ? `${o.id} · ${o.title}` : o.id)));
  return writeForm(panel, `${m.id} has ${m.total} task(s). What happens to them?`,
    [h("label", { class: "choice" }, move, h("span", {}, "Move them to "), to),
      h("label", { class: "choice" }, del, h("span", {}, "Delete them, with their audit trail")),
      h("p", { class: "muted small" }, "Deleting is refused while an agent holds one of them; tasks elsewhere that depend on a deleted one lose that dependency.")],
    "Remove the blueprint", () => post(projectAPI(p, `blueprints/${enc(m.id)}/remove`),
      del.checked ? { tasks: "delete" } : { tasks: "move", move_to: to.value }));
}

// --- tasks: grouped by blueprint, with progress ---
async function viewTasks(p, opts, sec) {
  const t = state.tasks;
  const [res, mres] = await Promise.all([api(projectAPI(p, "tasks")), api(projectAPI(p, "blueprints")).catch(() => ({ blueprints: [] }))]);
  const all = res.tasks || [];
  const info = Object.fromEntries((mres.blueprints || []).map((m) => [m.id, m]));
  // #/<project>/tasks/<task id> opens that task, whatever the filters
  const linked = all.find((x) => x.id === route().item);
  if (linked) {
    t.open.add(linked.id);
    t.collapsed.delete(linked.blueprint || "—");
    if (!(opts && opts.live)) setTimeout(() => document.getElementById(`task-${linked.id}`)?.scrollIntoView({ block: "start" }), 0);
  }
  const counts = Object.fromEntries(TASK_STATUSES.map((s) => [s, all.filter((x) => x.status === s).length]));
  const blueprints = [...new Set([...(mres.blueprints || []).map((m) => m.id), ...all.map((x) => x.blueprint || "—")])];
  const body = h("div", {});
  const list = h("div", {});

  function drawTasks() {
    const shown = all.filter((x) => x === linked || (t.statuses.has(x.status) && (!t.blueprint || (x.blueprint || "—") === t.blueprint)));
    // Deferred tasks are set aside by a decision: unless asked for, each
    // blueprint shows them as one line per reason instead of one row each.
    const deferred = t.statuses.has("deferred") ? [] :
      all.filter((x) => x.status === "deferred" && x !== linked && (!t.blueprint || (x.blueprint || "—") === t.blueprint));
    if (!shown.length && !deferred.length) {
      const hiddenDone = !t.statuses.has("done") && counts.done > 0;
      return fill(list, h("p", { class: "empty" },
        all.length && counts.done === all.length ? `No open tasks: all ${all.length} are done. ` : "No tasks match. ",
        hiddenDone && h("button", { onclick: () => { t.statuses.add("done"); renderMain(); } }, "Show done tasks")));
    }
    const inM = (m) => (x) => (x.blueprint || "—") === m;
    // Blueprints with something to work on first; those holding only
    // deferred tasks after them.
    const order = [...blueprints.filter((m) => shown.some(inM(m))), ...blueprints.filter((m) => !shown.some(inM(m)) && deferred.some(inM(m)))];
    fill(list, ...order.map((m) => {
      const mine = all.filter(inM(m));
      const rows = shown.filter(inM(m));
      const reasons = Map.groupBy ? Map.groupBy(deferred.filter(inM(m)), (x) => x.blocked_reason || "")
        : deferred.filter(inM(m)).reduce((acc, x) => acc.set(x.blocked_reason || "", [...(acc.get(x.blocked_reason || "") || []), x]), new Map());
      const done = mine.filter((x) => x.status === "done").length;
      const open = !t.collapsed.has(m);
      const mi = info[m];
      return h("section", { class: "mstone" },
        h("button", {
          class: "mhead", "aria-expanded": String(open),
          onclick: () => { open ? t.collapsed.add(m) : t.collapsed.delete(m); drawTasks(); },
        },
          chevron(open),
          h("span", { class: "mname" }, m, mi && mi.title && h("span", { class: "mtitle" }, mi.title)),
          h("span", { class: "mcount" }, `${rows.length} shown` + (reasons.size ? `, ${[...reasons.values()].reduce((n, xs) => n + xs.length, 0)} deferred` : "")),
          h("span", { class: "mprog" }, meter(done, mine.length, `${done} of ${mine.length} tasks done`))),
        open && mi && mi.goal && h("p", { class: "mgoal" }, mi.goal),
        open && rows.length > 0 && h("div", { class: "tlist" }, rows.map((x) => taskRow(x, drawTasks))),
        open && [...reasons].map(([reason, xs]) => h("div", { class: "deferredline" },
          statusPill("deferred"), h("span", { class: "dcount" }, `${xs.length} task(s)`),
          h("span", { class: "dreason" }, reason || "no reason given"),
          h("button", { class: "linkbtn", onclick: () => { t.statuses.add("deferred"); renderMain(); } }, "show"))));
    }));
  }

  function drawBlueprints() {
    const ms = blueprints.map((id) => info[id] || { id, title: "", goal: "", exit: [], tasks: {}, total: all.filter((x) => (x.blueprint || "—") === id).length });
    const stateOf = (m) => {
      const n = (s) => (m.tasks || {})[s] || 0;
      if (!m.total) return ["none", "○", "no tasks"];
      if (n("done") === m.total) return ["done", "✓", "done"];
      // Done except what was deferred by a decision: done (the counts show
      // the deferred ones); nothing done and the rest deferred: deferred.
      if (n("done") + n("deferred") === m.total) return n("done") ? ["done", "✓", "done"] : ["deferred", "◌", "deferred"];
      if (n("done") + n("claimed") + n("in_progress") + n("review") > 0) return ["active", "◐", "in progress"];
      return ["todo", "○", "not started"];
    };
    const filt = t.mfilter || "open";
    const kind = (m) => ({ done: "done", deferred: "deferred" })[stateOf(m)[0]] || "open";
    const shown = ms.filter((m) => filt === "all" || kind(m) === filt);
    const chips = [["open", "Open"], ["done", "Done"], ["deferred", "Deferred"], ["all", "All"]].map(([k, label]) => h("button", {
      class: "chip", "aria-pressed": String(filt === k), onclick: () => { t.mfilter = k; drawBlueprints(); },
    }, label, h("span", { class: "count" }, k === "all" ? ms.length : ms.filter((m) => kind(m) === k).length)));
    const newPanel = h("div", { class: "mpanel" });
    const add = h("button", { class: "primary newtask", onclick: () => fill(newPanel, newBlueprintForm(p, newPanel)) }, "New blueprint…");
    fill(body, h("div", { class: "filters" }, chips, add), newPanel,
      shown.length ? h("div", { class: "mcards" }, shown.map((m) => {
        const [cls, ico, label] = stateOf(m);
        const n = (s) => (m.tasks || {})[s] || 0;
        // "—" gathers the tasks without a blueprint: nothing to edit there
        const own = m.id !== "—";
        const panel = h("div", { class: "mpanel" });
        const others = ms.filter((o) => o.id !== m.id && o.id !== "—");
        const remove = async (ev) => {
          if (m.total) return fill(panel, removeBlueprintForm(p, panel, m, others));
          const btn = ev.currentTarget;
          btn.disabled = true;
          try { await post(projectAPI(p, `blueprints/${enc(m.id)}/remove`), {}); await afterWrite(); }
          catch (e) { btn.disabled = false; fill(panel, h("p", { class: "error" }, e.message)); }
        };
        return h("article", { class: `mcard ms-${cls}`, "data-blueprint": m.id },
          h("div", { class: "mctop" },
            h("span", { class: "mid" }, m.id),
            h("h3", {}, m.title || h("span", { class: "muted" }, own ? "(not described yet)" : "(tasks without a blueprint)")),
            h("span", { class: `pill ms-${cls}` }, h("span", { class: "ico", "aria-hidden": "true" }, ico), label),
            own && h("span", { class: "mcacts" },
              h("button", { class: "ghost", onclick: () => fill(panel, editBlueprintForm(p, panel, m)) }, m.title ? "Edit…" : "Describe…"),
              h("button", { class: "ghost", onclick: remove }, m.total ? "Remove…" : "Remove"))),
          panel,
          m.goal && h("p", { class: "mcgoal" }, m.goal),
          m.total > 0 && h("div", { class: "mcprog" }, meter(n("done"), m.total, `${n("done")} of ${m.total} tasks done`),
            h("span", { class: "counts" }, ["ready", "in_progress", "review", "blocked", "backlog", "deferred"].filter((s) => n(s)).map((s) => [statusPill(s), " ", n(s), " "]))),
          (m.exit || []).length > 0 && h("div", { class: "mcexit" }, h("h4", {}, "Done when"),
            h("ul", {}, m.exit.map((e) => h("li", {}, e)))),
          m.notes && h("p", { class: "mcnote" }, m.notes),
          m.total > 0 && h("button", {
            class: "mclink", onclick: () => {
              t.blueprint = m.id; t.statuses = new Set(TASK_STATUSES); t.collapsed.delete(m.id); location.hash = projectHref(p.id, "work", "tasks");
            },
          }, `Show its ${m.total} task(s) →`));
      })) : h("p", { class: "empty" }, "No blueprints match."));
  }

  const chips = TASK_STATUSES.map((s) => h("button", {
    class: "chip", "aria-pressed": String(t.statuses.has(s)),
    onclick: (ev) => {
      t.statuses.has(s) ? t.statuses.delete(s) : t.statuses.add(s);
      ev.currentTarget.setAttribute("aria-pressed", String(t.statuses.has(s)));
      drawTasks();
    },
  }, statusPill(s), h("span", { class: "count" }, counts[s])));
  const msSel = h("select", { "aria-label": "Blueprint", onchange: (ev) => { t.blueprint = ev.target.value; drawTasks(); } },
    h("option", { value: "" }, "all blueprints"),
    blueprints.map((m) => h("option", { value: m, selected: m === t.blueprint }, info[m] && info[m].title ? `${m} · ${info[m].title}` : m)));
  if (sec === "tasks") {
    const panel = h("div", { class: "mpanel" });
    const add = h("button", { class: "primary newtask", onclick: () => fill(panel, newTaskForm(p, panel, mres.blueprints || [])) }, "New task…");
    fill(body, h("div", { class: "filters" }, chips, msSel, add), panel, list);
    drawTasks();
  } else {
    drawBlueprints();
  }
  const done = counts.done;
  const stuck = attention(p, all);
  // the summary with whether anything is stuck (the switch is the view's
  // subnav), the stuck list only if so
  return h("div", {},
    all.length > 0 && h("div", { class: "workrow" },
      h("div", { class: "card pad worksum" },
        h("div", { class: "wshead" }, h("span", {}, h("b", {}, `${done} of ${all.length} tasks done`), h("span", { class: "muted" }, ` · ${pct(done / all.length)}`))),
        taskBar(all, true)),
      stuck
        ? h("a", { class: `card pad attncard lv-${stuck.worst}`, href: "#stuck", onclick: (ev) => { ev.preventDefault(); document.getElementById("stuck").scrollIntoView({ block: "center" }); } },
          h("div", { class: "label" }, "Needs attention"),
          h("div", { class: "value" }, h("span", { class: "ico", "aria-hidden": "true" }, stuck.worst === "problem" ? "✕" : "▲"), stuck.count),
          h("div", { class: "note" }, `task${stuck.count === 1 ? "" : "s"} stuck`))
        : h("div", { class: "card pad attncard lv-ok" },
          h("div", { class: "label" }, "Needs attention"),
          h("div", { class: "value" }, h("span", { class: "ico", "aria-hidden": "true" }, "✓"), "0"),
          h("div", { class: "note", title: "nothing blocked, no stale claims, no failed criteria" }, "nothing blocked or stale"))),
    stuck && stuck.el, body);
}

// attention lists what is stuck: blocked tasks, claims whose lease expired
// or ends within the hour, done tasks with failed criteria or checks.
function attention(p, all) {
  const link = (x) => h("a", { class: "mono", href: projectHref(p.id, "work", x.id) }, x.id);
  const items = [];
  for (const x of all) {
    if (x.status === "blocked") items.push(["warn", x, `blocked: ${x.blocked_reason || "no reason given"}`]);
    const [left, cls] = lease(x.claim);
    if (x.claim && cls) items.push([cls, x, `${x.status.replace("_", " ")} by ${x.claim.claimant}: ${left}`]);
    if (x.status === "done") {
      const failed = (x.acceptance || []).filter((c) => c.result === "fail").length + ((x.completion && x.completion.checks) || []).filter((c) => c.result === "fail").length;
      if (failed) items.push(["warn", x, `done with ${failed} failed criterion/check(s)`]);
    }
  }
  if (!items.length) return null;
  // as Overview lists what needs attention: the level, the task, why
  const rank = { bad: 0, warn: 1, "": 2 };
  items.sort((a, b) => rank[a[0]] - rank[b[0]]);
  const el = section("Needs attention", null, h("ul", { class: "attn" }, items.map(([cls, x, why]) =>
    h("li", {}, levelPill(cls === "bad" ? "problem" : "warn"), link(x), h("span", { class: "atitle" }, x.title), h("span", { class: "awhy" }, why)))));
  el.id = "stuck";
  return { count: items.length, worst: items[0][0] === "bad" ? "problem" : "warn", el };
}

// lease says how long a claim still holds: [text, class].
function lease(claim) {
  if (!claim || !claim.lease_until) return ["", ""];
  const left = (new Date(claim.lease_until).getTime() - Date.now()) / 60000;
  if (left <= 0) return ["lease expired", "bad"];
  const text = left < 90 ? `${Math.round(left)} min left` : `${Math.round(left / 60)} h left`;
  return [text, left < 60 ? "warn" : ""];
}

function taskRow(x, redraw) {
  const t = state.tasks;
  const open = t.open.has(x.id);
  const acc = x.acceptance || [];
  const passed = acc.filter((c) => c.result === "pass" || c.result === "n/a").length;
  const toggle = () => { open ? t.open.delete(x.id) : t.open.add(x.id); redraw(); };
  const row = h("div", {
    class: "trow" + (open ? " open" : ""), role: "button", tabindex: "0", "aria-expanded": String(open),
    onclick: toggle, onkeydown: (ev) => { if (ev.key === "Enter" || ev.key === " ") { ev.preventDefault(); toggle(); } },
  },
    chevron(open),
    h("a", { class: "tid mono", href: projectHref(route().id, "work", x.id), onclick: (ev) => ev.stopPropagation(), title: "Link to this task" }, x.id),
    h("span", { class: "ttitle" }, x.title, x.claim && (() => {
      const [left, cls] = lease(x.claim);
      return h("span", { class: `claimtag ${cls}`, title: `claimed ${x.claim.claimed_at || ""}` }, `${x.claim.claimant}${left ? " · " + left : ""}`);
    })()),
    // Always present (empty without criteria) so the columns line up.
    h("span", { class: "tacc", title: acc.length ? "acceptance criteria met" : "" }, acc.length ? `${passed}/${acc.length}` : ""),
    h("span", { class: `prio p-${x.priority}` }, x.priority),
    statusPill(x.status));
  return h("div", { class: "task" + (open ? " open" : ""), id: `task-${x.id}` }, row, open && taskDetail(x, passed));
}

// auditBlock loads a task's audit trail on request (harness tasks show --events).
function auditBlock(x) {
  const out = h("div", { class: "tblock" });
  const show = h("button", {
    class: "ghost",
    onclick: async () => {
      fill(out, h("h4", {}, "Audit trail"), h("p", { class: "empty" }, "Loading…"));
      try {
        const res = await api(projectAPI({ id: route().id }, `tasks/${encodeURIComponent(x.id)}/events`));
        fill(out, h("h4", {}, "Audit trail"), h("ul", { class: "events" }, (res.events || []).map((e) => h("li", {},
          h("span", { class: "ev" }, e.event.replaceAll("_", " ")), " ",
          e.from && e.to && h("span", {}, statusPill(e.from), " → ", statusPill(e.to)), " ",
          h("span", { class: "muted" }, `${e.actor} · ${ago(e.at)}`),
          e.detail && h("div", { class: "evidence" }, e.detail)))));
      } catch (e) {
        fill(out, h("p", { class: "error" }, e.message));
      }
    },
  }, "Show audit trail");
  out.append(show);
  return out;
}

function taskDetail(x, passed) {
  const acc = x.acceptance || [];
  const resIcon = { pass: "✓", fail: "✕", pending: "○", "n/a": "–" };
  const panel = h("div", { class: "mpanel" });
  const editable = x.status === "ready" || x.status === "backlog";
  const edit = async () => {
    const opening = h("div", { class: "aform" }, h("p", { class: "hint" }, "Loading task…"));
    fill(panel, opening);
    try {
      const form = await editTaskForm({ id: route().id }, panel, x);
      if (opening.parentNode === panel) fill(panel, form);
    } catch (e) { if (opening.parentNode === panel) fill(panel, h("p", { class: "error" }, e.message)); }
  };
  return h("div", { class: "tdetail" },
    h("div", { class: "tmain" },
      editable && h("div", { class: "review" }, h("button", { class: "ghost", onclick: edit }, "Edit…")),
      panel,
      x.blocked_reason && (x.status === "deferred"
        ? h("div", { class: "callout" }, h("b", {}, "Deferred: "), x.blocked_reason)
        : h("div", { class: "callout bad" }, h("b", {}, "Blocked: "), x.blocked_reason)),
      x.goal && h("div", { class: "tblock" }, h("h4", {}, "Goal"), h("p", {}, x.goal)),
      acc.length > 0 && h("div", { class: "tblock" },
        h("h4", {}, "Acceptance criteria ", h("span", { class: "count" }, `${passed} of ${acc.length} met`)),
        h("ul", { class: "checklist" }, acc.map((c) => h("li", { class: `res-${c.result.replace("/", "")}` },
          h("span", { class: "ico", title: c.result }, resIcon[c.result] || "•"),
          h("div", {}, c.criterion, c.evidence && h("div", { class: "evidence" }, c.evidence)))))),
      x.completion && h("div", { class: "tblock" }, h("h4", {}, "Completed"),
        h("p", {}, x.completion.summary),
        (x.completion.checks || []).length > 0 && h("ul", { class: "checklist" }, x.completion.checks.map((c) =>
          h("li", { class: `res-${c.result}` }, h("span", { class: "ico" }, resIcon[c.result] || "•"), h("div", {}, h("code", {}, c.command), c.output && h("div", { class: "evidence" }, c.output)))))),
      x.notes && h("div", { class: "tblock" }, h("h4", {}, "Notes"), h("p", { class: "muted" }, x.notes)),
      auditBlock(x)),
    h("aside", { class: "tside" }, kv([
      ["Status", statusPill(x.status)],
      ["Priority", x.priority],
      ["Type", x.type],
      ["Blueprint", x.blueprint],
      ["Depends on", (x.depends_on || []).map((d) => h("div", { class: "dep" }, h("code", {}, d.id), " ", statusPill(d.status)))],
      ["Scope", (x.scope || []).map((s) => h("div", {}, h("code", {}, s)))],
      ["Claimed by", x.claim && [x.claim.claimant,
        x.claim.claimed_at && h("div", { class: "muted small" }, `since ${ago(x.claim.claimed_at)}`),
        x.claim.lease_until && h("div", { class: `small ${lease(x.claim)[1] === "bad" ? "flag bad" : "muted"}` }, `${lease(x.claim)[0]} (until ${x.claim.lease_until.slice(0, 16).replace("T", " ")})`)]],
      ["Done", x.completion && [x.completion.completed_by, h("div", { class: "muted small" }, ago(x.completion.completed_at)), x.completion.commit && h("div", {}, h("code", {}, x.completion.commit.slice(0, 12)))]],
      ["Validation", (x.validation || []).map((v) => h("div", {}, h("code", {}, v)))],
    ])));
}

// --- boot ---
async function loadProjects() {
  try {
    const [res, ws, st] = await Promise.all([api("/api/projects"), api("/api/workspaces").catch(() => ({ workspaces: [] })), api("/api/stacks").catch(() => ({}))]);
    state.projects = res.projects || [];
    state.workspaces = ws.workspaces || [];
    state.stacks = st || {};
  } catch (e) {
    fill(document.getElementById("main"), h("p", { class: "error" }, `Could not read the machine registry: ${e.message}`));
    return false;
  }
  return true;
}

// --- loading screen ---
// The logo, inline so its parts can move: it starts without its radiance,
// the rays light up one by one from the capstone and the glow breathes
// until the first page is drawn.
const splashStart = Date.now();
async function showSplash() {
  const el = document.getElementById("splash");
  if (!el) return;
  try {
    const text = await (await fetch("logo.svg")).text();
    const svg = new DOMParser().parseFromString(text, "image/svg+xml").documentElement;
    if (svg.nodeName !== "svg") return;
    svg.querySelectorAll(".ray").forEach((ray, i) => ray.style.setProperty("--i", String(i)));
    svg.setAttribute("aria-hidden", "true");
    el.append(h("div", { class: "splash-mark" }, h("div", { class: "splash-halo" }), document.importNode(svg, true)),
      h("div", { class: "splash-name" }, "harness"));
  } catch { /* the page loads without it */ }
}
function hideSplash() {
  const el = document.getElementById("splash");
  if (!el || el.classList.contains("done")) return;
  // Long enough to see the light come up, never longer than the load.
  setTimeout(() => {
    el.classList.add("done");
    setTimeout(() => el.remove(), 400);
  }, Math.max(0, 1100 - (Date.now() - splashStart)));
}

async function refresh() {
  live.last = Date.now();
  if (!state.build) api("/api/machine").then((m) => { state.build = m.build; }).catch(() => {});
  try {
    if (await loadProjects()) { renderProjects(); await renderMain(); }
  } finally {
    hideSplash(); // a failed render must not leave the page hidden
  }
}

// --- live updates (HAR-0154) ---
// The server sends a "change" event (Server-Sent Events) when the machine
// registry or a project database changed, whoever changed it. Bursts are
// coalesced: an update waits LIVE_QUIET after the last event and at least
// LIVE_GAP after the previous update, and none runs while the tab is hidden
// or a form is open in it (it runs once that is over). A dropped stream
// reconnects by itself and then refetches everything.
const LIVE_QUIET = 500, LIVE_GAP = 3000;
const live = { pending: null, timer: 0, last: 0 };

function queueLive(c) {
  const p = live.pending || (live.pending = { registry: false, workspaces: false, all: false, kinds: {} });
  p.registry = p.registry || !!c.registry;
  p.workspaces = p.workspaces || !!c.workspaces;
  p.all = p.all || !!c.all;
  for (const id of c.projects || []) {
    const ks = (c.kinds && c.kinds[id]) || ["all"];
    p.kinds[id] = [...new Set([...(p.kinds[id] || []), ...ks])];
  }
  scheduleLive(LIVE_QUIET);
}

function scheduleLive(quiet) {
  if (!live.pending || document.hidden) return; // visibilitychange resumes
  clearTimeout(live.timer);
  live.timer = setTimeout(applyLive, Math.max(quiet, live.last + LIVE_GAP - Date.now()));
}

// applyLive applies what changed to what the page shows (TASK-0047): the
// named projects' summaries (their sidebar lines and cards) and the
// sections showing the kinds that changed, in place. Only projects added,
// forgotten, moved or renamed redraw the page.
async function applyLive() {
  live.timer = 0;
  const c = live.pending;
  if (!c || document.hidden) return;
  if (c.all || c.registry) {
    // an open correction, approval or rejection form would be lost: wait
    if (document.querySelector("#main .aform, #main .decide")) return scheduleLive(2000);
    live.pending = null;
    live.last = Date.now();
    if (await loadProjects()) { renderProjects(); await renderMain({ live: true }); }
    return;
  }
  live.pending = null;
  live.last = Date.now();
  if (c.workspaces) {
    try { state.workspaces = (await api("/api/workspaces")).workspaces || []; } catch { /* the next change retries */ }
  }
  // a project's summary changes with everything but its agents' activity
  const ids = Object.keys(c.kinds).filter((id) => c.kinds[id].some((k) => k !== "activity"));
  await Promise.all(ids.map(async (id) => {
    try {
      const res = await api(`/api/projects/${enc(id)}/summary`);
      const i = state.projects.findIndex((p) => p.id === id);
      if (i >= 0) state.projects[i] = res.project;
    } catch { /* the next change retries */ }
  }));
  if (ids.length || c.workspaces) renderProjects();
  invalidate(c);
}

function connectLive() {
  let poll = 0;
  const stopPolling = () => { clearInterval(poll); poll = 0; };
  const startPolling = () => {
    if (!poll) poll = setInterval(() => { if (!document.hidden) queueLive({ all: true }); }, 15000);
  };
  if (!window.EventSource) { startPolling(); return; }
  // While the stream is live, changes arrive by themselves and Refresh is
  // hidden; offline, the badge itself reloads the page's data.
  const badge = h("button", { class: "live off ghost", role: "status", onclick: () => { if (badge.classList.contains("off")) refresh(); } });
  const refreshBtn = document.getElementById("refresh");
  refreshBtn.before(badge);
  const show = (on, text, title) => {
    badge.className = `live ${on ? "on" : "off"} ghost`; badge.textContent = text; badge.title = title;
    refreshBtn.hidden = on;
  };
  let opened = false;
  function connect() {
    const es = new EventSource("/api/events");
    es.addEventListener("open", () => {
      stopPolling();
      show(true, "● live", "Changes by agents, the CLI, hooks and sync show up here by themselves.");
      if (opened) queueLive({ all: true }); // whatever changed while disconnected
      opened = true;
    });
    es.addEventListener("change", (ev) => {
      try { queueLive(JSON.parse(ev.data)); } catch { queueLive({ all: true }); }
    });
    es.addEventListener("error", () => {
      startPolling();
      show(false, "○ offline — reload", "The live update stream dropped (it reconnects by itself); click to reload now.");
      // the browser retries a dropped stream itself, but not a refused one
      if (es.readyState === EventSource.CLOSED) { es.close(); setTimeout(connect, 15000); }
    });
  }
  connect();
  document.addEventListener("visibilitychange", () => scheduleLive(LIVE_QUIET));
}

document.addEventListener("DOMContentLoaded", () => {
  showSplash();
  const days = document.getElementById("days");
  days.value = state.days;
  days.addEventListener("change", () => { state.days = days.value; store.set("days", days.value); renderMain(); });
  document.getElementById("refresh").addEventListener("click", refresh);
  applyTheme();
  document.getElementById("theme").addEventListener("click", () => {
    const t = store.get("theme", "system");
    store.set("theme", THEMES[(THEMES.indexOf(t) + 1) % THEMES.length]);
    applyTheme();
  });
  const search = document.getElementById("search");
  search.closest("form").addEventListener("submit", (ev) => {
    ev.preventDefault();
    location.hash = `#/search?q=${encodeURIComponent(search.value.trim())}`;
  });
  quickSearch(search);
  document.addEventListener("keydown", (ev) => {
    const typing = /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement && document.activeElement.tagName);
    if ((ev.key === "k" && (ev.metaKey || ev.ctrlKey)) || (ev.key === "/" && !typing)) { ev.preventDefault(); search.focus(); search.select(); }
  });
  const menu = document.getElementById("menu");
  menu.addEventListener("click", () => {
    const open = document.getElementById("projects").classList.toggle("open");
    menu.setAttribute("aria-expanded", String(open));
  });
  window.addEventListener("hashchange", () => { renderProjects(); renderMain(); });
  refresh();
});
// After load: a stream opened earlier keeps the page loading in some browsers.
window.addEventListener("load", () => setTimeout(connectLive, 0));
