"""Paints one texture per pattern piece (fit-to-piece, repeat 0): the weave at its real size, double-needle
topstitching and darker, worn edges that follow the piece's own outline, and dust where a mechanic's clothes get it.
Used by build.py; can also be run on a saved project:  python paint.py <project.garment.json> <out-dir>"""
import json, math, pathlib, sys
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

PX = 18.0            # pixels per centimetre of cloth


def outline(piece, steps=14):
    """The piece's outline in pattern cm, curves flattened."""
    pts = piece["points"]
    out = []
    for i, p in enumerate(pts):
        a = np.array(p["at"], float)
        b = np.array(pts[(i + 1) % len(pts)]["at"], float)
        if "curve" in p:
            c = np.array(p["curve"], float)
            for t in np.linspace(0, 1, steps, endpoint=False):
                out.append((1 - t) ** 2 * a + 2 * t * (1 - t) * c + t * t * b)
        else:
            out.append(a)
    return np.array(out)


def noise(shape, beta, seed, lo=0.0):
    """1/f^beta noise in 0..1 of any size; `lo` cuts features larger than about size/lo."""
    h, w = shape
    n = 1 << int(math.ceil(math.log2(max(h, w))))
    rng = np.random.default_rng(seed)
    f = np.fft.fftfreq(n) * n
    fx, fy = np.meshgrid(f, f)
    r = np.hypot(fx, fy)
    amp = np.where(r > max(lo, 0.5), 1.0 / np.maximum(r, 1) ** beta, 0.0)
    img = np.fft.ifft2(amp * np.exp(2j * np.pi * rng.random((n, n)))).real[:h, :w]
    img -= img.min()
    return img / max(img.max(), 1e-9)


def edge_distance(mask, reach):
    """Distance in pixels from the outline, inside the piece, up to `reach` (plain erosion: no SciPy needed)."""
    img = Image.fromarray((mask * 255).astype(np.uint8))
    total = np.zeros(mask.shape, np.float32)
    for _ in range(int(reach)):
        img = img.filter(ImageFilter.MinFilter(3))
        total += np.asarray(img, np.float32) / 255.0
    return total


def normal_map(height, strength):
    dx = (np.roll(height, -1, 1) - np.roll(height, 1, 1)) * strength
    dy = (np.roll(height, -1, 0) - np.roll(height, 1, 0)) * strength
    l = np.sqrt(dx * dx + dy * dy + 1)
    return np.stack([(-dx / l) * 0.5 + 0.5, (dy / l) * 0.5 + 0.5, (1 / l) * 0.5 + 0.5], -1)


def save(path, arr):
    Image.fromarray((np.clip(arr, 0, 1) * 255).astype(np.uint8)).save(path)


WEAVES = {
    # name: (base colour, weave kind, thread period in cm, bump)
    # Warm charcoal: the concept's cloth reads about (58, 52, 54) lit, so the albedo sits well below that.
    "navy": ((29, 27, 30), "twill", 0.17, 1.2),
    "charcoal": ((28, 28, 32), "twill", 0.17, 1.2),
    "knee": ((25, 24, 26), "twill", 0.19, 1.4),
    "fleece": ((35, 31, 31), "fleece", 0.0, 1.5),
    "jersey": ((24, 21, 21), "fleece", 0.0, 0.7),
    "patch": ((118, 118, 116), "twill", 0.12, 0.6),
    "webbing": ((38, 34, 29), "twill", 0.28, 1.8),
    "rag": ((66, 32, 28), "fleece", 0.0, 1.6),
    "leather": ((58, 43, 30), "leather", 0.0, 3.0),
}


