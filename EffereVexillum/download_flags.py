import requests
import os
import json
API_URL = "https://vexillology.fandom.com/api.php"
out_dir = "flags"
if not os.path.exists(out_dir):
    os.makedirs(out_dir)
def get_images():
    params = {
        "action": "query",
        "list": "allimages",
        "ailimit": 500,
        "format": "json"
    }
    while True:
        resp = requests.get(API_URL, params=params).json()
        if "query" in resp:
            images = resp["query"]["allimages"]
            for img in images:
                name = img["name"]
                url = img["url"]
                # Check if it has a common image extension we want
                if name.lower().endswith((".svg", ".png", ".jpg", ".jpeg", ".gif")):
                    yield (name, url)
        if "continue" in resp:
            params.update(resp["continue"])
        else:
            break
def main():
    print("Fetching images from Vexillology Wiki...")
    count = 0
    for name, url in get_images():
        # Sanitize name
        safe_name = "".join(c for c in name if c.isalnum() or c in (' ', '.', '_', '-')).strip()
        safe_name = safe_name.replace(' ', '_')
        path = os.path.join(out_dir, safe_name)
        # We can clean up the URL to just the base file if necessary, but this might be the hotlinked file.
        clean_url = url.split("/revision/")[0]
        if not os.path.exists(path):
            try:
                img_data = requests.get(clean_url).content
                with open(path, 'wb') as f:
                    f.write(img_data)
                print(f"Downloaded: {safe_name}")
                count += 1
            except Exception as e:
                print(f"Failed to download {safe_name}: {e}")
        # To avoid downloading 10000 images during a test run, we can optionally limit this, 
        # but the user said "all flags".
    print(f"Finished downloading {count} flags.")
if __name__ == "__main__":
    main()
