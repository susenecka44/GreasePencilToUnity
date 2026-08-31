"""Reading Blender 5 Grease Pencil (GPv3) data.

``GreasePencilDrawing`` in Blender 5.0 exposes only ``curve_offsets`` and
``attributes`` -- there is no ``.strokes`` convenience API.  Optional attributes
(``vertex_color``, ``fill_color``, ``softness``, caps, ...) only exist on a
drawing once they hold a non-default value, so every read goes through
:func:`_read` which substitutes a constant array when the attribute is missing.

Nothing here calls ``bpy.ops`` or mutates the scene.
"""

import numpy as np

# Grease Pencil stroke caps, matching Blender's GP_STROKE_CAP_* enum.
CAP_ROUND = 0
CAP_FLAT = 1

# Blender's default when no radius attribute has ever been written.
DEFAULT_RADIUS = 0.01


def _read(attributes, name, domain, count, dim, dtype, default, prop="value"):
    """Fetch an attribute as a numpy array, or a constant array if it is absent.

    ``dim`` is the number of components per element; the result is shaped
    ``(count,)`` for scalars and ``(count, dim)`` otherwise.
    """
    shape = (count,) if dim == 1 else (count, dim)
    attr = attributes.get(name)
    if attr is None or attr.domain != domain or len(attr.data) != count:
        return np.full(shape, default, dtype=dtype)

    buf = np.empty(count * dim, dtype=dtype)
    try:
        attr.data.foreach_get(prop, buf)
    except (TypeError, RuntimeError):
        # foreach_get is picky about dtype widths for the small int types;
        # fall back to the slow path rather than dropping the data.
        values = [getattr(item, prop) for item in attr.data]
        if dim == 1:
            buf = np.asarray(values, dtype=dtype)
        else:
            buf = np.asarray([tuple(v) for v in values], dtype=dtype).reshape(-1)
    return buf.reshape(shape)


def read_drawing(drawing):
    """Read one ``GreasePencilDrawing`` into plain numpy arrays.

    Positions stay in Blender object space; the caller converts them.
    """
    attrs = drawing.attributes
    offsets = np.empty(len(drawing.curve_offsets), dtype=np.int32)
    drawing.curve_offsets.foreach_get("value", offsets)

    n_curves = max(len(offsets) - 1, 0)
    n_points = int(offsets[-1]) if len(offsets) else 0

    return {
        "offsets": offsets,
        "n_curves": n_curves,
        "n_points": n_points,
        # Point domain.
        "position": _read(attrs, "position", "POINT", n_points, 3, np.float32, 0.0, "vector"),
        "radius": _read(attrs, "radius", "POINT", n_points, 1, np.float32, DEFAULT_RADIUS),
        "opacity": _read(attrs, "opacity", "POINT", n_points, 1, np.float32, 1.0),
        "vertex_color": _read(attrs, "vertex_color", "POINT", n_points, 4, np.float32, 0.0, "color"),
        # Curve domain.
        "cyclic": _read(attrs, "cyclic", "CURVE", n_curves, 1, bool, False),
        "material_index": _read(attrs, "material_index", "CURVE", n_curves, 1, np.int32, 0),
        "softness": _read(attrs, "softness", "CURVE", n_curves, 1, np.float32, 0.0),
        "start_cap": _read(attrs, "start_cap", "CURVE", n_curves, 1, np.uint8, CAP_ROUND),
        "end_cap": _read(attrs, "end_cap", "CURVE", n_curves, 1, np.uint8, CAP_ROUND),
        "fill_color": _read(attrs, "fill_color", "CURVE", n_curves, 4, np.float32, 0.0, "color"),
        "fill_opacity": _read(attrs, "fill_opacity", "CURVE", n_curves, 1, np.float32, 1.0),
    }


