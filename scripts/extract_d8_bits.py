"""從 D8Bean.java 抽出 0xD8 功能旗標回應的欄位表。

D8Bean 建構子把 payload[N] 交給 BN Bean，Bean 內用位元運算拆旗標。
用法：python -I scripts/extract_d8_bits.py <D8Bean.java> [輸出 JSON]
"""
import json
import re
import sys

text = open(sys.argv[1], encoding="utf-8").read()

# payload 位元組 -> Bean 類別 / 純欄位
ctor = text[text.index("public D8Bean(byte[] payload)"):]
ctor = ctor[:ctor.index("\n    }\n")]
byte_to_bean = {int(n): cls for cls, n in
                re.findall(r"new (B\d+Bean)\(payload\[(\d+)\]\)", ctor)}
plain = {int(n): f for f, n in re.findall(r"this\.(\w+) = payload\[(\d+)\]", ctor)}

# 各 Bean 的建構子
beans = {}
for m in re.finditer(r"public static final class (B\d+Bean) extends BaseBean \{", text):
    name = m.group(1)
    body_start = text.index(f"public {name}(int i) {{", m.end())
    body = text[body_start:text.index("\n        }\n", body_start)]
    fields = []
    for line in body.splitlines():
        line = line.strip()
        g = re.match(r"this\.(\w+) = \(\(?i >> (\d+)\) & 1\)? == 1;", line)
        if g:
            fields.append({"field": g.group(1), "bit": int(g.group(2))})
            continue
        g = re.match(r"this\.(\w+) = \(i & 1\) == 1;", line)
        if g:
            fields.append({"field": g.group(1), "bit": 0})
            continue
        if line.startswith("this.") or re.match(r"int \w+ = ", line):
            fields.append({"raw": line})
    beans[name] = fields

out = {}
for n in sorted(set(byte_to_bean) | set(plain)):
    if n in byte_to_bean:
        out[n] = {"bean": byte_to_bean[n], "fields": beans.get(byte_to_bean[n], [])}
    else:
        out[n] = {"plain": plain[n]}

dst = sys.argv[2] if len(sys.argv) > 2 else None
js = json.dumps(out, indent=1, ensure_ascii=False)
if dst:
    open(dst, "w", encoding="utf-8", newline="\n").write(js)
    print(f"{len(out)} bytes described -> {dst}")
else:
    print(js)
