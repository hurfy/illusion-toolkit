"""Reader and writer for .ilx exchange containers.

Layout (all little-endian, see C# ExchangeWriter/ExchangeReader):
u32 magic "ILEX" | u32 version | u32 header JSON byte length | UTF-8 header JSON |
raw blocks, each 16-byte aligned. Header block offsets are RELATIVE to
data_start = align16(12 + headerLength).
"""

import json
import os
import struct

import numpy as np

MAGIC = 0x58454C49  # ASCII "ILEX"
VERSION = 1
BLOCK_ALIGNMENT = 16

DTYPES = {
    "f32": "<f4",
    "u32": "<u4",
    "i32": "<i4",
    "u16": "<u2",
    "u8": "u1",
}


def read_container(path):
    """Parse an .ilx file into (header dict, block list).

    Blocks with a known dtype become numpy arrays shaped (count, components)
    (1-D when components == 1); blocks with an unknown dtype stay raw bytes.
    """
    with open(path, "rb") as fh:
        data = fh.read()

    magic, version, header_length = struct.unpack_from("<III", data, 0)
    if magic != MAGIC:
        raise ValueError(f"Not an .ilx container (magic 0x{magic:08X}): {path}")
    if version > VERSION:
        raise ValueError(
            f"Container version {version} is newer than supported {VERSION}: {path}")

    header = json.loads(data[12:12 + header_length].decode("utf-8"))
    data_start = _align(12 + header_length)

    blocks = []
    for entry in header.get("blocks", []):
        components = int(entry.get("components", 1))
        count = int(entry.get("count", 0))
        offset = data_start + int(entry.get("offset", 0))
        byte_length = int(entry.get("byteLength", 0))

        np_dtype = DTYPES.get(entry.get("dtype", ""))
        if np_dtype is None:
            blocks.append(data[offset:offset + byte_length])
            continue

        elements = count * components
        if offset + elements * np.dtype(np_dtype).itemsize > len(data):
            raise ValueError(f"Container block truncated at offset {offset}: {path}")
        array = np.frombuffer(data, dtype=np_dtype, count=elements, offset=offset)
        if components > 1:
            array = array.reshape(count, components)
        blocks.append(array)

    return header, blocks


def get_object_arrays(header, blocks, obj):
    """Map one header object's array names to their parsed blocks."""
    return {
        name: blocks[index]
        for name, index in (obj.get("arrays") or {}).items()
        if isinstance(index, int) and 0 <= index < len(blocks)
    }


def write_container(path, session, objects, blocks):
    """Write an .ilx file the C# ExchangeReader can parse — the inverse of read_container.

    objects: header object dicts (their "arrays" values index into blocks).
    blocks: list of (dtype_str, components, count, data) where data is a numpy
    array (any shape; flattened and cast to the little-endian dtype) or bytes.
    Written to path+".tmp" then atomically renamed.
    """
    entries = []
    payloads = []
    relative = 0
    for dtype_str, components, count, data in blocks:
        if isinstance(data, np.ndarray):
            raw = np.ascontiguousarray(data, dtype=DTYPES[dtype_str]).tobytes()
        else:
            raw = bytes(data)
        relative = _align(relative)
        entries.append({
            "dtype": dtype_str,
            "components": components,
            "count": count,
            "offset": relative,
            "byteLength": len(raw),
        })
        payloads.append((relative, raw))
        relative += len(raw)

    header = {
        "format": "illusion-exchange",
        "version": VERSION,
        "session": session,
        "producer": "blender-addon",
        "source": None,
        "objects": objects,
        "blocks": entries,
    }
    header_bytes = json.dumps(header).encode("utf-8")
    data_start = _align(12 + len(header_bytes))

    tmp = path + ".tmp"
    with open(tmp, "wb") as fh:
        fh.write(struct.pack("<III", MAGIC, VERSION, len(header_bytes)))
        fh.write(header_bytes)
        for relative_offset, raw in payloads:
            absolute = data_start + relative_offset
            fh.write(b"\x00" * (absolute - fh.tell()))
            fh.write(raw)
    os.replace(tmp, path)


def _align(offset):
    remainder = offset % BLOCK_ALIGNMENT
    return offset if remainder == 0 else offset + (BLOCK_ALIGNMENT - remainder)
