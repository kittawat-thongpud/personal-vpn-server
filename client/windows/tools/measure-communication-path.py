#!/usr/bin/env python3
"""Compare small Windows HTTPS and STUN probes without editing network settings."""

import argparse
from contextlib import ExitStack
from datetime import datetime, timezone
import http.client
import ipaddress
import json
import math
import os
import socket
import ssl
import statistics
import struct
import time


HOST = "speed.cloudflare.com"
STUN_HOST, STUN_PORT = "stun.cloudflare.com", 3478
STUN_COOKIE = b"\x21\x12\xa4\x42"
IP_UNICAST_IF = 31  # Winsock, AF_INET; option value is in network byte order.
TIMEOUT = 5
DOWNLOAD_BYTES = 512 * 1024


def elapsed(start):
    return round((time.perf_counter() - start) * 1000, 3)


def receive_exact(sock, count):
    data = bytearray()
    while len(data) < count:
        piece = sock.recv(count - len(data))
        if not piece:
            raise ConnectionError("SOCKS reply truncated")
        data.extend(piece)
    return bytes(data)


def socks_command(sock, command, target, port):
    sock.sendall(b"\x05\x01\x00")
    if receive_exact(sock, 2) != b"\x05\x00":
        raise ConnectionError("SOCKS no-auth unavailable")
    sock.sendall(bytes((5, command, 0, 1)) + socket.inet_aton(target) + struct.pack("!H", port))
    ver, result, reserved, address_type = receive_exact(sock, 4)
    if (ver, result, reserved) != (5, 0, 0):
        raise ConnectionError(f"SOCKS command {command} returned {result}")
    size = {1: 4, 4: 16}.get(address_type)
    if size is None and address_type == 3:
        size = receive_exact(sock, 1)[0]
    if size is None:
        raise ConnectionError("invalid SOCKS reply address type")
    address = receive_exact(sock, size)
    port, = struct.unpack("!H", receive_exact(sock, 2))
    return address_type, address, port


def bind_physical(sock, args):
    sock.setsockopt(socket.IPPROTO_IP, IP_UNICAST_IF, struct.pack("!I", args.interface_index))
    sock.bind((args.source_address, 0))


def request(connection, path, limit):
    start = time.perf_counter()
    connection.request("GET", path, headers={"Accept-Encoding": "identity", "Cache-Control": "no-cache"})
    with connection.getresponse() as response:
        headers_ms = elapsed(start)
        if response.status != 200:
            raise ConnectionError(f"HTTPS endpoint returned {response.status}")
        ray = response.getheader("CF-Ray", "")
        data = response.read(limit + 1)
        if len(data) > limit:
            raise ConnectionError("response exceeded expected size")
    return headers_ms, elapsed(start), ray.rpartition("-")[2] if "-" in ray else None, data


def sample(mode, target, args, context, number, download):
    row = {"path": mode, "sample": number}
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as raw:
            raw.settimeout(TIMEOUT)
            if mode == "bound_physical":
                bind_physical(raw, args)
            cold_start = time.perf_counter()
            start = time.perf_counter()
            raw.connect(("127.0.0.1", args.socks_port) if mode == "local_socks" else (target, 443))
            row["tcp_connect_ms"] = elapsed(start)
            if mode == "local_socks":
                start = time.perf_counter()
                socks_command(raw, 1, target, 443)
                row["socks_setup_ms"] = elapsed(start)
            start = time.perf_counter()
            with context.wrap_socket(raw, server_hostname=HOST) as tls:
                row["tls_ms"] = elapsed(start)
                conn = http.client.HTTPConnection(HOST, timeout=TIMEOUT)
                conn.sock = tls
                try:
                    row["headers_ms"], _, row["edge"], trace = request(conn, "/cdn-cgi/trace", 4096)
                    row["cold_total_ms"] = elapsed(cold_start)
                    fields = dict(line.split("=", 1) for line in trace.decode().splitlines() if "=" in line)
                    public_ip = ipaddress.ip_address(fields["ip"])
                    row["egress"] = "expected_vpn" if str(public_ip) == args.expected_vpn else "other"
                    row["edge"] = fields.get("colo", row["edge"])
                    if conn.sock is None:
                        raise ConnectionError("server closed connection before warm request")
                    count = DOWNLOAD_BYTES if download else 0
                    nonce = time.time_ns()
                    row["warm_headers_ms"], total, row["warm_edge"], payload = request(
                        conn, f"/__down?bytes={count}&nonce={nonce}", count
                    )
                    if len(payload) != count:
                        raise ConnectionError("download length did not match request")
                    if download:
                        row["download_ms"] = total
                        row["download_mbps_including_headers"] = round(count * 8 / total / 1000, 3)
                finally:
                    conn.close()
    except (OSError, http.client.HTTPException, UnicodeError, ValueError, KeyError) as error:
        row["error"] = type(error).__name__
        if isinstance(error, ConnectionError) and not getattr(error, "errno", None):
            row["reason"] = str(error)
        elif getattr(error, "winerror", None) or getattr(error, "errno", None):
            row["socket_error"] = error.winerror if getattr(error, "winerror", None) else error.errno
    return row


