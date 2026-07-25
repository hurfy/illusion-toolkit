"""Sidebar UI: bridge status, push action and options."""

import bpy

from . import exporter, importer, server


def _get_auto_push(_self):
    return server.state["auto_push"]


def _set_auto_push(_self, value):
    server.state["auto_push"] = bool(value)


class ILLUSION_OT_push(bpy.types.Operator):
    """Send edited bridge objects back to the Illusion Toolkit"""

    bl_idname = "illusion.push"
    bl_label = "Push to Illusion"

    def execute(self, context):
        if not server.is_connected():
            self.report({'WARNING'}, "The Illusion Toolkit is not connected")
            return {'CANCELLED'}
        try:
            pushed, deleted, new_count = exporter.export_scene("manual")
        except Exception as exc:
            self.report({'WARNING'}, str(exc))
            return {'CANCELLED'}
        extras = []
        if deleted:
            extras.append(f"{deleted} deleted")
        if new_count:
            extras.append(f"{new_count} new")
        suffix = f" ({', '.join(extras)})" if extras else ""
        self.report({'INFO'}, f"Pushed {pushed} object(s){suffix}")
        return {'FINISHED'}


class ILLUSION_PT_bridge(bpy.types.Panel):
    """Bridge connection status and actions."""

    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = "Illusion"
    bl_label = "Illusion Bridge"

    def draw(self, context):
        layout = self.layout

        if server.is_connected():
            session = server.owner_session() or "?"
            layout.label(text=f"Connected ({session[:8]})", icon='LINKED')
        else:
            layout.label(text="Not connected", icon='UNLINKED')

        collection = bpy.data.collections.get(importer.COLLECTION_NAME)
        count = len(collection.objects) if collection is not None else 0
        layout.label(text=f"Loaded objects: {count}")

        layout.operator(ILLUSION_OT_push.bl_idname, icon='EXPORT')
        layout.prop(context.window_manager, "illusion_auto_push")

        last_ack = server.state.get("last_push_ack")
        if last_ack:
            layout.label(text=last_ack, icon='INFO')


_CLASSES = (ILLUSION_OT_push, ILLUSION_PT_bridge)


def register():
    for cls in _CLASSES:
        bpy.utils.register_class(cls)
    bpy.types.WindowManager.illusion_auto_push = bpy.props.BoolProperty(
        name="Auto-push on Tab",
        description="Push automatically when leaving Edit Mode (applied in a later phase)",
        get=_get_auto_push,
        set=_set_auto_push,
    )


def unregister():
    if hasattr(bpy.types.WindowManager, "illusion_auto_push"):
        del bpy.types.WindowManager.illusion_auto_push
    for cls in reversed(_CLASSES):
        try:
            bpy.utils.unregister_class(cls)
        except RuntimeError:
            pass
