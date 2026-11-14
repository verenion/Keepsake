"""
Builds Keepsake's offline map data (src/PhotoLibrary.Core/Geo/Data) from public sources. Run it only to refresh the
data; the app itself never downloads anything.

Sources (download them yourself, then pass the folder):
  ne_50m_admin_0_countries.geojson  Natural Earth 1:50m countries (public domain)
      https://github.com/nvkelso/natural-earth-vector/blob/v5.1.2/geojson/ne_50m_admin_0_countries.geojson
  cities15000.zip                   GeoNames places with 15,000+ people (CC BY 4.0)
      https://download.geonames.org/export/dump/cities15000.zip
  admin1CodesASCII.txt              GeoNames region names (CC BY 4.0)
      https://download.geonames.org/export/dump/admin1CodesASCII.txt

    python build_geodata.py <folder with the three files> <output folder>

Output (gzip, little-endian; strings are a uint16 byte length then UTF-8):
  countries.bin.gz  "KSC1", int32 count, then per country: name, ISO-2, int32 rings, per ring int32 n and n x (float32 lon, float32 lat)
  places.bin.gz     "KSP1", int32 count, then per place: name, region, ISO-2, float32 lat, float32 lon, int32 population
"""
import gzip, io, json, struct, sys, zipfile
from pathlib import Path


def s(text):
    b = text.encode("utf-8")
    return struct.pack("<H", len(b)) + b


def main(src, out):
    src, out = Path(src), Path(out)
    out.mkdir(parents=True, exist_ok=True)

    countries = json.loads((src / "ne_50m_admin_0_countries.geojson").read_text(encoding="utf-8"))["features"]
    buf = io.BytesIO()
    buf.write(b"KSC1" + struct.pack("<i", len(countries)))
    points = 0
    for f in countries:
        p = f["properties"]
        iso = p.get("ISO_A2_EH") or p.get("ISO_A2") or ""
        iso = "" if iso == "-99" else iso
        geom = f["geometry"]
        polys = geom["coordinates"] if geom["type"] == "MultiPolygon" else [geom["coordinates"]]
        rings = [r for poly in polys for r in poly]
        buf.write(s(p["NAME"]) + s(iso) + struct.pack("<i", len(rings)))
        for r in rings:
            buf.write(struct.pack("<i", len(r)))
            for lon, lat in r:
                buf.write(struct.pack("<ff", lon, lat))
            points += len(r)
    (out / "countries.bin.gz").write_bytes(gzip.compress(buf.getvalue(), 9, mtime=0))

    regions = {}
    for line in (src / "admin1CodesASCII.txt").read_text(encoding="utf-8").splitlines():
        parts = line.split("\t")
        if len(parts) >= 2:
            regions[parts[0]] = parts[1]
    lines = zipfile.ZipFile(src / "cities15000.zip").read("cities15000.txt").decode("utf-8").splitlines()
    buf = io.BytesIO()
    rows = []
    for line in lines:
        c = line.split("\t")
        if len(c) < 15:
            continue
        rows.append((c[1], regions.get(f"{c[8]}.{c[10]}", ""), c[8], float(c[4]), float(c[5]), int(c[14] or 0)))
    buf.write(b"KSP1" + struct.pack("<i", len(rows)))
    for name, region, iso, lat, lon, pop in rows:
        buf.write(s(name) + s(region) + s(iso) + struct.pack("<ffi", lat, lon, pop))
    (out / "places.bin.gz").write_bytes(gzip.compress(buf.getvalue(), 9, mtime=0))
    print(f"{len(countries)} countries ({points} points), {len(rows)} places")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
