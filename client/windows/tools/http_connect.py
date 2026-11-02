"""Forward SSH stdio through an existing local HTTP CONNECT proxy."""
from __future__ import annotations

import argparse
import socket
import sys
import threading


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("hostname")
    parser.add_argument("port", type=int)
    parser.add_argument("--proxy-port", type=int, default=7688)
    args = parser.parse_args()
    if args.hostname != "45.77.179.227" or args.port != 22:
        raise SystemExit("This test helper only permits the selected VPS SSH endpoint.")
    if not 1 <= args.proxy_port <= 65535:
        raise SystemExit("Invalid local proxy port.")
    with socket.create_connection(("127.0.0.1", args.proxy_port), timeout=10) as stream:
        stream.settimeout(15)
        stream.sendall((f"CONNECT {args.hostname}:{args.port} HTTP/1.1\r\n"
                        f"Host: {args.hostname}:{args.port}\r\n\r\n").encode("ascii"))
        header = bytearray()
        while not header.endswith(b"\r\n\r\n") and len(header) < 16384:
            chunk = stream.recv(1)
            if not chunk:
                break
            header.extend(chunk)
        if not header.endswith(b"\r\n\r\n") or b" 200 " not in header.split(b"\r\n", 1)[0]:
            raise SystemExit("Existing proxy did not establish the SSH connection.")
        stream.settimeout(None)

        def upstream() -> None:
            try:
                while chunk := sys.stdin.buffer.read1(65536):
                    stream.sendall(chunk)
                stream.shutdown(socket.SHUT_WR)
            except OSError:
                pass

        threading.Thread(target=upstream, daemon=True).start()
        while chunk := stream.recv(65536):
            sys.stdout.buffer.write(chunk)
            sys.stdout.buffer.flush()


if __name__ == "__main__":
    try:
        main()
    except OSError:
        raise SystemExit("SSH proxy connection failed.") from None
