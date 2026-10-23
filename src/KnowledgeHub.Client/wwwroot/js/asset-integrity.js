// Shared by js/boot.js (page scope) and service-worker.published.js (worker
// scope via importScripts): base64 decoding and SRI digest verification for
// the /framework-assets ?enc=b64 mirror layer (SPEC-20260915 proxy hardening).
(function (root) {
    "use strict";

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

    // The manifest/boot integrity is "sha256-<base64>" (possibly a list); the
    // ?enc=b64 payload transforms the bytes on the wire so the browser cannot
    // check it — verify the decoded bytes ourselves before handing them over.
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
        if (hash === null || !crypto || !crypto.subtle) {
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

    root.khAssets = { decodeBase64: decodeBase64, verifyIntegrity: verifyIntegrity };
})(typeof self !== "undefined" ? self : window);
