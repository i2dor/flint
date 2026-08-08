# assets

The Flint mark: a bold F with blade-cut arms, striking an amber four-point spark off the top arm.

`logo.svg` is the vector source and `logo.png` is the 512x512 raster rendered from it by
`scripts/render-logo.sh`; the PNG is what the README shows and what the plugin registry is given.
The registry's upload accepts SVG, but it serves uploaded files from a public blob container with a
client-supplied content type, so an SVG there is a stored-XSS vector on that origin — upload the PNG.

The nav icon in `BTCPayServer.Plugins.Flint/Views/Shared/Spark/SparkNav.cshtml` is the same geometry
in monochrome `currentColor`. If the mark changes, change both; them being visibly the same mark is
the point of having one.

`render-logo.sh` reads its geometry straight out of `logo.svg` — the tile colour and corner radius,
the group transform, both fills and both path strings — so the SVG stays the single source and the
PNG cannot drift from it. The script expects the structure the SVG declares in its own comment: a
`<rect>` for the tile, then a `<g transform="translate(x y) scale(s)">` holding exactly two filled
paths in order — the letter (carrying `fill-rule`), then the spark. It validates every extraction and
fails loudly rather than rendering something wrong, and it checks afterwards that spark-coloured
pixels actually appear in the spark's own region, because a dropped path produces a file that still
renders and looks plausible. That check is colour-aware rather than variance-based on purpose: the
spark's bounding box overlaps the letter's arm, so a variance check passes even with the spark
missing.
