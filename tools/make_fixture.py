#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""ACTGS 归档容器 —— 合成 fixture 生成器 + 自测（不含任何真实游戏数据）

生成两种布局的假容器，再用与 C# 插件 `Archives/ACTGS ARC.cs` 完全相同的规则
解析回来，断言逐字节还原。可直接用于 CI。

    python tools/make_fixture.py            生成 Fixtures/ 并自测
    python tools/make_fixture.py --clean    自测后删除 Fixtures/

布局与密钥来源：对 ACTGS ver 2.33 的静态反编译。
"""
import pathlib
import shutil
import struct
import sys

HERE = pathlib.Path(__file__).resolve().parent.parent
FIX = HERE / "Fixtures"
KEY = bytes.fromhex("1105223341")
ALIGN = 16


def align_up(v):
    return (v + ALIGN - 1) // ALIGN * ALIGN


# ---------------- 变换（与 C# 一一对应） ----------------
def xor_stream(data, phase=0):
    return bytes(data[i] ^ KEY[(i + phase) % len(KEY)] for i in range(len(data)))


def scr_encode(plain):
    """明文首字节 'N'(0x4E) -> 密文首字节标记 'X'(0x58)，其余按 (i-1)%5 加密。"""
    out = bytearray([0x58])
    for i in range(1, len(plain)):
        out.append(plain[i] ^ KEY[(i - 1) % len(KEY)])
    return bytes(out)


def scr_decode(blob):
    o = bytearray([0x4E])
    for i in range(1, len(blob)):
        o.append(blob[i] ^ KEY[(i - 1) % len(KEY)])
    return bytes(o)


# ---------------- 合成内容（全 ASCII，不含游戏原文） ----------------
def fake_script(tag):
    lines = [
        b"N",
        b"",
        b"; fixture comment",
        b"set_all_fade 0",
        b"",
        b'vo ""',
        ("HELLO-FIXTURE-%s line one." % tag).encode("ascii"),
        ("HELLO-FIXTURE-%s line two." % tag).encode("ascii"),
        b"ret",
        b"",
        b'bgm1 "bgmXX"',
        b"",
    ]
    return b"\r\n".join(lines)


def fake_bmp(w=64, h=32):
    row = bytearray()
    for x in range(w):
        row += bytes([(x * 4) & 0xFF, (x * 2) & 0xFF, 0x80])
    row += b"\x00" * ((4 - len(row) % 4) % 4)
    body = bytes(row) * h
    hdr = b"BM" + struct.pack("<IHHI", 54 + len(body), 0, 0, 54)
    hdr += struct.pack("<IiiHHIIiiII", 40, w, h, 1, 24, 0, len(body), 0, 0, 0, 0)
    return hdr + body


def fake_psd(w=48, h=24):
    hdr = b"8BPS" + struct.pack(">H", 1) + b"\x00" * 6
    hdr += struct.pack(">HIIHH", 3, h, w, 8, 3)
    return hdr + b"\x00" * (w * h * 3)


# ---------------- 布局 B：连续索引（arc.scr 型） ----------------
def build_scr(files):
    n = len(files)
    index_len = n * 32
    data_start = 16 + index_len
    idx = bytearray()
    body = bytearray()
    off = data_start
    for name, plain in files:
        blob = scr_encode(plain)
        rec = struct.pack("<II", off, len(blob)) + name.encode("cp932")
        rec += b"\x00" * (32 - len(rec))
        idx += rec
        body += blob + b"\x00" * (align_up(len(blob)) - len(blob))
        off += align_up(len(blob))
    return struct.pack("<I", n) + b"\x00" * 12 + xor_stream(bytes(idx)) + bytes(body)


# ---------------- 布局 A：交错（arc.cg 型） ----------------
def build_cg(files):
    n = len(files)
    out = bytearray()
    out += struct.pack("<I", n) + b"\x00" * 12
    out += b"\x00" * (0x1000 - 16)
    for name, content in files:
        rec = struct.pack("<I", len(content)) + name.encode("cp932")
        rec += b"\x00" * (32 - len(rec))
        out += xor_stream(rec) + xor_stream(content)
        out += b"\x00" * (align_up(len(content)) - len(content))
    return bytes(out)


# ---------------- 解析（模拟 C# 插件） ----------------
def try_cg(buf):
    n = struct.unpack_from("<I", buf, 0)[0]
    file_len = len(buf)
    if file_len < 0x1000 + 32:
        return None
    pos = 0x1000
    items = []
    for _ in range(n):
        if pos + 32 > file_len:
            return None
        rec = xor_stream(buf[pos:pos + 32])
        size = struct.unpack_from("<I", rec, 0)[0]
        nm = rec[4:32].split(b"\x00")[0]
        if size <= 0 or size > file_len or not nm:
            return None
        d = pos + 32
        if d + size > file_len:
            return None
        items.append((nm.decode("cp932"), xor_stream(buf[d:d + size])))
        pos = pos + 32 + align_up(size)      # 下一条记录 = 本记录 + 32 + align16(size)
    if pos != file_len:                      # 强判据：最后一条数据块结束即文件末尾
        return None
    return items or None


def try_scr(buf):
    n = struct.unpack_from("<I", buf, 0)[0]
    file_len = len(buf)
    index_len = n * 32
    data_start = 16 + index_len
    if n <= 0 or n > 200_000 or data_start > file_len:
        return None
    idx = xor_stream(buf[16:data_start])
    items = []
    for i in range(n):
        r = idx[i * 32:(i + 1) * 32]
        off, size = struct.unpack_from("<II", r, 0)
        nm = r[8:32].split(b"\x00")[0]
        if not nm or size <= 0 or off < data_start or off + size > file_len:
            return None
        if i == 0 and off != data_start:
            return None
        items.append((nm.decode("cp932"), scr_decode(buf[off:off + size])))
    return items


def parse(buf):
    n = struct.unpack_from("<I", buf, 0)[0]
    if n <= 0 or n > 200_000:
        return None, []
    got = try_cg(buf)
    if got:
        return "cg", got
    got = try_scr(buf)
    if got:
        return "scr", got
    return None, []


# ---------------- 主流程 ----------------
def main():
    clean = "--clean" in sys.argv
    FIX.mkdir(exist_ok=True)

    scr_files = [("fix-001.scr", fake_script("A")),
                 ("fix-002.scr", fake_script("BB")),
                 ("fix-003.scr", fake_script("CCC"))]
    cg_files = [("fix01a.bmp", fake_bmp(64, 32)),
                ("fix01a_01.psd", fake_psd(48, 24)),
                ("fix02a.bmp", fake_bmp(32, 16))]

    (FIX / "fixture.scr").write_bytes(build_scr(scr_files))
    (FIX / "fixture.cg").write_bytes(build_cg(cg_files))
    for f in ("fixture.scr", "fixture.cg"):
        print("生成 %-14s %d B" % (f, (FIX / f).stat().st_size))

    k1, got1 = parse((FIX / "fixture.scr").read_bytes())
    k2, got2 = parse((FIX / "fixture.cg").read_bytes())
    print("解析 fixture.scr -> kind=%s 条目=%s" % (k1, [g[0] for g in got1]))
    print("解析 fixture.cg  -> kind=%s 条目=%s" % (k2, [g[0] for g in got2]))

    assert k1 == "scr", "fixture.scr 未被识别为连续索引布局"
    assert [x[0] for x in got1] == [e[0] for e in scr_files], "scr 条目名不符"
    for (n1, c1), (n2, c2) in zip(got1, scr_files):
        assert c1 == c2, "scr 内容不符: %s" % n1

    assert k2 == "cg", "fixture.cg 未被识别为交错布局"
    assert [x[0] for x in got2] == [e[0] for e in cg_files], "cg 条目名不符"
    for (n1, c1), (n2, c2) in zip(got2, cg_files):
        assert c1 == c2, "cg 内容不符: %s" % n1

    assert got2[0][1][:2] == b"BM", "cg 首条目不是 BMP 签名"
    assert got2[1][1][:4] == b"8BPS", "cg 第二条目不是 PSD 签名"
    assert got1[0][1][:1] == b"N", "scr 明文首字节不是 'N'"
    assert b"HELLO-FIXTURE-A line one." in got1[0][1], "scr 正文缺失"

    print("\n全部断言通过：两种布局均可被同一套规则无损解析。")

    if clean:
        shutil.rmtree(FIX)
        print("已清理 Fixtures/")


if __name__ == "__main__":
    main()
