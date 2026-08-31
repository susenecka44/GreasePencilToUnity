"""Viewport side panel."""

import bpy

from .operators import EXPORT_SCENE_OT_grease_pencil_unity, grease_pencil_objects


class VIEW3D_PT_grease_pencil_to_unity(bpy.types.Panel):
    bl_label = "Grease Pencil to Unity"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "GP Export"

    def draw(self, context):
        layout = self.layout
        selected = grease_pencil_objects(context, True)

        box = layout.box()
        if selected:
            box.label(text="%d object(s) selected" % len(selected), icon="OUTLINER_OB_GREASEPENCIL")
            for ob in selected[:4]:
                box.label(text=ob.name, icon="DOT")
            if len(selected) > 4:
                box.label(text="and %d more" % (len(selected) - 4))
        else:
            box.label(text="Select a Grease Pencil object", icon="INFO")

        column = layout.column()
        column.enabled = bool(selected)
        column.scale_y = 1.4
        column.operator(EXPORT_SCENE_OT_grease_pencil_unity.bl_idname, text="Export", icon="EXPORT")
        layout.label(text="Animation and bake options", icon="PREFERENCES")
        layout.label(text="are in the file browser sidebar")


classes = (VIEW3D_PT_grease_pencil_to_unity,)
