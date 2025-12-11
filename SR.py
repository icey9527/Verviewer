import sys
import os
import re
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
OUT_ROOT = os.path.join(SCRIPT_DIR, "out")
RES_DIR = os.path.join(OUT_ROOT, "res")


@dataclass
class Placeholder:
    name: str
    expr: str
    format_spec: str = ""


@dataclass
class StringMatch:
    start: int
    end: int
    original: str
    template: str
    string_type: str
    placeholders: list = field(default_factory=list)
    warning: str = ""


def has_chinese(s: str) -> bool:
    return any("\u4e00" <= c <= "\u9fff" for c in s)


def extract_param_name(expr: str) -> str:
    expr = expr.replace("?.", ".").replace("?[", "[")
    ids = re.findall(r"\b([a-zA-Z_]\w*)\b", expr)
    if not ids:
        return "arg"
    last = ids[-1]
    if last in ("Count", "Length", "Value", "Text", "Name", "ToString") and len(ids) > 1:
        return ids[-2] + last
    return last


def parse_interpolated(content: str):
    temp = content.replace("{{", "\x00LB\x00").replace("}}", "\x00RB\x00")
    placeholders = []
    warnings = []
    used = {}
    result = []
    i = 0
    while i < len(temp):
        if temp[i] != "{":
            result.append(temp[i])
            i += 1
            continue
        depth = 1
        j = i + 1
        while j < len(temp) and depth > 0:
            ch = temp[j]
            if ch == "{":
                depth += 1
            elif ch == "}":
                depth -= 1
            j += 1
        if depth != 0:
            warnings.append("大括号不匹配")
            result.append(temp[i])
            i += 1
            continue
        inner = temp[i + 1 : j - 1]
        expr = inner
        fmt = ""
        for k in range(len(inner) - 1, -1, -1):
            if inner[k] == ":":
                after = inner[k + 1 :]
                if after and re.match(r"^[A-Za-z0-9.#,]+$", after):
                    expr = inner[:k]
                    fmt = after
                    break
        expr = expr.strip()
        fmt = fmt.strip()
        if '"' in expr or "'" in expr or "=>" in expr:
            warnings.append(f"{{{expr}}} 包含复杂表达式")
        base = extract_param_name(expr)
        if base in used:
            used[base] += 1
            name = f"{base}{used[base]}"
        else:
            used[base] = 1
            name = base
        placeholders.append(Placeholder(name, expr, fmt))
        result.append("{" + name + "}")
        i = j
    template = "".join(result).replace("\x00LB\x00", "{{").replace("\x00RB\x00", "}}")
    return template, placeholders, warnings


def find_strings(text: str):
    results = []
    processed = []

    def overlaps(start: int, end: int) -> bool:
        return any(not (end <= s or start >= e) for s, e in processed)

    interp_re = re.compile(r'(?<!@)\$"((?:[^"\\{}]|\\.|{[^}]*})*)"')
    interp_matches = list(interp_re.finditer(text))
    i = 0
    while i < len(interp_matches):
        m = interp_matches[i]
        if m.start() > 0 and text[m.start() - 1] == "@":
            i += 1
            continue
        group = [m]
        j = i + 1
        while j < len(interp_matches):
            between = text[group[-1].end() : interp_matches[j].start()]
            if re.match(r"^[\s\n]*\+[\s\n]*$", between):
                group.append(interp_matches[j])
                j += 1
            else:
                break
        content = "".join(g.group(1) for g in group)
        if has_chinese(content):
            template, phs, warns = parse_interpolated(content)
            results.append(
                StringMatch(
                    start=group[0].start(),
                    end=group[-1].end(),
                    original=text[group[0].start() : group[-1].end()],
                    template=template,
                    string_type="interpolated",
                    placeholders=phs,
                    warning="; ".join(warns),
                )
            )
            processed.append((group[0].start(), group[-1].end()))
        i = j

    for m in re.finditer(r'"((?:[^"\\]|\\.)*)"', text):
        if overlaps(m.start(), m.end()):
            continue
        if m.start() > 0 and text[m.start() - 1] in "$@":
            continue
        if m.start() > 1 and text[m.start() - 2 : m.start()] in ("$@", "@$"):
            continue
        content = m.group(1)
        if has_chinese(content):
            results.append(
                StringMatch(
                    start=m.start(),
                    end=m.end(),
                    original=m.group(0),
                    template=content,
                    string_type="normal",
                )
            )
            processed.append((m.start(), m.end()))

    for m in re.finditer(r'(\$@|@\$|@)"((?:[^"]|"")*)"', text):
        if overlaps(m.start(), m.end()):
            continue
        content = m.group(2)
        if has_chinese(content):
            results.append(
                StringMatch(
                    start=m.start(),
                    end=m.end(),
                    original=m.group(0),
                    template=content.replace('""', '"'),
                    string_type="verbatim",
                    warning=f"逐字面字符串 ({m.group(1)}) 需手动处理",
                )
            )
            processed.append((m.start(), m.end()))

    return sorted(results, key=lambda x: x.start)


