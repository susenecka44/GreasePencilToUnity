"""The export operator -- the add-on's export button."""

import os

import bpy
from bpy.props import BoolProperty, EnumProperty, IntProperty
from bpy_extras.io_utils import ExportHelper

from . import exporter

ANIMATION_ITEMS = (
    ("NONE", "None", "Export the current frame only", 0),
    ("KEYFRAMES", "Keyframes",
     "Export each layer's own Grease Pencil keyframes. Small files, exact for "
     "hand-drawn animation", 1),
    ("BAKE", "Bake",
     "Sample every frame in the range so modifiers, armatures and drivers are "
     "captured. Identical drawings are deduplicated", 2),
)


def grease_pencil_objects(context, selected_only):
    source = context.selected_objects if selected_only else context.scene.objects
    return [ob for ob in source if ob.type == "GREASEPENCIL"]


class EXPORT_SCENE_OT_grease_pencil_unity(bpy.types.Operator, ExportHelper):
    """Export Grease Pencil strokes, widths and colours for Unity"""

    bl_idname = "export_scene.grease_pencil_unity"
    bl_label = "Export Grease Pencil to Unity"
    bl_options = {"REGISTER", "PRESET"}

    filename_ext = ".gpencil"
    filter_glob: bpy.props.StringProperty(default="*.gpencil", options={"HIDDEN"})

    use_selection: BoolProperty(
        name="Selected Only",
        description="Export only the selected Grease Pencil objects",
        default=True,
    )
    animation_mode: EnumProperty(
        name="Animation",
        description="Which frames to export",
        items=ANIMATION_ITEMS,
        default="KEYFRAMES",
    )
    use_scene_range: BoolProperty(
        name="Scene Frame Range",
        description="Use the scene's frame range instead of the one below",
        default=True,
    )
    frame_start: IntProperty(name="Start", default=1)
    frame_end: IntProperty(name="End", default=250)
    frame_step: IntProperty(name="Step", default=1, min=1, soft_max=10)
    apply_modifiers: BoolProperty(
        name="Apply Modifiers",
        description="Export the evaluated result, including modifiers, armatures "
                    "and geometry nodes. Required for deformation to show up",
        default=True,
    )
    export_fills: BoolProperty(
        name="Fills",
        description="Triangulate strokes whose material has Show Fill",
        default=True,
    )
    include_hidden_layers: BoolProperty(
        name="Hidden Layers",
        description="Also export layers hidden in the layer list",
        default=False,
    )
    write_debug_json: BoolProperty(
        name="Debug Manifest",
        description="Write the manifest next to the export as readable JSON",
        default=False,
    )

    def invoke(self, context, event):
        self.frame_start = context.scene.frame_start
        self.frame_end = context.scene.frame_end
        if not self.filepath:
            objects = grease_pencil_objects(context, self.use_selection)
            name = objects[0].name if objects else "grease_pencil"
            self.filepath = bpy.path.ensure_ext(name, self.filename_ext)
        return ExportHelper.invoke(self, context, event)

    def draw(self, context):
        layout = self.layout
        layout.use_property_split = True
        layout.use_property_decorate = False

        layout.prop(self, "use_selection")
        layout.prop(self, "export_fills")
        layout.prop(self, "include_hidden_layers")
        layout.prop(self, "apply_modifiers")

        layout.separator()
        layout.prop(self, "animation_mode")
        column = layout.column()
        column.enabled = self.animation_mode != "NONE"
        column.prop(self, "use_scene_range")
        row = column.column(align=True)
        row.enabled = not self.use_scene_range
        row.prop(self, "frame_start")
        row.prop(self, "frame_end")
        column.prop(self, "frame_step")

        layout.separator()
        layout.prop(self, "write_debug_json")

    def execute(self, context):
        objects = grease_pencil_objects(context, self.use_selection)
        if not objects:
            self.report({"ERROR"}, "No Grease Pencil object to export")
            return {"CANCELLED"}

        if self.use_scene_range:
            self.frame_start = context.scene.frame_start
            self.frame_end = context.scene.frame_end

        directory = os.path.dirname(self.filepath)
        window = context.window_manager
        window.progress_begin(0, len(objects))
        try:
            for index, ob in enumerate(objects):
                window.progress_update(index)
                if len(objects) == 1:
                    path = self.filepath
                else:
                    # One file per object, named after the object.
                    path = os.path.join(directory, bpy.path.clean_name(ob.name) + self.filename_ext)
                try:
                    exporter.export_object(context, ob, path, self, report=self.report)
                except Exception as error:  # noqa: BLE001 - surfaced in the UI
                    self.report({"ERROR"}, "%s: %s" % (ob.name, error))
                    raise
        finally:
            window.progress_end()

        return {"FINISHED"}


def menu_func_export(self, context):
    self.layout.operator(
        EXPORT_SCENE_OT_grease_pencil_unity.bl_idname,
        text="Grease Pencil for Unity (.gpencil)",
    )


classes = (EXPORT_SCENE_OT_grease_pencil_unity,)
