# -*- coding: utf-8 -*-
"""Windows 应用圆形图标生成脚本。

用法:
    python tools/make_win_icons.py preview            # 生成 filled / line 两方案多尺寸预览对比图
    python tools/make_win_icons.py apply filled|line  # 按选定方案写入正式图标文件

处理要点:
    - 以蓝色主体 bbox 精确居中;
    - 圆形 mask 高斯软化, 消除边缘锯齿;
    - LANCZOS 高质量缩放, 避免模糊;
    - line 方案先做形态学膨胀加粗线条, 小尺寸再二值化+膨胀补偿, 低分辨率下仍可辨识。
"""
import io
import os
import struct
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = r'e:\project\selftrans\windows\FileTransferApp.WinUI'
SRC = {
    'filled': r'C:\Users\13243\Downloads\MZFTr.jpg',
    'line': r'C:\Users\13243\Downloads\gaeum.jpg',
}
LINE_THICKEN = 10         # line 方案在 1024 基准每侧加粗像素
LOGO_RATIO = 0.88         # 主体占圆形直径比例
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]
LINE_BLUE = (23, 101, 216, 255)
# 简化版几何(1024 坐标系, 与 gaeum.jpg 主体比例一致)
SIM_CIRCLE = (512.0, 500.5, 408.5)   # 外圆 cx, cy, r
SIM_BAR = (428, 140, 596, 938, 84)   # 中竖条 x0, y0, x1, y1, 圆角半径


def blue_mask(im):
    a = np.asarray(im.convert('RGB'), dtype=np.int16)
    return ((a[..., 2] - a[..., 0]) > 40).astype(np.uint8) * 255


