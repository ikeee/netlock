# -*- coding: utf-8 -*-
"""
从 GitHub 头像生成程序图标（多尺寸 .ico）——**只改背景颜色，人物像素一个都不动**。

为什么要有这个脚本：以前 app.ico 是怎么生成的没有留档，改背景色就只能重新猜。
现在固定成一条命令可复现：

    py -3 _tools/make_app_icon.py --bg "#000000" --out <目标.ico>       # 教师端：黑底
    py -3 _tools/make_app_icon.py --bg "#C62828" --out <目标.ico>       # 学生端：红底

两种输入，自动判别：
  A. **已抠好的透明底图（推荐）**：源图带 alpha（例如 remove.bg 的产出）→ 直接把颜色垫在人物后面，
     人物像素**一个都不动**（羽化边按 alpha 混合到新底色，正是想要的效果）；
  B. 不透明原图：从头像四条边做**洪水填充**（容差内视为背景）识别背景——用于没有透明版时。
     背景是带渐变的深蓝黑时，单一全局阈值会把头发/深色衣服也吞掉，所以必须"从边缘连通地扩散"。
人物不变的自证：A 模式断言 alpha=255 的像素 RGB 逐字节不变；B 模式断言非背景像素逐字节不变。都输出 diff=0。

原图是**竖构图**（例如 457x640 的人物照）时会**自动裁成以人物为中心的方图**，
这样图标里人物才占够面积（否则四周全是背景）。
"""
import argparse
import os
import os
import sys
from collections import deque

from PIL import Image, ImageOps


def flood_background(im, tolerance):
    """从四边洪水填充出背景掩码（True=背景）。返回 (mask, stats)。"""
    w, h = im.size
    px = im.load()
    mask = bytearray(w * h)          # 0=人物 1=背景
    seed = []
    for x in range(w):
        seed.append((x, 0)); seed.append((x, h - 1))
    for y in range(h):
        seed.append((0, y)); seed.append((w - 1, y))

    def close(c1, c2):
        return (abs(c1[0] - c2[0]) + abs(c1[1] - c2[1]) + abs(c1[2] - c2[2])) <= tolerance

    q = deque()
    for (x, y) in seed:
        i = y * w + x
        if mask[i] == 0:
            mask[i] = 1
            q.append((x, y))
    while q:
        x, y = q.popleft()
        base = px[x, y]
        for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            nx, ny = x + dx, y + dy
            if 0 <= nx < w and 0 <= ny < h:
                i = ny * w + nx
                if mask[i] == 0 and close(px[nx, ny], base):
                    mask[i] = 1
                    q.append((nx, ny))
    bg = sum(mask)
    return mask, {"background_px": bg, "background_pct": round(100.0 * bg / (w * h), 1)}


