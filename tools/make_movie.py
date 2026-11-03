# Turns the frames CadConvert.exe --movie saved into a looping GIF for the README.
#   python tools\make_movie.py <frames folder> <out.gif> [--width N] [--ms N] [--crop left,top,right,bottom]
# Run with a Python that has Pillow (FreeCAD's has). The crop takes off the invisible border Windows counts as part of a
# window, which would show whatever is behind it.
import glob, os, sys
from PIL import Image


def main():
    args = sys.argv[1:]
    folder, out, rest = args[0], args[1], args[2:]
    width, ms, crop = 960, 70, (8, 0, 8, 8)
    while rest:
        a = rest.pop(0)
        if a == "--width":
            width = int(rest.pop(0))
        elif a == "--ms":
            ms = int(rest.pop(0))
        elif a == "--crop":
            crop = tuple(int(v) for v in rest.pop(0).split(","))
    frames = []
    for path in sorted(glob.glob(os.path.join(folder, "frame_*.png"))):
        im = Image.open(path).convert("RGB")
        w, h = im.size
        im = im.crop((crop[0], crop[1], w - crop[2], h - crop[3]))
        if width and im.size[0] > width:
            im = im.resize((width, round(im.size[1] * width / im.size[0])), Image.LANCZOS)
        frames.append(im)
    if not frames:
        print("no frames in", folder)
        return
    # One palette for every frame, from the first (the window doesn't change, only the model turns), with its left third
    # repeated four times over: the small coloured status dots there (green matches, orange approximate) are too few
    # pixels to win a palette entry otherwise.
    first = frames[0]
    w, h = first.size
    side = first.crop((0, 0, w // 3, h))
    source = Image.new("RGB", (w + side.size[0] * 4, h))
    source.paste(first, (0, 0))
    for k in range(4):
        source.paste(side, (w + k * side.size[0], 0))
    palette = source.quantize(colors=255, method=Image.Quantize.MEDIANCUT)
    frames = [f.quantize(palette=palette, dither=Image.Dither.NONE) for f in frames]
    frames[0].save(out, save_all=True, append_images=frames[1:], duration=ms, loop=0, optimize=True, disposal=1)
    print("%s: %d frames, %d x %d, %.1f MB" % (out, len(frames), frames[0].size[0], frames[0].size[1], os.path.getsize(out) / 1e6))


if __name__ == "__main__":
    main()
