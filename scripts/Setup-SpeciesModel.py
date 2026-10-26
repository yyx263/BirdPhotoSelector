"""Reproducible BioCLIP 2 + CAS China bird asset preparation (build time only).

The Windows application requires neither Python nor PyTorch at runtime.
All network requests in this script retrieve public model/catalog assets; no user photo is uploaded.
"""
from __future__ import annotations

import argparse
import concurrent.futures
from collections import defaultdict
import csv
import hashlib
import io
import json
import math
import os
from pathlib import Path
import re
import shutil
import time
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parent.parent
CACHE = ROOT / ".tools" / "species-assets"
DEST = ROOT / "models" / "bioclip2"
MODEL_REVISION = "2957b322090f9cb17ae72c71981c7218a28d81e0"
TEXT_REVISION = "5f2dc493b3dc0e544438a04038ab15faa646b749"
MODEL_SHA = "b7b2bf6fbc95799e42630e394cf95803892ab447c1a8ab629dbc82fbeaf7dfef"
LABEL_SHA = "4648928b006f85d83d28e5a27074ca9363465d82e778d708b369c5eaf54b8ef5"
TEXT_SHA = "c72442de7b0cb7fcb55ab7ca08099d0f42fbd6769efe16ca64c1daa7a8b87db2"
CAS_SHA = "9c17cdea98e210b2acc6ccf482580a22991a568455e0d6d3b58ba0ea5c675d1b"
CONFIG_SHA = "1bf947e96e943fe50efd5c3e26c37f843a2fa3c358967719a68c8a6d17ce68c8"
ASSETS = {
    "weights": (
        f"https://huggingface.co/imageomics/bioclip-2/resolve/{MODEL_REVISION}/open_clip_model.safetensors",
        "open_clip_model.safetensors", 1710517724, MODEL_SHA),
    "text": (
        f"https://huggingface.co/datasets/imageomics/TreeOfLife-200M/resolve/{TEXT_REVISION}/embeddings/txt_emb_bioclip-2.npy",
        "txt_emb_bioclip-2.npy", 2664821888, TEXT_SHA),
    "labels": (
        f"https://huggingface.co/datasets/imageomics/TreeOfLife-200M/resolve/{TEXT_REVISION}/embeddings/txt_emb_bioclip-2.json",
        "labels.json", 91586174, LABEL_SHA),
}


def sha256(path: Path) -> str:
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(4 * 1024 * 1024), b""):
            value.update(chunk)
    return value.hexdigest()


