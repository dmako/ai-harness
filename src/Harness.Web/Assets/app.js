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
// MEMORY_TYPE_HELP says what each type is for, an example, and where it
// reaches agents (TASK-0056; docs/reference.md "Knowledge types").
const MEMORY_TYPE_HELP = {
  goal: ["What the work is for: an outcome to optimize toward.", "Optimize code intelligence for C#/.NET first.", "goals_current, and the top goals in every get_context"],
  constraint: ["A hard limit set from outside: what must or must not happen, not ours to change.", "Keycloak must not be changed for now.", "get_context, decisions_for and constraints_current"],
  decision: ["A choice made between alternatives, with its reason: why the design is this way.", "Live updates use Server-Sent Events, not WebSocket: the page only needs to know what changed.", "get_context and decisions_for"],
  convention: ["How code and work are done here, in every new change.", "CancellationToken is always the last parameter of an async method.", "get_context and decisions_for; offered to other projects as a convention they may adopt"],
  warning: ["A trap: what breaks or misleads, and how to avoid it.", "graphify-csharp ignores SIGTERM: stop its sessions by id.", "get_context and decisions_for"],
  fact: ["Something true that the code does not show: who uses what, where data comes from.", "Tenant GUIDs originate in a Confluence table.", "get_context, where it applies or matches the task, and memory_search"],
  open_question: ["Not decided yet: what is open and what it waits for.", "Do roles exist on the organization server, and who approves?", "get_context, where it applies or matches the task (an agent asks rather than decides it), and memory_search"],
};

// typeSelect is a memory type's select, the chosen type's meaning,
// example and reach under it.
function typeSelect(value) {
  const help = h("p", { class: "typehelp muted small" });
  const sel = h("select", { name: "type", "aria-label": "Type", onchange: () => explain() },
    MEMORY_TYPES.map((t) => h("option", { value: t, selected: t === value || null }, `${MEMORY_GROUPS[t].replace(/s$/, "")}: ${MEMORY_TYPE_HELP[t][0]}`)));
  const explain = () => {
    const [, example, reach] = MEMORY_TYPE_HELP[sel.value];
    fill(help, h("span", {}, "e.g. ", h("i", {}, example)), h("br"), h("span", {}, `Reaches agents: ${reach}.`));
  };
  explain();
  return { sel, help };
}
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
  tasks: { statuses: new Set(["ready", "claimed", "in_progress", "review", "blocked"]), milestone: "", open: new Set(), collapsed: new Set() },
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
// a state's drawn icon (font characters come out at uneven sizes)
const STATE_ICON = { ok: "st-ok", stale: "refresh", warn: "st-warn", bad: "st-fail" };
const stateIcon = (k) => icon(STATE_ICON[k]);
const STATE_WORD = { ok: "ok", stale: "stale index", warn: "needs a fix", bad: "unreadable" };
// stateGlyph is a compact state: a shape per state (never colour alone)
// with the full text as its label.
function stateGlyph(live) {
  const k = stateKind(live);
  const text = live.state === "ok" ? STATE_WORD[k] : live.state.replaceAll("_", " ");
  return h("span", { class: `glyph g-${k}`, title: text, "aria-label": text, role: "img" }, stateIcon(k));
}

// projectMark is a project's monogram in its own colour, the same
// everywhere (sidebar, header, cards, activity): "Fleet Admin WebAPI" FA,
// "Route Planner WebAPI" RP, "Acme.Core" AC.
const MARK_SKIP = new Set(["webapi", "api", "web", "service", "services", "app"]);
function projectInitials(name) {
  const words = (name || "?").split(/[\s._-]+/).filter((w) => w && !MARK_SKIP.has(w.toLowerCase()));
  if (words.length >= 2) return (words[0][0] + words[1][0]).toUpperCase();
  const w = words[0] || name || "?";
  const caps = w.match(/[A-Z]/g) || [];
  return (caps.length >= 2 ? caps[0] + caps[1] : w.slice(0, 2)).toUpperCase();
}
function projectMark(p, big) {
  const id = (p && (p.id || p.project_id)) || "";
  let hash = 0;
  for (const c of id) hash = (hash * 31 + c.charCodeAt(0)) >>> 0;
  // a registered project by its shown name (the folder's when it has none)
  const name = p ? (p.root || p.title || p.name ? pname(p) : p.project || "") : "";
  return h("span", { class: `pmark pc-${hash % 8}${big ? " big" : ""}`, "aria-hidden": "true", title: name }, projectInitials(name));
}
// markFor finds a project by id for its mark.
const markFor = (id) => { const p = state.projects.find((x) => x.id === id); return p ? projectMark(p) : null; };

// shortPath is a folder as its parent and name ("…/Source/core"); the full
// path is the element's title.
function shortPath(p) {
  const parts = (p || "").split(/[\\/]/).filter(Boolean);
  return parts.length > 2 ? "…/" + parts.slice(-2).join("/") : p;
}

// plural is a count with its noun: "1 call", "3 calls"; irregular plurals
// are passed (plural(n, "copy", "copies")).
const plural = (n, one, many) => `${n} ${n === 1 ? one : many || one + "s"}`;