def mapped_ipv4(packet, transaction):
    if len(packet) < 20 or packet[:2] != b"\x01\x01" or packet[4:20] != STUN_COOKIE + transaction:
        return None
    end = 20 + int.from_bytes(packet[2:4], "big")
    if end > len(packet):
        return None
    offset = 20
    while offset + 4 <= end:
        kind, length = struct.unpack_from("!HH", packet, offset)
        start = offset + 4
        if start + length > end:
            return None
        if kind == 0x0020 and length == 8 and packet[start:start + 2] == b"\x00\x01":
            encoded = packet[start + 4:start + 8]
            return socket.inet_ntoa(bytes(a ^ b for a, b in zip(encoded, STUN_COOKIE)))
        offset = start + ((length + 3) & ~3)
    return None


def udp_probes(target, args, modes):
    header = b"\x00\x00\x00\x01" + socket.inet_aton(target) + struct.pack("!H", STUN_PORT)
    rows, setup_errors, sockets = [], {}, {}
    with ExitStack() as stack:
        for mode in modes:
            try:
                udp = stack.enter_context(socket.socket(socket.AF_INET, socket.SOCK_DGRAM))
                endpoint = (target, STUN_PORT)
                if mode == "bound_physical":
                    bind_physical(udp, args)
                elif mode == "local_socks":
                    control = stack.enter_context(socket.create_connection(("127.0.0.1", args.socks_port), TIMEOUT))
                    kind, bind_address, port = socks_command(control, 3, "0.0.0.0", 0)
                    if kind != 1 or not port or bind_address not in (b"\x00" * 4, b"\x7f\x00\x00\x01"):
                        raise ConnectionError("SOCKS returned a non-loopback or invalid UDP relay")
                    endpoint = ("127.0.0.1", port)
                udp.connect(endpoint)
                sockets[mode] = udp
            except OSError as error:
                setup_errors[mode] = type(error).__name__ + (f": {error}" if isinstance(error, ConnectionError) and error.errno is None else "")
        for number in range(args.samples):
            offset = number % len(modes)
            for mode in modes[offset:] + modes[:offset]:
                if mode not in sockets:
                    continue
                udp = sockets[mode]
                transaction = os.urandom(12)
                packet = b"\x00\x01\x00\x00" + STUN_COOKIE + transaction
                row = {"path": mode, "sample": number + 1}
                start = time.perf_counter()
                try:
                    udp.send(header + packet if mode == "local_socks" else packet)
                    while (remaining := start + 2 - time.perf_counter()) > 0:
                        udp.settimeout(remaining)
                        data = udp.recv(2048)
                        if mode == "local_socks":
                            if not data.startswith(header):
                                continue
                            data = data[len(header):]
                        mapped = mapped_ipv4(data, transaction)
                        if mapped:
                            row.update(rtt_ms=elapsed(start), egress="expected_vpn" if mapped == args.expected_vpn else "other")
                            break
                    if "rtt_ms" not in row:
                        row["error"] = "TimeoutError"
                except OSError as error:
                    row["error"] = type(error).__name__
                rows.append(row)
            time.sleep(0.15)
    return {"target": STUN_HOST, "target_ipv4": target, "port": STUN_PORT, "setup_errors": setup_errors,
            "samples": rows, "summary": summarize(rows, udp=True)}


