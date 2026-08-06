#!/usr/bin/env python3
"""Render the terminal transcripts in ./transcripts as self-contained SVG.

    python docs/assets/generate.py

Every transcript here is real output captured from the CLI against a live Databricks workspace,
with that workspace's identifiers replaced by synthetic ones before it was stored.

`chat.txt` used to be the exception, reconstructed from the literal strings in `ChatCommand.cs`,
because the REPL refuses to start without an interactive terminal and so cannot be piped to a
file. It was captured for real on 2026-08-06 by copying the terminal buffer of a live session --
which is the only way to do it, and is the way to do it again if the output changes.

That capture came off a 152-column terminal, which rendered an image half again as wide as the
others and correspondingly smaller once scaled into a page. It has been reflowed to 80 columns to
match `ask.txt`: the prose is re-wrapped, the tables and the SQL box are narrower, and the wording,
figures, ordering and command sequence are exactly as the session produced them. Nothing was
dropped or invented -- but it is reflowed output rather than a byte-exact capture, and that is the
difference worth knowing before treating it as evidence of anything but layout.

Two rules when editing a transcript by hand. Every box-drawing line in one table or panel must be
the same length, or the frame renders visibly ragged -- and the widest line in the file sets the
image width, so a stray long line quietly shrinks the whole thing when it is scaled into a page.

When substituting identifiers, keep the box-drawing borders aligned: the synthetic table name is
shorter than the real one, and replacing it without restoring the displaced padding leaves a
visibly ragged box. That had already happened once, in `ask.txt`.

SVG rather than screenshots, deliberately: the assets stay text, so they diff, scale, and need no
binary blobs in the repository -- and when the CLI's output changes, regenerating produces a
visibly different file instead of a stale PNG nobody notices.

Requires nothing beyond the standard library.
"""
import html
import os
import re

CHAR_W = 8.4
LINE_H = 20
PAD_X = 22
PAD_TOP = 46
PAD_BOT = 20

BG = "#11151c"
CHROME = "#1b212b"
FG = "#c8d3e0"
DIM = "#7c8899"
GREEN = "#5ec27a"
CYAN = "#56b6c2"
BORDER = "#2a3240"

FRAMES = [
    ("ask", 'lakespeak ask --agent sales --show-sql "What is the total revenue by region?"'),
    ("agents-list", "lakespeak agents list"),
    ("auth-check", "lakespeak auth check"),
    ("chat", "lakespeak chat --agent sales"),
]


def classify(line):
    """Pick a colour for a whole line, mirroring what Spectre prints to a real terminal."""
    stripped = line.strip()
    if stripped.startswith("OK"):
        return GREEN
    if stripped.endswith("…"):                 # a progress line, e.g. "Preparing answer..."
        return DIM
    if stripped[:1] in "╭╰├":        # box corners and tees
        return CYAN if "Generated SQL" in line else BORDER
    return FG


def render(lines, title, out_path):
    width = max(max((len(x) for x in lines), default=60), len(title) + 12)
    w = int(width * CHAR_W + PAD_X * 2)
    h = int(len(lines) * LINE_H + PAD_TOP + PAD_BOT)

    body = []
    for i, line in enumerate(lines):
        y = PAD_TOP + i * LINE_H
        if not line.strip():
            continue

        if line.startswith("You:"):
            # The prompt is green in a real terminal while what you typed is not, so the two are
            # split rather than washing the whole line one colour.
            body.append(
                f'<text x="{PAD_X}" y="{y}">'
                f'<tspan fill="{GREEN}" xml:space="preserve">You:</tspan>'
                f'<tspan fill="{FG}" xml:space="preserve">{html.escape(line[4:])}</tspan></text>')
        elif line[:1] == "│":
            # Split a table row so the frame recedes and the cells read as data. Colouring the
            # whole line as frame made the numbers almost invisible, which defeats the point of
            # showing a result table at all.
            spans = []
            for part in re.split("(│)", line):
                if part:
                    colour = BORDER if part == "│" else FG
                    spans.append(
                        f'<tspan fill="{colour}" xml:space="preserve">{html.escape(part)}</tspan>')
            body.append(f'<text x="{PAD_X}" y="{y}">{"".join(spans)}</text>')
        else:
            body.append(
                f'<text x="{PAD_X}" y="{y}" fill="{classify(line)}" xml:space="preserve">'
                f'{html.escape(line)}</text>')

    dots = "".join(
        f'<circle cx="{22 + i * 18}" cy="20" r="5.5" fill="{c}"/>'
        for i, c in enumerate(("#e06a5f", "#e0b341", "#5ec27a")))
    mono = "ui-monospace,'SF Mono',Menlo,'DejaVu Sans Mono',Consolas,monospace"
    rendered = "\n".join("    " + b for b in body)

    svg = (
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" '
        f'viewBox="0 0 {w} {h}" role="img" aria-label="{html.escape(title)}">\n'
        f'  <title>{html.escape(title)}</title>\n'
        f'  <rect x="0" y="0" width="{w}" height="{h}" rx="10" fill="{BG}"/>\n'
        f'  <path d="M0 10a10 10 0 0 1 10-10h{w - 20}a10 10 0 0 1 10 10v30H0z" fill="{CHROME}"/>\n'
        f'  {dots}\n'
        f'  <text x="{w / 2}" y="25" fill="{DIM}" font-size="12.5" text-anchor="middle" '
        f'font-family="{mono}">{html.escape(title)}</text>\n'
        f'  <g font-family="{mono}" font-size="13.5">\n{rendered}\n  </g>\n'
        f'</svg>\n')

    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(svg)
    print(f"  {os.path.basename(out_path)}  {w}x{h}")


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    for name, title in FRAMES:
        with open(os.path.join(here, "transcripts", name + ".txt"), encoding="utf-8") as f:
            lines = [line.rstrip() for line in f.read().splitlines()]
        render(lines, title, os.path.join(here, name + ".svg"))


if __name__ == "__main__":
    main()
