"""Disassemble one native method from the preserved GameAssembly.dll, naming call and jump targets.

Usage:
    python -B tools/decompile/trace_rva.py 0xE4CBA0            # RVA or VA; length from methods.tsv
    python -B tools/decompile/trace_rva.py 0x1800029D0 0x90    # give a byte length when there is no index row
    python -B tools/decompile/trace_rva.py --decompiled D:/other/decompiled 0xE4CBA0

Read-only. Needs capstone and pefile (pip install capstone pefile; checked with 5.0.3 and 2024.8.26).
The decompile folder defaults to artiffacts/decompiled next to this repository. Check the build first:
its input hashes must match manifest.json. A call label joining several names with " | " is code that
several methods share. Linear disassembly can run past a method's end into padding or data.
"""
import argparse
import csv
from collections import defaultdict
from pathlib import Path

import pefile
from capstone import CS_ARCH_X86, CS_MODE_64, Cs
from capstone.x86_const import X86_OP_IMM

DEFAULT = Path(__file__).resolve().parents[2] / "artiffacts" / "decompiled"

parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
parser.add_argument("address", help="RVA or preferred VA, for example 0xE4CBA0 or 0x180E4CBA0")
parser.add_argument("length", nargs="?", help="bytes to disassemble (default: the indexed method length)")
parser.add_argument("--decompiled", type=Path, default=DEFAULT, help="decompile output folder")
args = parser.parse_args()

root = args.decompiled
pe = pefile.PE(str(root / "input" / "GameAssembly.dll"), fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
by_rva = defaultdict(list)
with (root / "indexes" / "methods.tsv").open(encoding="utf-8-sig", newline="") as f:
    for row in csv.DictReader(f, delimiter="\t"):
        by_rva[int(row["rva"], 16)].append(row)


def names(entries):
    return " | ".join(dict.fromkeys(f'{r["type"]} {r["signature"]}' for r in entries))


address = int(args.address, 0)
rva = address - base if address >= base else address
rows = by_rva.get(rva, [])
if args.length:
    length = int(args.length, 0)
elif rows:
    length = max(int(r["length"], 16) for r in rows)
else:
    parser.error(f"{rva:#x} has no methods.tsv row; give a length")

section = pe.get_section_by_rva(rva)
offset = pe.get_offset_from_rva(rva)
if not section or not section.Characteristics & 0x20000000:
    parser.error(f"{rva:#x} is not in an executable section")
if offset + length > section.PointerToRawData + section.SizeOfRawData:
    parser.error("the length runs past the end of the section")
print(f"RVA={rva:#x} VA={base + rva:#x} raw={offset:#x} {names(rows)}")

decoder = Cs(CS_ARCH_X86, CS_MODE_64)
decoder.detail = True
for insn in decoder.disasm(pe.get_data(rva, length), base + rva):
    label = ""
    if insn.mnemonic in {"call", "jmp"} and insn.operands[0].type == X86_OP_IMM:
        label = names(by_rva.get(insn.operands[0].imm - base, []))
    print(f"{insn.address:x}  {insn.mnemonic:7} {insn.op_str}", label)
