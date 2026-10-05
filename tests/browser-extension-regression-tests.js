"use strict";

const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const repositoryRoot = path.resolve(__dirname, "..");
const tests = [];

function addTest(name, check) {
  tests.push({ name, check });
}

function assertEqual(expected, actual, message) {
  if (expected !== actual) {
    throw new Error(`${message} Expected: ${expected}; actual: ${actual}.`);
  }
}

function assertTrue(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

function loadManifest(browserDirectory) {
  const manifestPath = path.join(repositoryRoot, "extensions", browserDirectory, "manifest.json");
  return JSON.parse(fs.readFileSync(manifestPath, "utf8"));
}

function createEventStub() {
  return {
    addListener(listener) {
      assertEqual("function", typeof listener, "Event listeners must be functions.");
    }
  };
}

function createNativePort(postedMessages) {
  return {
    onDisconnect: createEventStub(),
    postMessage(message) {
      postedMessages.push(message);
    }
  };
}

function createTimerStubs() {
  let nextTimerId = 1;
  return {
    setInterval(callback) {
      assertEqual("function", typeof callback, "Interval callbacks must be functions.");
      return nextTimerId++;
    },
    setTimeout(callback) {
      assertEqual("function", typeof callback, "Timeout callbacks must be functions.");
      return nextTimerId++;
    }
  };
}

function createChromiumApi(postedMessages) {
  const event = createEventStub();
  return {
    action: { onClicked: event },
    permissions: {
      request(_permissions, callback) {
        callback(true);
      }
    },
    runtime: {
      connectNative() {
        return createNativePort(postedMessages);
      },
      lastError: null
    },
    tabs: {
      onActivated: event,
      onAttached: event,
      onDetached: event,
      onRemoved: event,
      onUpdated: event,
      query() {
        return Promise.resolve([]);
      }
    },
    windows: {
      get() {
        return Promise.reject(new Error("No windows are present in the smoke fixture."));
      },
      onBoundsChanged: event,
      onRemoved: event
    }
  };
}

function createFirefoxApi(postedMessages) {
  const event = createEventStub();
  return {
    browserAction: { onClicked: event },
    runtime: {
      connectNative() {
        return createNativePort(postedMessages);
      }
    },
    tabs: {
      onActivated: event,
      onAttached: event,
      onDetached: event,
      onRemoved: event,
      onUpdated: event,
      query() {
        return Promise.resolve([]);
      }
    },
    windows: {
      get() {
        return Promise.reject(new Error("No windows are present in the smoke fixture."));
      },
      onRemoved: event
    }
  };
}

async function evaluateBackgroundScript(browserDirectory, browserApiName, browserApi) {
  const backgroundPath = path.join(repositoryRoot, "extensions", browserDirectory, "background.js");
  const timers = createTimerStubs();
  const context = {
    [browserApiName]: browserApi,
    console,
    navigator: { userAgent: "Mozilla/5.0 Chrome/140.0" },
    setInterval: timers.setInterval,
    setTimeout: timers.setTimeout
  };

  vm.createContext(context);
  vm.runInContext(fs.readFileSync(backgroundPath, "utf8"), context, { filename: backgroundPath });
  await new Promise((resolve) => setImmediate(resolve));
}

addTest("Chromium manifest parses as JSON", () => {
  const manifest = loadManifest("chromium");
  assertEqual(3, manifest.manifest_version, "The Chromium manifest version should remain valid.");
  assertEqual("background.js", manifest.background.service_worker, "The Chromium background entry point should be declared.");
});

addTest("Firefox manifest parses as JSON", () => {
  const manifest = loadManifest("firefox");
  assertEqual(2, manifest.manifest_version, "The Firefox manifest version should remain valid.");
  assertTrue(manifest.background.scripts.includes("background.js"), "The Firefox background entry point should be declared.");
});

addTest("Chromium background evaluates with stub browser APIs", async () => {
  const postedMessages = [];
  await evaluateBackgroundScript("chromium", "chrome", createChromiumApi(postedMessages));
  assertEqual(1, postedMessages.length, "Chromium startup should send one initial snapshot.");
  assertEqual("audibleWindows", postedMessages[0].type, "Chromium should send the expected message type.");
});

addTest("Firefox background evaluates with stub browser APIs", async () => {
  const postedMessages = [];
  await evaluateBackgroundScript("firefox", "browser", createFirefoxApi(postedMessages));
  assertEqual(1, postedMessages.length, "Firefox startup should send one initial snapshot.");
  assertEqual("audibleWindows", postedMessages[0].type, "Firefox should send the expected message type.");
});

async function runTests() {
  let passed = 0;
  for (const test of tests) {
    try {
      await test.check();
      passed++;
      console.log(`[PASS] ${test.name}`);
    } catch (error) {
      console.error(`[FAIL] ${test.name}: ${error.message}`);
    }
  }

  console.log(`Browser extension regression tests passed: ${passed}/${tests.length}.`);
  if (passed !== tests.length) {
    process.exitCode = 1;
  }
}

runTests().catch((error) => {
  console.error(`[FAIL] Browser extension regression runner: ${error.stack || error.message}`);
  process.exitCode = 1;
});
