"""Main-thread push export: reads the evaluated bridge meshes back and sends a push.

The payload mirrors what the toolkit sent: welded vertices + per-loop attributes,
UVs in Blender's V convention (the toolkit un-flips), _orig_index riding along as
the vertex identity the count-preserving apply keys on. Modifiers are baked
(evaluated depsgraph) and n-gons triangulated via loop_triangles.
"""

import json
import os
import uuid

import bpy
import numpy as np

from . import importer, payload, protocol, server


def _guess_kind(obj) -> str:
    """What a brand-new object should be sent back as.

    A collision material carries illusion_collision_raw_id and a game material does not, so an object
    built from COL materials is a collision hull — which is exactly what a Shift+D of one looks like
    once its id has been re-minted. Anything else is an ordinary mesh.
    """
    for slot in obj.material_slots:
        if slot.material is not None and slot.material.get("illusion_collision_raw_id") is not None:
            return "collision"
    return "mesh"


def _remint_duplicate_ids(session):
    """Give every object after the first sharing an id a fresh 'new:' one. Returns how many were re-minted."""
    seen = set()
    reminted = 0
    for obj in bpy.data.objects:
        if obj.type != 'MESH':
            continue
        current = obj.get(importer.ID_PROP)
        if current is None:
            continue
        if current not in seen:
            seen.add(current)
            continue
        obj[importer.ID_PROP] = "new:" + uuid.uuid4().hex
        obj[importer.SESSION_PROP] = session
        # The kind is INHERITED where it exists: a duplicated hull keeps its materials, so re-deriving
        # would give the same answer anyway, but an object opened as collision stays collision even if
        # its materials were swapped afterwards.
        if importer.KIND_PROP not in obj.keys():
            obj[importer.KIND_PROP] = _guess_kind(obj)
        obj["illusion_meta"] = "{}"
        reminted += 1
    return reminted


def export_scene(reason):
    """Export every surviving bridge object and send one push message.

    Returns (pushed_count, deleted_count, new_count). Raises on a hard failure
    (no bridge scene, no exchange directory); per-object problems are printed
    and the object is left out of the push.
    """
    exchange_dir = server.state.get("exchange_dir")
    if not exchange_dir or not os.path.isdir(exchange_dir):
        raise RuntimeError("No bridge scene was loaded in this session")
    session = server.state.get("session", "")
    server.log(f"export_scene({reason}) start")

    # A mesh dropped into the bridge collection without an id is a NEW object: mint it a
    # "new:" id so the toolkit creates a frame object for it, and track it from now on.
    collection = bpy.data.collections.get(importer.COLLECTION_NAME)
    new_count = 0
    if collection is not None:
        for obj in collection.objects:
            if obj.type == 'MESH' and importer.ID_PROP not in obj.keys():
                obj[importer.ID_PROP] = "new:" + uuid.uuid4().hex
                obj[importer.SESSION_PROP] = session
                obj[importer.KIND_PROP] = _guess_kind(obj)
                obj["illusion_meta"] = "{}"
                new_count += 1

    # Shift+D is how anyone makes a second one of something, and Blender's duplicate copies custom
    # properties — so the copy arrives wearing the ORIGINAL's id. Left alone, both objects resolve to
    # the same placement and the second silently overwrites the first. Whoever holds the id longest is
    # arbitrary, so keep the first occurrence and re-mint the rest as new objects, preserving the kind
    # they inherited: a duplicated collision hull is still a collision hull.
    new_count += _remint_duplicate_ids(session)

    survivors = [o for o in bpy.data.objects
                 if importer.ID_PROP in o.keys() and o.type == 'MESH']
    present_ids = {o[importer.ID_PROP] for o in survivors}
    deleted = sorted(server.state.get("loaded_ids", set()) - present_ids)

    server.log(f"export_scene: {len(survivors)} survivors, evaluating depsgraph")
    depsgraph = bpy.context.evaluated_depsgraph_get()
    server.log("export_scene: depsgraph ready")
    objects = []
    blocks = []
    pushed_ids = []
    for obj in survivors:
        try:
            server.log(f"export_scene: exporting {obj.name}")
            entry = _export_object(obj, depsgraph, blocks)
        except Exception as exc:
            server.log(f"push: failed to export '{obj.get(importer.ID_PROP, obj.name)}': {exc}")
            continue
        objects.append(entry)
        pushed_ids.append(entry["id"])

    if not pushed_ids and not deleted and new_count == 0:
        server.log("export_scene: nothing to push")
        return 0, 0, 0

    counter = server.state.get("push_counter", 0) + 1
    server.state["push_counter"] = counter
    path = os.path.join(exchange_dir, f"push_{counter:04d}.ilx")
    server.log("export_scene: writing container")
    payload.write_container(path, session, objects, blocks)
    server.log("export_scene: container written, sending push")

    server.send(protocol.make(
        protocol.PUSH,
        file=path,
        reason=reason,
        objects=pushed_ids,
        deleted=list(deleted),
        newObjects=new_count))
    server.log("export_scene: push sent")
    # Deletions are reported exactly once — the toolkit acts on this push, so the
    # baseline forgets them (re-sending would delete-fail forever after).
    server.state["loaded_ids"] = present_ids
    return len(pushed_ids), len(deleted), new_count


