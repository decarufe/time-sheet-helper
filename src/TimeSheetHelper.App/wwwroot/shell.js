// Navigation shell interop (nav-c contracts §1.2): shortcuts, theme and panel preferences. No dependency.
const THEME_KEY = "tsh.theme";
const PANEL_KEY = "tsh.panel";

let listener = null;

function read(key) {
  try { return localStorage.getItem(key); } catch { return null; }
}

function write(key, value) {
  try { localStorage.setItem(key, value); } catch { /* storage unavailable: preference kept for the session only */ }
}

function isEditable(target) {
  if (!(target instanceof Element)) return false;
  if (target.isContentEditable) return true;
  const tag = target.tagName;
  return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT";
}

// AltGr (reported as Ctrl+Alt on Windows) types characters such as "[" and "]" on AZERTY/CSA layouts.
function isAltGraph(e) {
  return (e.ctrlKey && e.altKey && !e.metaKey)
    || (typeof e.getModifierState === "function" && e.getModifierState("AltGraph"));
}

// Alt+digit gives a symbol on some layouts (AZERTY, macOS Option): fall back to the physical key.
function normalizedKey(e) {
  if (isAltGraph(e)) return e.key;
  if (e.altKey) {
    const digit = /^(?:Digit|Numpad)(\d)$/.exec(e.code || "");
    if (digit) return digit[1];
    if (e.code === "KeyT") return "t";
  }
  if ((e.ctrlKey || e.metaKey) && e.code === "KeyB") return "b";
  return e.key;
}

function isShortcutCandidate(e, key) {
  if (isAltGraph(e)) return key === "[" || key === "]";
  if (e.altKey) return /^[1-7]$/.test(key) || key.toLowerCase() === "t";
  if (e.ctrlKey || e.metaKey) return key.toLowerCase() === "b";
  return key === "[" || key === "]";
}

export function init(dotNetRef) {
  dispose();
  listener = (e) => {
    if (e.defaultPrevented || e.isComposing) return;
    const key = normalizedKey(e);
    if (!key || !isShortcutCandidate(e, key)) return;
    const chord = {
      key,
      alt: e.altKey,
      ctrl: e.ctrlKey,
      meta: e.metaKey,
      shift: e.shiftKey,
      inEditableField: isEditable(e.target),
    };
    // The same guards as ShortcutMap.Resolve: when .NET would ignore the key, skip the round trip and keep the browser behaviour.
    const handled = isAltGraph(e) || !e.altKey ? !chord.inEditableField : !(e.ctrlKey || e.metaKey);
    if (!handled) return;
    e.preventDefault();
    dotNetRef.invokeMethodAsync("OnShortcut", chord).catch(() => { /* circuit gone */ });
  };
  document.addEventListener("keydown", listener);
}

export function getPrefs() {
  const stored = read(THEME_KEY);
  const theme = stored === "light" || stored === "dark"
    ? stored
    : (matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
  return { theme, panel: read(PANEL_KEY) !== "0" };
}

export function setTheme(theme) {
  if (theme !== "light" && theme !== "dark") return;
  document.documentElement.dataset.theme = theme;
  write(THEME_KEY, theme);
}

export function setPanel(open) {
  document.documentElement.dataset.panel = open ? "1" : "0";
  write(PANEL_KEY, open ? "1" : "0");
}

const FOCUSABLE = "a[href], button, input, select, textarea, summary, [tabindex]:not([tabindex='-1'])";

let trap = null;

function focusables(root) {
  return Array.from(root.querySelectorAll(FOCUSABLE))
    .filter((el) => !el.disabled && !el.closest("[inert]") && el.getClientRects().length > 0);
}

function releaseTrap() {
  if (trap) document.removeEventListener("keydown", trap, true);
  trap = null;
}

// Locks the page scroll and keeps Tab focus inside the panel while the mobile drawer is open.
export function setDrawerOpen(open) {
  document.documentElement.classList.toggle("drawer-open", !!open);
  releaseTrap();
  if (!open) return;
  trap = (e) => {
    if (e.key !== "Tab" || e.altKey || e.ctrlKey || e.metaKey) return;
    const panel = document.getElementById("panel");
    if (!panel) return;
    const items = focusables(panel);
    if (items.length === 0) return;
    const first = items[0];
    const last = items[items.length - 1];
    const active = document.activeElement;
    if (!panel.contains(active)) {
      e.preventDefault();
      (e.shiftKey ? last : first).focus();
    } else if (e.shiftKey && active === first) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && active === last) {
      e.preventDefault();
      first.focus();
    }
  };
  document.addEventListener("keydown", trap, true);
}

export function dispose() {
  if (listener) document.removeEventListener("keydown", listener);
  listener = null;
  releaseTrap();
}
