#!/usr/bin/env python3
"""Generates the Windows app's English string table from the macOS catalog.

Reads EchoDiar/Localizable.xcstrings and writes
windows/src/EchoDiar.App/Strings/en-US/Resources.resw.

- Key: a slug of the source text (the xcstrings key): the ASCII alphanumeric runs, each
  with its first letter upper-cased, joined and cut to 60 characters. A slug that is empty
  or starts with a digit gets the prefix "S_". Colliding slugs get "_2", "_3"... in the
  order of the sorted source keys. Loc.Slug in windows/src/EchoDiar.App/Loc.cs is the
  same algorithm; a colliding text is looked up with Loc.Key("<key>_2").
- Value: the English text (the en localization when present, the "other" plural form for
  plural variations, else the key), with printf placeholders as .NET format items:
  %@, %lld, %d, %f... become {0}, {1}... in order, %2$@ becomes {1}, %% becomes %, and
  literal braces are doubled so string.Format works.

Usage: python3 -I windows/tools/xcstrings_to_resw.py [--check]
  --check  regenerates in memory and diffs against the committed file; exit 1 on a mismatch.
"""

import difflib
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "EchoDiar" / "Localizable.xcstrings"
TARGET = ROOT / "windows" / "src" / "EchoDiar.App" / "Strings" / "en-US" / "Resources.resw"

MAX_SLUG = 60
PREFIX = "S_"

WORD = re.compile(r"[A-Za-z0-9]+")
PLACEHOLDER = re.compile(
    r"%(?:(\d+)\$)?[-+ #0]*\d*(?:\.\d+)?(?:hh|h|ll|l|q|z|t|j|L)?([@dDiuUxXoOfFeEgGcCsSpaA%])"
)

HEADER = """<?xml version="1.0" encoding="utf-8"?>
<root>
  <xsd:schema id="root" xmlns="" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:msdata="urn:schemas-microsoft-com:xml-msdata">
    <xsd:import namespace="http://www.w3.org/XML/1998/namespace" />
    <xsd:element name="root" msdata:IsDataSet="true">
      <xsd:complexType>
        <xsd:choice maxOccurs="unbounded">
          <xsd:element name="metadata">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" />
              </xsd:sequence>
              <xsd:attribute name="name" use="required" type="xsd:string" />
              <xsd:attribute name="type" type="xsd:string" />
              <xsd:attribute name="mimetype" type="xsd:string" />
              <xsd:attribute ref="xml:space" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="assembly">
            <xsd:complexType>
              <xsd:attribute name="alias" type="xsd:string" />
              <xsd:attribute name="name" type="xsd:string" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="data">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
                <xsd:element name="comment" type="xsd:string" minOccurs="0" msdata:Ordinal="2" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" msdata:Ordinal="1" />
              <xsd:attribute name="type" type="xsd:string" msdata:Ordinal="3" />
              <xsd:attribute name="mimetype" type="xsd:string" msdata:Ordinal="4" />
              <xsd:attribute ref="xml:space" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="resheader">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" />
            </xsd:complexType>
          </xsd:element>
        </xsd:choice>
      </xsd:complexType>
    </xsd:element>
  </xsd:schema>
  <resheader name="resmimetype">
    <value>text/microsoft-resx</value>
  </resheader>
  <resheader name="version">
    <value>2.0</value>
  </resheader>
  <resheader name="reader">
    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
  <resheader name="writer">
    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
"""


def slug(text):
    """PascalCase of the ASCII alphanumeric runs, at most 60 characters, "S_" if empty or digit-led."""
    s = "".join(w[0].upper() + w[1:] for w in WORD.findall(text))[:MAX_SLUG]
    if not s or s[0].isdigit():
        s = PREFIX + s
    return s


def to_net_format(text):
    """printf placeholders -> {n}; literal braces doubled; %% -> %."""
    out = []
    pos = 0
    sequential = 0
    for m in PLACEHOLDER.finditer(text):
        out.append(text[pos:m.start()].replace("{", "{{").replace("}", "}}"))
        if m.group(2) == "%":
            out.append("%")
        elif m.group(1):
            out.append("{%d}" % (int(m.group(1)) - 1))
        else:
            out.append("{%d}" % sequential)
            sequential += 1
        pos = m.end()
    out.append(text[pos:].replace("{", "{{").replace("}", "}}"))
    return "".join(out)


def english(key, entry):
    en = entry.get("localizations", {}).get("en", {})
    unit = en.get("stringUnit")
    if unit is None:
        plural = en.get("variations", {}).get("plural", {})
        unit = plural.get("other", {}).get("stringUnit")
    return unit["value"] if unit and "value" in unit else key


def xml_escape(text):
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def build():
    """Returns (resw text, number of strings, collision groups as {slug: [source keys]})."""
    with open(SOURCE, encoding="utf-8") as f:
        catalog = json.load(f)
    strings = catalog["strings"]
    rows = []
    used = set()
    groups = {}
    for key in sorted(strings):
        base = slug(key)
        name = base
        n = 1
        while name in used:
            n += 1
            name = "%s_%d" % (base, n)
        used.add(name)
        groups.setdefault(base, []).append(key)
        rows.append((name, to_net_format(english(key, strings[key])), key))
    rows.sort(key=lambda r: r[0])
    parts = [HEADER]
    for name, value, key in rows:
        parts.append('  <data name="%s" xml:space="preserve">\n' % name)
        parts.append("    <value>%s</value>\n" % xml_escape(value))
        parts.append("    <comment>%s</comment>\n" % xml_escape(key))
        parts.append("  </data>\n")
    parts.append("</root>\n")
    collisions = {b: keys for b, keys in groups.items() if len(keys) > 1}
    return "".join(parts), len(rows), collisions


def main(argv):
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    check = "--check" in argv[1:]
    text, count, collisions = build()
    print("%d strings, %d collision groups" % (count, len(collisions)))
    for base, keys in sorted(collisions.items()):
        print("  %s: %s" % (base, " | ".join(repr(k) for k in keys)))
    if check:
        try:
            with open(TARGET, encoding="utf-8", newline="") as f:
                committed = f.read().replace("\r\n", "\n")
        except FileNotFoundError:
            committed = ""
        if committed != text:
            diff = difflib.unified_diff(
                committed.splitlines(keepends=True),
                text.splitlines(keepends=True),
                "committed/Resources.resw",
                "generated/Resources.resw",
            )
            sys.stdout.writelines(diff)
            print("Resources.resw is out of date: run python3 -I windows/tools/xcstrings_to_resw.py")
            return 1
        print("Resources.resw is up to date")
        return 0
    TARGET.parent.mkdir(parents=True, exist_ok=True)
    with open(TARGET, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("wrote %s" % TARGET.relative_to(ROOT))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
