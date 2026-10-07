"""Bind shop sheets into an engineer fabrication booklet PDF (4–6 pages by role).

Page order (SheetRoleMap):
  1 Hardware (_-_1)
  2 Sections/3D (_-_2_Sections_3D or legacy _-_2)
  3 Placing (_-_3)
  4 BBS table (_-_4)
  5–6 optional overflow
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

try:
    import fitz  # PyMuPDF
except ImportError:
    print("PyMuPDF required: pip install pymupdf", file=sys.stderr)
    sys.exit(2)

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_PDF_DIR = ROOT / "Export" / "CivilDrawings" / "new with macros" / "PDF"


def first_existing(*paths):
    for p in paths:
        if p is not None and Path(p).exists():
            return Path(p)
    return None


def sheet_paths(pdf_dir: Path, mark: str):
    pdf_dir = Path(pdf_dir)
    p1 = pdf_dir / f"{mark}_-_1.pdf"
    p2 = first_existing(
        pdf_dir / f"{mark}_-_2_Sections_3D.pdf",
        pdf_dir / f"{mark}_-_2.pdf",
    )
    p3 = first_existing(
        pdf_dir / f"{mark}_-_3.pdf",
        pdf_dir / f"{mark}_-_2.pdf" if p2 and p2.name.endswith("_Sections_3D.pdf") else None,
    )
    if p3 is not None and p2 is not None and p3.resolve() == p2.resolve():
        p3 = first_existing(pdf_dir / f"{mark}_-_3.pdf")
    p4 = first_existing(
        pdf_dir / f"{mark}_-_4.pdf",
        pdf_dir / f"{mark}_-_3.pdf",
    )
    if p4 is not None and p3 is not None and p4.resolve() == p3.resolve():
        p4 = first_existing(pdf_dir / f"{mark}_-_4.pdf")

    pages = [p for p in (p1, p2, p3, p4) if p is not None]
    for extra in (pdf_dir / f"{mark}_-_5.pdf", pdf_dir / f"{mark}_-_6.pdf"):
        if extra.exists() and extra not in pages:
            pages.append(extra)
    return pages


def main():
    parser = argparse.ArgumentParser(description="Collate CU role sheets into one booklet PDF")
    parser.add_argument("--mark", default="W10-175")
    parser.add_argument("--pdf-dir", default=None, help="Folder with W10-175_-_1.pdf etc.")
    parser.add_argument("--out", default=None, help="Output booklet path")
    args = parser.parse_args()

    pdf_dir = Path(args.pdf_dir) if args.pdf_dir else DEFAULT_PDF_DIR
    pages = sheet_paths(pdf_dir, args.mark)
    if not pages:
        print(f"No sheet PDFs found in {pdf_dir} for mark={args.mark}")
        return 1

    out = Path(args.out) if args.out else pdf_dir / f"P22-132.{args.mark}.Rev 2.pdf"
    book = fitz.open()
    for idx, path in enumerate(pages):
        src = fitz.open(path)
        page_num = idx + 1
        for p in src:
            # Stamp PAGE - N in title-block corner (11x17 landscape coords from Paisley sheets)
            rect = fitz.Rect(1140, 780, 1185, 793)
            p.add_redact_annot(rect, fill=(1, 1, 1))
            p.apply_redactions()
            p.insert_text(fitz.Point(1141.6, 789.8), f"PAGE - {page_num}", fontsize=8.8, fontname="helv")
        book.insert_pdf(src)
        src.close()
        print(f"Added {path.name} as PAGE - {page_num}")
    book.save(out)
    book.close()
    print(f"Wrote {out} ({len(pages)} pages)")
    return 0 if len(pages) >= 3 else 1


if __name__ == "__main__":
    sys.exit(main())