def download_ranges(url: str, path: Path, size: int, expected_sha: str,
                    workers: int = 8, chunk_size: int = 8 * 1024 * 1024) -> None:
    """Parallel bounded ranges avoid proxies buffering multi-GB HTTP responses.

    Every range and the final file are validated. Interrupted downloads retain resumable
    private chunks; an unverified partial file is never installed as the final asset.
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.is_file() and path.stat().st_size == size and sha256(path) == expected_sha.lower():
        print(f"Verified existing {path.name}", flush=True)
        return
    parts = path.parent / (path.name + ".parts")
    parts.mkdir(exist_ok=True)
    count = (size + chunk_size - 1) // chunk_size

    def fetch(index: int) -> int:
        start = index * chunk_size
        end = min(size, start + chunk_size) - 1
        target = parts / f"{index:05d}.part"
        if target.is_file() and target.stat().st_size == end - start + 1:
            return index
        for attempt in range(5):
            try:
                # Range-specific URL prevents a shared intermediary caching the wrong range.
                address = url + ("&" if "?" in url else "?") + f"bps_range={start}"
                request = urllib.request.Request(address, headers={
                    "Range": f"bytes={start}-{end}", "Accept-Encoding": "identity",
                    "User-Agent": "BirdPhotoSelector-SpeciesSetup/1.0"})
                with urllib.request.urlopen(request, timeout=120) as response:
                    if response.status != 206:
                        raise RuntimeError(f"Expected HTTP 206, got {response.status}")
                    content_range = response.headers.get("Content-Range", "")
                    if content_range != f"bytes {start}-{end}/{size}":
                        raise RuntimeError(f"Unexpected Content-Range: {content_range}")
                    data = response.read(end - start + 2)
                if len(data) != end - start + 1:
                    raise RuntimeError(f"Incomplete range {index}: {len(data)} bytes")
                temporary = target.with_suffix(".tmp")
                temporary.write_bytes(data)
                os.replace(temporary, target)
                return index
            except Exception:
                if attempt == 4:
                    raise
                time.sleep(min(8, 2 ** attempt))
        raise AssertionError("unreachable")

    print(f"Downloading {path.name}: {size / 1e6:.1f} MB in {count} ranges", flush=True)
    done = 0
    with concurrent.futures.ThreadPoolExecutor(max_workers=workers) as pool:
        for _ in pool.map(fetch, range(count)):
            done += 1
            if done % 8 == 0 or done == count:
                print(f"  {path.name}: {done}/{count} ranges", flush=True)
    partial = path.with_suffix(path.suffix + ".partial")
    with partial.open("wb") as output:
        for index in range(count):
            with (parts / f"{index:05d}.part").open("rb") as stream:
                shutil.copyfileobj(stream, output, length=1024 * 1024)
    if partial.stat().st_size != size or sha256(partial) != expected_sha.lower():
        raise RuntimeError(f"SHA-256 mismatch for {path.name}; final asset was not replaced")
    os.replace(partial, path)
    shutil.rmtree(parts)
    print(f"Downloaded and SHA-256 verified {path.name}", flush=True)


def china_candidates() -> list[dict]:
    """CAS accepted bird species + accepted-name-linked synonyms, never arbitrary Latin matching.

    Subspecies are mapped to their binomial parents. The source has complete Chinese
    vernacular names for the 1,445 accepted binomial bird taxa in this pinned archive.
    """
    archive_path = CACHE / "china-checklist.zip"
    if sha256(archive_path) != CAS_SHA:
        raise RuntimeError("Pinned CAS catalogue SHA-256 verification failed")
    with zipfile.ZipFile(archive_path) as archive:
        rows = list(csv.DictReader(io.TextIOWrapper(archive.open("taxon.txt"), encoding="utf-8"), delimiter="\t"))
    birds = [r for r in rows if r.get("class") == "Aves"]
    accepted = {r["taxonID"]: r for r in birds if r["taxonomicStatus"] == "Accepted"
                and len(r["scientificName"].split()) == 2}
    candidates = []
    for identifier, row in sorted(accepted.items(), key=lambda x: x[1]["scientificName"]):
        aliases = {r["scientificName"] for r in birds if r["acceptedNameUsageID"] == identifier
                   and r["scientificName"] != row["scientificName"]}
        candidates.append({
            "ChineseName": row["vernacularName"] or row["scientificName"],
            "ScientificName": row["scientificName"],
            "Taxonomy": [row[k] for k in ("kingdom", "phylum", "class", "order", "family", "genus")]
                        + [row["scientificName"].split(" ", 1)[1]],
            "SourceTaxonId": identifier,
            "Synonyms": sorted(aliases),
            "Source": "CAS Catalogue of Life China 2023 v1.1; https://doi.org/10.15468/ae6ush",
            "License": "CC-BY-NC-4.0",
        })
    return candidates


def map_candidates(labels: list) -> tuple[list[dict], list[dict], list[dict]]:
    """Conservative taxonomy reconciliation; imperfect upstream synonyms are not truth.

    Exact accepted binomials take precedence, including their own subspecies. A
    synonym is used only when no exact entry exists, is not another accepted CAS
    species, and the publisher's order AND family agree. Ambiguous shared source
    rows are discarded from synonym fallbacks rather than silently conflated.
    """
    candidates = china_candidates()
    accepted_names = {c["ScientificName"] for c in candidates}
    index_by_name: dict[str, list[int]] = {}
    for index, (taxonomy, common_name) in enumerate(labels):
        if len(taxonomy) < 7 or taxonomy[2] != "Aves":
            continue
        scientific = (taxonomy[5] + " " + taxonomy[6]).replace(" subsp. ", " ").strip()
        binomial = " ".join(scientific.split()[:2])
        index_by_name.setdefault(scientific, []).append(index)
        if binomial != scientific:
            index_by_name.setdefault(binomial, []).append(index)
    mapped, missing, rejected = [], [], []
    for candidate in candidates:
        indices = set(index_by_name.get(candidate["ScientificName"], []))
        exact = bool(indices)
        if not exact:
            for synonym in candidate["Synonyms"]:
                normalized = synonym.replace(" subsp. ", " ")
                binomial = " ".join(normalized.split()[:2])
                for index in index_by_name.get(normalized, []):
                    taxonomy = labels[index][0]
                    reason = None
                    if binomial in accepted_names and binomial != candidate["ScientificName"]:
                        reason = "Synonym is another accepted CAS species"
                    elif taxonomy[3:5] != candidate["Taxonomy"][3:5]:
                        reason = "Publisher order/family differs from accepted CAS order/family"
                    if reason:
                        rejected.append({"Candidate": candidate["ScientificName"], "Synonym": synonym,
                                         "OfficialIndex": index, "OfficialTaxonomy": taxonomy,
                                         "Reason": reason})
                    else:
                        indices.add(index)
        if not indices:
            candidate["ExcludedReason"] = "No exact accepted binomial or unambiguous same-order-and-family CAS-linked synonym in pinned Imageomics Aves labels"
            missing.append(candidate)
            continue
        candidate["EmbeddingSourceIndices"] = sorted(indices)
        candidate["MatchMethod"] = "AcceptedName" if exact else "CASAcceptedNameLinkedSynonym"
        mapped.append(candidate)
    owners = defaultdict(list)
    for candidate in mapped:
        for index in candidate["EmbeddingSourceIndices"]:
            owners[index].append(candidate["ScientificName"])
    kept = []
    for candidate in mapped:
        indices = candidate["EmbeddingSourceIndices"]
        if candidate["MatchMethod"] != "AcceptedName":
            unambiguous = []
            for index in indices:
                if len(owners[index]) > 1:
                    rejected.append({"Candidate": candidate["ScientificName"], "OfficialIndex": index,
                                     "ConflictingCandidates": owners[index],
                                     "Reason": "Official vector row is shared with another accepted candidate"})
                else:
                    unambiguous.append(index)
            indices = unambiguous
        if not indices:
            candidate["ExcludedReason"] = "All synonym vector rows were ambiguous with another accepted candidate"
            missing.append(candidate)
            continue
        candidate["EmbeddingSourceIndices"] = indices
        candidate["EmbeddingSourceNames"] = [" ".join(labels[i][0][5:7]) for i in indices]
        kept.append(candidate)
    return kept, missing, rejected


def build_embeddings() -> None:
    import numpy as np

    DEST.mkdir(parents=True, exist_ok=True)
    if sha256(CACHE / "labels.json") != LABEL_SHA or sha256(CACHE / "txt_emb_bioclip-2.npy") != TEXT_SHA:
        raise RuntimeError("Official label/text asset SHA-256 verification failed")
    with (CACHE / "labels.json").open(encoding="utf-8") as stream:
        labels = json.load(stream)
    kept, missing, rejected = map_candidates(labels)
    table = np.load(CACHE / "txt_emb_bioclip-2.npy", mmap_mode="r")
    if table.shape != (768, len(labels)):
        raise RuntimeError(f"Unexpected official text array shape {table.shape}; labels={len(labels)}")
    vectors = []
    for candidate in kept:
        vector = np.asarray(table[:, candidate["EmbeddingSourceIndices"]], dtype=np.float32).mean(axis=1)
        norm = np.linalg.norm(vector)
        if not np.isfinite(vector).all() or not np.isfinite(norm) or norm <= 0:
            raise RuntimeError(f"Invalid text embedding for {candidate['ScientificName']}")
        vectors.append(vector / norm)
    exact_count = sum(c["MatchMethod"] == "AcceptedName" for c in kept)
    alias_count = len(kept) - exact_count
    values = np.stack(vectors).astype("<f4")
    norms = np.linalg.norm(values, axis=1)
    if values.shape != (len(kept), 768) or not np.isfinite(values).all() or not np.allclose(norms, 1, atol=1e-5):
        raise RuntimeError("Final China candidate table failed finite/unit-vector validation")
    by_source = defaultdict(list)
    by_vector = defaultdict(list)
    for index, candidate in enumerate(kept):
        for source_index in candidate["EmbeddingSourceIndices"]:
            by_source[source_index].append(candidate["ScientificName"])
        by_vector[hashlib.sha256(values[index].tobytes()).hexdigest()].append(candidate["ScientificName"])
    overlaps = [{"SourceIndex": source_index, "SourceName": " ".join(labels[source_index][0][5:7]),
                 "Candidates": sorted(set(names))}
                for source_index, names in sorted(by_source.items()) if len(set(names)) > 1]
    identical = [names for names in by_vector.values() if len(names) > 1]
    ambiguities = {"SharedOfficialSourceIndices": overlaps, "IdenticalVectorGroups": identical,
                   "RejectedSynonymMappings": rejected,
                   "MappingPolicy": "Exact accepted binomial first; otherwise CAS-linked synonym, identical order and family, not another accepted species, no shared source rows",
                   "Note": "Taxonomic splits/aliases may share training concepts. Candidate count does not imply "
                           "that every candidate is a separately learned or reliably distinguishable model class."}
    values.tofile(DEST / "embeddings.f32")
    (DEST / "candidates.json").write_text(json.dumps(kept, ensure_ascii=False, indent=2), encoding="utf-8")
    (DEST / "china-unmatched.json").write_text(json.dumps(missing, ensure_ascii=False, indent=2), encoding="utf-8")
    (DEST / "taxonomy-ambiguities.json").write_text(json.dumps(ambiguities, ensure_ascii=False, indent=2), encoding="utf-8")
    (CACHE / "candidate-build-summary.json").write_text(json.dumps({
        "ChinaAcceptedBirdSpecies": len(kept) + len(missing), "MatchedCandidates": len(kept),
        "UnmatchedCandidates": len(missing), "ChineseNameCount": sum(c["ChineseName"] != c["ScientificName"] for c in kept),
        "ExactAcceptedNameMatches": exact_count, "CASLinkedSynonymMatches": alias_count,
        "SharedSourceIndexCount": len(overlaps), "IdenticalVectorGroups": len(identical),
        "RejectedSynonymMappingCount": len(rejected),
        "EmbeddingNormRange": [float(norms.min()), float(norms.max())],
        "Dimension": 768, "LabelCount": len(labels),
        "SourceArchiveSHA256": sha256(CACHE / "china-checklist.zip"),
        "Sources": ASSETS,
    }, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Wrote {len(kept)} China candidates with normalized official vectors; unmatched={len(missing)}", flush=True)


def export_vision() -> None:
    import numpy as np
    import onnx
    import open_clip
    import torch
    import torch.nn.functional as functional
    from safetensors.torch import load_file

    DEST.mkdir(parents=True, exist_ok=True)
    weights = CACHE / "open_clip_model.safetensors"
    if sha256(weights) != MODEL_SHA:
        raise RuntimeError("Official model SHA-256 verification failed")
    if sha256(CACHE / "open_clip_config.json") != CONFIG_SHA:
        raise RuntimeError("Official model configuration SHA-256 verification failed")
    torch.set_num_threads(max(1, min(8, (os.cpu_count() or 4) // 2)))
    # Avoid inference-only fused attention operators in the legacy ONNX exporter.
    torch.backends.mha.set_fastpath_enabled(False)
    print("Loading the pinned official BioCLIP 2 checkpoint on CPU...", flush=True)
    model = open_clip.create_model("ViT-L-14", pretrained=None, precision="fp32", device="cpu")
    model.load_state_dict(load_file(str(weights), device="cpu"), strict=True)
    model.eval()

    class VisionEncoder(torch.nn.Module):
        def __init__(self, visual):
            super().__init__()
            self.visual = visual

        def forward(self, pixel_values):
            return functional.normalize(self.visual(pixel_values), dim=-1)

    encoder = VisionEncoder(model.visual).eval()
    # The application uses only the visual tower; do not retain the text tower
    # while the exporter constructs its large protobuf graph.
    del model
    # Deterministic non-constant RGB input for a numerical conversion check.
    values = np.linspace(-1.25, 1.25, 3 * 224 * 224, dtype=np.float32).reshape(1, 3, 224, 224)
    tensor = torch.from_numpy(values)
    with torch.no_grad():
        expected = encoder(tensor).cpu().numpy().astype("<f4")
    values.astype("<f4").tofile(CACHE / "validation-input.f32")
    expected.tofile(CACHE / "validation-embedding.f32")
    target = DEST / "vision.onnx"
    temporary = DEST / "vision.onnx.partial"
    print("Exporting BioCLIP 2 visual tower (FP32, opset 17)...", flush=True)
    with torch.no_grad():
        torch.onnx.export(encoder, tensor, str(temporary), input_names=["pixel_values"],
                          output_names=["image_embedding"], opset_version=17,
                          do_constant_folding=True, export_params=True, dynamo=False)
    # Path-based checking avoids copying the 1.2 GB model through a Python protobuf object.
    onnx.checker.check_model(str(temporary))
    os.replace(temporary, target)
    print(f"Exported {target.name}: {target.stat().st_size / 1e6:.1f} MB", flush=True)
    validation = {"Reference": "Official Imageomics BioCLIP 2 PyTorch visual tower",
                  "ReferenceInput": "validation-input.f32", "ReferenceOutput": "validation-embedding.f32",
                  "InputShape": [1, 3, 224, 224], "OutputShape": [1, 768],
                  "ModelRevision": MODEL_REVISION, "OfficialCheckpointSHA256": MODEL_SHA,
                  "ONNXSHA256": sha256(target), "ONNXOpset": 17,
                  "PyTorchVersion": torch.__version__, "OpenCLIPVersion": open_clip.__version__,
                  "ONNXVersion": onnx.__version__}
    (CACHE / "export-validation.json").write_text(json.dumps(validation, indent=2), encoding="utf-8")


def write_metadata() -> None:
    summary = json.loads((CACHE / "candidate-build-summary.json").read_text(encoding="utf-8"))
    export = json.loads((CACHE / "export-validation.json").read_text(encoding="utf-8"))
    expected = ("vision.onnx", "candidates.json", "embeddings.f32", "china-unmatched.json", "taxonomy-ambiguities.json")
    if not all((DEST / name).is_file() for name in expected):
        raise RuntimeError("Export vision model and build candidates before finalizing metadata")
    metadata = {
        "Model": "BioCLIP 2", "Dimension": 768, "CandidateScope": "China",
        "CandidateCount": summary["MatchedCandidates"], "InputSize": 224,
        "Preprocessing": "openclip-bicubic-shortest-224-center-crop",
        "CropPolicy": "SquareContext20-v1", "InputName": "pixel_values", "OutputName": "image_embedding",
        "InputShape": [1, 3, 224, 224], "OutputShape": [1, 768], "InputDataType": "float32",
        "Mean": [0.48145466, 0.4578275, 0.40821073], "Std": [0.26862954, 0.26130258, 0.27577711],
        "EmbeddingFormat": "Little-endian float32 row-major [CandidateCount,768], L2-normalized",
        "Score": "Cosine similarity; uncalibrated, not a probability",
        "ConfidenceMethod": "BioCLIP2CandidateSoftmax-v1",
        "LearnedLogitScale": 4.605170249938965,
        "ConfidenceLogitScale": 100.00000639508755,
        "ConfidenceNote": "Softmax over installed candidates using the official checkpoint temperature; not calibrated identification accuracy or open-set rejection.",
        "ModelLicense": "MIT", "CandidateLicense": "CC-BY-NC-4.0", "TextCompilationLicense": "CC0-1.0",
        "ModelSource": "https://huggingface.co/imageomics/bioclip-2", "ModelRevision": MODEL_REVISION,
        "TextSource": "https://huggingface.co/datasets/imageomics/TreeOfLife-200M", "TextRevision": TEXT_REVISION,
        "ChinaChecklist": "CAS Catalogue of Life China 2023 v1.1; https://doi.org/10.15468/ae6ush",
        "ChinaAcceptedBirdSpecies": summary["ChinaAcceptedBirdSpecies"],
        "ExactAcceptedNameMatches": summary["ExactAcceptedNameMatches"],
        "CASLinkedSynonymMatches": summary["CASLinkedSynonymMatches"],
        "RejectedSynonymMappingCount": summary["RejectedSynonymMappingCount"],
        "ExcludedMissingEmbedding": summary["UnmatchedCandidates"],
        "ChineseNameCount": summary["ChineseNameCount"],
        "ExcludedCandidatesFile": "china-unmatched.json",
        "TaxonomyAmbiguitiesFile": "taxonomy-ambiguities.json",
        "SharedSourceIndexCount": summary["SharedSourceIndexCount"],
        "IdenticalVectorGroups": summary["IdenticalVectorGroups"],
        "EmbeddingNormRange": summary["EmbeddingNormRange"],
        "ONNXOpset": export["ONNXOpset"],
        "BuildVersions": {key: export[key] for key in ("PyTorchVersion", "OpenCLIPVersion", "ONNXVersion")},
        "CoverageNote": "Fixed CAS 2023 China candidate set; not a claim to cover all current birds of China. "
                        "Unmatched names are excluded, not substituted with unrelated species.",
        "TaxonomyMappingPolicy": "Use exact accepted binomial vectors only when available; otherwise require "
                                 "CAS-linked synonym with the same publisher order and family, excluding another "
                                 "accepted CAS species and excluding shared ambiguous vector rows.",
        "SourceSHA256": {"OfficialModel": MODEL_SHA, "OfficialLabels": LABEL_SHA,
                         "OfficialModelConfig": CONFIG_SHA, "OfficialTextEmbeddings": TEXT_SHA,
                         "ChinaChecklistArchive": summary["SourceArchiveSHA256"]},
        "AssetSHA256": {name: sha256(DEST / name) for name in expected},
    }
    (DEST / "metadata.json").write_text(json.dumps(metadata, ensure_ascii=False, indent=2), encoding="utf-8")
    hashes = {str(p.relative_to(DEST)).replace("\\", "/"): sha256(p)
              for p in sorted(DEST.rglob("*")) if p.is_file() and p.name != "asset-hashes.json"}
    (DEST / "asset-hashes.json").write_text(json.dumps(hashes, indent=2), encoding="utf-8")
    print(f"Finalized BioCLIP 2 / China / {metadata['CandidateCount']} candidates", flush=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    acquire = sub.add_parser("download")
    acquire.add_argument("asset", choices=ASSETS)
    fetch = sub.add_parser("fetch")
    fetch.add_argument("url")
    fetch.add_argument("path", type=Path)
    fetch.add_argument("size", type=int)
    fetch.add_argument("sha256")
    sub.add_parser("candidates")
    sub.add_parser("export")
    sub.add_parser("metadata")
    args = parser.parse_args()
    if args.command == "download":
        url, filename, size, digest = ASSETS[args.asset]
        download_ranges(url, CACHE / filename, size, digest)
    elif args.command == "fetch":
        download_ranges(args.url, args.path, args.size, args.sha256)
    elif args.command == "candidates":
        build_embeddings()
    elif args.command == "export":
        export_vision()
    elif args.command == "metadata":
        write_metadata()


if __name__ == "__main__":
    main()
