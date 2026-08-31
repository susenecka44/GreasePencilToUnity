"""Assembling a ``.gpencil`` file from a Grease Pencil object."""

import hashlib
import json
import os

import numpy as np

from . import baker, convert, fills, gp_reader, writer

# Per-curve flag bits, mirrored by GpCurveFlags on the Unity side.
FLAG_CYCLIC = 1
FLAG_STROKE = 2
FLAG_FILL = 4
FLAG_START_FLAT = 8
FLAG_END_FLAT = 16

# Sentinel frame entry for a layer with nothing drawn yet.
EMPTY_DRAWING = -1


class _DrawingStore:
    """Writes prepared drawings into the blob, deduplicated by content."""

    def __init__(self, blob):
        self._blob = blob
        self._seen = {}
        self.entries = []

    def add(self, prepared):
        digest = prepared["hash"]
        hit = self._seen.get(digest)
        if hit is not None:
            return hit

        index = len(self.entries)
        self._seen[digest] = index
        add = self._blob.add
        self.entries.append({
            "n_points": int(prepared["n_points"]),
            "n_curves": int(prepared["n_curves"]),
            "points": {
                "position": add(prepared["position"], np.float32),
                "radius": add(prepared["radius"], np.float32),
                "color": add(prepared["color"], np.float32),
            },
            "curves": {
                "offsets": add(prepared["offsets"], np.int32),
                "material": add(prepared["material"], np.int32),
                "flags": add(prepared["flags"], np.uint8),
                "softness": add(prepared["softness"], np.float32),
                "fill_color": add(prepared["fill_color"], np.float32),
                "fill_tri_range": add(prepared["fill_tri_range"], np.int32),
            },
            "fill_tris": add(prepared["fill_tris"], np.int32),
        })
        return index


def prepare_drawing(drawing, materials, layer_style, export_fills):
    """Read one drawing and fold it into the arrays the container stores."""
    data = gp_reader.read_drawing(drawing)
    n_curves = data["n_curves"]

    position = convert.points_to_unity(data["position"])
    color = gp_reader.bake_point_colors(data, materials, layer_style)
    radius = gp_reader.baked_radius(data, layer_style)
    fill_color = gp_reader.bake_fill_colors(data, materials, layer_style)

    material_index = np.clip(data["material_index"], 0, max(len(materials) - 1, 0))
    show_stroke = np.array([m["show_stroke"] for m in materials] or [True], dtype=bool)
    show_fill = np.array([m["show_fill"] for m in materials] or [False], dtype=bool)
    draws_stroke = show_stroke[material_index] if n_curves else np.zeros(0, dtype=bool)
    draws_fill = show_fill[material_index] if n_curves else np.zeros(0, dtype=bool)
    if not export_fills:
        draws_fill = np.zeros(n_curves, dtype=bool)

    tris, tri_range = fills.triangulate_drawing(data, position, draws_fill)
    # A curve whose fill produced no triangles must not claim a fill submesh.
    draws_fill = draws_fill & (tri_range[:, 1] > 0)

    flags = np.zeros(n_curves, dtype=np.uint8)
    flags |= data["cyclic"].astype(np.uint8) * FLAG_CYCLIC
    flags |= draws_stroke.astype(np.uint8) * FLAG_STROKE
    flags |= draws_fill.astype(np.uint8) * FLAG_FILL
    flags |= (data["start_cap"] == gp_reader.CAP_FLAT).astype(np.uint8) * FLAG_START_FLAT
    flags |= (data["end_cap"] == gp_reader.CAP_FLAT).astype(np.uint8) * FLAG_END_FLAT

    prepared = {
        "n_points": data["n_points"],
        "n_curves": n_curves,
        "position": position,
        "radius": radius,
        "color": color,
        "offsets": data["offsets"],
        "material": material_index,
        "flags": flags,
        "softness": data["softness"],
        "fill_color": fill_color,
        "fill_tris": tris,
        "fill_tri_range": tri_range,
    }
    prepared["hash"] = _hash(prepared)
    return prepared