def read_materials(ob):
    """Read the object's Grease Pencil material slots into manifest dicts."""
    materials = []
    for slot in ob.material_slots:
        mat = slot.material
        if mat is None or mat.grease_pencil is None:
            # Empty slot: keep the index aligned with material_index.
            materials.append({
                "name": "Missing",
                "show_stroke": True,
                "show_fill": False,
                "stroke_color": [1.0, 0.0, 1.0, 1.0],
                "fill_color": [0.0, 0.0, 0.0, 0.0],
                "mode": "LINE",
                "holdout": False,
            })
            continue
        gp = mat.grease_pencil
        materials.append({
            "name": mat.name,
            "show_stroke": bool(gp.show_stroke),
            "show_fill": bool(gp.show_fill),
            "stroke_color": list(gp.color),
            "fill_color": list(gp.fill_color),
            "mode": gp.mode,
            "holdout": bool(gp.use_stroke_holdout or gp.use_fill_holdout),
        })
    return materials


def read_layer_style(layer):
    """Read the presentation settings of a ``GreasePencilLayer``."""
    return {
        "name": layer.name,
        "opacity": float(layer.opacity),
        "blend_mode": layer.blend_mode,
        "tint_color": list(layer.tint_color),
        "tint_factor": float(layer.tint_factor),
        "radius_offset": float(layer.radius_offset),
        "use_lights": bool(layer.use_lights),
        "hide": bool(layer.hide),
    }


def bake_point_colors(data, materials, layer_style):
    """Fold material, per-point vertex colour, opacity and layer tint into RGBA.

    Blender mixes the material stroke colour with the point's ``vertex_color``
    using that colour's alpha as the mix factor, then the layer applies its tint
    and opacity on top.  Baking it here is what keeps Unity's colours identical
    without reimplementing the blend chain in the shader.
    """
    n_points = data["n_points"]
    out = np.zeros((n_points, 4), dtype=np.float32)
    if n_points == 0:
        return out

    # Per-point material colour, expanded from the per-curve material index.
    stroke_colors = np.array(
        [m["stroke_color"] for m in materials] or [[1.0, 1.0, 1.0, 1.0]],
        dtype=np.float32,
    )
    idx = np.clip(data["material_index"], 0, len(stroke_colors) - 1)
    per_point_mat = stroke_colors[np.repeat(idx, np.diff(data["offsets"]))]

    vcol = data["vertex_color"]
    factor = vcol[:, 3:4]
    out[:, :3] = per_point_mat[:, :3] * (1.0 - factor) + vcol[:, :3] * factor
    out[:, 3] = per_point_mat[:, 3] * data["opacity"] * layer_style["opacity"]

    _apply_tint(out, layer_style)
    return out


def bake_fill_colors(data, materials, layer_style):
    """Same fold for per-curve fill colours."""
    n_curves = data["n_curves"]
    out = np.zeros((n_curves, 4), dtype=np.float32)
    if n_curves == 0:
        return out

    fill_colors = np.array(
        [m["fill_color"] for m in materials] or [[1.0, 1.0, 1.0, 1.0]],
        dtype=np.float32,
    )
    idx = np.clip(data["material_index"], 0, len(fill_colors) - 1)
    per_curve_mat = fill_colors[idx]

    fcol = data["fill_color"]
    factor = fcol[:, 3:4]
    out[:, :3] = per_curve_mat[:, :3] * (1.0 - factor) + fcol[:, :3] * factor
    out[:, 3] = per_curve_mat[:, 3] * data["fill_opacity"] * layer_style["opacity"]

    _apply_tint(out, layer_style)
    return out


def _apply_tint(colors, layer_style):
    tint_factor = layer_style["tint_factor"]
    if tint_factor <= 0.0:
        return
    tint = np.array(layer_style["tint_color"][:3], dtype=np.float32)
    colors[:, :3] += (tint - colors[:, :3]) * tint_factor


def baked_radius(data, layer_style):
    """Point radius in world units, including the layer's radius offset."""
    return np.maximum(data["radius"] + layer_style["radius_offset"], 0.0)