def gen_replacement(key: str, match: StringMatch) -> str | None:
    if match.string_type == "verbatim":
        return None
    if not match.placeholders:
        return f'SR.Get("{key}")'
    args = []
    for p in match.placeholders:
        if p.format_spec:
            args.append(f'("{p.name}", $"{{{p.expr}:{p.format_spec}}}")')
        else:
            args.append(f'("{p.name}", {p.expr})')
    return f'SR.F("{key}", {", ".join(args)})'


def save_resx(path: str, data: dict[str, str]):
    root = ET.Element("root")
    for name, value in (
        ("resmimetype", "text/microsoft-resx"),
        ("version", "2.0"),
        ("reader", "System.Resources.ResXResourceReader, System.Windows.Forms"),
        ("writer", "System.Resources.ResXResourceWriter, System.Windows.Forms"),
    ):
        h = ET.SubElement(root, "resheader", name=name)
        v = ET.SubElement(h, "value")
        v.text = value
    for key in sorted(data):
        d = ET.SubElement(root, "data", name=key)
        d.set("xml:space", "preserve")
        v = ET.SubElement(d, "value")
        v.text = data[key] or ""

    def indent(elem, level=0):
        i = "\n" + level * "  "
        if len(elem):
            if not elem.text or not elem.text.strip():
                elem.text = i + "  "
            for e in elem:
                indent(e, level + 1)
            if not e.tail or not e.tail.strip():
                e.tail = i
        elif level and (not elem.tail or not elem.tail.strip()):
            elem.tail = i

    indent(root)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)


def sanitize_prefix(path: str) -> str:
    base = os.path.splitext(os.path.basename(path))[0]
    base = re.sub(r"\W+", "_", base).strip("_")
    return base or "Key"


def main():
    argv = sys.argv[1:]
    no_resx = False

    if "--no-resx" in argv:
        no_resx = True
        argv.remove("--no-resx")

    if len(argv) < 1:
        print("用法: python sr.py <源码根目录> [输出根目录] [--no-resx]")
        return

    source_root = os.path.abspath(argv[0])

    # 可选的输出目录参数
    global OUT_ROOT, RES_DIR
    if len(argv) >= 2:
        OUT_ROOT = os.path.abspath(argv[1])
        RES_DIR = os.path.join(OUT_ROOT, "res")

    if not os.path.isdir(source_root):
        print("无效目录:", source_root)
        return

    os.makedirs(OUT_ROOT, exist_ok=True)

    data: dict[str, str] = {}
    template_to_key: dict[str, str] = {}
    counters: dict[str, int] = {}
    manual = []
    files = 0
    total_strings = 0

    for dirpath, _, filenames in os.walk(source_root):
        for fn in filenames:
            if not fn.lower().endswith(".cs"):
                continue
            cs_path = os.path.join(dirpath, fn)
            with open(cs_path, "r", encoding="utf-8", errors="ignore") as f:
                text = f.read()
            matches = find_strings(text)
            if not matches:
                continue
            prefix = sanitize_prefix(cs_path)
            count = counters.get(prefix, 0)
            new_text = text
            replaced = 0
            for match in reversed(matches):
                line = text.count("\n", 0, match.start) + 1
                if match.string_type == "verbatim":
                    manual.append((cs_path, line, match.original[:80], match.warning))
                    continue
                if match.warning:
                    manual.append((cs_path, line, match.original[:80], match.warning))
                if match.template in template_to_key:
                    key = template_to_key[match.template]
                else:
                    count += 1
                    key = f"{prefix}_{count}"
                    template_to_key[match.template] = key
                    data[key] = match.template
                replacement = gen_replacement(key, match)
                if replacement:
                    new_text = new_text[: match.start] + replacement + new_text[match.end :]
                    replaced += 1
            counters[prefix] = count
            if replaced:
                files += 1
                total_strings += replaced
                rel = os.path.relpath(cs_path, source_root)
                out_path = os.path.join(OUT_ROOT, rel)
                os.makedirs(os.path.dirname(out_path), exist_ok=True)
                with open(out_path, "w", encoding="utf-8") as f:
                    f.write(new_text)
                print(f"{rel}: {replaced}")

    print()
    if data:
        if no_resx:
            print("检测到字符串，但已启用 --no-resx，跳过生成 resx")
        else:
            os.makedirs(RES_DIR, exist_ok=True)
            save_resx(os.path.join(RES_DIR, "Strings.resx"), data)
            save_resx(os.path.join(RES_DIR, "Strings.zh-Hans.resx"), data)
            print(f"完成 {files} 文件, {total_strings} 字符串")
            print("res 输出目录:", RES_DIR)
    else:
        print("未找到中文字符串")
    if manual:
        print(f"\n需手动处理 {len(manual)} 处:")
        for path, line, text, reason in manual:
            print(f"  {os.path.relpath(path, source_root)}:{line} - {reason}")


if __name__ == "__main__":
    main()