// healthBadge is a project's state in one word, linking to Health: what
// is wrong if anything, otherwise healthy.
function healthBadge(p) {
  const l = p.live;
  const href = projectHref(p.id, "health");
  if (l.state !== "ok") return h("a", { href, class: "badge-link" }, stateBadge(l));
  const [cls, label] = l.checks_failed > 0 ? ["bad", `${plural(l.checks_failed, "check")} failing`] : l.stale ? ["warn", "index stale"]
    : l.checks_warned > 0 ? ["warn", plural(l.checks_warned, "warning")] : ["ok", "healthy"];
  return h("a", { href, class: "badge-link", title: l.stale && !l.checks_failed ? `${l.stale}. See Health` : "See Health" }, h("span", { class: `state ${cls}` }, label));
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


function statusPill(status) {
  return h("span", { class: `pill st-${status}` }, h("span", { class: "ico", "aria-hidden": "true" }, STATUS_ICON[status] || "•"), status.replace("_", " "));
}
function levelPill(level) {
  const icon = { ok: "✓", warn: "▲", fail: "✕", problem: "✕", info: "ⓘ" }[level] || "•";
  return h("span", { class: `pill lv-${level}`, title: level }, h("span", { class: "ico", "aria-hidden": "true" }, icon), h("span", { class: "pword" }, level));
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
// A workspace's views mirror a project's: Architecture second, Issues where
// a project has Health; each groups its parts as sub-views.
const WORKSPACE_VIEWS = [["overview", "Overview"], ["architecture", "Architecture"], ["knowledge", "Knowledge"], ["issues", "Issues"], ["members", "Members"]];
// views of the earlier layout, now sub-views
const WORKSPACE_MOVED = { shared: ["issues", "shared"], libraries: ["architecture", "libraries"] };
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
const workspaceHref = (name, view, item) => `#/w/${enc(name)}/${view || "overview"}${item ? "/" + item : ""}`;

function route() {
  const raw = location.hash.replace(/^#\/?/, "");
  const [path, query] = raw.split("?");
  const seg = path.split("/").filter(Boolean).map((s) => { try { return decodeURIComponent(s); } catch { return s; } });
  const r = { page: "home", id: "", ws: "", view: "", item: "", q: new URLSearchParams(query || "").get("q") || "", seg };
  switch (seg[0]) {
    case undefined: return r;
    case "p": return { ...r, page: "project", id: seg[1] || "", view: PROJECT_VIEWS.some(([v]) => v === seg[2]) ? seg[2] : defaultView(), item: seg.slice(3).join("/") };
    case "w": {
      const [view, item] = WORKSPACE_MOVED[seg[2]] || [WORKSPACE_VIEWS.some(([v]) => v === seg[2]) ? seg[2] : "overview", seg[3] || ""];
      return { ...r, page: "workspace", ws: seg[1] || "", view, item };
    }
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
  else if (r.page === "docs") crumbs.push(["Docs", ""]);
  const el = document.getElementById("crumbs");
  // the first crumb, Home, is its icon unless Home is the page
  const home = (label, i) => i === 0 && crumbs.length > 1 ? [icon("home"), h("span", { class: "sr" }, label)] : label;
  fill(el, ...crumbs.flatMap(([label, href], i) => [
    i > 0 && h("span", { class: "sep", "aria-hidden": "true" }, "›"),
    href && i < crumbs.length - 1 ? h("a", { href, title: label }, home(label, i)) : h("span", { "aria-current": "page" }, label)]).filter(Boolean));
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
// It is the sum of the project's tab badges (Knowledge, Work, Health),
// plus one when its state cannot be read.
const workProposals = (l) => (l.pending_arch_proposals || 0) + (l.pending_task_proposals || 0) + (l.pending_blueprint_proposals || 0);
const waitingIn = (p) => (p.live.pending_proposals || 0) + workProposals(p.live) + (p.live.checks_failed || 0) + (p.live.state !== "ok" ? 1 : 0);

function navProject(p, ws, r) {
  const n = waitingIn(p);
  return h("a", {
    class: "nrow", href: projectHref(p.id, r.page === "project" ? r.view : ""), "aria-current": r.page === "project" && r.id === p.id && (!ws || state.lastWs === ws || !(p.workspaces || []).includes(state.lastWs)) ? "page" : null,
    onclick: () => { state.lastWs = ws || ""; document.getElementById("projects").classList.remove("open"); },
  }, projectMark(p), h("span", { class: "nname" }, pname(p)), stateGlyph(p.live),
  // the badge's slot stays when it is empty, so the state icons line up
  h("span", { class: "nslot" }, n > 0 && h("span", { class: "badge", title: `${n} waiting for you: proposals, failing checks or unreadable state` }, n)));
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
    return ["bad", "warn", "stale", "ok"].filter((k) => count[k]).map((k) => h("span", { class: `glyph g-${k}`, title: `${count[k]} ${STATE_WORD[k]}` }, stateIcon(k), h("span", { class: "gcount" }, count[k])));
  };
  const outside = state.projects.filter((p) => !inWs.has(p.id) && hit(p));
  fill(nav, [
    h("a", { class: "ntop", href: "#/", "aria-current": r.page === "home" ? "page" : null, onclick: close }, icon("home"), "Home"),
    h("a", { class: "ntop", href: "#/search", "aria-current": r.page === "search" ? "page" : null, onclick: close }, icon("search"), "Search"),
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
            h("span", { class: "nicon", "aria-hidden": "true" }, icon("workspace")), h("span", { class: "nname" }, wname(w)), !isOpen && h("span", { class: "count", title: plural(w.members.length, "project") }, w.members.length)),
          waiting > 0 && h("span", { class: "badge", title: `${waiting} waiting for you in its projects` }, waiting)),
        !isOpen && h("div", { class: "nsum" }, summary(all)),
        isOpen && h("div", { class: "nmembers" }, shown.map((p) => navProject(p, w.name, r)),
          !q && sorted.length > shown.length && h("a", { class: "nmore", href: workspaceHref(w.name, "members"), onclick: close }, `All ${sorted.length} projects →`)));
    }) : h("p", { class: "nempty" }, h("a", { href: "#/", onclick: close }, "Create one on Home"), " to search repositories together."),
    // the heading only when it lists something (or nothing is registered)
    (outside.length > 0 || !q && !wss.length) && h("h2", { class: "nhead" }, "Projects", h("span", { class: "count" }, outside.length)),
    outside.map((p) => navProject(p, "", r)),
    !state.projects.length && h("p", { class: "nempty" }, "No projects registered yet. 'harness init' in a repository registers it."),
    q && !outside.length && !wss.some((w) => w.members.some((m) => { const p = state.projects.find((x) => x.id === m.project_id); return p && hit(p); })) && h("p", { class: "nempty" }, "No project matches."),
    h("div", { class: "nfoot" },
      h("a", { class: "ntop", href: "#/knowledge", "aria-current": r.page === "knowledge" ? "page" : null, onclick: close }, icon("knowledge"), "Your knowledge"),
      h("a", { class: "ntop", href: "#/machine", "aria-current": r.page === "machine" ? "page" : null, onclick: close }, icon("machine"), "Machine"),
      h("a", { class: "ntop", href: "#/docs", "aria-current": r.page === "docs" ? "page" : null, onclick: close }, icon("docs"), "Docs")),
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
  renderCrumbs();
  // the window applies to what agents did: Home's project cards, a
  // project's Overview and Agents; elsewhere it would change nothing
  const r = route();
  const win = document.querySelector(".top .window");
  if (win) win.hidden = !(r.page === "home" || r.page === "project" && (r.view === "overview" || r.view === "agents"));
  if (!live) {
    // a page you open rises in; a live update redraws in place, still
    main.classList.remove("enter"); void main.offsetWidth; main.classList.add("enter");
    state.freshUntil = Date.now() + 4000;
    clearTimeout(renderMain.enterTimer); renderMain.enterTimer = setTimeout(() => main.classList.remove("enter"), 600);
    return drawMain(main, false);
  }
  if (main.querySelector(".aform, .decide")) return;
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
// summary; registry and workspaces for the machine's lists; levels for the
// workspaces' and the user's knowledge databases, TASK-0065). A live change
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
    // the first time, the shape of what is coming, shimmering
    if (first) fill(el, h("div", { class: "loadbar skel", role: "progressbar", "aria-label": "Loading" }, h("span", {}), h("span", {}), h("span", {})));
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
  const before = m.drawn ? el.textContent : null;
  fill(el, draw());
  for (const s of el.querySelectorAll("details > summary")) if (open.has(s.textContent)) s.parentElement.open = true;
  if (el.isConnected) window.scrollTo(x, y);
  restoreFocus(el, focus);
  // a redraw that changed something glows a moment; a page just opened
  // counts its numbers up
  if (before != null && before !== el.textContent) { el.classList.remove("flash"); void el.offsetWidth; el.classList.add("flash"); }
  if (!m.drawn && Date.now() < (state.freshUntil || 0)) countUp(el);
  m.drawn = true;
}

// countUp runs a section's headline numbers up from zero (tiles, rings,
// totals): "12", "72%", "1,240".
function countUp(root) {
  if (window.matchMedia && matchMedia("(prefers-reduced-motion: reduce)").matches) return;
  for (const el of root.querySelectorAll(".tile .value, .rval, .asum > b, .mt-meter b")) {
    const t = el.firstChild;
    if (!t || t.nodeType !== 3) continue;
    const m = /^(\d[\d,]*)(%?)(.*)$/.exec(t.textContent.trim());
    if (!m) continue;
    const to = Number(m[1].replace(/,/g, "")), suffix = m[2] + m[3];
    if (!to) continue;
    const start = performance.now(), dur = 700;
    const step = (now) => {
      const f = Math.min(1, (now - start) / dur), e = 1 - Math.pow(1 - f, 3);
      t.textContent = Math.round(to * e).toLocaleString("en") + suffix;
      if (f < 1) requestAnimationFrame(step);
    };
    t.textContent = "0" + suffix;
    requestAnimationFrame(step);
  }
}

// wants: a change shows something a section draws.
function wants(m, c) {
  const ks = m.o.kinds || [];
  if (c === "all") return true;
  if (!ks.length) return false;
  if ((c.registry && ks.includes("registry")) || (c.workspaces && ks.includes("workspaces"))) return true;
  if ((c.levels || []).length && ks.includes("levels")) return true;
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

// emptyState is a list with nothing in it, said kindly: a drawn icon, a
// title and a line (TASK-0057).
function emptyState(name, title, text) {
  return h("div", { class: `emptystate es-${name}` }, h("span", { class: "esico" }, icon(name)),
    h("div", {}, h("b", {}, title), h("p", { class: "muted small" }, text)));
}

// toast says a moment that something happened, at the bottom corner.
function toast(text, bad, detail) {
  const t = h("div", { class: "toast" + (bad ? " bad" : ""), role: bad ? "alert" : "status", title: detail || "" },
    h("span", { class: "tick", "aria-hidden": "true" }, bad ? "✕" : "✓"), h("span", {}, text, detail && bad && h("span", { class: "tdetail" }, detail)));
  document.body.append(t);
  const stay = bad ? 6000 : 1600;
  setTimeout(() => t.classList.add("out"), stay);
  setTimeout(() => t.remove(), stay + 400);
}

// afterWrite follows a write made on this page: the lists and the
// sections, not the page around them.
async function afterWrite() {
  live.last = Date.now();
  toast("Done");
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
    h("div", { class: "title" }, projectMark(p, true), h("h2", {}, pname(p)),
      iconButton("edit", "Rename: change the display name or the short name", () => renameDialog({
        what: "project", display: p.title || "", short: p.name || "", folder: baseName(p.root), mark: p,
        send: (body) => post(projectAPI(p, "rename"), body),
      })),
      healthBadge(p)),
    h("div", { class: "where" },
      h("span", { class: "mono", title: p.root }, shortPath(p.root)),
      p.name && p.name !== baseName(p.root) && h("span", { class: "muted" }, `short name ${p.name}`),
      (p.workspaces || []).length > 0 && h("span", { class: "inws" }, "in ",
        p.workspaces.map((w) => h("a", { class: "wschip", href: workspaceHref(w), title: "Open this workspace", onclick: () => { state.lastWs = w; } }, wtitle(w))))),
    facts.length > 0 && h("p", { class: "metaline" }, facts),
    l.detail && h("p", { class: "detail" }, l.detail));
  const badges = { knowledge: l.pending_proposals, work: workProposals(l), health: l.checks_failed };
  const tabs = h("div", { class: "tabs", role: "tablist", "aria-label": "Views" },
    PROJECT_VIEWS.map(([v, label]) => h("a", { href: projectHref(id, v), role: "tab", "aria-selected": String(v === view) },
      label, badges[v] > 0 && h("span", { class: v === "health" ? "badge bad" : "badge" }, badges[v]))));
  return [head, tabs];
}

// VIEW_KINDS: the kinds of data each project view shows (Health: none, it
// runs doctor's checks when opened or refreshed).
const VIEW_KINDS = {
  overview: ["summary", "tasks", "knowledge", "proposals", "findings", "sync"], architecture: ["findings", "sync"],
  "work/inbox": ["proposals"], work: ["tasks", "proposals"], health: [], "knowledge/inbox": ["proposals"], knowledge: ["knowledge", "levels"],
  "agents/tools": ["activity", "sync"], agents: ["activity", "knowledge", "proposals"],
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
    case "architecture": {
      // four parts, one at a time: the structure and its smells, the
      // layers, complexity, what changes
      const sec = ["layers", "complexity", "coupling"].includes(item) ? item : "structure";
      const nav = subnav(base, [["structure", "Structure"], ["layers", "Layers"], ["complexity", "Complexity"], ["coupling", "Change coupling"]], sec);
      return h("div", {}, nav, await viewArchitecture(p, sec));
    }
    case "work": {
      // one switch: Blueprints, Tasks, Inbox; a task's id opens Tasks
      // (#…/work/milestones, the former name, opens Blueprints)
      const sec = item === "milestones" ? "blueprints" : ["blueprints", "tasks", "inbox"].includes(item) ? item : item ? "tasks" : state.tasks.view || "blueprints";
      if (sec !== "inbox") state.tasks.view = sec;
      const nav = subnav(base, [["blueprints", "Blueprints"], ["tasks", "Tasks"], ["inbox", "Inbox", workProposals(p.live)]], sec);
      return h("div", {}, nav, sec === "inbox" ? await viewArchProposals(p) : await viewTasks(p, opts, sec));
    }
    case "health": return viewHealth(p);
    case "knowledge": {
      const inbox = item === "inbox";
      const nav = subnav(base, [["memories", "Memories"], ["inbox", "Inbox", p.live.pending_proposals]], inbox ? "inbox" : "memories");
      return h("div", {}, nav, inbox ? await viewProposals(p) : await viewKnowledge(p));
    }
    case "agents": {
      // what the user acts on; how harness itself performs is the
      // developer's, in harness-collect over opted-in telemetry, never here
      // (TASK-0053)
      const sec = item === "tools" ? "tools" : "serving";
      const nav = subnav(base, [["serving", "How harness serves them"], ["tools", "Tools & providers"]], sec);
      const content = await { serving: viewServing, tools: viewProviders }[sec](p);
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
  overview: ["summary", "findings", "sync", "workspaces", "registry", "levels"], issues: ["sync", "findings", "levels"], knowledge: ["workspaces", "levels"],
  members: ["workspaces", "registry", "summary", "sync"], architecture: ["sync", "findings", "levels"],
};

// workspaceHead is a workspace page's header and tabs.
function workspaceHead(w, members, r) {
  if (!w) return h("p", { class: "error" }, `There is no workspace ${r.ws} on this machine.`);
  const waiting = members.reduce((s, p) => s + waitingIn(p), 0);
  const head = h("div", { class: "phead" },
    h("div", { class: "title" }, h("span", { class: "kind" }, "workspace"), h("h2", {}, wname(w)),
      iconButton("edit", "Rename: change the display name or the short name", () => renameDialog({
        what: "workspace", display: w.title || "", short: w.name,
        send: async (body) => { const res = await post(`/api/workspaces/${enc(w.name)}/rename`, body); location.hash = workspaceHref(res.workspace, route().view); },
      })),
      h("button", {
        class: "copychip", title: `Agents and commands name this workspace workspace:${w.name}. Click to copy.`,
        onclick: (ev) => { navigator.clipboard?.writeText(`workspace:${w.name}`); ev.currentTarget.classList.add("copied"); },
      }, `workspace:${w.name}`)),
    h("p", { class: "path" }, `${plural(w.members.length, "project")}, created ${ago(w.created)}`,
      waiting > 0 ? ` · ${waiting} waiting for you` : ""));
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
    case "issues": {
      // findings (the code repeated across members among them) and
      // version drift; an old link to shared code opens the findings
      const sec = item === "versions" ? "versions" : "findings";
      const nav = subnav(workspaceHref(w.name, "issues"), [["findings", "Findings"], ["versions", "Version drift"]], sec);
      if (sec === "versions") {
        const [stk, drift] = await Promise.all([api(`/api/workspaces/${enc(w.name)}/stack`).catch(() => null), issuesView(w, "version-drift")]);
        return h("div", {}, nav, drift, versionsTable(w, stk));
      }
      // a peer of the design issues: its smells, the full table folded in it
      const shared = h("div", { id: "repeated" }, mount({ members: () => w.members.map((m) => m.project_id), kinds: ["sync", "findings"], load: () => sharedCodeView(w) }));
      if (item === "shared") setTimeout(() => shared.scrollIntoView({ block: "start" }), 300);
      return h("div", {}, nav, await issuesView(w), shared);
    }
    case "knowledge": return levelKnowledge(`workspace:${w.name}`, "Knowledge for every project of this workspace: it applies to each of them unless a project states otherwise (then the project's own takes precedence).");
    case "members": return h("div", {}, membersManage(w));
    case "architecture": {
      const lens = ["build", "libraries"].includes(item) ? item : "runtime";
      const nav = subnav(workspaceHref(w.name, "architecture"), [["runtime", "Runtime: calls"], ["build", "Build: packages"], ["libraries", "Libraries"]], lens);
      if (lens === "runtime") return h("div", {}, nav, await systemView(w));
      if (lens === "libraries") return h("div", {}, nav, await sharedCode(w));
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
  const href = (r) => r.kind === "member" ? projectHref(r.member_id, "architecture") : (r.kind === "shared" ? workspaceHref(w.name, "issues", "shared") : workspaceHref(w.name, "architecture"));
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
      if (k) line("◇", `${name(n.project_id)} is used by ${k === of ? `all ${k}` : `${k} of ${of}`} other members`, workspaceHref(w.name, "architecture", "build"), "· a library, used through its packages");
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
    if (behind.size) line("↺", `${behind.size} member${behind.size === 1 ? " uses" : "s use"} older versions of packages from ${[...new Set([...behind.values()].flatMap((x) => [...x]))].join(", ")}`, workspaceHref(w.name, "issues", "versions"), "· than their owners declare now");
    if (stale.length) line("?", `${stale.length} package version declaration${stale.length === 1 ? " looks" : "s look"} stale`, workspaceHref(w.name, "issues", "versions"), `· ${stale.slice(0, 2).join(", ")}${stale.length > 2 ? "…" : ""}`);
    const rts = (stk.rows || []).filter((x) => x.kind === "runtime" && x.differ);
    for (const r of rts) line("↺", `${r.name} versions differ`, workspaceHref(w.name, "issues", "versions"), `· ${[...new Set(Object.values(r.versions).flat())].join(", ")}`);
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
  // wide windows read it left to right: callers, the called, outside
  const map = h("div", { class: "archmap sysmap" + (window.innerWidth >= 1100 ? " horiz" : "") });
  const svg = sv("svg", { class: "archlines", "aria-hidden": "true" });
  const nodes = {};
  let sel = null, hover = null;
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
    const el = h("button", { class: `anode syscard sys-${n.kind}${hub ? " hub" : ""}`, title: n.name, onclick: () => select(sel === n.id ? null : n.id),
      onmouseenter: () => { hover = n.id; draw(); }, onmouseleave: () => { hover = null; draw(); } },
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
      // the lines of the service pointed at (or selected) flow, toward the called
      const flow = (hover || sel) && (l.from === (hover || sel) || l.to === (hover || sel));
      const fa = a.getBoundingClientRect(), ta = b.getBoundingClientRect();
      if (map.classList.contains("horiz") && (ta.left > fa.right - 4 || ta.right < fa.left + 4)) {
        // side by side: from the caller's edge to the called's edge
        const right = ta.left > fa.right - 4;
        const x1 = (right ? fa.right : fa.left) - box.left, x2 = (right ? ta.left : ta.right) - box.left;
        const y1 = fa.top + fa.height / 2 - box.top, y2 = ta.top + ta.height / 2 - box.top, m = (x1 + x2) / 2;
        const p = sv("path", { d: `M${x1},${y1} C${m},${y1} ${m},${y2} ${x2},${y2}`, class: `sline st-${l.style}${hot ? " hot" : ""}${flow ? " flow" : ""}`, "marker-end": `url(#sa-${l.style})` });
        p.append(sv("title", {}, `${label(l.from)} → ${label(l.to)} (${STYLE_LABEL[l.style]}): ${l.what.join(", ")}`));
        lines.push(p);
        continue;
      }
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
      const p = sv("path", { d, class: `sline st-${l.style}${hot ? " hot" : ""}${flow ? " flow" : ""}`, "marker-end": `url(#sa-${l.style})` });
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
    // nothing selected: each service in a line (whom it calls, who calls
    // it, its databases), then how to read more
    if (!id) return fill(side, h("h4", {}, "Services"),
      h("ul", { class: "svclist" }, members.map((n) => {
        const out = calls.filter((l) => l.from === n.id), inn = calls.filter((l) => l.to === n.id), dbs = dbsOf(n.id);
        return h("li", {}, h("a", { href: "#", onclick: (ev) => { ev.preventDefault(); select(n.id); } }, h("b", {}, label(n.id))),
          h("div", { class: "muted small" }, [out.length && `calls ${[...new Set(out.map((l) => label(l.to)))].join(", ")}`, inn.length && `called by ${[...new Set(inn.map((l) => label(l.from)))].join(", ")}`,
            dbs.length && plural(dbs.length, "database")].filter(Boolean).join(" · ") || "no calls found"));
      })),
      h("p", { class: "hint" }, "Select a service for how it calls (gRPC contracts, HTTP, messaging) and where in the code."));
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
  // the used ones grouped by who uses whom: one line per pair, its
  // contracts as chips (twelve rows saying the same three names are one)
  const pairs = new Map();
  for (const c of used) {
    const k = `${c.clients.map(label).join(", ") || "no member"}\x00${c.servers.map(label).join(", ") || "nobody"}\x00${label(c.owner)}`;
    if (!pairs.has(k)) pairs.set(k, []);
    pairs.get(k).push(c);
  }
  const plines = h("div", { class: "cpairs" }, [...pairs].map(([k, cs]) => {
    const [cl, sv2, own] = k.split("\x00");
    return h("div", { class: "card pad cpair" },
      h("div", { class: "cphead" }, h("b", {}, cl), h("span", { class: "cparrow", "aria-hidden": "true" }, "→"), h("b", {}, sv2),
        h("span", { class: "muted small" }, ` · ${plural(cs.length, "contract")}` + (own !== sv2 ? `, declared in ${own}` : ""))),
      h("div", { class: "cpchips" }, cs.map((c) => h("span", { class: "tag mono", title: c.declared }, c.name.replace(/^I/, "").replace(/GrpcService$|Service$/, "")))));
  }));
  const contracts = s.contracts.length > 0 && section("gRPC contracts", "Who uses whose service contracts: the caller, the member serving them, and each contract.",
    used.length > 0 && plines,
    idle.length > 0 && h("details", { class: "more card pad section" }, h("summary", {}, `${plural(idle.length, "contract")} served, used by no other member`), ctable(idle)));
  return h("div", {}, head, smells,
    section("Communication", "Who calls whom and how: callers above the services they call, lines pointing from the caller. Each service shows the databases its code owns.", layout,
      silent.length > 0 && h("p", { class: "muted small" }, `Services with no call, configured address or database found: ${silent.map((n) => n.name).join(", ")}.`)),
    contracts);
}

// ICONS: stroke icons on a 24-unit grid, drawn in the text's colour.
const ICONS = {
  home: ["M3 10.5 12 3l9 7.5", "M5 9.5V21h5v-6h4v6h5V9.5"],
  search: ["M10.5 3a7.5 7.5 0 1 0 0 15a7.5 7.5 0 0 0 0-15z", "m16 16 5 5"],
  knowledge: ["M4 5.5A2.5 2.5 0 0 1 6.5 3H20v15H6.5A2.5 2.5 0 0 0 4 20.5z", "M4 20.5A2.5 2.5 0 0 0 6.5 23H20v-5", "M9 8h7M9 12h5"],
  machine: ["M3 4h18v12H3z", "M8 20h8M12 16v4"],
  docs: ["M6 2h9l5 5v15H6z", "M14 2v6h6", "M9 13h7M9 17h7"],
  workspace: ["m12 3 9 4.5-9 4.5-9-4.5z", "m3 12 9 4.5 9-4.5", "m3 16.5 9 4.5 9-4.5"],
  edit: ["M4 20h4L19 9l-4-4L4 16z", "m13.5 6.5 4 4"],
  refresh: ["M20 11a8 8 0 1 0-2.3 5.7", "M20 4v7h-7"],
  close: ["M6 6l12 12", "M18 6 6 18"],
  // states, drawn so they read the same size (font glyphs do not)
  "st-ok": ["m5 12.5 4.5 4.5L19 7.5"],
  "st-warn": ["M12 4.5 21 19.5H3z", "M12 10v4.5", "M12 17h.01"],
  "st-fail": ["M7 7l10 10", "M17 7 7 17"],
  trash: ["M4 7h16", "M10 11v6M14 11v6", "M6 7l1 13h10l1-13", "M9 7V4h6v3"],
  plus: ["M12 5v14", "M5 12h14"],
  // task types
  feature: ["M12 3l2.4 5.3 5.6.6-4.2 3.8 1.2 5.6L12 15.6 6.9 18.3l1.2-5.6L4 8.9l5.6-.6z"],
  bug: ["M9 7.5a3 3 0 0 1 6 0", "M7.5 10h9v4.5a4.5 4.5 0 0 1-9 0z", "M12 10v9", "M4 9l3.5 2M20 9l-3.5 2M4 15h3.5M20 15h-3.5M5 20l3-2.5M19 20l-3-2.5"],
  chore: ["M14.5 5.5a4 4 0 0 0-5 5L4 16l4 4 5.5-5.5a4 4 0 0 0 5-5L16 12l-4-4z"],
  spike: ["M13 2 5 13.5h6L10 22l8-11.5h-6z"],
  test: ["M9 3h6", "M10 3v6l-5 9.5A2 2 0 0 0 6.8 21h10.4a2 2 0 0 0 1.8-2.5L14 9V3", "M7.5 15h9"],
  process: ["M20 11a8 8 0 0 0-14.3-4.9", "M4 4v4h4", "M4 13a8 8 0 0 0 14.3 4.9", "M20 20v-4h-4"],
  // priorities
  "p-low": ["M12 5v14", "m6 13 6 6 6-6"],
  "p-medium": ["M5 9h14", "M5 15h14"],
  "p-high": ["M12 19V5", "m6 11 6-6 6 6"],
  "p-critical": ["M12 21V9", "m6 15 6-6 6 6", "m6 9 6-6 6 6"],
  check: ["M12 3a9 9 0 1 0 0 18a9 9 0 0 0 0-18z", "m8 12.5 2.7 2.7L16.5 9.5"],
  inbox: ["M3 13h5l1.5 3h5L16 13h5", "M5.5 5h13L21 13v6H3v-6z"],
  calm: ["M3 12c2-3 4-3 6 0s4 3 6 0 4-3 6 0", "M3 17c2-3 4-3 6 0s4 3 6 0 4-3 6 0", "M12 3v3"],
};
// iconButton is a small button that is only its icon; label names it
// for the tooltip and screen readers.
function iconButton(name, label, onclick) {
  return h("button", { class: "iconbtn", type: "button", title: label, "aria-label": label, onclick }, icon(name));
}
function icon(name) {
  return sv("svg", { class: "icon", viewBox: "0 0 24 24", width: "16", height: "16", fill: "none", stroke: "currentColor", "stroke-width": "1.8", "stroke-linecap": "round", "stroke-linejoin": "round", "aria-hidden": "true" },
    (ICONS[name] || []).map((d) => sv("path", { d })));
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
      let k = 0; // drop the prefix the names share (Acme.RoutePlanner.)
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
// issuesView is the design issues between the members: those of one kind
// (version drift, on its own view) or all the others with the findings.
async function issuesView(w, only) {
  const [r, f] = await Promise.all([api(`/api/workspaces/${enc(w.name)}/issues`), only ? null : api(`/api/workspaces/${enc(w.name)}/findings`).catch(() => null)]);
  const rollup = f && section("Findings", "Open architecture findings per member and between them, as each one's last sync recorded them; open a row for the detail and the evidence.",
    findingsRollup(w, f.rows || []));
  // copies are findings now (shared code, with homes and triage)
  const is = (r.issues || []).filter((x) => x.kind !== "local-copy" && x.kind !== "duplicate" && (only ? x.kind === only : x.kind !== "version-drift"));
  if (!is.length) return only ? h("p", { class: "okay section" }, "✓ Every package used by several members is at one version.")
    : h("div", {}, rollup, section("Design issues", null, h("p", { class: "okay" }, "✓ No design issues found between these projects.")));
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
  // the levels in one line; the kind filter only when there are kinds to choose
  const kinds = new Set(is.map((x) => x.kind)).size;
  const counts = [["problem", "problem", "problems"], ["warn", "warning", "warnings"], ["info", "note", "notes"]].map(([lv, one, many]) => n(lv) ? `${n(lv)} ${n(lv) === 1 ? one : many}` : null).filter(Boolean).join(" · ");
  return h("div", {}, rollup,
    section(only ? "Packages at different versions" : "Design issues", only ? "Each with the members using which version, and what to do." : "Smells that only show across repositories, read from the members' manifests and code. Evidence, not a verdict.",
      h("p", { class: "small" }, counts),
      kinds > 1 && h("div", { class: "deptools" }, chips),
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
      post: (action, body) => post(`/api/workspaces/${enc(w.name)}/findings/${action}`, body), snippet: wsSnippet(w), title: "Code repeated across members" });
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
  // the copies worth acting on are the smells above; every name shared,
  // same names only included, is the reference below, folded when the
  // smells list the copies
  const byName = [h("p", { class: "hint" }, "The same code in several projects, whether or not a library is involved: types declared in two or more projects, compared by their code, and files with the same content. " +
      "How alike: the share of code lines the copies have in common (comments, imports and namespaces ignored)."),
    h("div", { class: "deptools" }, chips, filter),
    list,
    (d.notes || []).filter((x) => !x.startsWith("similarity compares")).map((x) => h("p", { class: "hint" }, x))];
  return h("div", {}, copies,
    (d.findings || []).length ? h("details", { class: "card pad more" }, h("summary", {}, `All code by name: ${plural(groups.length, "name")} in several projects`), byName)
      : section("All code by name", null, byName));
}

// whereList shows copies one per line, the project first; more than
// three fold behind "+N more".
function whereList(copies, snip) {
  // the member by its display name, once: the evidence line carries it
  const row = (c) => h("div", { class: "where" },
    snip ? h("ul", { class: "evlist inline" }, evidenceItem(`${c.project}: ${c.path}${c.line ? ":" + c.line : ""}`, snip, 0))
      : [h("b", {}, folderLabel(c.project)), " ", h("span", { class: "mono small muted" }, `${c.path}${c.line ? ":" + c.line : ""}`)],
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
    h("div", { class: "libs" + (libs.length <= 4 ? " few" : "") }, pick, detail));
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
  const rows = [...consumers].sort((a, b) => (b.packages || []).length - (a.packages || []).length || b.import_files - a.import_files);
  return h("div", {},
    h("h3", { class: "libtitle" }, name),
    h("p", { class: "libsum" }, `Offers ${plural(r.packages.length, "package")}; ${consumers.length} of ${plural(others, "other project")} use it` +
      (usedPkgs.length < r.packages.length ? `, ${plural(r.packages.length - usedPkgs.length, "package")} nobody here uses.` : ".")),
    // one matrix: a row per package, a column per project using any;
    // under it, how much each project imports and its own copies
    section("Who uses what", "A mark where a project references the package in its manifest; below, how many of its code files import the library and its own copies of the library's types.",
      h("div", { class: "card" }, h("table", { class: "libmx" },
        h("thead", {}, h("tr", {}, h("th", {}, "package"), rows.map((c) => h("th", { class: "mxcol" }, folderLabel(c.project))), h("th", { class: "n" }, "used by"))),
        h("tbody", {}, pkgRows.map((p) => h("tr", { class: pkgUsers[p].size ? "" : "unused" },
          h("td", { class: "mono small" }, pkgName(p, r.packages)),
          rows.map((c) => h("td", { class: "mxcell" }, pkgUsers[p].has(c.project) ? h("span", { class: "mxyes", title: `${c.project} uses ${p}` }, "✓") : [h("span", { class: "mxno", "aria-hidden": "true" }, "·"), h("span", { class: "sr" }, "not used")])),
          h("td", { class: "n small" }, pkgUsers[p].size ? `${pkgUsers[p].size} of ${others}` : h("span", { class: "muted" }, "nobody"))))),
        h("tfoot", {},
          h("tr", {}, h("th", { class: "small" }, "files importing"), rows.map((c) => h("td", { class: "mxcell" }, h("b", {}, c.import_files))), h("td", {})),
          h("tr", {}, h("th", { class: "small" }, "own copies of its types"), rows.map((c) => h("td", { class: "mxcell small" },
            twins[c.project] ? h("a", { href: workspaceHref(w.name, "issues", "shared"), title: twins[c.project].join(", ") }, `▲ ${twins[c.project].length}`) : h("span", { class: "muted" }, "none"))), h("td", {})))))),
    (r.non_consumers || []).length > 0 && h("p", { class: "hint" }, `Not using it: ${r.non_consumers.join(", ")}.`));
}

// folderLabel is a project's display name from its folder name.
function folderLabel(folder) {
  const p = state.projects.find((x) => baseName(x.root) === folder || x.name === folder);
  return p ? pname(p) : folder;
}
// pkgName shows a package without the prefix all of the library's share,
// dimmed ("Acme.Core.Grpc" among Acme.Core.*: "Acme.Core" dim,
// ".Grpc" bright).
function pkgName(p, all) {
  const root = all.reduce((a, b) => (b.length < a.length ? b : a), all[0] || "");
  return p !== root && p.startsWith(root + ".") ? [h("span", { class: "muted" }, root), p.slice(root.length)] : p;
}

// listCut shows the first n of a list and the rest behind "+N more".
function listCut(xs, n) {
  if (xs.length <= n) return xs.join(", ");
  return h("details", { class: "cut" }, h("summary", {}, `${xs.slice(0, n).join(", ")} +${xs.length - n} more`), xs.slice(n).join(", "));
}

// membersSummary is the workspace Overview's line about its projects:
// how they are and what waits; the table is in Members.
function membersSummary(w, members) {
  // a card per member (the ones waiting for you first, then by name): its
  // state, what waits, how well get_context serves it, when agents last
  // used it
  const sorted = [...members].sort((a, b) => waitingIn(b) - waitingIn(a) || pname(a).localeCompare(pname(b)));
  const CAP = 12;
  return h("div", {}, h("div", { class: "mtiles" }, sorted.slice(0, CAP).map(memberTile)),
    sorted.length > CAP && h("a", { href: workspaceHref(w.name, "members"), class: "small" }, `All ${sorted.length} projects →`));
}
function memberTile(p) {
  const l = p.live, n = waitingIn(p);
  const parts = [l.pending_proposals && `${l.pending_proposals} knowledge`, workProposals(l) && `${workProposals(l)} tasks`, l.checks_failed && `${l.checks_failed} failing`].filter(Boolean);
  return h("a", { class: `mtile k-${stateKind(l)}`, href: projectHref(p.id) },
    h("div", { class: "mt-top" }, projectMark(p), h("b", { class: "mt-name" }, pname(p)), stateGlyph(l),
      (state.stacks || {})[p.id] && h("span", { class: "chipmark" }, langMark(state.stacks[p.id]))),
    h("div", { class: "mt-wait" }, n ? [h("span", { class: "badge" }, n), h("span", { class: "muted small" }, ` ${parts.join(" · ") || "waiting"}`)] : h("span", { class: "okay small" }, "✓ nothing waiting")),
    l.context_answers_30d > 0 ? h("div", { class: "mt-meter", title: "get_context hits per answer, 30 days" }, miniRing(l.context_hit_rate_30d, "r-context"),
      h("span", {}, h("b", {}, pct(l.context_hit_rate_30d)), h("span", { class: "muted small" }, " context hits")))
      : h("div", { class: "muted small" }, "no scored answers yet"),
    p.last_mcp && h("div", { class: "muted small" }, `agents ${ago(p.last_mcp)}`));
}

// --- home: the machine ---
async function renderHome(main) {
  const ps = state.projects;
  const wss = state.workspaces || [];
  const inWs = new Set(wss.flatMap((w) => w.members.map((m) => m.project_id)));
  // the tiles and the workspaces' cards draw from the project list, again
  // when a project's summary changes
  const tiles = mount({ anyProject: true, kinds: ["summary", "proposals", "sync", "workspaces"], draw: () => {
    const ps = state.projects, wss = state.workspaces || [];
    const inWs = new Set(wss.flatMap((w) => w.members.map((m) => m.project_id)));
    const know = ps.reduce((n, p) => n + (p.live.pending_proposals || 0), 0);
    const work = ps.reduce((n, p) => n + workProposals(p.live), 0);
    const failing = ps.filter((p) => p.live.checks_failed > 0 || p.live.state !== "ok").length;
    const waiting = ps.filter((p) => waitingIn(p) > 0);
    const count = {};
    for (const p of ps) count[stateKind(p.live)] = (count[stateKind(p.live)] || 0) + 1;
    const states = ["bad", "warn", "stale"].filter((k) => count[k]).map((k) => `${count[k]} ${STATE_WORD[k]}`);
    return h("div", { class: "tiles section" },
      tile("Projects", ps.length, states.length ? states.join(" · ") : "all healthy"),
      tile("Waiting for you", waiting.reduce((n, p) => n + waitingIn(p), 0), waiting.length ? `in ${plural(waiting.length, "project")}: the badges in the sidebar` : "nothing waiting"),
      tile("Proposals to review", know + work, know + work ? `${know} knowledge · ${work} tasks` : "nothing waiting"),
      tile("Failing checks", failing, failing ? `${plural(failing, "project")} with a failing check or unreadable state` : "every project passes 'harness doctor'"));
  } });
  const wsGrid = mount({ anyProject: true, kinds: ["summary", "proposals", "sync", "workspaces"],
    draw: () => h("div", { class: "dash" }, (state.workspaces || []).map(workspaceSummary)) });
  const grid = h("div", { class: "dash" });
  fill(main, 
    h("div", { class: "phead" }, h("div", { class: "title" }, h("h2", {}, "Home")),
      h("p", { class: "path" }, "Every repository on this machine that uses harness.")),
    sinceVisit(""),
    tiles,
    h("div", { class: "withside" },
      h("div", { class: "mainside" },
        h("section", { class: "section" },
          h("div", { class: "shead" },
            h("div", {}, h("h3", {}, "Workspaces"), h("p", { class: "hint" }, "Repositories searched and reasoned about together.")),
            h("button", { class: "primary", onclick: () => workspaceWizard() }, "New workspace")),
          wss.length ? wsGrid : h("p", { class: "empty" }, "No workspaces yet.")),
        (!wss.length || ps.some((p) => !inWs.has(p.id))) && section(wss.length ? "Projects outside workspaces" : "Projects", null, grid)),
      h("aside", { class: "sidecol" }, activitySection(""))));
  const outside = ps.filter((p) => !inWs.has(p.id));
  if (!outside.length) grid.append(h("p", { class: "empty" }, ps.length ? "Every project is in a workspace." : "No projects registered yet. 'harness init' in a repository registers it."));
  // the same compact cards as a workspace's members, in a grid that holds
  // one project or many alike
  if (outside.length) grid.append(mount({ anyProject: true, kinds: ["summary", "proposals", "sync"],
    draw: () => h("div", { class: "mtiles" }, state.projects.filter((p) => !inWs.has(p.id)).map(memberTile)) }));
}

// workspaceSummary is a workspace's card on Home: how its projects are
// (a strip and counts per state), what waits, and a few names.
function workspaceSummary(w) {
  const members = w.members.map((m) => state.projects.find((x) => x.id === m.project_id)).filter(Boolean)
    .sort((a, b) => waitingIn(b) - waitingIn(a) || pname(a).localeCompare(pname(b)));
  const waiting = members.reduce((n, p) => n + waitingIn(p), 0);
  const SHOW = 6;
  return h("article", { class: "pcard wscard" },
    h("a", { class: "ptitle", href: workspaceHref(w.name), onclick: () => { state.lastWs = w.name; } },
      h("span", { class: "wsname" }, icon("workspace"), wname(w)),
      h("span", { class: "count" }, waiting ? `${waiting} waiting` : plural(members.length, "project"))),
    // a row per member: its state, name, language and what waits
    !members.length && h("div", { class: "wsempty" }, h("p", { class: "muted small" }, "No projects yet."),
      h("button", { class: "small", type: "button", onclick: () => addProjectsWizard(w, state.projects) }, icon("plus"), "Add projects")),
    h("div", { class: "wsrows" }, members.slice(0, SHOW).map((p) => h("a", { class: "wsrow", href: projectHref(p.id), onclick: () => { state.lastWs = w.name; } },
      projectMark(p), h("span", { class: "nname" }, pname(p)), stateGlyph(p.live),
      (state.stacks || {})[p.id] && h("span", { class: "chipmark" }, langMark(state.stacks[p.id])),
      h("span", { class: "nslot" }, waitingIn(p) > 0 && h("span", { class: "badge" }, waitingIn(p)))))),
    members.length > SHOW && h("a", { class: "small", href: workspaceHref(w.name, "members") }, `+${members.length - SHOW} more →`),
    members.length > 0 && wsFooter(members));
}

// wsFooter sums a workspace's members up in a line: their context hits
// (each answer's share, the members' mean), when an agent last worked in
// one, and what is not fine.
function wsFooter(members) {
  const scored = members.filter((p) => p.live.context_answers_30d > 0);
  const rate = scored.length ? scored.reduce((n, p) => n + p.live.context_hit_rate_30d, 0) / scored.length : null;
  const last = members.map((p) => p.last_mcp).filter(Boolean).sort().pop();
  const count = {};
  for (const p of members) count[stateKind(p.live)] = (count[stateKind(p.live)] || 0) + 1;
  const notFine = ["bad", "warn", "stale"].filter((k) => count[k]);
  return h("div", { class: "wsfoot" },
    rate != null && h("span", { class: "wsstat", title: "get_context hits per answer, the members' mean over 30 days" }, miniRing(rate, "r-context"), `${pct(rate)} context hits`),
    last && h("span", { class: "wsstat" }, icon("calm"), `agents ${ago(last)}`),
    notFine.map((k) => h("span", { class: `wsstat glyph g-${k}` }, stateIcon(k), `${count[k]} ${STATE_WORD[k]}`)));
}

// --- since your last visit (TASK-0057) ---
// sinceVisit says what happened since this browser last opened the page
// (Home or a project): agents' calls, tasks done, knowledge changes. The
// time of the last visit is kept per browser; a live redraw keeps the one
// this visit started from.
const visitFrom = {};
function sinceVisit(projectID) {
  const key = "seen." + (projectID || "home");
  if (!(key in visitFrom)) { visitFrom[key] = store.get(key, ""); store.set(key, new Date().toISOString()); }
  const prev = visitFrom[key];
  const box = h("div", {});
  if (!prev || Date.now() - new Date(prev).getTime() < 60 * 60 * 1000) return box; // first visit, or back within the hour
  const qs = new URLSearchParams({ days: "7", limit: "300" });
  if (projectID) qs.set("project", projectID);
  api(`/api/activity?${qs}`).then((r) => {
    const from = new Date(prev).getTime();
    const xs = (r.items || []).filter((x) => new Date(x.at).getTime() > from);
    const calls = xs.filter((x) => x.kind === "session").reduce((n, x) => n + (x.calls || 0), 0);
    const done = xs.filter((x) => x.kind === "task" && / done: /.test(x.title)).length;
    const other = xs.filter((x) => x.kind === "task").length - done;
    const know = xs.filter((x) => x.kind === "memory").length;
    const parts = [calls && plural(calls, "agent call"), done && `${plural(done, "task")} done`, other && plural(other, "other task change"), know && plural(know, "knowledge change")].filter(Boolean);
    if (!parts.length) return;
    fill(box, h("div", { class: "since" }, h("span", { class: "since-ico", "aria-hidden": "true" }, "✦"),
      h("span", {}, h("b", {}, `Since your last visit ${ago(prev)}: `), parts.join(" · ")),
      h("a", { href: "#activity", class: "small", onclick: (ev) => { ev.preventDefault(); document.querySelector(".sidecol")?.scrollIntoView({ block: "start", behavior: "smooth" }); } }, "see the activity")));
  }).catch(() => {});
  return box;
}

// --- activity (7 days) ---
const ACTIVITY_ICON = { session: "◆", task: "✓", memory: "✎", sync: "↻" };
const CLIENT_SHORT = { "claude-code": "Claude Code", codex: "Codex" };
// activitySection loads what happened (across the machine, or in one
// project): a strip of the 7 days (agents' calls per day, failed ones
// apart, with the task and knowledge changes), then the newest first, one
// line each.
function activitySection(projectID) {
  const qs = new URLSearchParams({ days: "7", limit: "300" });
  if (projectID) qs.set("project", projectID);
  const list = mount({ project: projectID || null, anyProject: !projectID, kinds: ["activity", "tasks", "knowledge", "sync"], load: async () => {
    const items = (await api(`/api/activity?${qs}`)).items || [];
    if (!items.length) return emptyState("calm", "A quiet week", "No agent session, task or knowledge change in the last 7 days.");
    const SHOW = projectID ? 8 : 10;
    const rows = h("ol", { class: "activity" }, items.slice(0, SHOW).map((x) => activityRow(x, !projectID)));
    const more = items.length > SHOW && h("button", { class: "ghost small actmore", onclick: (ev) => {
      fill(rows, items.map((x) => activityRow(x, !projectID))); ev.currentTarget.remove();
    } }, `Show all ${items.length}`);
    return h("div", {}, activityStrip(items), rows, more);
  } });
  return section(projectID ? "Recent activity" : "Activity", null, list);
}

// activityStrip: the last 7 days, a bar each of the agents' calls (failed
// ones on top, red) and the day's task and knowledge changes under it.
function activityStrip(items) {
  const today = new Date(); today.setHours(0, 0, 0, 0);
  const days = [...Array(7)].map((_, i) => { const d = new Date(today); d.setDate(d.getDate() - 6 + i); return { d, calls: 0, failed: 0, sessions: 0, changes: 0 }; });
  for (const x of items) {
    const t = new Date(x.at); t.setHours(0, 0, 0, 0);
    const day = days.find((d) => d.d.getTime() === t.getTime());
    if (!day) continue;
    if (x.kind === "session") { day.calls += x.calls || 0; day.failed += x.failed || 0; day.sessions++; } else if (x.kind !== "sync") day.changes++;
  }
  const most = Math.max(1, ...days.map((d) => d.calls));
  const total = days.reduce((n, d) => n + d.calls, 0), sessions = days.reduce((n, d) => n + d.sessions, 0), changes = days.reduce((n, d) => n + d.changes, 0);
  const WD = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
  return h("div", { class: "astrip" },
    h("div", { class: "asum" }, h("b", {}, plural(total, "call")), h("span", { class: "muted" }, ` in ${plural(sessions, "session")}`, changes ? ` · ${plural(changes, "change")} to tasks and knowledge` : "")),
    h("div", { class: "alegend", "aria-hidden": "true" },
      h("span", {}, h("i", { class: "lk calls" }), "agents' calls"), h("span", {}, h("i", { class: "lk failed" }), "failed"),
      h("span", {}, h("i", { class: "lk chg" }), "a task or knowledge change")),
    h("div", { class: "abars", role: "img", "aria-label": days.map((d) => `${WD[d.d.getDay()]}: ${d.calls} calls`).join(", ") }, days.map((d, i) => {
      const bar = h("div", { class: "abar" + (d.calls ? "" : " none") });
      bar.style.setProperty("--h", `${Math.max(d.calls ? 6 : 2, Math.round((d.calls / most) * 100))}%`);
      bar.style.setProperty("--f", `${d.calls ? Math.round((d.failed / d.calls) * 100) : 0}%`);
      return h("div", { class: "aday" + (i === 6 ? " today" : ""), title: `${d.d.toDateString()}: ${plural(d.calls, "call")} (${d.failed} failed) in ${plural(d.sessions, "session")}; ${plural(d.changes, "change")}` },
        h("span", { class: "anum" }, d.calls || ""), h("div", { class: "acol" }, bar),
        h("span", { class: "achg", "aria-hidden": "true" }, d.changes ? "●".repeat(Math.min(d.changes, 4)) : ""),
        h("span", { class: "awd" }, i === 6 ? "today" : WD[d.d.getDay()]));
    })));
}

function activityRow(x, showProject) {
  const href = x.kind === "task" ? projectHref(x.project_id, "work", x.ref) : x.kind === "memory" ? projectHref(x.project_id, "knowledge")
    : x.kind === "sync" ? projectHref(x.project_id, "health") : projectHref(x.project_id, "agents", "tools");
  // line one: what and when; line two: where, who and the detail
  const when = h("span", { class: "act-when", title: x.at }, ago(x.at));
  const where = showProject && x.project && h("a", { class: "act-proj", href: projectHref(x.project_id) }, markFor(x.project_id), x.project);
  if (x.kind === "session") {
    return h("li", { class: "act session" + (x.level ? ` lv-${x.level}` : "") },
      h("span", { class: "act-ico k-session", "aria-hidden": "true" }, (CLIENT_SHORT[x.actor] || x.actor || "?").slice(0, 1)),
      h("div", { class: "act-body" },
        h("div", { class: "act-line" },
          h("a", { href }, h("b", {}, plural(x.calls || 0, "call")), h("span", { class: "muted" }, ` ${x.span || ""}`)),
          x.failed > 0 && h("span", { class: "act-fail" }, `${x.failed} failed`), when),
        h("div", { class: "act-tools", title: x.detail || "" }, where, where && " · ", CLIENT_SHORT[x.actor] || x.actor || "an agent", x.detail && ` · ${x.detail}`)));
  }
  return h("li", { class: "act" + (x.level ? ` lv-${x.level}` : "") },
    h("span", { class: `act-ico k-${x.kind}`, "aria-hidden": "true" }, ACTIVITY_ICON[x.kind] || "•"),
    h("div", { class: "act-body" },
      h("div", { class: "act-line" }, h("a", { href }, x.title), x.level && levelPill(x.level), when),
      (x.detail || where) && h("div", { class: "act-detail" }, where, where && x.detail && " · ", x.detail, x.actor && h("span", { class: "muted" }, ` · ${x.actor}`))));
}

// rings draws up to three rates as concentric rings, the outer first,
// with a legend beside: how far each has come. They fill when first seen
// (key), not again on a live redraw.
const ringsSeen = new Set();
function rings(key, xs) {
  const R = [52, 38, 24], W = 11;
  const fresh = !ringsSeen.has(key);
  ringsSeen.add(key);
  const svg = sv("svg", { class: "rings" + (fresh ? " fresh" : ""), viewBox: "0 0 128 128", role: "img", "aria-label": xs.map((x) => `${x.label}: ${x.text}`).join("; ") },
    xs.map((x, i) => {
      const c = 2 * Math.PI * R[i], v = x.value == null ? 0 : Math.max(0, Math.min(1, x.value));
      const arc = sv("circle", { class: `rarc ${x.cls}`, cx: 64, cy: 64, r: R[i], "stroke-width": W, "stroke-dasharray": `${c}`, "stroke-dashoffset": `${c * (1 - v)}`, transform: "rotate(-90 64 64)" });
      arc.style.setProperty("--c", `${c}`);
      // a ring and its legend row light up together
      const g = sv("g", { class: `rgroup ${x.cls}`, onmouseenter: () => card.dataset.hl = x.cls, onmouseleave: () => delete card.dataset.hl },
        sv("title", {}, `${x.label}: ${x.text}`),
        sv("circle", { class: `rtrack ${x.cls}`, cx: 64, cy: 64, r: R[i], "stroke-width": W }), v > 0 && arc);
      return g;
    }));
  const card = h("div", { class: "card ringcard" });
  fill(card, svg,
    h("ul", { class: "ringlegend" }, xs.map((x) => h("li", { "data-r": x.cls, onmouseenter: () => card.dataset.hl = x.cls, onmouseleave: () => delete card.dataset.hl },
      h("a", { href: x.href },
        h("span", { class: `ringdot ${x.cls}`, "aria-hidden": "true" }),
        h("span", { class: "rlabel" }, x.label),
        h("b", { class: "rval" }, x.value == null ? "—" : pct(x.value)),
        h("span", { class: "muted small rtext" }, x.text, x.delta && h("span", { class: `rdelta ${x.delta.cls}` }, ` · ${x.delta.text}`)),
        x.spark && h("span", { class: "rspark" }, sparkline(x.spark, x.cls)))))));
  return card;
}

// nowCard is what is in flight beside the rings: the tasks claimed or in
// progress, then the next ready ones, a few each.
function nowCard(p, all) {
  const flight = all.filter((x) => ["claimed", "in_progress", "review"].includes(x.status));
  const ready = all.filter((x) => x.status === "ready");
  // the state only where it is not "ready" (the heading says so)
  const row = (x) => h("a", { class: "nowrow", href: projectHref(p.id, "work", x.id) }, x.status !== "ready" && statusPill(x.status),
    h("span", { class: "mono small muted" }, x.id), h("span", { class: "nowtitle" }, x.title));
  const shown = [...flight.slice(0, 3), ...ready.slice(0, Math.max(0, 4 - Math.min(flight.length, 3)))];
  return h("div", { class: "card pad nowcard" },
    h("div", { class: "label" }, flight.length ? "In flight" : "Next up",
      h("span", { class: "muted small" }, ` · ${flight.length} in flight · ${ready.length} ready`)),
    shown.length ? h("div", { class: "nowlist" }, shown.map(row))
      : [h("p", { class: all.length && all.every((x) => x.status === "done") ? "okay small" : "muted small" },
        !all.length ? "No tasks yet." : all.every((x) => x.status === "done") ? "✓ Every task is done; the last ones:"
          : "Nothing in flight or ready: the open tasks wait on others or are deferred. Done last:"),
        // nothing to pick up: what was finished last, newest first
        h("div", { class: "nowlist" }, all.filter((x) => x.status === "done")
          .sort((a, b) => String((b.completion || {}).completed_at || "").localeCompare(String((a.completion || {}).completed_at || ""))).slice(0, 3).map(row))],
    (flight.length + ready.length) > shown.length && h("a", { class: "small", href: projectHref(p.id, "work", "tasks") }, "All tasks →"));
}

// sparkline is a small picture of the window's days: bars (counts) or a
// line (rates; a day without data is a gap).
function sparkline(d, cls) {
  const W = 72, H = 18;
  if (d.bars) {
    const top = Math.max(1, ...d.bars), bw = W / d.bars.length;
    return sv("svg", { class: `spark ${cls}`, viewBox: `0 0 ${W} ${H}`, "aria-hidden": "true" },
      d.bars.map((v, i) => sv("rect", { x: i * bw + 0.5, y: H - (v ? Math.max(2, (v / top) * H) : 1), width: Math.max(bw - 1, 1), height: v ? Math.max(2, (v / top) * H) : 1, class: v ? "sbar" : "sbar zero" })));
  }
  const n = d.line.length, step = W / Math.max(1, n - 1);
  let path = "", pen = false;
  d.line.forEach((v, i) => {
    if (v == null) { pen = false; return; }
    path += `${pen ? "L" : "M"}${(i * step).toFixed(1)},${(H - 1 - v * (H - 2)).toFixed(1)}`;
    pen = true;
  });
  const last = d.line.map((v, i) => [v, i]).filter(([v]) => v != null).pop();
  return sv("svg", { class: `spark ${cls}`, viewBox: `0 0 ${W} ${H}`, "aria-hidden": "true" },
    sv("path", { d: path, class: "sline" }), last && sv("circle", { cx: last[1] * step, cy: H - 1 - last[0] * (H - 2), r: 2, class: "sdot" }));
}
// weekRate: the context hit rate of the last 7 days against the 7 before
// (each answer weighs the same), in points.
function weekRate(days) {
  const mean = (xs) => { const n = xs.reduce((a, d) => a + d.answers, 0); return n ? xs.reduce((a, d) => a + d.hit_rate * d.answers, 0) / n : null; };
  const a = mean(days.slice(-7)), b = mean(days.slice(-14, -7));
  if (a == null || b == null) return null;
  const pts = Math.round((a - b) * 100);
  return pts ? { text: `${pts > 0 ? "▲" : "▼"} ${Math.abs(pts)} pts vs the week before`, cls: pts > 0 ? "up" : "down" } : { text: "steady this week", cls: "" };
}
// weekDone: tasks completed in the last 7 days.
function weekDone(days) {
  const n = days.slice(-7).reduce((a, d) => a + d.done, 0);
  return n ? { text: `+${n} this week`, cls: "up" } : null;
}

// miniRing is one rate as a small ring, as the Overview's.
function miniRing(v, cls) {
  const r = 9, c = 2 * Math.PI * r, f = Math.max(0, Math.min(1, v || 0));
  return sv("svg", { class: "minring", viewBox: "0 0 24 24", "aria-hidden": "true" },
    sv("circle", { class: `rtrack ${cls}`, cx: 12, cy: 12, r, "stroke-width": 4 }),
    f > 0 && sv("circle", { class: `rarc ${cls}`, cx: 12, cy: 12, r, "stroke-width": 4, "stroke-dasharray": `${c}`, "stroke-dashoffset": `${c * (1 - f)}`, transform: "rotate(-90 12 12)" }));
}

// --- project overview ---
async function viewOverview(p) {
  const l = p.live;
  const [ts, rep, tr] = await Promise.allSettled([api(projectAPI(p, "tasks")), api(projectAPI(p, `report?days=${state.days}`)), api(projectAPI(p, `trends?days=${state.days}`))]);
  const all = ts.status === "fulfilled" ? ts.value.tasks || [] : [];
  const days = tr.status === "fulfilled" ? tr.value.days || [] : [];
  const fs = rep.status === "fulfilled" ? rep.value.findings || [] : [];
  const st = rep.status === "fulfilled" ? rep.value.context || {} : {};
  const done = all.filter((x) => x.status === "done").length;
  const items = [];
  if (l.pending_proposals > 0) items.push(["warn", `${plural(l.pending_proposals, "knowledge proposal")} ${l.pending_proposals === 1 ? "waits" : "wait"} for review`, projectHref(p.id, "knowledge", "inbox")]);
  if (workProposals(l) > 0) items.push(["warn", `${plural(workProposals(l), "task proposal")} ${workProposals(l) === 1 ? "waits" : "wait"} for review`, projectHref(p.id, "work", "inbox")]);
  if (l.checks_failed > 0) items.push(["problem", `${plural(l.checks_failed, "doctor check")} failing`, projectHref(p.id, "health")]);
  // an index going stale is the header's badge (its sentence on hover), not a row here
  for (const x of all.filter((x) => x.status === "blocked")) items.push(["warn", `${x.id} is blocked${x.blocked_reason ? ": " + x.blocked_reason : ""}`, projectHref(p.id, "work", x.id)]);
  // the proposals' finding is the Inbox row above
  const act = userFindings(fs).filter((f) => (f.level === "problem" || f.level === "warn") && f.status !== "resolved" && f.problem !== "proposals");
  for (const g of providerGroups(act)) items.push([g.level, providerLine(g), projectHref(p.id, "agents")]);
  for (const f of act.filter((f) => !f.provider).slice(0, 5)) items.push([f.level, f.message, projectHref(p.id, "agents")]);
  const rank = { problem: 0, warn: 1, info: 2 };
  items.sort((a, b) => rank[a[0]] - rank[b[0]]);
  const ar = st.after_read || {};
  return h("div", {}, sinceVisit(p.id),
    h("div", { class: "ovtop" },
      rings(`p:${p.id}`, [
        { label: "Tasks done", value: all.length ? done / all.length : null, text: all.length ? `${done} of ${all.length}` : "no tasks", cls: "r-tasks", href: projectHref(p.id, "work"),
          spark: days.some((d) => d.done) ? { bars: days.map((d) => d.done) } : null, delta: weekDone(days) },
        { label: "Context hits", value: st.changed ? st.hit_rate : null, text: st.changed ? `${pct(st.hit_rate)} per answer` : "not scored yet", cls: "r-context", href: projectHref(p.id, "agents"),
          spark: days.filter((d) => d.answers).length >= 2 ? { line: days.map((d) => (d.answers ? d.hit_rate : null)) } : null, delta: weekRate(days) },
        { label: "Read next", value: ar.touched ? ar.hit_rate : null, text: ar.touched ? `${ar.hits} of ${ar.touched} files` : "needs the client hooks", cls: "r-read", href: projectHref(p.id, "agents") },
      ]),
      nowCard(p, all),
      h("a", { class: "tile link", href: l.pending_proposals || !workProposals(l) ? projectHref(p.id, "knowledge", "inbox") : projectHref(p.id, "work", "inbox") }, h("div", { class: "label" }, "Proposals to review"),
        h("div", { class: "value" }, (l.pending_proposals || 0) + workProposals(l)),
        h("div", { class: "note" }, (l.pending_proposals || 0) + workProposals(l) ? `${l.pending_proposals || 0} knowledge · ${workProposals(l)} tasks` : "nothing waiting"))),
    h("div", { class: "withside" },
      h("div", { class: "mainside" },
        section("Needs attention", "What waits for a person in this project, most urgent first.",
          items.length ? h("ul", { class: "attn rows" }, items.map(([lv, msg, href]) => h("li", { class: `lv-${lv}` }, levelPill(lv), h("a", { href }, msg))))
            : emptyState("check", "All clear", "Nothing waits for you in this project.")),
        userFindings(fs).length > 0 && h("p", { class: "section" }, h("a", { href: projectHref(p.id, "agents") }, `How harness serves your agents, and what to change →`))),
      h("aside", { class: "sidecol" }, activitySection(p.id))));
}

// --- machine ---
async function renderMachine(main) {
  const m = await api("/api/machine").catch((e) => ({ error: e.message }));
  // segmented choices: every option in view, the chosen one lit
  const seg = (label, opts, cur, pick) => {
    const box = h("div", { class: "seg", role: "radiogroup", "aria-label": label });
    const draw = (c) => fill(box, opts.map(([v, text]) => h("button", { type: "button", role: "radio", "aria-checked": String(v === c), onclick: () => { pick(v); draw(v); } }, text)));
    draw(cur);
    return box;
  };
  const b = m.build || {}, paths = m.paths || {};
  const tel = h("div", {});
  const drawTel = (t, editing, err) => fill(tel, telemetryCard(t || {}, editing, err, drawTel));
  drawTel(m.telemetry);
  fill(main,
    h("div", { class: "phead" }, h("div", { class: "title" }, h("h2", {}, "Machine")),
      h("p", { class: "path" }, "This harness installation, where its state lives, and this page's settings.")),
    m.error && h("p", { class: "error" }, m.error),
    section("Settings", "Kept in this browser.", h("div", { class: "card pad settings" },
      h("div", { class: "setrow" }, h("div", {}, h("b", {}, "Theme"), h("p", { class: "muted small" }, "Follow the system, or always light or dark.")),
        seg("Theme", THEMES.map((t) => [t, t === "system" ? "System" : t[0].toUpperCase() + t.slice(1)]), store.get("theme", "system"), (v) => { store.set("theme", v); applyTheme(); })),
      h("div", { class: "setrow" }, h("div", {}, h("b", {}, "Open projects on"), h("p", { class: "muted small" }, "The view a project opens on from the sidebar.")),
        seg("Default project view", PROJECT_VIEWS, defaultView(), (v) => store.set("projectView", v))))),
    section("Developer telemetry", "Anonymous, aggregated numbers for the harness developer: how well harness serves agents. Never code, paths, queries or names.", tel),
    section("C# sessions (graphify-csharp)", "Warm sessions keep a C# solution compiled for callers, references and impact; loading a large one takes minutes. Sessions harness starts stay warm for the next agent session and stop after 2 hours without queries; ones started by hand are left alone.",
      graphifySessions(null)),
    section("State", "Machine-wide files; each project keeps its own state in its .harness folder.", h("div", { class: "card pad" }, kv([
      // the folder once, the files in it by name
      ["Global folder", paths.global && h("code", {}, paths.global)],
      ...[["Project registry", paths.registry], ["Workspaces", paths.workspaces], ["Workspace and user knowledge", paths.knowledge]].map(([k, v]) =>
        [k, v && h("code", { title: v }, paths.global && v.startsWith(paths.global + "/") ? v.slice(paths.global.length + 1) : v)]),
    ]))),
    h("p", { class: "muted small buildline" }, `harness ${b.version || "?"}`, b.commit && ` · ${b.commit.slice(0, 12)}`, b.date && ` · built ${b.date.slice(0, 10)}`, b.platform && ` · ${b.platform}`, b.runtime && ` · ${b.runtime}`));
}

// telemetryCard is the opt-in as a switch: on asks for the collector's
// endpoint (and token, if it wants one), off stops sending (TASK-0057).
function telemetryCard(t, editing, err, redraw) {
  const send = async (body) => {
    try { redraw((await post("/api/telemetry", body)).telemetry); } catch (e) { redraw(t, editing, e.message); }
  };
  const sw = h("button", {
    type: "button", class: "switch", role: "switch", "aria-checked": String(!!t.enabled), "aria-label": "Share telemetry",
    onclick: () => t.enabled ? send({ enabled: false }) : redraw(t, true),
  }, h("span", { class: "knob" }));
  const endpoint = h("input", { type: "text", value: t.endpoint || "", placeholder: "http://collector.example:8766/v1/report", "aria-label": "Collector endpoint" });
  const token = h("input", { type: "password", placeholder: t.endpoint ? "(unchanged)" : "if the collector asks for one", "aria-label": "Collector token" });
  return h("div", { class: "card pad telcard" + (t.enabled ? " on" : "") },
    h("div", { class: "setrow" },
      h("div", {}, h("b", {}, t.enabled ? "Sharing" : "Not sharing"),
        h("p", { class: "muted small" }, t.enabled ? `To ${t.endpoint}; ${t.last_sent ? `last sent ${ago(t.last_sent)}` : "nothing sent yet"}, at most daily after a sync.`
          : "Turn on to send a daily report to a harness-collect server.")),
      h("div", { class: "telacts" }, t.enabled && !editing && h("button", { class: "ghost small", type: "button", onclick: () => redraw(t, true) }, "Change…"), sw)),
    editing && h("form", { class: "telform", onsubmit: (ev) => { ev.preventDefault(); send({ enabled: true, endpoint: endpoint.value.trim(), token: token.value }); } },
      h("label", {}, h("span", {}, "Collector endpoint"), endpoint),
      h("label", {}, h("span", {}, "Token"), token),
      err && h("p", { class: "error" }, err),
      h("div", { class: "review" }, h("button", { class: "primary", type: "submit" }, t.enabled ? "Save" : "Turn on"), h("button", { class: "ghost", type: "button", onclick: () => redraw(t) }, "Cancel")),
      h("p", { class: "muted small" }, "'harness telemetry preview' prints exactly what is sent.")));
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
      // a document's first section is its introduction: the heading opens it
      h("nav", { class: "doclist", "aria-label": "Sections" }, groups.map((g) => {
        const intro = g.items[0] && g.items[0].title === title(g.doc) ? g.items[0] : null;
        return h("div", {},
          h("h4", {}, intro ? h("a", { href: "#/docs/" + enc(intro.slug), "aria-current": intro.slug === sec.slug ? "page" : null }, title(g.doc)) : title(g.doc)),
          h("ul", {}, g.items.filter((s) => s !== intro).map((s) => h("li", {}, h("a", { href: "#/docs/" + enc(s.slug), "aria-current": s.slug === sec.slug ? "page" : null }, s.title)))));
      })),
      h("article", { class: "doctext" }, sec.error ? h("p", { class: "error" }, sec.error) : markdown(sec.body || ""), docsNext(list.sections, sec.slug))));
}

// docsNext links the section before and after, to read on.
function docsNext(all, slug) {
  const i = all.findIndex((s) => s.slug === slug);
  if (i < 0) return null;
  const prev = all[i - 1], next = all[i + 1];
  return h("nav", { class: "docnext", "aria-label": "Read on" },
    prev ? h("a", { href: "#/docs/" + enc(prev.slug) }, h("span", { class: "muted small" }, "Previous"), h("b", {}, "← " + prev.title)) : h("span", {}),
    next && h("a", { class: "next", href: "#/docs/" + enc(next.slug) }, h("span", { class: "muted small" }, "Next"), h("b", {}, next.title + " →")));
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
      h("p", { class: "path" }, "Knowledge for every project of yours on this machine; a workspace's or a project's own statement takes precedence.")),
    mount({ kinds: ["levels"], load: () => levelKnowledge("user", null) }));
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

// SEARCH_HINT: what each kind of result is, for the empty page.
const SEARCH_HINT = { project: "by name or folder", workspace: "by name", memory: "decisions, conventions, warnings… by their words", task: "by id or title", blueptone: "by id, title or goal" };
async function renderSearch(main, r) {
  const head = h("div", { class: "phead" }, h("div", { class: "title" }, h("h2", {}, "Search")),
    h("p", { class: "path" }, "Projects, workspaces, knowledge, tasks and blueprints across the machine. Agents search the code."));
  const field = h("input", { type: "search", class: "bigsearch", value: r.q, placeholder: "Search everything…", "aria-label": "Search everything", autofocus: true });
  const form = h("form", { class: "bigform", role: "search", onsubmit: (ev) => { ev.preventDefault(); location.hash = `#/search?q=${enc(field.value.trim())}`; } },
    icon("search"), field);
  setTimeout(() => { if (document.activeElement !== field) { field.focus(); field.setSelectionRange(field.value.length, field.value.length); } }, 0);
  if (r.q.length < 2) {
    return fill(main, head, form, h("div", { class: "kindcards" }, SEARCH_KINDS.map(([k, label]) => h("div", { class: "kindcard" },
      h("b", {}, label), h("span", { class: "muted small" }, SEARCH_HINT[k])))));
  }
  const res = await api(`/api/search?q=${enc(r.q)}`);
  const hits = res.hits || [];
  const off = new Set();
  const chips = h("div", { class: "filters" });
  const out = h("div", {});
  function draw() {
    fill(chips, SEARCH_KINDS.filter(([k]) => hits.some((x) => x.kind === k)).map(([k, label]) => h("button", {
      class: "chip", "aria-pressed": String(!off.has(k)), onclick: () => { off.has(k) ? off.delete(k) : off.add(k); draw(); },
    }, label, h("span", { class: "count" }, hits.filter((x) => x.kind === k).length))));
    const groups = SEARCH_KINDS.filter(([k]) => !off.has(k)).map(([k, label]) => {
      const xs = hits.filter((x) => x.kind === k);
      return xs.length && h("section", { class: "section hits" }, h("h3", {}, label, " ", h("span", { class: "count" }, xs.length >= res.per_kind ? `${xs.length}+` : xs.length)),
        h("div", { class: "hitlist" }, xs.map((x) => hitRow(x))));
    }).filter(Boolean);
    fill(out, groups.length ? groups : emptyState("search", `Nothing matches “${r.q}”`, "Try fewer or other words: names, ids, or a phrase from a memory."));
  }
  draw();
  fill(main, head, form, chips, out);
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
      .sort((a, b) => (a.p ? pname(a.p) : a.m.project).localeCompare(b.p ? pname(b.p) : b.m.project)); // by name, as the sidebar
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
      title: `Remove ${plural(ids.length, "project")} from ${wname(w)}?`, lines: ids.map((id) => `− ${name(id)}`).concat([`Their own state and the other members stay as they are.`]),
      confirm: "Remove", danger: true, onBack: () => fill(box),
      onConfirm: async () => { for (const id of ids) await post(`/api/workspaces/${enc(w.name)}/leave`, { project_id: id }); await afterWrite(); },
    }));
  });
  const filter = h("input", { type: "search", placeholder: "Filter members", "aria-label": "Filter members", oninput: (ev) => { q = ev.target.value.trim().toLowerCase(); draw(); } });
  const dbox = h("div", {});
  draw();
  return h("div", {},
    h("div", { class: "deptools" }, filter,
      removeBtn,
      others.length > 0 && h("button", { onclick: () => addProjectsWizard(w, others) }, "Add projects")),
    box,
    common && h("p", { class: "hint" }, "Paths under ", h("code", {}, common)),
    h("div", { class: "card" }, h("table", { class: "members" },
      h("thead", {}, h("tr", {}, h("th", { class: "cbx" }, all), h("th", {}, "project"), h("th", {}, "state"), h("th", {}, "stack"), h("th", { class: "n" }, "waiting"), h("th", {}, "synced"), h("th", {}, "agent used"), h("th", {}, "context hits"), h("th", {}, "path"))),
      tbody)),
    // what cannot be taken back, apart at the bottom
    h("div", { class: "card pad danger-zone" },
      h("div", {}, h("b", {}, "Delete this workspace"), h("p", { class: "muted small" }, "Its projects keep all their state; its workspace knowledge no longer applies.")),
      h("button", { class: "ghost danger-text", onclick: () => fill(dbox, reviewPanel({
        title: `Delete workspace ${wname(w)}?`, lines: [`The workspace and its membership list go; its ${plural(w.members.length, "project")} keep all their state.`,
          "Knowledge recorded at workspace scope stays on disk but no longer applies to any project."],
        confirm: "Delete workspace", danger: true, onBack: () => fill(dbox),
        onConfirm: async () => { await post(`/api/workspaces/${enc(w.name)}/delete`, {}); location.hash = "#/"; await refresh(); },
      })) }, "Delete workspace…")),
    dbox);
}

