"use strict";

const { spawn } = require("child_process");
const readline = require("readline");

const executable = process.argv[2];
if (!executable) {
  console.error("Usage: node probe_codex_appserver.js <codex.exe>");
  process.exit(2);
}

const child = spawn(executable, ["app-server", "--stdio"], {
  stdio: ["pipe", "pipe", "ignore"],
  windowsHide: true,
});

function send(value) {
  child.stdin.write(JSON.stringify(value) + "\n");
}

const timer = setTimeout(() => {
  console.error("Timed out waiting for Codex app-server");
  child.kill();
  process.exitCode = 1;
}, 15000);

readline.createInterface({ input: child.stdout }).on("line", line => {
  let message;
  try {
    message = JSON.parse(line);
  } catch {
    return;
  }
  if (message.id === 1) {
    send({ method: "initialized", params: {} });
    send({ id: 2, method: "account/rateLimits/read", params: {} });
  } else if (message.id === 2) {
    clearTimeout(timer);
    console.log(JSON.stringify(message.result, null, 2));
    child.kill();
  }
});

send({
  id: 1,
  method: "initialize",
  params: {
    clientInfo: {
      name: "usage-companion-probe",
      title: "Usage Companion Probe",
      version: "0.1.0",
    },
    capabilities: { experimentalApi: true },
  },
});
