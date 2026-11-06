import assert from "node:assert/strict";
import test from "node:test";
import { isLoopbackHostname, isPrivateVncSourceAddress, resolveVncSourceAddress } from "../src/lib/agent-install-vnc-source.mjs";

test("recognizes loopback hosts for localhost-only HTTP URL validation", () => {
  assert.equal(isLoopbackHostname("localhost"), true);
  assert.equal(isLoopbackHostname("[::1]"), true);
  assert.equal(isLoopbackHostname("127.9.8.7"), true);
  assert.equal(isLoopbackHostname("shop.localhost"), false);
});

test("requires an explicit LAN source when the Controller URL is loopback", () => {
  assert.equal(resolveVncSourceAddress("localhost", undefined), null);
  assert.equal(resolveVncSourceAddress("127.0.0.1", ""), null);
  assert.equal(resolveVncSourceAddress("localhost", "192.168.1.34"), "192.168.1.34");
});

test("does not accept loopback or public addresses as a VNC source", () => {
  assert.equal(isPrivateVncSourceAddress("127.0.0.1"), false);
  assert.equal(resolveVncSourceAddress("localhost", "127.0.0.1"), null);
  assert.equal(resolveVncSourceAddress("localhost", "203.0.113.10"), null);
});

test("accepts only canonical RFC1918 IPv4 sources", () => {
  for (const address of ["10.0.0.1", "172.16.0.1", "172.31.255.254", "192.168.1.34"]) {
    assert.equal(isPrivateVncSourceAddress(address), true, address);
  }
  for (const address of ["172.15.0.1", "172.32.0.1", "192.169.1.1", "192.168.001.34", "192.168.1.0/24"]) {
    assert.equal(isPrivateVncSourceAddress(address), false, address);
  }
});

test("falls back only when the Controller hostname itself is a private IPv4 address", () => {
  assert.equal(resolveVncSourceAddress("192.168.50.4", undefined), "192.168.50.4");
  assert.equal(resolveVncSourceAddress("controller.example", undefined), null);
});
