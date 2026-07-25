"""Bridge control-channel constants and helpers.

The wire format is NDJSON over localhost TCP: one JSON object per line,
discriminated by a "type" field; every message carries a per-sender monotonic
"seq" (stamped by server.send). Mirrors the C# BridgeMessages declarations.
"""

PROTOCOL_VERSION = 1
ADDON_VERSION = "0.1.0"

HELLO = "hello"
HELLO_ACK = "hello_ack"
HELLO_DENIED = "hello_denied"
LOAD_SCENE = "load_scene"
SCENE_READY = "scene_ready"
PUSH = "push"
PUSH_ACK = "push_ack"
SET_OPTIONS = "set_options"
REQUEST_PUSH = "request_push"
CLEAR_SCENE = "clear_scene"
SCENE_LOST = "scene_lost"
PING = "ping"
PONG = "pong"
ERROR = "error"
BYE = "bye"


def make(type_str, **fields):
    """Build a message dict; "seq" is stamped later by server.send."""
    msg = {"type": type_str}
    msg.update(fields)
    return msg