def filled_silhouette(mask_img):
    """蓝色区域 + 被蓝色包围的白色镂空 = 主体不透明轮廓。"""
    inv = Image.eval(mask_img, lambda v: 255 - v).convert('RGB')
    ImageDraw.floodfill(inv, (0, 0), (1, 1, 1))
    reached = np.asarray(inv.convert('L'))
    holes = reached == 255
    sil = np.maximum(np.asarray(mask_img) > 0, holes)
    return Image.fromarray((sil * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(0.8))


def line_logo_source(mode):
    """返回 (RGB 图, alpha mask, bbox)。line 方案做加粗处理。"""
    im = Image.open(SRC[mode]).convert('RGB')
    mask = Image.fromarray(blue_mask(im))
    if mode == 'line':
        for _ in range(LINE_THICKEN):
            mask = mask.filter(ImageFilter.MaxFilter(3))
        mask = mask.filter(ImageFilter.GaussianBlur(0.8))
    else:
        mask = filled_silhouette(mask)
    la = np.asarray(mask)
    ys, xs = np.nonzero(la > 127)
    bbox = (xs.min(), ys.min(), xs.max() + 1, ys.max() + 1)
    return im, mask, bbox


def simplified_line_logo(size):
    """小尺寸简化版: 外圆 + 中竖条, 线宽随尺寸自适应。"""
    k = size * LOGO_RATIO / 849.0  # 849 = 原始主体 bbox 高度
    lw = max(1.4, size * 0.05)
    img = Image.new('RGBA', (1024, 1024), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    cx, cy, r = SIM_CIRCLE
    d.ellipse([cx - r, cy - r, cx + r, cy + r], outline=LINE_BLUE, width=int(round(lw / k)))
    x0, y0, x1, y1, rad = SIM_BAR
    d.rounded_rectangle([x0, y0, x1, y1], radius=rad, outline=LINE_BLUE, width=int(round(lw / k)))
    a = np.asarray(img.split()[-1])
    ys, xs = np.nonzero(a > 0)
    return img, (xs.min(), ys.min(), xs.max() + 1, ys.max() + 1)


def build(size, mode):
    if mode == 'line' and size <= 64:
        src_img, bbox = simplified_line_logo(size)
        logo_alpha = src_img.split()[-1]
    else:
        src_img, logo_alpha, bbox = line_logo_source(mode)

    x0, y0, x1, y1 = bbox
    bw, bh = x1 - x0, y1 - y0

    scale = (size * LOGO_RATIO) / max(bw, bh)
    lw, lh = max(1, int(round(bw * scale))), max(1, int(round(bh * scale)))
    logo = src_img.crop((x0, y0, x1, y1)).resize((lw, lh), Image.LANCZOS).convert('RGBA')
    logo.putalpha(logo_alpha.crop((x0, y0, x1, y1)).resize((lw, lh), Image.LANCZOS))

    layer = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    layer.alpha_composite(logo, ((size - lw) // 2, (size - lh) // 2))

    canvas = Image.new('RGBA', (size, size), (255, 255, 255, 255))
    circ = Image.new('L', (size, size), 0)
    ImageDraw.Draw(circ).ellipse([0, 0, size - 1, size - 1], fill=255)
    circ = circ.filter(ImageFilter.GaussianBlur(max(0.6, size * 0.004)))
    canvas.putalpha(circ)  # 颜色保持纯白, 仅边缘 alpha 渐变抗锯齿
    canvas.alpha_composite(layer)
    return canvas


def write_ico(path, images):
    frames = []
    for im in images:
        buf = io.BytesIO()
        im.save(buf, 'PNG')
        frames.append((im.width, im.height, buf.getvalue()))
    out = struct.pack('<HHH', 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    body = b''
    for w, h, png in frames:
        out += struct.pack('<BBBBHHII', w % 256, h % 256, 0, 0, 1, 32, len(png), offset)
        offset += len(png)
        body += png
    with open(path, 'wb') as f:
        f.write(out + body)


def make_strip(mode):
    sizes = [256, 128, 64, 48, 32, 24, 16]
    gap, pad, label_h = 28, 24, 34
    width = pad * 2 + sum(sizes) + gap * (len(sizes) - 1)
    height = pad * 2 + 256 + label_h
    strip = Image.new('RGBA', (width, height), (32, 34, 40, 255))
    draw = ImageDraw.Draw(strip)
    font = ImageFont.truetype(r'C:\Windows\Fonts\arial.ttf', 20)
    x = pad
    for s in sizes:
        icon = build(s, mode)
        strip.alpha_composite(icon, (x, pad + (256 - s)))
        draw.text((x, pad + 256 + 6), '%d px' % s, fill=(230, 232, 238, 255), font=font)
        x += s + gap
    return strip


def preview():
    out_dir = os.path.join(ROOT, 'assets', 'round_preview')
    for mode in ('filled', 'line'):
        p = os.path.join(out_dir, 'candidate_%s.png' % mode)
        make_strip(mode).save(p)
        print('saved', p)


def apply(mode):
    res = os.path.join(ROOT, 'Resources')
    assets = os.path.join(ROOT, 'assets')
    prev = os.path.join(assets, 'round_preview')

    build(192, mode).save(os.path.join(res, 'ic_launcher.png'))
    build(432, mode).save(os.path.join(res, 'ic_launcher_foreground.png'))
    write_ico(os.path.join(assets, 'app.ico'), [build(s, mode) for s in ICO_SIZES])
    for s in ICO_SIZES:
        build(s, mode).save(os.path.join(prev, '%d.png' % s))
    make_strip(mode).save(os.path.join(prev, 'all_sizes.png'))
    for f in ('candidate_filled.png', 'candidate_line.png'):
        p = os.path.join(prev, f)
        if os.path.exists(p):
            os.remove(p)
    print('applied', mode)


if __name__ == '__main__':
    cmd = sys.argv[1] if len(sys.argv) > 1 else 'preview'
    if cmd == 'preview':
        preview()
    elif cmd == 'apply':
        apply(sys.argv[2])
    else:
        print(__doc__)
