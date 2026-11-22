"""Tileable worn-fabric textures (albedo + _normal + _rough [+ _metal]) for the Gus outfit.
python textures.py <out-dir>"""
import sys, pathlib, numpy as np
from PIL import Image

N = 1024


def noise(beta, seed, n=N, lo=0.0):
    """Periodic 1/f^beta noise in 0..1; `lo` cuts frequencies below it (cycles per tile) so no big blotches appear."""
    rng = np.random.default_rng(seed)
    f = np.fft.fftfreq(n) * n
    fx, fy = np.meshgrid(f, f)
    r = np.hypot(fx, fy)
    amp = np.where(r > max(lo, 0.5), 1.0 / np.maximum(r, 1) ** beta, 0.0)
    spec = amp * np.exp(2j * np.pi * rng.random((n, n)))
    img = np.fft.ifft2(spec).real
    img -= img.min()
    return img / img.max()


def grid(n=N):
    y, x = np.mgrid[0:n, 0:n]
    return x.astype(float), y.astype(float)


def twill(period=8, n=N):
    x, y = grid(n)
    diag = 0.5 + 0.5 * np.sin(2 * np.pi * (x + y) / period)
    yarn = 0.5 + 0.5 * np.sin(2 * np.pi * (x - y) / (period / 2))
    return 0.75 * diag ** 1.5 + 0.25 * yarn


