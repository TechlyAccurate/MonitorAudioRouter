"use strict";

// Firefox bridge:
// - find audible tabs grouped by browser window;
// - send only the local window hints needed for routing;
// - let the Windows tray app decide and apply audio routes.
const NATIVE_HOST_NAME = "com.monitoraudiorouter.router";
const SNAPSHOT_INTERVAL_MS = 1500;

let nativeHostPort = null;
let nativeHostReconnectTimer = null;

// Native messaging connection

function connectNativeHost() {
  if (nativeHostPort) {
    return;
  }

  try {
    const nextPort = browser.runtime.connectNative(NATIVE_HOST_NAME);
    nativeHostPort = nextPort;
    nextPort.onDisconnect.addListener(() => {
      if (nativeHostPort === nextPort) {
        nativeHostPort = null;
      }

      scheduleReconnect();
    });
  } catch {
    nativeHostPort = null;
    scheduleReconnect();
  }
}

function scheduleReconnect() {
  if (nativeHostReconnectTimer !== null) {
    return;
  }

  // Keep one reconnect timer alive at most. Older versions could create a
  // reconnect storm when Firefox held stale extension workers.
  nativeHostReconnectTimer = setTimeout(() => {
    nativeHostReconnectTimer = null;
    if (!nativeHostPort) {
      connectNativeHost();
    }
  }, 2500);
}

function postToNativeHost(message) {
  if (!nativeHostPort) {
    connectNativeHost();
  }

  if (!nativeHostPort) {
    return;
  }

  const connectedNativeHostPort = nativeHostPort;
  try {
    connectedNativeHostPort.postMessage(message);
  } catch {
    if (nativeHostPort === connectedNativeHostPort) {
      nativeHostPort = null;
    }

    scheduleReconnect();
  }
}

// Audible window collection

async function collectAudibleWindows() {
  const tabs = await browser.tabs.query({});
  const tabsByWindowId = new Map();

  // Firefox does not expose a useful OS audio-session PID to this extension.
  // The tray app infers the PID only when Windows reports one matching active
  // Firefox audio session.
  for (const tab of tabs.filter((tab) => tab.audible && tab.windowId !== undefined)) {
    const tabsForWindow = tabsByWindowId.get(tab.windowId) || [];
    tabsForWindow.push(tab);
    tabsByWindowId.set(tab.windowId, tabsForWindow);
  }

  const windows = [];
  for (const [windowId, windowTabs] of tabsByWindowId) {
    try {
      const browserWindow = await browser.windows.get(windowId);
      if (!Number.isFinite(browserWindow.left) || !Number.isFinite(browserWindow.top) ||
          !Number.isFinite(browserWindow.width) || !Number.isFinite(browserWindow.height)) {
        continue;
      }

      // Titles are local matching hints. The extension does not send URLs,
      // page contents, history, cookies, or anything to a remote service.
      const audibleTabTitles = [...new Set(windowTabs
        .map((tab) => tab.title)
        .filter((title) => typeof title === "string" && title.trim().length > 0)
        .map((title) => title.trim()))];
      const activeTabTitles = [...new Set(tabs
        .filter((tab) => tab.windowId === windowId && tab.active)
        .map((tab) => tab.title)
        .filter((title) => typeof title === "string" && title.trim().length > 0)
        .map((title) => title.trim()))];

      windows.push({
        windowId,
        left: browserWindow.left,
        top: browserWindow.top,
        width: browserWindow.width,
        height: browserWindow.height,
        processIds: [],
        titles: audibleTabTitles,
        windowTitles: activeTabTitles
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
    postToNativeHost({
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
  connectNativeHost();
  sendSnapshot();
  setInterval(sendSnapshot, SNAPSHOT_INTERVAL_MS);

  browser.tabs.onUpdated.addListener(sendSnapshotBurst);
  browser.tabs.onActivated.addListener(sendSnapshotBurst);
  browser.tabs.onAttached.addListener(sendSnapshotBurst);
  browser.tabs.onDetached.addListener(sendSnapshotBurst);
  browser.tabs.onRemoved.addListener(sendSnapshotBurst);
  browser.windows.onRemoved.addListener(sendSnapshotBurst);

  browser.browserAction.onClicked.addListener(sendSnapshotBurst);
}

start();
