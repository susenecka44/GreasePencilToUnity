"""Triangulation of filled Grease Pencil strokes.

Blender fills a stroke by closing it into a polygon, whether or not the stroke
is flagged cyclic, so the triangulator here always treats the point run as
closed.  Strokes are near-planar in practice, so we fit a plane with the Newell
normal, project to 2D and hand the result to ``mathutils.geometry``.

Indices come back local to the drawing's point array, so fills share vertices
with their stroke and no positions are duplicated.
"""

import numpy as np
from mathutils import Vector
from mathutils.geometry import tessellate_polygon


def _newell_normal(pts):
    """Area-weighted normal of a closed polygon; robust to non-planarity."""
    nxt = np.roll(pts, -1, axis=0)
    return np.array([
        np.sum((pts[:, 1] - nxt[:, 1]) * (pts[:, 2] + nxt[:, 2])),
        np.sum((pts[:, 2] - nxt[:, 2]) * (pts[:, 0] + nxt[:, 0])),
        np.sum((pts[:, 0] - nxt[:, 0]) * (pts[:, 1] + nxt[:, 1])),
    ], dtype=np.float64)


def _plane_basis(normal):
    """Two orthonormal vectors spanning the plane perpendicular to ``normal``."""
    n = normal / np.linalg.norm(normal)
    # Pick the axis least aligned with n so the cross product stays well-conditioned.
    axis = np.zeros(3)
    axis[np.argmin(np.abs(n))] = 1.0
    u = np.cross(n, axis)
    u /= np.linalg.norm(u)
    return u, np.cross(n, u)


def triangulate_curve(points):
    """Triangulate one closed polygon given as an (N, 3) array.

    Returns an (M, 3) int32 array of indices local to ``points``, already
    wound for Unity (the axis conversion flips handedness).
    """
    n_points = len(points)
    if n_points < 3:
        return np.zeros((0, 3), dtype=np.int32)

    pts = np.asarray(points, dtype=np.float64)
    normal = _newell_normal(pts)
    length = np.linalg.norm(normal)
    if length < 1e-12:
        # Collinear or zero-area: nothing sensible to fill.
        return np.zeros((0, 3), dtype=np.int32)

    u, v = _plane_basis(normal)
    origin = pts[0]
    rel = pts - origin
    flat = [Vector((float(rel[i] @ u), float(rel[i] @ v))) for i in range(n_points)]

    try:
        tris = tessellate_polygon([flat])
    except (RuntimeError, ValueError):
        return np.zeros((0, 3), dtype=np.int32)
    if not tris:
        return np.zeros((0, 3), dtype=np.int32)

    out = np.asarray(tris, dtype=np.int32)
    # Reverse winding: the Blender -> Unity y/z swap mirrors handedness.
    return out[:, ::-1].copy()


def triangulate_drawing(data, positions, fillable):
    """Triangulate every fillable curve of a drawing.

    ``fillable`` is a per-curve boolean mask (material has ``show_fill``).
    Returns ``(tris, per_curve_slices)`` where ``tris`` is a flat (M, 3) index
    array into the drawing's points and ``per_curve_slices`` records
    ``(first_tri, tri_count)`` per curve so Unity can colour each fill.
    """
    offsets = data["offsets"]
    chunks = []
    slices = np.zeros((data["n_curves"], 2), dtype=np.int32)
    total = 0

    for c in range(data["n_curves"]):
        if not fillable[c]:
            slices[c] = (total, 0)
            continue
        first, last = int(offsets[c]), int(offsets[c + 1])
        tris = triangulate_curve(positions[first:last])
        slices[c] = (total, len(tris))
        if len(tris):
            chunks.append(tris + first)
            total += len(tris)

    if not chunks:
        return np.zeros((0, 3), dtype=np.int32), slices
    return np.concatenate(chunks, axis=0), slices
