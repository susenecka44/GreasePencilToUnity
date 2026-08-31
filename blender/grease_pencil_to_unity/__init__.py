"""Grease Pencil to Unity -- export GPv3 strokes, widths and colours."""

import bpy

from . import operators, ui

_classes = operators.classes + ui.classes


def register():
    for cls in _classes:
        bpy.utils.register_class(cls)
    bpy.types.TOPBAR_MT_file_export.append(operators.menu_func_export)


def unregister():
    bpy.types.TOPBAR_MT_file_export.remove(operators.menu_func_export)
    for cls in reversed(_classes):
        bpy.utils.unregister_class(cls)
