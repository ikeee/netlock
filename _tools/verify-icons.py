# -*- coding: utf-8 -*-
"""独立校验 NetLock 的两个程序图标（**不依赖生成脚本的自证**）。

硬检查：
  ① 两个 .ico 都是 7 帧（16/24/32/48/64/128/256）；
  ② 四角 = 指定底色（教师黑 #000000 / 学生红 #C62828），容差 ±2；
  ③ 图标里真的有人物像素（非底色占比 > 0，防"整块纯色"混过去）；
  ④ 从**源图** src/Assets/avatar-source.png 按生成规则（透明底模式：按 alpha bbox 裁到人物、
     四周留 6%、垫底色后 LANCZOS 缩到 256）重算 256 帧，与图标里的 256 帧**逐字节比对 → 必须 0 处差异**。

为什么 ④ 不能用"教师图 vs 学生图的差异只能是底色"代替：羽化边像素是"底色与人物按 alpha 混合"的产物，
两张图在边缘**必然**有非纯底色差异，那样判会把正确的羽化边误判成"人物被动过"（错题本 #207）。

退出码：0 = 通过；1 = 失败。  用法：py -3 _tools\\verify-icons.py
"""
import os
import sys

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "src", "Assets")
SRC = os.path.join(ASSETS, "avatar-source.png")
TARGETS = [
    ("teacher-black", os.path.join(ASSETS, "teacher-black.ico"), (0, 0, 0)),
    ("student-red", os.path.join(ASSETS, "student-red.ico"), (198, 40, 40)),   # #C62828
]
EXPECT_SIZES = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
PADDING = 0.06          # 必须与 make_app_icon.py 的默认 --padding 一致

fails = []


def fail(msg):
    fails.append(msg)
    print("    ❌ " + msg)


def load_icon(path, name):
    if not os.path.isfile(path):
        fail("缺少图标文件：" + path)
        return None
    im = Image.open(path)
    sizes = sorted(im.ico.sizes())
    print("%s  （%s）" % (name, os.path.relpath(path, ROOT)))
    print("    帧数 %d  尺寸 %s" % (len(sizes), sizes))
    if sizes != EXPECT_SIZES:
        fail("帧尺寸不是 %s：" % (EXPECT_SIZES,) + str(sizes))
    im.size = (256, 256)
    return im.convert("RGBA")


def check_corners(im, name, bg):
    px = im.load()
    got = [px[p][:3] for p in ((0, 0), (255, 0), (0, 255), (255, 255))]
    ok = all(max(abs(c[i] - bg[i]) for i in range(3)) <= 2 for c in got)
    print("    四角 %s → %s" % (got, "匹配底色 ✅" if ok else "不匹配 ❌"))
    if not ok:
        fail("%s 四角不是底色 %s（实际 %s）" % (name, bg, got))


def check_has_figure(im, name, bg):
    px = im.load()
    other = total = 0
    for y in range(0, 256, 2):
        for x in range(0, 256, 2):
            total += 1
            c = px[x, y][:3]
            if max(abs(c[i] - bg[i]) for i in range(3)) > 6:
                other += 1
    pct = 100.0 * other / total
    print("    人物（非底色）像素占比：%.1f%%（抽样 %d）" % (pct, total))
    if other == 0:
        fail("%s 一个非底色像素都没有 → 图标像纯色块" % name)


def expect_frame256(fig, bg):
    """按 make_app_icon.py 的透明底模式规则重算 256 帧。"""
    w, h = fig.size
    pad = int(round(max(w, h) * PADDING))
    side = max(w, h) + pad * 2
    canvas = Image.new("RGB", (side, side), bg)
    canvas.paste(fig.convert("RGB"), ((side - w) // 2, (side - h) // 2), fig.split()[3])
    return canvas.resize((256, 256), Image.LANCZOS)


def check_recompute(im, name, bg, fig):
    exp = expect_frame256(fig, bg)
    got = im.convert("RGB")
    ep, gp = exp.load(), got.load()
    diff = 0
    first = None
    for y in range(256):
        for x in range(256):
            if ep[x, y] != gp[x, y]:
                diff += 1
                if first is None:
                    first = (x, y, ep[x, y], gp[x, y])
    print("    与『源图重算』差异像素：%d" % diff)
    if diff:
        fail("%s 与源图重算结果不一致（%d 处，首个 %s）→ 人物像素被改动过" % (name, diff, first))


def main():
    if not os.path.isfile(SRC):
        print("❌ 缺少源图：" + SRC)
        return 1
    src = Image.open(SRC).convert("RGBA")
    bbox = src.getchannel("A").getbbox()
    if not bbox:
        print("❌ 源图整张透明：" + SRC)
        return 1
    fig = src.crop(bbox)
    print("源图人物 %dx%d（原图 %s）" % (fig.size[0], fig.size[1], "x".join(str(v) for v in src.size)))
    for name, path, bg in TARGETS:
        im = load_icon(path, name)
        if im is None:
            continue
        check_corners(im, name, bg)
        check_has_figure(im, name, bg)
        check_recompute(im, name, bg, fig)
    print()
    if fails:
        print("图标校验失败 ❌（%d 项）" % len(fails))
        return 1
    print("图标校验通过 ✅（两个 .ico 都 = 『源图人物原样 + 指定底色』）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