def build(src, bg_hex, out_path, tolerance=16, preview=None, padding=0.06, autocrop=True):
    raw = ImageOps.exif_transpose(Image.open(src))
    # ---------- A 模式：源图自带 alpha（已抠背景）→ 直接垫底色，人物像素不动 ----------
    if raw.mode in ("RGBA", "LA") or "transparency" in raw.info:
        return build_from_alpha(raw.convert("RGBA"), bg_hex, out_path, preview, padding)
    im = raw.convert("RGB")
    w, h = im.size
    mask, stats = flood_background(im, tolerance)
    mask, comp = keep_largest_person_component(mask, w, h)
    stats.update(comp)
    mask, morph = morphology_clean(mask, w, h)
    stats.update(morph)

    # 竖构图 → 以人物为中心的方图（人物占比才够；否则图标四周全是背景）
    crop = (0, 0, w, h)
    if autocrop and w != h:
        xs = [i % w for i in range(w * h) if mask[i] == 0]
        ys = [i // w for i in range(w * h) if mask[i] == 0]
        if xs:
            cx = (min(xs) + max(xs)) / 2.0
            cy = (min(ys) + max(ys)) / 2.0
            span = max(max(xs) - min(xs), max(ys) - min(ys)) * (1.0 + padding * 2)
            side = int(max(span, min(w, h) * 0.5))
            side = min(side, w, h)
            left = int(round(cx - side / 2.0))
            top = int(round(cy - side / 2.0))
            left = max(0, min(w - side, left))
            top = max(0, min(h - side, top))
            crop = (left, top, left + side, top + side)
    if crop != (0, 0, w, h):
        im2 = im.crop(crop)
        m2 = bytearray()
        for y in range(crop[1], crop[3]):
            m2 += mask[y * w + crop[0]:y * w + crop[2]]
        im, mask = im2, m2
        w, h = im.size
        stats["crop"] = "%d,%d-%d,%d" % crop
    else:
        stats["crop"] = "原图未裁"

    bg = tuple(int(bg_hex.lstrip("#")[i:i + 2], 16) for i in (0, 2, 4))

    out = Image.new("RGB", (w, h))
    sp, op = im.load(), out.load()
    changed = kept = 0
    for y in range(h):
        for x in range(w):
            if mask[y * w + x]:
                op[x, y] = bg
                changed += 1
            else:
                op[x, y] = sp[x, y]      # 人物像素原样搬运
                kept += 1

    # 自证：非背景像素必须与源图逐字节一致
    diff = 0
    for y in range(h):
        for x in range(w):
            if not mask[y * w + x] and op[x, y] != sp[x, y]:
                diff += 1
    assert diff == 0, "人物像素被改动了：%d 个" % diff

    sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)]
    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
    out.save(out_path, format="ICO", sizes=sizes)
    if preview:
        out.resize((256, 256), Image.LANCZOS).save(preview, format="PNG")

    stats.update({"source": src, "size": "%dx%d" % (w, h), "bg": bg_hex,
                  "recolored_px": changed, "person_px": kept, "person_diff": diff,
                  "out": out_path, "out_bytes": os.path.getsize(out_path)})
    return stats


def build_from_alpha(rgba, bg_hex, out_path, preview=None, padding=0.06):
    """透明底图 → 新底色图标（人物像素零改动，羽化边按 alpha 混合到新底色）。"""
    w, h = rgba.size
    alpha = rgba.getchannel("A")
    bbox = alpha.getbbox()
    if not bbox:
        raise ValueError("源图整张都是透明的：%s" % out_path)
    rgba = rgba.crop(bbox)
    w, h = rgba.size
    pad = int(round(max(w, h) * padding))
    side = max(w, h) + pad * 2
    bg = tuple(int(bg_hex.lstrip("#")[i:i + 2], 16) for i in (0, 2, 4))
    out = Image.new("RGB", (side, side), bg)
    out.paste(rgba.convert("RGB"), ((side - w) // 2, (side - h) // 2), rgba.split()[3])

    # 自证：不透明像素必须与源图逐字节一致
    src_rgb = rgba.convert("RGB").load()
    out_rgb = out.load()
    off_x, off_y = (side - w) // 2, (side - h) // 2
    a = rgba.split()[3].load()
    diff = opaque = feathered = 0
    for y in range(h):
        for x in range(w):
            av = a[x, y]
            if av == 255:
                opaque += 1
                if out_rgb[x + off_x, y + off_y] != src_rgb[x, y]:
                    diff += 1
            elif av > 0:
                feathered += 1
    assert diff == 0, "人物像素被改动了：%d 个" % diff

    sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)]
    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
    out.save(out_path, format="ICO", sizes=sizes)
    if preview:
        out.resize((256, 256), Image.LANCZOS).save(preview, format="PNG")
    return {
        "mode": "alpha（已抠好的透明底图）",
        "source": out_path, "figure_bbox": "%d,%d-%d,%d" % bbox, "figure": "%dx%d" % (w, h),
        "canvas": "%dx%d" % (side, side), "bg": bg_hex,
        "opaque_px": opaque, "feathered_px": feathered, "person_diff": diff,
        "out": out_path, "out_bytes": os.path.getsize(out_path),
    }


