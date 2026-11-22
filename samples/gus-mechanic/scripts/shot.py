"""Capture the live Seamstress 3D garment window: python shot.py <project.garment.json> <out-prefix> [views...]"""
import subprocess, sys, pathlib, os
def _root():
    p = pathlib.Path(__file__).resolve()
    for d in p.parents:
        if (d / "Seamstress.slnx").exists():
            return d
    return p.parents[2]


ROOT = _root()
EXE = ROOT / "src/Seamstress.App/bin/Release/net8.0/Seamstress.exe"


def shot(project, prefix, views=("front", "closeup", "back"), size="1600x1050"):
    out = []
    for v in views:
        png = pathlib.Path(f"{prefix}-{v}.png").resolve()
        if png.exists():
            png.unlink()
        env = {**os.environ, "DOTNET_ROOT": str(pathlib.Path.home() / ".dotnet")}
        try:
            subprocess.run([str(EXE), str(pathlib.Path(project).resolve()), "--gpu", "--view", f"garment{v}clean",
                            "--screenshot", str(png), "--size", size], cwd=EXE.parent, env=env, timeout=90,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        except subprocess.TimeoutExpired:
            print("timeout", v)
        print(v, png.exists())
        out.append(png)
    return out


if __name__ == "__main__":
    shot(sys.argv[1], sys.argv[2], sys.argv[3:] or ("front", "closeup", "back"))
