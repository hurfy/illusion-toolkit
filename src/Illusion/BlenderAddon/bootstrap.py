"""Launch-time bootstrap for the Illusion Bridge addon.

Run as: blender --python bootstrap.py, with the BLENDER_USER_SCRIPTS environment
variable pointing at the sibling "scripts" folder so Blender can discover the
illusion_bridge addon without installing it.
"""

import bpy

REQUIRED_VERSION = (4, 2, 0)


def _version_popup():
    """One-shot timer callback: tell the user their Blender is too old."""

    def draw(menu, _context):
        menu.layout.label(text="Illusion Bridge requires Blender 4.2 or newer.")
        menu.layout.label(text=f"This is Blender {bpy.app.version_string}.")

    try:
        bpy.context.window_manager.popup_menu(draw, title="Illusion Bridge", icon='ERROR')
    except Exception:
        pass  # headless or no window yet; the stdout error already said it all
    return None  # returning None unregisters the timer


def main():
    if bpy.app.version < REQUIRED_VERSION:
        print(
            "[illusion_bridge] ERROR: Blender "
            f"{bpy.app.version_string} is too old; 4.2 or newer is required."
        )
        bpy.app.timers.register(_version_popup, first_interval=0.5)
        return

    import addon_utils

    module = addon_utils.enable("illusion_bridge", default_set=False)
    if module is None:
        print(
            "[illusion_bridge] ERROR: failed to enable the addon; is "
            "BLENDER_USER_SCRIPTS pointing at the bridge 'scripts' folder?"
        )
        return

    version = ".".join(str(n) for n in module.bl_info.get("version", ()))
    print(f"[illusion_bridge] Addon {version} enabled.")


main()
