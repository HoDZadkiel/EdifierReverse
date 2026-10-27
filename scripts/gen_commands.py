"""從反編譯的 CommandIndex.java 產生 src/edifier_reverse/commands.py。

用法：python -I scripts/gen_commands.py <CommandIndex.java> <輸出 commands.py>
"""
import re
import sys

src, dst = sys.argv[1], sys.argv[2]
text = open(src, encoding="utf-8").read()
items = [(m.group(1), int(m.group(2)))
         for m in re.finditer(r"public static final int (\w+) = (\d+);", text)]
items.sort(key=lambda x: x[1])

values = [v for _, v in items]
dups = sorted({v for v in values if values.count(v) > 1})

lines = ['"""指令碼（來源：ConneX 1.0.31 CommandIndex.java，靜態分析，尚未實機驗證）。',
         "",
         "由 scripts/gen_commands.py 自動產生，名稱沿用官方，數值為十進位。",
         '"""', ""]
lines += [f"{n.upper()} = {v}  # 0x{v:02X}" for n, v in items]
open(dst, "w", encoding="utf-8").write("\n".join(lines) + "\n")
print(f"{len(items)} commands -> {dst}; duplicated values: {dups}")