const projectLabel = (id) => { const p = state.projects.find((x) => x.id === id); return p ? `${pname(p)}  ${p.root}` : id; };

// createForm: a name, and optionally the registered projects under a
// folder, all shown checked for review before anything is created.
// --- dialogs (TASK-0057) ---
// dialog opens a centred dialog over a dimmed page (the browser's own:
// focus stays inside, Esc closes it) with a title, a body that can change
// (wizard steps) and its buttons; it removes itself when closed.
function dialog({ title, sub, wide }) {
  const head = h("div", { class: "dlg-head" }, h("div", {}, h("h3", { class: "dlg-title" }, title), sub && h("p", { class: "muted small" }, sub)),
    iconButton("close", "Close", () => d.close()));
  const body = h("div", { class: "dlg-body" });
  const foot = h("div", { class: "dlg-foot" });
  const d = h("dialog", { class: "dlg" + (wide ? " wide" : "") }, head, body, foot);
  d.addEventListener("close", () => d.remove());
  d.addEventListener("click", (ev) => { if (ev.target === d) d.close(); }); // a click on the backdrop
  document.body.append(d);
  d.showModal();
  return {
    el: d, body, foot,
    set(title2, nodes, buttons) { if (title2) head.querySelector(".dlg-title").textContent = title2; fill(body, nodes); fill(foot, buttons); setTimeout(() => body.querySelector("input:not([type=checkbox])")?.focus(), 0); },
    close: () => d.close(),
  };
}
// field is a labelled input with its hint under it.
function field(label, input, hint) {
  // a label only around one field: a label forwards a click anywhere in it
  // to its first control, which would make a group of buttons pick its first
  const one = /^(INPUT|TEXTAREA|SELECT)$/.test(input.tagName);
  return h(one ? "label" : "div", { class: "dfield" }, h("span", { class: "dlabel" }, label), input, hint && h("span", { class: "dhint" }, hint));
}
// stepper shows a wizard's steps, the current one lit.
function stepper(steps, at) {
  return h("ol", { class: "stepper" }, steps.map((t, i) => h("li", { class: i < at ? "done" : i === at ? "on" : "" }, h("span", { class: "sn" }, i < at ? "✓" : i + 1), t)));
}
const slug = (t) => t.toLowerCase().normalize("NFKD").replace(/[^a-z0-9._-]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 64);

