FileUp is a self-hosted file upload and sharing service built with .NET 9 Minimal API, deployed on Linux (Ubuntu) with Nginx as a reverse proxy.

Essentially, it’s a lightweight clone of file-sharing sites like filebin.net's upload backend.

You can:
- Upload and get a link for direct use both in website's interface or 
- Use the public REST API directly from your own apps or scripts.


### Features:
- Public upload API (no login required)
- Admin-only upload panel (BasicAuth)
- Expiring uploads: has default value, can take optional value by user
- Max view count for each file (optional)
- Logging, metrics, and background maintenance
- Automatic deletion of expired files (uses Redis to prevent reset on service restart)

---

# Public File Upload API

Endpoint:
`POST https://upload.sinasnp.com/api/public/upload` <br>

Form fields:
- file
- expireMinutes (int)
- maxViews (int)

<br/>
here's how JSON response looks like:

```json
{
  "fileName": "image_20251010171300.jpg",
  "url": "https://upload.sinasnp.com/files/public/jpg/image_20251010171300.jpg",
  "expireAt": "2025-10-10T17:43:00Z",
  "maxViews": 5
}
```


## Usage examples:
<br/>

Curl

```bash
curl -L -F "file=@image.jpg" \
     -F "expireMinutes=30" \
     -F "maxViews=5" \
     https://upload.sinasnp.com/api/public/upload
```

<br/>

React / JS

```js
async function uploadFile(file) {
    const fd = new FormData();
    fd.append("file", file);
    fd.append("expireMinutes", 30);
    fd.append("maxViews", 5);

    const res = await fetch("https://upload.sinasnp.com/api/public/upload", {
        method: "POST",
        body: fd
    });

    const data = await res.json();
    if (!res.ok) throw new Error(data.error || "Upload failed");

    return data.url;
}
```

<br/>

.NET

```C#
using var client = new HttpClient();
using var form = new MultipartFormDataContent();
using var fileStream = File.OpenRead("file.png");

form.Add(new StreamContent(fileStream), "file", "file.png");
form.Add(new StringContent("30"), "expireMinutes");
form.Add(new StringContent("5"), "maxViews");

var response = await client.PostAsync("https://upload.sinasnp.com/api/public/upload", form);
var json = await response.Content.ReadAsStringAsync();
Console.WriteLine(json);
```

<br/>

Python / CLI

```python
import requests

with open("file.png", "rb") as f:
    res = requests.post(
        "https://upload.sinasnp.com/api/public/upload",
        files={"file": f},
        data={"expireMinutes": "30", "maxViews": "5"}
    )

print(res.json())
```

Licence:
Use, modify, and deploy freely