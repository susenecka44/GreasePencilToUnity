"""Choosing which frames to export, and evaluating the scene at each of them.

Three modes share one sampling loop; they differ only in which times they visit:

``NONE``       the current frame only.
``KEYFRAMES``  the union of the layers' own Grease Pencil keyframes.  Small
               files, exact for hand-drawn animation.
``BAKE``       every frame in the range (with a step), so modifiers, armatures,
               drivers and geometry nodes are captured.

``layer.get_frame_at()`` holds the last keyframe at or before the requested
time, which is what Grease Pencil does when it draws, so sampling never
interpolates between drawings.
"""

from contextlib import contextmanager

MODES = ("NONE", "KEYFRAMES", "BAKE")


def sample_times(scene, ob, settings):
    """The frame numbers to visit, ascending."""
    mode = settings.animation_mode
    if mode == "NONE":
        return [scene.frame_current]

    start = int(settings.frame_start)
    end = max(int(settings.frame_end), start)

    if mode == "KEYFRAMES":
        times = {
            int(frame.frame_number)
            for layer in ob.data.layers
            for frame in layer.frames
            if start <= frame.frame_number <= end
        }
        # Always sample the first frame so the clip starts on the drawing that
        # is being held at that point, even if its keyframe is earlier.
        times.add(start)
        return sorted(times)

    return list(range(start, end + 1, max(1, int(settings.frame_step))))


@contextmanager
def frame_scope(scene):
    """Restore the scene's current frame however the export ends."""
    saved = scene.frame_current
    try:
        yield
    finally:
        scene.frame_set(saved)


def iter_samples(context, ob, times, apply_modifiers):
    """Yield ``(frame_number, source_object)`` for each sample time.

    The source object is the evaluated one when modifiers are applied, so its
    drawings already carry noise, armature deformation and the rest.  Either
    way its ``matrix_world`` is up to date for the frame.
    """
    scene = context.scene
    for time in times:
        scene.frame_set(time)
        if apply_modifiers:
            yield time, ob.evaluated_get(context.evaluated_depsgraph_get())
        else:
            yield time, ob


def find_layer(source_ob, name, index):
    """Match a layer on the (possibly evaluated) object; by name, then index."""
    layers = source_ob.data.layers
    layer = layers.get(name)
    if layer is not None:
        return layer
    return layers[index] if index < len(layers) else None
