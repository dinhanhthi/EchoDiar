#!/usr/bin/env python3
"""Generates the Windows app's English string table from the macOS catalog.

Reads EchoDiar/Localizable.xcstrings and writes
windows/src/EchoDiar.App/Strings/en-US/Resources.resw and
windows/src/EchoDiar.App/Strings/LocKeys.g.cs.

- Key: a slug of the source text (the xcstrings key): the ASCII alphanumeric runs, each
  with its first letter upper-cased, joined and cut to 60 characters. A slug that is empty
  or starts with a digit gets the prefix "S_". Colliding slugs get "_2", "_3"... in the
  order of the sorted source keys. Loc.Slug in windows/src/EchoDiar.App/Loc.cs is the
  same algorithm. LocKeys.g.cs lists every source text (Loc.KnownTexts): Loc.T and
  Loc.Format resolve only those, and return any other text as is, so a Windows-only text
  whose slug happens to match a catalog key never gets that key's string. It also maps the
  exact source text of every member of a collision group to its key (Loc.CollisionKeys),
  which Loc.T and Loc.Format look up before the slug.
- Value: the English text (the en localization when present, the "other" plural form for
  plural variations, else the key), with each substitution (%2$#@name@) replaced by the
  "other" plural form of that substitution, its %arg as the substitution's argument
  (%2$lld), and printf placeholders as .NET format items: %@, %lld, %d, %f... become
  {0}, {1}... in order, %2$@ becomes {1}, %% becomes %, and literal braces are doubled so
  string.Format works.
- Self-checks (fail generation and --check, nothing written): an English text that still
  holds a substitution (#@) or a % that is neither a printf placeholder to_net_format
  converts nor %%, or a value whose {n} count differs from its source text's placeholder count.

Usage: python3 -I windows/tools/xcstrings_to_resw.py [--check]
  --check  regenerates in memory and diffs against the committed files; exit 1 on a mismatch or a self-check error.
"""

import difflib
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "EchoDiar" / "Localizable.xcstrings"
STRINGS = ROOT / "windows" / "src" / "EchoDiar.App" / "Strings"
TARGET = STRINGS / "en-US" / "Resources.resw"
KEYS_TARGET = STRINGS / "LocKeys.g.cs"

MAX_SLUG = 60
PREFIX = "S_"

WORD = re.compile(r"[A-Za-z0-9]+")
SUBSTITUTION = re.compile(r"%(?:\d+\$)?#@(\w+)@")
FORMAT_ITEM = re.compile(r"\{\{|\}\}|\{(\d+)\}")
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
    value = unit["value"] if unit and "value" in unit else key
    return SUBSTITUTION.sub(lambda m: substitution(key, en, m.group(1)), value)


def substitution(key, en, name):
    """The "other" plural form of substitution name, its %arg as %<argNum>$<formatSpecifier>."""
    try:
        sub = en["substitutions"][name]
        other = sub["variations"]["plural"]["other"]["stringUnit"]["value"]
        # Positional, so to_net_format gives it its own argument whatever comes before it.
        arg = "%%%d$%s" % (sub["argNum"], sub["formatSpecifier"])
    except (KeyError, TypeError) as e:
        raise SystemExit("%r: substitution %r has no %s" % (key, name, e))
    return other.replace("%arg", arg)


def check(key, english_text, value):
    """The problems of value, to_net_format(english_text): a substitution left, a printf placeholder not
    converted, a {n} count off the key's. Checked in english_text: PLACEHOLDER reads %2$#@ as a %@ (# is a
    flag), so the value loses the #@, and %% is still distinct from a stray %."""
    problems = []
    if "#@" in english_text:
        problems.append("substitution left")
    left = PLACEHOLDER.sub("", english_text)
    if "%" in left:
        problems.append("printf placeholder not converted near %r" % left[left.index("%"):][:12])
    expected = sum(1 for m in PLACEHOLDER.finditer(key) if m.group(2) != "%")
    found = sum(1 for m in FORMAT_ITEM.finditer(value) if m.group(1))  # {{ and }} are literal braces
    if found != expected:
        problems.append("%d format items, the source text has %d placeholders" % (found, expected))
    return problems


