"""Convert flows/*.md to PDF via Markdown → HTML → Edge headless print.

Avoids xhtml2pdf glyph/background bugs (black blocks in tables and inline code).
Requires Microsoft Edge (default on Windows 10/11).
"""
from __future__ import annotations

import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

import markdown

EDGE_CANDIDATES = (
    Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"))
    / "Microsoft"
    / "Edge"
    / "Application"
    / "msedge.exe",
    Path(os.environ.get("ProgramFiles", r"C:\Program Files"))
    / "Microsoft"
    / "Edge"
    / "Application"
    / "msedge.exe",
)

CSS = """
@page { margin: 18mm; }
body {
  font-family: "Microsoft YaHei", "SimSun", sans-serif;
  font-size: 11pt;
  line-height: 1.55;
  color: #222;
  max-width: 100%;
}
h1 { font-size: 20pt; border-bottom: 2px solid #333; padding-bottom: 6px; }
h2 { font-size: 14pt; margin-top: 16px; color: #1565C0; }
h3 { font-size: 12pt; margin-top: 12px; }
table { border-collapse: collapse; width: 100%; margin: 10px 0; font-size: 10pt; }
th, td { border: 1px solid #888; padding: 5px 8px; vertical-align: top; text-align: left; }
th { background: #eceff1; font-weight: bold; }
code, pre {
  font-family: Consolas, "Microsoft YaHei", "SimSun", monospace;
  font-size: 9.5pt;
}
pre {
  background: #f5f5f5;
  padding: 10px;
  white-space: pre-wrap;
  word-break: break-word;
  border: 1px solid #ddd;
}
blockquote {
  border-left: 4px solid #90a4ae;
  margin: 8px 0;
  padding: 4px 12px;
  color: #455a64;
}
hr { border: none; border-top: 1px solid #ccc; margin: 16px 0; }
a { color: #1565c0; text-decoration: none; }
ul, ol { padding-left: 1.4em; }
"""


def find_edge() -> Path:
    for path in EDGE_CANDIDATES:
        if path.is_file():
            return path
    raise FileNotFoundError(
        "未找到 Microsoft Edge，无法生成 PDF。"
        "请安装 Edge 或使用 Windows 10/11 自带浏览器。"
    )


def preprocess_md(md_text: str) -> str:
    md_text = re.sub(
        r"```mermaid[\s\S]*?```",
        "\n\n> **流程图**：完整 Mermaid 图见同目录 Markdown 源文件。\n\n",
        md_text,
    )
    return md_text


def md_to_html(md_text: str) -> str:
    body = markdown.markdown(
        md_text,
        extensions=["tables", "fenced_code", "nl2br", "sane_lists"],
    )
    return f"""<!DOCTYPE html>
<html lang="zh-CN"><head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1"/>
<title>Document</title>
<style>{CSS}</style>
</head>
<body>{body}</body></html>"""


def file_uri(path: Path) -> str:
    return "file:///" + path.resolve().as_posix()


def html_to_pdf_edge(html_path: Path, pdf_path: Path, edge: Path) -> None:
    pdf_path.parent.mkdir(parents=True, exist_ok=True)
    if pdf_path.is_file():
        pdf_path.unlink()
    cmd = [
        str(edge),
        "--headless=new",
        "--disable-gpu",
        "--no-first-run",
        "--no-pdf-header-footer",
        f"--print-to-pdf={pdf_path.resolve()}",
        file_uri(html_path),
    ]
    proc = subprocess.run(
        cmd,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=120,
    )
    if proc.returncode != 0 and not pdf_path.is_file():
        err = (proc.stderr or proc.stdout or "").strip()
        raise RuntimeError(f"Edge 打印 PDF 失败 (code {proc.returncode}): {err}")
    if not pdf_path.is_file() or pdf_path.stat().st_size < 500:
        raise RuntimeError("Edge 未生成有效 PDF 文件")


def convert(md_path: Path, pdf_path: Path | None = None, *, keep_html: bool = False) -> Path:
    edge = find_edge()
    pdf_path = pdf_path or md_path.with_suffix(".pdf")
    html_content = md_to_html(preprocess_md(md_path.read_text(encoding="utf-8")))

    html_path = md_path.with_suffix(".print.html") if keep_html else None
    if keep_html:
        html_path.write_text(html_content, encoding="utf-8")
        try:
            html_to_pdf_edge(html_path, pdf_path, edge)
        finally:
            pass
    else:
        with tempfile.NamedTemporaryFile(
            mode="w",
            encoding="utf-8",
            suffix=".html",
            delete=False,
            dir=str(md_path.parent),
        ) as tmp:
            tmp.write(html_content)
            tmp_path = Path(tmp.name)
        try:
            html_to_pdf_edge(tmp_path, pdf_path, edge)
        finally:
            tmp_path.unlink(missing_ok=True)
    return pdf_path


def main(argv: list[str]) -> int:
    base = Path(__file__).resolve().parent
    keep_html = "--keep-html" in argv
    args = [a for a in argv[1:] if a != "--keep-html"]
    files = [Path(a) for a in args] if args else [
        base / "产线视觉标定与主流程.md",
        base / "v4" / "九点标定_圆点检测.md",
    ]
    for md in files:
        if not md.is_file():
            print("skip (missing):", md)
            continue
        out = convert(md, keep_html=keep_html)
        print("written:", out, out.stat().st_size, "bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
