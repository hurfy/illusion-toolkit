"""DDS post-load fixes for game textures."""

import numpy as np


def unswizzle_dxt5nm(image):
    """Rebuild a tangent normal map from DXT5nm swizzle (x in A, y in G) in place.

    Idempotent: the image is tagged with a custom property after processing so
    a cached (check_existing) load is never unswizzled twice.
    """
    if image.get("illusion_dxt5nm"):
        return
    n = len(image.pixels)
    if n == 0 or n % 4 != 0:
        return  # failed load or non-RGBA layout; leave untouched

    buf = np.empty(n, dtype=np.float32)
    image.pixels.foreach_get(buf)
    rgba = buf.reshape(-1, 4)

    x = rgba[:, 3] * 2.0 - 1.0
    y = rgba[:, 1] * 2.0 - 1.0
    z = np.sqrt(np.clip(1.0 - x * x - y * y, 0.0, 1.0))

    rgba[:, 0] = (x + 1.0) * 0.5
    rgba[:, 1] = (y + 1.0) * 0.5
    rgba[:, 2] = (z + 1.0) * 0.5
    rgba[:, 3] = 1.0

    image.pixels.foreach_set(buf)
    image["illusion_dxt5nm"] = True
