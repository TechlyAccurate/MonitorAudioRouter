"use strict";

const HOST_NAME = "com.monitoraudiorouter.router";
const SEND_INTERVAL_MS = 1500;

let port = null;
let reconnectTimer = null;

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

async function collectAudibleWindows() {
  const tabs = await browser.tabs.query({});
  const grouped = new Map();

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
  sendSnapshot();
  setTimeout(sendSnapshot, 150);
  setTimeout(sendSnapshot, 750);
}

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
