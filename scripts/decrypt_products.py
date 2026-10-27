"""解密 ConneX APK 內的 assets/products_release.json。

演算法來自 com.edifier.lib_common.utils.CipherUtil：
Base64 -> AES-256-CBC/PKCS5 -> UTF-8 JSON。金鑰與 IV 寫死在官方 APK 內（CipherUtil），
**不放進版控**：存在被 gitignore 的 docs/apk/cipher.json（{"key": "...", "iv": "..."}），
或用環境變數 EDIFIER_CIPHER_FILE 指向別處。

用法：uv run --with cryptography scripts/decrypt_products.py <輸入檔> <輸出檔>
輸出含官方資料，請放在專案外（例如 C:\\Work），不要進版控。
"""
import base64
import sys

from cryptography.hazmat.primitives import padding
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

import json
import os
from pathlib import Path

CIPHER_FILE = Path(os.environ.get("EDIFIER_CIPHER_FILE", Path(__file__).resolve().parent.parent / "docs/apk/cipher.json"))


def _load_cipher():
    try:
        d = json.loads(CIPHER_FILE.read_text(encoding="utf-8"))
        return d["key"].encode(), d["iv"].encode()
    except (OSError, KeyError, ValueError) as e:
        raise SystemExit(f"找不到金鑰檔 {CIPHER_FILE}：請從官方 APK 的 CipherUtil 取得 key 與 iv 存成 JSON（{e}）")


KEY, IV = _load_cipher()


def decrypt(b64_text: bytes) -> str:
    raw = base64.b64decode(b64_text)
    dec = Cipher(algorithms.AES(KEY), modes.CBC(IV)).decryptor()
    padded = dec.update(raw) + dec.finalize()
    unpad = padding.PKCS7(128).unpadder()
    return (unpad.update(padded) + unpad.finalize()).decode("utf-8")


if __name__ == "__main__":
    src, dst = sys.argv[1], sys.argv[2]
    with open(src, "rb") as f:
        text = decrypt(f.read())
    with open(dst, "w", encoding="utf-8") as f:
        f.write(text)
    print(f"ok: {len(text)} chars -> {dst}")
