"""從 ProtocolConfigurations.getANC 抽出 groupIndex -> [(modeIndex, AncEnum)]。

用法：python -I scripts/extract_anc_groups.py <ProtocolConfigurations.java>
輸出 JSON 到 stdout。
"""
import json
import re
import sys

text = open(sys.argv[1], encoding="utf-8").read()
start = text.index("List<AncConfigBean> getANC(int groupIndex)")
end = text.index("\n    public final", start + 10)
body = text[start:end]

groups = {}
pending = []
for line in body.splitlines():
    pending += [int(n) for n in re.findall(r"case (\d+):", line)]
    modes = re.findall(r"new AncConfigBean\((\d+), \"[^\"]*\", AncEnum\.(\w+)", line)
    if modes:
        for g in pending:
            groups[g] = [(int(i), n) for i, n in modes]
        pending = []
    elif "default" in line:
        pending = []

print(json.dumps(groups, indent=1))
print(f"# {len(groups)} groups, {sum(1 for _ in re.finditer(r'Omitted', body))} omitted", file=sys.stderr)
