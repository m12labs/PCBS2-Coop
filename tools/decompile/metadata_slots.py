"""Decode IL2CPP metadata cells and vtable slots from the preserved global-metadata.dat (version 31).

Usage:
    python -B tools/decompile/metadata_slots.py cell 0x1845EF918 0x184624A50
        Prints the method a metadata-usage cell points at (the cell VA is the next instruction plus a
        RIP-relative displacement; delegates and listeners load their target this way).
    python -B tools/decompile/metadata_slots.py slots Job@0x2000177 37 39 IEmail:15
        Prints a type's vtable entries. A number is a class slot: for a call through [klass+off],
        slot = (off - vtable base) / 16. Iface:n is an interface call: that interface's offset in
        this type plus n. Type names repeat across assemblies, so pin one with @token (the
        [Token] line in DiffableCs) or #index.
    --decompiled D:/other/decompiled   uses another decompile folder (default: artiffacts/decompiled).

Read-only. Needs pefile for "cell" (pip install pefile). Only ordinary method definitions are decoded;
generic method references go through the method-spec table and are reported as another usage kind.
"""
import argparse
import sys
from pathlib import Path
from struct import unpack_from as read

DEFAULT = Path(__file__).resolve().parents[2] / "artiffacts" / "decompiled"

parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
parser.add_argument("mode", choices=["cell", "slots"])
parser.add_argument("items", nargs="+", help="cell VAs, or a type followed by slots")
parser.add_argument("--decompiled", type=Path, default=DEFAULT, help="decompile output folder")
args = parser.parse_args()

root = args.decompiled / "input"
data = (root / "global-metadata.dat").read_bytes()
if read("<II", data) != (0xFAB11BAF, 31):
    sys.exit("not IL2CPP metadata version 31; the table layout here would be wrong")


def section(number):
    return read("<II", data, 8 + 8 * number)


strings, strings_size = section(2)
methods, methods_size = section(5)
vtables, _ = section(17)
interfaces, _ = section(18)
types, types_size = section(19)
METHOD_ROW, TYPE_ROW = 36, 88
assert methods_size % METHOD_ROW == types_size % TYPE_ROW == 0


def name(index):
    start = strings + index
    return data[start:data.index(b"\0", start, strings + strings_size)].decode()


def type_row(index):
    return types + TYPE_ROW * index


def type_name(index):
    namespace = name(read("<I", data, type_row(index) + 4)[0])
    return (namespace + "." if namespace else "") + name(read("<I", data, type_row(index))[0])


def type_token(index):
    return read("<I", data, type_row(index) + 84)[0]


def method(index):
    at = methods + METHOD_ROW * index
    method_name, owner = read("<II", data, at)
    token = read("<I", data, at + 24)[0]
    slot = read("<H", data, at + 32)[0]
    return f"{type_name(owner)}.{name(method_name)} token={token:#x} slot={slot}"


def method_ref(value):
    if value >> 29 != 3 or not value & 1:
        return f"not a method definition (usage {value:#x}, kind {value >> 29})"
    return method((value & 0x1FFFFFFF) >> 1)


def find_type(spec):
    if spec.startswith("#"):
        return int(spec[1:])
    full, _, token = spec.partition("@")
    hits = [i for i in range(types_size // TYPE_ROW)
            if (type_name(i) == full or type_name(i).split(".")[-1] == full)
            and (not token or type_token(i) == int(token, 0))]
    if len(hits) != 1:
        found = [f"#{h} {type_name(h)}@{type_token(h):#x}" for h in hits]
        sys.exit(f"{spec}: {len(hits)} matches {found}; add @token from DiffableCs")
    return hits[0]


if args.mode == "cell":
    import pefile

    pe = pefile.PE(str(root / "GameAssembly.dll"), fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase
    for item in args.items:
        va = int(item, 0)
        print(hex(va), method_ref(pe.get_qword_at_rva(va - base)))
else:
    t = find_type(args.items[0])
    at = type_row(t)
    vtable, interface_start = read("<ii", data, at + 56)
    counts = read("<8H", data, at + 64)
    slot_count, interface_count = counts[5], counts[7]
    offsets = dict(read("<ii", data, interfaces + 8 * (interface_start + k)) for k in range(interface_count))
    print(f"{type_name(t)} #{t} token={type_token(t):#x} slots={slot_count}")
    for item in args.items[1:]:
        if ":" in item:
            interface, n = item.split(":")
            byval = read("<I", data, type_row(find_type(interface)) + 8)[0]
            if byval not in offsets:
                print(f"  {item}: {type_name(t)} does not implement {interface}")
                continue
            slot, how = offsets[byval] + int(n, 0), f"{interface} base {offsets[byval]} + {n}"
        else:
            slot, how = int(item, 0), "class slot"
        entry = method_ref(read("<I", data, vtables + 4 * (vtable + slot))[0]) if 0 <= slot < slot_count else "out of range"
        print(f"  {slot} ({how}): {entry}")