// renameDialog changes a project's or a workspace's names: a live preview,
// what will change, Save.
function renameDialog({ what, display, short, folder, mark, send }) {
  const dlg = dialog({ title: `Rename this ${what}`, sub: what === "workspace" ? "Only the machine's workspace list changes." : "Only the machine's project registry changes; the repository and its .harness state are untouched." });
  const dIn = h("input", { type: "text", value: display, placeholder: what === "project" ? folder : short, maxlength: "80" });
  const sIn = h("input", { type: "text", value: short, placeholder: folder || "", class: "mono" });
  const shortRule = what === "workspace" ? /^[a-z0-9][a-z0-9._-]{0,63}$/ : /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;
  const preview = h("div", { class: "dpreview" });
  const changes = h("ul", { class: "dchanges" });
  const err = h("p", { class: "error", hidden: true });
  const save = h("button", { class: "primary", type: "button" }, "Save");
  const state2 = () => ({ d: dIn.value.trim().replace(/\s+/g, " "), sh: sIn.value.trim() });
  const draw = () => {
    const { d, sh } = state2();
    const shown = d || sh || folder || short;
    fill(preview, mark && projectMark({ ...mark, title: d || undefined, name: sh || mark.name }, true), h("div", {},
      h("b", { class: "dpname" }, shown), h("div", { class: "muted small mono" }, what === "workspace" ? `workspace:${sh || "…"}` : `short name ${sh || folder}`)));
    const lines = [];
    if (d !== display) lines.push(d ? ["Display name", display || "(none)", d] : ["Display name", display, "removed"]);
    if (sh !== short) lines.push(["Short name", short || folder, sh || folder]);
    fill(changes, lines.length ? lines.map(([k, a, b]) => h("li", {}, h("span", { class: "muted" }, k), " ", h("s", {}, a), " → ", h("b", {}, b))) : h("li", { class: "muted" }, "Nothing changed yet."));
    const bad = (what === "workspace" || sh) && !shortRule.test(sh);
    err.hidden = !bad;
    err.textContent = bad ? (what === "workspace" ? "Short name: lower-case letters, digits, '.', '_' or '-'." : "Short name: letters, digits, '.', '_' or '-' (empty: the folder's name).") : "";
    save.disabled = bad || !lines.length;
  };
  dIn.addEventListener("input", draw);
  sIn.addEventListener("input", draw);
  save.addEventListener("click", async () => {
    const { d, sh } = state2(), body = {};
    if (d !== display) body.title = d;
    if (sh !== short) body.name = sh;
    save.disabled = true;
    try { await send(body); dlg.close(); await afterWrite(); } catch (e) { err.textContent = e.message; err.hidden = false; save.disabled = false; }
  });
  dlg.set(null, [preview,
    field("Display name", dIn, "Any text, shown everywhere; empty shows the short name."),
    field("Short name", sIn, what === "workspace" ? "What agents and commands use: workspace:<short name>." : "How workspace results and agents name it; empty: the folder's name."),
    h("div", { class: "dsum" }, h("div", { class: "dlabel" }, "Will change"), changes), err],
  [h("button", { class: "ghost", type: "button", onclick: () => dlg.close() }, "Cancel"), save]);
  draw();
}

// projectPicker chooses projects: a filter, a row each with its mark and
// folder, select what is shown, and (optionally) find those under a
// folder. It returns the element and the chosen ids.
function projectPicker(projects, chosen, withFolder) {
  let q = "";
  const list = h("div", { class: "pick-list" });
  const count = h("span", { class: "muted small" });
  const draw = () => {
    const shown = projects.filter((p) => !q || pname(p).toLowerCase().includes(q) || p.root.toLowerCase().includes(q));
    fill(list, shown.length ? shown.map((p) => {
      const cb = h("input", { type: "checkbox", checked: chosen.has(p.id), onchange: (ev) => { ev.target.checked ? chosen.add(p.id) : chosen.delete(p.id); drawCount(); } });
      return h("label", { class: "pick-row" + (chosen.has(p.id) ? " on" : "") }, cb, projectMark(p), h("span", { class: "pick-name" }, pname(p)), h("span", { class: "muted small mono pick-path", title: p.root }, shortPath(p.root)));
    }) : h("p", { class: "muted small" }, "No project matches."));
    drawCount();
  };
  const drawCount = () => {
    count.textContent = `${chosen.size} chosen`;
    for (const r of list.querySelectorAll(".pick-row")) r.classList.toggle("on", r.querySelector("input").checked);
  };
  const filter = h("input", { type: "search", placeholder: "Filter by name or folder", oninput: (ev) => { q = ev.target.value.trim().toLowerCase(); draw(); } });
  const all = h("button", { class: "ghost small", type: "button", onclick: () => { for (const p of projects) if (!q || pname(p).toLowerCase().includes(q) || p.root.toLowerCase().includes(q)) chosen.add(p.id); draw(); } }, "Choose all shown");
  const none = h("button", { class: "ghost small", type: "button", onclick: () => { chosen.clear(); draw(); } }, "None");
  const folderIn = h("input", { type: "text", placeholder: "/path/to/folder", class: "mono" });
  const folderMsg = h("span", { class: "muted small" });
  const find = h("button", { class: "small", type: "button", onclick: async () => {
    try {
      const found = await api(`/api/workspaces/discover?folder=${encodeURIComponent(folderIn.value.trim())}`);
      const ids = found.projects.map((x) => x.project_id).filter((id) => projects.some((p) => p.id === id));
      ids.forEach((id) => chosen.add(id));
      folderMsg.textContent = ids.length ? `✓ ${plural(ids.length, "project")} under ${found.folder} chosen` : `No registered project under ${found.folder}.`;
      draw();
    } catch (e) { folderMsg.textContent = e.message; }
  } }, "Find");
  draw();
  return {
    el: h("div", { class: "picker" },
      h("div", { class: "pick-tools" }, filter, all, none, count),
      list,
      withFolder && h("div", { class: "pick-folder" }, h("span", { class: "dlabel" }, "Or choose those under a folder"), h("div", { class: "pick-tools" }, folderIn, find), folderMsg)),
  };
}

// workspaceWizard creates a workspace in three steps: its name, its
// projects, a review.
function workspaceWizard() {
  const steps = ["Name", "Projects", "Review"];
  const dlg = dialog({ title: "New workspace", sub: "Repositories searched and reasoned about together.", wide: true });
  const st = { title: "", name: "", nameEdited: false, ids: new Set() };
  const titleIn = h("input", { type: "text", placeholder: "e.g. Company services", maxlength: "80" });
  const nameIn = h("input", { type: "text", placeholder: "e.g. company", class: "mono" });
  const err = h("p", { class: "error", hidden: true });
  const next = h("button", { class: "primary", type: "button" }, "Next");
  const back = h("button", { class: "ghost", type: "button" }, "Back");
  const cancel = h("button", { class: "ghost", type: "button", onclick: () => dlg.close() }, "Cancel");
  const okName = () => /^[a-z0-9][a-z0-9._-]{0,63}$/.test(st.name) && !(state.workspaces || []).some((w) => w.name === st.name);
  // the short name follows the display name until it is edited
  titleIn.addEventListener("input", () => { st.title = titleIn.value.trim().replace(/\s+/g, " "); if (!st.nameEdited) { st.name = slug(st.title); nameIn.value = st.name; } checkName(); });
  nameIn.addEventListener("input", () => { st.nameEdited = true; st.name = nameIn.value.trim(); checkName(); });
  const checkName = () => {
    const taken = (state.workspaces || []).some((w) => w.name === st.name);
    err.hidden = !st.name || okName();
    err.textContent = taken ? `A workspace ${st.name} exists already.` : "Lower-case letters, digits, '.', '_' or '-', starting with a letter or digit.";
    next.disabled = !okName();
  };
  const step = (i) => {
    const head = stepper(steps, i);
    if (i === 0) {
      next.textContent = "Next";
      next.onclick = () => step(1);
      dlg.set(null, [head, field("Display name", titleIn, "Any text, shown in the sidebar and on its page (optional)."),
        field("Short name", nameIn, "What agents and commands use: workspace:<short name>. Follows the display name until you edit it."), err], [cancel, next]);
      checkName();
    } else if (i === 1) {
      const pk = projectPicker(state.projects, st.ids, true);
      next.textContent = "Next";
      next.disabled = false;
      next.onclick = () => step(2);
      back.onclick = () => step(0);
      dlg.set(null, [head, h("p", { class: "muted small" }, "Choose its projects now, or none and add them later."), pk.el], [back, next]);
    } else {
      const ps = [...st.ids].map((id) => state.projects.find((p) => p.id === id)).filter(Boolean);
      next.textContent = "Create workspace";
      next.disabled = false;
      back.onclick = () => step(1);
      next.onclick = async () => {
        next.disabled = true;
        try { await post("/api/workspaces", { name: st.name, title: st.title, project_ids: [...st.ids] }); dlg.close(); toast("Workspace created"); location.hash = workspaceHref(st.name); refresh(); }
        catch (e) { err.textContent = e.message; err.hidden = false; next.disabled = false; }
      };
      err.hidden = true;
      dlg.set(null, [head, h("div", { class: "dreview" },
        h("div", { class: "dpreview" }, h("span", { class: "nicon big" }, icon("workspace")), h("div", {}, h("b", { class: "dpname" }, st.title || st.name), h("div", { class: "muted small mono" }, `workspace:${st.name}`))),
        h("div", { class: "dlabel" }, ps.length ? plural(ps.length, "project") : "No projects yet"),
        ps.length ? h("div", { class: "dchips" }, ps.map((p) => h("span", { class: "pchip" }, projectMark(p), pname(p)))) : h("p", { class: "muted small" }, "Add them later on the workspace's Members."),
        h("p", { class: "muted small" }, WS_NOTE)), err], [back, next]);
    }
  };
  step(0);
}

