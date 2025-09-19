# Public File Upload API

Endpoint: POST https://upload.sinasnp.com/api/public/upload <br/>
Form field: file (multipart/form-data)

<br/>
here's how JSON response looks like:

```json
{
  "fileName": "abc123.png",
  "size": 8802,
  "url": "https://upload.sinasnp.com/files/public/png/abc123.png",
  "expireAt": "2025-09-19T03:45:06.5912777Z"
}
```


## Usage examples:
<br/>

Curl

```bash
curl -F "file=@/path/to/file.png" https://upload.sinasnp.com/api/public/upload
```

<br/>

React / JS

```js
async function uploadFile(file) {
    const fd = new FormData();
    fd.append("file", file);

    const res = await fetch("https://upload.sinasnp.com/api/public/upload", {
        method: "POST",
        body: fd
    });
    const data = await res.json();

    if (!res.ok) throw new Error(data.error || "Upload failed");

    return data.url; // the public link
}

```

<br/>

.NET

```C#
using var client = new HttpClient();
using var form = new MultipartFormDataContent();
using var fileStream = File.OpenRead("file.png");
form.Add(new StreamContent(fileStream), "file", "file.png");

var response = await client.PostAsync("https://upload.sinasnp.com/api/public/upload", form);
var json = await response.Content.ReadAsStringAsync();
Console.WriteLine(json); // contains the URL
```

<br/>

Python / CLI

```python
import requests
with open("file.png","rb") as f:
    r = requests.post("https://upload.sinasnp.com/api/public/upload", files={"file": f})
print(r.json())  # contains URL & expiry
```