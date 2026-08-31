"""Blender -> Unity coordinate conversion.

Blender is right-handed Z-up, Unity is left-handed Y-up.  We use the mapping the
glTF/FBX pipeline ends up with, ``(x, y, z) -> (x, z, y)``, so Grease Pencil
objects land in the same place as meshes exported the usual way.

The mapping swaps handedness (its determinant is -1), so triangle winding is
reversed on write.  Matrices are converted as ``S @ M @ S``; ``S`` is the y/z
swap and is its own inverse.
"""

import numpy as np

# Row/column order that applies the y/z swap to a 4x4 matrix.
_SWAP = (0, 2, 1, 3)


def points_to_unity(arr):
    """(N, 3) float32 in Blender space -> new (N, 3) float32 in Unity space."""
    out = np.empty_like(arr)
    out[:, 0] = arr[:, 0]
    out[:, 1] = arr[:, 2]
    out[:, 2] = arr[:, 1]
    return out


def matrix_to_unity(m):
    """mathutils.Matrix -> list of 16 floats, row-major, in Unity space."""
    out = []
    for r in _SWAP:
        row = m[r]
        out.extend((row[0], row[2], row[1], row[3]))
    return out


def vector_to_unity(v):
    """Length-3 sequence in Blender space -> list of 3 floats in Unity space."""
    return [v[0], v[2], v[1]]