// addProjectsWizard adds projects to a workspace: choose, then review.
function addProjectsWizard(w, others) {
  const dlg = dialog({ title: `Add projects to ${wname(w)}`, wide: true });
  const ids = new Set();
  const err = h("p", { class: "error", hidden: true });
  const next = h("button", { class: "primary", type: "button" }, "Next");
  const back = h("button", { class: "ghost", type: "button", onclick: () => step(0) }, "Back");
  const step = (i) => {
    if (i === 0) {
      const pk = projectPicker(others, ids, true);
      next.textContent = "Next";
      next.onclick = () => { if (!ids.size) { err.textContent = "Choose at least one project."; err.hidden = false; return; } err.hidden = true; step(1); };
      dlg.set(null, [stepper(["Projects", "Review"], 0), pk.el, err], [h("button", { class: "ghost", type: "button", onclick: () => dlg.close() }, "Cancel"), next]);
    } else {
      const ps = [...ids].map((id) => state.projects.find((p) => p.id === id)).filter(Boolean);
      next.textContent = `Add ${plural(ps.length, "project")}`;
      next.onclick = async () => {
        next.disabled = true;
        try { await post(`/api/workspaces/${enc(w.name)}/join`, { project_ids: [...ids] }); dlg.close(); await afterWrite(); }
        catch (e) { err.textContent = e.message; err.hidden = false; next.disabled = false; }
      };
      dlg.set(null, [stepper(["Projects", "Review"], 1), h("div", { class: "dreview" }, h("div", { class: "dlabel" }, `Joining ${wname(w)}`),
        h("div", { class: "dchips" }, ps.map((p) => h("span", { class: "pchip" }, projectMark(p), pname(p)))), h("p", { class: "muted small" }, WS_NOTE)), err], [back, next]);
    }
  };
  step(0);
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
      fill(out, healthReport(r, p, () => load(false)));
    } catch (e) {
      fill(out, h("p", { class: "error" }, e.message));
    } finally {
      probeBtn.disabled = false;
    }
  }
  wrap.append(h("div", { class: "shead" },
    h("p", { class: "hint" }, "What 'harness doctor' reports. Checking the providers starts each one, so it runs only on request."), probeBtn), out);
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
  const name = (id) => { const p = state.projects.find((x) => x.id === id); return p ? pname(p) : id; };
  // the members by name, as the sidebar lists them
  const ids = w.members.map((m) => m.project_id).filter((id) => d.stacks[id] || d.rows.some((r) => r.versions[id])).sort((a, b) => name(a).localeCompare(name(b)));
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
        if (!vs) return h("td", { class: "muted", title: "not used" }, "—");
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
// SMELL_RISK: what a kind of smell costs, in a few words, for its row.
const SMELL_RISK = {
  layering: "a lower layer cannot change without a higher one",
  "deep-import": "a change inside a feature breaks what imports it",
  unused: "code to read and keep that nothing needs",
  duplicate: "a fix in one copy misses the others",
  template: "the same steps maintained in several places",
  "interface-candidate": "each new variant means editing every place that chooses",
  "repeated-sequence": "a change to the steps must be made in each",
  "shared-code": "a fix in one member's copy misses the others",
  boundary: "it breaks a rule recorded as knowledge",
  complexity: "hard to understand and test: bugs hide in the paths",
};
// smellSubject is a finding's subject: its title up to what is said of
// it ("Findings has cyclomatic complexity 50": "Findings").
function smellSubject(x) {
  const t = x.title || "";
  // "2 statements repeated in 4 places (Compare, sortedKeys, …)": the first name inside
  const inner = /^\d.*?\(([^,)]+)/.exec(t);
  if (inner) return inner[1];
  return t.split(/: | (?:has|is|are|repeats|follows|and \d+ more|written|takes|reach(?:es)?) /)[0].replace(/^(Functions?|Types?) /, "");
}
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
  complexity: "Cyclomatic complexity counts the independent paths through a function, each a case to understand and test. Counted as each language's usual tool counts it (gocyclo, Microsoft's code metrics, ESLint); the thresholds are [architecture.complexity] in the configuration (note 11, warning 21, problem 51 by default).",
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
  // a path breaks at its slashes, never inside a name: the folder dimmed,
  // from the file name on bright
  const show = (part) => {
    const i = part.lastIndexOf("/");
    return i < 0 ? part : [h("span", { class: "evdir" }, part.slice(0, i + 1).replace(/\//g, "/\u200b")), part.slice(i + 1)];
  };
  // "TP-WebAPI: path" names the member by its display name
  const named = (part) => {
    const m = /^([^\s:/]+): (.+)$/.exec(part);
    const label = m && folderLabel(m[1]);
    return m && label !== m[1] ? [h("b", { class: "evwho" }, label), ": ", show(m[2])] : show(part);
  };
  return e.split(" → ").map((part, i) => h("code", { class: i ? "evto" : "" }, i ? "→ " : "", named(part)));
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
  // a call: "caller file:line → server file:line (Contract)", each side
  // opening to its own code
  if (snip && e.includes(" → ")) {
    const [from, to] = e.split(" → ");
    const what = /\s\(([^()]+)\)$/.exec(to);
    const sides = [from, what ? to.slice(0, what.index) : to];
    if (sides.every(namesFile)) {
      return h("li", { class: "evpair" }, what && h("span", { class: "muted small" }, what[1]),
        codeFold(sides[0], snip, span), h("span", { class: "evarrow", "aria-hidden": "true" }, "↓ calls"), codeFold(sides[1], snip, span));
    }
  }
  if (!snip || !namesFile(e)) return h("li", {}, evidenceText(e));
  return h("li", {}, codeFold(e, snip, span));
}

// codeFold is a place that opens to its code, read when opened.
function codeFold(e, snip, span) {
  const d = h("details", { class: "evcode" }, h("summary", {}, evidenceText(e)));
  d.addEventListener("toggle", () => {
    if (!d.open || d.dataset.loaded) return;
    d.dataset.loaded = "1";
    d.append(mount({ load: async () => codeView((await api(snip(e, span))).snippet) }));
  });
  return d;
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
            h("summary", {}, levelPill(x.level), h("span", { class: "stitle" }, x.title), chips(x),
              SMELL_RISK[kind] && h("span", { class: "srisk" }, SMELL_RISK[kind])), body(x))));
        }
        const fresh = xs.filter((x) => x.status === "new" || x.status === "changed").length;
        // one "could help" for the group when it is the same for all;
        // otherwise each says its own (a shared-code group's home)
        const sameFix = xs.every((x) => x.fix === xs[0].fix);
        const item = (x) => h("li", { class: quiet(x) ? "quiet" : "" }, h("details", {}, h("summary", {}, x.title, chips(x)), body(x, sameFix)));
        return h("li", { class: "smell" }, h("details", {},
          h("summary", {}, levelPill(worstLevel(xs)), h("span", { class: "stitle" }, h("b", {}, `${xs.length} × `), SMELL_KIND[kind] || kind),
            fresh > 0 && h("span", { class: "tag fstat new" }, `${fresh} new or changed`),
            // closed, a row still says what the risk is and where it is worst
            h("span", { class: "srisk" }, SMELL_RISK[kind] && h("span", {}, SMELL_RISK[kind]),
              h("span", { class: "sworst" }, kind === "complexity" ? " · worst: " : " · e.g. ", xs.slice(0, 3).map((x) => smellSubject(x)).join(", ")))),
          h("div", { class: "sbody" }, sameFix && xs[0].fix && h("p", { class: "sfix" }, h("b", {}, "Could help: "), xs[0].fix),
            h("p", { class: "muted small swhy" }, SMELL_WHY[kind] || xs[0].why),
            levelFiltered(kind, xs, item))));
      })) : emptyState("check", marked.length ? "Nothing open" : "No smells", marked.length ? `${plural(marked.length, "finding")} marked by design.` : okay.replace(/^✓ ?/, "")),
      gone.length > 0 && h("details", { class: "evfold gonefold" }, h("summary", {}, `Gone (${gone.length})`),
        h("ul", { class: "evlist" }, gone.map((x) => h("li", {}, x.title,
          x.mark && x.mark.state === "task" ? h("span", { class: "tag fmark resolved" }, `resolved · ${x.mark.task_id}`) : null)))));
  };
  draw();
  return section((t && t.title) || "Smells", intro, box);
}

// LEVELS orders finding levels, worst first, with what a chip says.
const LEVELS = [["problem", "problems"], ["warn", "warnings"], ["info", "notes"]];
function worstLevel(xs) {
  return (LEVELS.find(([l]) => xs.some((x) => x.level === l)) || [xs[0].level])[0];
}

// levelFiltered is a group's list; a group spanning levels gets a chip
// per level with its count that filters it (the viewer's choice kept in
// the browser; only the list draws again).
function levelFiltered(kind, xs, item) {
  const present = LEVELS.filter(([l]) => xs.some((x) => x.level === l));
  if (present.length < 2) return smellList(xs, item);
  const key = "smells.levels." + kind;
  let on = new Set((store.get(key, "") || present.map(([l]) => l).join(",")).split(",").filter((l) => present.some(([p]) => p === l)));
  if (!on.size) on = new Set(present.map(([l]) => l));
  const list = h("div", {});
  const bar = h("div", { class: "lvchips", role: "group", "aria-label": "Show levels" });
  const draw = () => {
    fill(bar, present.map(([l, label]) => h("button", {
      type: "button", class: `chip lv-${l}` + (on.has(l) ? " on" : ""), "aria-pressed": on.has(l) ? "true" : "false",
      onclick: () => {
        if (on.has(l) && on.size > 1) on.delete(l); else on.add(l);
        store.set(key, [...on].join(","));
        draw();
      },
    }, h("span", { class: "ico", "aria-hidden": "true" }, { problem: "✕", warn: "▲", info: "ⓘ" }[l]), ` ${xs.filter((x) => x.level === l).length} ${label}`)));
    fill(list, smellList(xs.filter((x) => on.has(x.level)), item));
  };
  draw();
  return h("div", {}, bar, list);
}

// complexitySection (from the C# rewrite): how the project's functions
// spread over cyclomatic complexity, per language reported, loaded on its
// own: bins of five with the thresholds marked, and the bins as a table.
function complexitySection(p) {
  return section("Complexity distribution", "Every function by cyclomatic complexity, reported or not, in bins of five (bar heights on a square-root scale, each bar with its count); the lines are the thresholds of a note, a warning and a problem.",
    mount({ project: p.id, kinds: ["sync"], load: async () => {
      const d = await api(projectAPI(p, "complexity"));
      if (!(d.languages || []).length) return h("p", { class: "empty" }, "No function of a reported language (csharp and go by default; [architecture.complexity] languages).");
      return h("div", { class: "cxcharts" }, d.languages.map((l) => complexityChart(l, d.bin_width)));
    } }));
}

