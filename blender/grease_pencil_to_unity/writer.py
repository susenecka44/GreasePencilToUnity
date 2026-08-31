"""The ``.gpencil`` container.

Layout::

    "GPU3" | u32 version | u32 jsonLen | u32 blobOffset | u32 blobLen
    utf-8 JSON manifest at offset 20
    raw little-endian blob at blobOffset

Structure lives in the JSON manifest; every bulk array is a ``{offset, count,
type}`` slice into the blob.  This keeps a 300-frame bake small and fast to
parse while leaving the manifest readable for debugging.
"""

import json
import struct

import numpy as np

MAGIC = b"GPU3"
VERSION = 1

# numpy dtype -> short tag stored in the manifest, so the C# reader knows how
# wide each slice is without guessing from the field name.
_TAGS = {
    np.dtype(np.float32): "f32",
    np.dtype(np.int32): "i32",
    np.dtype(np.uint8): "u8",
}


class Blob:
    """Accumulates typed arrays and hands back manifest slices."""

    def __init__(self):
        self._parts = []
        self._size = 0

    def add(self, array, dtype):
        """Append ``array`` as ``dtype``; return the manifest slice for it."""
        arr = np.ascontiguousarray(array, dtype=dtype)
        tag = _TAGS[arr.dtype]
        # Everything stays 4-byte aligned so the C# reader can blit.
        pad = (-self._size) % 4
        if pad:
            self._parts.append(b"\x00" * pad)
            self._size += pad
        slice_ = {"offset": self._size, "count": int(arr.size), "type": tag}
        data = arr.tobytes()
        self._parts.append(data)
        self._size += len(data)
        return slice_

    def to_bytes(self):
        return b"".join(self._parts)

    def __len__(self):
        return self._size


def write(path, manifest, blob):
    """Write the container to ``path``."""
    text = json.dumps(manifest, separators=(",", ":")).encode("utf-8")
    json_pad = (-len(text)) % 4
    blob_offset = 20 + len(text) + json_pad
    payload = blob.to_bytes()
    with open(path, "wb") as fh:
        fh.write(MAGIC)
        fh.write(struct.pack("<IIII", VERSION, len(text), blob_offset, len(payload)))
        fh.write(text)
        fh.write(b"\x00" * json_pad)
        fh.write(payload)
    return 20 + len(text) + json_pad + len(payload)


def read(path):
    """Read a container back; returns ``(manifest, memoryview)``.  Used by tests."""
    with open(path, "rb") as fh:
        data = fh.read()
    if data[:4] != MAGIC:
        raise ValueError("not a .gpencil file: bad magic %r" % data[:4])
    version, json_len, blob_offset, blob_len = struct.unpack("<IIII", data[4:20])
    if version != VERSION:
        raise ValueError("unsupported .gpencil version %d" % version)
    manifest = json.loads(data[20:20 + json_len].decode("utf-8"))
    return manifest, memoryview(data)[blob_offset:blob_offset + blob_len]


def unpack(blob, slice_):
    """Materialise a manifest slice from ``blob`` as a numpy array."""
    dtype = {"f32": np.float32, "i32": np.int32, "u8": np.uint8}[slice_["type"]]
    width = np.dtype(dtype).itemsize
    start = slice_["offset"]
    return np.frombuffer(blob, dtype=dtype, count=slice_["count"], offset=start)