def summarize(rows, udp=False):
    result = {}
    for mode in dict.fromkeys(row["path"] for row in rows):
        group = [row for row in rows if row["path"] == mode]
        ok = [row for row in group if "error" not in row]
        summary = {"successful": len(ok), "attempted": len(group), "observed_egress": sorted({row["egress"] for row in ok})}
        if udp:
            summary["no_reply"] = len(group) - len(ok)
            pairs = [(a, b) for a, b in zip(ok, ok[1:]) if b["sample"] == a["sample"] + 1]
            if pairs:
                summary["median_consecutive_rtt_delta_ms"] = round(statistics.median(abs(a["rtt_ms"] - b["rtt_ms"]) for a, b in pairs), 3)
            metrics = ("rtt_ms",)
        else:
            summary["observed_edges"] = sorted({row["edge"] for row in ok if row["edge"]})
            metrics = ("tcp_connect_ms", "socks_setup_ms", "tls_ms", "headers_ms", "cold_total_ms", "warm_headers_ms", "download_mbps_including_headers")
        for metric in metrics:
            samples = sorted(row[metric] for row in ok if metric in row and (metric != "warm_headers_ms" or "download_ms" not in row))
            if samples:
                stats = {"n": len(samples), "median": round(statistics.median(samples), 3), "min": samples[0], "max": samples[-1]}
                if len(samples) >= 20:
                    stats["p95"] = samples[math.ceil(len(samples) * 0.95) - 1]
                summary[metric] = stats
        result[mode] = summary
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--interface-index", type=int, required=True)
    parser.add_argument("--source-address", required=True, type=lambda value: str(ipaddress.IPv4Address(value)))
    parser.add_argument("--expected-vpn", required=True, type=lambda value: str(ipaddress.ip_address(value)))
    parser.add_argument("--socks-port", type=int, default=17891)
    parser.add_argument("--samples", type=int, default=8, choices=range(3, 25), metavar="[3-24]")
    parser.add_argument("--stun", action="store_true", help="also measure low-rate IPv4 STUN UDP probes")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    if os.name != "nt" or not 0 < args.interface_index < 2**32 or not 0 < args.socks_port < 65536:
        parser.error("Windows, a positive interface index and a valid loopback SOCKS port are required")
    with open(args.output, "x", encoding="utf-8") as report:
        target = socket.getaddrinfo(HOST, 443, socket.AF_INET, socket.SOCK_STREAM)[0][4][0]
        context = ssl.create_default_context()
        modes = ["os_default", "local_socks", "bound_physical"]
        rows = []
        for number in range(args.samples + 2):
            offset = number % len(modes)
            for mode in modes[offset:] + modes[:offset]:
                rows.append(sample(mode, target, args, context, number + 1, number >= args.samples))
        result = {"utc": datetime.now(timezone.utc).isoformat(), "host": HOST, "target_ipv4": target,
                  "interface_index": args.interface_index, "summary": summarize(rows), "samples": rows}
        if args.stun:
            try:
                stun_target = socket.getaddrinfo(STUN_HOST, STUN_PORT, socket.AF_INET, socket.SOCK_DGRAM)[0][4][0]
                result["stun"] = udp_probes(stun_target, args, modes)
            except OSError as error:
                result["stun"] = {"error": type(error).__name__}
        json.dump(result, report, indent=2)
        report.write("\n")
        print(json.dumps({"https": result["summary"], "stun": {k: v for k, v in result.get("stun", {}).items() if k != "samples"}}, indent=2))
    return int(not all(result["summary"][mode]["successful"] for mode in modes))


if __name__ == "__main__":
    raise SystemExit(main())