const CX_LANG = { csharp: "C#", go: "Go", typescript: "TypeScript and JavaScript" };
function complexityChart(l, width) {
  const ns = "http://www.w3.org/2000/svg";
  const svg = (tag, attrs, ...kids) => {
    const el = document.createElementNS(ns, tag);
    for (const [k, v] of Object.entries(attrs || {})) el.setAttribute(k, v);
    for (const c of kids.flat()) if (c != null) el.append(typeof c === "string" || typeof c === "number" ? String(c) : c);
    return el;
  };
  const bins = l.bins, n = bins.length, top = Math.max(...bins.map((b) => b.count), 1);
  const W = Math.max(320, n * 22), band = 30, plot = 120, axis = 18, H = band + plot + axis;
  const bw = W / n;
  // heights on a square-root scale: the many simple functions would
  // flatten the few complex ones; every bar says its count
  const y = (c) => band + plot - (c ? Math.max(2, Math.sqrt(c / top) * (plot - 14)) : 0);
  const th = l.thresholds;
  const marks = [["info", "note", th.note], ["warn", "warning", th.warn], ["problem", "problem", th.problem]].filter(([, , v]) => (v - 1) / width < n);
  // a label moves up a row only when it would overlap the one before
  let lastEnd = -Infinity, row = 0;
  const labels = marks.map(([cls, name, v]) => {
    const x = ((v - 1) / width) * bw, text = `${name} ${v}`, w = text.length * 6.2 + 6;
    row = x < lastEnd ? row + 1 : 0;
    lastEnd = x + w;
    return { cls, x, text, yy: band - 6 - row * 12 };
  });
  const level = (b) => b.from >= th.problem ? "problem" : b.from >= th.warn ? "warn" : b.from >= th.note ? "info" : "low";
  const chart = svg("svg", { viewBox: `0 0 ${W} ${H}`, preserveAspectRatio: "xMinYMin meet", class: "cxchart", role: "img",
    "aria-label": `${CX_LANG[l.language] || l.language}: ${l.functions} functions, the most complex at ${l.max}` },
  bins.map((b, i) => svg("g", { class: `cxbar lv-${level(b)}` },
    svg("title", {}, `${b.from}–${b.to}: ${b.count} function${b.count === 1 ? "" : "s"}`),
    svg("rect", { x: i * bw + 1, y: y(b.count), width: Math.max(bw - 2, 1), height: band + plot - y(b.count) }),
    b.count > 0 && svg("text", { x: i * bw + bw / 2, y: y(b.count) - 3, "text-anchor": "middle", class: "cxcount" }, b.count))),
  marks.map(([cls, , v]) => svg("line", { class: `cxth lv-${cls}`, x1: ((v - 1) / width) * bw, x2: ((v - 1) / width) * bw, y1: band - 2, y2: band + plot })),
  labels.map((lb) => svg("text", { x: lb.x + 3, y: lb.yy, class: `cxlabel lv-${lb.cls}` }, lb.text)),
  svg("line", { x1: 0, x2: W, y1: band + plot, y2: band + plot, class: "cxaxis" }),
  bins.map((b, i) => i % Math.ceil(n / 12) === 0 && svg("text", { x: i * bw + 2, y: H - 4, class: "cxtick" }, b.from)));
  const lv = (c) => c >= th.problem ? "problem" : c >= th.warn ? "warn" : c >= th.note ? "info" : "low";
  const most = (l.top || []).length > 0 && h("div", { class: "cxtop" }, h("h5", {}, "Most complex"),
    h("ol", {}, l.top.map((f) => h("li", {}, h("span", { class: `cxn lv-${lv(f.complexity)}` }, f.complexity),
      h("div", { class: "cxf" }, h("b", {}, f.name), h("div", { class: "muted small" }, filePath(`${f.path}:${f.line}`)))))));
  return h("div", { class: "cxlang" },
    h("h4", {}, CX_LANG[l.language] || l.language, h("span", { class: "muted small" }, ` · ${l.functions} functions, the most complex at ${l.max}`)),
    h("div", { class: "cxrow" }, h("div", { class: "cxscroll" }, chart), most),
    h("details", { class: "evfold" }, h("summary", {}, "Table"),
      h("table", { class: "hot" }, h("thead", {}, h("tr", {}, h("th", {}, "complexity"), h("th", { class: "n" }, "functions"))),
        h("tbody", {}, bins.map((b) => h("tr", {}, h("td", {}, `${b.from}–${b.to}`), h("td", { class: "n" }, b.count)))))));
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
async function viewArchitecture(p, sec) {
  if (sec === "coupling") return couplingSection(p);
  if (sec === "complexity") return complexitySection(p);
  const [s, built] = await Promise.all([api(projectAPI(p, "structure")), api(projectAPI(p, "stack")).catch(() => null)]);
  const builtOn = builtOnSection(built);
  if (!s.known) {
    return h("div", {}, builtOn, h("p", { class: "empty section" }, "No .NET project (.csproj), go.mod, package.json or tsconfig found: this view reads .NET, Go and TypeScript/JavaScript repositories."));
  }
  const a = s.architecture;
  const noun = a.noun || "project", nouns = noun + "s";
  const ts = a.language === "typescript" || a.language === "go"; // references are imports
  const users = s.package_users || {};
  const all = a.projects;
  // names without the prefix every project shares (Acme.RoutePlanner.)
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
  let sel = null, hover = null;
  // a large graph draws lines only for the package hovered or selected:
  // every line at once is a tangle
  const big = all.filter((x) => x.kind !== "test").length > 24;
  if (big) map.classList.add("big");
  const node = (x) => {
    const n = memberCount(x);
    const el = h("button", {
      class: `anode role-${x.role || "none"}`, title: x.name, onclick: () => select(sel === x.path ? null : x.path),
      onmouseenter: () => { if (!sel) { hover = x.path; draw(); } }, onmouseleave: () => { if (hover) { hover = null; draw(); } },
    },
      h("span", { class: "aname" }, short(x)),
      h("span", { class: "ameta" }, x.role ? h("span", { class: "arole" }, x.role) : x.kind === "entry" ? h("span", { class: "arole" }, "entry") : null,
        n > 0 && h("span", { class: "ausers", title: `${plural(n, "workspace member")} use its package` }, `↗ ${n}`)));
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
    const focus = sel || hover;
    for (const x of all) {
      for (const r of x.references) {
        const from = nodes[x.path], to = nodes[r];
        if (!from || !to) continue;
        const hot = focus && (x.path === focus || r === focus);
        if (focus && !hot) continue;
        if (!focus && (x.kind === "test" || big)) continue; // test edges, and a large graph's, only when in focus
        const fa = from.getBoundingClientRect(), ta = to.getBoundingClientRect();
        const x1 = fa.left + fa.width / 2 - box.left, y1 = fa.bottom - box.top;
        const x2 = ta.left + ta.width / 2 - box.left, y2 = ta.top - box.top;
        const down = y2 > y1;
        const ya = down ? y1 : fa.top - box.top, yb = down ? y2 : ta.bottom - box.top;
        const m = (ya + yb) / 2;
        lines.push(sv("path", { d: `M${x1},${ya} C${x1},${m} ${x2},${m} ${x2},${yb}`,
          class: "aline" + (hot ? (x.path === focus ? " out" : " in") : "") }));
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
    if (!x) return fill(side, h("p", { class: "hint" }, `${big ? `Point at a ${noun} to see its lines; select` : "Select"} a ${noun} to see what it ${ts ? "imports" : "references"}, what ${ts ? "imports" : "references"} it, its packages and who uses it. Lines point from a ${noun} to the ${nouns} it ${ts ? "imports" : "references"}.`));
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
  // the stack and the shape in one card
  const bcard = builtOn && builtOn.querySelector(".builton");
  if (bcard) { bcard.append(h("div", { class: "archline" }, ...head.childNodes)); }
  if (sec === "layers") {
    const intro = `${noun[0].toUpperCase() + nouns.slice(1)} by what they ${ts ? "import" : "reference"}: layer 0 ${ts ? "imports" : "references"} no other ${noun} here, each layer above ${ts ? "imports" : "references"} the ones below; ${nouns} in a circle share a layer. Tests sit on top.`;
    if (!big) return h("div", {}, section("Layers", intro, layout));
    // a large one also as a matrix, which stays readable at any size
    const body = h("div", {});
    const show = (k) => { store.set("layers.view", k); fill(body, k === "matrix" ? depMatrix(all, short, ts) : layout); if (k !== "matrix") requestAnimationFrame(draw); };
    const pick = h("div", { class: "seg", role: "radiogroup", "aria-label": "Show as" });
    const drawPick = (k) => fill(pick, [["map", "Map"], ["matrix", "Matrix"]].map(([v, t]) => h("button", { type: "button", role: "radio", "aria-checked": String(v === k), onclick: () => { drawPick(v); show(v); } }, t)));
    const k0 = store.get("layers.view", "map");
    drawPick(k0); show(k0);
    return h("div", {}, section("Layers", intro, h("div", { class: "filters" }, pick), body));
  }
  return h("div", {}, builtOn, !bcard && head, smells);
}

// depMatrix: who imports whom as a grid, a row and a column per package,
// ordered by layer (the highest first). A mark is "the row imports the
// column"; layered code keeps them right of the diagonal, so a mark left
// of it (a lower layer importing a higher one, or a circle) is red.
function depMatrix(all, short, ts) {
  // tests first (they sit on top), then the highest layer down
  const isTest = (x) => (x.kind === "test" ? 1 : 0);
  const xs = [...all].sort((a, b) => isTest(b) - isTest(a) || b.layer - a.layer || short(a).localeCompare(short(b)));
  const idx = Object.fromEntries(xs.map((x, i) => [x.path, i]));
  const n = xs.length, T = 24;
  let C = 18; // the cell size follows the card's width (below)
  const verb = ts ? "imports" : "references", verbed = ts ? "imported" : "referenced";
  // the label gutter fits the longest name
  const L = Math.max(...xs.map((x) => short(x).length)) * 7 + 24;
  const group = (x) => (isTest(x) ? "tests" : `L${x.layer}`);
  // the side: what it shows, or the package or pair in focus
  const side = h("aside", { class: "depside mxside" });
  let back = 0, edges = 0;
  const intro = () => fill(side, h("h4", {}, "Reading it"),
    h("p", { class: "small" }, `A row ${verb} the columns it has a mark in; the columns are the same ${n} packages in the same order, the highest layer first.`),
    h("p", { class: "small" }, "Layered code keeps every mark right of the dashed diagonal; a mark left of it (red) is a lower layer reaching up, or a circle."),
    h("p", { class: "muted small" }, `${plural(edges, "reference")} · ${plural(new Set(xs.map(group)).size, "layer group")}` + (back ? ` · ${back} against the layers` : "")),
    h("p", { class: "muted small" }, "Point at a mark for the pair; select a name for all of its."));
  let labels = [];
  const lit = (is, on) => { for (const i of is) labels[i].classList.toggle("on", on); };
  const nameList = (ps) => ps.length ? h("ul", { class: "mxlist" }, ps.map((p) => h("li", {}, h("a", { href: "#", onclick: (ev) => { ev.preventDefault(); pick(idx[p]); } }, short(xs[idx[p]] || { name: p }))))) : h("p", { class: "muted small" }, "none");
  let picked = null;
  function pick(i) {
    if (picked != null) lit([picked], false);
    picked = i;
    lit([i], true);
    const x = xs[i];
    fill(side, h("h4", {}, short(x)), h("p", { class: "muted small" }, `${group(x)} · ${x.kind}`),
      h("h5", {}, ts ? "Imports" : "References"), nameList((x.references || []).filter((r) => idx[r] != null)),
      h("h5", {}, `${verbed[0].toUpperCase() + verbed.slice(1)} by`), nameList((x.referenced_by || []).filter((r) => idx[r] != null)),
      h("button", { class: "ghost small", type: "button", onclick: () => { lit([i], false); picked = null; intro(); } }, "Back"));
  }
  // the pairs, counted once
  const pairs = [];
  xs.forEach((x, i) => (x.references || []).forEach((r) => {
    const j = idx[r];
    if (j == null) return;
    const wrong = j < i && !isTest(x);
    pairs.push([i, j, wrong]);
    edges++;
    if (wrong) back++;
  }));
  // draw lays the grid out at cell size C
  const holder = h("div", { class: "mxscroll" });
  function draw() {
    const W = L + n * C + 8, H = T + n * C + 8;
    // a faint stripe every other row, as a table; a line where a layer
    // group starts, across and down, its name above the columns
    const bands = [], heads = [];
    for (let i = 1; i < n; i += 2) bands.push(sv("rect", { x: 0, y: T + i * C, width: L + n * C, height: C, class: "mxband" }));
    let start = 0;
    for (let i = 1; i <= n; i++) {
      if (i === n || group(xs[i]) !== group(xs[start])) {
        const w = (i - start) * C;
        if (start > 0) {
          bands.push(sv("line", { x1: L + start * C, x2: L + start * C, y1: T - 16, y2: T + n * C, class: "mxsep" }));
          bands.push(sv("line", { x1: 0, x2: L + n * C, y1: T + start * C, y2: T + start * C, class: "mxsep" }));
        }
        heads.push(sv("text", { x: L + start * C + w / 2, y: T - 8, "text-anchor": "middle", class: "mxhead" }, w >= 26 ? group(xs[start]) : group(xs[start]).replace("tests", "t")));
        start = i;
      }
    }
    labels = xs.map((x, i) => sv("text", { x: L - 8, y: T + i * C + C / 2 + 4, "text-anchor": "end", class: "mxlabel" + (isTest(x) ? " test" : "") + (i === picked ? " on" : ""), onclick: () => pick(i) }, short(x)));
    const cells = pairs.map(([i, j, wrong]) => { const x = xs[i]; return sv("rect", { x: L + j * C + 2, y: T + i * C + 2, width: C - 4, height: C - 4, rx: 3, class: "mxdot" + (wrong ? " back" : ""),
      onmouseenter: () => { lit([i, j], true); if (picked == null) fill(side, h("h4", {}, short(x)), h("p", { class: "small" }, `${verb} `, h("b", {}, short(xs[j]))), wrong && h("p", { class: "flag warn small" }, "a lower layer reaching up")); },
      onmouseleave: () => { lit([i, j], false); if (picked != null) lit([picked], true); else intro(); } },
      sv("title", {}, `${short(x)} ${verb} ${short(xs[j])}`)); });
    // as wide as its card, never wider: the cells are sized to fit it
    fill(holder, sv("svg", { viewBox: `0 0 ${W} ${H}`, style: `width: min(100%, ${W}px)`, class: "depmx", role: "img", "aria-label": `${n} packages; ${back} imports against the layers` },
      bands, heads,
      sv("line", { x1: L, y1: T, x2: L + n * C, y2: T + n * C, class: "mxdiag" }),
      labels, cells));
  }
  intro();
  draw();
  // the cells grow to fill the card (14 to 34 px), the labels keep their size
  if (window.ResizeObserver) {
    new ResizeObserver(([e]) => {
      const c = Math.max(10, Math.min(34, Math.floor((e.contentRect.width - L - 12) / n)));
      if (c !== C) { C = c; draw(); }
    }).observe(holder);
  }
  const card = h("div", { class: "card pad mxcard" },
    h("p", { class: "small" }, back ? h("span", { class: "flag warn" }, `${plural(back, "import")} against the layers (red)`) : h("span", { class: "okay" }, "✓ every import points down the layers")),
    holder);
  return h("div", { class: "deplayout archlayout" }, card, side);
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
  Promise.all([api(projectAPI(p, "coupling"), { "X-Harness-UI": "1" }), api(projectAPI(p, "complexity")).catch(() => ({}))]).then(([c, cx]) => {
    const miss = c.missing || [];
    // each file's most complex function, over the reported languages
    const fileCx = Object.assign({}, ...(cx.languages || []).map((l) => l.files || {}));
    const th = ((cx.languages || [])[0] || {}).thresholds || { note: 11, warn: 21, problem: 31 };
    const hs = c.hotspots || [];
    const most = Math.max(1, ...hs.map((x) => x.commits));
    const cxCell = (x) => { const v = fileCx[x.path]; return v ? h("span", { class: `cxn lv-${v >= th.problem ? "problem" : v >= th.warn ? "warn" : v >= th.note ? "info" : "low"}` }, v) : h("span", { class: "muted" }, "–"); };
    fill(body,
      miss.length > 0 && h("div", { class: "card pad cmiss" },
        h("p", {}, h("b", {}, `Your uncommitted change leaves out ${plural(miss.length, "file")} that usually change with it`), h("span", { class: "muted" }, " (a reminder, not a rule)")),
        miss.map((m) => h("p", { class: "cline" }, h("code", {}, m.partner.path), " usually changes with ", h("code", {}, m.changed),
          h("span", { class: "muted" }, ` (${m.partner.together} commits together, ${pct(m.partner.share)} of its commits)`)))),
      c.changed && !miss.length && h("p", { class: "okay" }, `✓ Your uncommitted change (${plural(c.changed.length, "file")}) leaves out none of their usual partners.`),
      !c.providers && hs.length > 0 && h("p", { class: "muted small" }, "No provider offers change coupling (ripwire does), so the files that change together are unknown; the counts are Git's."),
      hs.length > 0 && Object.keys(fileCx).length > 0 && hotspotChart(hs, fileCx, th),
      hs.length ? h("table", { class: "hot" },
        h("thead", {}, h("tr", {}, h("th", {}, "file"), h("th", {}, "commits"), h("th", { class: "cxcell", title: "its most complex function" }, "complexity"), c.providers ? h("th", {}, "changes together with") : null)),
        h("tbody", {}, hs.map((x) => {
          const bar = h("span", { class: "cbar" }); bar.style.setProperty("--w", `${(100 * x.commits) / most}%`);
          return h("tr", {}, h("td", { class: "cfile" }, filePath(x.path)), h("td", {}, h("div", { class: "cbarcell" }, bar, h("span", { class: "n" }, x.commits))), h("td", { class: "cxcell" }, cxCell(x)),
            !c.providers ? null : h("td", {}, (x.partners || []).length ? h("div", { class: "cparts" }, x.partners.map(partner)) : h("span", { class: "muted" }, "nothing often")));
        })))
        : h("p", { class: "empty" }, "No code file changed more than once in the window."),
      (c.errors || []).length > 0 && h("p", { class: "muted small" }, c.errors.join("; ")));
  }).catch((e) => fill(body, h("p", { class: "error" }, e.message)));
  return section("Change coupling", "Code files changed most in the last 180 days (hotspots: harness's count from Git) and the files that change with them (co-change, from providers offering it; the percentage is the share of the file's commits). A partner that usually changes but was not touched may have been forgotten.", body);
}

// hotspotChart: each changing file by how often it changed (across) and
// its most complex function (up); top right is complex code that keeps
// changing, where bugs and slow changes concentrate. The worst few are
// named.
function hotspotChart(hs, fileCx, th) {
  const pts = hs.filter((x) => fileCx[x.path]).map((x) => ({ x, c: x.commits, k: fileCx[x.path] }));
  if (pts.length < 3) return null;
  const W = 640, H = 220, L = 34, B = 26, T = 10, R = 10;
  const cmax = Math.max(...pts.map((q) => q.c)), kmax = Math.max(th.problem + 5, ...pts.map((q) => q.k));
  const X = (c) => L + (c / cmax) * (W - L - R), Y = (k) => H - B - (k / kmax) * (H - B - T);
  const score = (q) => (q.c / cmax) * (q.k / kmax);
  const named = [...pts].sort((a, b) => score(b) - score(a)).slice(0, 4);
  const lv = (k) => k >= th.problem ? "problem" : k >= th.warn ? "warn" : k >= th.note ? "info" : "low";
  return h("div", { class: "card pad hotchart" },
    h("h5", {}, "Hotspots: complex and often changed"),
    sv("svg", { viewBox: `0 0 ${W} ${H}`, class: "hotsvg", role: "img", "aria-label": `Changing files by commits and complexity; the worst: ${named.map((q) => q.x.path.split("/").pop()).join(", ")}` },
      // the warning band: complexity above the warning threshold
      sv("rect", { x: L, y: T, width: W - L - R, height: Math.max(0, Y(th.warn) - T), class: "hband" }),
      sv("line", { x1: L, x2: W - R, y1: Y(th.warn), y2: Y(th.warn), class: "hth" }),
      sv("text", { x: W - R - 4, y: Y(th.warn) - 4, "text-anchor": "end", class: "hlabel" }, `warning ${th.warn}`),
      sv("line", { x1: L, x2: W - R, y1: H - B, y2: H - B, class: "haxis" }), sv("line", { x1: L, x2: L, y1: T, y2: H - B, class: "haxis" }),
      sv("text", { x: W - R, y: H - 6, "text-anchor": "end", class: "hlabel" }, `commits →  (${cmax} at most)`),
      sv("text", { x: 4, y: T + 8, class: "hlabel" }, "cx"),
      pts.map((q) => sv("circle", { cx: X(q.c), cy: Y(q.k), r: named.includes(q) ? 6 : 4.5, class: `hdot lv-${lv(q.k)}` }, sv("title", {}, `${q.x.path}: ${q.c} commits, complexity ${q.k}`))),
      hotLabels(named.map((q) => ({ x: X(q.c) - 9, y: Y(q.k) + 4, text: q.x.path.split("/").pop() })),
        [{ x: W - R, y: H - 6, text: `commits →  (${cmax} at most)` }, { x: W - R - 4, y: Y(th.warn) - 4, text: `warning ${th.warn}` }], H - B - 2, L + 4).map((lb) => sv("text", { x: lb.x, y: lb.y, "text-anchor": "end", class: "hname" }, lb.text))));
}

// hotLabels places right-aligned labels so none overlaps another: a label
// that would moves down a line, then up; one with no room is left out.
// taken are labels already drawn (axis captions); a label stays above
// floor and right of left.
function hotLabels(xs, taken, floor, left) {
  const placed = [...taken];
  const box = (lb) => ({ l: lb.x - lb.text.length * 6.8, r: lb.x, t: lb.y - 11, b: lb.y + 2 });
  const hits = (a, b) => a.l < b.r && b.l < a.r && a.t < b.b && b.t < a.b;
  const out = [];
  for (const lb of xs) {
    for (const dy of [0, -13, 13, -26, 26]) {
      const c = { ...lb, y: lb.y + dy };
      if (c.y > floor || box(c).l < left) continue;
      if (!placed.some((p) => hits(box(p), box(c)))) { placed.push(c); out.push(c); break; }
    }
  }
  return out;
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
    case "project": return (m = /^(\S+), (\d+) worktrees?$/.exec(d)) ? [`${m[2]} worktree${m[2] === "1" ? "" : "s"}`, [m[1]]] : ["", [d]];
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

// runFix runs a check's fix (or a sync) on the server and says how it went.
async function runFix(p, body, btn, after) {
  btn.disabled = true;
  const label = btn.textContent;
  btn.textContent = "Working…";
  try {
    const res = await post(projectAPI(p, "fix"), body);
    toast(res.message || (res.ok ? "Done" : "Failed"), !res.ok, res.output);
    if (res.ok) { await afterWrite(); after && after(); }
  } catch (e) { toast(e.message, true); }
  btn.disabled = false;
  btn.textContent = label;
}

function healthReport(r, p, reload) {
  const checks = r.checks || [];
  const n = (lv) => checks.filter((c) => c.level === lv).length;
  const st = r.status || {};
  const db = st.database || {};
  const sync = st.sync || {};
  // one summary line; the database and the index are named only when
  // something is wrong with them
  const issues = [
    n("fail") > 0 && `${plural(n("fail"), "failing check")}`,
    n("warn") > 0 && plural(n("warn"), "warning"),
    !db.exists ? "no database" : db.schema_version !== db.schema_latest && `schema ${db.schema_version} of ${db.schema_latest}`,
    db.error,
    sync.stale ? "index stale" : !sync.synced_at && "never synced",
  ].filter(Boolean);
  const out = h("div", {},
    h("div", { class: "card pad hsum " + (r.healthy ? "ok" : "bad") },
      h("span", { class: "hico", "aria-hidden": "true" }, icon(r.healthy ? "st-ok" : "st-fail")),
      p && (sync.stale || !sync.synced_at) && h("button", { class: "small hsync", type: "button", onclick: (ev) => runFix(p, { sync: true }, ev.currentTarget, reload) }, "Sync now"),
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
  const check = (c) => {
    // a failing check's detail without the file it read: that is on hover
    const at = c.level !== "ok" && /\s*\((?:checked|read|in) ([^)]+)\)/.exec(c.detail || "");
    const [value, lines] = c.level === "ok" ? checkParts(c) : ["", [at ? c.detail.replace(at[0], "") : c.detail]];
    return h("div", { class: `chk lv-${c.level}`, title: at ? at[1] : null },
      h("span", { class: "cico", "aria-hidden": "true" }, icon({ ok: "st-ok", warn: "st-warn", fail: "st-fail" }[c.level] || "st-ok")),
      h("div", { class: "cbody" },
        h("div", { class: "chead" }, h("span", { class: "cname" }, c.name, c.level !== "ok" && h("span", { class: "sr" }, ` (${c.level})`)), value && h("span", { class: "cvalue" }, value)),
        lines.filter(Boolean).map((x) => h("div", { class: "cdetail" }, x)), c.fix && h("div", { class: "fix" }, h("b", {}, "Fix: "), c.fix)),
      // what harness can fix itself: the action at the card's right
      p && (r.fixable || []).includes(c.name) && h("button", { class: "primary fixbtn", type: "button", onclick: (ev) => runFix(p, { check: c.name }, ev.currentTarget, reload) }, icon("chore"), "Fix it"));
  };
  const bad = checks.filter((c) => c.level !== "ok");
  const stacks = [["Installation", "Agents' clients"], ["Project"], ["Code providers"]];
  // what is wrong in full; what passes one line per area, its checks
  // (versions, paths) folded inside
  out.append(section("Checks", bad.length ? "What is wrong first, with its fix; then what passes." : null,
    bad.length > 0 && h("div", { class: "chkgrid wide" }, bad.map(check)),
    h("div", { class: "chkareas" }, stacks.flat().map((ar) => {
      const cs = checks.filter((c) => c.level === "ok" && (AREA[c.name] || "Code providers") === ar);
      return cs.length > 0 && h("details", { class: "chkarea" },
        h("summary", {}, h("h4", {}, ar, h("span", { class: "count" }, `✓ ${cs.length} pass`)), h("span", { class: "muted small" }, cs.map((c) => c.name).join(" · "))),
        h("div", { class: "chkgrid" }, cs.map(check)));
    }))));
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
// typeBar: the knowledge by type, one stacked bar in the types' colours;
// a segment toggles its type like its chip.
function typeBar(ms, on, toggle) {
  const n = ms.length;
  if (!n) return null;
  return h("div", { class: "tybar", role: "group", "aria-label": "Knowledge by type" },
    MEMORY_TYPES.filter((t) => ms.some((m) => m.type === t)).map((t) => {
      const c = ms.filter((m) => m.type === t).length;
      const seg = h("button", { class: `tyseg ty-${t}` + (on.has(t) ? "" : " off"), title: `${MEMORY_GROUPS[t]}: ${c}. ${MEMORY_TYPE_HELP[t][0]}`, "aria-pressed": String(on.has(t)), onclick: () => toggle(t) },
        h("span", { class: "tyn" }, c), h("span", { class: "tyl" }, MEMORY_GROUPS[t]));
      seg.style.setProperty("--w", String(c));
      return seg;
    }));
}

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
    // every type is loaded, the chosen ones shown: the bar counts them all
    const qs = new URLSearchParams({ type: MEMORY_TYPES.join(","), limit: "500", levels: "1" });
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
    const inScope = memories.filter((m) => !k.scope || m.scope === k.scope);
    const shown = inScope.filter((m) => k.types.has(m.type));
    summary.textContent = `${shown.length} memor${shown.length === 1 ? "y" : "ies"}${k.scope ? " in " + k.scope : ""}`;
    fill(bar, typeBar(inScope, k.types, (t) => { k.types.has(t) ? k.types.delete(t) : k.types.add(t); draw(); }));
    if (!k.types.size) return fill(list, h("p", { class: "empty" }, "Pick at least one type."));
    if (!shown.length) return fill(list, h("p", { class: "empty" }, empty || "No memories match."));
    fill(list, ...MEMORY_TYPES.filter((t) => shown.some((m) => m.type === t)).map((t) => {
      const group = shown.filter((m) => m.type === t);
      return h("section", { class: `mgroup ty-${t}` },
        h("h3", { title: MEMORY_TYPE_HELP[t][0] }, h("span", { class: "dot", "aria-hidden": "true" }), MEMORY_GROUPS[t], h("span", { class: "count" }, group.length)),
        h("div", { class: "mems" }, group.map((m) => memoryCard(p, m))));
    }));
  }
  const bar = h("div", {});
  let timer;
  const search = h("input", {
    type: "search", placeholder: "Search memories", value: k.q, "aria-label": "Search memories",
    oninput: (ev) => { k.q = ev.target.value.trim(); clearTimeout(timer); timer = setTimeout(load, 250); },
  });
  const inactive = h("label", { class: "check" },
    h("input", { type: "checkbox", checked: k.inactive, onchange: (ev) => { k.inactive = ev.target.checked; load(); } }),
    "include superseded and expired");
  const applies = appliesPanel(p);
  // the author adds knowledge too, not only agents (TASK-0056)
  const addBox = h("div", {});
  const add = h("button", { class: "primary", onclick: () => fill(addBox, actionForm({
    title: "Add knowledge for this project, as yours: agents get it where its type and scope apply.",
    fields: [{ name: "type", label: "Type", value: "decision", required: true, memoryType: true },
      { name: "content", label: "Statement", multiline: true, required: true },
      { name: "scope", label: "Applies to (project, or path:internal/gui, subsystem:sync, branch:main)", value: "project" },
      { name: "source_ref", label: "Source (optional)" }],
    submit: "Add",
    onSubmit: async (v) => {
      const r = await post(projectAPI(p, "memories"), v);
      if (r.status === "needs_decision") throw new Error(`Something similar is already recorded: ${(r.similar || []).map((x) => x.content).join("; ")} (correct that one instead)`);
      fill(addBox);
      await afterWrite();
    },
    onCancel: () => fill(addBox),
  })) }, "Add knowledge");
  // the type bar is the one type filter: its segments toggle
  wrap.append(h("div", { class: "section kadd" }, add, addBox), applies, bar, h("div", { class: "filters" }, search, scopeSel, inactive, summary), list);
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

// leadOf splits a statement into its point and the rest: up to the first
// ": ", "; " or sentence end, when that is a real part of it.
function leadOf(text) {
  const t = (text || "").trim();
  const m = /^(.{12,140}?[.:;])\s+(\S[\s\S]*)$/.exec(t);
  return m ? [m[1], m[2]] : [t, ""];
}

// onLevel: the card is on its level's own page, which says the scope.
function memoryCard(p, m, onLevel) {
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
  // the actions show on hover or focus: a page of memories is read, not
  // a wall of buttons
  const actions = h("div", { class: "review memacts" },
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
  const [lead, rest] = leadOf(m.content);
  return h("article", { class: `mem ty-${m.type}` + (inactive ? " inactive" : "") },
    // the point first, the detail after it
    h("div", { class: "content" }, h("b", { class: "mlead" }, lead), rest && h("span", { class: "mrest" }, " " + rest)),
    h("div", { class: "prov" },
      !onLevel && h("span", { class: "scope" + (level ? " lvl" : "") }, level ? (m.scope === "user" ? "you · all your projects" : `workspace · ${wtitle(m.scope.slice(10))}`) : m.scope),
      h("span", { class: "who", title: m.recorded_by || "" }, m.source_type === "user" ? "stated by you" : "recorded by an agent"),
      m.source_ref && h("span", { class: "src", title: m.source_ref }, m.source_ref),
      h("span", { title: `${m.created_at} · ${m.id}` }, ago(m.created_at)),
      m.confidence != null && h("span", {}, `confidence ${m.confidence}`),
      m.valid_until && h("span", {}, `valid until ${m.valid_until.slice(0, 10)}`),
      actions),
    (m.overrides || []).length > 0 && h("div", { class: "overrides" }, "Takes precedence here over ", m.overrides.join(", ")),
    (m.overridden_in || []).length > 0 && h("div", { class: "overrides" }, "Overridden by their own memory in: ", m.overridden_in.join(", ")),
    m.superseded_by && h("div", { class: "superseded" }, "Superseded by ",
      h("a", { href: "#", onclick: (ev) => { ev.preventDefault(); openHistory(true); }, title: "Show the versions, newest included" }, m.superseded_by),
      m.supersede_reason ? ": " + m.supersede_reason : ""),
    panel);
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
    fields: [{ name: "type", label: "Type", value: "convention", required: true, memoryType: true },
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
    return h("section", { class: `mgroup ty-${t}` }, h("h3", { title: MEMORY_TYPE_HELP[t][0] }, h("span", { class: "dot", "aria-hidden": "true" }), MEMORY_GROUPS[t], h("span", { class: "count" }, g.length)),
      h("div", { class: "mems" }, g.map((m) => memoryCard(null, m, true))));
  });
  // nothing yet: what each type is, to start from
  const empty = h("div", { class: "section" }, h("p", { class: "empty" }, "Nothing recorded yet: add a statement, or promote a project's memory from its Knowledge view. What each type is for:"),
    h("div", { class: "kindcards" }, MEMORY_TYPES.map((t) => h("div", { class: `kindcard tycard ty-${t}` },
      h("b", {}, h("span", { class: "dot", "aria-hidden": "true" }), " ", MEMORY_GROUPS[t].replace(/s$/, "")), h("span", { class: "muted small" }, MEMORY_TYPE_HELP[t][0])))));
  return h("div", {}, intro && h("p", { class: "hint" }, intro), h("div", { class: "review" }, add), box,
    groups.length ? groups : empty);
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
      if (f.memoryType) {
        const { sel, help } = typeSelect(f.value);
        inputs[f.name] = sel;
        return h("label", {}, h("span", {}, f.label), sel, help);
      }
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

// userFindings are the report findings the user can act on (TASK-0053):
// a configuration, hook, instruction or review fixes them; the others
// are about how harness itself gathers and serves: the developer's, sent
// only in the opt-in telemetry and never served here (the report endpoint
// leaves them out).
function userFindings(fs) { return fs.filter((f) => f.audience !== "developer"); }

const CLIENT_NAME = { "claude-code": "Claude Code", codex: "Codex" };

// viewServing is how well harness serves the project's agents, for the
// person using it: which agents use it, what the answers held, and what
// to change (TASK-0053).
async function viewServing(p) {
  const r = await api(projectAPI(p, `report?days=${state.days}`));
  const st = r.context || {}, ar = st.after_read || {};
  // a problem that stopped is not something to change: listed apart
  const resolved = (f) => /: resolved$/.test(f.message);
  const all = userFindings(r.findings || []);
  const fs = all.filter((f) => !resolved(f)), done = all.filter(resolved);
  const act = fs.filter((f) => f.level !== "info");
  const tools = (r.metrics && r.metrics.tools) || [];
  const calls = tools.reduce((n, t) => n + t.calls, 0);
  const clients = [...new Set(tools.flatMap((t) => t.clients || []))];
  const last = tools.map((t) => t.last_at).filter(Boolean).sort().pop();
  const hooks = (r.metrics && r.metrics.hooks && r.metrics.hooks.events) || 0;
  const providerProblems = providerGroups(fs.filter((f) => f.level !== "info")).length;
  return h("div", {},
    h("p", { class: act.length ? "flag warn section verdict" : "okay section verdict" },
      act.length ? `${plural(act.length, "thing")} to change so harness serves your agents better: see below.`
        : calls ? "✓ Harness serves your agents; nothing to change." : `No agent called harness in ${state.days} days.`),
    h("div", { class: "tiles" },
      tile("Agent calls", calls || "none",
        calls ? `from ${clients.map((c) => CLIENT_NAME[c] || c).join(", ")}; the last ${ago(last)}` : "connect an agent: harness install, then harness doctor"),
      tile("Context hits", st.changed ? pct(st.hit_rate) : "—",
        st.changed ? `per answer · ${st.hits} of ${st.changed} changed files over ${st.calls} answers` : "scored once agents' changes are committed (harness sync)"),
      tile("Read next", ar.touched ? pct(ar.hit_rate) : "—",
        ar.touched ? `of ${ar.touched} files agents read or edited after an answer, ${ar.hits} were in it` : hooks ? "nothing read after an answer yet" : "measured with the client hooks (Tools & providers → Hooks)"),
      tile("Knowledge", `${r.knowledge_reads || 0} read`, `${r.knowledge_writes || 0} written by agents; ${(r.knowledge || {}).current_memories || 0} current memories`),
      tile("Providers with problems", providerProblems || "✓ none", providerProblems ? "failing or slow: see below" : "no provider failed or was slow")),
    section("What to change", `What harness finds over the last ${r.days} days that you can act on, most severe first, each with what to do.`,
      fs.length ? h("div", { class: "findings" },
        // the providers' problems a card per provider, the others one each,
        // most severe first
        [...providerGroups(fs).map((g) => ({ level: g.level, el: providerCard(g) })), ...fs.filter((f) => !f.provider).map((f) => ({ level: f.level, el: findingCard(f) }))]
          .sort((a, b) => LV_RANK[a.level] - LV_RANK[b.level]).map((x) => x.el)) : emptyState("check", "Nothing to change", "Harness serves your agents as it should over this window."),
      done.length > 0 && h("details", { class: "capfold" }, h("summary", {}, `Resolved: ${plural(done.length, "problem")} that stopped`), h("div", { class: "findings" }, done.map(findingCard)))));
}

// providerGroups puts the provider findings together, one per provider
// with its capabilities' problems (failed, slow, empty), worst first.
const LV_RANK = { problem: 0, warn: 1, info: 2 };
function providerGroups(fs) {
  const by = new Map();
  for (const f of fs.filter((f) => f.provider)) {
    if (!by.has(f.provider)) by.set(f.provider, []);
    by.get(f.provider).push(f);
  }
  return [...by].map(([provider, xs]) => ({ provider, xs, level: xs.map((f) => f.level).sort((a, b) => LV_RANK[a] - LV_RANK[b])[0] }))
    .sort((a, b) => LV_RANK[a.level] - LV_RANK[b.level] || a.provider.localeCompare(b.provider));
}
// providerLine is a provider's problems in a few words: "impact and
// references fail and are slow".
function providerLine(g) {
  const caps = [...new Set(g.xs.map((f) => f.capability))];
  const kinds = [...new Set(g.xs.map((f) => f.problem))];
  const verb = { failed: caps.length === 1 ? "fails" : "fail", slow: caps.length === 1 ? "is slow" : "are slow", empty: "answers empty" };
  const list = (xs) => xs.length > 1 ? `${xs.slice(0, -1).join(", ")} and ${xs[xs.length - 1]}` : xs[0];
  return `${g.provider}: ${list(caps)} ${list(kinds.map((k) => verb[k] || k))}`;
}
const STATUS_WORD = { still: "still happening", unclear: "too few calls since to tell", resolved: "stopped" };
// providerCard is one provider's problems as a table: a row per
// capability, its failures and slowness, whether it goes on, its days.
function providerCard(g) {
  const caps = [...new Set(g.xs.map((f) => f.capability))];
  const actions = [...new Set(g.xs.map((f) => f.action).filter(Boolean))];
  const cell = (f) => f ? h("span", { class: `pfig lv-${f.level}` }, h("span", { class: "ico", "aria-hidden": "true" }, f.level === "problem" ? "✕" : f.level === "warn" ? "▲" : "ⓘ"), f.figure) : h("span", { class: "muted" }, "—");
  return h("article", { class: `finding provcard lv-${g.level}` },
    h("div", { class: "fhead" }, levelPill(g.level), h("b", {}, g.provider), h("span", { class: "farea" }, plural(caps.length, "capability", "capabilities"))),
    h("div", { class: "provwrap" }, h("table", { class: "provtab" },
      h("thead", {}, h("tr", {}, h("th", {}, "capability"), h("th", {}, "failed"), h("th", {}, "slow"), h("th", {}, "goes on?"), h("th", {}, "calls per day, red: with the problem"))),
      h("tbody", {}, caps.map((c) => {
        const mine = g.xs.filter((f) => f.capability === c);
        const get = (k) => mine.find((f) => f.problem === k);
        const trend = mine.map((f) => f.trend).find(Boolean);
        const status = mine.map((f) => f.status).find(Boolean);
        return h("tr", {},
          h("td", {}, h("b", {}, c.replaceAll("_", " ")), get("empty") && h("div", { class: "muted small" }, get("empty").figure)),
          h("td", {}, cell(get("failed"))), h("td", {}, cell(get("slow"))),
          h("td", { class: "small" + (status === "still" ? " still" : " muted") }, STATUS_WORD[status] || ""),
          h("td", {}, trend ? dayChart(trend) : ""));
      })))),
    actions.length > 0 && h("ul", { class: "factions" }, actions.map((a) => h("li", {}, "→ ", a))));
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
      title: `${d.day}: ${plural(d.calls, "call")}${d.errors ? `, ${d.errors} failed` : ""}${d.slow ? `, ${d.slow} slow` : ""}`,
    });
    bar.style.setProperty("--h", `${d.calls ? Math.max(8, Math.round((d.calls / peak) * 100)) : 4}%`);
    chart.append(bar);
  }
  return chart;
}

async function viewProviders(p) {
  const v = await api(projectAPI(p, `providers?days=${state.days}`));
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
    return h("article", { class: "provcard" },
      h("div", { class: "provhead" },
        h("h3", {}, ps.name), h("span", { class: "tag" }, kind(ps)), ps.pack && h("span", { class: "tag" }, `pack ${ps.pack}`),
        h("span", { class: "provsum" }, calls ? `${plural(calls, "call")} · ${pct(calls ? errs / calls : 0)} failed` : `not called in ${state.days} days`)),
      h("div", { class: "pmeta mono muted" }, ps.command),
      (ps.only || []).length > 0 && h("div", { class: "pmeta" }, "Limited to ", ps.only.map(cap).join(", ")),
      caps.length === 0 ? h("p", { class: "empty" }, ps.type === "harness" ? "Declares its capabilities when started; none called yet." : "No capabilities.")
        : [quiet.length > 0 && h("p", { class: "pmeta muted" }, `Not called in ${state.days} days: `, quiet.map((c) => cap(c.capability) + (c.tool ? ` (${c.tool})` : "")).join(", "))],
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
  // the ones a provider could add, first; the served ones in a line
  const complete = (c) => c.providers.length || /nothing is missing/.test(c.without || "");
  const missing = arch.filter((c) => !complete(c)), served = arch.filter(complete);
  if (arch.length) out.append(section("Architecture evidence", "What a provider would add to the Architecture view; nothing fails without one.",
    missing.length > 0 && h("div", { class: "card" }, h("table", { class: "archcov" },
      h("thead", {}, h("tr", {}, h("th", {}, "capability"), h("th", {}, "without a provider"))),
      h("tbody", {}, missing.map((c) => h("tr", {},
        h("td", {}, h("b", {}, c.capability.replace(/^arch\./, "").replaceAll("_", " "))),
        h("td", { class: "wrap small muted" }, c.without || "—")))))),
    served.length > 0 && h("p", { class: "okay small" }, "✓ Complete: ", served.map((c) => `${c.capability.replace(/^arch\./, "").replaceAll("_", " ")}${c.providers.length ? ` (${c.providers.join(", ")})` : ""}`).join(", "))));
  out.append(hooksSection(p));
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
      ss.length ? h("table", { class: "gstable" },
        h("thead", {}, h("tr", {}, !projectId && h("th", {}, "solution"), h("th", {}, "state"), h("th", {}, "detail"), h("th", {}, ""),
          h("th", { class: "gsact" }, iconButton("refresh", "Refresh", () => load().catch((e) => fill(box, h("p", { class: "error" }, e.message))))))),
        h("tbody", {}, ss.map((s) => {
        // "ready, 0.1 GB, up 2h, …": the state apart, the rest as it says
        const [stateWord, ...rest] = (s.summary || "").split(", ");
        return h("tr", {},
        !projectId && h("td", {}, s.project_id ? h("a", { href: projectHref(s.project_id, "agents", "tools") }, s.project_name || s.project_id)
          : h("span", { title: s.root }, h("b", {}, baseName(s.root)), h("span", { class: "muted small" }, " " + shortPath(s.root.slice(0, -baseName(s.root).length - 1))))),
        h("td", {}, h("span", { class: `state ${stateWord === "ready" ? "ok" : stateWord === "loading" ? "warn" : "bad"}` }, stateWord)),
        h("td", { class: "small muted" }, rest.join(" · ")),
        h("td", { class: "mono muted small", title: s.id }, s.id.slice(0, 8)),
        h("td", { class: "gsact" }, h("div", { class: "gsbtns" }, s.project_id && act("restart", { project: s.project_id }, "Restart"), act("stop", { session: s.id }, "Stop"))));
        })))
        : h("p", { class: "muted small" }, projectId ? "No warm session for this project: the first callers, references or impact question starts one." : "No graphify sessions on this machine."),
      // with sessions, Refresh is in the table's header
      !ss.length && h("div", { class: "gsbtns" }, projectId && act("start", { project: projectId }, "Start a session"),
        iconButton("refresh", "Refresh", () => load().catch((e) => fill(box, h("p", { class: "error" }, e.message))))));
  };
  load().catch((e) => fill(box, h("p", { class: "error" }, e.message)));
  return box;
}

// hooksSection shows which hooks are installed, read from the files as
// 'harness hooks status' does, next to the events they recorded in a week
// (TASK-0044): installed but silent says the hook may not run.
function hooksSection(p) {
  const box = h("div", { class: "card pad" }, h("p", { class: "muted small" }, "Reading the hook files…"));
  const yes = (on, label) => h("span", { class: on ? "okay" : "muted" }, on ? `✓ ${label}` : `○ ${label}`);
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
      ),
      h("h4", {}, "Client hooks"),
      h("table", { class: "gstable" }, h("tbody", {}, (d.clients || []).map((c) => h("tr", {},
        h("td", { title: c.path }, c.client === "claude-code" ? "Claude Code" : "Codex", h("span", { class: "muted" }, ` · ${c.scope}`)),
        h("td", { class: "small" }, yes(!!c.command, "tool events"), c.client === "claude-code" && ["  ", yes(!!c.session, "session start")]),
        h("td", { class: "small" }, c.error ? h("span", { class: "error" }, c.error) : c.problem ? h("span", { class: "error" }, c.problem)
          // one scope with the hook is enough: the install hint only for a client with none
          : !c.command && !(d.clients || []).some((o) => o.client === c.client && o.command) ? "harness hooks install" : ""))))),
      h("p", { class: "small" }, ev.events ? `Last event ${ago(ev.last_at)}: the hooks run.` : "No events recorded in 7 days: if they are installed, they may not run."),
      h("details", { class: "evfold" }, h("summary", {}, "Files"), h("ul", { class: "evlist" }, (d.clients || []).map((c) => h("li", {}, h("code", {}, c.path))), d.git_dir && h("li", {}, h("code", {}, d.git_dir)))),
      d.note && h("p", { class: "small freview" }, d.note));
  }).catch((e) => fill(box, h("p", { class: "error" }, e.message)));
  return section("Hooks", "Installed hooks tell harness what agents read and when sessions start.", box);
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
      fill(list, ...(ps.length ? ps.map((x) => proposalCard(p, x)) : [st.status === "pending" ? emptyState("inbox", "Inbox zero", "No proposal waits for you: agents' suggestions appear here for you to approve.") : h("p", { class: "empty" }, `No ${st.status === "all" ? "" : st.status + " "}proposals.`)]));
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
      ] : [st.status === "pending" ? emptyState("inbox", "Inbox zero", "No proposal waits for you: agents' suggestions appear here for you to approve.") : h("p", { class: "empty" }, `No ${st.status === "all" ? "" : st.status + " "}proposals.`)]));
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

