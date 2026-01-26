"""
Generate a professional radiology-themed icon for RVU Counter.
Design: Circular badge with stylized CT/MRI scan imagery and chart elements.

Run this script once to create the app.ico file:
    python generate_icon.py
"""

import struct
import io
import os

try:
    from PIL import Image, ImageDraw, ImageFont
except ImportError:
    print("Installing Pillow...")
    import subprocess
    subprocess.check_call(['pip', 'install', 'Pillow'])
    from PIL import Image, ImageDraw, ImageFont


def create_icon_image(size: int) -> Image.Image:
    """Create a single icon image at the specified size."""
    img = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    # Colors
    bg_dark = (20, 55, 90)      # Dark blue
    bg_light = (26, 82, 118)    # Teal
    scan_color = (200, 230, 255, 180)  # Light blue for scan lines
    chart_color = (100, 200, 150, 220)  # Soft green for chart
    white = (255, 255, 255)

    cx, cy = size // 2, size // 2

    # Draw circular background with gradient effect
    for r in range(size // 2, 0, -1):
        ratio = r / (size // 2)
        color = (
            int(bg_dark[0] * (1 - ratio) + bg_light[0] * ratio),
            int(bg_dark[1] * (1 - ratio) + bg_light[1] * ratio),
            int(bg_dark[2] * (1 - ratio) + bg_light[2] * ratio),
        )
        draw.ellipse([cx - r, cy - r, cx + r, cy + r], fill=color)

    # Draw subtle border
    border_width = max(1, size // 32)
    draw.ellipse([2, 2, size - 3, size - 3], outline=(255, 255, 255, 100), width=border_width)

    # Draw stylized scan arcs
    arc_width = max(2, size // 20)

    # Inner arc
    inner_r = int(size * 0.25)
    bbox_inner = [cx - inner_r, cy - inner_r, cx + inner_r, cy + inner_r]
    draw.arc(bbox_inner, -135, -45, fill=scan_color[:3], width=arc_width)

    # Outer arc
    outer_r = int(size * 0.35)
    bbox_outer = [cx - outer_r, cy - outer_r, cx + outer_r, cy + outer_r]
    draw.arc(bbox_outer, 45, 135, fill=scan_color[:3], width=arc_width)

    # Draw chart bars (RVU tracking representation)
    bar_width = max(2, int(size * 0.06))
    bar_spacing = int(size * 0.08)
    base_y = int(cy + size * 0.15)
    bar_x = int(cx - size * 0.12)

    bar_heights = [int(size * 0.12), int(size * 0.18), int(size * 0.24)]
    for i, h in enumerate(bar_heights):
        x = bar_x + i * bar_spacing
        draw.rectangle([x, base_y - h, x + bar_width, base_y], fill=chart_color[:3])

    # Add small accent dot for larger sizes
    if size >= 48:
        dot_size = max(2, int(size * 0.06))
        dot_x = int(cx + size * 0.2)
        dot_y = int(cy - size * 0.25)
        draw.ellipse([dot_x, dot_y, dot_x + dot_size, dot_y + dot_size], fill=(255, 255, 255, 200))

    return img


def create_ico_file(output_path: str):
    """Create a multi-resolution ICO file."""
    sizes = [16, 32, 48, 64, 128, 256]
    images = []

    for size in sizes:
        img = create_icon_image(size)
        images.append(img)

    # Save as ICO (Pillow handles the multi-resolution format)
    images[0].save(
        output_path,
        format='ICO',
        sizes=[(s, s) for s in sizes],
        append_images=images[1:]
    )
    print(f"Icon created: {output_path}")


def main():
    # Ensure Resources directory exists
    resources_dir = os.path.join(os.path.dirname(__file__), 'Resources')
    os.makedirs(resources_dir, exist_ok=True)

    output_path = os.path.join(resources_dir, 'app.ico')
    create_ico_file(output_path)

    # Also create a PNG preview
    preview_path = os.path.join(resources_dir, 'app_preview.png')
    preview = create_icon_image(256)
    preview.save(preview_path, 'PNG')
    print(f"Preview created: {preview_path}")


if __name__ == '__main__':
    main()
