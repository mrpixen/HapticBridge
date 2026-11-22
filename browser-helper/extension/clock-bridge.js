// Independently written compatibility adapter. This function is self-contained
// because scripting.executeScript serializes it into the page's MAIN world.
// No page values, URLs, clocks or identifiers are returned to the extension.
export function manageClockBridge(command = "status") {
  const key = Symbol.for("hapticbridge.clock-helper.v1");
  const ownState = Object.getOwnPropertyDescriptor(window, key);
  const current = ownState?.value;
  const result = (status, enabled = false, compatible = false, restored = true) =>
    ({ status, enabled, compatible, restored });
  if (!["status", "enable", "disable"].includes(command)) return result("unsupported");
  if (ownState && (current?.version !== 1 || typeof current.inspect !== "function" || typeof current.stop !== "function"))
    return result("conflict");
  if (current) {
    if (command === "disable") return current.stop("disabled");
    const state = current.inspect();
    if (state.enabled || command === "status") return state;
  }
  if (command === "disable") return result("disabled");

  function locate() {
    const id = window.VIDEO_SHOW?.videoId;
    if (typeof id !== "string" || !id || id.length > 256) return null;
    const container = document.getElementById(id);
    if (!container?.isConnected || !container.matches(".playerFlvContainer.mgp_container")) return null;
    const old = container.querySelectorAll(":scope > video.player-html5");
    const wrappers = container.querySelectorAll(":scope > .mgp_videoWrapper");
    if (old.length !== 1 || wrappers.length !== 1) return null;
    const videos = wrappers[0].querySelectorAll(":scope > video.mgp_videoElement");
    if (videos.length !== 1 || !(old[0] instanceof HTMLVideoElement) || !(videos[0] instanceof HTMLVideoElement)) return null;
    if (old[0] === videos[0] || container.querySelector("video") !== old[0]) return null;
    return { id, container, legacy: old[0], active: videos[0] };
  }
  const found = locate();
  if (!found) return result("unsupported");
  const original = Object.getOwnPropertyDescriptor(found.legacy, "currentTime");
  // A custom data property or locked accessor has unknown ownership: leave it alone.
  if (original && (!original.configurable || typeof original.get !== "function")) return result("custom-clock");
  const mediaClock = Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, "currentTime");
  if (typeof mediaClock?.get !== "function" || typeof mediaClock?.set !== "function") return result("unsupported");
  if (!Object.isExtensible(found.legacy) && !original) return result("custom-clock");
  if (command === "status") return result("available", false, true);

  const originalGet = original?.get ?? mediaClock.get;
  const originalSet = original ? original.set : mediaClock.set;
  const pageAtEnable = location.href; // RAM-only navigation guard; never returned or logged.
  let enabled = true, observer = null, timer = null, controller;
  function topology() {
    if (location.href !== pageAtEnable) return null;
    const next = locate();
    return next && next.id === found.id && next.container === found.container && next.legacy === found.legacy && next.active === found.active ? next : null;
  }
  function proxyGet() {
    if (!enabled || this !== found.legacy) return originalGet.call(this);
    const next = topology();
    if (!next) { stop("page-changed"); return originalGet.call(this); }
    // Read the real media clock, not a page-defined property on the active node.
    const time = mediaClock.get.call(next.active);
    return Number.isFinite(time) && time >= 0 ? time : originalGet.call(this);
  }
  // Writes retain the original legacy element's exact setter semantics. The
  // adapter never seeks, plays, pauses, rearranges or removes either video.
  function proxySet(value) { return originalSet.call(this, value); }
  const replacement = {
    configurable: true,
    enumerable: original?.enumerable ?? mediaClock.enumerable,
    get: proxyGet,
    set: originalSet ? proxySet : undefined
  };
  function stillOwns() {
    const descriptor = Object.getOwnPropertyDescriptor(found.legacy, "currentTime");
    return descriptor?.get === proxyGet && descriptor?.set === replacement.set;
  }
  function stop(reason = "disabled") {
    enabled = false;
    observer?.disconnect();
    if (timer !== null) clearInterval(timer);
    window.removeEventListener("pagehide", onPageHide);
    window.removeEventListener("popstate", onNavigation);
    window.removeEventListener("hashchange", onNavigation);
    let restored = true;
    // Never overwrite a property another page script replaced after activation.
    if (stillOwns()) {
      try {
        if (original) Object.defineProperty(found.legacy, "currentTime", original);
        else if (!Reflect.deleteProperty(found.legacy, "currentTime")) restored = false;
      } catch { restored = false; }
    }
    if (Object.getOwnPropertyDescriptor(window, key)?.value === controller) {
      try { Reflect.deleteProperty(window, key); } catch { /* Page can interfere with MAIN state. */ }
    }
    return result(reason, false, reason === "disabled" && restored && Boolean(locate()), restored);
  }
  function inspect() {
    if (!enabled) return result("disabled");
    if (!stillOwns()) return stop("superseded");
    if (!topology()) return stop("page-changed");
    return result("enabled", true, true);
  }
  function onPageHide() { stop("page-changed"); }
  function onNavigation() { inspect(); }
  controller = { version: 1, inspect, stop };
  try {
    Object.defineProperty(found.legacy, "currentTime", replacement);
    Object.defineProperty(window, key, { configurable: true, value: controller });
    observer = new MutationObserver(inspect);
    observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ["id", "class"] });
    window.addEventListener("pagehide", onPageHide);
    window.addEventListener("popstate", onNavigation);
    window.addEventListener("hashchange", onNavigation);
    // pushState has no universal navigation event. Guard reads immediately and
    // also tear down dormant adapters without wrapping history/prototype APIs.
    timer = setInterval(inspect, 500);
    return inspect();
  } catch {
    stop("unavailable");
    return result("unavailable");
  }
}