def paint(piece, out_dir, name, weave="navy", stitch=(0.55,), dust=0.3, dust_bands=(), seed=1, wash=0.45, edge_dark=0.30,
          thread=None, shade_top=0.0, lift=0.0, no_stitch=(), lines=(), zip_x=None, shade_bands=(), edge_dust=0.0):
    """shade_top: darken the top of the piece, as under a flap; lift: make the whole piece a little paler than the cloth
    it is sewn to, the way a patch pocket catches the light and the dust.
    dust_bands: [(y_cm, sigma_cm, strength)] in the piece's own pattern coordinates: where the dirt collects.
    shade_bands: [(y_cm, sigma_cm, strength)]: painted-in shadow, as under a jacket hem.
    no_stitch: [(x0, y0, x1, y1)] boxes in pattern cm with no topstitching along the outline (a fly, a centre seam).
    lines: [[(x, y), ...]] extra topstitched lines in pattern cm (a J-stitched fly).
    zip_x: pattern x of a straight centre-front edge that carries a zip: tape and small antique-brass teeth."""
    base, kind, period, bump = WEAVES[weave]
    if thread is None:                       # tone-on-tone: the cloth's own colour, a little paler
        thread = tuple(min(1.0, c / 255 * 1.75 + 0.02) for c in base)
    poly = outline(piece)
    lo, hi = poly.min(0), poly.max(0)
    w = max(8, int(round((hi[0] - lo[0]) * PX)))
    h = max(8, int(round((hi[1] - lo[1]) * PX)))
    # image row 0 is the top of the piece (v runs up)
    px = [((x - lo[0]) * PX, (hi[1] - y) * PX) for x, y in poly]
    m = Image.new("L", (w, h), 0)
    ImageDraw.Draw(m).polygon(px, fill=255)
    mask = np.asarray(m, np.float32) / 255.0
    dist = edge_distance(mask, 2.4 * PX) / PX                    # cm from the outline, capped at 2.4
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    y_cm = hi[1] - yy / PX

    fine = noise((h, w), 0.5, seed, lo=max(h, w) / 6)
    grit = noise((h, w), 0.15, seed + 1, lo=max(h, w) / 3)
    mid = noise((h, w), 1.5, seed + 2, lo=max(h, w) / (9 * PX))   # patches about 5-9 cm
    blot = noise((h, w), 1.3, seed + 3, lo=max(h, w) / (5 * PX))  # patches about 3-5 cm
    if kind == "twill":
        p = period * PX
        weave_h = 0.5 + 0.5 * np.sin(2 * np.pi * (xx + yy) / p)
        weave_h = 0.7 * weave_h ** 1.4 + 0.3 * (0.5 + 0.5 * np.sin(2 * np.pi * (xx - yy) / (p / 2)))
    elif kind == "leather":
        weave_h = noise((h, w), 1.1, seed + 7, lo=max(h, w) / (1.2 * PX))
    else:
        weave_h = noise((h, w), 0.8, seed + 7, lo=max(h, w) / (0.6 * PX))
    if weave in ("fleece", "jersey"):
        wash, dust = wash * 0.4, dust * 0.5      # knits pill and fade less than canvas
    height = 0.55 * weave_h + 0.3 * fine + 0.15 * grit

    col = np.array(base, float) / 255
    # The weave is felt (normal map) more than seen: drawn strongly in the colour it turns into stripes at a distance.
    alb = col[None, None, :] * (1 + 0.14 * (weave_h - 0.5) + 0.30 * (fine - 0.5) + 0.22 * (grit - 0.5))[..., None]
    # stone wash: paler patches; worn seams: paler right at the edges where the cloth is doubled
    pale = np.clip((mid - 0.45) * 2.2, 0, 1) * wash
    seam_wear = np.exp(-np.maximum(dist - 0.25, 0) / 0.55) * (0.35 + 0.65 * grit) * 0.55
    fade = np.clip(pale + seam_wear, 0, 1)
    alb = alb + fade[..., None] * (np.array([0.235, 0.225, 0.225]) - alb) * 0.5
    # waxed canvas: mottled light and dark patches 3-10 cm across, about +-15% in value
    alb = alb * (1 + 0.3 * (mid - 0.5))[..., None]
    # dust and ground-in dirt (also along the edges of pockets and cuffs, where it is caught)
    band = np.zeros((h, w), np.float32) + dust * 0.35 + edge_dust * np.exp(-dist / 0.9)
    for yc, sigma, strength in dust_bands:
        band += strength * np.exp(-((y_cm - yc) / sigma) ** 2)
    dirt = np.clip((blot - 0.38) * 1.5, 0, 1) * np.clip(band, 0, 1.2) * (0.7 + 0.3 * grit)       # soft-edged, not spotty
    alb = alb * (1 - 0.45 * dirt[..., None]) + np.array([0.33, 0.255, 0.18])[None, None, :] * 0.45 * dirt[..., None]
    if lift > 0:
        alb = alb + lift * (np.array([0.30, 0.27, 0.24]) - alb)
    if shade_top > 0:
        v = np.clip(yy / max(h * 0.30, 1), 0, 1)                    # 0 at the top edge, 1 a third of the way down
        alb *= (1 - shade_top * (1 - v) ** 1.5)[..., None]
    for yc, sigma, strength in shade_bands:
        alb *= (1 - strength * np.exp(-((y_cm - yc) / sigma) ** 2))[..., None]
    # the edge is in the shade of the seam; topstitching runs parallel to it
    alb *= (1 - edge_dark * np.exp(-dist / 0.35))[..., None]
    groove = np.zeros((h, w), np.float32)
    dash = ((xx + yy) // max(2.0, 0.3 * PX)) % 2 == 0
    x_cm = lo[0] + xx / PX
    keep = np.ones((h, w), bool)
    for x0, y0, x1, y1 in no_stitch:
        keep &= ~((x_cm >= min(x0, x1)) & (x_cm <= max(x0, x1)) & (y_cm >= min(y0, y1)) & (y_cm <= max(y0, y1)))
    tc = np.array(thread)
    for d in stitch:
        on = (np.abs(dist - d) < 0.05) & (mask > 0.5) & keep
        line = on & dash
        alb[line] = alb[line] * 0.45 + tc * 0.55
        groove += on * 0.5
    for pl in lines:
        img = Image.new("L", (w, h), 0)
        ImageDraw.Draw(img).line([((x - lo[0]) * PX, (hi[1] - y) * PX) for x, y in pl], fill=255, width=max(1, int(round(0.1 * PX))))
        on = (np.asarray(img) > 127) & (mask > 0.5)
        alb[on & dash] = alb[on & dash] * 0.45 + tc * 0.55
        groove += on * 0.5
    if zip_x is not None:
        inward = 1.0 if (lo[0] + hi[0]) / 2 > zip_x else -1.0
        off = (x_cm - zip_x) * inward                                   # cm into the piece from the zip edge
        tape = (off >= 0) & (off < 1.3) & (mask > 0.5)
        alb[tape] *= 0.55
        teeth = (off >= 0.05) & (off < 0.42) & (((y_cm * 10) % 3.2) < 1.9) & (mask > 0.5)
        shade = (0.75 + 0.3 * (((y_cm * 10) % 3.2) / 1.9))[teeth]
        alb[teeth] = np.array([0.33, 0.26, 0.15]) * shade[:, None]
        height = height + teeth * 0.7 - tape * 0.1
    height = height - groove + 0.5 * np.exp(-dist / 0.18)          # a raised seam at the very edge

    out_dir = pathlib.Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    save(out_dir / f"{name}.png", alb)
    save(out_dir / f"{name}_normal.png", normal_map(height, bump * 0.8))
    rough = 0.9 + 0.06 * (fine - 0.5) - 0.12 * fade + 0.05 * dirt
    save(out_dir / f"{name}_rough.png", np.repeat(rough[..., None], 3, -1))
    return str(out_dir / f"{name}.png")


if __name__ == "__main__":
    project = json.load(open(sys.argv[1], encoding="utf-8"))
    for g in project["garments"]:
        for i, piece in enumerate(g["pieces"]):
            print(paint(piece, sys.argv[2], piece["name"].replace(" ", "_"), seed=i))
