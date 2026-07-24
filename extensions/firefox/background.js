"use strict";

const HOST_NAME = "com.monitoraudiorouter.router";
const SEND_INTERVAL_MS = 1500;

let port = null;

function connect() {
  try {
    port = browser.runtime.connectNative(HOST_NAME);
    port.onDisconnect.addListener(() => {
      port = null;
      setTimeout(connect, 2500);
    });
  } catch {
    port = null;
    setTimeout(connect, 2500);
  }
}

function post(message) {
  if (!port) {
    connect();
  }

  if (!port) {
    return;
  }

  try {
    port.postMessage(message);
  } catch {
    port = null;
    setTimeout(connect, 2500);
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

function start() {
  connect();
  sendSnapshot();
  setInterval(sendSnapshot, SEND_INTERVAL_MS);

  browser.tabs.onUpdated.addListener(sendSnapshot);
  browser.tabs.onActivated.addListener(sendSnapshot);
  browser.tabs.onRemoved.addListener(sendSnapshot);
  browser.windows.onRemoved.addListener(sendSnapshot);

  browser.browserAction.onClicked.addListener(sendSnapshot);
}

start();