def plain(period=6, n=N):
    x, y = grid(n)
    a = np.abs(np.sin(np.pi * x / period)); b = np.abs(np.sin(np.pi * y / period))
    check = ((x // period + y // period) % 2)
    return np.where(check > 0, a, b) * 0.9 + 0.1


def knit(period=8, n=N):
    x, y = grid(n)
    col = np.abs(((x / period) % 1) - 0.5) * 2
    return 0.5 + 0.5 * np.sin(2 * np.pi * (y / (period * 0.75) + col * 0.5)) * (0.4 + 0.6 * col)


def normal_map(height, strength):
    dx = (np.roll(height, -1, 1) - np.roll(height, 1, 1)) * strength
    dy = (np.roll(height, -1, 0) - np.roll(height, 1, 0)) * strength
    nz = np.ones_like(height)
    l = np.sqrt(dx * dx + dy * dy + nz * nz)
    return np.stack([(-dx / l) * 0.5 + 0.5, (dy / l) * 0.5 + 0.5, (nz / l) * 0.5 + 0.5], -1)


def save(path, arr):
    Image.fromarray((np.clip(arr, 0, 1) * 255).astype(np.uint8)).save(path)


def fabric(out, name, base, weave, seed, wear=0.18, grime=(0.30, 0.24, 0.17), grime_amt=0.25, bump=3.0, rough=0.86):
    """Fine weave + stippled grit + mid-frequency wear. No low-frequency blotches (they read as camouflage)."""
    fine = noise(0.6, seed, lo=40)
    mid = noise(1.2, seed + 1, lo=14)
    grit = noise(0.2, seed + 2, lo=120)
    height = 0.55 * weave + 0.3 * fine + 0.15 * grit
    base = np.array(base, float) / 255
    shade = 1 + 0.55 * (height - 0.5) + wear * (mid - 0.5) * 2
    alb = base[None, None, :] * shade[..., None]
    # faded high spots: lighter, slightly desaturated where the weave stands proud and the cloth is worn
    fade = np.clip((mid - 0.55) * 3, 0, 1) * np.clip(weave - 0.4, 0, 1) * wear * 1.6
    alb = alb + fade[..., None] * (0.55 - alb) * 0.5
    dirt = np.clip((noise(1.0, seed + 3, lo=16) - 0.52) * 4, 0, 1) * grime_amt * (0.6 + 0.4 * grit)
    alb = alb * (1 - dirt[..., None]) + np.array(grime)[None, None, :] * dirt[..., None]
    save(out / f"{name}.png", alb)
    save(out / f"{name}_normal.png", normal_map(height, bump))
    r = rough + 0.08 * (fine - 0.5) - 0.10 * fade + 0.06 * dirt
    save(out / f"{name}_rough.png", np.repeat(r[..., None], 3, -1))


def leather(out, name, base, seed):
    cells = noise(1.1, seed, lo=14)
    crease = np.clip(1 - np.abs(noise(1.4, seed + 1, lo=6) - 0.5) * 9, 0, 1)
    height = 0.6 * cells + 0.25 * noise(0.3, seed + 2, lo=90) - 0.5 * crease
    base = np.array(base, float) / 255
    alb = base[None, None, :] * (0.8 + 0.5 * (height - 0.2))[..., None]
    scuff = np.clip((noise(1.5, seed + 3, lo=5) - 0.6) * 5, 0, 1) * 0.35
    alb = alb + scuff[..., None] * (np.array([0.62, 0.5, 0.36]) - alb)
    save(out / f"{name}.png", alb)
    save(out / f"{name}_normal.png", normal_map(height, 4.0))
    save(out / f"{name}_rough.png", np.repeat((0.55 + 0.25 * scuff + 0.1 * (cells - 0.5))[..., None], 3, -1))


def metal(out, name, base, seed, rough=0.3):
    x, y = grid(256)
    brushed = noise(0.2, seed, 256, lo=30)
    brushed = np.repeat(brushed[:, :1], 256, 1) * 0.6 + brushed * 0.4
    base = np.array(base, float) / 255
    alb = base[None, None, :] * (0.85 + 0.3 * (brushed - 0.5))[..., None]
    save(out / f"{name}.png", alb)
    save(out / f"{name}_normal.png", normal_map(brushed, 1.2))
    save(out / f"{name}_rough.png", np.repeat((rough + 0.15 * (brushed - 0.5))[..., None], 3, -1))
    save(out / f"{name}_metal.png", np.ones((256, 256, 3)))


def knee_patch(out, name, base, seed, w=640, h=960):
    """Fit-to-piece knee panel (aspect matches the 17 x 26 cm piece): ground-in dirt towards the middle, stitched rim."""
    weave = twill(8, N)[:h, :w]
    fine = noise(0.6, seed, lo=40)[:h, :w]
    y, x = np.mgrid[0:h, 0:w]
    u, v = x / w, y / h
    blob = np.exp(-(((u - 0.5) / 0.36) ** 2 + ((v - 0.52) / 0.40) ** 2))
    dirt = np.clip(blob * (0.5 + 0.9 * noise(1.5, seed + 4, lo=4)[:h, :w]) - 0.25, 0, 1) * (0.7 + 0.3 * noise(0.2, seed + 5, lo=120)[:h, :w])
    height = 0.6 * weave + 0.4 * fine
    base = np.array(base, float) / 255
    alb = base[None, None, :] * (1 + 0.5 * (height - 0.5))[..., None]
    alb = alb * (1 - 0.75 * dirt[..., None]) + np.array([0.36, 0.28, 0.19])[None, None, :] * 0.75 * dirt[..., None]
    edge = np.minimum.reduce([x, y, w - 1 - x, h - 1 - y]).astype(float)
    alb *= (0.72 + 0.28 * np.clip(edge / 26, 0, 1))[..., None]          # worn, darker rim
    for inset in (16, 28):                                               # double-needle topstitch
        line = (np.abs(edge - inset) < 1.6) & (((x + y) // 9) % 2 == 0)
        alb[line] = alb[line] * 0.4 + np.array([0.55, 0.47, 0.34]) * 0.6
        height = height - 0.5 * (np.abs(edge - inset) < 3)
    save(out / f"{name}.png", alb)
    save(out / f"{name}_normal.png", normal_map(height, 3.0))
    save(out / f"{name}_rough.png", np.repeat((0.86 + 0.08 * dirt)[..., None], 3, -1))


def zipper(out, name, w=128, h=1536):
    """Fit-to-piece zip facing: fleece with a brass tooth track down one long edge (the right-hand one)."""
    y, x = np.mgrid[0:h, 0:w]
    fl = np.tile(noise(0.9, 7, lo=20), (2, 1))[:h, :w]
    alb = np.array([0.155, 0.135, 0.125])[None, None, :] * (0.8 + 0.5 * fl)[..., None]
    tape = x > w * 0.60
    alb[tape] = np.array([0.10, 0.09, 0.085])
    teeth = (x > w * 0.74) & ((y % 14) < 9)
    alb[teeth] = np.array([0.62, 0.47, 0.22]) * (0.8 + 0.3 * ((y[teeth] % 14) / 9))[:, None]
    metal_map = np.zeros((h, w, 3)); metal_map[teeth] = 1
    height = fl * 0.5; height[teeth] += 0.8
    save(out / f"{name}.png", alb)
    save(out / f"{name}_normal.png", normal_map(height, 3.0))
    r = np.full((h, w), 0.9); r[teeth] = 0.32
    save(out / f"{name}_rough.png", np.repeat(r[..., None], 3, -1))
    save(out / f"{name}_metal.png", metal_map)
    save(out / f"{name}_mirror.png", alb[:, ::-1])
    save(out / f"{name}_mirror_normal.png", normal_map(height[:, ::-1], 3.0))
    save(out / f"{name}_mirror_rough.png", np.repeat(r[:, ::-1][..., None], 3, -1))
    save(out / f"{name}_mirror_metal.png", metal_map[:, ::-1])


def main(out):
    out = pathlib.Path(out); out.mkdir(parents=True, exist_ok=True)
    fabric(out, "canvas_navy", (37, 41, 56), twill(8), 11, wear=0.14, grime_amt=0.22)
    fabric(out, "canvas_charcoal", (38, 40, 50), twill(8), 23, wear=0.14, grime_amt=0.30)
    fabric(out, "duck_brown", (60, 52, 46), twill(10), 31, wear=0.22, grime_amt=0.35)
    fabric(out, "jersey_black", (27, 26, 29), knit(6), 41, wear=0.08, grime_amt=0.08, bump=1.6, rough=0.9)
    fabric(out, "fleece_brown", (41, 35, 32), noise(0.8, 55, lo=30), 51, wear=0.12, grime_amt=0.1, bump=2.2, rough=0.95)
    fabric(out, "rag_red", (132, 60, 52), plain(8), 61, wear=0.25, grime=(0.2, 0.13, 0.1), grime_amt=0.45, bump=2.5, rough=0.92)
    fabric(out, "patch_tan", (172, 160, 138), plain(5), 71, wear=0.12, grime_amt=0.18, bump=2.0)
    leather(out, "leather_brown", (88, 64, 42), 81)
    metal(out, "brass", (176, 138, 72), 91, rough=0.30)
    metal(out, "steel", (150, 154, 160), 92, rough=0.26)
    knee_patch(out, "knee_panel", (60, 52, 46), 33)
    zipper(out, "zip_facing")
    print("textures ->", out)


if __name__ == "__main__":
    main(sys.argv[1])
