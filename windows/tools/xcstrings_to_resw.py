#!/usr/bin/env python3
"""Generates the Windows app's string tables from the macOS catalog and the Windows-only catalog.

Reads EchoDiar/Localizable.xcstrings (the macOS catalog, never edited for Windows) and
windows/src/EchoDiar.App/Strings/Windows.xcstrings (Windows-only texts, same xcstrings format; a key in both
catalogs is an error), and writes windows/src/EchoDiar.App/Strings/<folder>/Resources.resw for en-US, vi, fr, es
and zh-Hans, and windows/src/EchoDiar.App/Strings/LocKeys.g.cs.

- Key: a slug of the source text (the xcstrings key): the ASCII alphanumeric runs, each
  with its first letter upper-cased, joined and cut to 60 characters. A slug that is empty
  or starts with a digit gets the prefix "S_". Colliding slugs get "_2", "_3"... in the
  order of the sorted source keys of both catalogs. Loc.Slug in windows/src/EchoDiar.App/Loc.cs
  is the same algorithm. LocKeys.g.cs lists every source text (Loc.KnownTexts): Loc.T and
  Loc.Format resolve only those, and return any other text as is, so a text whose slug
  happens to match a catalog key never gets that key's string. It also maps the exact
  source text of every member of a collision group to its key (Loc.CollisionKeys), which
  Loc.T and Loc.Format look up before the slug.
- Plurals: a text with plural variations, or with a substitution (%2$#@name@) that has plural
  variations, gets its "other" form under the key and its "one" form under "<key>__One" (slugs
  never contain "__"). Other plural categories ("many"...) are not written.
- Value: en-US has every key: the English text (the en localization when present, else the
  key). vi, fr, es and zh-Hans have only the forms translated in that language (state
  "translated" or "needs_review"); MRT falls back to en-US for the others. Each substitution
  is replaced by the same plural form of that substitution ("other" when it has no "one"),
  its %arg as the substitution's argument (%2$lld), and printf placeholders become .NET
  format items: %@, %lld, %d, %f... become {0}, {1}... in order, %2$@ becomes {1}, %% becomes
  %, and literal braces are doubled so string.Format works.
- Self-checks: a text that still holds a substitution (#@) or a % that is neither a printf
  placeholder to_net_format converts nor %%, or a value whose {n} indices are not exactly
  0..n-1 for the n placeholders of its source text. A failing translation of the macOS
  catalog is left out of that language (warning; MRT falls back to en-US); any other failure
  (English, or the Windows catalog) fails generation and --check, nothing written.
- Literal check: every literal first argument of Loc.T / Loc.Format / Loc.Plural("...") in a
  .cs file, and every {local:T Text='...'} in a .xaml file, under windows/src/EchoDiar.App
  must be a key of one of the catalogs: a warning when generating, an error with --check.
  Other arguments (variables, ternaries, concatenations) are not checked.

Usage: python3 -I windows/tools/xcstrings_to_resw.py [--check]
  --check  regenerates in memory and diffs against the committed files; exit 1 on a mismatch,
           a self-check error or a literal in neither catalog.
"""

import difflib
import html
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "EchoDiar" / "Localizable.xcstrings"
APP = ROOT / "windows" / "src" / "EchoDiar.App"
STRINGS = APP / "Strings"
WINDOWS_SOURCE = STRINGS / "Windows.xcstrings"
KEYS_TARGET = STRINGS / "LocKeys.g.cs"
# xcstrings language -> resw folder (an MRT language qualifier). en is the source language: every key.
LANGUAGES = [("en", "en-US"), ("vi", "vi"), ("fr", "fr"), ("es", "es"), ("zh-Hans", "zh-Hans")]
USABLE = {"translated", "needs_review"}
ONE = "__One"

MAX_SLUG = 60
PREFIX = "S_"

WORD = re.compile(r"[A-Za-z0-9]+")
SUBSTITUTION = re.compile(r"%(?:\d+\$)?#@(\w+)@")
FORMAT_ITEM = re.compile(r"\{\{|\}\}|\{(\d+)\}")
PLACEHOLDER = re.compile(
    r"%(?:(\d+)\$)?[-+ #0]*\d*(?:\.\d+)?(?:hh|h|ll|l|q|z|t|j|L)?([@dDiuUxXoOfFeEgGcCsSpaA%])"
)
# A string literal as the whole first argument of Loc.T / Loc.Format / Loc.Plural, and {local:T Text='...'}.
LOC_CALL = re.compile(r'\bLoc\.(?:T|Format|Plural)\(\s*"((?:[^"\\\n]|\\.)*)"\s*[,)]')
XAML_T = re.compile(r"\{local:T\s+Text='((?:[^'\\]|\\.)*)'\s*\}")
CS_ESCAPE = re.compile(r"\\(u[0-9A-Fa-f]{4}|U[0-9A-Fa-f]{8}|x[0-9A-Fa-f]{1,4}|.)")
CS_SIMPLE = {"0": "\0", "a": "\a", "b": "\b", "e": "\x1b", "f": "\f", "n": "\n", "r": "\r", "t": "\t", "v": "\v"}

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


