import { createServer } from "node:http";
import { connect as netConnect, isIP } from "node:net";
import { connect as tlsConnect } from "node:tls";
import next from "next";

/**
 * Production web entrypoint.
 *
 * Next.js is served exactly like `next start`, with one addition: the read-only VNC gateway
 * (`/admin/vnc`) is proxied over the same public origin. Browsers that can reach the web through
 * Cloudflare/reverse proxy (443/wss) can then open the Controller's WSS gateway without exposing
 * Controller port 7443 or trusting its private certificate. The Controller still authenticates the
 * one-time ticket, admin origin and read-only protocol; this proxy only relays the WebSocket.
 */

const port = Number.parseInt(process.env.PORT || "3000", 10);
const hostname = process.env.HOSTNAME_BIND || "0.0.0.0";
const dev = process.env.NODE_ENV !== "production";
const vncTarget = new URL(process.env.VNC_PROXY_TARGET || "https://127.0.0.1:7443");
const vncPath = "/admin/vnc";
const handshakeTimeoutMs = 20_000;
const maxHandshakeHeadersBytes = 32 * 1024;

if (!["http:", "https:"].includes(vncTarget.protocol))
  throw new Error("VNC_PROXY_TARGET must be an http(s) URL.");

const app = next({ dev });
const handle = app.getRequestHandler();

function controllerRequestBytes(incoming) {
  const host = vncTarget.host;
  const lines = [
    `GET ${vncPath} HTTP/1.1`,
    `Host: ${host}`,
    "Upgrade: websocket",
    "Connection: Upgrade",
  ];
  for (const name of [
    "sec-websocket-key",
    "sec-websocket-version",
    "sec-websocket-protocol",
    "origin",
    "user-agent",
  ]) {
    const value = incoming.headers[name];
    if (typeof value === "string" && value) lines.push(`${headerName(name)}: ${value}`);
  }
  return `${lines.join("\r\n")}\r\n\r\n`;
}

function headerName(name) {
  return name
    .split("-")
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join("-");
}

function writeError(socket, status, message) {
  const body = `${message}\n`;
  try {
    socket.write(
      `HTTP/1.1 ${status} ${message}\r\nConnection: close\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: ${Buffer.byteLength(body)}\r\n\r\n${body}`,
    );
  } catch {
    // The browser may already be gone.
  }
  socket.destroy();
}

function proxyVncUpgrade(req, socket, head) {
  socket.pause();
  let settled = false;
  let pending = Buffer.alloc(0);
  const finish = () => {
    if (settled) return;
    settled = true;
    try {
      socket.destroy();
    } catch {
      /* ignore */
    }
  };

  const upstream =
    vncTarget.protocol === "https:"
      ? tlsConnect({
          host: vncTarget.hostname,
          port: Number.parseInt(vncTarget.port || "443", 10),
          // Node rejects SNI for IP literals; the Controller certificate is not validated here
          // because the public TLS hop is terminated by Cloudflare/Caddy.
          ...(process.env.VNC_PROXY_SERVERNAME &&
          !isIP(process.env.VNC_PROXY_SERVERNAME)
            ? { servername: process.env.VNC_PROXY_SERVERNAME }
            : {}),
          rejectUnauthorized: false,
          ALPNProtocols: ["http/1.1"],
        })
      : netConnect({
          host: vncTarget.hostname,
          port: Number.parseInt(vncTarget.port || "80", 10),
        });

  const timer = setTimeout(() => {
    writeError(socket, 504, "VNC gateway timeout");
    finish();
    upstream.destroy();
  }, handshakeTimeoutMs);

  const cleanup = () => {
    clearTimeout(timer);
    upstream.destroy();
  };
  socket.on("close", cleanup);
  socket.on("error", () => finish());
  upstream.on("error", () => {
    writeError(socket, 502, "VNC gateway unavailable");
    finish();
  });

  upstream.on("data", (chunk) => {
    if (settled) return;
    pending = Buffer.concat([pending, chunk]);
    if (pending.length > maxHandshakeHeadersBytes) {
      writeError(socket, 502, "VNC gateway response too large");
      finish();
      upstream.destroy();
      return;
    }
    const marker = pending.indexOf("\r\n\r\n");
    if (marker === -1) return;
    const header = pending.subarray(0, marker + 4);
    const rest = pending.subarray(marker + 4);
    clearTimeout(timer);
    const statusLine = header.toString("latin1").split("\r\n", 1)[0];
    if (!/^HTTP\/1\.[01] 101\b/.test(statusLine)) {
      // Relay the Controller's rejection verbatim so the viewer can show the real reason.
      try {
        socket.write(header);
        if (rest.length) socket.write(rest);
      } catch {
        /* ignore */
      }
      finish();
      upstream.destroy();
      return;
    }
    try {
      socket.write(header);
      if (rest.length) socket.write(rest);
      if (head?.length) upstream.write(head);
    } catch {
      finish();
      upstream.destroy();
      return;
    }
    settled = true;
    clearTimeout(timer);
    socket.pipe(upstream);
    upstream.pipe(socket);
    socket.resume();
  });

  upstream.on("connect", () => {
    try {
      upstream.write(controllerRequestBytes(req));
    } catch {
      writeError(socket, 502, "VNC gateway unavailable");
      finish();
      upstream.destroy();
    }
  });
}

await app.prepare();

const server = createServer((req, res) => {
  void handle(req, res);
});

server.on("upgrade", (req, socket, head) => {
  let pathname = "";
  try {
    pathname = new URL(req.url || "/", "http://localhost").pathname;
  } catch {
    socket.destroy();
    return;
  }
  if (pathname === vncPath) {
    try {
      proxyVncUpgrade(req, socket, head);
    } catch {
      // A malformed upgrade must never crash the web server; report it to the viewer.
      try {
        socket.write(
          "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\nContent-Length: 20\r\n\r\nVNC gateway failure\n",
        );
      } catch {
        /* ignore */
      }
      socket.destroy();
    }
    return;
  }
  // Production Next does not need WebSocket upgrades; fail fast instead of leaving sockets open.
  socket.destroy();
});

server.listen(port, hostname, () => {
  console.log(
    `> KamenRaiderPlay web listening at http://${hostname}:${port} (vnc proxy -> ${vncTarget.origin}${vncPath})`,
  );
});