// --- form pieces (TASK-0057) ---
// segmented is a choice among a few, every option in view (with an icon
// when it has one); value() reads it.
function segmented(label, opts, cur, cls, iconOf) {
  let v = cur;
  const box = h("div", { class: "seg segpick" + (cls ? " " + cls : ""), role: "radiogroup", "aria-label": label });
  const draw = () => fill(box, opts.map(([k, t]) => h("button", { type: "button", role: "radio", "aria-checked": String(k === v), class: `opt-${k}`, onclick: () => { v = k; draw(); } }, iconOf && icon(iconOf(k)), t)));
  draw();
  return { el: box, value: () => v };
}
// listBuilder edits a list one row each: Enter adds the next row, × removes
// one (acceptance criteria, exit criteria).
function listBuilder(items, placeholder, mark) {
  const box = h("div", { class: "lbuild" });
  const row = (text) => {
    const input = h("input", { type: "text", value: text || "", placeholder });
    const r = h("div", { class: "lrow" }, h("span", { class: "lbox" + (mark === "round" ? " round" : ""), "aria-hidden": "true" }), input,
      h("button", { type: "button", class: "iconbtn", title: "Remove", "aria-label": "Remove", onclick: () => { r.remove(); if (!box.querySelector(".lrow")) box.insertBefore(row(""), add); } }, icon("close")));
    input.addEventListener("keydown", (ev) => {
      if (ev.key === "Enter") { ev.preventDefault(); const n = row(""); r.after(n); n.querySelector("input").focus(); }
      if (ev.key === "Backspace" && !input.value && box.querySelectorAll(".lrow").length > 1) { ev.preventDefault(); const prev = r.previousElementSibling; r.remove(); prev?.querySelector("input")?.focus(); }
    });
    return r;
  };
  const add = h("button", { type: "button", class: "ladd", onclick: () => { const n = row(""); box.insertBefore(n, add); n.querySelector("input").focus(); } }, icon("plus"), "Add another (or press Enter)");
  fill(box, (items && items.length ? items : [""]).map(row), add);
  return { el: box, value: () => [...box.querySelectorAll(".lrow input")].map((i) => i.value.trim()).filter(Boolean) };
}
// tokenInput holds a few short values as chips (task ids, paths); Enter or
// a comma makes a chip, suggestions complete them.
function tokenInput(values, placeholder, suggestions) {
  const vals = [...(values || [])];
  const listId = "tok-" + Math.random().toString(36).slice(2);
  const input = h("input", { type: "text", placeholder, list: suggestions ? listId : null });
  const chips = h("span", { class: "tchips" });
  const draw = () => fill(chips, vals.map((v, i) => h("span", { class: "tchip" }, v, h("button", { type: "button", "aria-label": `Remove ${v}`, onclick: () => { vals.splice(i, 1); draw(); } }, "×"))));
  const take = () => { for (const v of input.value.split(",").map((x) => x.trim()).filter(Boolean)) if (!vals.includes(v)) vals.push(v); input.value = ""; draw(); };
  input.addEventListener("keydown", (ev) => {
    if (ev.key === "Enter" || ev.key === ",") { ev.preventDefault(); take(); }
    if (ev.key === "Backspace" && !input.value && vals.length) { vals.pop(); draw(); }
  });
  input.addEventListener("change", take);
  draw();
  return {
    el: h("div", { class: "tokens", onclick: () => input.focus() }, chips, input, suggestions && h("datalist", { id: listId }, suggestions.map((x) => h("option", { value: x })))),
    value: () => { take(); return [...vals]; },
  };
}

// taskPicker chooses the tasks one waits for: search by id or title over
// this project's tasks and the workspace's other projects' (those written
// <project>:<task id>, TASK-0058), several chosen as chips.
function taskPicker(p, chosen, self) {
  const picked = [...chosen];
  let all = [], others = [], q = "", at = 0;
  const chips = h("span", { class: "tchips" });
  const input = h("input", { type: "text", placeholder: "Search tasks by id or title", "aria-label": "Depends on", autocomplete: "off" });
  const menu = h("div", { class: "tpmenu", hidden: true, role: "listbox" });
  const box = h("div", { class: "tokens tpick", onclick: () => input.focus() }, chips, input, menu);
  const extKey = (x) => `${x.p.name || baseName(x.p.root)}:${x.id}`;
  const drawChips = () => fill(chips, picked.map((id, i) => {
    const t = all.find((x) => x.id === id) || others.find((x) => extKey(x) === id);
    return h("span", { class: "tchip", title: t ? t.title : "" }, t && t.p && projectMark(t.p), id, h("button", { type: "button", "aria-label": `Remove ${id}`, onclick: (ev) => { ev.stopPropagation(); picked.splice(i, 1); drawChips(); drawMenu(); } }, "×"));
  }));
  const match = (x) => !q || x.id.toLowerCase().includes(q) || (x.title || "").toLowerCase().includes(q);
  const toggle = (id) => { const i = picked.indexOf(id); i >= 0 ? picked.splice(i, 1) : picked.push(id); input.value = ""; q = ""; drawChips(); drawMenu(); input.focus(); };
  function drawMenu() {
    const mine = all.filter((x) => x.id !== self && x.status !== "done" && match(x)).slice(0, 40);
    const theirs = others.filter(match).slice(0, 20);
    at = Math.min(at, Math.max(0, mine.length - 1));
    fill(menu, mine.length ? mine.map((x, i) => h("button", { type: "button", role: "option", class: "tpopt" + (i === at ? " at" : "") + (picked.includes(x.id) ? " on" : ""), "aria-selected": String(picked.includes(x.id)),
      onmousedown: (ev) => ev.preventDefault(), onclick: () => toggle(x.id) },
      h("span", { class: "tpcheck", "aria-hidden": "true" }, picked.includes(x.id) ? "✓" : ""), h("span", { class: "mono small" }, x.id), h("span", { class: "tptitle" }, x.title), statusPill(x.status)))
      : h("p", { class: "muted small tpnone" }, q ? "No open task here matches." : "No open task in this project."),
      theirs.length > 0 && [h("div", { class: "tphead" }, "In the workspace's other projects"),
        theirs.map((x) => h("button", { type: "button", role: "option", class: "tpopt" + (picked.includes(extKey(x)) ? " on" : ""), "aria-selected": String(picked.includes(extKey(x))),
          onmousedown: (ev) => ev.preventDefault(), onclick: () => toggle(extKey(x)) },
          h("span", { class: "tpcheck", "aria-hidden": "true" }, picked.includes(extKey(x)) ? "✓" : ""), projectMark(x.p), h("span", { class: "mono small" }, x.id), h("span", { class: "tptitle" }, x.title), statusPill(x.status)))]);
  }
  input.addEventListener("focus", () => { menu.hidden = false; drawMenu(); });
  input.addEventListener("blur", () => setTimeout(() => { menu.hidden = true; }, 120));
  input.addEventListener("input", () => { q = input.value.trim().toLowerCase(); at = 0; menu.hidden = false; drawMenu(); });
  input.addEventListener("keydown", (ev) => {
    const opts = [...menu.querySelectorAll("button.tpopt")];
    if (ev.key === "ArrowDown") { ev.preventDefault(); at = Math.min(at + 1, opts.length - 1); drawMenu(); }
    else if (ev.key === "ArrowUp") { ev.preventDefault(); at = Math.max(at - 1, 0); drawMenu(); }
    else if (ev.key === "Enter") { ev.preventDefault(); opts[at]?.click(); }
    else if (ev.key === "Escape") { menu.hidden = true; }
    else if (ev.key === "Backspace" && !input.value && picked.length) { picked.pop(); drawChips(); drawMenu(); }
  });
  // this project's tasks, and the workspace's other members' to show
  api(projectAPI(p, "tasks")).then((r) => { all = r.tasks || []; drawChips(); if (!menu.hidden) drawMenu(); }).catch(() => {});
  const members = [...new Set((state.workspaces || []).filter((w) => (p.workspaces || []).includes(w.name)).flatMap((w) => w.members.map((m) => m.project_id)))].filter((id) => id !== p.id);
  Promise.all(members.map((id) => { const q2 = state.projects.find((x) => x.id === id); return q2 ? api(projectAPI(q2, "tasks")).then((r) => (r.tasks || []).filter((x) => x.status !== "done").map((x) => ({ ...x, p: q2 }))).catch(() => []) : []; }))
    .then((xs) => { others = xs.flat(); if (!menu.hidden) drawMenu(); });
  drawChips();
  return { el: box, value: () => [...picked] };
}

// taskFields is the form for a task's definition (new, a proposal
// restated before it is approved, or an unclaimed task edited): what it
// is, how it is sorted, when it is done, what it touches; value() reads it
// back. An edited task keeps its id.
function taskFields(p, init, blueprints, keepId) {
  const id = h("input", { type: "text", value: init.id || "", placeholder: "next free id", class: "mono", "aria-label": "Id", disabled: !!keepId });
  if (!init.id) api(projectAPI(p, "tasks-next-id")).then((r) => { id.placeholder = r.id; }).catch(() => {});
  const title = h("input", { type: "text", value: init.title || "", required: true, class: "tbig", placeholder: "What should be done", "aria-label": "Title" });
  const goal = h("textarea", { rows: 3, placeholder: "The outcome wanted, and why", "aria-label": "Goal" }, init.goal || "");
  const type = segmented("Type", TASK_TYPES.map((t) => [t, t]), init.type || "feature", "types", (k) => (k === "docs" ? "docs" : k));
  const prio = segmented("Priority", PRIORITIES.map((t) => [t, t]), init.priority || "medium", "prio", (k) => "p-" + k);
  const known = blueprints.map((m) => [m.id, m.title ? `${m.id} · ${m.title}` : m.id]);
  if (init.blueprint && !blueprints.some((m) => m.id === init.blueprint)) known.push([init.blueprint, `${init.blueprint} (new)`]);
  const bp = h("select", { "aria-label": "Blueprint" }, h("option", { value: "" }, "none"),
    known.map(([v, t]) => h("option", { value: v, selected: v === (init.blueprint || "") }, t)));
  const acc = listBuilder(init.acceptance, "A criterion one can check", "box");
  const deps = taskPicker(p, init.depends_on || [], init.id);
  const scope = tokenInput(init.scope, "a path, e.g. internal/tasks");
  const group = (title2, ...kids) => h("fieldset", { class: "fgroup" }, h("legend", {}, title2), ...kids);
  return {
    nodes: [
      h("div", { class: "frow-title" }, field("Title", title), field("Id", id, keepId ? "An edited task keeps its id." : "Empty: the next free one.")),
      group("Sorting", field("Type", type.el), h("hr", { class: "fsep" }), field("Priority", prio.el), h("hr", { class: "fsep" }), field("Blueprint", bp)),
      field("Goal", goal),
      group("Done when", acc.el),
      group("Around it", field("Depends on", deps.el, "Tasks that must be done first: search by id or title."), field("Scope", scope.el, "Paths it may touch.")),
    ],
    value: () => ({ id: id.value.trim(), title: title.value, goal: goal.value, type: type.value(), priority: prio.value(), blueprint: bp.value,
      depends_on: deps.value(), acceptance: acc.value(), scope: scope.value() }),
  };
}

// newTaskDialog adds a task by hand.
async function newTaskDialog(p, blueprints, blueprint) {
  const dlg = dialog({ title: "New task", sub: "It starts in the backlog and is ready once its dependencies are done; 'harness tasks export' writes it into the backlog file.", wide: true });
  const f = taskFields(p, { blueprint: blueprint || "" }, blueprints);
  const err = h("p", { class: "error", hidden: true });
  const add = h("button", { class: "primary", type: "button", onclick: async () => {
    add.disabled = true;
    try { await post(projectAPI(p, "tasks"), f.value()); dlg.close(); await afterWrite(); }
    catch (e) { err.textContent = e.message; err.hidden = false; add.disabled = false; }
  } }, "Add the task");
  dlg.set(null, [...f.nodes, err], [h("button", { class: "ghost", type: "button", onclick: () => dlg.close() }, "Cancel"), add]);
}

// editTaskDialog restates a ready or backlog task with the same fields;
// the definition is fixed once an agent claims it.
async function editTaskDialog(p, x) {
  const dlg = dialog({ title: `Edit ${x.id}`, sub: "It can change until an agent claims it; its validation and notes stay as they are.", wide: true });
  const ms = await api(projectAPI(p, "blueprints")).then((r) => r.blueprints || []).catch(() => []);
  const init = { ...x, depends_on: (x.depends_on || []).map((d) => d.id), acceptance: (x.acceptance || []).map((c) => c.criterion) };
  const f = taskFields(p, init, ms, true);
  const err = h("p", { class: "error", hidden: true });
  const save = h("button", { class: "primary", type: "button", onclick: async () => {
    save.disabled = true;
    try { await post(projectAPI(p, `tasks/${enc(x.id)}/edit`), f.value()); dlg.close(); toast(`${x.id} saved`); await afterWrite(); }
    catch (e) { err.textContent = e.message; err.hidden = false; save.disabled = false; }
  } }, "Save the task");
  dlg.set(null, [...f.nodes, err], [h("button", { class: "ghost", type: "button", onclick: () => dlg.close() }, "Cancel"), save]);
}

