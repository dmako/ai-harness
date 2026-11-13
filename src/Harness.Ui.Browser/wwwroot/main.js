import { dotnet } from "./_framework/dotnet.js";

// what Program.cs imports through JSImport: the page's address and its hash (the deep link)
globalThis.harnessLocation = {
  href: () => globalThis.location.href,
  setHash: (hash) => { if (globalThis.location.hash !== hash) history.replaceState(null, "", hash); },
  mark: (name) => {
    globalThis.harnessTimings[name] ??= performance.now();
    // the placeholder stays in the page under the canvas: a screen reader would still read it
    if (name === "board") document.querySelector(".loading")?.remove();
  },
  count: (name) => { globalThis.harnessTimings[name] = (globalThis.harnessTimings[name] || 0) + 1; },
};

// first-load timing for the spike's measurements: from navigation start to the first Board (window.harnessTimings)
globalThis.harnessTimings = { scriptStart: performance.now() };

const runtime = await dotnet.withDiagnosticTracing(false).create();
globalThis.harnessTimings.runtimeReady = performance.now();
await runtime.runMain();
