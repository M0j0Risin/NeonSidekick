// NeonSidekick's tool bridge for a Node script run by execute_code.
//
// Every tool the chat offers is one call away: `await neon.call("read_file", { path: "notes.txt" })`
// resolves to the tool's text, and a tool that answers "Error: ..." rejects with that sentence.
// The wrappers below name the common ones. Each call is one short connection to the app over
// loopback, authenticated by the run's token; both come from the environment execute_code set.
// CommonJS, built-in modules only: `const neon = require("neon_tools")`, then
// `neon.run(async () => { ... })` for top-level awaits, or plain promises.

"use strict";

const net = require("net");

const ADDRESS = process.env.NEONSIDEKICK_BRIDGE_ADDRESS || "";
const TOKEN = process.env.NEONSIDEKICK_BRIDGE_TOKEN || "";

class ToolError extends Error {}

function call(tool, args) {
  return new Promise((resolve, reject) => {
    if (!ADDRESS || !TOKEN) {
      reject(new ToolError("Error: the bridge is not configured (run this script through execute_code)"));
      return;
    }
    const at = ADDRESS.lastIndexOf(":");
    const host = ADDRESS.slice(0, at);
    const port = Number(ADDRESS.slice(at + 1));
    const chunks = [];
    const socket = net.createConnection({ host, port }, () => {
      socket.write(JSON.stringify({ token: TOKEN, tool, arguments: args || {} }) + "\n");
    });
    socket.setEncoding("utf8");
    socket.on("data", (chunk) => {
      chunks.push(chunk);
      if (chunk.endsWith("\n")) {
        socket.end();
      }
    });
    socket.on("error", reject);
    socket.on("close", () => {
      let reply;
      try {
        reply = JSON.parse(chunks.join(""));
      } catch (error) {
        reject(new ToolError("Error: the bridge answered with something that is not JSON"));
        return;
      }
      if (reply.error !== undefined) {
        reject(new ToolError(reply.error));
      } else {
        resolve(reply.result === undefined ? "" : reply.result);
      }
    });
  });
}

// Runs an async main and reports a rejection as the script's failure (exit 1).
function run(main) {
  return Promise.resolve()
    .then(main)
    .catch((error) => {
      console.error(error && error.message ? error.message : String(error));
      process.exitCode = 1;
    });
}

module.exports = {
  ToolError,
  call,
  run,
  readFile: (path, args) => call("read_file", { path, ...(args || {}) }),
  readPdf: (path, args) => call("read_pdf", { path, ...(args || {}) }),
  writeFile: (path, content, args) => call("write_file", { path, content, ...(args || {}) }),
  patchFile: (path, args) => call("patch_file", { path, ...(args || {}) }),
  searchFiles: (args) => call("search_files", args || {}),
  fileInfo: (path, args) => call("file_info", { path, ...(args || {}) }),
  webSearch: (query, args) => call("web_search", { query, ...(args || {}) }),
  webFetch: (url, args) => call("web_fetch", { url, ...(args || {}) }),
  runCommand: (command, args) => call("run_command", { command, ...(args || {}) }),
  getWorkingDirectory: () => call("get_working_directory", {}),
};
