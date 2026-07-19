#!/usr/bin/env python3
import ipaddress
import re
import socket
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse


HOST_PATTERN = re.compile(r"^[A-Za-z0-9.-]{1,253}$")


class ResolveHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        parsed = urlparse(self.path)
        host = parse_qs(parsed.query).get("host", [""])[0]
        if parsed.path != "/resolve" or not HOST_PATTERN.fullmatch(host):
            self.send_error(400)
            return

        try:
            addresses = []
            for result in socket.getaddrinfo(host, None, socket.AF_INET, socket.SOCK_STREAM):
                address = str(ipaddress.ip_address(result[4][0]))
                if address not in addresses:
                    addresses.append(address)
            if not addresses:
                raise socket.gaierror("no IPv4 result")
            body = ("\n".join(addresses) + "\n").encode("ascii")
            self.send_response(200)
            self.send_header("Content-Type", "text/plain; charset=ascii")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        except (socket.gaierror, ValueError):
            self.send_error(502)

    def log_message(self, _format, *args):
        return


if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", 39082), ResolveHandler).serve_forever()
