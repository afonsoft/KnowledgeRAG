// SPEC-20260915-wasm-boot-proxy-hardening RF-004/RF-005: corporate proxies may
// block boot assets by URL extension (.dat/.wasm/...) or by sniffing binary
// payloads. Every non-.js _framework asset is fetched through a three-layer
// same-origin chain:
//   1. /framework-assets/{stem}/{ext}          — no blocked suffix in the URL,
//      identical bytes, so the boot integrity hash validates natively.
//   2. /framework-assets/{stem}/{ext}?enc=b64  — base64 text/plain, defeats
//      content-sniffing; we verify SHA-256 ourselves via crypto.subtle.
//   3. defaultUri                              — dev / non-proxy setups.
// If all layers fail, Blazor.start rejects and #app shows a readable message.
(function () {
    "use strict";

    var frameworkSegment = "/_framework/";
    var mimeByExt = { wasm: "application/wasm", json: "application/json", js: "text/javascript" };

    function mirrorUrl(stem, ext, enc) {
        var url = new URL("framework-assets/" + stem + "/" + ext, document.baseURI);
        if (enc) {
            url.searchParams.set("enc", enc);
        }
        return url;
    }

    function decodeBase64(text) {
        var clean = text.replace(/\s+/g, "");
        if (typeof Uint8Array.fromBase64 === "function") {
            return Uint8Array.fromBase64(clean);
        }
        var bin = atob(clean);
        var bytes = new Uint8Array(bin.length);
        for (var i = 0; i < bin.length; i++) {
            bytes[i] = bin.codePointAt(i);
        }
        return bytes;
    }

    // The boot manifest integrity is "sha256-<base64>" (possibly a list); the
    // encoded payload transforms the bytes on the wire, so the browser cannot
    // check it — we re-verify the decoded bytes ourselves before use.
    function verifyIntegrity(bytes, integrity) {
        if (!integrity) {
            return Promise.resolve(true);
        }
        var tokens = integrity.split(/\s+/);
        var hash = null;
        for (const t of tokens) {
            if (t.indexOf("sha256-") === 0) {
                hash = t.substring(7);
                break;
            }
        }
        if (hash === null || !window.crypto || !crypto.subtle) {
            return Promise.resolve(false);
        }
        return crypto.subtle.digest("SHA-256", bytes).then(function (buf) {
            var digest = new Uint8Array(buf);
            var bin = "";
            for (const b of digest) {
                bin += String.fromCodePoint(b);
            }
            return btoa(bin) === hash;
        });
    }

    function fetchEncoded(url, ext, integrity) {
        return fetch(url).then(function (response) {
            if (!response.ok) {
                throw new Error("b64 mirror " + response.status);
            }
            return response.text();
        }).then(function (text) {
            var bytes = decodeBase64(text);
            return verifyIntegrity(bytes, integrity).then(function (ok) {
                if (!ok) {
                    throw new Error("integrity mismatch for " + url.pathname);
                }
                var headers = new Headers();
                headers.set("Content-Type", mimeByExt[ext] || "application/octet-stream");
                return new Response(bytes, { headers: headers });
            });
        });
    }

    function loadAsset(defaultUri, integrity, stem, ext) {
        var init = integrity ? { integrity: integrity } : {};
        var raw = mirrorUrl(stem, ext, null);
        var encoded = function () {
            return fetchEncoded(mirrorUrl(stem, ext, "b64"), ext, integrity);
        };
        return fetch(raw, init).then(
            function (response) {
                return response.ok ? response : encoded();
            },
            encoded
        ).catch(function () {
            return fetch(defaultUri, init);
        });
    }

    function showBootError() {
        var app = document.getElementById("app");
        if (!app) {
            return;
        }
        app.innerHTML =
            '<div style="max-width:36rem;margin:4rem auto;padding:0 1rem;font-family:sans-serif">' +
            '<h1 style="font-size:1.25rem">Não foi possível iniciar o aplicativo</h1>' +
            '<p>A rede ou o proxy corporativo bloqueou arquivos necessários ao ' +
            'carregamento (filtro de downloads por tipo de mídia).</p>' +
            '<p>Recarregue a página. Se o problema persistir, contate o suporte ' +
            'de TI ou acesse por outra rede.</p>' +
            '<p><a href=".">Recarregar</a></p></div>';
    }

    // Drawer mobile: Escape fecha o menu. Listener document-level porque o
    // foco pode estar em qualquer elemento; o MainLayout registra um
    // DotNetObjectReference no first render.
    var khNavHandler = null;
    window.khNav = {
        registerEscape: function (dotNetRef) {
            if (khNavHandler) {
                document.removeEventListener("keydown", khNavHandler);
            }
            khNavHandler = function (e) {
                if (e.key === "Escape") {
                    dotNetRef.invokeMethodAsync("OnEscapeKey");
                }
            };
            document.addEventListener("keydown", khNavHandler);
        },
        unregisterEscape: function () {
            if (khNavHandler) {
                document.removeEventListener("keydown", khNavHandler);
                khNavHandler = null;
            }
        }
    };

    try {
        Blazor.start({
            loadBootResource: function (type, name, defaultUri, integrity) {
                try {
                    var path = new URL(defaultUri, document.baseURI).pathname;
                    if (!path.includes(frameworkSegment) || path.endsWith(".js")) {
                        return null; // default loading — proxies allow .js
                    }
                    var fileName = path.substring(path.lastIndexOf("/") + 1);
                    var dot = fileName.lastIndexOf(".");
                    if (dot <= 0) {
                        return null; // no extension to split — default loading
                    }
                    return loadAsset(
                        defaultUri,
                        integrity,
                        fileName.substring(0, dot),
                        fileName.substring(dot + 1));
                } catch {
                    return null;
                }
            }
        }).catch(showBootError);
    } catch {
        showBootError();
    }
})();
