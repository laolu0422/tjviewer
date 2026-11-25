"""生成 TjViewer 白紫 TJV 应用图标（PNG + 多尺寸 ICO）。"""
from PIL import Image, ImageDraw, ImageFont
import os

S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
draw = ImageDraw.Draw(img)

# 紫色渐变圆角方块
radius = int(S * 0.22)
grad = Image.new("RGB", (S, S))
gd = ImageDraw.Draw(grad)
c1 = (124, 58, 237)   # #7C3AED
c2 = (167, 139, 250)  # #A78BFA
for y in range(S):
    t = y / (S - 1)
    gd.line([(0, y), (S, y)], fill=(
        int(c1[0] + (c2[0] - c1[0]) * t),
        int(c1[1] + (c2[1] - c1[1]) * t),
        int(c1[2] + (c2[2] - c1[2]) * t),
    ))
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=radius, fill=255)
img.paste(grad, (0, 0), mask)

# 内层高光描边
draw.rounded_rectangle([26, 26, S - 27, S - 27], radius=radius - 12, outline=(255, 255, 255, 46), width=8)

def load_font(size):
    for name in ("seguisb.ttf", "segoeuib.ttf", "arialbd.ttf", "arial.ttf"):
        for base in (r"C:\Windows\Fonts", "/usr/share/fonts/truetype/dejavu"):
            path = os.path.join(base, name)
            if os.path.exists(path):
                return ImageFont.truetype(path, size)
    return ImageFont.load_default()

text = "TJV"
font = load_font(330)
bbox = draw.textbbox((0, 0), text, font=font)
tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
x = (S - tw) / 2 - bbox[0]
y = (S - th) / 2 - bbox[1] - 30

# 柔和投影增强可读性
draw.text((x + 10, y + 12), text, font=font, fill=(60, 20, 130, 110))
draw.text((x, y), text, font=font, fill=(255, 255, 255, 255))

# 底部三条白色指示线（象征画面传输）
line_w, gap = 150, 34
total = line_w * 3 + gap * 2
lx = (S - total) / 2
for i in range(3):
    alpha = 235 - i * 55
    draw.rounded_rectangle([lx + i * (line_w + gap), S - 232, lx + i * (line_w + gap) + line_w, S - 212],
                           radius=10, fill=(255, 255, 255, alpha))

os.makedirs("assets", exist_ok=True)
img.save("assets/tjv-1024.png")
sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
img.save("assets/tjv.ico", sizes=sizes)
img.resize((256, 256), Image.LANCZOS).save("assets/tjv-256.png")
print("generated", os.path.getsize("assets/tjv.ico"), "bytes ico")
