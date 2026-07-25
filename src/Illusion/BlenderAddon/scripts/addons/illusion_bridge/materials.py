"""Builds Blender materials from bridge material infos (Principled BSDF + maps)."""

import os

import bpy

from . import dds


def build(mat_info):
    """Return the material for one mesh material info dict, reusing by game-material hash.

    The game-material identity rides in the "illusion_hash" custom property — the
    push exporter reads it back, so re-assigning a slot to another bridge material
    in Blender translates into a real material change in the toolkit. Reuse must key
    on that hash, not the display name: the game ships thousands of materials across
    many libraries and names collide, which would silently point this slot at another
    material's datablock (and push back the wrong hash).
    """
    label = mat_info.get("name") or mat_info.get("hash") or "unnamed"
    mat_hash = mat_info.get("hash") or ""
    mat_name = f"M2 {label}"

    if mat_hash:
        for existing in bpy.data.materials:
            if existing.get("illusion_hash") == mat_hash:
                return existing
    # Legacy datablock from an older session (same name, no hash yet): adopt it once.
    existing = bpy.data.materials.get(mat_name)
    if existing is not None and not existing.get("illusion_hash"):
        existing["illusion_hash"] = mat_hash
        return existing
    if existing is not None and not mat_hash:
        return existing

    # A name collision with a different hash falls through: Blender auto-suffixes the
    # new datablock (.001) — identity stays with the hash, only the display name differs.
    mat = bpy.data.materials.new(mat_name)
    mat["illusion_hash"] = mat_hash
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    links = mat.node_tree.links
    principled = nodes.get("Principled BSDF")
    if principled is None:
        return mat  # unexpected default tree; bare material is still usable

    diffuse = _load_image(mat_info.get("diffuse"))
    if diffuse is not None:
        tex = nodes.new("ShaderNodeTexImage")
        tex.image = diffuse
        tex.location = (-500, 300)
        _link(links, tex.outputs.get("Color"), principled.inputs.get("Base Color"))

    normal = _load_image(mat_info.get("normal"), non_color=True)
    if normal is not None:
        if mat_info.get("normalIsDxt5nm"):
            dds.unswizzle_dxt5nm(normal)
        tex = nodes.new("ShaderNodeTexImage")
        tex.image = normal
        tex.location = (-700, -250)
        normal_map = nodes.new("ShaderNodeNormalMap")
        normal_map.location = (-400, -250)
        _link(links, tex.outputs.get("Color"), normal_map.inputs.get("Color"))
        _link(links, normal_map.outputs.get("Normal"), principled.inputs.get("Normal"))

    specular = _load_image(mat_info.get("specular"), non_color=True)
    if specular is not None:
        tex = nodes.new("ShaderNodeTexImage")
        tex.image = specular
        tex.location = (-500, 30)
        target = None
        for socket_name in ("Specular IOR Level", "Specular"):  # 4.x renamed it
            target = principled.inputs.get(socket_name)
            if target is not None:
                break
        _link(links, tex.outputs.get("Color"), target)

    return mat


def build_collision(mat_info):
    """Return the reference material for one collision surface, reusing by name.

    A collision material is a PhysX surface id, not a game material: there is nothing to
    texture and no hash to re-point, so the slot carries the surface token and the overlay
    colour for display only. Pushing a slot change back is meaningless and the toolkit
    ignores it.
    """
    label = mat_info.get("name") or mat_info.get("token") or "unknown"
    mat_name = f"COL {label}"
    existing = bpy.data.materials.get(mat_name)
    if existing is not None:
        return existing

    mat = bpy.data.materials.new(mat_name)
    mat["illusion_collision_raw_id"] = mat_info.get("rawId", -1)
    mat["illusion_collision_token"] = mat_info.get("token") or ""

    color = mat_info.get("color")
    if not isinstance(color, (list, tuple)) or len(color) < 3:
        color = (0.60, 0.62, 0.66)  # catalog's unknown-surface grey
    rgba = (float(color[0]), float(color[1]), float(color[2]), 1.0)

    mat.diffuse_color = rgba  # solid-shading viewport colour
    mat.use_nodes = True
    principled = mat.node_tree.nodes.get("Principled BSDF")
    if principled is not None:
        base = principled.inputs.get("Base Color")
        if base is not None:
            base.default_value = rgba
    return mat


def _load_image(path, non_color=False):
    """Load an image datablock; None when the path is absent or unreadable."""
    if not path or not os.path.isfile(path):
        return None
    try:
        image = bpy.data.images.load(path, check_existing=True)
    except RuntimeError:
        return None
    if non_color:
        image.colorspace_settings.name = 'Non-Color'
    return image


def _link(links, output, input_socket):
    if output is not None and input_socket is not None:
        links.new(output, input_socket)
