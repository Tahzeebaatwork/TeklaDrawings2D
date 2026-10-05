"""Bind shop sheets into an engineer fabrication booklet PDF (4–6 pages by role)."""
import argparse
import sys
from pathlib import Path

import fitz

ROOT = Path(__file__).resolve().parents[1]
PDF_DIR = ROOT / "Export" / "CivilDrawings" / "new with macros" / "PDF"


def first_existing(*paths):
    for p in paths:
        if p is not None and p.exists():
            return p
    return None


def sheet_paths(mark):
    # Role-based booklet (SheetRoleMap):
    # 1 Hardware, 2 Sections/3D, 3 Placing, 4 BBS (+ optional overflow 5/6)
    p1 = PDF_DIR / f"{mark}_-_1.pdf"
    p2 = first_existing(
        PDF_DIR / f"{mark}_-_2_Sections_3D.pdf",
        PDF_DIR / f"{mark}_-_2.pdf",
    )
    # Placing: new pack uses _-_3; legacy used _-_2 when sections was sidecar-only.
    p3 = first_existing(
        PDF_DIR / f"{mark}_-_3.pdf",
        PDF_DIR / f"{mark}_-_2.pdf" if p2 and p2.name.endswith("_Sections_3D.pdf") else None,
    )
    # Avoid duplicating the same file as sections + placing.
    if p3 is not None and p2 is not None and p3.resolve() == p2.resolve():
        p3 = first_existing(PDF_DIR / f"{mark}_-_3.pdf")
    p4 = first_existing(
        PDF_DIR / f"{mark}_-_4.pdf",
        PDF_DIR / f"{mark}_-_3.pdf",
    )
    if p4 is not None and p3 is not None and p4.resolve() == p3.resolve():
        p4 = first_existing(PDF_DIR / f"{mark}_-_4.pdf")

    pages = [p for p in (p1, p2, p3, p4) if p is not None]
    for extra in (PDF_DIR / f"{mark}_-_5.pdf", PDF_DIR / f"{mark}_-_6.pdf"):
        if extra.exists() and extra not in pages:
            pages.append(extra)
    return pages


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--mark", default="W10-175")
    args = parser.parse_args()
    pages = sheet_paths(args.mark)
    if not pages:
        print(f"No sheet PDFs found in {PDF_DIR}")
        return 1

    out = PDF_DIR / f"P22-132.{args.mark}.Rev 2.pdf"
    book = fitz.open()
    for idx, path in enumerate(pages):
        src = fitz.open(path)
        page_num = idx + 1
        for p in src:
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
