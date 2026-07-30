"use strict";

// Firefox bridge:
// - find audible tabs grouped by browser window;
// - send only the local window hints needed for routing;
// - let the Windows tray app decide and apply audio routes.
const HOST_NAME = "com.monitoraudiorouter.router";
const SEND_INTERVAL_MS = 1500;

let port = null;
let reconnectTimer = null;

// Native messaging connection

function connect() {
  if (port) {
    return;
  }

  try {
    const nextPort = browser.runtime.connectNative(HOST_NAME);
    port = nextPort;
    nextPort.onDisconnect.addListener(() => {
      if (port === nextPort) {
        port = null;
      }

      scheduleReconnect();
    });
  } catch {
    port = null;
    scheduleReconnect();
  }
}

function scheduleReconnect() {
  if (reconnectTimer !== null) {
    return;
  }

  // Keep one reconnect timer alive at most. Older versions could create a
  // reconnect storm when Firefox held stale extension workers.
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    if (!port) {
      connect();
    }
  }, 2500);
}

function post(message) {
  if (!port) {
    connect();
  }

  if (!port) {
    return;
  }

  const currentPort = port;
  try {
    currentPort.postMessage(message);
  } catch {
    if (port === currentPort) {
      port = null;
    }

    scheduleReconnect();
  }
}

// Audible window collection

async function collectAudibleWindows() {
  const tabs = await browser.tabs.query({});
  const grouped = new Map();

  // Firefox does not expose a useful OS audio-session PID to this extension.
  // The tray app infers the PID only when Windows reports one matching active
  // Firefox audio session.
  for (const tab of tabs.filter((t) => t.audible && t.windowId !== undefined)) {
    const current = grouped.get(tab.windowId) || [];
    current.push(tab);
    grouped.set(tab.windowId, current);
  }

  const windows = [];
  for (const [windowId, windowTabs] of grouped) {
    try {
      const win = await browser.windows.get(windowId);
      if (!Number.isFinite(win.left) || !Number.isFinite(win.top) ||
          !Number.isFinite(win.width) || !Number.isFinite(win.height)) {
        continue;
      }

      // Titles are local matching hints. The extension does not send URLs,
      // page contents, history, cookies, or anything to a remote service.
      const titles = [...new Set(windowTabs
        .map((tab) => tab.title)
        .filter((title) => typeof title === "string" && title.trim().length > 0)
        .map((title) => title.trim()))];
      const windowTitles = [...new Set(tabs
        .filter((tab) => tab.windowId === windowId && tab.active)
        .map((tab) => tab.title)
        .filter((title) => typeof title === "string" && title.trim().length > 0)
        .map((title) => title.trim()))];

      windows.push({
        windowId,
        left: win.left,
        top: win.top,
        width: win.width,
        height: win.height,
        processIds: [],
        titles,
        windowTitles
      });
    } catch {
      // Window may have closed during collection.
    }
  }

  return windows;
}

// Snapshot scheduling

async function sendSnapshot() {
  try {
    post({
      type: "audibleWindows",
      browser: "firefox",
      sentAt: Date.now(),
      windows: await collectAudibleWindows()
    });
  } catch {
    // The next scheduled pass will retry.
  }
}

function sendSnapshotBurst() {
  // A tab move or pause/play handoff can produce short-lived stale browser
  // state, so send a small burst instead of waiting for the passive interval.
  sendSnapshot();
  setTimeout(sendSnapshot, 150);
  setTimeout(sendSnapshot, 750);
}

// Extension entry point

function start() {
  connect();
  sendSnapshot();
  setInterval(sendSnapshot, SEND_INTERVAL_MS);

  browser.tabs.onUpdated.addListener(sendSnapshotBurst);
  browser.tabs.onActivated.addListener(sendSnapshotBurst);
  browser.tabs.onAttached.addListener(sendSnapshotBurst);
  browser.tabs.onDetached.addListener(sendSnapshotBurst);
  browser.tabs.onRemoved.addListener(sendSnapshotBurst);
  browser.windows.onRemoved.addListener(sendSnapshotBurst);

  browser.browserAction.onClicked.addListener(sendSnapshotBurst);
}

start();
