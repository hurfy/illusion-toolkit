"""Localhost TCP server for the bridge control channel.

All networking runs on daemon threads that NEVER touch bpy: inbound messages
are queued and drained by the main-thread pump in __init__. One client owns
the bridge at a time — ownership is claimed by the first accepted hello and
lasts until that socket disconnects; extra connections are accepted so the
hello handler can deny them explicitly.
"""

import json
import os
import queue
import socket
import threading
import time

from . import protocol

# Windowed Blender on Windows swallows late stdout, so diagnostics go to a file.
LOG_PATH = os.path.join(os.environ.get("TEMP", "."), "illusion_bridge_addon.log")


def log(text):
    """Append one timestamped line to the addon log (best-effort)."""
    try:
        with open(LOG_PATH, "a", encoding="utf-8") as fh:
            fh.write(f"{time.strftime('%H:%M:%S')} {text}\n")
    except OSError:
        pass

# Cross-module bridge state, read/written on the main thread (except scene_loaded
# is only ever toggled there too). Survives importlib.reload of sibling modules.
state = {
    "auto_push": False,   # non-functional in Phase A; kept in sync with the toolkit
    "scene_loaded": False,  # a bridge scene is present (drives scene_lost on file open)
}

_lock = threading.Lock()
_send_lock = threading.Lock()
_inbound = queue.Queue()  # (client, message dict)
_running = False
_listen_sock = None
_accept_thread = None
_clients = []
_owner = None
_seq = 0


class Client:
    """One accepted TCP connection; session is set when its hello is accepted."""

    def __init__(self, sock):
        self.sock = sock
        self.session = None
        self.alive = True


def start():
    """Bind 127.0.0.1 on an ephemeral port, start accepting; returns the port."""
    global _running, _listen_sock, _accept_thread, _seq
    stop()  # reload safety: never leak a previous socket or threads

    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.bind(("127.0.0.1", 0))
    sock.listen(2)

    _listen_sock = sock
    _seq = 0
    _running = True
    _accept_thread = threading.Thread(
        target=_accept_loop, name="illusion-bridge-accept", daemon=True)
    _accept_thread.start()
    return sock.getsockname()[1]


def stop():
    """Close the listener and every client; idempotent."""
    global _running, _listen_sock, _accept_thread, _owner
    _running = False

    if _listen_sock is not None:
        try:
            _listen_sock.close()
        except OSError:
            pass
        _listen_sock = None

    with _lock:
        clients = list(_clients)
        _clients.clear()
        _owner = None
    for client in clients:
        client.alive = False
        try:
            client.sock.close()
        except OSError:
            pass

    if _accept_thread is not None:
        if _accept_thread.is_alive():
            _accept_thread.join(timeout=1.0)
        _accept_thread = None

    while True:
        try:
            _inbound.get_nowait()
        except queue.Empty:
            break


def poll():
    """Next queued (client, message) or None; called by the main-thread pump."""
    try:
        return _inbound.get_nowait()
    except queue.Empty:
        return None


def send(msg, client=None):
    """Stamp seq and send one NDJSON line; defaults to the owner. Peer-gone
    socket errors are swallowed — the read loop notices the disconnect."""
    global _seq
    target = client
    if target is None:
        with _lock:
            target = _owner
    if target is None:
        log(f"send {msg.get('type', '?')}: no target")
        return
    with _send_lock:
        _seq += 1
        msg["seq"] = _seq
        try:
            data = (json.dumps(msg) + "\n").encode("utf-8")
            target.sock.sendall(data)
            log(f"send {msg.get('type', '?')} ({len(data)} bytes)")
        except OSError as exc:
            log(f"send {msg.get('type', '?')} failed: {exc}")


def owner():
    with _lock:
        return _owner


def owner_session():
    with _lock:
        return _owner.session if _owner is not None else None


def is_connected():
    with _lock:
        return _owner is not None and _owner.alive


def claim_owner(client, session):
    """Record the hello'd client as the bridge owner (main thread)."""
    global _owner
    with _lock:
        client.session = session
        _owner = client


def close_client(client):
    """Gracefully drop one client (bye, or a denied hello)."""
    _drop_client(client)


def _drop_client(client):
    global _owner
    client.alive = False
    with _lock:
        if client in _clients:
            _clients.remove(client)
        if _owner is client:
            _owner = None
    try:
        client.sock.close()
    except OSError:
        pass


def _accept_loop():
    sock = _listen_sock
    while _running:
        try:
            client_sock, _addr = sock.accept()
        except OSError:
            break  # listener closed by stop()
        client = Client(client_sock)
        with _lock:
            _clients.append(client)
        threading.Thread(
            target=_read_loop, args=(client,), name="illusion-bridge-read",
            daemon=True).start()


def _read_loop(client):
    buffer = b""
    while _running and client.alive:
        try:
            data = client.sock.recv(65536)
        except OSError:
            break
        if not data:
            break
        buffer += data
        while True:
            line, sep, rest = buffer.partition(b"\n")
            if not sep:
                break
            buffer = rest
            line = line.strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
                if not isinstance(msg, dict):
                    raise ValueError("not a JSON object")
            except (ValueError, UnicodeDecodeError):
                send(protocol.make(protocol.ERROR, message="malformed json", fatal=False), client)
                continue
            log(f"recv {msg.get('type', '?')}")
            _inbound.put((client, msg))
    _drop_client(client)
