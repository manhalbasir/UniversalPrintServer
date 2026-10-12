import json, os, hashlib, sys

store_repo = os.environ["STORE_REPO"]
app_id = os.environ["APP_ID"]
app_name = os.environ["APP_NAME"]
version = os.environ["VERSION"]
summary = os.environ.get("SUMMARY", "")
description = os.environ.get("DESCRIPTION", "")
dist = "dist"
p = "store/public/catalog/apps.json"

if os.path.exists(p):
    data = json.load(open(p, encoding="utf-8"))
else:
    data = {"apps": []}

app = next((a for a in data["apps"] if a["id"] == app_id), None)
if not app:
    app = {"id": app_id, "name": app_name, "summary": "",
           "description": "", "categories": ["Others"], "downloads": []}
    data["apps"].append(app)

if not app.get("summary"):
    app["summary"] = summary
if not app.get("description"):
    app["description"] = description

files = [f for f in sorted(os.listdir(dist)) if f.lower().endswith(".exe")]
if not files:
    sys.exit("no exe found in dist")

for f in files:
    h = hashlib.sha256()
    size = 0
    with open(os.path.join(dist, f), "rb") as fh:
        for chunk in iter(lambda: fh.read(1024 * 1024), b""):
            h.update(chunk)
            size += len(chunk)
    app["downloads"] = [d for d in app["downloads"] if d["os"] != "windows"]
    app["downloads"].append({
        "os": "windows",
        "version": version,
        "size": size,
        "sha256": h.hexdigest(),
        "url": f"https://github.com/{store_repo}/releases/download/files/{f}",
    })

os.makedirs(os.path.dirname(p), exist_ok=True)
json.dump(data, open(p, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
print("catalog updated:", [d["os"] for d in app["downloads"]])
