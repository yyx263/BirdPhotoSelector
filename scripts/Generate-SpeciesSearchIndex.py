#!/usr/bin/env python3
"""Generate the offline search-only pinyin index; never edit species/model assets.

Install the pinned wheel into .tools/search-python first (no global environment):
  .tools/species-python/python.exe -m pip install --no-deps --no-compile \
    --require-hashes --only-binary=:all: --target .tools/search-python \
    'https://files.pythonhosted.org/packages/b9/7b/4cabc76fcc21c3c7d5c671d8783984d30ac9d3bb387c4ba784fca3cdfa3a/pypinyin-0.55.0-py2.py3-none-any.whl#sha256=d53b1e8ad2cdb815fb2cb604ed3123372f5a28c6f447571244aca36fc62a286f'
Then run this script, or --check to verify without writing any file.
Python and pypinyin are build-time dependencies only; the application reads JSON.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
PYTHON_TARGET = ROOT / ".tools/search-python"
PINNED_VERSION = "0.55.0"
WHEEL_SHA256 = "d53b1e8ad2cdb815fb2cb604ed3123372f5a28c6f447571244aca36fc62a286f"
SOURCE_PATHS = (
    "models/bioclip2/candidates.json",
    "models/bioclip2/china-unmatched.json",
)
OUTPUT = ROOT / "Assets/SpeciesSearchData.json"

# A single canonical reading is chosen for each name: no heteronym expansion.
# Rules apply only in these bird-name contexts, never to arbitrary user text.
# References establish readings/meanings; no dictionary definitions are copied.
RULES = (
    {
        "Id": "long-anatomy",
        "Patterns": ["长尾", "长嘴", "长耳", "长趾", "长脚"],
        "Character": "长", "Syllable": "chang",
        "Reason": "Length adjective in the listed anatomical bird-name phrases.",
        "Sources": ["https://dict.concised.moe.edu.tw/dictView.jsp?ID=32100&la=0&powerMode=1"],
    },
    {
        "Id": "curlew-shao",
        "Patterns": ["杓鹬"], "Character": "杓", "Syllable": "shao",
        "Reason": "Canonical curlew name reading; not the unrelated dipper-handle biao reading.",
        "Sources": [
            "https://police.yinchuan.gov.cn/zzb/jwdt/jcjx_71131/202405/t20240516_4540545.html",
            "https://eaaflyway.net/wp-content/uploads/2018/01/YJReport28March2014.pdf",
        ],
    },
    {
        "Id": "tibetan-zang",
        "Names": ["藏雀", "藏马鸡", "藏鹀", "藏雪雀", "藏黄雀", "藏雪鸡", "藏乌鸫", "西藏毛腿沙鸡"],
        "Character": "藏", "Syllable": "zang",
        "Reason": "Geographic Tibetan designation in these explicitly reviewed names.",
        "Sources": ["https://dict.revised.moe.edu.tw/dictView.jsp?ID=9592&la=0&powerMode=0"],
    },
    {
        "Id": "rare-pi", "Character": "䴙", "Syllable": "pi",
        "Reason": "Pinned rare bird character, equivalent to 鸊/鷿; tones are not indexed.",
        "Sources": ["https://dict.variants.moe.edu.tw/dictView.jsp?ID=77633&la=0"],
    },
    {
        "Id": "rare-ti", "Character": "䴘", "Syllable": "ti",
        "Reason": "Pinned rare bird character, equivalent to 鷉/鷈; regional tone differences do not change ti.",
        "Sources": ["https://dict.variants.moe.edu.tw/dictView.jsp?ID=77441&la=1"],
    },
    {
        "Id": "rare-lie", "Character": "䴕", "Syllable": "lie",
        "Reason": "Pinned rare bird character, equivalent to 鴷.",
        "Sources": ["https://dict.revised.moe.edu.tw/dictView.jsp?ID=3519&la=0&powerMode=0"],
    },
    {
        "Id": "bulbul-bei", "Character": "鹎", "Syllable": "bei",
        "Reason": "Pinned bulbul character reading, equivalent to 鵯.",
        "Sources": ["https://dict.revised.moe.edu.tw/dictView.jsp?ID=158&q=1&word=%E9%B5%AF"],
    },
    {
        "Id": "plover-heng", "Character": "鸻", "Syllable": "heng",
        "Reason": "Pinned plover character reading, equivalent to 鴴.",
        "Sources": ["https://pedia.cloud.edu.tw/Entry/Detail?title=%E9%B4%B4"],
    },
)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load_pypinyin():
    if not (PYTHON_TARGET / "pypinyin/__init__.py").is_file():
        raise ValueError("Missing build dependency: install the pinned wheel into .tools/search-python as described in this script.")
    sys.path.insert(0, str(PYTHON_TARGET))
    import pypinyin

    if pypinyin.__version__ != PINNED_VERSION:
        raise ValueError(f"Expected pypinyin {PINNED_VERSION}, found {pypinyin.__version__}; use the pinned wheel.")
    return pypinyin


def rule_positions(rule: dict, name: str) -> list[int]:
    if "Names" in rule and name not in rule["Names"]:
        return []
    if "Patterns" not in rule:
        return [i for i, ch in enumerate(name) if ch == rule["Character"]]
    positions = set()
    for pattern in rule["Patterns"]:
        start = 0
        while (position := name.find(pattern, start)) >= 0:
            positions.add(position + pattern.index(rule["Character"]))
            start = position + 1
    return sorted(positions)


def unknown_characters(characters: str) -> list[str]:
    code_points = ", ".join(f"U+{ord(character):04X}" for character in characters)
    raise ValueError(f"Unknown pinyin characters: {characters!r} ({code_points}); add an audited reading before regenerating.")


def generate() -> bytes:
    pypinyin = load_pypinyin()
    names: set[str] = set()
    source_hashes = {}
    for relative in SOURCE_PATHS:
        path = ROOT / relative
        source_hashes[relative] = sha256(path)
        records = json.loads(path.read_text(encoding="utf-8-sig"))
        if not isinstance(records, list):
            raise ValueError(f"Expected an array in {relative}")
        for record in records:
            name = record.get("ChineseName")
            if not isinstance(name, str) or not name.strip() or name != name.strip():
                raise ValueError(f"Missing or untrimmed ChineseName in {relative}: {name!r}")
            if name in names:
                raise ValueError(f"Duplicate ChineseName: {name}")
            names.add(name)
    if not names:
        raise ValueError("No species names found")

    entries = []
    audit = []
    for name in sorted(names):
        # v is the official library's normal-style spelling for ü. Unknown
        # characters fail rather than silently leaking Han characters into keys.
        syllables = pypinyin.lazy_pinyin(name, style=pypinyin.Style.NORMAL, errors=unknown_characters, v_to_u=False)
        if len(syllables) != len(name):
            raise ValueError(f"Expected one syllable per character: {name}: {syllables}")
        for rule in RULES:
            for position in rule_positions(rule, name):
                original = syllables[position]
                syllables[position] = rule["Syllable"]
                audit.append({"Name": name, "Position": position, "Rule": rule["Id"],
                              "Original": original, "Selected": syllables[position]})
        if any(re.fullmatch(r"[a-z]+", syllable) is None for syllable in syllables):
            raise ValueError(f"Unknown or non-ASCII pinyin: {name}: {syllables}")
        entries.append({"Name": name, "Syllables": syllables,
                        "FullPinyin": "".join(syllables), "Initials": "".join(s[0] for s in syllables)})

    # Explicit review checks catch accidental regressions in rare characters,
    # polyphonic contexts, zero-initial syllables, and the ü/v convention.
    reviewed = {
        "小䴙䴘": ("xiaopiti", "xpt"), "蚁䴕": ("yilie", "yl"),
        "白头鹎": ("baitoubei", "btb"), "金眶鸻": ("jinkuangheng", "jkh"),
        "绿头鸭": ("lvtouya", "lty"), "红头长尾山雀": ("hongtouchangweishanque", "htcwsq"),
        "爪哇八哥": ("zhaowabage", "zwbg"), "中杓鹬": ("zhongshaoyu", "zsy"),
        "藏雀": ("zangque", "zq"), "欧亚大山雀": ("ouyadashanque", "oydsq"),
    }
    by_name = {entry["Name"]: entry for entry in entries}
    for name, (full, initials) in reviewed.items():
        if name in by_name and (by_name[name]["FullPinyin"], by_name[name]["Initials"]) != (full, initials):
            raise ValueError(f"Reviewed reading changed: {name}: {by_name[name]}")

    result = {
        "Metadata": {
            "SchemaVersion": 1, "Generator": "scripts/Generate-SpeciesSearchIndex.py",
            "GeneratorVersion": 1, "PypinyinVersion": PINNED_VERSION,
            "PypinyinWheelSHA256": WHEEL_SHA256,
            "PinyinDataVersion": "0.15.0", "PhrasePinyinDataVersion": "0.19.0",
            "DictionaryHashes": {filename: sha256(PYTHON_TARGET / "pypinyin" / filename)
                                 for filename in ("pinyin_dict.json", "phrases_dict.json")},
            "SourceHashes": source_hashes, "NameCount": len(entries), "UnknownCharacters": 0,
            "KeyConvention": "lowercase ASCII; no tones; ü=v; initials=first letter of each syllable",
            "Purpose": "Search aliases only; species names, IDs and export names are unchanged.",
            "OverrideRules": RULES, "OverrideAudit": audit,
        },
        "Entries": entries,
    }
    return (json.dumps(result, ensure_ascii=False, indent=2) + "\n").encode("utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check", action="store_true", help="Compare with checked-in JSON without writing anything")
    args = parser.parse_args()
    try:
        content = generate()
        if args.check:
            if not OUTPUT.is_file() or OUTPUT.read_bytes() != content:
                raise ValueError("SpeciesSearchData.json is missing or stale; rerun without --check and review the diff.")
        else:
            OUTPUT.parent.mkdir(parents=True, exist_ok=True)
            OUTPUT.write_bytes(content)
        payload = json.loads(content)
        metadata = payload["Metadata"]
        changed = sum(item["Original"] != item["Selected"] for item in metadata["OverrideAudit"])
        print(f"{'Verified' if args.check else 'Generated'} {metadata['NameCount']} names; unknown characters=0; "
              f"audited overrides={len(metadata['OverrideAudit'])}, changed readings={changed}; "
              f"SHA256={hashlib.sha256(content).hexdigest()}")
        return 0
    except (ImportError, ValueError, OSError, KeyError, TypeError) as error:
        print(f"Species search index: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