def usable(unit, lang):
    """A unit with a value; outside en, only a translated or needs_review one."""
    return unit is not None and "value" in unit and (lang == "en" or unit.get("state") in USABLE)


def plural_unit(variations, form, lang):
    """The usable unit of plural form, else of "other", else None."""
    plural = (variations or {}).get("plural", {})
    for f in (form, "other"):
        unit = plural.get(f, {}).get("stringUnit")
        if usable(unit, lang):
            return unit
    return None


def expand(loc, text, form, lang):
    """text with each substitution (%2$#@name@) replaced by its plural form, its %arg as
    %<argNum>$<formatSpecifier> (positional, so to_net_format gives it its own argument whatever comes before
    it), and the problems found. A substitution without a usable form is left in place (check reports it)."""
    problems = []

    def replace(m):
        sub = loc.get("substitutions", {}).get(m.group(1), {})
        unit = plural_unit(sub.get("variations"), form, lang)
        if unit is None or "argNum" not in sub or "formatSpecifier" not in sub:
            problems.append("substitution %r has no usable %s form" % (m.group(1), form))
            return m.group(0)
        return unit["value"].replace("%arg", "%%%d$%s" % (sub["argNum"], sub["formatSpecifier"]))

    return SUBSTITUTION.sub(replace, text), problems


def forms(key, entry, lang):
    """({"other": text, "one": text}, problems): the plural forms of lang with substitutions expanded. A form
    that lang has no usable unit for is left out, except en's "other", which falls back to the key. "one" comes
    from the plural variations, or from the substitutions of a plain string unit."""
    loc = entry.get("localizations", {}).get(lang, {})
    if "variations" in loc:
        plural = loc["variations"].get("plural", {})
        units = {f: plural[f].get("stringUnit") for f in ("other", "one") if f in plural}
    else:
        unit = loc.get("stringUnit")
        units = {"other": unit}
        names = SUBSTITUTION.findall(unit["value"]) if usable(unit, lang) else []
        subs = loc.get("substitutions", {})
        if any("one" in subs.get(n, {}).get("variations", {}).get("plural", {}) for n in names):
            units["one"] = unit
    texts, problems = {}, []
    for form, unit in units.items():
        if usable(unit, lang):
            text, found = expand(loc, unit["value"], form, lang)
            texts[form] = text
            problems += ["%s form: %s" % (form, p) for p in found]
    if lang == "en" and "other" not in texts:
        texts["other"] = key
    return texts, problems


def check(key, text, value):
    """The problems of value, to_net_format(text): a substitution left, a printf placeholder not converted, {n}
    indices other than 0..n-1 for the key's n placeholders. Checked in text: PLACEHOLDER reads %2$#@ as a %@
    (# is a flag), so the value loses the #@, and %% is still distinct from a stray %."""
    problems = []
    if "#@" in text:
        problems.append("substitution left")
    left = PLACEHOLDER.sub("", text)
    if "%" in left:
        problems.append("printf placeholder not converted near %r" % left[left.index("%"):][:12])
    expected = sum(1 for m in PLACEHOLDER.finditer(key) if m.group(2) != "%")
    found = {int(m.group(1)) for m in FORMAT_ITEM.finditer(value) if m.group(1)}  # {{ and }} are literal braces
    if found != set(range(expected)):
        problems.append("format items %s, the source text has %d placeholders" % (sorted(found), expected))
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
        "// Generated by windows/tools/xcstrings_to_resw.py from EchoDiar/Localizable.xcstrings and\n",
        "// windows/src/EchoDiar.App/Strings/Windows.xcstrings. Do not edit.\n",
        "// </auto-generated>\n",
        "using System;\n",
        "using System.Collections.Generic;\n",
        "\n",
        "namespace EchoDiar.App;\n",
        "\n",
        "public static partial class Loc\n",
        "{\n",
        "    // Every source text of both catalogs: the only texts Loc.T and Loc.Format look up.\n",
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


def load(path):
    """The "strings" of an xcstrings catalog."""
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)["strings"]
    except FileNotFoundError:
        raise SystemExit("%s not found" % path.relative_to(ROOT))


def resw(rows):
    """A Resources.resw of rows (name, value, source text as the comment), sorted by name."""
    parts = [HEADER]
    for name, value, key in sorted(rows):
        parts.append('  <data name="%s" xml:space="preserve">\n' % name)
        parts.append("    <value>%s</value>\n" % xml_escape(value))
        parts.append("    <comment>%s</comment>\n" % xml_escape(key))
        parts.append("  </data>\n")
    parts.append("</root>\n")
    return "".join(parts)


