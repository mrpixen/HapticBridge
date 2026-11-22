import { manageClockBridge } from "./clock-bridge.js";

const api = globalThis.browser ?? globalThis.chrome;
const toggle = document.getElementById("toggle");
const retry = document.getElementById("retry");
const state = document.getElementById("state");
const label = document.getElementById("state-label");
const detail = document.getElementById("detail");
let tabId, enabled = false, busy = false;

function display(value) {
  enabled = value?.enabled === true;
  state.dataset.state = enabled ? "enabled" : "off";
  toggle.textContent = enabled ? "Disable for this tab" : "Enable for this tab";
  toggle.disabled = !enabled && value?.compatible !== true;
  retry.hidden = enabled || value?.compatible === true;
  label.textContent = enabled ? "Enabled" : value?.compatible ? "Ready" : "Off";
  detail.textContent = enabled ? "The stale clock now follows the active player." : value?.compatible ? "Use the active player’s clock on this tab." : "No supported duplicate-player layout found.";
  if (value?.status === "disabled") detail.textContent = "Original clock restored.";
  if (["custom-clock", "conflict", "superseded"].includes(value?.status)) detail.textContent = "Another script owns this clock. No change was made.";
  if (value?.status === "page-changed") detail.textContent = "The page changed. Check again before enabling.";
  if (value?.restored === false) detail.textContent = "Forwarding is off. Reload the tab to fully clear the modified property.";
}
async function run(command) {
  if (busy) return;
  busy = true; toggle.disabled = true; retry.disabled = true;
  try {
    if (tabId === undefined) {
      const [tab] = await api.tabs.query({ active: true, currentWindow: true });
      if (!Number.isInteger(tab?.id)) throw new Error("No active tab");
      tabId = tab.id;
    }
    const replies = await api.scripting.executeScript({
      target: { tabId, frameIds: [0] }, world: "MAIN",
      func: manageClockBridge, args: [command]
    });
    const reply = replies.find(item => item.frameId === 0);
    if (reply?.error || !reply?.result) throw new Error("No result");
    display(reply.result);
  } catch {
    // Do not expose arbitrary page exception strings, URLs or identifiers.
    enabled = false; state.dataset.state = "error";label.textContent = "Unavailable";
    detail.textContent = "Open a normal webpage and try again. Browser-protected pages cannot be changed.";
    toggle.textContent = "Enable for this tab";toggle.disabled = true;retry.hidden = false;
  } finally { busy = false;retry.disabled = false; }
}
toggle.addEventListener("click", () => run(enabled ? "disable" : "enable"));
retry.addEventListener("click", () => run("status"));
run("status");
