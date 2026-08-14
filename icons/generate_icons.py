"""Generate ZV icon PNGs and ICOs from the Cascadia Code font."""
import os
from PIL import Image, ImageDraw, ImageFont

ICONS_DIR = os.path.dirname(os.path.abspath(__file__))
FONT_PATH = r"C:\Windows\Fonts\CascadiaCode.ttf"
SIZES = [16, 32, 48, 64, 128, 256, 512]
TEXT = "{ZV}"

# Colors matching the SVG gradient (we'll use the midpoint green)
GREEN = "#3BD46E"  # midpoint of #4ADE80 -> #22C55E


def render_icon(size, bg_color, output_path):
    """Render the {ZV} icon at a given size."""
    img = Image.new("RGBA", (size, size), bg_color)
    draw = ImageDraw.Draw(img)

    # Auto-fit: find the largest font size where text fits within 90% of icon
    target = int(size * 0.90)
    font_size = size  # start large and shrink
    try:
        font = ImageFont.truetype(FONT_PATH, font_size)
    except OSError:
        print(f"  Warning: Could not load {FONT_PATH}, using default font")
        font = ImageFont.load_default()

    # Binary search for best fit
    lo, hi = 4, size
    while lo < hi:
        mid = (lo + hi + 1) // 2
        test_font = ImageFont.truetype(FONT_PATH, mid)
        bbox = draw.textbbox((0, 0), TEXT, font=test_font)
        tw = bbox[2] - bbox[0]
        th = bbox[3] - bbox[1]
        if tw <= target and th <= target:
            lo = mid
        else:
            hi = mid - 1

    font_size = lo
    font = ImageFont.truetype(FONT_PATH, font_size)

    # Get text bounding box for centering
    bbox = draw.textbbox((0, 0), TEXT, font=font)
    text_w = bbox[2] - bbox[0]
    text_h = bbox[3] - bbox[1]

    x = (size - text_w) / 2 - bbox[0]
    y = (size - text_h) / 2 - bbox[1]

    # Draw with a subtle glow (draw slightly blurred underneath)
    # Simple approximation: draw offset copies at lower opacity
    glow_color = (74, 222, 128, 40)  # semi-transparent green
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            if dx == 0 and dy == 0:
                continue
            draw.text((x + dx, y + dy), TEXT, font=font, fill=glow_color)

    # Main text
    draw.text((x, y), TEXT, font=font, fill=GREEN)

    img.save(output_path)
    return img


def main():
    print("Generating ZV icons...")
    print(f"  Font: {FONT_PATH}")
    print(f"  Sizes: {SIZES}")
    print()

    # Generate solid black background PNGs
    solid_images = {}
    for size in SIZES:
        out = os.path.join(ICONS_DIR, f"icon-{size}.png")
        img = render_icon(size, (0, 0, 0, 255), out)
        solid_images[size] = img
        print(f"  Created: icon-{size}.png")

    # Generate transparent background PNGs
    transparent_images = {}
    for size in SIZES:
        out = os.path.join(ICONS_DIR, f"icon-transparent-{size}.png")
        img = render_icon(size, (0, 0, 0, 0), out)
        transparent_images[size] = img
        print(f"  Created: icon-transparent-{size}.png")

    # Generate ICO files (multi-size, largest first)
    # Windows ICO supports up to 256x256; we include all sizes up to 256
    ico_sizes = sorted([s for s in SIZES if s <= 256], reverse=True)

    # Solid ICO
    ico_path = os.path.join(ICONS_DIR, "icon.ico")
    ico_images = [solid_images[s].copy() for s in ico_sizes]
    ico_images[0].save(
        ico_path,
        format="ICO",
        sizes=[(s, s) for s in ico_sizes],
        append_images=ico_images[1:],
    )
    print(f"\n  Created: icon.ico (sizes: {ico_sizes})")

    # Transparent ICO
    ico_t_path = os.path.join(ICONS_DIR, "icon-transparent.ico")
    ico_t_images = [transparent_images[s].copy() for s in ico_sizes]
    ico_t_images[0].save(
        ico_t_path,
        format="ICO",
        sizes=[(s, s) for s in ico_sizes],
        append_images=ico_t_images[1:],
    )
    print(f"  Created: icon-transparent.ico (sizes: {ico_sizes})")

    print("\nDone!")


if __name__ == "__main__":
    main()