def xml_escape(text):
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def cs_string(text):
    """A C# regular string literal: backslash, quote and control/line-break characters escaped."""
    out = []
    for c in text:
        if c in "\\\"":
            out.append("\\" + c)
        elif ord(c) < 0x20 or c in "\x7f\x85\u2028\u2029":
            out.append("\\u%04x" % ord(c))
        else:
            out.append(c)
    return '"%s"' % "".join(out)


def keys_cs(collisions, names):
    """LocKeys.g.cs: every source text, and the exact source text of every member of a collision group -> its key."""
    lines = [
        "// <auto-generated>\n",
        "// Generated by windows/tools/xcstrings_to_resw.py from EchoDiar/Localizable.xcstrings. Do not edit.\n",
        "// </auto-generated>\n",
        "using System;\n",
        "using System.Collections.Generic;\n",
        "\n",
        "namespace EchoDiar.App;\n",
        "\n",
        "public static partial class Loc\n",
        "{\n",
        "    // Every source text of the catalog: the only texts Loc.T and Loc.Format look up.\n",
        "    private static readonly HashSet<string> KnownTexts = new(StringComparer.Ordinal)\n",
        "    {\n",
    ]
    for key in sorted(names):
        lines.append("        %s,\n" % cs_string(key))
    lines += [
        "    };\n",
        "\n",
        "    // Source texts whose slug collides with another one's: the key the generator gave each.\n",
        "    private static readonly Dictionary<string, string> CollisionKeys = new(StringComparer.Ordinal)\n",
        "    {\n",
    ]
    for key in sorted(k for keys in collisions.values() for k in keys):
        lines.append("        [%s] = %s,\n" % (cs_string(key), cs_string(names[key])))
    lines += ["    };\n", "}\n"]
    return "".join(lines)


def build():
    """Returns (resw text, LocKeys.g.cs text, number of strings, collision groups as {slug: [source keys]},
    self-check problems as [(source key, problem)])."""
    with open(SOURCE, encoding="utf-8") as f:
        catalog = json.load(f)
    strings = catalog["strings"]
    rows = []
    used = set()
    groups = {}
    names = {}
    problems = []
    for key in sorted(strings):
        base = slug(key)
        name = base
        n = 1
        while name in used:
            n += 1
            name = "%s_%d" % (base, n)
        used.add(name)
        names[key] = name
        groups.setdefault(base, []).append(key)
        english_text = english(key, strings[key])
        value = to_net_format(english_text)
        problems += [(key, problem) for problem in check(key, english_text, value)]
        rows.append((name, value, key))
    rows.sort(key=lambda r: r[0])
    parts = [HEADER]
    for name, value, key in rows:
        parts.append('  <data name="%s" xml:space="preserve">\n' % name)
        parts.append("    <value>%s</value>\n" % xml_escape(value))
        parts.append("    <comment>%s</comment>\n" % xml_escape(key))
        parts.append("  </data>\n")
    parts.append("</root>\n")
    collisions = {b: keys for b, keys in groups.items() if len(keys) > 1}
    return "".join(parts), keys_cs(collisions, names), len(rows), collisions, problems


def read_committed(path):
    try:
        with open(path, encoding="utf-8", newline="") as f:
            return f.read().replace("\r\n", "\n")
    except FileNotFoundError:
        return ""


def main(argv):
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    check = "--check" in argv[1:]
    text, keys_text, count, collisions, problems = build()
    print("%d strings, %d collision groups" % (count, len(collisions)))
    for base, keys in sorted(collisions.items()):
        print("  %s: %s" % (base, " | ".join(repr(k) for k in keys)))
    if problems:
        for key, problem in problems:
            print("error: %r: %s" % (key, problem))
        print("%d self-check errors: nothing written" % len(problems))
        return 1
    outputs = [(TARGET, text), (KEYS_TARGET, keys_text)]
    if check:
        stale = 0
        for path, generated in outputs:
            committed = read_committed(path)
            if committed != generated:
                diff = difflib.unified_diff(
                    committed.splitlines(keepends=True),
                    generated.splitlines(keepends=True),
                    "committed/" + path.name,
                    "generated/" + path.name,
                )
                sys.stdout.writelines(diff)
                print("%s is out of date: run python3 -I windows/tools/xcstrings_to_resw.py" % path.name)
                stale += 1
            else:
                print("%s is up to date" % path.name)
        return 1 if stale else 0
    for path, generated in outputs:
        path.parent.mkdir(parents=True, exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(generated)
        print("wrote %s" % path.relative_to(ROOT))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
