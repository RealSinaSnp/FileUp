async function uploadFile() {
    const inp   = document.getElementById("fileInput");
    const resEl = document.getElementById("result");

    if (!inp.files[0]) { show("Please choose a file", "error"); return; }

    const fd = new FormData();
    fd.append("file", inp.files[0]);

    try {
        const r   = await fetch("/api/files/upload", { method: "POST", body: fd });
        const dat = await r.json();

        if (r.ok) {
            const nice = formatBytes(dat.size);

            // format expiry if present
            let expTxt = "";
            if (dat.expireAt) {
                const exp = new Date(dat.expireAt);
                expTxt = `<br>Expires at: <strong>${exp.toLocaleString()}</strong>`;
            } else {
                expTxt = `<br>Expires: <strong>Never</strong>`;
            }

            show(
                `Uploaded!<br>Name: <code>${escape(dat.fileName)}</code><br>` +
                `Size: ${nice}<br>Link: <a href="${dat.url}" target="_blank">${dat.url}</a>` +
                expTxt,
                "success"
            );
        } else show("Fail: " + dat, "error");
    } catch (e) { show("Err: " + e, "error"); }

    function show(msg, cls) {
        resEl.className = cls;
        resEl.innerHTML = msg;
    }
}

function escape(str) {
    return str.replace(/[&<>"']/g, c =>
        ({ "&":"&amp;","<":"&lt;",">":"&gt;","\"":"&quot;","'":"&#039;" }[c]));
}
function formatBytes(b, d = 2) {
    if (!+b) return "0 B";
    const k = 1024, s = ["B","KB","MB","GB"];
    const i = Math.floor(Math.log(b) / Math.log(k));
    return `${parseFloat((b/Math.pow(k,i)).toFixed(d))} ${s[i]}`;
}
