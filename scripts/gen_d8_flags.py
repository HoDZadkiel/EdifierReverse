"""從 docs/d8-bits.json 產生 src/edifier_reverse/d8_flags.py。

用法：python -I scripts/gen_d8_flags.py docs/d8-bits.json src/edifier_reverse/d8_flags.py
鍵名為 "b<位元組>_<官方欄位名>"，因為同名欄位（如 isSupportTimeCalibration）會出現在不同位元組。
"""
import json
import sys

src, dst = sys.argv[1], sys.argv[2]
data = json.load(open(src, encoding="utf-8"))

flags = {}
for byte, info in data.items():
    for f in info.get("fields", []):
        if "bit" in f:
            flags[f"b{byte}_{f['field']}"] = (int(byte), f["bit"])

lines = ['"""0xD8 功能旗標位置（byte, bit）。', "",
         "由 scripts/gen_d8_flags.py 自動產生，來源 docs/d8-bits.json。", '"""', "",
         "FLAGS = {"]
lines += [f'    "{k}": ({b}, {bit}),' for k, (b, bit) in flags.items()]
lines.append("}")
open(dst, "w", encoding="utf-8", newline="\n").write("\n".join(lines) + "\n")
print(f"{len(flags)} flags -> {dst}")
