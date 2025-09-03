async function uploadFile() {
            const fileInput = document.getElementById('fileInput');
            const result = document.getElementById('result');

            if (!fileInput.files[0]) {
                result.innerHTML = '<div class="error">Please select a file first!</div>';
                return;
            }

            const formData = new FormData();
            formData.append('file', fileInput.files[0]);

            try {
                const response = await fetch('/api/files/upload', {
                    method: 'POST',
                    body: formData
                });

                const data = await response.json();

                if (response.ok) {
                    var MBsize = data.size / 1024 / 1024;


                    result.innerHTML = `<div class="success">File uploaded successfully!<br>Name: ${data.fileName}<br>Size: ${MBsize} MB</div>`;
                } else {
                    result.innerHTML = `<div class="error">Upload failed: ${data}</div>`;
                }
            } catch (error) {
                result.innerHTML = `<div class="error">Error: ${error.message}</div>`;
            }
        }