def _export_object(obj, depsgraph, blocks):
    """Read one evaluated mesh into payload arrays; appends blocks, returns the header entry."""
    if obj.mode == 'EDIT':
        obj.update_from_editmode()  # commit the live BMesh before evaluating

    ob_eval = obj.evaluated_get(depsgraph)
    me = ob_eval.to_mesh()
    try:
        me.calc_loop_triangles()
        tris = me.loop_triangles
        n_tris = len(tris)
        n_loops = len(me.loops)
        n_verts = len(me.vertices)

        tri_loops = np.empty(n_tris * 3, dtype=np.int32)
        tris.foreach_get("loops", tri_loops)
        tri_polys = np.empty(n_tris, dtype=np.int32)
        tris.foreach_get("polygon_index", tri_polys)

        positions = np.empty(n_verts * 3, dtype=np.float32)
        me.vertices.foreach_get("co", positions)
        positions = positions.reshape(n_verts, 3)

        loop_vi = np.empty(n_loops, dtype=np.int32)
        me.loops.foreach_get("vertex_index", loop_vi)

        # Split normals per corner (4.1+); fall back to vertex normals when absent.
        loop_normals = np.empty(n_loops * 3, dtype=np.float32)
        try:
            me.corner_normals.foreach_get("vector", loop_normals)
            loop_normals = loop_normals.reshape(n_loops, 3)
        except (AttributeError, RuntimeError):
            vertex_normals = np.empty(n_verts * 3, dtype=np.float32)
            me.vertices.foreach_get("normal", vertex_normals)
            loop_normals = vertex_normals.reshape(n_verts, 3)[loop_vi]

        uv_layer = me.uv_layers.active
        if uv_layer is not None:
            loop_uv = np.empty(n_loops * 2, dtype=np.float32)
            uv_layer.data.foreach_get("uv", loop_uv)
            loop_uv = loop_uv.reshape(n_loops, 2)
        else:
            loop_uv = np.zeros((n_loops, 2), dtype=np.float32)

        attr = me.attributes.get("_orig_index")
        if attr is not None and attr.domain == 'CORNER' and attr.data_type == 'INT':
            orig = np.empty(n_loops, dtype=np.int32)
            attr.data.foreach_get("value", orig)
        else:
            orig = np.full(n_loops, -1, dtype=np.int32)

        face_mats = np.zeros(len(me.polygons), dtype=np.int32)
        if len(me.polygons):
            me.polygons.foreach_get("material_index", face_mats)

        arrays = {
            "positions": _add_block(blocks, "f32", 3, n_verts, positions),
            "indices": _add_block(blocks, "u32", 1, n_tris * 3, loop_vi[tri_loops]),
            "loopNormals": _add_block(blocks, "f32", 3, n_tris * 3, loop_normals[tri_loops]),
            "loopUv0": _add_block(blocks, "f32", 2, n_tris * 3, loop_uv[tri_loops]),
            "origIndex": _add_block(blocks, "i32", 1, n_tris * 3, orig[tri_loops]),
            "faceMaterials": _add_block(blocks, "u16", 1, n_tris, face_mats[tri_polys]),
        }
    finally:
        ob_eval.to_mesh_clear()

    try:
        meta = json.loads(obj.get("illusion_meta", "") or "{}")
    except ValueError:
        meta = {}

    # The material SET is live state, not import-time state: the user may have re-pointed a slot
    # at another bridge material (or added/removed slots). Identity = the illusion_hash prop for a
    # game material, and illusion_collision_raw_id for a collision surface — a collision material
    # has no illusion_hash, so without the second field the surface a face was painted with is
    # simply lost on the way back and cannot be fed to the cooker.
    slot_materials = []
    for slot in obj.material_slots:
        material = slot.material
        entry = {
            "hash": (material.get("illusion_hash") if material else None) or None,
            "name": material.name if material else None,
        }
        # Spelled "rawId" so it lands straight back in CollisionMaterialInfo.RawId — the same field the
        # toolkit sent out. Absent for ordinary game materials, and a collision surface id is never 0
        # (the .col section bias subtracts 2), so 0 reads unambiguously as "not a collision surface".
        raw_id = material.get("illusion_collision_raw_id") if material else None
        if raw_id is not None:
            entry["rawId"] = int(raw_id)
        slot_materials.append(entry)
    if slot_materials:
        meta["materials"] = slot_materials

    return {
        # Echo the kind stamped at import. Hard-coding "mesh" here would send a collision hull
        # back as a mesh, and the toolkit would route it into the geometry-apply path that
        # cannot handle it.
        "kind": obj.get(importer.KIND_PROP, "mesh"),
        "id": obj[importer.ID_PROP],
        "name": obj.name,
        "parentId": None,
        "world": _row_major(obj.matrix_world),
        "local": _row_major(obj.matrix_local),
        "meta": meta,
        "arrays": arrays,
    }


def _add_block(blocks, dtype_str, components, count, data):
    blocks.append((dtype_str, components, count, data))
    return len(blocks) - 1


def _row_major(matrix):
    """Blender column-vector Matrix → the toolkit's 16 row-vector floats."""
    t = matrix.transposed()
    return [value for row in t for value in row]
