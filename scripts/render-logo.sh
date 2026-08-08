#!/usr/bin/env bash
#
# Renders assets/logo.png (512x512) from assets/logo.svg.
#
# Why this is not just `rsvg-convert assets/logo.svg`: the render has to stay reproducible on a
# machine that has ImageMagick and nothing else, and ImageMagick without a librsvg delegate falls
# back to its own MSVG renderer, which is not an SVG renderer so much as a partial one that fails
# quietly — it has silently dropped gradients and stroked paths from earlier versions of this mark,
# producing files that looked plausible and were wrong. So the drawing is done with ImageMagick's own
# -draw primitives, with every number read out of the SVG so that file stays the single source of
# geometry and the PNG cannot drift from it.
#
# The SVG's structure is a contract (assets/logo.svg states it too): one <rect> tile, then one
# <g transform="translate(x y) scale(s)"> holding exactly two filled paths in order — the letter
# (carrying fill-rule), then the spark. Every extraction below is validated, so an edit that changes
# the SVG's shape fails here loudly rather than rendering something wrong.
#
# Usage: scripts/render-logo.sh   (from anywhere; writes <repo>/assets/logo.png)

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
svg="$repo_root/assets/logo.svg"
out="$repo_root/assets/logo.png"

command -v magick >/dev/null 2>&1 || { echo "error: ImageMagick 7 (magick) is required" >&2; exit 1; }
[ -f "$svg" ] || { echo "error: $svg not found" >&2; exit 1; }

# The PNG is 512 and the SVG's viewBox is 256, so every extracted length doubles.
scale_factor=2

# Pulls one value out of the SVG and refuses to carry on with an empty or malformed one. $1 names the
# thing for the error message, $2 is a sed program, $3 is an anchored ERE the value has to match.
extract() {
    local what="$1" program="$2" pattern="$3" value
    value="$(sed -n "$program" "$svg" | head -n 1)"
    if [ -z "$value" ] || ! printf '%s' "$value" | grep -Eq "^$pattern\$"; then
        echo "error: could not read the $what from assets/logo.svg (got '${value}')." >&2
        echo "       The SVG's shape changed; update the matching sed program in $(basename "$0")." >&2
        exit 1
    fi
    printf '%s' "$value"
}

hex='#[0-9A-Fa-f]{6}'
number='-?[0-9]+(\.[0-9]+)?'
transform="translate\($number $number\) scale\($number\)"

tile_fill="$(extract   'tile fill'          '/<rect/s/.*[^-]fill="\([^"]*\)".*/\1/p'          "$hex")"
tile_radius="$(extract 'tile corner radius' '/<rect/s/.*rx="\([^"]*\)".*/\1/p'                "$number")"
group_tf="$(extract    'group transform'    '/<g transform=/s/.*transform="\([^"]*\)".*/\1/p' "$transform")"
mark_rule="$(extract   'letter fill-rule'   '/fill-rule=/s/.*fill-rule="\([^"]*\)".*/\1/p'    '(nonzero|evenodd)')"

# The two path fills, in document order, taken from <path> lines only so the rect's own fill is not
# counted among them.
mark_fill="$(sed -n '/<path/s/.*fill="\([^"]*\)".*/\1/p' "$svg" | sed -n '1p')"
spark_fill="$(sed -n '/<path/s/.*fill="\([^"]*\)".*/\1/p' "$svg" | sed -n '2p')"
for pair in "letter fill:$mark_fill" "spark fill:$spark_fill"; do
    case "${pair#*:}" in
        \#*) ;;
        *) echo "error: no ${pair%%:*} found in assets/logo.svg (got '${pair#*:}')." >&2; exit 1 ;;
    esac
done

# The two path data strings, in document order: the letter, then the spark.
mark_path="$(sed -n 's/.* d="\([^"]*\)".*/\1/p' "$svg" | sed -n '1p')"
spark_path="$(sed -n 's/.* d="\([^"]*\)".*/\1/p' "$svg" | sed -n '2p')"
for pair in "letter:$mark_path" "spark:$spark_path"; do
    case "${pair#*:}" in
        M*) ;;
        *) echo "error: no ${pair%%:*} path data in assets/logo.svg." >&2; exit 1 ;;
    esac
done

# Splits "translate(x y) scale(s)" into its three numbers; `extract` already checked the shape.
read -r g_x g_y g_s <<<"$(printf '%s' "$group_tf" | sed 's/translate(\([^ ]*\) \([^)]*\)) scale(\([^)]*\))/\1 \2 \3/')"

# The one flat transform -draw needs, doubled for the 512 output.
read -r draw_x draw_y draw_s tile_rx <<EOF
$(awk -v x="$g_x" -v y="$g_y" -v s="$g_s" -v rx="$tile_radius" -v k="$scale_factor" '
    BEGIN { printf "%.4f %.4f %.6f %.4f\n", k*x, k*y, k*s, k*rx }')
EOF

magick -size 512x512 xc:none \
    -draw "fill '$tile_fill' roundrectangle 0,0 511,511 $tile_rx,$tile_rx" \
    -draw "fill '$mark_fill' stroke none fill-rule $mark_rule \
           translate $draw_x,$draw_y scale $draw_s,$draw_s path '$mark_path'" \
    -draw "fill '$spark_fill' stroke none fill-rule nonzero \
           translate $draw_x,$draw_y scale $draw_s,$draw_s path '$spark_path'" \
    -depth 8 -strip -define png:compression-level=9 \
    "$out"

# A dropped path is a file that still renders, so check the render rather than the exit code: the
# spark's own region must not be flat tile colour. The region is derived from the spark path's
# bounds rather than hardcoded — a hardcoded crop once outlived the mark it was written for and
# silently stopped testing anything.
read -r spark_x spark_y spark_w spark_h <<EOF
$(awk -v path="$spark_path" -v ox="$draw_x" -v oy="$draw_y" -v os="$draw_s" '
    BEGIN {
        n = split(path, tok, /[^0-9.eE+-]+/)
        c = 0; first = 1
        for (i = 1; i <= n; i++) {
            if (tok[i] == "") continue
            v = tok[i] + 0
            if (++c % 2 == 1) { if (first || v < x0) x0 = v; if (first || v > x1) x1 = v }
            else              { if (first || v < y0) y0 = v; if (first || v > y1) y1 = v; first = 0 }
        }
        px0 = ox + os * x0; px1 = ox + os * x1
        py0 = oy + os * y0; py1 = oy + os * y1
        if (px0 < 0) px0 = 0
        if (py0 < 0) py0 = 0
        printf "%d %d %d %d\n", px0, py0, px1 - px0, py1 - py0
    }')
EOF

# Colour-aware, not variance-based: the spark's bounding box overlaps the letter's top arm, so the
# region is not flat even when the spark is missing — a variance check here passed against a render
# with no spark in it, which is exactly the silent failure this block exists to catch. Instead,
# measure what fraction of the region is the spark's own colour.
spark_share="$(magick "$out" -crop "${spark_w}x${spark_h}+${spark_x}+${spark_y}" +repage -alpha off \
    -fuzz 5% -fill white -opaque "$spark_fill" -fill black +opaque white \
    -format '%[fx:mean]' info:)"
if awk -v s="$spark_share" 'BEGIN { exit !(s < 0.02) }'; then
    echo "error: the spark is missing from $out: no ${spark_fill} pixels in its region" >&2
    echo "       (${spark_w}x${spark_h}+${spark_x}+${spark_y}; spark-coloured share: ${spark_share})." >&2
    exit 1
fi

magick identify "$out"
