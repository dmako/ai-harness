import { dotnet } from "./_framework/dotnet.js";

// first-load timing for measurements: from navigation start to the first Board (window.harnessTimings)
globalThis.harnessTimings = { scriptStart: performance.now() };

// what Program.cs imports through JSImport: the page's address and its history (the hash is the shell's route)
globalThis.harnessLocation = {
  href: () => globalThis.location.href,
  hash: () => globalThis.location.hash,
  push: (hash) => { if (globalThis.location.hash !== hash) history.pushState(null, "", hash); },
  replace: (hash) => { if (globalThis.location.hash !== hash) history.replaceState(null, "", hash); },
  back: () => history.back(),
  forward: () => history.forward(),
  mark: (name) => {
    globalThis.harnessTimings[name] ??= performance.now();
    // the placeholder stays in the page under the canvas: a screen reader would still read it
    if (name === "board") document.querySelector(".loading")?.remove();
  },
  shown: (href) => { globalThis.harnessTimings.route = href; },
  count: (name) => { globalThis.harnessTimings[name] = (globalThis.harnessTimings[name] || 0) + 1; },
};

const runtime = await dotnet.withDiagnosticTracing(false).create();
globalThis.harnessTimings.runtimeReady = performance.now();

// Back, Forward and an edited address come back to the shell as hash changes
const exports = await runtime.getAssemblyExports("Harness.Ui.Browser.dll");
const changed = () => exports.Harness.Ui.Browser.BrowserNavigation.HashChanged(globalThis.location.hash);
globalThis.addEventListener("hashchange", changed);
globalThis.addEventListener("popstate", changed);

await runtime.runMain();