def keep_largest_person_component(mask, w, h):
    """
    只保留最大的"人物"连通块，其余非背景像素按背景处理。

    为什么需要：头像背景是带噪点的渐变，洪水填充后总会在边角留下若干孤立小岛；
    不去掉的话，它们会以"原色"（深色）留在新背景上，看起来就是脏点。
    返回 (新掩码, 统计)。
    """
    person = bytearray(1 if mask[i] == 0 else 0 for i in range(w * h))
    seen = bytearray(w * h)
    best = []
    for start in range(w * h):
        if person[start] and not seen[start]:
            q = deque([start])
            seen[start] = 1
            comp = []
            while q:
                i = q.popleft()
                comp.append(i)
                x, y = i % w, i // w
                for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)):
                    nx, ny = x + dx, y + dy
                    if 0 <= nx < w and 0 <= ny < h:
                        j = ny * w + nx
                        if person[j] and not seen[j]:
                            seen[j] = 1
                            q.append(j)
            if len(comp) > len(best):
                best = comp
    kept = set(best)
    out = bytearray(w * h)
    dropped = 0
    for i in range(w * h):
        if mask[i] == 0 and i not in kept:
            out[i] = 1          # 孤立小岛 → 当背景
            dropped += 1
        else:
            out[i] = mask[i]
    return out, {"person_blobs_kept": 1, "person_blobs_dropped_px": dropped}


def morphology_clean(mask, w, h, open_keep=3, close_fill=6):
    """
    形态学清理（8 邻域，纯 Python 跑 457² 够快）：
      ① 开运算（侵蚀）：人物像素若 8 邻域里人物少于 open_keep，判为噪点/细桥 → 变背景；
      ② 闭运算（膨胀）：背景像素若 8 邻域里人物不少于 close_fill，判为针孔 → 变人物。
    这样"背景→人物"的连接细桥会被切断（避免背景色顺着细桥漏进人物内部），
    同时人物内部的孤立背景点被补上。
    """
    def neighbors(i, w, h):
        x, y = i % w, i // w
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                if dx == 0 and dy == 0:
                    continue
                nx, ny = x + dx, y + dy
                if 0 <= nx < w and 0 <= ny < h:
                    yield ny * w + nx

    # ① 开
    opened = bytearray(mask)
    eroded = 0
    for i in range(w * h):
        if mask[i] == 0:
            cnt = sum(1 for j in neighbors(i, w, h) if mask[j] == 0)
            if cnt < open_keep:
                opened[i] = 1
                eroded += 1
    # ② 闭
    closed = bytearray(opened)
    filled = 0
    for i in range(w * h):
        if opened[i] == 1:
            cnt = sum(1 for j in neighbors(i, w, h) if opened[j] == 0)
            if cnt >= close_fill:
                closed[i] = 0
                filled += 1
    return closed, {"morph_eroded_px": eroded, "morph_filled_px": filled}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", default=os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                                 "..", "src", "Assets", "avatar-source.png"))
    ap.add_argument("--bg", required=True, help='例如 "#000000" / "#C62828"')
    ap.add_argument("--out", required=True)
    ap.add_argument("--tolerance", type=int, default=16, help="背景容差（曼哈顿距离，默认 16）")
    ap.add_argument("--padding", type=float, default=0.06, help="裁方时给人物四周留的余量（默认 6%%）")
    ap.add_argument("--no-crop", action="store_true", help="不要自动裁方（原图已方）")
    ap.add_argument("--preview", default=None, help="同时导出一张 256x256 PNG 便于肉眼核对")
    a = ap.parse_args()
    st = build(a.src, a.bg, a.out, a.tolerance, a.preview, a.padding, not a.no_crop)
    for k in ("mode", "source", "size", "crop", "figure_bbox", "figure", "canvas", "bg",
              "background_px", "background_pct", "recolored_px", "person_px",
              "opaque_px", "feathered_px", "person_diff", "out", "out_bytes"):
        if k not in st:
            continue
        print("%-18s %s" % (k, st[k]))
    print("OK：人物像素 diff=0（一个都没动）")


if __name__ == "__main__":
    sys.exit(main())