def _hash(prepared):
    digest = hashlib.blake2b(digest_size=16)
    for key in ("position", "radius", "color", "offsets", "material", "flags",
                "softness", "fill_color", "fill_tris", "fill_tri_range"):
        digest.update(np.ascontiguousarray(prepared[key]).tobytes())
    return digest.digest()


def export_object(context, ob, filepath, settings, report=None):
    """Export one Grease Pencil object.  Returns a stats dict."""
    scene = context.scene
    materials = gp_reader.read_materials(ob)

    source_layers = [
        (index, layer) for index, layer in enumerate(ob.data.layers)
        if settings.include_hidden_layers or not layer.hide
    ]
    styles = [gp_reader.read_layer_style(layer) for _, layer in source_layers]

    blob = writer.Blob()
    store = _DrawingStore(blob)
    tracks = [[] for _ in source_layers]
    # Last (keyframe number, drawing index) per layer, so holds collapse.
    held = [(None, None) for _ in source_layers]
    times = baker.sample_times(scene, ob, settings)
    matrices = []

    with baker.frame_scope(scene):
        for time, source in baker.iter_samples(context, ob, times, settings.apply_modifiers):
            matrices.append(convert.matrix_to_unity(source.matrix_world))
            for slot, (index, _) in enumerate(source_layers):
                style = styles[slot]
                layer = baker.find_layer(source, style["name"], index)
                frame = layer.get_frame_at(time) if layer is not None else None
                if frame is None or frame.drawing is None:
                    if held[slot][1] != EMPTY_DRAWING:
                        tracks[slot].append([time, EMPTY_DRAWING])
                        held[slot] = (None, EMPTY_DRAWING)
                    continue

                key = None if settings.apply_modifiers else int(frame.frame_number)
                if key is not None and key == held[slot][0]:
                    # Same keyframe still held, and with no modifiers running
                    # nothing else can have changed it.
                    continue

                drawing_index = store.add(
                    prepare_drawing(frame.drawing, materials, style, settings.export_fills)
                )
                if drawing_index != held[slot][1]:
                    tracks[slot].append([time, drawing_index])
                held[slot] = (key, drawing_index)

    # Unity parses the manifest with JsonUtility, which has no nested arrays and
    # no nulls, so tracks go out as parallel flat arrays and an absent slice is
    # an empty one rather than null.
    animation = {
        "mode": settings.animation_mode,
        "step": int(settings.frame_step),
        "times": [int(t) for t in times],
        "matrices": blob.add(
            np.asarray(matrices if len(matrices) > 1 else [], dtype=np.float32).reshape(-1),
            np.float32,
        ),
    }

    manifest = {
        "scene": {
            "fps": scene.render.fps / scene.render.fps_base,
            "frame_start": int(times[0]),
            "frame_end": int(times[-1]),
        },
        "object": {
            "name": ob.name,
            "matrix": matrices[0] if matrices else convert.matrix_to_unity(ob.matrix_world),
            "depth_order": ob.data.stroke_depth_order,
        },
        "materials": materials,
        "layers": [
            dict(style,
                 frame_times=[int(entry[0]) for entry in track],
                 frame_drawings=[int(entry[1]) for entry in track])
            for style, track in zip(styles, tracks)
        ],
        "drawings": store.entries,
        "animation": animation,
    }

    size = writer.write(filepath, manifest, blob)
    if settings.write_debug_json:
        debug_path = os.path.splitext(filepath)[0] + ".manifest.json"
        with open(debug_path, "w", encoding="utf-8") as fh:
            json.dump(manifest, fh, indent=1)

    stats = {
        "layers": len(source_layers),
        "drawings": len(store.entries),
        "samples": len(times),
        "points": sum(entry["n_points"] for entry in store.entries),
        "bytes": size,
    }
    if report is not None:
        report({"INFO"}, (
            "%s: %d layers, %d drawings over %d frames, %d points, %.1f KB"
            % (ob.name, stats["layers"], stats["drawings"], stats["samples"],
               stats["points"], size / 1024.0)
        ))
    return stats
