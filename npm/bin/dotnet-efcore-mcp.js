#!/usr/bin/env node

/**
 * dotnet-efcore-mcp launcher
 * 
 * This package ships no server code; it exists so the server can be started via
 * `npx -y dotnet-efcore-mcp`. The launcher verifies the .NET SDK is present,
 * ensures the matching-version `DotnetEfCoreMcp.Server` .NET global tool is
 * installed, then execs it. stdout is the JSON-RPC channel so all diagnostics
 * and install output go to stderr.
 */

const { execFileSync, spawn } = require("node:child_process");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const PACKAGE_ID = "DotnetEfCoreMcp.Server";
const COMMAND = "dotnet-efcore-mcp";
const { version } = require("../package.json");

/**
 * Write an error message to stderr and exit with code 1.
 */
function fail(message) {
  process.stderr.write(`dotnet-efcore-mcp: ${message}\n`);
  process.exit(1);
}

/**
 * Check if the .NET SDK is available on PATH.
 */
function hasDotnet() {
  try {
    execFileSync("dotnet", ["--version"], { stdio: "ignore" });
    return true;
  } catch {
    return false;
  }
}

/**
 * Get the installed version of the global tool, or null if not found/error.
 * Parses `dotnet tool list --global` output, looking for a line matching
 * the package id (dotnet lowercases and strips dots).
 */
function installedVersion() {
  try {
    const output = execFileSync("dotnet", ["tool", "list", "--global"], {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "ignore"],
    });
    const lines = output.split("\n");
    for (const line of lines) {
      const match = line.match(/^dotnetefcoremcp\.server\s+(\S+)/i);
      if (match) {
        return match[1];
      }
    }
    return null;
  } catch {
    return null;
  }
}

/**
 * Install or update the global tool to the current package version.
 * @param {boolean} alreadyInstalled - true if updating, false if installing
 */
function install(alreadyInstalled) {
  const verb = alreadyInstalled ? "update" : "install";
  process.stderr.write(`dotnet-efcore-mcp: ${verb}ing ${PACKAGE_ID} v${version}...\n`);
  
  const args = ["tool", verb, "--global", PACKAGE_ID, "--version", version];
  
  // If a custom NuGet source is configured, add it to the args
  if (process.env.DOTNET_EFCORE_MCP_NUGET_SOURCE) {
    args.push("--add-source", process.env.DOTNET_EFCORE_MCP_NUGET_SOURCE);
  }
  
  execFileSync("dotnet", args, { stdio: ["ignore", 2, 2] });
}

/**
 * Resolve the directory where .NET global tools are installed.
 */
function toolsDir() {
  return process.env.DOTNET_TOOLS_PATH || path.join(
    process.env.DOTNET_CLI_HOME || os.homedir(),
    ".dotnet",
    "tools"
  );
}

/**
 * Resolve the full path to the executable command.
 * On Windows, looks for ${COMMAND}.exe; on other platforms, just ${COMMAND}.
 * Returns the full path if it exists, otherwise returns just the command name
 * (rely on PATH resolution).
 */
function resolveExecutable() {
  const dir = toolsDir();
  const executable = process.platform === "win32" ? `${COMMAND}.exe` : COMMAND;
  const fullPath = path.join(dir, executable);
  
  if (fs.existsSync(fullPath)) {
    return fullPath;
  }
  return COMMAND;
}

/**
 * Launch the dotnet-efcore-mcp server.
 * Forwards stdin/stdout/stderr to the child process, handles signals,
 * and exits with the child's exit code.
 */
function launch() {
  const executable = resolveExecutable();
  const child = spawn(executable, process.argv.slice(2), {
    stdio: "inherit",
    env: {
      ...process.env,
      PATH: `${toolsDir()}${path.delimiter}${process.env.PATH || ""}`,
    },
  });

  // Forward SIGINT and SIGTERM to the child
  const signalHandler = (signal) => {
    child.kill(signal);
  };
  
  process.on("SIGINT", () => signalHandler("SIGINT"));
  process.on("SIGTERM", () => signalHandler("SIGTERM"));

  child.on("error", (err) => {
    fail(`failed to launch: ${err.message}`);
  });

  child.on("exit", (code, signal) => {
    if (signal) {
      // Child was killed by a signal; re-send it to ourselves
      process.kill(process.pid, signal);
      return;
    }
    process.exit(code ?? 0);
  });
}

// Main flow
if (!hasDotnet()) {
  fail(
    "the .NET SDK was not found on PATH. dotnet-efcore-mcp requires the .NET 10 SDK — https://dotnet.microsoft.com/download"
  );
}

const current = installedVersion();
if (current !== version) {
  try {
    install(current !== null);
  } catch (err) {
    if (current === null) {
      fail(`${PACKAGE_ID} is not installed and automatic installation failed: ${err.message}`);
    } else {
      // Non-fatal: could not update, but an installed version exists; proceed with it
      process.stderr.write(
        `dotnet-efcore-mcp: warning: could not update to v${version}: ${err.message}\n`
      );
      process.stderr.write(
        `dotnet-efcore-mcp: continuing with installed version v${current}\n`
      );
    }
  }
}

launch();