function taskEditable(x) {
  return x.status === "ready" || x.status === "backlog";
}

// blueprintFields is a blueprint's description, new or edited: a new one
// has an id (the next free one suggested, as 'harness tasks blueprints
// add' gives it), an edited one keeps its id; value() reads it back.
function blueprintFields(p, init, isNew) {
  const id = isNew && h("input", { type: "text", value: "", required: true, class: "mono", "aria-label": "Id" });
  if (isNew) api(projectAPI(p, "blueprints-next-id")).then((r) => { if (!id.value) id.value = r.id; }).catch(() => {});
  const title = h("input", { type: "text", value: init.title || "", required: true, class: "tbig", placeholder: "e.g. Search works offline", "aria-label": "Title" });
  const goal = h("textarea", { rows: 3, placeholder: "What it is for: the outcome, and why now", "aria-label": "Goal" }, init.goal || "");
  const exit = listBuilder(init.exit || [], "A criterion that says it is done", "round");
  const notes = h("textarea", { rows: 2, "aria-label": "Notes" }, init.notes || "");
  return {
    nodes: [isNew ? h("div", { class: "frow2 bphead" }, field("Id", id, "Suggested: the next one."), field("Title", title)) : field("Title", title),
      field("Goal", goal), h("fieldset", { class: "fgroup" }, h("legend", {}, "Done when"), exit.el), field("Notes", notes)],
    value: () => ({ ...(isNew ? { id: id.value.trim() } : {}), title: title.value.trim(), goal: goal.value.trim(), exit: exit.value(), notes: notes.value.trim() }),
  };
}

// blueprintDialog creates a blueprint (m null) or restates one; its id stays.
function blueprintDialog(p, m) {
  const isNew = !m;
  const dlg = dialog({ title: isNew ? "New blueprint" : `Edit blueprint ${m.id}`, sub: isNew ? "A group of tasks with what it is for and when it is done." : "Its id and its tasks stay.", wide: true });
  const f = blueprintFields(p, m || {}, isNew);
  const err = h("p", { class: "error", hidden: true });
  const save = h("button", { class: "primary", type: "button", onclick: async () => {
    save.disabled = true;
    try {
      const v = f.value();
      if (isNew) { await post(projectAPI(p, "blueprints"), v); toast(`Blueprint ${v.id} created`); }
      else { await post(projectAPI(p, `blueprints/${enc(m.id)}/edit`), v); toast(`Blueprint ${m.id} saved`); }
      dlg.close(); await afterWrite();
    } catch (e) { err.textContent = e.message; err.hidden = false; save.disabled = false; }
  } }, isNew ? "Create blueprint" : "Save blueprint");
  dlg.set(null, [...f.nodes, err], [h("button", { class: "ghost", type: "button", onclick: () => dlg.close() }, "Cancel"), save]);
}
function newBlueprintDialog(p) { blueprintDialog(p, null); }

// removeBlueprintDialog confirms removing a blueprint and, when it has
// tasks, asks what happens to them: moved to another blueprint (or none),
// or deleted with it (TASK-0048).
function removeBlueprintDialog(p, m, others) {
  const url = projectAPI(p, `blueprints/${enc(m.id)}/remove`);
  const backlog = "A backlog file that still defines it brings it back on the next import: 'harness tasks export' writes the change into the file.";
  if (!m.total) {
    return confirmDelete({ title: `Remove blueprint ${m.id}?`, what: m.title, lines: ["It has no tasks.", backlog],
      confirm: "Remove the blueprint", send: () => post(url, {}), done: `Blueprint ${m.id} removed` });
  }
  const dlg = dialog({ title: `Remove blueprint ${m.id}?`, sub: `It has ${plural(m.total, "task")}. What happens to them?` });
  const name = "bprm-" + m.id;
  const move = h("input", { type: "radio", name, value: "move", checked: true, "aria-label": "Move the tasks" });
  const del = h("input", { type: "radio", name, value: "delete", "aria-label": "Delete the tasks" });
  const to = h("select", { "aria-label": "Move to", onchange: () => { move.checked = true; } },
    h("option", { value: "" }, "no blueprint"),
    others.map((o) => h("option", { value: o.id }, o.title ? `${o.id} · ${o.title}` : o.id)));
  const err = h("p", { class: "error", hidden: true });
  const ok = h("button", { class: "danger", type: "button", onclick: async () => {
    ok.disabled = true;
    try {
      await post(url, del.checked ? { tasks: "delete" } : { tasks: "move", move_to: to.value });
      dlg.close(); toast(`Blueprint ${m.id} removed`); await afterWrite();
    } catch (e) { err.textContent = e.message; err.hidden = false; ok.disabled = false; }
  } }, "Remove the blueprint");
  dlg.set(null, [m.title && h("p", { class: "dpname" }, m.title),
    h("div", { class: "choices" },
      h("label", { class: "choice" }, move, h("span", {}, "Move them to"), to),
      h("label", { class: "choice" }, del, h("span", {}, "Delete them, with their audit trail"))),
    h("ul", { class: "dchanges" }, h("li", {}, "Deleting is refused while an agent holds one of them; tasks elsewhere that depend on a deleted one lose that dependency."), h("li", {}, backlog)), err],
  [h("button", { class: "ghost", type: "button", onclick: () => dlg.close() }, "Cancel"), ok]);
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
    h("h4", { class: "ptitle2" }, d.title),
    d.goal && h("p", { class: "why" }, h("b", {}, "Goal: "), d.goal),
    x.reason && h("p", { class: "why" }, h("b", {}, "Why proposed: "), x.reason),
    exit.length > 0 && h("details", { class: "evfold", open: true }, h("summary", {}, `Done when (${exit.length})`), h("ul", { class: "donewhen" }, exit.map((e) => h("li", {}, e)))),
    d.notes && h("p", { class: "why" }, h("b", {}, "Notes: "), d.notes),
    h("div", { class: "prov" },
      h("span", {}, h("b", {}, "proposed by "), x.proposed_by || "unknown"),
      h("span", { title: x.proposed_at }, ago(x.proposed_at)),
      x.decided_by && h("span", {}, h("b", {}, `${x.status} by `), x.decided_by),
      x.note && h("span", {}, h("b", {}, "note "), x.note)),
    x.status === "pending" && h("div", { class: "review" },
      h("button", { class: "primary", onclick: () => fill(panel, approveForm()) }, isNew ? "Create the blueprint…" : "Apply the change…"),
      h("button", { class: "ghost", onclick: () => fill(panel, rejectForm()) }, "Reject…")),
    panel);
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
  return h("article", { class: "mem aprop" + (x.status === "rejected" ? " inactive" : ""), "data-proposal": x.id },
    h("div", { class: "mtype" }, "Task", h("span", { class: "tag" }, `${d.type || "feature"} · ${d.priority || "medium"}`),
      d.blueprint && h("span", { class: "tag" }, d.blueprint),
      x.status !== "pending" && h("span", { class: "muted" }, ` · ${x.status}${x.result ? ` → ${x.result}` : ""}`)),
    h("h4", { class: "ptitle2" }, d.title),
    d.goal && h("p", { class: "why" }, h("b", {}, "Goal: "), d.goal),
    x.reason && h("p", { class: "why" }, h("b", {}, "Why proposed: "), x.reason),
    acc.length > 0 && h("details", { class: "evfold", open: true }, h("summary", {}, `Done when (${acc.length})`), h("ul", { class: "donewhen" }, acc.map((a) => h("li", {}, a)))),
    h("div", { class: "prov" },
      (d.depends_on || []).length > 0 && h("span", {}, h("b", {}, "depends on "), d.depends_on.join(", ")),
      (d.scope || []).length > 0 && h("span", {}, h("b", {}, "scope "), d.scope.join(", ")),
      h("span", {}, h("b", {}, "proposed by "), x.proposed_by || "unknown"),
      h("span", { title: x.proposed_at }, ago(x.proposed_at)),
      x.decided_by && h("span", {}, h("b", {}, `${x.status} by `), x.decided_by),
      x.note && h("span", {}, h("b", {}, "note "), x.note)),
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

// findingKind names a finding by its key's kind ("duplicate:a|b": "copy
// finding"); the key itself is the title.
const FINDING_NOUN = { duplicate: "copy", "local-copy": "local copy", complexity: "complexity", template: "template", contract: "contract", sequence: "repeated statements" };
function findingKind(key) {
  const k = key.split(":")[0];
  return `${FINDING_NOUN[k] || k.replaceAll("-", " ")} finding`;
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
      own ? h("span", { class: "tag", title: "Not found by harness: the agent's reading of the code" }, "the agent's reading")
        : h("span", { class: "tag" }, `harness finding · ${x.basis}`),
      x.status !== "pending" && h("span", { class: "muted" }, ` · ${x.status}${x.result && x.result !== "marked" ? ` → ${x.result}` : ""}`)),
    h("h4", { class: "ptitle2" }, x.title),
    x.body && h("p", { class: "why" }, h("b", {}, task ? "Proposed: " : "Why intended: "), x.body),
    ev.length > 0 && h("details", { class: "evfold" }, h("summary", {}, `Evidence (${ev.length})`), h("ul", { class: "evlist" }, ev.map((e) => h("li", {}, evidenceText(e))))),
    h("div", { class: "prov" },
      h("span", { class: "scope" }, x.scope),
      x.finding_key && h("span", { title: x.finding_key }, h("b", {}, "from a "), findingKind(x.finding_key)),
      h("span", {}, h("b", {}, "proposed by "), x.proposed_by || "unknown"),
      h("span", { title: x.proposed_at }, ago(x.proposed_at)),
      x.decided_by && h("span", {}, h("b", {}, `${x.status} by `), x.decided_by),
      x.note && h("span", {}, h("b", {}, "note "), x.note)),
    x.status === "pending" && h("div", { class: "review" },
      h("button", { class: "primary", onclick: () => fill(panel, approveForm()) }, task ? "Create the task…" : "Mark by design…"),
      h("button", { class: "ghost", onclick: () => fill(panel, rejectForm()) }, "Reject…")),
    panel);
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
  const milestones = [...new Set([...(mres.blueprints || []).map((m) => m.id), ...all.map((x) => x.blueprint || "—")])];
  const body = h("div", {});
  const list = h("div", {});

  function drawTasks() {
    const shown = all.filter((x) => x === linked || (t.statuses.has(x.status) && (!t.milestone || (x.blueprint || "—") === t.milestone)));
    // Deferred tasks are set aside by a decision: unless asked for, each
    // blueprint shows them as one line per reason instead of one row each.
    const deferred = t.statuses.has("deferred") ? [] :
      all.filter((x) => x.status === "deferred" && x !== linked && (!t.milestone || (x.blueprint || "—") === t.milestone));
    if (!shown.length && !deferred.length) {
      const hiddenDone = !t.statuses.has("done") && counts.done > 0;
      return fill(list, h("p", { class: "empty" },
        all.length && counts.done === all.length ? `No open tasks: all ${all.length} are done. ` : "No tasks match. ",
        hiddenDone && h("button", { onclick: () => { t.statuses.add("done"); renderMain(); } }, "Show done tasks")));
    }
    const inM = (m) => (x) => (x.blueprint || "—") === m;
    // Blueprints with something to work on first; those holding only
    // deferred tasks after them.
    const order = [...milestones.filter((m) => shown.some(inM(m))), ...milestones.filter((m) => !shown.some(inM(m)) && deferred.some(inM(m)))];
    fill(list, ...order.map((m) => {
      const mine = all.filter(inM(m));
      const rows = shown.filter(inM(m));
      const reasons = Map.groupBy ? Map.groupBy(deferred.filter(inM(m)), (x) => x.blocked_reason || "")
        : deferred.filter(inM(m)).reduce((acc, x) => acc.set(x.blocked_reason || "", [...(acc.get(x.blocked_reason || "") || []), x]), new Map());
      const done = mine.filter((x) => x.status === "done").length;
      const open = !t.collapsed.has(m);
      const mi = info[m];
      return dropZone(h("section", { class: "mstone" },
        h("button", {
          class: "mhead", "aria-expanded": String(open),
          onclick: () => { open ? t.collapsed.add(m) : t.collapsed.delete(m); drawTasks(); },
        },
          chevron(open),
          h("span", { class: "mname" }, m, mi && mi.title && h("span", { class: "mtitle" }, mi.title)),
          h("span", { class: "mcount" }, `${rows.length} shown` + (reasons.size ? `, ${[...reasons.values()].reduce((n, xs) => n + xs.length, 0)} deferred` : "")),
          h("span", { class: "mprog" }, meter(done, mine.length, `${done} of ${mine.length} tasks done`))),
        open && mi && mi.goal && h("p", { class: "mgoal" }, mi.goal),
        open && rows.length > 0 && h("div", { class: "tlist" }, rows.map((x) => draggableTask(taskRow(x, drawTasks), x))),
        open && h("button", { class: "taddrow", type: "button", onclick: () => newTaskDialog(p, mres.blueprints || [], m === "—" ? "" : m) }, icon("plus"), "Add task"),
        open && [...reasons].map(([reason, xs]) => h("div", { class: "deferredline" },
          statusPill("deferred"), h("span", { class: "dcount" }, plural(xs.length, "task")),
          h("span", { class: "dreason" }, reason || "no reason given"),
          h("button", { class: "linkbtn", onclick: () => { t.statuses.add("deferred"); renderMain(); } }, "show")))), m);
    }),
    // while a task is dragged: the blueprints with nothing shown, as targets
    h("div", { class: "dropstrip" }, h("span", { class: "muted small" }, "Move to:"),
      [...milestones.filter((m) => !order.includes(m)), ...(milestones.includes("—") ? [] : ["—"])].map((m) => dropZone(h("span", { class: "dropchip" }, m === "—" ? "no blueprint" : info[m] && info[m].title ? `${m} · ${info[m].title}` : m), m))));
  }
  // dragging a task onto a blueprint moves it there
  function draggableTask(row, x) {
    row.draggable = true;
    row.classList.add("draggable");
    row.addEventListener("dragstart", (ev) => { ev.dataTransfer.setData("text/x-task", x.id); ev.dataTransfer.effectAllowed = "move"; list.classList.add("dragging"); });
    row.addEventListener("dragend", () => list.classList.remove("dragging"));
    return row;
  }
  function dropZone(el, m) {
    // types is an array in some browsers, a DOMStringList in others
    el.addEventListener("dragover", (ev) => { if (Array.from(ev.dataTransfer.types || []).includes("text/x-task")) { ev.preventDefault(); ev.dataTransfer.dropEffect = "move"; el.classList.add("dropover"); } });
    el.addEventListener("dragleave", () => el.classList.remove("dropover"));
    el.addEventListener("drop", async (ev) => {
      ev.preventDefault();
      el.classList.remove("dropover");
      list.classList.remove("dragging");
      const id = ev.dataTransfer.getData("text/x-task");
      const task = all.find((x) => x.id === id);
      const to = m === "—" ? "" : m;
      if (!task || (task.blueprint || "") === to) return;
      try { await post(projectAPI(p, `tasks/${enc(id)}/blueprint`), { blueprint: to }); toast(`${id} moved to ${to || "no blueprint"}`); await afterWrite(); }
      catch (e) { toast(e.message, true); }
    });
    return el;
  }

  function drawMilestones() {
    // tasks without a blueprint (and blueprints the backlog does not
    // describe) are counted from the tasks themselves
    const ms = milestones.map((id) => {
      if (info[id]) return info[id];
      const mine = all.filter((x) => (x.blueprint || "—") === id);
      const tasks = {};
      for (const x of mine) tasks[x.status] = (tasks[x.status] || 0) + 1;
      return { id, title: "", goal: "", exit: [], tasks, total: mine.length };
    });
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
    const kind = (m) => ({ done: "done", deferred: "deferred" })[stateOf(m)[0]] || "open";
    // open blueprints by default; all of them when none is open
    const filt = t.mfilter || (ms.some((m) => kind(m) === "open") ? "open" : "all");
    const shown = ms.filter((m) => filt === "all" || kind(m) === filt);
    const chips = [["open", "Open"], ["done", "Done"], ["deferred", "Deferred"], ["all", "All"]].map(([k, label]) => h("button", {
      class: "chip", "aria-pressed": String(filt === k), onclick: () => { t.mfilter = k; drawMilestones(); },
    }, label, h("span", { class: "count" }, k === "all" ? ms.length : ms.filter((m) => kind(m) === k).length)));
    fill(body, h("div", { class: "filters" }, chips, h("button", { class: "primary newtask", onclick: () => newBlueprintDialog(p) }, "New blueprint…")),
      shown.length ? h("div", { class: "mcards" }, shown.map((m) => {
        const [cls, ico, label] = stateOf(m);
        const n = (s) => (m.tasks || {})[s] || 0;
        const others = ms.filter((o) => o.id !== m.id && o.id !== "—");
        return h("article", { class: `mcard ms-${cls}`, "data-blueprint": m.id },
          h("div", { class: "mctop" },
            m.id !== "—" && h("span", { class: "mid" }, m.id),
            h("h3", {}, m.title || h("span", { class: "muted" }, m.id === "—" ? "Without a blueprint" : "Not described in the backlog")),
            h("span", { class: `pill ms-${cls}` }, h("span", { class: "ico", "aria-hidden": "true" }, ico), label),
          m.id !== "—" && h("button", { class: "iconbtn mcedit", type: "button", title: m.title ? `Edit blueprint ${m.id}` : `Describe blueprint ${m.id}`, "aria-label": `Edit blueprint ${m.id}`,
            onclick: () => blueprintDialog(p, m) }, icon("edit")),
          m.id !== "—" && h("button", { class: "iconbtn mcdel", type: "button", title: `Remove blueprint ${m.id}`, "aria-label": `Remove blueprint ${m.id}`,
            onclick: () => removeBlueprintDialog(p, m, others) }, icon("trash"))),
          m.goal && h("p", { class: "mcgoal" }, m.goal),
          m.total > 0 && h("div", { class: "mcprog" }, meter(n("done"), m.total, `${n("done")} of ${m.total} tasks done`),
            h("span", { class: "counts" }, ["ready", "in_progress", "review", "blocked", "backlog", "deferred"].filter((s) => n(s)).map((s) => [statusPill(s), " ", n(s), " "]))),
          (m.exit || []).length > 0 && h("div", { class: "mcexit" }, h("h4", {}, "Done when"),
            h("ul", {}, m.exit.map((e) => h("li", {}, e)))),
          m.notes && h("p", { class: "mcnote" }, m.notes),
          m.total > 0 && h("button", {
            class: "mclink", onclick: () => {
              t.milestone = m.id; t.statuses = new Set(TASK_STATUSES); t.collapsed.delete(m.id); location.hash = projectHref(p.id, "work", "tasks");
            },
          }, `Show its ${plural(m.total, "task")} →`));
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
  const msSel = h("select", { "aria-label": "Blueprint", onchange: (ev) => { t.milestone = ev.target.value; drawTasks(); } },
    h("option", { value: "" }, "all blueprints"),
    milestones.map((m) => h("option", { value: m, selected: m === t.milestone }, info[m] && info[m].title ? `${m} · ${info[m].title}` : m)));
  if (sec === "tasks") {
    const panel = h("div", { class: "mpanel" });
    const add = h("button", { class: "primary newtask", onclick: () => newTaskDialog(p, mres.blueprints || []) }, "New task…");
    fill(body, h("div", { class: "filters" }, chips, msSel, add), panel,
      h("p", { class: "muted small draghint" }, "Drag a task onto another blueprint to move it; 'harness tasks export' writes the moves into the backlog file."), list);
    drawTasks();
  } else {
    drawMilestones();
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
          h("div", { class: "label" }, "Blocked or stale"),
          h("div", { class: "value" }, h("span", { class: "ico", "aria-hidden": "true" }, stuck.worst === "problem" ? "✕" : "▲"), stuck.count),
          h("div", { class: "note" }, `task${stuck.count === 1 ? "" : "s"} stuck`))
        : h("div", { class: "card pad attncard lv-ok" },
          h("div", { class: "label" }, "Blocked or stale"),
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
      if (failed) items.push(["warn", x, `done with ${failed} failed ${failed === 1 ? "criterion or check" : "criteria or checks"}`]);
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
    h("span", { class: `prio p-${x.priority}`, title: `${x.priority} priority` }, icon("p-" + x.priority), x.priority),
    statusPill(x.status),
    h("button", { class: "iconbtn tedit", type: "button", disabled: !taskEditable(x), "aria-label": `Edit ${x.id}`,
      title: taskEditable(x) ? `Edit ${x.id}` : "Only a ready or backlog task can be edited: once claimed, its definition is fixed",
      onclick: (ev) => { ev.stopPropagation(); editTaskDialog({ id: route().id }, x); }, onkeydown: (ev) => ev.stopPropagation() }, icon("edit")),
    // delete at the row's end, shown on hover (its click is not the row's)
    h("button", { class: "iconbtn rowdel", type: "button", title: `Delete ${x.id}`, "aria-label": `Delete ${x.id}`,
      onclick: (ev) => { ev.stopPropagation(); deleteTaskDialog(x); }, onkeydown: (ev) => ev.stopPropagation() }, icon("trash")));
  return h("div", { class: "task" + (open ? " open" : ""), id: `task-${x.id}` }, row, open && taskDetail(x, passed));
}

// deleteTask asks, then deletes a task (harness ui only).
function deleteTaskDialog(x) {
  confirmDelete({
    title: `Delete ${x.id}?`, what: x.title,
    lines: ["The task goes with its acceptance criteria, results and audit trail; this cannot be undone.",
      "Refused while it is claimed or while other tasks depend on it.",
      "A backlog file that still has it brings it back on the next import: 'harness tasks export' writes the deletion into the file."],
    confirm: "Delete task", send: () => post(projectAPI({ id: route().id }, `tasks/${encodeURIComponent(x.id)}/delete`), {}), done: `${x.id} deleted`,
  });
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

// confirmDelete asks before deleting, saying what goes, then does it.
function confirmDelete({ title, what, lines, confirm, send, done }) {
  const dlg = dialog({ title });
  const err = h("p", { class: "error", hidden: true });
  const ok = h("button", { class: "danger", type: "button", onclick: async () => {
    ok.disabled = true;
    try { await send(); dlg.close(); toast(done); await afterWrite(); }
    catch (e) { err.textContent = e.message; err.hidden = false; ok.disabled = false; }
  } }, confirm);
  dlg.set(null, [what && h("p", { class: "dpname" }, what), h("ul", { class: "dchanges" }, lines.map((l) => h("li", {}, l))), err],
    [h("button", { class: "ghost", type: "button", onclick: () => dlg.close() }, "Cancel"), ok]);
}

function taskDetail(x, passed) {
  const acc = x.acceptance || [];
  const resIcon = { pass: "✓", fail: "✕", pending: "○", "n/a": "–" };
  return h("div", { class: "tdetail" },
    h("div", { class: "tmain" },
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
  const p = live.pending || (live.pending = { registry: false, workspaces: false, all: false, kinds: {}, levels: [] });
  p.registry = p.registry || !!c.registry;
  p.workspaces = p.workspaces || !!c.workspaces;
  p.levels = [...new Set([...p.levels, ...(c.levels || [])])];
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
