// first-load timing for measurements: from navigation start to the first Board (window.harnessTimings)
globalThis.harnessTimings = { scriptStart: performance.now() };

// The splash is separate from Avalonia's host so it cannot be hidden underneath the canvas.
const splash = document.getElementById("startup");
const host = document.getElementById("out");
let startupFailed = false;
const ready = () => {
  if (!host.querySelector("canvas")) return;
  observer.disconnect();
  requestAnimationFrame(() => requestAnimationFrame(() => { if (!startupFailed) splash?.remove(); }));
};
const observer = new MutationObserver(ready);
observer.observe(host, { childList: true, subtree: true });
ready();

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
  },
  shown: (href) => { globalThis.harnessTimings.route = href; },
  count: (name) => { globalThis.harnessTimings[name] = (globalThis.harnessTimings[name] || 0) + 1; },
};

// what BrowserPreferences imports: localStorage, which a private window may refuse
globalThis.harnessStore = {
  get: (key) => { try { return localStorage.getItem(key); } catch { return null; } },
  set: (key, value) => { try { localStorage.setItem(key, value); } catch { /* private window */ } },
};

try {
  const { dotnet } = await import("./_framework/dotnet.js");
  const runtime = await dotnet.withDiagnosticTracing(false).create();
  globalThis.harnessTimings.runtimeReady = performance.now();

  // Back, Forward and an edited address come back to the shell as hash changes
  const exports = await runtime.getAssemblyExports("Harness.Ui.Browser.dll");
  globalThis.addEventListener("keydown", (event) => {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "f") {
      event.preventDefault();
      exports.Harness.Ui.Browser.Program.OpenFind();
    }
  }, true);
  const changed = () => exports.Harness.Ui.Browser.BrowserNavigation.HashChanged(globalThis.location.hash);
  globalThis.addEventListener("hashchange", changed);
  globalThis.addEventListener("popstate", changed);

  await runtime.runMain();
} catch (error) {
  startupFailed = true;
  observer.disconnect();
  document.getElementById("startup-message").textContent = "Harness could not start. Reload the page to try again.";
  document.querySelector(".spinner")?.remove();
  console.error(error);
}
