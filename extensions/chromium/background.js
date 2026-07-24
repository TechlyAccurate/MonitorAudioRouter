"use strict";

const HOST_NAME = "com.monitoraudiorouter.router";
const SEND_INTERVAL_MS = 1500;

let port = null;
let sendTimer = null;
let hasTriedProcessPermission = false;

function browserName() {
  const ua = navigator.userAgent;
  if (ua.includes("Edg/")) {
    return "edge";
  }

  if (ua.includes("Firefox/")) {
    return "firefox";
  }

  return "chrome";
}

function chromeCall(fn, ...args) {
  return new Promise((resolve, reject) => {
    let settled = false;
    const finish = (value) => {
      if (!settled) {
        settled = true;
        resolve(value);
      }
    };
    const fail = (error) => {
      if (!settled) {
        settled = true;
        reject(error);
      }
    };

    try {
      const maybePromise = fn(...args, (result) => {
        const error = chrome.runtime.lastError;
        if (error) {
          fail(new Error(error.message));
          return;
        }

        finish(result);
      });

      if (maybePromise && typeof maybePromise.then === "function") {
        maybePromise.then(finish, fail);
      }
    } catch (error) {
      fail(error);
    }
  });
}

function connect() {
  try {
    port = chrome.runtime.connectNative(HOST_NAME);
    port.onDisconnect.addListener(() => {
      port = null;
      scheduleReconnect();
    });
  } catch {
    port = null;
    scheduleReconnect();
  }
}

function scheduleReconnect() {
  setTimeout(() => {
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

  try {
    port.postMessage(message);
  } catch {
    port = null;
    scheduleReconnect();
  }
}

async function processIdsForTabs(tabs) {
  const api = chrome.processes;
  if (!api || typeof api.getProcessIdForTab !== "function" || typeof api.getProcessInfo !== "function") {
    return [];
  }

  const browserProcessIds = [];
  for (const tab of tabs) {
    try {
      const processId = await chromeCall(api.getProcessIdForTab, tab.id);
      if (Number.isInteger(processId) && !browserProcessIds.includes(processId)) {
        browserProcessIds.push(processId);
      }
    } catch {
      // The API is optional and may not be available on stable Chromium builds.
    }
  }

  if (browserProcessIds.length === 0) {
    return [];
  }

  try {
    const infos = await chromeCall(api.getProcessInfo, browserProcessIds, false);
    return Object.values(infos || {})
      .map((info) => info && info.osProcessId)
      .filter((pid, index, values) => Number.isInteger(pid) && pid > 0 && values.indexOf(pid) === index);
  } catch {
    return [];
  }
}

async function collectAudibleWindows() {
  const tabs = await chromeCall(chrome.tabs.query, {});
  const audibleTabs = tabs.filter((tab) => tab.audible && tab.windowId !== undefined);
  const grouped = new Map();

  for (const tab of audibleTabs) {
    const current = grouped.get(tab.windowId) || [];
    current.push(tab);
    grouped.set(tab.windowId, current);
  }

  const windows = [];
  for (const [windowId, windowTabs] of grouped) {
    try {
      const win = await chromeCall(chrome.windows.get, windowId);
      if (!Number.isFinite(win.left) || !Number.isFinite(win.top) ||
          !Number.isFinite(win.width) || !Number.isFinite(win.height)) {
        continue;
      }

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
        processIds: await processIdsForTabs(windowTabs),
        titles: [...new Set(windowTabs
          .map((tab) => tab.title)
          .filter((title) => typeof title === "string" && title.trim().length > 0)
          .map((title) => title.trim()))],
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
      browser: browserName(),
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
  sendTimer = setInterval(sendSnapshot, SEND_INTERVAL_MS);

  chrome.tabs.onUpdated.addListener(sendSnapshot);
  chrome.tabs.onActivated.addListener(sendSnapshot);
  chrome.tabs.onAttached.addListener(sendSnapshot);
  chrome.tabs.onDetached.addListener(sendSnapshot);
  chrome.tabs.onRemoved.addListener(sendSnapshot);
  chrome.windows.onBoundsChanged.addListener(sendSnapshot);
  chrome.windows.onRemoved.addListener(sendSnapshot);

  chrome.action.onClicked.addListener(() => {
    if (hasTriedProcessPermission || !chrome.permissions) {
      sendSnapshot();
      return;
    }

    hasTriedProcessPermission = true;
    chrome.permissions.request({ permissions: ["processes"] }, () => {
      sendSnapshot();
    });
  });
}

start();