def build():
    """Returns (outputs as {path: text}, source keys of both catalogs, entries per resw folder as {folder: (keys,
    "one" forms)}, collision groups as {slug: [source keys]}, dropped translations as [(language, source key,
    problems)], self-check errors as [(language, source key, problem)])."""
    mac = load(SOURCE)
    windows = load(WINDOWS_SOURCE)
    both = sorted(set(mac) & set(windows))
    if both:
        raise SystemExit("in both EchoDiar/Localizable.xcstrings and Strings/Windows.xcstrings: %s"
                         % ", ".join(repr(k) for k in both))
    strings = {**mac, **windows}
    used = set()
    groups = {}
    names = {}
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
    collisions = {b: keys for b, keys in groups.items() if len(keys) > 1}
    outputs, counts, dropped, errors = {}, {}, [], []
    for lang, folder in LANGUAGES:
        rows = []
        for key in sorted(strings):
            texts, problems = forms(key, strings[key], lang)
            values = {form: to_net_format(text) for form, text in texts.items()}
            for form, text in texts.items():
                problems += ["%s form: %s" % (form, p) for p in check(key, text, values[form])]
            if problems and lang != "en" and key in mac:
                dropped.append((lang, key, problems))  # A broken macOS translation: MRT falls back to en-US.
                continue
            errors += [(lang, key, p) for p in problems]
            rows += [(names[key] + (ONE if form == "one" else ""), value, key) for form, value in values.items()]
        outputs[STRINGS / folder / "Resources.resw"] = resw(rows)
        ones = sum(1 for r in rows if r[0].endswith(ONE))
        counts[folder] = (len(rows) - ones, ones)
    outputs[KEYS_TARGET] = keys_cs(collisions, names)
    return outputs, set(strings), counts, collisions, dropped, errors


def unescape_cs(text):
    """The value of a C# regular string literal's content."""
    def one(m):
        e = m.group(1)
        if len(e) > 1:
            return chr(int(e[1:], 16))  # \uXXXX, \UXXXXXXXX, \xH..
        return CS_SIMPLE.get(e, e)
    return CS_ESCAPE.sub(one, text)


def literals():
    """(text, "file:line") of every literal first argument of Loc.T / Loc.Format / Loc.Plural in a .cs file and
    every {local:T Text='...'} in a .xaml file under windows/src/EchoDiar.App, bin and obj left out."""
    found = []
    for path in sorted(APP.rglob("*")):
        rel = path.relative_to(APP)
        if rel.parts[0] in ("bin", "obj") or path.suffix not in (".cs", ".xaml") or not path.is_file():
            continue
        with open(path, encoding="utf-8-sig") as f:
            source = f.read()
        for m in (LOC_CALL if path.suffix == ".cs" else XAML_T).finditer(source):
            if path.suffix == ".cs":
                text = unescape_cs(m.group(1))
            else:  # XML entities first (the XML parser), then the markup extension's \x escapes.
                text = re.sub(r"\\(.)", r"\1", html.unescape(m.group(1)))
            found.append((text, "%s:%d" % (rel.as_posix(), source.count("\n", 0, m.start()) + 1)))
    return found


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
    outputs, known, counts, collisions, dropped, errors = build()
    print("%d strings, %d collision groups" % (len(known), len(collisions)))
    for folder, (keys, ones) in counts.items():
        print("  %s: %d strings, %d one forms" % (folder, keys, ones))
    for base, keys in sorted(collisions.items()):
        print("  %s: %s" % (base, " | ".join(repr(k) for k in keys)))
    for lang, key, problems in dropped:
        print("warning: %s translation of %r left out: %s" % (lang, key, "; ".join(problems)))
    if errors:
        for lang, key, problem in errors:
            print("error: %s %r: %s" % (lang, key, problem))
        print("%d self-check errors: nothing written" % len(errors))
        return 1
    missing = [(text, where) for text, where in literals() if text not in known]
    for text, where in missing:
        print("%s: %s %r is in neither catalog: add it to Strings/Windows.xcstrings"
              % ("error" if check else "warning", where, text))
    if check:
        stale = 0
        for path, generated in outputs.items():
            committed = read_committed(path)
            name = path.relative_to(STRINGS).as_posix()
            if committed != generated:
                diff = difflib.unified_diff(
                    committed.splitlines(keepends=True),
                    generated.splitlines(keepends=True),
                    "committed/" + name,
                    "generated/" + name,
                )
                sys.stdout.writelines(diff)
                print("%s is out of date: run python3 -I windows/tools/xcstrings_to_resw.py" % name)
                stale += 1
            else:
                print("%s is up to date" % name)
        return 1 if stale or missing else 0
    for path, generated in outputs.items():
        path.parent.mkdir(parents=True, exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(generated)
        print("wrote %s" % path.relative_to(ROOT))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
