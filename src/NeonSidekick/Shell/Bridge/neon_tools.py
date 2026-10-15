"""NeonSidekick's tool bridge for a Python script run by execute_code.

Every tool the chat offers is one call away: ``call("read_file", path="notes.txt")`` returns the
tool's text, and a tool that answers ``Error: ...`` raises ``ToolError`` with that sentence. The
wrappers below name the common ones; ``call`` takes any tool the turn offers, with its arguments
as keywords. Each call is one short connection to the app over loopback, authenticated by the
run's token; both come from the environment execute_code set. Standard library only.
"""

import json
import os
import socket

_ADDRESS = os.environ.get("NEONSIDEKICK_BRIDGE_ADDRESS", "")
_TOKEN = os.environ.get("NEONSIDEKICK_BRIDGE_TOKEN", "")


class ToolError(Exception):
    """A tool answered with an Error: sentence."""


def call(tool, **arguments):
    """Calls the tool by name with keyword arguments and returns its text."""
    if not _ADDRESS or not _TOKEN:
        raise ToolError("Error: the bridge is not configured (run this script through execute_code)")
    host, port = _ADDRESS.rsplit(":", 1)
    request = json.dumps({"token": _TOKEN, "tool": tool, "arguments": arguments}, ensure_ascii=False) + "\n"
    with socket.create_connection((host, int(port))) as connection:
        connection.sendall(request.encode("utf-8"))
        connection.shutdown(socket.SHUT_WR)
        data = bytearray()
        while True:
            chunk = connection.recv(65536)
            if not chunk:
                break
            data.extend(chunk)
            if data.endswith(b"\n"):
                break
    reply = json.loads(data.decode("utf-8"))
    if "error" in reply:
        raise ToolError(reply["error"])
    return reply.get("result", "")


def read_file(path, **arguments):
    return call("read_file", path=path, **arguments)


def read_pdf(path, **arguments):
    return call("read_pdf", path=path, **arguments)


def write_file(path, content, **arguments):
    return call("write_file", path=path, content=content, **arguments)


def patch_file(path, **arguments):
    return call("patch_file", path=path, **arguments)


def search_files(**arguments):
    return call("search_files", **arguments)


def file_info(path, **arguments):
    return call("file_info", path=path, **arguments)


def web_search(query, **arguments):
    return call("web_search", query=query, **arguments)


def web_fetch(url, **arguments):
    return call("web_fetch", url=url, **arguments)


def run_command(command, **arguments):
    return call("run_command", command=command, **arguments)


def get_working_directory():
    return call("get_working_directory")
